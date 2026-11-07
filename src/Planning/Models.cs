namespace Shoko.SeasonPosters.Planning;

/// <summary>What to do for later parts that share one TMDB season or movie with an earlier part.</summary>
public enum SplitSeasonPolicy
{
    /// <summary>Use the entry's own AniDB poster: accurate, but low resolution.</summary>
    AniDbPoster,

    /// <summary>Use the next unused TMDB poster: sharp, but it may show the wrong part.</summary>
    NextTmdbPoster,

    /// <summary>Change nothing and wait for a choice on the review page.</summary>
    LeaveForManualChoice,
}

/// <summary>How the Jellyfin show poster relates to the poster of its main season.</summary>
public enum ShowPosterMode
{
    /// <summary>The main season keeps its season poster; the TMDB show poster goes straight to Jellyfin.</summary>
    JellyfinUpload,

    /// <summary>The main series uses the TMDB show poster, so the show and that season share it.</summary>
    TmdbShowPosterOnMainSeason,

    /// <summary>The main series uses its season poster, so the show and that season share it.</summary>
    SameAsMainSeason,
}

public enum PosterKind
{
    TmdbSeason,
    TmdbMovie,
    TmdbShow,
    AniDb,
}

public enum DecisionReason
{
    /// <summary>Matched one TMDB season.</summary>
    SeasonMatch,

    /// <summary>Matched one TMDB movie.</summary>
    MovieMatch,

    /// <summary>The main season deliberately uses the TMDB show poster.</summary>
    ShowPosterOnMainSeason,

    /// <summary>An earlier part in this show already took the shared TMDB season or movie.</summary>
    SharedWithEarlierPart,

    /// <summary>Episodes spread over TMDB seasons with no clear majority.</summary>
    MixedSeasons,

    /// <summary>One entry links to several TMDB movies.</summary>
    MultipleMovies,

    /// <summary>A movie entry links to a TMDB TV season instead of a movie.</summary>
    MovieLinkedToSeason,

    /// <summary>Only TMDB's specials season matches, and its posters are not specific to the entry.</summary>
    SpecialsOnly,

    /// <summary>No TMDB link for this entry.</summary>
    NoTmdbLink,

    /// <summary>The matched TMDB season or movie has no unused posters, often because its data has not downloaded yet.</summary>
    NoTmdbPosters,

    /// <summary>A choice made on the review page.</summary>
    Pinned,

    /// <summary>A preferred poster set outside this plugin, which it will not replace.</summary>
    SetOutsidePlugin,
}

public sealed record PosterCandidate(
    Guid ImageId,
    Guid UniquenessKey,
    PosterKind Kind,
    string? Language = null,
    int? Width = null,
    int? Height = null,
    double? Rating = null,
    int? Votes = null,
    bool IsAvailable = true,
    string? RemotePreviewUrl = null);

/// <summary>A TMDB season and how many of the entry's episodes link to it.</summary>
public sealed record SeasonLink(string Key, int Number, int EpisodeCount, string? ShowKey, IReadOnlyList<PosterCandidate> Posters);

public sealed record MovieLink(string Key, string Title, IReadOnlyList<PosterCandidate> Posters);

public sealed record ShowLink(string Key, IReadOnlyList<PosterCandidate> Posters);

public sealed record SeriesFacts(
    int Id,
    string Title,
    DateOnly? AirDate,
    bool IsMovieType,
    IReadOnlyList<SeasonLink> Seasons,
    IReadOnlyList<MovieLink> Movies,
    IReadOnlyList<ShowLink> Shows,
    IReadOnlyList<PosterCandidate> AniDbPosters,
    Guid? CurrentPreferredImageId = null,
    Guid? PluginAssignedImageId = null,
    Guid? PinnedImageId = null,
    bool IsTvType = true);

/// <summary>One Jellyfin show: a Shoko group, or a series that Shokofin makes standalone.</summary>
public sealed record ShowFacts(
    string Key,
    int? GroupId,
    string Title,
    int MainSeriesId,
    IReadOnlyList<SeriesFacts> Series,
    SplitSeasonPolicy SplitPolicy,
    ShowPosterMode ShowMode,
    Guid? PinnedShowPosterImageId = null);

public enum DecisionAction
{
    /// <summary>Make <see cref="SeriesDecision.ImageId"/> the preferred poster.</summary>
    SetPreferred,

    /// <summary>The right poster is already preferred.</summary>
    AlreadySet,

    /// <summary>Leave the entry as it is.</summary>
    LeaveAlone,
}

public sealed record SeriesDecision(
    int SeriesId,
    string Title,
    bool IsMovieType,
    string Match,
    DecisionReason Reason,
    bool NeedsReview,
    DecisionAction Action,
    Guid? ImageId,
    PosterKind? Kind,
    Guid? CurrentPreferredImageId,
    IReadOnlyList<PosterCandidate> Alternatives);

public sealed record ShowPlan(
    string Key,
    int? GroupId,
    string Title,
    int MainSeriesId,
    SplitSeasonPolicy SplitPolicy,
    ShowPosterMode ShowMode,
    Guid? JellyfinPosterImageId,
    IReadOnlyList<SeriesDecision> Series,
    IReadOnlyList<PosterCandidate> ShowPosterAlternatives)
{
    public bool NeedsReview => Series.Any(item => item.NeedsReview);
}
