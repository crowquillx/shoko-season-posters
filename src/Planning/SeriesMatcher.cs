namespace Shoko.SeasonPosters.Planning;

public enum MatchKind
{
    Season,
    Movie,
    Unclear,
}

/// <summary>What an entry's TMDB links say it is.</summary>
public sealed record SeriesMatch(
    MatchKind Kind,
    string Label,
    string? SharedKey = null,
    IReadOnlyList<PosterCandidate>? Posters = null,
    DecisionReason Reason = DecisionReason.NoTmdbLink);

public static class SeriesMatcher
{
    /// <summary>
    /// Works out which TMDB season or movie an entry is. A movie-type entry looks at its movie links first,
    /// because it can appear as a Jellyfin season without having any TMDB season. Anything uncertain comes back
    /// as <see cref="MatchKind.Unclear"/> rather than as a guess.
    /// </summary>
    public static SeriesMatch Match(SeriesFacts series, int minimumSeasonSharePercent)
    {
        ArgumentNullException.ThrowIfNull(series);
        var regularSeasons = series.Seasons.Where(season => season.Number > 0).ToArray();
        var hasSpecialsLink = series.Seasons.Any(season => season.Number <= 0);

        if (series.IsMovieType)
        {
            return series.Movies.Count switch
            {
                1 => MovieMatch(series.Movies[0]),
                > 1 => Unclear("Several TMDB movies", DecisionReason.MultipleMovies),
                _ when regularSeasons.Length > 0 => Unclear("Movie linked to a TMDB season", DecisionReason.MovieLinkedToSeason),
                _ when hasSpecialsLink => Unclear("Movie linked to TMDB specials", DecisionReason.SpecialsOnly),
                _ => Unclear("No TMDB link", DecisionReason.NoTmdbLink),
            };
        }

        if (regularSeasons.Length > 0)
        {
            var top = regularSeasons.OrderByDescending(season => season.EpisodeCount).ThenBy(season => season.Number).First();
            var total = regularSeasons.Sum(season => season.EpisodeCount);
            if (total > 0 && top.EpisodeCount * 100 >= total * minimumSeasonSharePercent)
                return new SeriesMatch(MatchKind.Season, $"TMDB season {top.Number}", top.Key, top.Posters, DecisionReason.SeasonMatch);
            return Unclear("Spans several TMDB seasons", DecisionReason.MixedSeasons);
        }

        // Specials, OVAs and web entries are sometimes linked to a TMDB movie rather than a season.
        return series.Movies.Count switch
        {
            1 => MovieMatch(series.Movies[0]),
            > 1 => Unclear("Several TMDB movies", DecisionReason.MultipleMovies),
            _ when hasSpecialsLink => Unclear("TMDB specials only", DecisionReason.SpecialsOnly),
            _ => Unclear("No TMDB link", DecisionReason.NoTmdbLink),
        };
    }

    private static SeriesMatch MovieMatch(MovieLink movie)
        => new(MatchKind.Movie, $"TMDB movie: {movie.Title}", movie.Key, movie.Posters, DecisionReason.MovieMatch);

    private static SeriesMatch Unclear(string label, DecisionReason reason)
        => new(MatchKind.Unclear, label, Reason: reason);
}
