namespace Shoko.SeasonPosters.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ChoicesSurviveARestart()
    {
        var path = Path.Combine(_directory, "nested", "state.json");
        var image = Guid.NewGuid();
        new StateStore(path).Update(state =>
        {
            state.Pins[4] = image;
            state.Assigned[5] = image;
            state.Shows["group:1"] = new ShowSettings { PinnedShowPosterImageId = image };
            return (true, true);
        });

        var reloaded = new StateStore(path);

        Assert.Equal(image, reloaded.Read(state => state.Pins[4]));
        Assert.Equal(image, reloaded.Read(state => state.Assigned[5]));
        Assert.Equal(image, reloaded.Read(state => state.Shows["group:1"].PinnedShowPosterImageId));
    }

    [Fact]
    public void EnumsAreStoredByNameSoReorderingThemCannotChangeAChoice()
    {
        var path = Path.Combine(_directory, "state.json");
        new StateStore(path).Update(state =>
        {
            state.Shows["group:1"] = new ShowSettings { SplitPolicy = Planning.SplitSeasonPolicy.NextTmdbPoster };
            return (true, true);
        });

        Assert.Contains("NextTmdbPoster", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(Planning.SplitSeasonPolicy.NextTmdbPoster, new StateStore(path).Read(state => state.Shows["group:1"].SplitPolicy));
    }

    [Fact]
    public void NothingIsWrittenWhenNothingChanged()
    {
        var path = Path.Combine(_directory, "state.json");

        new StateStore(path).Update(_ => (true, false));

        Assert.False(File.Exists(path));
    }
}
