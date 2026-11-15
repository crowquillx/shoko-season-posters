namespace Shoko.SeasonPosters.Planning;

/// <summary>Decides every poster for one Jellyfin show. Pure, so a plan never changes anything by itself.</summary>
public static class PosterPlanner
{
    private const string ShowOwner = "show";
    private const string ShowOwnerLabel = "Show poster";

    /// <summary>How many further candidates to queue for fingerprinting when a pick could not be compared yet.</summary>
    private const int Lookahead = 6;

    public static ShowPlan Plan(ShowFacts show, IReadOnlyList<string> languages, int minimumSeasonSharePercent)
        => Plan(show, new PlanOptions(languages, minimumSeasonSharePercent), NoLookalikes.Instance);

    public static ShowPlan Plan(ShowFacts show, PlanOptions options, ILookalikes lookalikes)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lookalikes);

        var languages = options.Languages;
        var used = new UsedPosters(lookalikes);
        var decisions = new Dictionary<int, SeriesDecision>();
        var ordered = show.Series
            .OrderBy(series => series.AirDate ?? DateOnly.MaxValue)
            .ThenBy(series => series.Id)
            .ToArray();
        var known = new Dictionary<Guid, PosterCandidate>();
        foreach (var poster in ordered.SelectMany(AllPosters))
            known.TryAdd(poster.ImageId, poster);

        // Choices the plugin must not override go first, so automatic picks can avoid them.
        foreach (var series in ordered)
        {
            if (Fixed(series, languages) is { } decision)
            {
                decisions[series.Id] = decision;
                if (decision.ImageId is { } id)
                    used.Add(known.GetValueOrDefault(id) ?? Unknown(id), Owner(series), series.Title);
            }
        }

        var main = ordered.FirstOrDefault(series => series.Id == show.MainSeriesId);
        Guid? showPosterId = null;
        string? showPosterSource = null;
        var showPosterLooksSame = false;
        if (show.ShowMode is ShowPosterMode.JellyfinUpload && show.PinnedShowPosterImageId is { } pinnedShowPoster)
        {
            showPosterId = pinnedShowPoster;
            showPosterSource = "Your choice";
            used.Add(known.GetValueOrDefault(pinnedShowPoster) ?? Unknown(pinnedShowPoster), ShowOwner, ShowOwnerLabel);
        }

        if (show.ShowMode is ShowPosterMode.TmdbShowPosterOnMainSeason && main is not null && !decisions.ContainsKey(main.Id))
        {
            var poster = Pick(ShowPosters(ordered, main, options.ShowPosterLanguages), used, lookalikes, Likeness.SameArtwork);
            if (poster is not null)
            {
                decisions[main.Id] = Build(main, "TMDB show poster", DecisionReason.ShowPosterOnMainSeason, false, poster, languages);
                used.Add(poster, Owner(main), main.Title);
            }
        }

        // Owners choose first, so the entry a shared TMDB season really belongs to gets its best poster.
        var owners = SharedOwners(ordered, options.MinimumSeasonSharePercent);
        foreach (var series in ordered.OrderBy(series => owners.ContainsValue(series.Id) ? 0 : 1))
        {
            if (!decisions.ContainsKey(series.Id))
                decisions[series.Id] = Decide(series, show.SplitPolicy, options, owners, used, lookalikes);
        }

        // The show takes what the seasons left, so each season keeps its best poster.
        if (show.ShowMode is ShowPosterMode.JellyfinUpload && showPosterId is null)
        {
            var (poster, source) = ChooseShowPoster(ordered, main, options.ShowPosterLanguages, show.ShowText, used, lookalikes);
            if (poster is not null)
            {
                showPosterId = poster.ImageId;
                showPosterSource = source;
                showPosterLooksSame = used.Resemblance(poster) is Likeness.SameImage;
                used.Add(poster, ShowOwner, ShowOwnerLabel);
            }
        }

        var showPoster = showPosterSource is null
            ? null
            : Annotate(new ShowPosterChoice(showPosterSource, show.PinnedShowPosterImageId is not null, showPosterLooksSame, []), used);
        return new ShowPlan(
            show.Key,
            show.GroupId,
            show.Title,
            show.MainSeriesId,
            show.SplitPolicy,
            show.ShowMode,
            showPosterId,
            ordered.Select(series => Annotate(series, decisions[series.Id], used)).ToArray(),
            ShowPosterAlternatives(ordered, main, options.ShowPosterLanguages),
            showPoster,
            show.ShowText);
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
                pinned, kind, series.CurrentPreferredImageId, Alternatives(series, languages), []);
        }

        if (series.CurrentPreferredImageId is { } current && current != series.PluginAssignedImageId)
        {
            var kind = AllPosters(series).FirstOrDefault(poster => poster.ImageId == current)?.Kind;
            return new SeriesDecision(
                series.Id, series.Title, series.IsMovieType, "Set outside this plugin", DecisionReason.SetOutsidePlugin, false,
                DecisionAction.LeaveAlone, current, kind, current, Alternatives(series, languages), []);
        }

        return null;
    }

    private static SeriesDecision Decide(
        SeriesFacts series,
        SplitSeasonPolicy splitPolicy,
        PlanOptions options,
        IReadOnlyDictionary<string, int> owners,
        UsedPosters used,
        ILookalikes lookalikes)
    {
        var languages = options.Languages;
        var match = SeriesMatcher.Match(series, options.MinimumSeasonSharePercent);
        if (match is { Kind: MatchKind.Unclear })
            return Fallback(series, match.Label, match.Reason, languages, used, lookalikes);

        var posters = PosterRanking.Rank(match.Posters ?? [], languages);
        var isLaterPart = owners[match.SharedKey!] != series.Id;
        if (!isLaterPart)
        {
            // The same artwork in another language or without text is still this season's poster, so it beats AniDB.
            var poster = Pick(posters, used, lookalikes, Likeness.SameArtwork);
            if (poster is null)
            {
                var reason = posters.Any(candidate => !used.Contains(candidate)) ? DecisionReason.LooksLikeAnotherPoster : DecisionReason.NoTmdbPosters;
                return Fallback(series, match.Label, reason, languages, used, lookalikes);
            }

            used.Add(poster, Owner(series), series.Title);
            return Build(series, match.Label, match.Reason, false, poster, languages);
        }

        var sharedLabel = $"{match.Label} (shared with an earlier part)";
        switch (splitPolicy)
        {
            case SplitSeasonPolicy.LeaveForManualChoice:
                return new SeriesDecision(
                    series.Id, series.Title, series.IsMovieType, sharedLabel, DecisionReason.SharedWithEarlierPart, true,
                    DecisionAction.LeaveAlone, series.CurrentPreferredImageId, null, series.CurrentPreferredImageId, Alternatives(series, languages), []);

            case SplitSeasonPolicy.NextTmdbPoster:
                // Another version of the earlier part's artwork would show the wrong part, so only different artwork will do.
                var next = Pick(posters, used, lookalikes, Likeness.Different);
                if (next is not null)
                {
                    used.Add(next, Owner(series), series.Title);
                    return Build(series, sharedLabel, DecisionReason.SharedWithEarlierPart, true, next, languages);
                }

                return Fallback(series, sharedLabel, DecisionReason.SharedWithEarlierPart, languages, used, lookalikes);

            default:
                return Fallback(series, sharedLabel, DecisionReason.SharedWithEarlierPart, languages, used, lookalikes);
        }
    }

    /// <summary>The AniDB poster is the only one known to belong to this exact entry, so uncertain entries use it and get flagged.</summary>
    private static SeriesDecision Fallback(SeriesFacts series, string label, DecisionReason reason, IReadOnlyList<string> languages, UsedPosters used, ILookalikes lookalikes)
    {
        var ranked = PosterRanking.Rank(series.AniDbPosters, languages);
        var poster = Pick(ranked, used, lookalikes, Likeness.SameArtwork) ?? ranked.FirstOrDefault(candidate => !used.Contains(candidate));
        if (poster is null)
        {
            return new SeriesDecision(
                series.Id, series.Title, series.IsMovieType, label, reason, true, DecisionAction.LeaveAlone,
                series.CurrentPreferredImageId, null, series.CurrentPreferredImageId, Alternatives(series, languages), []);
        }

        used.Add(poster, Owner(series), series.Title);
        return Build(series, label, reason, true, poster, languages);
    }

    private static SeriesDecision Build(SeriesFacts series, string label, DecisionReason reason, bool needsReview, PosterCandidate poster, IReadOnlyList<string> languages)
        => new(
            series.Id, series.Title, series.IsMovieType, label, reason, needsReview,
            series.CurrentPreferredImageId == poster.ImageId ? DecisionAction.AlreadySet : DecisionAction.SetPreferred,
            poster.ImageId, poster.Kind, series.CurrentPreferredImageId, Alternatives(series, languages), []);

    /// <summary>
    /// Prefers different artwork over a TMDB show poster: first a show poster unlike any season's, then an unused
    /// season or movie poster unlike them, and only then another version of artwork already in use. With
    /// <see cref="ShowPosterText.TextlessFirst"/>, textless posters go through those steps before any others.
    /// </summary>
    private static (PosterCandidate? Poster, string Source) ChooseShowPoster(
        IReadOnlyList<SeriesFacts> ordered,
        SeriesFacts? main,
        IReadOnlyList<string> languages,
        ShowPosterText text,
        UsedPosters used,
        ILookalikes lookalikes)
    {
        var showPosters = ShowPosters(ordered, main, languages);
        var spare = PosterRanking.Rank(SparePosters(ordered).Select(item => item.Poster), languages);
        var spareSource = SparePosters(ordered).ToDictionary(item => item.Poster.ImageId, item => $"Unused poster of {item.Series.Title}");

        if (text is ShowPosterText.TextlessFirst)
        {
            var textless = Choose(
                showPosters.Where(PosterRanking.IsTextless).ToArray(),
                spare.Where(PosterRanking.IsTextless).ToArray(),
                used,
                lookalikes);
            if (textless.Poster is not null)
                return (textless.Poster, textless.FromShow ? "Textless TMDB show poster" : $"Textless, {spareSource[textless.Poster.ImageId]}");
        }

        var chosen = Choose(showPosters, spare, used, lookalikes);
        if (chosen.Poster is not null)
            return (chosen.Poster, chosen.FromShow ? "TMDB show poster" : spareSource[chosen.Poster.ImageId]);

        // A show poster that looks like a season's is still no worse than sharing the season's own image.
        return (showPosters.FirstOrDefault(candidate => !used.Contains(candidate)), "TMDB show poster");
    }

    private static (PosterCandidate? Poster, bool FromShow) Choose(
        IReadOnlyList<PosterCandidate> showPosters,
        IReadOnlyList<PosterCandidate> spare,
        UsedPosters used,
        ILookalikes lookalikes)
    {
        if (Pick(showPosters, used, lookalikes, Likeness.Different) is { } distinctShow)
            return (distinctShow, true);
        if (Pick(spare, used, lookalikes, Likeness.Different) is { } distinctSpare)
            return (distinctSpare, false);
        if (Pick(showPosters, used, lookalikes, Likeness.SameArtwork) is { } variantShow)
            return (variantShow, true);
        if (Pick(spare, used, lookalikes, Likeness.SameArtwork) is { } variantSpare)
            return (variantSpare, false);
        return (null, false);
    }

    /// <summary>
    /// Returns the best untaken poster that looks unlike every poster in use, or failing that the best one that looks
    /// no more alike than <paramref name="worstAccepted"/>.
    /// </summary>
    private static PosterCandidate? Pick(IReadOnlyList<PosterCandidate> ranked, UsedPosters used, ILookalikes lookalikes, Likeness worstAccepted)
    {
        PosterCandidate? fallback = null;
        for (var index = 0; index < ranked.Count; index++)
        {
            var candidate = ranked[index];
            if (used.Contains(candidate))
                continue;

            var likeness = used.Resemblance(candidate);
            if (likeness is Likeness.Different)
            {
                // A poster that could not be compared yet may turn out to be a lookalike, so queue the next few as well
                // and the next planning round can move straight past it.
                if (!lookalikes.IsKnown(candidate))
                {
                    foreach (var next in ranked.Skip(index + 1).Take(Lookahead))
                        lookalikes.IsKnown(next);
                }

                return candidate;
            }

            if (fallback is null && likeness <= worstAccepted)
                fallback = candidate;
        }

        return fallback;
    }

    /// <summary>The main entry's own TMDB show comes first, so a group led by a movie can still find a show poster elsewhere.</summary>
    private static IReadOnlyList<PosterCandidate> ShowPosters(IReadOnlyList<SeriesFacts> ordered, SeriesFacts? main, IReadOnlyList<string> languages)
    {
        var sources = main is null ? ordered : [main, .. ordered.Where(series => series.Id != main.Id)];
        return sources
            .SelectMany(series => PosterRanking.Rank(series.Shows.SelectMany(link => link.Posters), languages))
            .DistinctBy(poster => poster.UniquenessKey)
            .ToArray();
    }

    private static IEnumerable<(PosterCandidate Poster, SeriesFacts Series)> SparePosters(IReadOnlyList<SeriesFacts> ordered)
        => ordered
            .SelectMany(series => series.Seasons.SelectMany(season => season.Posters)
                .Concat(series.Movies.SelectMany(movie => movie.Posters))
                .Select(poster => (Poster: poster, Series: series)))
            .DistinctBy(item => item.Poster.ImageId);

    /// <summary>Everything the show poster could be: show posters first, then every season, movie and AniDB poster in the show.</summary>
    private static IReadOnlyList<PosterCandidate> ShowPosterAlternatives(IReadOnlyList<SeriesFacts> ordered, SeriesFacts? main, IReadOnlyList<string> languages)
        => ShowPosters(ordered, main, languages)
            .Concat(PosterRanking.Rank(SparePosters(ordered).Select(item => item.Poster), languages))
            .Concat(PosterRanking.Rank(ordered.SelectMany(series => series.AniDbPosters), languages))
            .DistinctBy(poster => poster.UniquenessKey)
            .ToArray();

    /// <summary>
    /// Lists the other posters in the show that this entry's poster looks like. One that looks the same is flagged,
    /// unless someone chose it on purpose.
    /// </summary>
    private static SeriesDecision Annotate(SeriesFacts series, SeriesDecision decision, UsedPosters used)
    {
        var looksLike = used.ResemblancesOf(Owner(series));
        if (looksLike.Count == 0)
            return decision;

        var chosenOnPurpose = decision.Reason is DecisionReason.Pinned or DecisionReason.SetOutsidePlugin;
        var looksSame = looksLike.Any(item => item.Likeness is Likeness.SameImage);
        return decision with { LooksLike = looksLike, NeedsReview = decision.NeedsReview || (looksSame && !chosenOnPurpose) };
    }

    private static ShowPosterChoice Annotate(ShowPosterChoice choice, UsedPosters used)
    {
        var looksLike = used.ResemblancesOf(ShowOwner);
        var looksSame = looksLike.Any(item => item.Likeness is Likeness.SameImage);
        return choice with { LooksLike = looksLike, NeedsReview = choice.NeedsReview || (looksSame && !choice.IsPinned) };
    }

    private static string Owner(SeriesFacts series) => $"series:{series.Id}";

    private static PosterCandidate Unknown(Guid imageId) => new(imageId, imageId, PosterKind.Other);

    private static IEnumerable<PosterCandidate> AllPosters(SeriesFacts series)
        => series.Seasons.SelectMany(season => season.Posters)
            .Concat(series.Movies.SelectMany(movie => movie.Posters))
            .Concat(series.Shows.SelectMany(link => link.Posters))
            .Concat(series.AniDbPosters);

    private static IReadOnlyList<PosterCandidate> Alternatives(SeriesFacts series, IReadOnlyList<string> languages)
        => PosterRanking.Rank(AllPosters(series).DistinctBy(poster => poster.UniquenessKey), languages);

    /// <summary>The posters already taken in a show, and who took them, so later picks can avoid them and their lookalikes.</summary>
    private sealed class UsedPosters(ILookalikes lookalikes)
    {
        private readonly HashSet<Guid> _keys = [];
        private readonly List<(PosterCandidate Poster, string Owner, string Label)> _posters = [];

        public bool Contains(PosterCandidate poster) => _keys.Contains(poster.UniquenessKey) || _keys.Contains(poster.ImageId);

        public void Add(PosterCandidate poster, string owner, string label)
        {
            _keys.Add(poster.UniquenessKey);
            _keys.Add(poster.ImageId);
            _posters.Add((poster, owner, label));
        }

        /// <summary>How much the poster looks like the most similar poster in use.</summary>
        public Likeness Resemblance(PosterCandidate poster)
        {
            var most = Likeness.Different;
            foreach (var item in _posters)
            {
                var likeness = lookalikes.Compare(poster, item.Poster);
                if (likeness > most)
                    most = likeness;
                if (most is Likeness.SameImage)
                    break;
            }

            return most;
        }

        public IReadOnlyList<Resemblance> ResemblancesOf(string owner)
        {
            var mine = _posters.Where(item => item.Owner == owner).Select(item => item.Poster).FirstOrDefault();
            if (mine is null)
                return [];

            return _posters
                .Where(item => item.Owner != owner)
                .Select(item => new Resemblance(item.Label, lookalikes.Compare(mine, item.Poster)))
                .Where(item => item.Likeness is not Likeness.Different)
                .ToArray();
        }
    }
}
