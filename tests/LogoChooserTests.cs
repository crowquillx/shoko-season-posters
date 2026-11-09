using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Tests;

public sealed class LogoChooserTests
{
    private static readonly Guid Japanese = Guid.NewGuid();
    private static readonly Guid English = Guid.NewGuid();
    private static readonly Guid EnglishNotDownloaded = Guid.NewGuid();
    private static readonly Guid Textless = Guid.NewGuid();

    [Fact]
    public void TheFirstLanguageWinsOverShokosDefault()
    {
        LogoCandidate[] candidates = [new(Japanese, "ja", true), new(EnglishNotDownloaded, "en", false), new(English, "en", true)];

        Assert.Equal(English, LogoChooser.Choose(candidates, ["en", "none"]));
    }

    [Fact]
    public void LaterLanguagesAreUsedInOrder()
    {
        LogoCandidate[] candidates = [new(Japanese, "ja", true), new(Textless, null, true)];

        Assert.Equal(Textless, LogoChooser.Choose(candidates, ["en", "none"]));
        Assert.Equal(Japanese, LogoChooser.Choose(candidates, ["en", "ja", "none"]));
    }

    [Fact]
    public void ALogoInNoListedLanguageIsNeverChosen()
    {
        Assert.Null(LogoChooser.Choose([new(Japanese, "ja", true)], ["en"]));
        Assert.Null(LogoChooser.Choose([new(English, "EN", true)], []));
    }

    [Fact]
    public void ASeriesWithoutAPreferredLogoGetsTheBestOne()
        => Assert.Equal(new LogoDecision(LogoAction.SetPreferred, English), LogoChooser.Decide(null, null, English));

    [Fact]
    public void ALogoSomeoneElsePickedStays()
        => Assert.Equal(LogoAction.Keep, LogoChooser.Decide(Japanese, null, English).Action);

    [Fact]
    public void ThePluginsOwnEarlierChoiceIsReplaced()
        => Assert.Equal(new LogoDecision(LogoAction.SetPreferred, English), LogoChooser.Decide(Textless, Textless, English));

    [Fact]
    public void NothingChangesWithoutABetterLogo()
    {
        Assert.Equal(LogoAction.Keep, LogoChooser.Decide(English, English, English).Action);
        Assert.Equal(LogoAction.Keep, LogoChooser.Decide(Textless, Textless, null).Action);
        Assert.Equal(LogoAction.Keep, LogoChooser.Decide(null, null, null).Action);
    }
}
