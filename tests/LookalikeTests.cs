using ImageMagick;
using Shoko.SeasonPosters.Imaging;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Tests;

public class FingerprintTests
{
    private const int ArtWidth = 240;
    private const int ArtHeight = 360;

    /// <summary>Paints soft blobs and lines, deterministic per seed, standing in for a poster's artwork.</summary>
    private static double[,] Artwork(int seed)
    {
        var random = new Random(seed);
        var art = new double[ArtWidth, ArtHeight];
        for (var blob = 0; blob < 14; blob++)
        {
            double cx = random.Next(ArtWidth), cy = random.Next(ArtHeight), radius = 20 + random.Next(70), value = random.Next(-120, 120);
            for (var x = 0; x < ArtWidth; x++)
            {
                for (var y = 0; y < ArtHeight; y++)
                    art[x, y] += value * Math.Exp(-(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / (2 * radius * radius));
            }
        }

        for (var x = 0; x < ArtWidth; x++)
        {
            for (var y = 0; y < ArtHeight; y++)
                art[x, y] = Math.Clamp(128 + art[x, y], 0, 255);
        }

        return art;
    }

    /// <summary>Covers a band with high-contrast strokes, like a title.</summary>
    private static double[,] WithTitle(double[,] art, double top, double height)
    {
        var copy = (double[,])art.Clone();
        for (var x = 12; x < ArtWidth - 12; x++)
        {
            for (var y = (int)(top * ArtHeight); y < (int)((top + height) * ArtHeight); y++)
                copy[x, y] = (x / 7 + y / 9) % 2 == 0 ? 250 : 15;
        }

        return copy;
    }

    private static double[,] Zoomed(double[,] art, double zoom)
    {
        var width = (int)(ArtWidth * zoom);
        var height = (int)(ArtHeight * zoom);
        var left = (ArtWidth - width) / 2;
        var top = (ArtHeight - height) / 2;
        var copy = new double[ArtWidth, ArtHeight];
        for (var x = 0; x < ArtWidth; x++)
        {
            for (var y = 0; y < ArtHeight; y++)
                copy[x, y] = art[left + (x * width / ArtWidth), top + (y * height / ArtHeight)];
        }

        return copy;
    }

    private static PosterFingerprint Fingerprint(double[,] art, int noiseSeed = 0)
    {
        var noise = new Random(noiseSeed);
        var pixels = new byte[PosterFingerprint.Width * PosterFingerprint.Height];
        var cellWidth = ArtWidth / PosterFingerprint.Width;
        var cellHeight = ArtHeight / PosterFingerprint.Height;
        for (var row = 0; row < PosterFingerprint.Height; row++)
        {
            for (var column = 0; column < PosterFingerprint.Width; column++)
            {
                var sum = 0.0;
                for (var x = 0; x < cellWidth; x++)
                {
                    for (var y = 0; y < cellHeight; y++)
                        sum += art[(column * cellWidth) + x, (row * cellHeight) + y];
                }

                var jitter = noiseSeed == 0 ? 0 : noise.Next(-2, 3);
                pixels[(row * PosterFingerprint.Width) + column] = (byte)Math.Clamp((sum / (cellWidth * cellHeight)) + jitter, 0, 255);
            }
        }

        return new PosterFingerprint(pixels);
    }

    [Fact]
    public void ReencodedCopyLooksTheSame()
    {
        var art = Artwork(1);
        Assert.Equal(Likeness.SameImage, PosterFingerprint.Compare(Fingerprint(art), Fingerprint(art, noiseSeed: 7)));
    }

    [Fact]
    public void TextlessAndTitledVersionsAreTheSameArtwork()
    {
        var art = Artwork(2);
        Assert.Equal(Likeness.SameArtwork, PosterFingerprint.Compare(Fingerprint(art), Fingerprint(WithTitle(art, 0.05, 0.14))));
    }

    [Fact]
    public void DifferentTitlesOnOneArtworkAreTheSameArtwork()
    {
        var art = Artwork(3);
        Assert.Equal(Likeness.SameArtwork, PosterFingerprint.Compare(Fingerprint(WithTitle(art, 0.04, 0.12)), Fingerprint(WithTitle(art, 0.78, 0.15))));
    }

    [Fact]
    public void TighterCropOfOneArtworkIsTheSameArtwork()
    {
        var art = Artwork(4);
        Assert.Equal(Likeness.SameArtwork, PosterFingerprint.Compare(Fingerprint(art), Fingerprint(Zoomed(art, 0.85))));
    }

    [Fact]
    public void DifferentArtworkIsDifferentEvenWithTheSameTitle()
    {
        for (var seed = 10; seed < 30; seed++)
        {
            var first = Fingerprint(WithTitle(Artwork(seed), 0.05, 0.14));
            var second = Fingerprint(WithTitle(Artwork(seed + 100), 0.05, 0.14));
            Assert.Equal(Likeness.Different, PosterFingerprint.Compare(first, second));
        }
    }

    [Fact]
    public void FingerprintSurvivesStorage()
    {
        var fingerprint = Fingerprint(Artwork(5));
        Assert.Equal(fingerprint.Pixels, PosterFingerprint.FromBase64(fingerprint.ToBase64())!.Pixels);
        Assert.Null(PosterFingerprint.FromBase64("not base64"));
        Assert.Null(PosterFingerprint.FromBase64(Convert.ToBase64String(new byte[3])));
    }

    [Fact]
    public void DecoderReadsAJpegIntoAFingerprint()
    {
        var art = Artwork(6);
        using var image = new MagickImage(MagickColors.Black, ArtWidth, ArtHeight);
        using (var pixels = image.GetPixels())
        {
            for (var x = 0; x < ArtWidth; x++)
            {
                for (var y = 0; y < ArtHeight; y++)
                {
                    var value = (byte)art[x, y];
                    pixels.SetPixel(x, y, [value, value, value]);
                }
            }
        }

        image.Format = MagickFormat.Jpeg;
        var decoded = FingerprintDecoder.Decode(image.ToByteArray());

        Assert.NotNull(decoded);
        Assert.Equal(Likeness.SameImage, PosterFingerprint.Compare(new PosterFingerprint(decoded), Fingerprint(art)));
    }

    [Fact]
    public void DecoderRejectsDataThatIsNotAnImage()
        => Assert.Null(FingerprintDecoder.Decode("<html>not an image</html>"u8.ToArray()));
}

public class LookalikePlanningTests
{
    private static readonly string[] Languages = ["en", "none", "ja"];

