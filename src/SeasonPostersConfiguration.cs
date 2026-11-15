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

    [Display(Name = "Show poster languages", Description = "Comma-separated language codes for the show's own poster, best first. Put \"none\" first for a textless show poster while the seasons keep their titles. Leave empty to use the poster languages.")]
    [DefaultValue("")]
    public string ShowPosterLanguages { get; set; } = string.Empty;

    [Display(Name = "Show poster text", Description = "TextlessFirst gives the show's own poster a textless poster whenever there is one, while the seasons keep their titles. Textless artwork unlike every season's comes first, then a textless version of a season's artwork. FollowLanguages uses the show poster languages. Each show can override this.")]
    [DefaultValue(ShowPosterText.FollowLanguages)]
    public ShowPosterText ShowPosterText { get; set; } = ShowPosterText.FollowLanguages;

    [Display(Name = "Specials poster", Description = "OwnPoster gives each show's specials season a poster unlike its other posters: TMDB's specials poster first, then an unused season, movie or show poster. LeaveAlone keeps the main season's poster, which clients show by default. Each show can override this.")]
    [DefaultValue(SpecialsPosterMode.OwnPoster)]
    public SpecialsPosterMode SpecialsPoster { get; set; } = SpecialsPosterMode.OwnPoster;

    [Display(Name = "Avoid lookalike posters", Description = "TMDB often holds the same artwork several times: in each language, textless, or cropped differently. AvoidSameArtwork keeps those apart within a show and uses another version only when nothing else is left. AvoidSameImageOnly allows other versions, such as a textless show poster of the first season's artwork. Off only avoids the same image.")]
    [DefaultValue(LookalikePolicy.AvoidSameArtwork)]
    public LookalikePolicy Lookalikes { get; set; } = LookalikePolicy.AvoidSameArtwork;

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
