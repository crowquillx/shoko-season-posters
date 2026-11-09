using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Config;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters;

[Display(Name = "Season Posters")]
public class SeasonPostersConfiguration : IPluginConfiguration
{
    [Display(Name = "Apply automatically", Description = "Off: scans only plan changes, and nothing changes until you press Apply on the review page. On: scans apply their plan.")]
    [DefaultValue(false)]
    public bool AutoApply { get; set; }

    [Display(Name = "Poster languages", Description = "Comma-separated language codes, best first. \"none\" means textless or unknown.")]
    [DefaultValue("en,none,ja")]
    public string PosterLanguages { get; set; } = "en,none,ja";

    [Display(Name = "Logo languages", Description = "Comma-separated language codes, best first. \"none\" means textless or unknown. Each series gets its best logo in these languages as its preferred logo, unless you picked one in Shoko. A series with no logo in these languages keeps its logo. Leave empty to leave logos alone.")]
    [DefaultValue("en")]
    public string LogoLanguages { get; set; } = "en";

    [Display(Name = "Season match threshold (%)", Description = "An entry matches a TMDB season when at least this share of its linked episodes point to it. Otherwise it is flagged for review.")]
    [Range(51, 100)]
    [DefaultValue(60)]
    public int MinimumSeasonSharePercent { get; set; } = 60;

    [Display(Name = "Split seasons", Description = "What later parts use when they share a TMDB season or movie with an earlier part. They are flagged for review either way, and each show can override this.")]
    [DefaultValue(SplitSeasonPolicy.AniDbPoster)]
    public SplitSeasonPolicy SplitSeasons { get; set; } = SplitSeasonPolicy.AniDbPoster;

    [Display(Name = "Show poster", Description = "How the show poster relates to its main season. Each show can override this.")]
    [DefaultValue(ShowPosterMode.JellyfinUpload)]
    public ShowPosterMode ShowPoster { get; set; } = ShowPosterMode.JellyfinUpload;

    [Display(Name = "Jellyfin URL", Description = "Only needed to upload own show posters to Jellyfin, for example http://jellyfin:8096. Silo reads them from Shoko instead.")]
    public string? JellyfinUrl { get; set; }

    [Display(Name = "Jellyfin API key", Description = "Only needed to upload own show posters to Jellyfin.")]
    [DataType(DataType.Password)]
    public string? JellyfinApiKey { get; set; }
}
