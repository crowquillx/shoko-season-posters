using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Services;

namespace Shoko.SeasonPosters.Web;

/// <summary>
/// Lists each applied show's own poster, for clients that build shows from Shoko groups, such as the Silo ShokoAnime
/// plugin. Shoko gives a group only its main series' poster, so without this a show and its main season share one.
/// Any signed-in user can read it, because it only names images the server already serves.
/// </summary>
[ApiController]
[Authorize]
[Route("api/plugin/season-posters/show-posters")]
public class ShowPostersController(StateStore store, IImageManager images) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpGet("")]
    public ContentResult List()
    {
        var shows = store.Read(state => state.ShowPosters.ToArray())
            .Where(entry => images.GetImageByID(entry.Value.ImageId) is { IsAvailable: true })
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new { entry.Key, entry.Value.GroupId, entry.Value.MainSeriesId, entry.Value.ImageId });
        return Content(JsonSerializer.Serialize(new { Shows = shows }, Json), "application/json");
    }
}
