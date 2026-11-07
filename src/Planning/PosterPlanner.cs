namespace Shoko.SeasonPosters.Planning;

/// <summary>Decides every poster for one Jellyfin show. Pure, so a plan never changes anything by itself.</summary>
public static class PosterPlanner
{
    public static ShowPlan Plan(ShowFacts show, IReadOnlyList<string> languages, int minimumSeasonSharePercent)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(languages);

        var used = new HashSet<Guid>();
        var decisions = new Dictionary<int, SeriesDecision>();
        var ordered = show.Series
            .OrderBy(series => series.AirDate ?? DateOnly.MaxValue)
            .ThenBy(series => series.Id)
            .ToArray();

        // Choices the plugin must not override go first, so automatic picks can avoid them.
        foreach (var series in ordered)
        {
            if (Fixed(series, languages) is { } decision)
            {
                decisions[series.Id] = decision;
                Reserve(used, series, decision.ImageId);
            }
        }

        var main = ordered.FirstOrDefault(series => series.Id == show.MainSeriesId);
        if (show.ShowMode is ShowPosterMode.TmdbShowPosterOnMainSeason && main is not null && !decisions.ContainsKey(main.Id))
        {
            var poster = BestShowPoster(ordered, main, languages, used);
            if (poster is not null)
            {
                decisions[main.Id] = Build(main, "TMDB show poster", DecisionReason.ShowPosterOnMainSeason, false, poster, languages);
                used.Add(poster.UniquenessKey);
            }
        }

        // Owners choose first, so the entry a shared TMDB season really belongs to gets its best poster.
        var owners = SharedOwners(ordered, minimumSeasonSharePercent);
        foreach (var series in ordered.OrderBy(series => owners.ContainsValue(series.Id) ? 0 : 1))
        {
            if (!decisions.ContainsKey(series.Id))
                decisions[series.Id] = Decide(series, show.SplitPolicy, languages, minimumSeasonSharePercent, owners, used);
        }

