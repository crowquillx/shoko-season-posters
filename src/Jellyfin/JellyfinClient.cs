using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoko.SeasonPosters.Jellyfin;

public sealed record JellyfinShow(string Id, string Name, IReadOnlyDictionary<string, string> ProviderIds, string? PrimaryTag);

/// <summary>The few Jellyfin calls the show poster upload needs.</summary>
public sealed class JellyfinClient(HttpClient http)
{
    private const int PageSize = 500;

    public async Task<IReadOnlyList<JellyfinShow>> ListShowsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var shows = new List<JellyfinShow>();
        for (var start = 0; ; start += PageSize)
        {
            var path = $"Items?IncludeItemTypes=Series&Recursive=true&Fields=ProviderIds&EnableImageTypes=Primary&EnableUserData=false&StartIndex={start}&Limit={PageSize}";
            var page = await GetItemsAsync(baseUrl, apiKey, path, cancellationToken).ConfigureAwait(false);
            shows.AddRange(page.Items.Select(ToShow));
            if (page.Items.Count < PageSize)
                return shows;
        }
    }

    public async Task<JellyfinShow?> GetShowAsync(string baseUrl, string apiKey, string itemId, CancellationToken cancellationToken)
    {
        var path = $"Items?Ids={Uri.EscapeDataString(itemId)}&Fields=ProviderIds&EnableImageTypes=Primary&EnableUserData=false";
        var page = await GetItemsAsync(baseUrl, apiKey, path, cancellationToken).ConfigureAwait(false);
        return page.Items.Count > 0 ? ToShow(page.Items[0]) : null;
    }

    /// <summary>Jellyfin takes a new image as a base64 body, with the image's own type as the content type.</summary>
    public async Task UploadPrimaryImageAsync(string baseUrl, string apiKey, string itemId, byte[] image, string contentType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(baseUrl, $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary"));
        Authorize(request, apiKey);
        request.Content = new StringContent(Convert.ToBase64String(image), Encoding.ASCII);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<ItemsPage> GetItemsAsync(string baseUrl, string apiKey, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(baseUrl, path));
        Authorize(request, apiKey);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<ItemsPage>(body, cancellationToken: cancellationToken).ConfigureAwait(false) ?? new ItemsPage();
    }

    private static Uri Url(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path}");

    private static void Authorize(HttpRequestMessage request, string apiKey)
        => request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");

    private static JellyfinShow ToShow(Item item)
        => new(
            item.Id,
            item.Name ?? string.Empty,
            item.ProviderIds is null ? new Dictionary<string, string>() : new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            item.ImageTags?.GetValueOrDefault("Primary"));

    private sealed class ItemsPage
    {
        [JsonPropertyName("Items")]
        public List<Item> Items { get; set; } = [];
    }

    private sealed class Item
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string? Name { get; set; }

        [JsonPropertyName("ProviderIds")]
        public Dictionary<string, string>? ProviderIds { get; set; }

        [JsonPropertyName("ImageTags")]
        public Dictionary<string, string>? ImageTags { get; set; }
    }
}
