using System.Net;
using System.Text;
using Shoko.SeasonPosters.Jellyfin;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Tests;

public class JellyfinTests
{
    private static JellyfinShow Show(string id, string? group, string? series)
    {
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (group is not null)
            providers["Shoko Group"] = group;
        if (series is not null)
            providers["Shoko Series"] = series;
        return new JellyfinShow(id, id, providers, "tag");
    }

    private static ShowPlan Plan(int? groupId, int mainSeries)
        => new("key", groupId, "Title", mainSeries, SplitSeasonPolicy.AniDbPoster, ShowPosterMode.JellyfinUpload, Guid.NewGuid(), [], []);

    [Fact]
    public void FindsTheGroupsShow()
    {
        var found = JellyfinShowPosters.FindShow([Show("a", "7", "1"), Show("b", "8", "2")], Plan(8, 2), new HashSet<int>());

        Assert.Equal("b", found?.Id);
    }

    [Fact]
    public void SeparatedMovieShowIsNotMistakenForTheGroupsShow()
    {
        // With Shokofin's separate-movies setting, the movie's own show also carries the group's ID.
        var found = JellyfinShowPosters.FindShow([Show("movie", "7", "3"), Show("real", "7", "1")], Plan(7, 1), new HashSet<int> { 3 });

        Assert.Equal("real", found?.Id);
    }

    [Fact]
    public void AmbiguousShowsAreSkippedRatherThanGuessed()
    {
        var found = JellyfinShowPosters.FindShow([Show("a", "7", "1"), Show("b", "7", "2")], Plan(7, 1), new HashSet<int>());

        Assert.Null(found);
    }

    [Fact]
    public void StandaloneShowMatchesOnSeries()
    {
        var found = JellyfinShowPosters.FindShow([Show("a", "7", "1"), Show("b", "7", "2")], Plan(null, 2), new HashSet<int>());

        Assert.Equal("b", found?.Id);
    }

    [Fact]
    public async Task ListShowsReadsEveryPageAndKeepsTheImageTag()
    {
        var firstPage = string.Join(",", Enumerable.Range(0, 500).Select(index =>
            "{\"Id\":\"i" + index + "\",\"Name\":\"n\",\"ProviderIds\":{\"Shoko Group\":\"" + index + "\"},\"ImageTags\":{\"Primary\":\"t" + index + "\"}}"));
        var handler = new RecordingHandler(request => request.RequestUri!.Query.Contains("StartIndex=0", StringComparison.Ordinal)
            ? Json("{\"Items\":[" + firstPage + "]}")
            : Json("""{"Items":[{"Id":"last","ProviderIds":{},"ImageTags":{}}]}"""));
        using var http = new HttpClient(handler);

        var shows = await new JellyfinClient(http).ListShowsAsync("http://jf/", "key", CancellationToken.None);

        Assert.Equal(501, shows.Count);
        Assert.Equal("t3", shows[3].PrimaryTag);
        Assert.Equal("3", shows[3].ProviderIds["shoko group"]);
        Assert.Null(shows[500].PrimaryTag);
        Assert.All(handler.Requests, request => Assert.Equal("MediaBrowser Token=\"key\"", request.Headers.GetValues("Authorization").Single()));
    }

    [Fact]
    public async Task UploadSendsBase64WithTheImagesContentType()
    {
        string? body = null;
        string? contentType = null;
        Uri? uri = null;
        var handler = new RecordingHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            contentType = request.Content.Headers.ContentType?.ToString();
            uri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);

        await new JellyfinClient(http).UploadPrimaryImageAsync("http://jf", "key", "item", [1, 2, 3], "image/png", CancellationToken.None);

        Assert.Equal(new Uri("http://jf/Items/item/Images/Primary"), uri);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), body);
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public async Task FailedUploadThrowsSoItIsNotRecordedAsDone()
    {
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<HttpRequestException>(() => new JellyfinClient(http).UploadPrimaryImageAsync("http://jf", "key", "item", [1], "image/png", CancellationToken.None));
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
