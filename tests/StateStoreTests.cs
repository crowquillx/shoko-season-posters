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

    [Fact]
    public void ShowPostersSurviveARestart()
    {
        var path = Path.Combine(_directory, "state.json");
        var image = Guid.NewGuid();
        new StateStore(path).Update(state =>
        {
            state.ShowPosters["group:933"] = new AppliedShowPoster { GroupId = 933, MainSeriesId = 102, ImageId = image };
            return (true, true);
        });

        var record = new StateStore(path).Read(state => state.ShowPosters["group:933"]);

        Assert.Equal((933, 102, image), (record.GroupId, record.MainSeriesId, record.ImageId));
    }

    [Fact]
    public void AnAppliedShowRecordsItsOwnPoster()
    {
        var image = Guid.NewGuid();
        var records = new Dictionary<string, AppliedShowPoster>();

        var changed = ShowPosterRecords.Update(records, [Show("group:5", 5, image)], new HashSet<Guid> { image });

        Assert.True(changed);
        Assert.Equal((5, 7, image), (records["group:5"].GroupId, records["group:5"].MainSeriesId, records["group:5"].ImageId));
        Assert.False(ShowPosterRecords.Update(records, [Show("group:5", 5, image)], new HashSet<Guid> { image }));
    }

    [Fact]
    public void AShowThatSharesItsMainSeasonPosterIsForgotten()
    {
        var records = new Dictionary<string, AppliedShowPoster>
        {
            ["group:5"] = new() { GroupId = 5, MainSeriesId = 7, ImageId = Guid.NewGuid() },
        };

        var changed = ShowPosterRecords.Update(records, [Show("group:5", 5, null)], new HashSet<Guid>());

        Assert.True(changed);
        Assert.Empty(records);
    }

    [Fact]
    public void APosterThatCouldNotBeDownloadedKeepsTheEarlierRecord()
    {
        var earlier = Guid.NewGuid();
        var records = new Dictionary<string, AppliedShowPoster>
        {
            ["group:5"] = new() { GroupId = 5, MainSeriesId = 7, ImageId = earlier },
        };

        var changed = ShowPosterRecords.Update(records, [Show("group:5", 5, Guid.NewGuid())], new HashSet<Guid>());

        Assert.False(changed);
        Assert.Equal(earlier, records["group:5"].ImageId);
    }

    [Fact]
    public void ShowsOutsideTheAppliedSetAreLeftAlone()
    {
        var other = new AppliedShowPoster { GroupId = 9, MainSeriesId = 90, ImageId = Guid.NewGuid() };
        var records = new Dictionary<string, AppliedShowPoster> { ["group:9"] = other };
        var image = Guid.NewGuid();

        ShowPosterRecords.Update(records, [Show("group:5", 5, image)], new HashSet<Guid> { image });

        Assert.Same(other, records["group:9"]);
        Assert.Equal(2, records.Count);
    }

    private static Planning.ShowPlan Show(string key, int? groupId, Guid? showPoster)
        => new(key, groupId, "Show", 7, Planning.SplitSeasonPolicy.AniDbPoster, Planning.ShowPosterMode.JellyfinUpload, showPoster, [], []);
}
