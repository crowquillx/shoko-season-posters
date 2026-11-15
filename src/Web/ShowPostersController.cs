using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Services;

namespace Shoko.SeasonPosters.Web;

/// <summary>
/// Lists each applied show's own poster and its specials season's poster, for clients that build shows from Shoko
/// groups, such as the Silo ShokoAnime plugin. Shoko gives a group only its main series' poster, so without this a
/// show and its specials season share their main season's poster. Any signed-in user can read it, because it only
/// names images the server already serves.
/// </summary>
/// <remarks>
/// Either image ID can be null. A show only Silo sees, made of the own series of a group with sub-groups, is listed
/// under that group's ID like any other group.
/// </remarks>
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
            .Select(entry => new
            {
                entry.Key,
                entry.Value.GroupId,
                entry.Value.MainSeriesId,
                ImageId = OnDisk(entry.Value.ImageId),
                SpecialsImageId = OnDisk(entry.Value.SpecialsImageId),
            })
            .Where(entry => entry.ImageId is not null || entry.SpecialsImageId is not null)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal);
        return Content(JsonSerializer.Serialize(new { Shows = shows }, Json), "application/json");
    }

    private Guid? OnDisk(Guid? imageId)
        => imageId is { } id && images.GetImageByID(id) is { IsAvailable: true } ? id : null;
}