    /// <summary>Says which pairs look alike; every other pair is different.</summary>
    private sealed class Lookalikes : ILookalikes
    {
        private readonly Dictionary<(Guid, Guid), Likeness> _pairs = [];

        public Lookalikes Same(PosterCandidate first, PosterCandidate second, Likeness likeness = Likeness.SameArtwork)
        {
            _pairs[(first.UniquenessKey, second.UniquenessKey)] = likeness;
            _pairs[(second.UniquenessKey, first.UniquenessKey)] = likeness;
            return this;
        }

        public Likeness Compare(PosterCandidate first, PosterCandidate second)
            => first.UniquenessKey == second.UniquenessKey ? Likeness.SameImage : _pairs.GetValueOrDefault((first.UniquenessKey, second.UniquenessKey));

        public bool IsKnown(PosterCandidate poster) => true;
    }

    private static PosterCandidate Poster(PosterKind kind, string? language = "en", double rating = 5)
    {
        var id = Guid.NewGuid();
        return new PosterCandidate(id, id, kind, language, 1000, 1500, rating);
    }

    private static SeriesFacts Series(int id, int year, IReadOnlyList<SeasonLink>? seasons = null, IReadOnlyList<ShowLink>? shows = null, PosterCandidate? aniDb = null)
        => new(id, $"Series {id}", new DateOnly(year, 1, 1), false, seasons ?? [], [], shows ?? [], aniDb is null ? [] : [aniDb]);

    private static SeasonLink Season(int number, params PosterCandidate[] posters) => new($"season-{number}", number, 12, "show-1", posters);

