using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Library;

/// <summary>Turns Shoko's data into the facts the planner works from, and shapes shows the way Shokofin does.</summary>
public sealed class ShokoLibrary(
    IMetadataService metadata,
    IShokoGroupManager groups,
    IImageManager images,
    ConfigurationProvider<SeasonPostersConfiguration> configuration)
{
    public static string GroupKey(int groupId) => $"group:{groupId}";

    public static string SeriesKey(int seriesId) => $"series:{seriesId}";

    /// <summary>The show Silo makes of a group with sub-groups, from the group's own series.</summary>
    public static string SiloGroupKey(int groupId) => $"silo-group:{groupId}";

    public IShokoSeries? GetSeries(int seriesId) => metadata.GetShokoSeriesByID(seriesId);

    /// <summary>
    /// A group with no sub-groups is one show. Shokofin makes each direct series of a group with sub-groups a
    /// show of its own, so those are standalone here too. Silo instead makes one show of those series, under the
    /// group, so that show is planned as well, for Silo only.
    /// </summary>
    public IReadOnlyList<ShowFacts> ReadShows(PluginState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var settings = configuration.Load();
        var shows = new List<ShowFacts>();
        foreach (var group in groups.GetAllGroups())
        {
            if (group.Groups.Count > 0)
            {
                foreach (var series in group.Series)
                    shows.Add(BuildShow(SeriesKey(series.LocalID), null, series.Title, series.LocalID, [series], settings, state));

                if (group.Series.Count > 0)
                {
                    var main = group.Series.Any(series => series.LocalID == group.MainSeriesID) ? group.MainSeriesID : group.Series[0].LocalID;
                    shows.Add(BuildShow(SiloGroupKey(group.LocalID), group.LocalID, group.Title, main, group.Series, settings, state, isSiloOnly: true));
                }
            }
            else if (group.Series.Count > 0)
            {
                shows.Add(BuildShow(GroupKey(group.LocalID), group.LocalID, group.Title, group.MainSeriesID, group.Series, settings, state));
            }
        }

        return shows;
    }

    private ShowFacts BuildShow(
        string key,
        int? groupId,
        string title,
        int mainSeriesId,
        IReadOnlyList<IShokoSeries> series,
        SeasonPostersConfiguration settings,
        PluginState state,
        bool isSiloOnly = false)
    {
        var overrides = state.Shows.GetValueOrDefault(key);
        return new ShowFacts(
            key,
            groupId,
            title,
            mainSeriesId,
            series.Select(item => BuildSeries(item, state)).ToArray(),
            overrides?.SplitPolicy ?? settings.SplitSeasons,
            overrides?.ShowMode ?? settings.ShowPoster,
            overrides?.PinnedShowPosterImageId,
            overrides?.ShowText ?? settings.ShowPosterText,
            overrides?.SpecialsMode ?? settings.SpecialsPoster,
            overrides?.PinnedSpecialsPosterImageId,
            isSiloOnly);
    }

    private SeriesFacts BuildSeries(IShokoSeries series, PluginState state)
    {
        var episodeLinks = metadata.GetEpisodeCrossReferencesForSeries(series.AnidbAnimeID, MetadataSource.TMDB)
            .Where(link => link.SeasonID is not null)
            .ToArray();

        // Credits and trailers say little about what an entry is. An entry made only of specials still counts them.
        var regularLinks = episodeLinks.Where(link => link.ShokoEpisode?.Type is EpisodeType.Episode).ToArray();
        var counted = regularLinks.Length > 0 ? regularLinks : episodeLinks;
        var linkedSeasons = series.LinkedSeasons.ToDictionary(season => season.ID);

        var seasons = counted
            .GroupBy(link => link.SeasonID!)
            .Select(linksToSeason =>
            {
                linkedSeasons.TryGetValue(linksToSeason.Key, out var season);
                return new SeasonLink(
                    linksToSeason.Key.ToString(),
                    season?.SeasonNumber ?? linksToSeason.First().SeasonNumber ?? 0,
                    linksToSeason.Select(link => link.AnidbEpisodeID).Distinct().Count(),
                    season?.SeriesID.ToString() ?? linksToSeason.First().ProviderParentID?.ToString(),
                    season is null ? [] : Posters(season, PosterKind.TmdbSeason));
            })
            .ToArray();

        // Read the links themselves: a linked movie whose TMDB data has not downloaded yet is not in LinkedMovies,
        // and it should show as waiting for posters rather than as having no link at all.
        var movies = metadata.GetMovieCrossReferencesForSeries(series.AnidbAnimeID, MetadataSource.TMDB)
            .Where(link => link.ProviderID is not null)
            .DistinctBy(link => link.ProviderID)
            .Select(link => link.Provider is IMovie movie
                ? new MovieLink(link.ProviderID!.ToString(), movie.Title, Posters(movie, PosterKind.TmdbMovie))
                : new MovieLink(link.ProviderID!.ToString(), "data not downloaded yet", []))
            .ToArray();

        var shows = series.LinkedSeries
            .Where(show => show.ID.Source == MetadataSource.TMDB)
            .DistinctBy(show => show.ID)
            .Select(show => new ShowLink(
                show.ID.ToString(),
                Posters(show, PosterKind.TmdbShow),
                show.Seasons.Where(season => season.SeasonNumber == 0).SelectMany(season => Posters(season, PosterKind.TmdbSpecials)).ToArray()))
            .ToArray();

        var current = ((IWithImages)series).GetImages(new() { ImageType = ImageEntityType.Primary, IsPreferred = true, LinkedEntityImages = false });
        return new SeriesFacts(
            series.LocalID,
            series.Title,
            AirDate(series),
            series.Type is AnimeType.Movie,
            seasons,
            movies,
            shows,
            Posters(series.AnidbAnime, PosterKind.AniDb),
            current.Count > 0 ? current[0].ID : null,
            state.Assigned.TryGetValue(series.LocalID, out var assigned) ? assigned : null,
            state.Pins.TryGetValue(series.LocalID, out var pinned) ? pinned : null,
            series.Type is AnimeType.TV,
            HasSpecials(series));
    }

    /// <summary>
    /// Whether the series has a file for any episode that is not a regular one. Silo puts all of those in the
    /// specials season; Jellyfin has one when Shokofin puts specials there.
    /// </summary>
    private static bool HasSpecials(IShokoSeries series)
        => series.LocalEpisodeCounts is { } counts
            && counts.Specials + counts.Credits + counts.Trailers + counts.Parodies + counts.Others > 0;

    private IReadOnlyList<PosterCandidate> Posters(IWithImages entity, PosterKind kind)
        => entity.GetImages(new() { ImageType = ImageEntityType.Primary, IsEnabled = true, LinkedEntityImages = false })
            .Select(image => ToCandidate(image, kind))
            .ToArray();

    private PosterCandidate ToCandidate(IImage image, PosterKind kind)
        => new(
            image.ID,
            image.PrimaryID == Guid.Empty ? image.ID : image.PrimaryID,
            kind,
            image.LanguageCode,
            image.Width,
            image.Height,
            image.Rating,
            image.RatingVotes,
            image.IsAvailable,
            image.IsAvailable ? null : RemotePreview(image));

    /// <summary>Lets the review page show a poster Shoko has not downloaded, at a size suited to a thumbnail where the source offers one.</summary>
    private string? RemotePreview(IImage image) => RemoteImages.Url(images, image, "w342");

    private static DateOnly? AirDate(IShokoSeries series)
        => series.AirDate is { } date && DateOnly.TryParse(date.ToString(), out var parsed) ? parsed : null;
}
