using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Shoko.SeasonPosters.Web;

/// <summary>Serves the review page's static files, which hold no data. The data comes from the admin-only API.</summary>
[ApiController]
[AllowAnonymous]
[Route("plugin/season-posters")]
public class PageController : ControllerBase
{
    [HttpGet("")]
    public ActionResult Index() => Resource("ui.index.html", "text/html; charset=utf-8");

    [HttpGet("app.css")]
    public ActionResult Css() => Resource("ui.app.css", "text/css; charset=utf-8");

    [HttpGet("app.js")]
    public ActionResult Script() => Resource("ui.app.js", "text/javascript; charset=utf-8");

    private ActionResult Resource(string name, string contentType)
    {
        var stream = typeof(PageController).Assembly.GetManifestResourceStream(name);
        return stream is null ? NotFound() : File(stream, contentType);
    }
}
