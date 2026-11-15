using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Config;
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

/// <summary>The plugin's settings, for the review page. A missing field stays as it is.</summary>
public sealed class SettingsBody
{
    public bool? AutoApply { get; set; }

    public string? PosterLanguages { get; set; }

    public string? ShowPosterLanguages { get; set; }

    public string? ShowPosterText { get; set; }

    public string? Lookalikes { get; set; }

    public string? LogoLanguages { get; set; }

    public int? MinimumSeasonSharePercent { get; set; }

    public string? SplitSeasons { get; set; }

    public string? ShowPoster { get; set; }

    public string? SpecialsPoster { get; set; }

    public string? JellyfinUrl { get; set; }

    /// <summary>A new key. Empty or missing keeps the stored key, since the page never receives it.</summary>
    public string? JellyfinApiKey { get; set; }

    public bool? ClearJellyfinApiKey { get; set; }
}

public sealed class LookalikesBody
{
    public string ShowKey { get; set; } = string.Empty;

    /// <summary>The entry being chosen for, or null when choosing the show or specials poster.</summary>
    public int? SeriesId { get; set; }

    /// <summary>Whether the specials poster is being chosen.</summary>
    public bool Specials { get; set; }

    public List<Guid> ImageIds { get; set; } = [];
}

/// <summary>A missing field stays as it is. An empty string returns that setting to the plugin's default.</summary>
public sealed class ShowSettingsBody
{
    public string? SplitPolicy { get; set; }

    public string? ShowMode { get; set; }

    public Guid? PinnedShowPosterImageId { get; set; }

    public string? ShowText { get; set; }

    public string? SpecialsMode { get; set; }
}

