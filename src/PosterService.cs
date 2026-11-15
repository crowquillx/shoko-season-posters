using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;
using Shoko.SeasonPosters.Imaging;
using Shoko.SeasonPosters.Jellyfin;
using Shoko.SeasonPosters.Planning;
using Shoko.SeasonPosters.Library;

namespace Shoko.SeasonPosters;

public sealed record LibraryPlan(DateTimeOffset CreatedAt, IReadOnlyList<ShowPlan> Shows);

public sealed record ApplyResult(int Changed, int Failed, int JellyfinUploaded, int JellyfinFailed, int ShowPostersFailed = 0);

/// <summary>Plans and applies posters. Only one scan or apply runs at a time.</summary>
public sealed class PosterService(
    ShokoLibrary library,
    IImageManager images,
    StateStore store,
    JellyfinShowPosters jellyfin,
    FingerprintService fingerprints,
    ConfigurationProvider<SeasonPostersConfiguration> configuration,
    ILogger<PosterService> logger)
{
    /// <summary>
    /// Each round fingerprints the posters the last one compared without one. Most shows settle in two or three, and
    /// a poster still unchecked after the last round counts as different.
    /// </summary>
    private const int MaxLookalikeRounds = 6;
    private const int MaxLookalikeQueries = 300;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile LibraryPlan? _latest;

    public LibraryPlan? Latest => _latest;

    public async Task<LibraryPlan> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
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
            var plan = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
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

            var showPostersFailed = await RecordShowPostersAsync(shows, cancellationToken).ConfigureAwait(false);
            var (uploaded, uploadFailed) = await jellyfin.SyncAsync(shows, cancellationToken).ConfigureAwait(false);
            await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Season Posters applied {Changed} posters ({Failed} failed), recorded show posters ({ShowPostersFailed} could not be downloaded) and uploaded {Uploaded} show posters to Jellyfin ({UploadFailed} failed).",
                changed, failed, showPostersFailed, uploaded, uploadFailed);
            return new ApplyResult(changed, failed, uploaded, uploadFailed, showPostersFailed);
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
            await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
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
            await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
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
            await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<(int Uploaded, int Failed)> ReconcileJellyfinAsync(CancellationToken cancellationToken)
        => jellyfin.ReconcileAsync(cancellationToken);

    /// <summary>Stores an uploaded poster and makes it the show's own poster. It is applied with the show.</summary>
    public Task UploadShowPosterAsync(string showKey, Stream stream, string? contentType, CancellationToken cancellationToken)
    {
        var uploaded = images.UploadImage(stream, contentType, userSubmitted: true);
        return UpdateShowAsync(showKey, settings => settings.PinnedShowPosterImageId = uploaded.ID, cancellationToken);
    }

    /// <summary>The show and specials posters last applied, per show key, so the review page can tell what is pending.</summary>
    public IReadOnlyDictionary<string, (Guid? Show, Guid? Specials)> AppliedShowPosters()
        => store.Read(state => state.ShowPosters.ToDictionary(pair => pair.Key, pair => (pair.Value.ImageId, pair.Value.SpecialsImageId)));

    /// <summary>Stores an uploaded poster and makes it the show's specials poster. It is applied with the show.</summary>
    public Task UploadSpecialsPosterAsync(string showKey, Stream stream, string? contentType, CancellationToken cancellationToken)
    {
        var uploaded = images.UploadImage(stream, contentType, userSubmitted: true);
        return UpdateShowAsync(showKey, settings => settings.PinnedSpecialsPosterImageId = uploaded.ID, cancellationToken);
    }

    /// <summary>
    /// Says which posters in use in a show each given image looks like, for the review page's choosers. Images are
    /// fingerprinted as needed, so this can take a few seconds the first time.
    /// </summary>
    /// <param name="showKey">The show the images would be used in.</param>
    /// <param name="seriesId">The entry being chosen for, which is left out, or null for the show or specials poster.</param>
    /// <param name="specials">Whether the specials poster is being chosen, which is then left out.</param>
    /// <param name="imageIds">The images to check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Resemblance>>> FindLookalikesAsync(
        string showKey,
        int? seriesId,
        bool specials,
        IReadOnlyCollection<Guid> imageIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        var plan = (Latest ?? await ScanAsync(cancellationToken).ConfigureAwait(false)).Shows.FirstOrDefault(show => show.Key == showKey);
        if (plan is null)
            return new Dictionary<Guid, IReadOnlyList<Resemblance>>();

        var known = plan.Series.SelectMany(series => series.Alternatives)
            .Concat(plan.ShowPosterAlternatives)
            .Concat(plan.SpecialsPosterAlternatives ?? [])
            .DistinctBy(poster => poster.ImageId)
            .ToDictionary(poster => poster.ImageId);
        PosterCandidate Find(Guid id) => known.GetValueOrDefault(id) ?? new PosterCandidate(id, id, PosterKind.Other);

        var inUse = plan.Series
            .Where(series => series.ImageId is not null && series.SeriesId != seriesId)
            .Select(series => (Poster: Find(series.ImageId!.Value), UsedBy: series.Title))
            .ToList();
        var choosingShowPoster = seriesId is null && !specials;
        if (!choosingShowPoster && plan.JellyfinPosterImageId is { } showPoster)
            inUse.Add((Find(showPoster), "Show poster"));
        if (!specials && plan.SpecialsPosterImageId is { } specialsPoster)
            inUse.Add((Find(specialsPoster), "Specials"));

        var requested = imageIds.Distinct().Take(MaxLookalikeQueries).Select(Find).ToArray();
        var wanted = requested.Concat(inUse.Select(item => item.Poster))
            .DistinctBy(poster => poster.UniquenessKey)
            .ToDictionary(poster => poster.UniquenessKey, poster => poster.ImageId);
        await fingerprints.AddAsync(wanted, cancellationToken).ConfigureAwait(false);

        // Report every lookalike whatever the setting says, since this only informs a choice.
        var lookalikes = new FingerprintLookalikes(fingerprints.Find, LookalikePolicy.AvoidSameArtwork);
        return requested.ToDictionary(
            poster => poster.ImageId,
            poster => (IReadOnlyList<Resemblance>)inUse
                .Select(item => new Resemblance(item.UsedBy, lookalikes.Compare(poster, item.Poster)))
                .Where(item => item.Likeness is not Likeness.Different)
                .ToArray());
    }

    private async Task<LibraryPlan> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var settings = configuration.Load();
        var options = new PlanOptions(
            PosterRanking.ParseLanguages(settings.PosterLanguages),
            settings.MinimumSeasonSharePercent,
            PosterRanking.ParseLanguages(settings.ShowPosterLanguages));
        var shows = store.Read(library.ReadShows);
        for (var round = 1; ; round++)
        {
            var lookalikes = new FingerprintLookalikes(fingerprints.Find, settings.Lookalikes);
            var plans = new Dictionary<string, ShowPlan>(StringComparer.Ordinal);
            foreach (var show in shows.Where(show => !show.IsSiloOnly))
                plans[show.Key] = PosterPlanner.Plan(show, options, lookalikes);

            // A show only Silo sees keeps the season posters its series got in their own shows.
            var decided = plans.Values.SelectMany(show => show.Series).GroupBy(series => series.SeriesId).ToDictionary(group => group.Key, group => group.First());
            foreach (var show in shows.Where(show => show.IsSiloOnly))
                plans[show.Key] = PosterPlanner.Plan(show, options, lookalikes, decided);

            var plan = new LibraryPlan(DateTimeOffset.UtcNow, shows.Select(show => plans[show.Key]).ToArray());
            if (lookalikes.Missing.Count == 0
                || round == MaxLookalikeRounds
                || await fingerprints.AddAsync(lookalikes.Missing, cancellationToken).ConfigureAwait(false) == 0)
            {
                _latest = plan;
                return plan;
            }
        }
    }

    /// <summary>
    /// Downloads each applied show's own poster and records it, so clients that build shows from Shoko, such as the
    /// Silo ShokoAnime plugin, can use it. Returns how many could not be downloaded.
    /// </summary>
    private async Task<int> RecordShowPostersAsync(IReadOnlyList<ShowPlan> shows, CancellationToken cancellationToken)
    {
        var available = new HashSet<Guid>();
        var failed = 0;
        foreach (var imageId in shows.SelectMany(show => new[] { show.JellyfinPosterImageId, show.SpecialsPosterImageId }).OfType<Guid>().Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = images.GetImageByID(imageId);
            if (image is not null && (image.IsAvailable || await images.DownloadImage(image).ConfigureAwait(false)))
            {
                available.Add(imageId);
                continue;
            }

            logger.LogWarning("Season Posters could not download show poster {ImageId}.", imageId);
            failed++;
        }

        store.Update(state => (true, ShowPosterRecords.Update(state.ShowPosters, shows, available)));
        return failed;
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
