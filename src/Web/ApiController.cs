using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Metadata.Services;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Web;

public sealed class PinBody
{
    public Guid ImageId { get; set; }
}

public sealed class ApplyBody
{
    public List<string>? ShowKeys { get; set; }
}

/// <summary>A missing field stays as it is. An empty string returns that setting to the plugin's default.</summary>
public sealed class ShowSettingsBody
{
    public string? SplitPolicy { get; set; }

    public string? ShowMode { get; set; }

    public Guid? PinnedShowPosterImageId { get; set; }
}

/// <summary>What the review page calls. Every route is for administrators only.</summary>
[ApiController]
[Authorize("admin")]
[Route("api/plugin/season-posters")]
public class ApiController(PosterService posters, LogoService logos, IImageManager images) : ControllerBase
{
    private const long MaxUploadBytes = 50 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpGet("plan")]
    public async Task<ContentResult> GetPlan(CancellationToken cancellationToken)
        => JsonResult(ToDto(posters.Latest ?? await posters.ScanAsync(cancellationToken).ConfigureAwait(false)));

    [HttpPost("scan")]
    public async Task<ContentResult> Scan(CancellationToken cancellationToken)
        => JsonResult(ToDto(await posters.ScanAsync(cancellationToken).ConfigureAwait(false)));

    [HttpPost("apply")]
    public async Task<ContentResult> Apply([FromBody] ApplyBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var keys = body.ShowKeys is { Count: > 0 } ? body.ShowKeys.ToHashSet(StringComparer.Ordinal) : null;
        return JsonResult(await posters.ApplyAsync(keys, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Sets every series' logo in the logo languages now, instead of waiting for the next automatic pass.</summary>
    [HttpPost("logos/apply")]
    public async Task<ContentResult> ApplyLogos(CancellationToken cancellationToken)
        => JsonResult(await logos.ApplyAsync(cancellationToken).ConfigureAwait(false));

    [HttpPut("series/{seriesId:int}/pin")]
    public async Task<ActionResult> Pin([FromRoute] int seriesId, [FromBody] PinBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (images.GetImageByID(body.ImageId) is null)
            return NotFound("That image does not exist.");

        return await posters.PinAsync(seriesId, body.ImageId, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : Problem("The choice was saved, but the poster could not be applied. Check the server log.");
    }

    [HttpPost("series/{seriesId:int}/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> Upload([FromRoute] int seriesId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("The file is empty.");
        if (!images.AllowedMimeTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return BadRequest("That file type is not an accepted image type.");

        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
        {
            return await posters.PinUploadAsync(seriesId, stream, file.ContentType, cancellationToken).ConfigureAwait(false)
                ? NoContent()
                : Problem("The upload was stored, but the poster could not be applied. Check the server log.");
        }
    }

    /// <summary>Returns the series to automatic. This also hands over a poster that was set outside this plugin.</summary>
    [HttpDelete("series/{seriesId:int}/pin")]
    public async Task<NoContentResult> Unpin([FromRoute] int seriesId, CancellationToken cancellationToken)
    {
        await posters.UnpinAsync(seriesId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPut("shows/{showKey}")]
    public async Task<ActionResult> UpdateShow([FromRoute] string showKey, [FromBody] ShowSettingsBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        SplitSeasonPolicy? split = null;
        if (!string.IsNullOrEmpty(body.SplitPolicy))
        {
            if (!Enum.TryParse<SplitSeasonPolicy>(body.SplitPolicy, out var parsed))
                return BadRequest("Unknown split season policy.");
            split = parsed;
        }

        ShowPosterMode? mode = null;
        if (!string.IsNullOrEmpty(body.ShowMode))
        {
            if (!Enum.TryParse<ShowPosterMode>(body.ShowMode, out var parsed))
                return BadRequest("Unknown show poster mode.");
            mode = parsed;
        }

        await posters.UpdateShowAsync(
            showKey,
            settings =>
            {
                if (body.SplitPolicy is not null)
                    settings.SplitPolicy = split;
                if (body.ShowMode is not null)
                    settings.ShowMode = mode;
                if (body.PinnedShowPosterImageId is not null)
                    settings.PinnedShowPosterImageId = body.PinnedShowPosterImageId;
            },
            cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private ContentResult JsonResult<T>(T value)
        => Content(JsonSerializer.Serialize(value, Json), "application/json");

    private static object ToDto(LibraryPlan plan) => new
    {
        plan.CreatedAt,
        Shows = plan.Shows.Select(show => new
        {
            show.Key,
            show.GroupId,
            show.Title,
            show.MainSeriesId,
            SplitPolicy = show.SplitPolicy.ToString(),
            ShowMode = show.ShowMode.ToString(),
            show.JellyfinPosterImageId,
            show.NeedsReview,
            ShowPosterAlternatives = show.ShowPosterAlternatives.Select(ToDto),
            Series = show.Series.Select(series => new
            {
                series.SeriesId,
                series.Title,
                series.IsMovieType,
                series.Match,
                Reason = series.Reason.ToString(),
                series.NeedsReview,
                Action = series.Action.ToString(),
                series.ImageId,
                Kind = series.Kind?.ToString(),
                series.CurrentPreferredImageId,
                Alternatives = series.Alternatives.Select(ToDto),
            }),
        }),
    };

    private static object ToDto(PosterCandidate poster) => new
    {
        poster.ImageId,
        Kind = poster.Kind.ToString(),
        poster.Language,
        poster.Width,
        poster.Height,
        poster.IsAvailable,
        poster.RemotePreviewUrl,
    };
}