    private static ShowPlan Plan(
        IReadOnlyList<SeriesFacts> series,
        ILookalikes lookalikes,
        SplitSeasonPolicy split = SplitSeasonPolicy.AniDbPoster,
        ShowPosterText text = ShowPosterText.FollowLanguages,
        Guid? pinnedShow = null)
        => PosterPlanner.Plan(
            new ShowFacts("group:1", 1, "Group", series[0].Id, series, split, ShowPosterMode.JellyfinUpload, pinnedShow, text),
            new PlanOptions(Languages, 60),
            lookalikes);

    private static SeriesDecision For(ShowPlan plan, int id) => plan.Series.Single(item => item.SeriesId == id);

    [Fact]
    public void ShowPosterSkipsAShowPosterWithTheFirstSeasonsArtwork()
    {
        var season = Poster(PosterKind.TmdbSeason);
        var sameArt = Poster(PosterKind.TmdbShow, rating: 9);
        var otherArt = Poster(PosterKind.TmdbShow, rating: 4);
        var plan = Plan(
            [Series(1, 2020, [Season(1, season)], [new ShowLink("s", [sameArt, otherArt])])],
            new Lookalikes().Same(season, sameArt));

        Assert.Equal(otherArt.ImageId, plan.JellyfinPosterImageId);
        Assert.Equal("TMDB show poster", plan.ShowPoster!.Source);
        Assert.Empty(plan.ShowPoster.LooksLike);
    }

    [Fact]
    public void ShowPosterUsesAnUnusedSeasonPosterWhenEveryShowPosterRepeatsASeason()
    {
        var season = Poster(PosterKind.TmdbSeason, rating: 9);
        var spare = Poster(PosterKind.TmdbSeason, rating: 3);
        var show = Poster(PosterKind.TmdbShow);
        var plan = Plan(
            [Series(1, 2020, [Season(1, season, spare)], [new ShowLink("s", [show])])],
            new Lookalikes().Same(season, show));

        Assert.Equal(season.ImageId, For(plan, 1).ImageId);
        Assert.Equal(spare.ImageId, plan.JellyfinPosterImageId);
        Assert.Equal("Unused poster of Series 1", plan.ShowPoster!.Source);
    }

    [Fact]
    public void AnotherVersionOfTheArtworkIsUsedWhenNothingElseIsLeftAndIsNotFlagged()
    {
        var season = Poster(PosterKind.TmdbSeason);
        var show = Poster(PosterKind.TmdbShow);
        var plan = Plan([Series(1, 2020, [Season(1, season)], [new ShowLink("s", [show])])], new Lookalikes().Same(season, show));

        Assert.Equal(show.ImageId, plan.JellyfinPosterImageId);
        Assert.False(plan.NeedsReview);
        Assert.Equal(new Resemblance("Series 1", Likeness.SameArtwork), Assert.Single(plan.ShowPoster!.LooksLike));
    }

    [Fact]
    public void ShowPosterThatLooksTheSameAsASeasonIsFlagged()
    {
        var season = Poster(PosterKind.TmdbSeason);
        var show = Poster(PosterKind.TmdbShow);
        var plan = Plan([Series(1, 2020, [Season(1, season)], [new ShowLink("s", [show])])], new Lookalikes().Same(season, show, Likeness.SameImage));

        Assert.Equal(show.ImageId, plan.JellyfinPosterImageId);
        Assert.True(plan.ShowPoster!.NeedsReview);
        Assert.True(plan.NeedsReview);
    }

    [Fact]
    public void TextlessFirstPrefersATextlessVersionOfTheSeasonsArtworkOverATitledPosterWithOtherArtwork()
    {
        var season = Poster(PosterKind.TmdbSeason);
        var titledOtherArt = Poster(PosterKind.TmdbShow, "en", rating: 9);
        var textlessSameArt = Poster(PosterKind.TmdbShow, null, rating: 2);
        var lookalikes = new Lookalikes().Same(season, textlessSameArt);
        var series = new[] { Series(1, 2020, [Season(1, season)], [new ShowLink("s", [titledOtherArt, textlessSameArt])]) };

        Assert.Equal(titledOtherArt.ImageId, Plan(series, lookalikes).JellyfinPosterImageId);

        var plan = Plan(series, lookalikes, text: ShowPosterText.TextlessFirst);
        Assert.Equal(textlessSameArt.ImageId, plan.JellyfinPosterImageId);
        Assert.Equal("Textless TMDB show poster", plan.ShowPoster!.Source);
    }

