namespace Shoko.SeasonPosters.Planning;

public static class PosterRanking
{
    private const double WidestPosterRatio = 0.85;

    /// <summary>
    /// Orders posters best first: real poster shape, then language preference, then rating, votes and width,
    /// with the image ID as a tiebreak so a repeat run gives the same answer.
    /// </summary>
    /// <param name="candidates">The posters to rank.</param>
    /// <param name="languages">Language codes in order of preference; "none" means textless or unknown.</param>
    public static IReadOnlyList<PosterCandidate> Rank(IEnumerable<PosterCandidate> candidates, IReadOnlyList<string> languages)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(languages);
        return candidates
            .OrderBy(candidate => IsPosterShaped(candidate) ? 0 : 1)
            .ThenBy(candidate => LanguageRank(candidate.Language, languages))
            .ThenByDescending(candidate => candidate.Rating ?? 0)
            .ThenByDescending(candidate => candidate.Votes ?? 0)
            .ThenByDescending(candidate => candidate.Width ?? 0)
            .ThenBy(candidate => candidate.ImageId)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseLanguages(string? setting)
        => (setting ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(language => language.ToLowerInvariant())
            .ToArray();

    private static bool IsPosterShaped(PosterCandidate candidate)
        => candidate is not { Width: > 0, Height: > 0 } || (double)candidate.Width.Value / candidate.Height.Value <= WidestPosterRatio;

    private static int LanguageRank(string? language, IReadOnlyList<string> languages)
    {
        var code = string.IsNullOrWhiteSpace(language) ? "none" : language.ToLowerInvariant();
        for (var index = 0; index < languages.Count; index++)
        {
            if (languages[index] == code)
                return index;
        }

        return languages.Count;
    }
}
