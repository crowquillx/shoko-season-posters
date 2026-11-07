using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Tests;

public class PosterPlannerTests
{
    private static readonly string[] Languages = ["en", "none", "ja"];

    private static PosterCandidate Poster(PosterKind kind, string? language = "en", double rating = 5, int width = 1000, int height = 1500)
    {
        var id = Guid.NewGuid();
        return new PosterCandidate(id, id, kind, language, width, height, rating);
    }

    private static SeriesFacts Series(
        int id,
        int year,
        IReadOnlyList<SeasonLink>? seasons = null,
        IReadOnlyList<MovieLink>? movies = null,
        IReadOnlyList<ShowLink>? shows = null,
        PosterCandidate? aniDb = null,
        bool isMovie = false,
        Guid? current = null,
        Guid? assigned = null,
        Guid? pinned = null,
        bool isTv = true)
        => new(id, $"Series {id}", new DateOnly(year, 1, 1), isMovie, seasons ?? [], movies ?? [], shows ?? [], aniDb is null ? [] : [aniDb], current, assigned, pinned, isTv && !isMovie);

    private static ShowPlan Plan(
        IReadOnlyList<SeriesFacts> series,
        SplitSeasonPolicy split = SplitSeasonPolicy.AniDbPoster,
        ShowPosterMode mode = ShowPosterMode.JellyfinUpload,
        int? main = null,
        Guid? pinnedShow = null)
        => PosterPlanner.Plan(new ShowFacts("group:1", 1, "Group", main ?? series[0].Id, series, split, mode, pinnedShow), Languages, 60);

    private static SeasonLink Season(int number, int episodes, params PosterCandidate[] posters)
        => new($"season-{number}", number, episodes, "show-1", posters);

    private static SeriesDecision For(ShowPlan plan, int id) => plan.Series.Single(item => item.SeriesId == id);

    [Fact]
    public void EachSeasonGetsItsOwnSeasonPosterEvenWhenTheShowPosterIsRatedHigher()
    {
        var showPoster = Poster(PosterKind.TmdbShow, rating: 10);
        var first = Poster(PosterKind.TmdbSeason);
        var second = Poster(PosterKind.TmdbSeason);
        var shows = new[] { new ShowLink("show-1", [showPoster]) };

        var plan = Plan([
            Series(1, 2019, [Season(1, 12, first)], shows: shows),
            Series(2, 2021, [Season(2, 12, second)], shows: shows),
        ]);

        Assert.Equal(first.ImageId, For(plan, 1).ImageId);
        Assert.Equal(second.ImageId, For(plan, 2).ImageId);
        Assert.False(plan.NeedsReview);
    }

    [Fact]
    public void MovieInAGroupUsesItsTmdbMoviePosterWithoutAnyTmdbSeason()
    {
        // For example, a Miss Kobayashi's Dragon Maid film that Jellyfin shows as a season of the group.
        var season = Poster(PosterKind.TmdbSeason);
        var moviePoster = Poster(PosterKind.TmdbMovie);
        var plan = Plan([
            Series(1, 2017, [Season(1, 13, season)]),
            Series(2, 2021, [Season(2, 12, Poster(PosterKind.TmdbSeason))]),
            Series(3, 2025, movies: [new MovieLink("movie-1", "A Lonely Dragon Wants to Be Loved", [moviePoster])], aniDb: Poster(PosterKind.AniDb), isMovie: true),
        ]);

        var movie = For(plan, 3);
        Assert.Equal(moviePoster.ImageId, movie.ImageId);
        Assert.Equal(DecisionReason.MovieMatch, movie.Reason);
        Assert.Equal(PosterKind.TmdbMovie, movie.Kind);
        Assert.False(movie.NeedsReview);
    }

    [Fact]
    public void MovieLinkTakesPriorityOverASeasonLinkForAMovieEntry()
    {
        var moviePoster = Poster(PosterKind.TmdbMovie);
        var plan = Plan([
            Series(1, 2020, [Season(1, 1, Poster(PosterKind.TmdbSeason))], [new MovieLink("m", "Film", [moviePoster])], isMovie: true),
        ]);

        Assert.Equal(moviePoster.ImageId, For(plan, 1).ImageId);
    }

