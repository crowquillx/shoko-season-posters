using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;
using Shoko.SeasonPosters.Jellyfin;
using Shoko.SeasonPosters.Planning;
using Shoko.SeasonPosters.Library;

namespace Shoko.SeasonPosters;

public sealed record LibraryPlan(DateTimeOffset CreatedAt, IReadOnlyList<ShowPlan> Shows);

public sealed record ApplyResult(int Changed, int Failed, int JellyfinUploaded, int JellyfinFailed);

/// <summary>Plans and applies posters. Only one scan or apply runs at a time.</summary>
public sealed class PosterService(
    ShokoLibrary library,
    IImageManager images,
    StateStore store,
    JellyfinShowPosters jellyfin,
    ConfigurationProvider<SeasonPostersConfiguration> configuration,
    ILogger<PosterService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile LibraryPlan? _latest;

    public LibraryPlan? Latest => _latest;

    public async Task<LibraryPlan> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Scan();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs on a schedule or after a change: applies when the admin enabled that, and otherwise only refreshes the plan.</summary>
    public async Task RunAutomaticPassAsync(CancellationToken cancellationToken)
    {
        if (configuration.Load().AutoApply)
            await ApplyAsync(null, cancellationToken).ConfigureAwait(false);
        else
            await ScanAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplyResult> ApplyAsync(IReadOnlySet<string>? showKeys, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = Scan();
            var shows = showKeys is null ? plan.Shows : plan.Shows.Where(show => showKeys.Contains(show.Key)).ToArray();
            var changed = 0;
            var failed = 0;
            foreach (var decision in shows.SelectMany(show => show.Series).Where(item => item.Action is DecisionAction.SetPreferred))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await SetPreferredAsync(decision.SeriesId, decision.ImageId!.Value).ConfigureAwait(false))
                    changed++;
                else
                    failed++;
            }

            var (uploaded, uploadFailed) = await jellyfin.SyncAsync(shows, cancellationToken).ConfigureAwait(false);
            Scan();
            logger.LogInformation("Season Posters applied {Changed} posters ({Failed} failed) and uploaded {Uploaded} show posters ({UploadFailed} failed).", changed, failed, uploaded, uploadFailed);
            return new ApplyResult(changed, failed, uploaded, uploadFailed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Keeps a choice from the review page and applies it straight away, replacing any other preferred poster.</summary>
    public async Task<bool> PinAsync(int seriesId, Guid imageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.Update(state =>
            {
                state.Pins[seriesId] = imageId;
                return (true, true);
            });
            var applied = await SetPreferredAsync(seriesId, imageId).ConfigureAwait(false);
            Scan();
            return applied;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stores an uploaded poster and pins it to the series.</summary>
    public async Task<bool> PinUploadAsync(int seriesId, Stream stream, string? contentType, CancellationToken cancellationToken)
    {
        var uploaded = images.UploadImage(stream, contentType, userSubmitted: true);
        return await PinAsync(seriesId, uploaded.ID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a series to automatic: removes any choice from the review page, and hands over a poster that was set
    /// outside this plugin, so the next apply decides again.
    /// </summary>
    public async Task UnpinAsync(int seriesId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var series = library.GetSeries(seriesId);
            var current = series is null
                ? []
                : ((IWithImages)series).GetImages(new() { ImageType = ImageEntityType.Primary, IsPreferred = true, LinkedEntityImages = false });
            store.Update(state =>
            {
                state.Pins.Remove(seriesId);

                // The poster stays preferred for now, and marking it as ours lets the next apply replace it.
                if (current.Count > 0)
                    state.Assigned[seriesId] = current[0].ID;
                return (true, true);
            });
            Scan();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateShowAsync(string showKey, Action<ShowSettings> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.Update(state =>
            {
                if (!state.Shows.TryGetValue(showKey, out var settings))
                    state.Shows[showKey] = settings = new ShowSettings();
                change(settings);
                return (true, true);
            });
            Scan();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<(int Uploaded, int Failed)> ReconcileJellyfinAsync(CancellationToken cancellationToken)
        => jellyfin.ReconcileAsync(cancellationToken);

    private LibraryPlan Scan()
    {
        var settings = configuration.Load();
        var languages = PosterRanking.ParseLanguages(settings.PosterLanguages);
        var shows = store.Read(library.ReadShows);
        var plan = new LibraryPlan(
            DateTimeOffset.UtcNow,
            shows.Select(show => PosterPlanner.Plan(show, languages, settings.MinimumSeasonSharePercent)).ToArray());
        _latest = plan;
        return plan;
    }

    private async Task<bool> SetPreferredAsync(int seriesId, Guid imageId)
    {
        var series = library.GetSeries(seriesId);
        var image = images.GetImageByID(imageId);
        if (series is null || image is null)
        {
            logger.LogWarning("Season Posters could not find series {SeriesId} or image {ImageId}.", seriesId, imageId);
            return false;
        }

        // Shokofin skips images that are not on disk, and would then fall back to a poster shared with other seasons.
        if (!image.IsAvailable && !await images.DownloadImage(image).ConfigureAwait(false))
        {
            logger.LogWarning("Season Posters could not download image {ImageId} for series {SeriesId}.", imageId, seriesId);
            return false;
        }

        images.SetPreferredImageForEntity(series, ImageEntityType.Primary, image);
        store.Update(state =>
        {
            state.Assigned[seriesId] = imageId;
            return (true, true);
        });
        return true;
    }
}