        var jellyfinPoster = JellyfinPoster(show, ordered, main, languages, used);
        return new ShowPlan(
            show.Key,
            show.GroupId,
            show.Title,
            show.MainSeriesId,
            show.SplitPolicy,
            show.ShowMode,
            jellyfinPoster,
            ordered.Select(series => decisions[series.Id]).ToArray(),
            PosterRanking.Rank(ordered.SelectMany(series => series.Shows).SelectMany(link => link.Posters).DistinctBy(poster => poster.UniquenessKey), languages));
    }

    /// <summary>
    /// Picks which entry owns each TMDB season or movie that several entries share. A TV entry beats an OVA or special
    /// that happened to air earlier, because a few extra episodes linked into a season do not make it that season.
    /// </summary>
    private static Dictionary<string, int> SharedOwners(IReadOnlyList<SeriesFacts> ordered, int minimumSeasonSharePercent)
        => ordered
            .Select(series => (Series: series, Key: SeriesMatcher.Match(series, minimumSeasonSharePercent).SharedKey))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Series).OrderByDescending(series => series.IsTvType).First().Id,
                StringComparer.Ordinal);

    private static SeriesDecision? Fixed(SeriesFacts series, IReadOnlyList<string> languages)
    {
        if (series.PinnedImageId is { } pinned)
        {
            var kind = AllPosters(series).FirstOrDefault(poster => poster.ImageId == pinned)?.Kind;
            return new SeriesDecision(
                series.Id, series.Title, series.IsMovieType, "Your choice", DecisionReason.Pinned, false,
                series.CurrentPreferredImageId == pinned ? DecisionAction.AlreadySet : DecisionAction.SetPreferred,
                pinned, kind, series.CurrentPreferredImageId, Alternatives(series, languages));
        }

        if (series.CurrentPreferredImageId is { } current && current != series.PluginAssignedImageId)
        {
            var kind = AllPosters(series).FirstOrDefault(poster => poster.ImageId == current)?.Kind;
            return new SeriesDecision(
                series.Id, series.Title, series.IsMovieType, "Set outside this plugin", DecisionReason.SetOutsidePlugin, false,
                DecisionAction.LeaveAlone, current, kind, current, Alternatives(series, languages));
        }

        return null;
    }

    private static SeriesDecision Decide(
        SeriesFacts series,
        SplitSeasonPolicy splitPolicy,
        IReadOnlyList<string> languages,
        int minimumSeasonSharePercent,
        IReadOnlyDictionary<string, int> owners,
        HashSet<Guid> used)
    {
        var match = SeriesMatcher.Match(series, minimumSeasonSharePercent);
        if (match is { Kind: MatchKind.Unclear })
            return Fallback(series, match.Label, match.Reason, languages, used);

        var posters = PosterRanking.Rank(match.Posters ?? [], languages);
        var isLaterPart = owners[match.SharedKey!] != series.Id;
        if (!isLaterPart)
        {
            var poster = posters.FirstOrDefault(candidate => !used.Contains(candidate.UniquenessKey));
            if (poster is null)
                return Fallback(series, match.Label, DecisionReason.NoTmdbPosters, languages, used);

            used.Add(poster.UniquenessKey);
            return Build(series, match.Label, match.Reason, false, poster, languages);
        }

        var sharedLabel = $"{match.Label} (shared with an earlier part)";
        switch (splitPolicy)
        {
            case SplitSeasonPolicy.LeaveForManualChoice:
                return new SeriesDecision(
                    series.Id, series.Title, series.IsMovieType, sharedLabel, DecisionReason.SharedWithEarlierPart, true,
                    DecisionAction.LeaveAlone, series.CurrentPreferredImageId, null, series.CurrentPreferredImageId, Alternatives(series, languages));

            case SplitSeasonPolicy.NextTmdbPoster:
                var next = posters.FirstOrDefault(candidate => !used.Contains(candidate.UniquenessKey));
                if (next is not null)
                {
                    used.Add(next.UniquenessKey);
                    return Build(series, sharedLabel, DecisionReason.SharedWithEarlierPart, true, next, languages);
                }

                return Fallback(series, sharedLabel, DecisionReason.SharedWithEarlierPart, languages, used);

            default:
                return Fallback(series, sharedLabel, DecisionReason.SharedWithEarlierPart, languages, used);
        }
    }

    /// <summary>The AniDB poster is the only one known to belong to this exact entry, so uncertain entries use it and get flagged.</summary>
    private static SeriesDecision Fallback(SeriesFacts series, string label, DecisionReason reason, IReadOnlyList<string> languages, HashSet<Guid> used)
    {
        var poster = PosterRanking.Rank(series.AniDbPosters, languages).FirstOrDefault(candidate => !used.Contains(candidate.UniquenessKey));
        if (poster is null)
        {
            return new SeriesDecision(
                series.Id, series.Title, series.IsMovieType, label, reason, true, DecisionAction.LeaveAlone,
                series.CurrentPreferredImageId, null, series.CurrentPreferredImageId, Alternatives(series, languages));
        }

        used.Add(poster.UniquenessKey);
        return Build(series, label, reason, true, poster, languages);
    }

    private static SeriesDecision Build(SeriesFacts series, string label, DecisionReason reason, bool needsReview, PosterCandidate poster, IReadOnlyList<string> languages)
        => new(
            series.Id, series.Title, series.IsMovieType, label, reason, needsReview,
            series.CurrentPreferredImageId == poster.ImageId ? DecisionAction.AlreadySet : DecisionAction.SetPreferred,
            poster.ImageId, poster.Kind, series.CurrentPreferredImageId, Alternatives(series, languages));

    private static Guid? JellyfinPoster(ShowFacts show, IReadOnlyList<SeriesFacts> ordered, SeriesFacts? main, IReadOnlyList<string> languages, HashSet<Guid> used)
    {
        if (show.ShowMode is not ShowPosterMode.JellyfinUpload)
            return null;
        if (show.PinnedShowPosterImageId is { } pinned)
            return pinned;

        return BestShowPoster(ordered, main, languages, used)?.ImageId;
    }

    /// <summary>The main entry's own TMDB show comes first, so a group led by a movie can still find a show poster elsewhere.</summary>
    private static PosterCandidate? BestShowPoster(IReadOnlyList<SeriesFacts> ordered, SeriesFacts? main, IReadOnlyList<string> languages, HashSet<Guid> used)
    {
        var sources = main is null ? ordered : [main, .. ordered.Where(series => series.Id != main.Id)];
        foreach (var series in sources)
        {
            var poster = PosterRanking.Rank(series.Shows.SelectMany(link => link.Posters), languages)
                .FirstOrDefault(candidate => !used.Contains(candidate.UniquenessKey));
            if (poster is not null)
                return poster;
        }

        return null;
    }

    private static void Reserve(HashSet<Guid> used, SeriesFacts series, Guid? imageId)
    {
        if (imageId is not { } id)
            return;

        used.Add(id);
        if (AllPosters(series).FirstOrDefault(poster => poster.ImageId == id) is { } known)
            used.Add(known.UniquenessKey);
    }

    private static IEnumerable<PosterCandidate> AllPosters(SeriesFacts series)
        => series.Seasons.SelectMany(season => season.Posters)
            .Concat(series.Movies.SelectMany(movie => movie.Posters))
            .Concat(series.Shows.SelectMany(link => link.Posters))
            .Concat(series.AniDbPosters);

    private static IReadOnlyList<PosterCandidate> Alternatives(SeriesFacts series, IReadOnlyList<string> languages)
        => PosterRanking.Rank(AllPosters(series).DistinctBy(poster => poster.UniquenessKey), languages);
}
