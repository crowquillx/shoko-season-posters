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

    /// <summary>The upload record key of a show's specials season poster.</summary>
    internal static string SpecialsKey(string showKey) => $"{showKey}#specials";

    /// <summary>
    /// Uploads each show's own poster, and its specials poster to its specials season. Shows only Silo sees are left
    /// out, since Jellyfin shows their series separately.
    /// </summary>
    public async Task<(int Uploaded, int Failed)> SyncAsync(IReadOnlyList<ShowPlan> plans, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans)
        {
            if (plan.IsSiloOnly || plan.JellyfinPosterImageId is null)
                Forget(plan.Key);
            if (plan.IsSiloOnly || plan.SpecialsPosterImageId is null)
                Forget(SpecialsKey(plan.Key));
        }

        var wanted = plans
            .Where(plan => !plan.IsSiloOnly && (plan.JellyfinPosterImageId is not null || plan.SpecialsPosterImageId is not null))
            .ToArray();
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

            if (plan.JellyfinPosterImageId is { } showPoster)
                Count(await SyncItemAsync(client, url, key, plan.Key, show, showPoster, cancellationToken).ConfigureAwait(false));

            if (plan.SpecialsPosterImageId is { } specialsPoster)
            {
                var specials = await FindSpecialsAsync(client, url, key, show, cancellationToken).ConfigureAwait(false);
                if (specials is null)
                    logger.LogDebug("Season Posters found no specials season in Jellyfin for {ShowKey}.", plan.Key);
                else
                    Count(await SyncItemAsync(client, url, key, SpecialsKey(plan.Key), specials, specialsPoster, cancellationToken).ConfigureAwait(false));
            }
        }

        return (uploaded, failed);

        void Count(bool? result)
        {
            if (result == true)
                uploaded++;
            else if (result == false)
                failed++;
        }
    }

    /// <summary>Uploads the poster unless Jellyfin already has it. Null means there was nothing to do.</summary>
    private async Task<bool?> SyncItemAsync(JellyfinClient client, string url, string key, string uploadKey, JellyfinShow item, Guid imageId, CancellationToken cancellationToken)
    {
        var previous = store.Read(state => state.Uploads.GetValueOrDefault(uploadKey));
        if (previous is not null && previous.ImageId == imageId && previous.ItemId == item.Id && previous.Tag == item.PrimaryTag)
            return null;

        return await UploadAsync(client, url, key, uploadKey, item.Id, imageId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JellyfinShow?> FindSpecialsAsync(JellyfinClient client, string url, string key, JellyfinShow show, CancellationToken cancellationToken)
    {
        try
        {
            var seasons = await client.ListSeasonsAsync(url, key, show.Id, cancellationToken).ConfigureAwait(false);
            return seasons.FirstOrDefault(season => season.IndexNumber == 0);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Season Posters could not list the seasons of Jellyfin show {ItemId}.", show.Id);
            return null;
        }
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
