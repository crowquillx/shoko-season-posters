# Season Posters

A [Shoko Server](https://shokoanime.com) plugin that gives every season in a Jellyfin show its own accurate poster when you use [Shokofin](https://github.com/ShokoAnime/Shokofin)'s group structure.

Without it, seasons in a group usually end up on low-resolution AniDB posters or all share one TMDB show poster. Shokofin picks from a merged image list without knowing which poster came from the show and which from the season. This plugin works that out in Shoko and marks the right poster as preferred, which Shokofin already respects. Neither Shoko nor Shokofin needs changing.

Daily Shoko only. It uses the 6.0 plugin API, which stable 5.3 does not have. Shokofin must be a dev build, as Shokofin requires with a daily server.

## Status

Early, tested against one real library on Shoko `v6.0.0-dev.588`.

- **Verified:** loading, planning, the review page and the Shoko side of applying. On 834 shows and 1,340 entries, no poster was used twice in a show. Applying a show set its season posters as preferred and downloaded them.
- **Verified on `v6.0.0-dev.591` with Shokofin and Jellyfin `12.4.0`:** a show poster upload to Jellyfin, and its restore after the poster was replaced.
- **Not yet verified:** season posters reaching Jellyfin through a Shokofin refresh, and show posters reaching Silo through the [Silo ShokoAnime plugin](https://github.com/crowquillx/silo-shoko-plugin).

## What it picks

| The entry is | It gets |
|---|---|
| The main entry for a TMDB season | That season's best poster |
| A movie, including one that appears as a season in a group | Its TMDB movie poster |
| A later part sharing a TMDB season or movie | Your [split season](#split-seasons) choice, flagged for review |
| Linked to several TMDB movies, spread across TMDB seasons, or only linked to TMDB specials | Its AniDB poster, flagged for review |
| Not linked to TMDB, or its TMDB data has not downloaded yet | Its AniDB poster, flagged for review |

A TV entry owns a shared TMDB season over an OVA or special, even one that aired first. Among entries of the same kind, the earliest owns it.

Posters are ranked by shape, then your language order, then rating, votes and width. No poster is used twice in a show, and neither is its [artwork](#lookalike-posters) if anything else will do.

AniDB is the fallback because its poster is the only one known to belong to that exact entry. Once TMDB posters arrive, the next apply replaces a fallback automatically.

## Installing

Add this repository in Settings → Plugins → Repositories, install **Season Posters** and restart Shoko:

```
https://raw.githubusercontent.com/crowquillx/shoko-season-posters/metadata/manifest.json
```

Then open the **Poster review** page and press **Apply all** when the plan looks right.

Nothing changes until you press Apply, unless you turn on **Apply automatically**. Scans only update the plan.

## Settings

These are on the plugin's settings page.

| Setting | Default | What it does |
|---|---|---|
| Apply automatically | Off | Off: scans only plan. On: scans and detected changes apply their plan. |
| Poster languages | `en,none,ja` | Language order, best first. `none` means textless or unknown. |
| Show poster languages | Empty | Language order for the show's own poster. Empty uses the poster languages. |
| Show poster text | Follow languages | **Textless first** gives the show a textless poster while seasons keep their titles. See [below](#show-posters). |
| Avoid lookalike posters | Avoid same artwork | See [below](#lookalike-posters). |
| Logo languages | `en` | Language order for [logos](#logos), best first. Empty leaves logos alone. |
| Season match threshold | 60% | The share of an entry's linked episodes that must point at one TMDB season. Below this it is flagged. |
| Split seasons | AniDB poster | See [below](#split-seasons). |
| Show poster | Own show poster | See [below](#show-posters). |
| Jellyfin URL and API key | Empty | Only needed to upload own show posters to Jellyfin. |

The split season, show poster and show poster text settings can also be changed per show on the review page.

### Split seasons

When several AniDB entries share one TMDB season, TMDB can't say which poster belongs to which part. Later parts are flagged whichever you choose.

- **AniDB poster:** accurate, but low resolution.
- **Next TMDB poster:** sharp, but it may show the wrong part. Falls back to AniDB when posters run out.
- **Leave for me:** changes nothing until you choose on the review page.

### Show posters

Shokofin reads the show poster and its main season's poster from the same Shoko series, so Shoko alone can't make them differ. A Shoko group's own images are also just its main series' poster.

- **Own show poster:** the season keeps its season poster, and the show gets a poster of its own. It is the best TMDB show poster whose artwork no season uses, or else an unused season or movie poster with different artwork. Only when there is none does it use another version of a season's artwork, and a show poster that looks the same as a season's is flagged. Applying a show downloads that poster and lists it at `GET /api/plugin/season-posters/show-posters`, which any signed-in user can read. The [Silo ShokoAnime plugin](https://github.com/crowquillx/silo-shoko-plugin) uses that list for its show posters. For Jellyfin, the poster is uploaded straight to the show, which needs the Jellyfin URL and an API key.
- **TMDB show poster on main season:** the show and that season share the show poster.
- **Same as main season:** the show and that season share the season poster.

With **Textless first**, the show poster is textless whenever a textless show, season or movie poster exists. Textless artwork that no season uses comes first, then a textless version of a season's artwork, such as the first season's key art without its title. A textless poster that looks the same as a season's is skipped. Without a textless poster, the order above applies.

A Shokofin refresh replaces every image on an item, including an uploaded show poster. The **Restore Jellyfin Show Posters** action checks every 15 minutes and uploads it again, so a show can briefly lose its poster until the next check.

### Lookalike posters

TMDB often holds one artwork many times over: in each language, textless, or cropped differently. Each is a separate image, so a show poster can look exactly like its first season's. The plugin compares a tiny grayscale copy of each poster it considers, which tolerates different text, crops and zoom.

- **Avoid same artwork:** a season or show takes different artwork when there is some, and another version of artwork already in use only when nothing else is left. A later part of a [split season](#split-seasons) never takes another version of an earlier part's artwork.
- **Avoid same image only:** only posters that look the same are kept apart, so a textless version counts as different.
- **Off:** only the image itself counts, as before.

A poster Shoko has not downloaded is compared using a small thumbnail from its source, so checking never downloads it into Shoko. The copies are kept in `fingerprints.json` in the plugin's data folder, which can be deleted at any time. The first scan checks thousands of posters and can take a few minutes; later scans only check new ones. The comparison uses the Magick.NET library that Shoko ships. If it can't be loaded, the plugin logs a warning and only avoids the same image.

### Logos

For a series' logo, Shoko uses the logo its source pins, whatever its language. For anime from TMDB that is usually the Japanese logo, even when English ones are downloaded. Each series without a logo you picked in Shoko gets its best logo in the logo languages as its preferred logo:
- Languages come in the setting's order, then logos Shoko has already downloaded, then Shoko's own order.
- A series with no logo in these languages keeps the one it has.
- A logo this plugin set is replaced if a better one appears, or if you change the languages.

Shokofin and the Silo ShokoAnime plugin show a series' preferred logo, and a group shows its main series' logo. Logos are set on every scan and after changes, whatever **Apply automatically** says, and **Apply logos** on the review page sets them straight away. Jellyfin and Silo show a new logo once the item is refreshed.

## Review page

The page opens on the entries that need review, and you can filter by reason or search by title. For each entry you can:

- **Keep this poster**, which clears the flag.
- **Choose another poster**, including ones Shoko hasn't downloaded. Those are downloaded when chosen, and their thumbnails load from the source, such as TMDB. The chooser filters by language, including textless, and by source, and marks posters that look like one already used in the show. Clicking a poster opens a larger preview, and **Use this poster** chooses it.
- **Upload your own.**
- **Return to automatic**, which also hands over a poster that was set outside this plugin.

Each show starts with a preview of its show poster beside its seasons' posters, as a client lines them up, and lookalikes are outlined. With **Own show poster**, the show poster can be chosen the same way, uploaded, or returned to automatic, and it is applied with the show. The reason filter can also find shows with lookalike posters.

**Apply all** applies every pending change after a second click, and the summary line then reports how many posters were set and uploaded and how many failed. Shoko shows the page in a sandboxed frame that blocks browser dialogs, so the page never uses them.

A preferred poster set outside this plugin is never replaced unless you choose that here. Choices are kept in `state.json` in the plugin's data folder, which Shoko includes in its backups.

## Scheduled actions

Both appear in Shoko's scheduled actions, where you can change when they run.

| Action | Default | What it does |
|---|---|---|
| Scan Season Posters | Every 6 hours | Updates the plan, and applies it when automatic apply is on. Sets [logos](#logos). |
| Restore Jellyfin Show Posters | Every 15 minutes | Uploads a show poster again if a refresh replaced it. |

Changes to TMDB links or groups also start a scan after two quiet minutes.

## Known limits

- Lookalike detection judges overall composition. Two different artworks with nearly the same layout and colours could be taken for one, and a heavily reworked version of one artwork can pass as different.
- Only the own show poster mode can give a show a different poster from its main season, and only Jellyfin and clients that read the show poster list see it. Silo shows the new poster once that show's metadata is refreshed in Silo.
- The show poster mode that sets the main season assumes Shokofin's default season ordering, where the Shoko main series is the show's default season.
- TMDB is the only high-resolution source so far.

## Development

You need the .NET 10 SDK.

```sh
dotnet test Shoko.SeasonPosters.slnx -c Release
```

The poster decisions are pure functions in `src/Planning`, so most behaviour can be tested without a server. `src/Library` reads from Shoko, `src/Imaging` makes poster fingerprints, `src/Jellyfin` handles uploads, `src/Web` holds the API and review page, and `src/Background` holds the triggers.

### Staying current with daily Shoko

The plugin API moves with Shoko's daily builds, and a plugin built against a newer API than the server has will not load, so keep your server up to date.

- **`follow-shoko-daily.yml`** runs daily and moves `Directory.Build.props` to the newest packages. If the build and tests pass, it commits the bump and publishes. If they fail, it opens an issue and the feed stays on the last working version.
- **`publish.yml`** builds with `shoko-build`, creates a prerelease and updates `manifest.json` on the `metadata` branch. The feed lives there because Shoko needs a text content type, which raw GitHub files have and release assets do not. It can also be run by hand.
- **`ci.yml`** runs the tests on pushes and pull requests.

Passing tests show the plugin compiles and its logic holds against the new API. They don't prove it runs correctly on the new server.
