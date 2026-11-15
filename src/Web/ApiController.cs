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

public sealed class LookalikesBody
{
    public string ShowKey { get; set; } = string.Empty;

    /// <summary>The entry being chosen for, or null when choosing the show poster.</summary>
    public int? SeriesId { get; set; }

    public List<Guid> ImageIds { get; set; } = [];
}

/// <summary>A missing field stays as it is. An empty string returns that setting to the plugin's default.</summary>
public sealed class ShowSettingsBody
{
    public string? SplitPolicy { get; set; }

    public string? ShowMode { get; set; }

    public Guid? PinnedShowPosterImageId { get; set; }

    public string? ShowText { get; set; }
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

        ShowPosterText? text = null;
        if (!string.IsNullOrEmpty(body.ShowText))
        {
            if (!Enum.TryParse<ShowPosterText>(body.ShowText, out var parsed))
                return BadRequest("Unknown show poster text setting.");
            text = parsed;
        }

        await posters.UpdateShowAsync(
            showKey,
            settings =>
            {
                if (body.ShowText is not null)
                    settings.ShowText = text;
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

    /// <summary>Makes an image the show's own poster. It is applied with the show.</summary>
    [HttpPut("shows/{showKey}/poster")]
    public async Task<ActionResult> PinShowPoster([FromRoute] string showKey, [FromBody] PinBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (images.GetImageByID(body.ImageId) is null)
            return NotFound("That image does not exist.");

        await posters.UpdateShowAsync(showKey, settings => settings.PinnedShowPosterImageId = body.ImageId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("shows/{showKey}/poster/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> UploadShowPoster([FromRoute] string showKey, IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("The file is empty.");
        if (!images.AllowedMimeTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return BadRequest("That file type is not an accepted image type.");

        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
            await posters.UploadShowPosterAsync(showKey, stream, file.ContentType, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Lets the plugin choose the show poster again.</summary>
    [HttpDelete("shows/{showKey}/poster")]
    public async Task<NoContentResult> UnpinShowPoster([FromRoute] string showKey, CancellationToken cancellationToken)
    {
        await posters.UpdateShowAsync(showKey, settings => settings.PinnedShowPosterImageId = null, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Says which posters in use in the show each image looks like.</summary>
    [HttpPost("lookalikes")]
    public async Task<ContentResult> Lookalikes([FromBody] LookalikesBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = await posters.FindLookalikesAsync(body.ShowKey, body.SeriesId, body.ImageIds, cancellationToken).ConfigureAwait(false);
        return JsonResult(new
        {
            Images = found.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value.Select(item => new { item.UsedBy, Likeness = item.Likeness.ToString() })),
        });
    }

    private ContentResult JsonResult<T>(T value)
        => Content(JsonSerializer.Serialize(value, Json), "application/json");

    private object ToDto(LibraryPlan plan)
    {
        var applied = posters.AppliedShowPosters();
        return new
        {
            plan.CreatedAt,
            Shows = plan.Shows.Select(show => ToDto(show, applied.TryGetValue(show.Key, out var id) ? id : null)),
        };
    }

    private static object ToDto(ShowPlan show, Guid? appliedShowPoster)
    {
        var main = show.Series.FirstOrDefault(series => series.SeriesId == show.MainSeriesId);
        return new
        {
            show.Key,
            show.GroupId,
            show.Title,
            show.MainSeriesId,
            SplitPolicy = show.SplitPolicy.ToString(),
            ShowMode = show.ShowMode.ToString(),
            ShowText = show.ShowText.ToString(),
            show.JellyfinPosterImageId,

            // What clients show for the show: its own poster, or in the other modes the main season's.
            ShowPosterImageId = show.ShowMode is ShowPosterMode.JellyfinUpload ? show.JellyfinPosterImageId : main?.ImageId,
            ShowPosterSource = show.ShowMode switch
            {
                ShowPosterMode.JellyfinUpload => show.ShowPoster?.Source,
                _ when main is null => null,
                _ => $"Shared with {main.Title}",
            },
            ShowPosterPinned = show.ShowPoster?.IsPinned == true,
            ShowPosterNeedsReview = show.ShowPoster?.NeedsReview == true,
            ShowPosterLooksLike = (show.ShowPoster?.LooksLike ?? []).Select(ToDto),
            ShowPosterPending = show.JellyfinPosterImageId is { } planned && planned != appliedShowPoster,
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
                LooksLike = series.LooksLike.Select(ToDto),
                Alternatives = series.Alternatives.Select(ToDto),
            }),
        };
    }

    private static object ToDto(Resemblance resemblance) => new { resemblance.UsedBy, Likeness = resemblance.Likeness.ToString() };

    private static object ToDto(PosterCandidate poster) => new
    {
        poster.ImageId,
        Kind = poster.Kind.ToString(),
        poster.Language,
        poster.Width,
        poster.Height,
        poster.Rating,
        poster.IsAvailable,
        poster.RemotePreviewUrl,
    };
}