    [Fact]
    public void TextlessFirstPrefersTextlessArtworkUnlikeEverySeason()
    {
        var season = Poster(PosterKind.TmdbSeason);
        var textlessSameArt = Poster(PosterKind.TmdbShow, null, rating: 9);
        var textlessOtherArt = Poster(PosterKind.TmdbShow, null, rating: 2);
        var plan = Plan(
            [Series(1, 2020, [Season(1, season)], [new ShowLink("s", [textlessSameArt, textlessOtherArt])])],
            new Lookalikes().Same(season, textlessSameArt),
            text: ShowPosterText.TextlessFirst);

        Assert.Equal(textlessOtherArt.ImageId, plan.JellyfinPosterImageId);
    }

    [Fact]
    public void TextlessFirstCanUseATextlessSeasonPoster()
    {
        var titled = Poster(PosterKind.TmdbSeason, "en", rating: 9);
        var textless = Poster(PosterKind.TmdbSeason, null, rating: 5);
        var showPoster = Poster(PosterKind.TmdbShow, "en");
        var plan = Plan(
            [Series(1, 2020, [Season(1, titled, textless)], [new ShowLink("s", [showPoster])])],
            new Lookalikes().Same(titled, textless),
            text: ShowPosterText.TextlessFirst);

        Assert.Equal(titled.ImageId, For(plan, 1).ImageId);
        Assert.Equal(textless.ImageId, plan.JellyfinPosterImageId);
        Assert.Equal("Textless, Unused poster of Series 1", plan.ShowPoster!.Source);
    }

    [Fact]
    public void TextlessFirstSkipsATextlessPosterThatLooksTheSameAsASeason()
    {
        var season = Poster(PosterKind.TmdbSeason);
        var textless = Poster(PosterKind.TmdbShow, null, rating: 9);
        var titled = Poster(PosterKind.TmdbShow, "en", rating: 2);
        var plan = Plan(
            [Series(1, 2020, [Season(1, season)], [new ShowLink("s", [textless, titled])])],
            new Lookalikes().Same(season, textless, Likeness.SameImage),
            text: ShowPosterText.TextlessFirst);

        Assert.Equal(titled.ImageId, plan.JellyfinPosterImageId);
    }

    [Fact]
    public void LaterSeasonAvoidsAPosterWithTheEarlierSeasonsArtwork()
    {
        var first = Poster(PosterKind.TmdbSeason);
        var reused = Poster(PosterKind.TmdbSeason, rating: 9);
        var own = Poster(PosterKind.TmdbSeason, rating: 3);
        var plan = Plan([Series(1, 2020, [Season(1, first)]), Series(2, 2021, [Season(2, reused, own)])], new Lookalikes().Same(first, reused));

        Assert.Equal(own.ImageId, For(plan, 2).ImageId);
        Assert.Empty(For(plan, 2).LooksLike);
    }

    [Fact]
    public void SeasonWhosePostersAllLookTheSameAsAnotherFallsBackToAniDbAndIsFlagged()
    {
        var first = Poster(PosterKind.TmdbSeason);
        var copy = Poster(PosterKind.TmdbSeason);
        var aniDb = Poster(PosterKind.AniDb, null);
        var plan = Plan(
            [Series(1, 2020, [Season(1, first)]), Series(2, 2021, [Season(2, copy)], aniDb: aniDb)],
            new Lookalikes().Same(first, copy, Likeness.SameImage));

        Assert.Equal(aniDb.ImageId, For(plan, 2).ImageId);
        Assert.Equal(DecisionReason.LooksLikeAnotherPoster, For(plan, 2).Reason);
        Assert.True(For(plan, 2).NeedsReview);
    }