    [Fact]
    public void MovieEntryLinkedOnlyToASeasonFallsBackToAniDbAndIsFlagged()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan([Series(1, 2020, [Season(1, 1, Poster(PosterKind.TmdbSeason))], aniDb: aniDb, isMovie: true)]);

        var decision = For(plan, 1);
        Assert.Equal(aniDb.ImageId, decision.ImageId);
        Assert.Equal(DecisionReason.MovieLinkedToSeason, decision.Reason);
        Assert.True(decision.NeedsReview);
    }

    [Fact]
    public void EntryWithSeveralMoviesFallsBackToAniDbAndIsFlagged()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan([
            Series(1, 2020, movies: [new MovieLink("a", "Part 1", [Poster(PosterKind.TmdbMovie)]), new MovieLink("b", "Part 2", [Poster(PosterKind.TmdbMovie)])], aniDb: aniDb, isMovie: true),
        ]);

        Assert.Equal(aniDb.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.MultipleMovies, For(plan, 1).Reason);
    }

    [Fact]
    public void TwoEntriesOnOneMovieAreTreatedLikeASplitSeason()
    {
        var moviePoster = Poster(PosterKind.TmdbMovie);
        var aniDb = Poster(PosterKind.AniDb, null);
        var movie = new MovieLink("m", "Film", [moviePoster]);
        var plan = Plan([
            Series(1, 2020, movies: [movie], isMovie: true),
            Series(2, 2021, movies: [movie], aniDb: aniDb, isMovie: true),
        ]);

        Assert.Equal(moviePoster.ImageId, For(plan, 1).ImageId);
        Assert.Equal(aniDb.ImageId, For(plan, 2).ImageId);
        Assert.Equal(DecisionReason.SharedWithEarlierPart, For(plan, 2).Reason);
    }

    [Fact]
    public void SpecialsOnlyEntryFallsBackToAniDbAndIsFlagged()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan([Series(1, 2020, [Season(0, 2, Poster(PosterKind.TmdbSeason))], aniDb: aniDb)]);

        Assert.Equal(aniDb.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.SpecialsOnly, For(plan, 1).Reason);
        Assert.True(For(plan, 1).NeedsReview);
    }

    [Fact]
    public void SplitSeasonWithAniDbPolicyGivesTheLaterPartItsAniDbPosterAndFlagsIt()
    {
        var top = Poster(PosterKind.TmdbSeason, rating: 9);
        var next = Poster(PosterKind.TmdbSeason, rating: 5);
        var aniDb = Poster(PosterKind.AniDb, null);
        var season = Season(1, 12, top, next);

        var plan = Plan([Series(1, 2020, [season]), Series(2, 2020, [season], aniDb: aniDb)]);

        Assert.Equal(top.ImageId, For(plan, 1).ImageId);
        Assert.False(For(plan, 1).NeedsReview);
        Assert.Equal(aniDb.ImageId, For(plan, 2).ImageId);
        Assert.True(For(plan, 2).NeedsReview);
        Assert.Equal(DecisionReason.SharedWithEarlierPart, For(plan, 2).Reason);
    }

    [Fact]
    public void SplitSeasonWithNextPosterPolicyUsesTheNextPosterAndStillFlagsIt()
    {
        var top = Poster(PosterKind.TmdbSeason, rating: 9);
        var next = Poster(PosterKind.TmdbSeason, rating: 5);
        var season = Season(1, 12, top, next);

        var plan = Plan([Series(1, 2020, [season]), Series(2, 2020, [season], aniDb: Poster(PosterKind.AniDb))], SplitSeasonPolicy.NextTmdbPoster);

        Assert.Equal(next.ImageId, For(plan, 2).ImageId);
        Assert.True(For(plan, 2).NeedsReview);
    }

    [Fact]
    public void SplitSeasonWithNextPosterPolicyFallsBackToAniDbWhenPostersRunOut()
    {
        var top = Poster(PosterKind.TmdbSeason);
        var aniDb = Poster(PosterKind.AniDb, null);
        var season = Season(1, 12, top);

        var plan = Plan([Series(1, 2020, [season]), Series(2, 2020, [season], aniDb: aniDb)], SplitSeasonPolicy.NextTmdbPoster);

        Assert.Equal(aniDb.ImageId, For(plan, 2).ImageId);
    }

    [Fact]
    public void SplitSeasonWithManualPolicyChangesNothingAndFlagsIt()
    {
        var season = Season(1, 12, Poster(PosterKind.TmdbSeason));

        var plan = Plan([Series(1, 2020, [season]), Series(2, 2020, [season], aniDb: Poster(PosterKind.AniDb))], SplitSeasonPolicy.LeaveForManualChoice);

        Assert.Equal(DecisionAction.LeaveAlone, For(plan, 2).Action);
        Assert.True(For(plan, 2).NeedsReview);
    }

    [Fact]
    public void EarliestPartGetsTheSharedSeasonPosterWhateverTheInputOrder()
    {
        var poster = Poster(PosterKind.TmdbSeason);
        var season = Season(1, 12, poster);

        var plan = Plan([Series(1, 2022, [season], aniDb: Poster(PosterKind.AniDb)), Series(2, 2020, [season], aniDb: Poster(PosterKind.AniDb))]);

        Assert.Equal(poster.ImageId, For(plan, 2).ImageId);
    }

    [Fact]
    public void OvaThatAiredFirstDoesNotTakeTheSeasonFromTheTvEntry()
    {
        // From a real library: Food Wars OVAs aired before The Second Plate and also link into TMDB season 2.
        var poster = Poster(PosterKind.TmdbSeason);
        var aniDb = Poster(PosterKind.AniDb, null);
        var season = Season(2, 13, poster);

        var plan = Plan([Series(1, 2015, [season], aniDb: aniDb, isTv: false), Series(2, 2016, [season], aniDb: Poster(PosterKind.AniDb))]);

        Assert.Equal(poster.ImageId, For(plan, 2).ImageId);
        Assert.False(For(plan, 2).NeedsReview);
        Assert.Equal(aniDb.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.SharedWithEarlierPart, For(plan, 1).Reason);
    }

    [Fact]
    public void LinkedMovieWithoutDownloadedDataWaitsOnAniDbInsteadOfClaimingNoLink()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan([Series(1, 2020, movies: [new MovieLink("m", "data not downloaded yet", [])], aniDb: aniDb, isMovie: true)]);

        Assert.Equal(aniDb.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.NoTmdbPosters, For(plan, 1).Reason);
        Assert.True(For(plan, 1).NeedsReview);
    }

    [Fact]
    public void AniDbFallbackIsReplacedOnceTmdbPostersArrive()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var moviePoster = Poster(PosterKind.TmdbMovie);
        var plan = Plan([Series(1, 2020, movies: [new MovieLink("m", "Film", [moviePoster])], aniDb: aniDb, isMovie: true, current: aniDb.ImageId, assigned: aniDb.ImageId)]);

        Assert.Equal(DecisionAction.SetPreferred, For(plan, 1).Action);
        Assert.Equal(moviePoster.ImageId, For(plan, 1).ImageId);
    }

    [Fact]
    public void EntrySpanningSeasonsWithNoMajorityIsFlagged()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan([Series(1, 2020, [Season(1, 6, Poster(PosterKind.TmdbSeason)), Season(2, 6, Poster(PosterKind.TmdbSeason))], aniDb: aniDb)]);

        Assert.Equal(aniDb.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.MixedSeasons, For(plan, 1).Reason);
    }

    [Fact]
    public void ClearMajoritySeasonIsAccepted()
    {
        var majority = Poster(PosterKind.TmdbSeason);
        var plan = Plan([Series(1, 2020, [Season(1, 11, majority), Season(2, 1, Poster(PosterKind.TmdbSeason))])]);

        Assert.Equal(majority.ImageId, For(plan, 1).ImageId);
        Assert.False(For(plan, 1).NeedsReview);
    }

    [Fact]
    public void SeasonWithoutPostersFallsBackToAniDbAndIsFlagged()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan([Series(1, 2020, [Season(1, 12)], aniDb: aniDb)]);

        Assert.Equal(aniDb.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.NoTmdbPosters, For(plan, 1).Reason);
    }

    [Fact]
    public void NoTmdbLinkFallsBackToAniDbAndIsFlagged()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan([Series(1, 2020, aniDb: aniDb)]);

        Assert.Equal(aniDb.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.NoTmdbLink, For(plan, 1).Reason);
    }

    [Fact]
    public void PosterSetOutsideThePluginIsLeftAlone()
    {
        var manual = Guid.NewGuid();
        var plan = Plan([Series(1, 2020, [Season(1, 12, Poster(PosterKind.TmdbSeason))], current: manual)]);

        Assert.Equal(DecisionAction.LeaveAlone, For(plan, 1).Action);
        Assert.Equal(DecisionReason.SetOutsidePlugin, For(plan, 1).Reason);
    }

    [Fact]
    public void PosterThePluginAssignedCanBeReplaced()
    {
        var old = Guid.NewGuid();
        var poster = Poster(PosterKind.TmdbSeason);
        var plan = Plan([Series(1, 2020, [Season(1, 12, poster)], current: old, assigned: old)]);

        Assert.Equal(DecisionAction.SetPreferred, For(plan, 1).Action);
        Assert.Equal(poster.ImageId, For(plan, 1).ImageId);
    }

    [Fact]
    public void RunningAgainAfterApplyingChangesNothing()
    {
        var poster = Poster(PosterKind.TmdbSeason);
        var plan = Plan([Series(1, 2020, [Season(1, 12, poster)], current: poster.ImageId, assigned: poster.ImageId)]);

        Assert.Equal(DecisionAction.AlreadySet, For(plan, 1).Action);
    }

    [Fact]
    public void PinIsAppliedAndKeepsOtherEntriesOffThatPoster()
    {
        var shared = Poster(PosterKind.TmdbSeason, rating: 9);
        var other = Poster(PosterKind.TmdbSeason, rating: 5);
        var season = Season(1, 12, shared, other);

        var plan = Plan([Series(1, 2020, [season], aniDb: Poster(PosterKind.AniDb)), Series(2, 2021, [season], pinned: shared.ImageId)]);

        Assert.Equal(shared.ImageId, For(plan, 2).ImageId);
        Assert.Equal(DecisionReason.Pinned, For(plan, 2).Reason);
        Assert.Equal(other.ImageId, For(plan, 1).ImageId);
    }

    [Fact]
    public void PinnedEarlierPartStillMakesTheNextPartALaterPart()
    {
        var pinned = Poster(PosterKind.TmdbSeason, rating: 9);
        var aniDb = Poster(PosterKind.AniDb, null);
        var season = Season(1, 12, pinned, Poster(PosterKind.TmdbSeason, rating: 5));

        var plan = Plan([Series(1, 2020, [season], pinned: pinned.ImageId), Series(2, 2021, [season], aniDb: aniDb)]);

        Assert.Equal(aniDb.ImageId, For(plan, 2).ImageId);
        Assert.Equal(DecisionReason.SharedWithEarlierPart, For(plan, 2).Reason);
    }

    [Fact]
    public void SameImageFromTwoSourcesCountsAsADuplicate()
    {
        var key = Guid.NewGuid();
        var first = new PosterCandidate(Guid.NewGuid(), key, PosterKind.TmdbSeason, "en", Rating: 9);
        var sameImage = new PosterCandidate(Guid.NewGuid(), key, PosterKind.TmdbSeason, "en", Rating: 8);
        var plan = Plan([Series(1, 2020, [Season(1, 12, first)]), Series(2, 2021, [Season(2, 12, sameImage)], aniDb: Poster(PosterKind.AniDb))]);

        Assert.Equal(DecisionReason.NoTmdbPosters, For(plan, 2).Reason);
    }

    [Fact]
    public void JellyfinUploadModeKeepsTheSeasonPosterAndPlansTheShowPoster()
    {
        var season = Poster(PosterKind.TmdbSeason);
        var show = Poster(PosterKind.TmdbShow);

        var plan = Plan([Series(1, 2020, [Season(1, 12, season)], shows: [new ShowLink("s", [show])])]);

        Assert.Equal(season.ImageId, For(plan, 1).ImageId);
        Assert.Equal(show.ImageId, plan.JellyfinPosterImageId);
    }

    [Fact]
    public void JellyfinUploadFindsAShowPosterWhenTheMainEntryIsAMovie()
    {
        var show = Poster(PosterKind.TmdbShow);
        var plan = Plan(
            [
                Series(1, 2020, movies: [new MovieLink("m", "Film", [Poster(PosterKind.TmdbMovie)])], isMovie: true),
                Series(2, 2021, [Season(1, 12, Poster(PosterKind.TmdbSeason))], shows: [new ShowLink("s", [show])]),
            ],
            main: 1);

        Assert.Equal(show.ImageId, plan.JellyfinPosterImageId);
    }

    [Fact]
    public void JellyfinUploadWithNoTmdbShowPlansNoUpload()
    {
        var plan = Plan([Series(1, 2020, movies: [new MovieLink("m", "Film", [Poster(PosterKind.TmdbMovie)])], isMovie: true)]);

        Assert.Null(plan.JellyfinPosterImageId);
    }

    [Fact]
    public void PinnedShowPosterWins()
    {
        var pinned = Guid.NewGuid();
        var plan = Plan([Series(1, 2020, [Season(1, 12, Poster(PosterKind.TmdbSeason))], shows: [new ShowLink("s", [Poster(PosterKind.TmdbShow)])])], pinnedShow: pinned);

        Assert.Equal(pinned, plan.JellyfinPosterImageId);
    }

    [Fact]
    public void ShowPosterOnMainSeasonModeUsesTheShowPosterAndLeavesTheSeasonPosterForOthers()
    {
        var show = Poster(PosterKind.TmdbShow);
        var seasonPoster = Poster(PosterKind.TmdbSeason);
        var plan = Plan([Series(1, 2020, [Season(1, 12, seasonPoster)], shows: [new ShowLink("s", [show])])], mode: ShowPosterMode.TmdbShowPosterOnMainSeason);

        Assert.Equal(show.ImageId, For(plan, 1).ImageId);
        Assert.Equal(DecisionReason.ShowPosterOnMainSeason, For(plan, 1).Reason);
        Assert.Null(plan.JellyfinPosterImageId);
    }

    [Fact]
    public void ShowPosterOnMainSeasonModeStillCountsTheMainSeasonsTmdbSeasonAsTaken()
    {
        var aniDb = Poster(PosterKind.AniDb, null);
        var season = Season(1, 12, Poster(PosterKind.TmdbSeason));
        var plan = Plan(
            [Series(1, 2020, [season], shows: [new ShowLink("s", [Poster(PosterKind.TmdbShow)])]), Series(2, 2021, [season], aniDb: aniDb)],
            mode: ShowPosterMode.TmdbShowPosterOnMainSeason);

        Assert.Equal(aniDb.ImageId, For(plan, 2).ImageId);
    }

    [Fact]
    public void SameAsMainSeasonModePlansNoUpload()
    {
        var plan = Plan([Series(1, 2020, [Season(1, 12, Poster(PosterKind.TmdbSeason))], shows: [new ShowLink("s", [Poster(PosterKind.TmdbShow)])])], mode: ShowPosterMode.SameAsMainSeason);

        Assert.Null(plan.JellyfinPosterImageId);
    }

    [Fact]
    public void RankingPrefersPosterShapeThenLanguageThenRating()
    {
        var wide = Poster(PosterKind.TmdbSeason, "en", 10, 1920, 1080);
        var japanese = Poster(PosterKind.TmdbSeason, "ja", 10);
        var english = Poster(PosterKind.TmdbSeason, "en", 2);
        var englishBetter = Poster(PosterKind.TmdbSeason, "en", 8);

        var ranked = PosterRanking.Rank([wide, japanese, english, englishBetter], Languages);

        Assert.Equal([englishBetter.ImageId, english.ImageId, japanese.ImageId, wide.ImageId], ranked.Select(item => item.ImageId));
    }

    [Fact]
    public void ParseLanguagesIgnoresSpacesAndCase()
        => Assert.Equal(["en", "none", "ja"], PosterRanking.ParseLanguages(" EN, none ,ja,"));
}
