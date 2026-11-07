using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata.Services;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Jellyfin;

/// <summary>Uploads show posters straight to Jellyfin, and uploads them again when a refresh replaces them.</summary>
public sealed class JellyfinShowPosters(
    IServiceScopeFactory scopes,
    IImageManager images,
    StateStore store,
    ConfigurationProvider<SeasonPostersConfiguration> configuration,
    ILogger<JellyfinShowPosters> logger)
{
    // Shokofin's provider ID names, which it sets on every show it creates.
    internal const string ShokoGroupProviderId = "Shoko Group";
    internal const string ShokoSeriesProviderId = "Shoko Series";

    public async Task<(int Uploaded, int Failed)> SyncAsync(IReadOnlyList<ShowPlan> plans, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans.Where(plan => plan.ShowMode is not ShowPosterMode.JellyfinUpload))
            Forget(plan.Key);

        var wanted = plans.Where(plan => plan.JellyfinPosterImageId is not null).ToArray();
        if (wanted.Length == 0 || !TryGetConnection(out var url, out var key))
            return (0, 0);

        using var scope = scopes.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<JellyfinClient>();
        IReadOnlyList<JellyfinShow> shows;
        try
        {
            shows = await client.ListShowsAsync(url, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Season Posters could not list shows from Jellyfin.");
            return (0, wanted.Length);
        }

        var uploaded = 0;
        var failed = 0;
        foreach (var plan in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movieSeriesIds = plan.Series.Where(item => item.IsMovieType).Select(item => item.SeriesId).ToHashSet();
            var show = FindShow(shows, plan, movieSeriesIds);
            if (show is null)
            {
                logger.LogDebug("Season Posters found no single Jellyfin show for {ShowKey}.", plan.Key);
                continue;
            }

            var imageId = plan.JellyfinPosterImageId!.Value;
            var previous = store.Read(state => state.Uploads.GetValueOrDefault(plan.Key));
            if (previous is not null && previous.ImageId == imageId && previous.ItemId == show.Id && previous.Tag == show.PrimaryTag)
                continue;

            if (await UploadAsync(client, url, key, plan.Key, show.Id, imageId, cancellationToken).ConfigureAwait(false))
                uploaded++;
            else
                failed++;
        }

        return (uploaded, failed);
    }

    /// <summary>
    /// Shokofin's refreshes replace every image on an item, including an uploaded show poster. This compares Jellyfin's
    /// image tag with the one recorded after the upload, and restores the poster when they differ.
    /// </summary>
    public async Task<(int Uploaded, int Failed)> ReconcileAsync(CancellationToken cancellationToken)
    {
        var uploads = store.Read(state => state.Uploads.ToArray());
        if (uploads.Length == 0 || !TryGetConnection(out var url, out var key))
            return (0, 0);

        using var scope = scopes.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<JellyfinClient>();
        var uploaded = 0;
        var failed = 0;
        foreach (var (showKey, upload) in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var show = await client.GetShowAsync(url, key, upload.ItemId, cancellationToken).ConfigureAwait(false);
                if (show is null)
                {
                    Forget(showKey);
                    continue;
                }

                if (show.PrimaryTag == upload.Tag)
                    continue;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Season Posters could not check Jellyfin item {ItemId}.", upload.ItemId);
                failed++;
                continue;
            }

            if (await UploadAsync(client, url, key, showKey, upload.ItemId, upload.ImageId, cancellationToken).ConfigureAwait(false))
                uploaded++;
            else
                failed++;
        }

        return (uploaded, failed);
    }

    /// <summary>
    /// Shows carry their group's ID, and with Shokofin's separate-movies setting a movie's own show carries it as
    /// well. Those movie shows are left out so the group's real show is the one that matches.
    /// </summary>
    internal static JellyfinShow? FindShow(IReadOnlyList<JellyfinShow> shows, ShowPlan plan, IReadOnlySet<int> movieSeriesIds)
    {
        if (plan.GroupId is not { } groupId)
        {
            var seriesId = plan.MainSeriesId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var standalone = shows.Where(show => show.ProviderIds.GetValueOrDefault(ShokoSeriesProviderId) == seriesId).ToArray();
            return standalone.Length == 1 ? standalone[0] : null;
        }

        var id = groupId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var matches = shows.Where(show => show.ProviderIds.GetValueOrDefault(ShokoGroupProviderId) == id).ToArray();
        if (matches.Length <= 1)
            return matches.FirstOrDefault();

        var notMovies = matches
            .Where(show => !(int.TryParse(show.ProviderIds.GetValueOrDefault(ShokoSeriesProviderId), out var seriesId) && movieSeriesIds.Contains(seriesId)))
            .ToArray();
        return notMovies.Length == 1 ? notMovies[0] : null;
    }

    private async Task<bool> UploadAsync(JellyfinClient client, string url, string key, string showKey, string itemId, Guid imageId, CancellationToken cancellationToken)
    {
        try
        {
            var image = images.GetImageByID(imageId);
            if (image is null)
                return false;

            if (!image.IsAvailable && !await images.DownloadImage(image).ConfigureAwait(false))
                return false;

            var stream = image.GetStream();
            if (stream is null)
                return false;

            byte[] bytes;
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }

            await client.UploadPrimaryImageAsync(url, key, itemId, bytes, image.ContentType, cancellationToken).ConfigureAwait(false);
            var after = await client.GetShowAsync(url, key, itemId, cancellationToken).ConfigureAwait(false);
            store.Update(state =>
            {
                state.Uploads[showKey] = new JellyfinUpload { ItemId = itemId, ImageId = imageId, Tag = after?.PrimaryTag ?? string.Empty };
                return (true, true);
            });
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Season Posters could not upload a show poster to Jellyfin item {ItemId}.", itemId);
            return false;
        }
    }

    private void Forget(string showKey)
        => store.Update(state => (true, state.Uploads.Remove(showKey)));

    private bool TryGetConnection(out string url, out string key)
    {
        var settings = configuration.Load();
        url = settings.JellyfinUrl?.Trim() ?? string.Empty;
        key = settings.JellyfinApiKey?.Trim() ?? string.Empty;
        return url.Length > 0 && key.Length > 0;
    }
}