    [Fact]
    public void SeasonStillUsesAnotherVersionOfItsArtworkBeforeAniDb()
    {
        var first = Poster(PosterKind.TmdbSeason);
        var variant = Poster(PosterKind.TmdbSeason);
        var plan = Plan(
            [Series(1, 2020, [Season(1, first)]), Series(2, 2021, [Season(2, variant)], aniDb: Poster(PosterKind.AniDb))],
            new Lookalikes().Same(first, variant));

        Assert.Equal(variant.ImageId, For(plan, 2).ImageId);
        Assert.Equal(new Resemblance("Series 1", Likeness.SameArtwork), Assert.Single(For(plan, 2).LooksLike));
    }

    [Fact]
    public void NextPosterPolicyWillNotGiveALaterPartAnotherVersionOfTheEarlierPartsArtwork()
    {
        var first = Poster(PosterKind.TmdbSeason, rating: 9);
        var variant = Poster(PosterKind.TmdbSeason, rating: 5);
        var aniDb = Poster(PosterKind.AniDb, null);
        var season = Season(1, first, variant);
        var plan = Plan(
            [Series(1, 2020, [season]), Series(2, 2021, [season], aniDb: aniDb)],
            new Lookalikes().Same(first, variant),
            split: SplitSeasonPolicy.NextTmdbPoster);

        Assert.Equal(aniDb.ImageId, For(plan, 2).ImageId);
    }

    [Fact]
    public void PinnedShowPosterKeepsSeasonsOffItsArtwork()
    {
        var pinned = Poster(PosterKind.TmdbShow);
        var sameArt = Poster(PosterKind.TmdbSeason, rating: 9);
        var other = Poster(PosterKind.TmdbSeason, rating: 3);
        var plan = Plan(
            [Series(1, 2020, [Season(1, sameArt, other)], [new ShowLink("s", [pinned])])],
            new Lookalikes().Same(pinned, sameArt),
            pinnedShow: pinned.ImageId);

        Assert.Equal(pinned.ImageId, plan.JellyfinPosterImageId);
        Assert.True(plan.ShowPoster!.IsPinned);
        Assert.Equal(other.ImageId, For(plan, 1).ImageId);
    }

    [Fact]
    public void FingerprintLookalikesListsWhatItCouldNotCompare()
    {
        var known = Poster(PosterKind.TmdbSeason);
        var unknown = Poster(PosterKind.TmdbShow);
        var fingerprint = new PosterFingerprint(new byte[PosterFingerprint.Width * PosterFingerprint.Height]);
        var lookalikes = new FingerprintLookalikes(key => key == known.UniquenessKey ? fingerprint : null, LookalikePolicy.AvoidSameArtwork);

        Assert.Equal(Likeness.Different, lookalikes.Compare(known, unknown));
        Assert.Equal(unknown.ImageId, Assert.Single(lookalikes.Missing).Value);
    }

    [Fact]
    public void SameImageOnlyPolicyTreatsOtherVersionsAsDifferent()
    {
        var art = new byte[PosterFingerprint.Width * PosterFingerprint.Height];
        for (var index = 0; index < art.Length; index++)
        {
            var (row, column) = Math.DivRem(index, PosterFingerprint.Width);
            art[index] = (byte)(128 + (100 * Math.Sin(column / 4.0) * Math.Cos(row / 5.0)));
        }

        var titled = (byte[])art.Clone();
        for (var index = PosterFingerprint.Width; index < PosterFingerprint.Width * 4; index++)
            titled[index] = (byte)(index % 3 == 0 ? 255 : 10);

        var first = Poster(PosterKind.TmdbSeason);
        var second = Poster(PosterKind.TmdbShow);
        PosterFingerprint? Find(Guid key) => new(key == first.UniquenessKey ? art : titled);
        Assert.Equal(Likeness.SameArtwork, new FingerprintLookalikes(Find, LookalikePolicy.AvoidSameArtwork).Compare(first, second));
        Assert.Equal(Likeness.Different, new FingerprintLookalikes(Find, LookalikePolicy.AvoidSameImageOnly).Compare(first, second));
        Assert.Equal(Likeness.Different, new FingerprintLookalikes(Find, LookalikePolicy.Off).Compare(first, second));
    }
}