/// <summary>What the review page calls. Every route is for administrators only.</summary>
[ApiController]
[Authorize("admin")]
[Route("api/plugin/season-posters")]
public class ApiController(
    PosterService posters,
    LogoService logos,
    IImageManager images,
    ConfigurationProvider<SeasonPostersConfiguration> configuration) : ControllerBase
{
    private const long MaxUploadBytes = 50 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpGet("plan")]
    public async Task<ContentResult> GetPlan(CancellationToken cancellationToken)
        => JsonResult(ToDto(posters.Latest ?? await posters.ScanAsync(cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// The plugin's settings. Shoko's web UI has no settings form for plugins that are not providers, so the review
    /// page edits them. The Jellyfin API key is never sent back, only whether one is stored.
    /// </summary>
    [HttpGet("settings")]
    public ContentResult GetSettings() => JsonResult(ToDto(configuration.Load()));

    /// <summary>Saves the settings. Scan again afterwards to plan with them.</summary>
    [HttpPut("settings")]
    public ActionResult SaveSettings([FromBody] SettingsBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var settings = configuration.Load(copy: true);
        if (body.AutoApply is { } autoApply)
            settings.AutoApply = autoApply;
        if (body.PosterLanguages is not null)
            settings.PosterLanguages = body.PosterLanguages.Trim();
        if (body.ShowPosterLanguages is not null)
            settings.ShowPosterLanguages = body.ShowPosterLanguages.Trim();
        if (body.LogoLanguages is not null)
            settings.LogoLanguages = body.LogoLanguages.Trim();
        if (body.MinimumSeasonSharePercent is { } share)
            settings.MinimumSeasonSharePercent = share;
        if (body.JellyfinUrl is not null)
            settings.JellyfinUrl = body.JellyfinUrl.Trim();
        if (body.ClearJellyfinApiKey == true)
            settings.JellyfinApiKey = null;
        else if (!string.IsNullOrWhiteSpace(body.JellyfinApiKey))
            settings.JellyfinApiKey = body.JellyfinApiKey.Trim();

        if (!TryParse(body.ShowPosterText, settings.ShowPosterText, out var text)
            || !TryParse(body.Lookalikes, settings.Lookalikes, out var lookalikes)
            || !TryParse(body.SplitSeasons, settings.SplitSeasons, out var split)
            || !TryParse(body.ShowPoster, settings.ShowPoster, out var mode)
            || !TryParse(body.SpecialsPoster, settings.SpecialsPoster, out var specials))
            return BadRequest("Unknown setting value.");

        settings.ShowPosterText = text;
        settings.Lookalikes = lookalikes;
        settings.SplitSeasons = split;
        settings.ShowPoster = mode;
        settings.SpecialsPoster = specials;

        var errors = configuration.Validate(settings);
        if (errors.Count > 0)
            return BadRequest(string.Join(" ", errors.Select(pair => $"{pair.Key}: {string.Join(", ", pair.Value)}")));

        return configuration.Save(settings)
            ? Content(JsonSerializer.Serialize(ToDto(settings), Json), "application/json")
            : Problem("The settings could not be saved. Check the server log.");
    }

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

        SpecialsPosterMode? specials = null;
        if (!string.IsNullOrEmpty(body.SpecialsMode))
        {
            if (!Enum.TryParse<SpecialsPosterMode>(body.SpecialsMode, out var parsed))
                return BadRequest("Unknown specials poster setting.");
            specials = parsed;
        }

        await posters.UpdateShowAsync(
            showKey,
            settings =>
            {
                if (body.SpecialsMode is not null)
                    settings.SpecialsMode = specials;
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

    /// <summary>Makes an image the show's specials poster. It is applied with the show.</summary>
    [HttpPut("shows/{showKey}/specials-poster")]
    public async Task<ActionResult> PinSpecialsPoster([FromRoute] string showKey, [FromBody] PinBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (images.GetImageByID(body.ImageId) is null)
            return NotFound("That image does not exist.");

        await posters.UpdateShowAsync(showKey, settings => settings.PinnedSpecialsPosterImageId = body.ImageId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("shows/{showKey}/specials-poster/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> UploadSpecialsPoster([FromRoute] string showKey, IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("The file is empty.");
        if (!images.AllowedMimeTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return BadRequest("That file type is not an accepted image type.");

        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
            await posters.UploadSpecialsPosterAsync(showKey, stream, file.ContentType, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Lets the plugin choose the specials poster again.</summary>
    [HttpDelete("shows/{showKey}/specials-poster")]
    public async Task<NoContentResult> UnpinSpecialsPoster([FromRoute] string showKey, CancellationToken cancellationToken)
    {
        await posters.UpdateShowAsync(showKey, settings => settings.PinnedSpecialsPosterImageId = null, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Says which posters in use in the show each image looks like.</summary>
    [HttpPost("lookalikes")]
    public async Task<ContentResult> Lookalikes([FromBody] LookalikesBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = await posters.FindLookalikesAsync(body.ShowKey, body.SeriesId, body.Specials, body.ImageIds, cancellationToken).ConfigureAwait(false);
        return JsonResult(new
        {
            Images = found.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value.Select(item => new { item.UsedBy, Likeness = item.Likeness.ToString() })),
        });
    }

    private static bool TryParse<T>(string? value, T current, out T parsed)
        where T : struct, Enum
    {
        if (string.IsNullOrEmpty(value))
        {
            parsed = current;
            return true;
        }

        return Enum.TryParse(value, out parsed) && Enum.IsDefined(parsed);
    }

    private static object ToDto(SeasonPostersConfiguration settings) => new
    {
        settings.AutoApply,
        settings.PosterLanguages,
        settings.ShowPosterLanguages,
        ShowPosterText = settings.ShowPosterText.ToString(),
        Lookalikes = settings.Lookalikes.ToString(),
        settings.LogoLanguages,
        settings.MinimumSeasonSharePercent,
        SplitSeasons = settings.SplitSeasons.ToString(),
        ShowPoster = settings.ShowPoster.ToString(),
        SpecialsPoster = settings.SpecialsPoster.ToString(),
        settings.JellyfinUrl,
        JellyfinApiKeySet = !string.IsNullOrWhiteSpace(settings.JellyfinApiKey),
    };

    private ContentResult JsonResult<T>(T value)
        => Content(JsonSerializer.Serialize(value, Json), "application/json");

    private object ToDto(LibraryPlan plan)
    {
        var applied = posters.AppliedShowPosters();
        return new
        {
            plan.CreatedAt,
            Shows = plan.Shows.Select(show => ToDto(show, applied.GetValueOrDefault(show.Key))),
        };
    }

    private static object ToDto(ShowPlan show, (Guid? Show, Guid? Specials) applied)
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
            // A locked show poster is the show's own whatever the mode.
            ShowPosterImageId = show.ShowMode is ShowPosterMode.JellyfinUpload || show.ShowPoster?.IsPinned == true ? show.JellyfinPosterImageId : main?.ImageId,
            ShowPosterSource = show.ShowMode is ShowPosterMode.JellyfinUpload || show.ShowPoster?.IsPinned == true
                ? show.ShowPoster?.Source
                : main is null ? null : $"Shared with {main.Title}",
            ShowPosterPinned = show.ShowPoster?.IsPinned == true,
            ShowPosterNeedsReview = show.ShowPoster?.NeedsReview == true,
            ShowPosterLooksLike = (show.ShowPoster?.LooksLike ?? []).Select(ToDto),
            ShowPosterPending = show.JellyfinPosterImageId is { } planned && planned != applied.Show,
            show.IsSiloOnly,
            show.HasSpecials,
            SpecialsMode = show.SpecialsMode.ToString(),
            show.SpecialsPosterImageId,
            SpecialsPosterSource = show.SpecialsPoster?.Source,
            SpecialsPosterPinned = show.SpecialsPoster?.IsPinned == true,
            SpecialsPosterLooksLike = (show.SpecialsPoster?.LooksLike ?? []).Select(ToDto),
            SpecialsPosterPending = show.SpecialsPosterImageId is { } plannedSpecials && plannedSpecials != applied.Specials,
            SpecialsPosterAlternatives = (show.SpecialsPosterAlternatives ?? []).Select(ToDto),
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
