namespace Shoko.SeasonPosters.Planning;

/// <summary>One logo a series can use, in the order Shoko lists them.</summary>
public sealed record LogoCandidate(Guid ImageId, string? Language, bool IsAvailable);

/// <summary>What to do with one series' preferred logo.</summary>
public enum LogoAction
{
    /// <summary>Nothing to change.</summary>
    Keep,

    /// <summary>Make the chosen logo the series' preferred logo.</summary>
    SetPreferred,
}

public sealed record LogoDecision(LogoAction Action, Guid? ImageId);

/// <summary>Picks a series' logo by language, the way the poster languages pick posters.</summary>
public static class LogoChooser
{
    /// <summary>
    /// The best logo in one of the given languages, best language first, preferring one already downloaded, then
    /// Shoko's order. A logo in any other language is never chosen, so a series without one keeps what it has.
    /// </summary>
    public static Guid? Choose(IReadOnlyList<LogoCandidate> candidates, IReadOnlyList<string> languages)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(languages);
        return candidates
            .Select((candidate, order) => (candidate, order, rank: LanguageRank(candidate.Language, languages)))
            .Where(item => item.rank < languages.Count)
            .OrderBy(item => item.rank)
            .ThenByDescending(item => item.candidate.IsAvailable)
            .ThenBy(item => item.order)
            .Select(item => (Guid?)item.candidate.ImageId)
            .FirstOrDefault();
    }

    /// <summary>
    /// A logo someone chose in Shoko stays. The plugin only sets a series' logo when it has none, or when the
    /// current one is the plugin's own earlier choice.
    /// </summary>
    /// <param name="current">The series' preferred logo now.</param>
    /// <param name="assigned">The logo this plugin last made preferred for the series.</param>
    /// <param name="best">The logo <see cref="Choose"/> picked.</param>
    public static LogoDecision Decide(Guid? current, Guid? assigned, Guid? best)
    {
        if (best is null || current == best)
            return new LogoDecision(LogoAction.Keep, current);
        if (current is not null && current != assigned)
            return new LogoDecision(LogoAction.Keep, current);
        return new LogoDecision(LogoAction.SetPreferred, best);
    }

    private static int LanguageRank(string? language, IReadOnlyList<string> languages)
    {
        var code = string.IsNullOrWhiteSpace(language) ? "none" : language.Trim().ToLowerInvariant();
        for (var index = 0; index < languages.Count; index++)
        {
            if (languages[index] == code)
                return index;
        }

        return languages.Count;
    }
}
