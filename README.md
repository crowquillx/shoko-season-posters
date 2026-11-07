# Season Posters

A [Shoko Server](https://shokoanime.com) plugin that gives every season in a Jellyfin show its own accurate poster when you use [Shokofin](https://github.com/ShokoAnime/Shokofin)'s group structure.

Without it, seasons in a group usually end up on low-resolution AniDB posters or all share one TMDB show poster. Shokofin picks from a merged image list without knowing which poster came from the show and which from the season. This plugin works that out in Shoko and marks the right poster as preferred, which Shokofin already respects. Neither Shoko nor Shokofin needs changing.

Daily Shoko only. It uses the 6.0 plugin API, which stable 5.3 does not have. Shokofin must be a dev build, as Shokofin requires with a daily server.

## Status

Early, tested against one real library on Shoko `v6.0.0-dev.588`.

- **Verified:** loading, planning, the review page and the Shoko side of applying. On 834 shows and 1,340 entries, no poster was used twice in a show. Applying a show set its season posters as preferred and downloaded them.
- **Not yet verified:** posters reaching Jellyfin through Shokofin, and the Jellyfin show poster upload and restore. The upload code has only been tested against a simulated Jellyfin.

## What it picks

| The entry is | It gets |
|---|---|
| The main entry for a TMDB season | That season's best poster |
| A movie, including one that appears as a season in a group | Its TMDB movie poster |
| A later part sharing a TMDB season or movie | Your [split season](#split-seasons) choice, flagged for review |
| Linked to several TMDB movies, spread across TMDB seasons, or only linked to TMDB specials | Its AniDB poster, flagged for review |
| Not linked to TMDB, or its TMDB data has not downloaded yet | Its AniDB poster, flagged for review |

A TV entry owns a shared TMDB season over an OVA or special, even one that aired first. Among entries of the same kind, the earliest owns it.

Posters are ranked by shape, then your language order, then rating, votes and width. No poster is used twice in a show.

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
| Season match threshold | 60% | The share of an entry's linked episodes that must point at one TMDB season. Below this it is flagged. |
| Split seasons | AniDB poster | See [below](#split-seasons). |
| Show poster | Upload to Jellyfin | See [below](#show-posters). |
| Jellyfin URL and API key | Empty | Only needed to upload show posters to Jellyfin. |

The split season and show poster settings can also be changed per show on the review page.

### Split seasons

When several AniDB entries share one TMDB season, TMDB can't say which poster belongs to which part. Later parts are flagged whichever you choose.

- **AniDB poster:** accurate, but low resolution.
- **Next TMDB poster:** sharp, but it may show the wrong part. Falls back to AniDB when posters run out.
- **Leave for me:** changes nothing until you choose on the review page.

### Show posters

Shokofin reads the show poster and its main season's poster from the same Shoko series, so Shoko alone can't make them differ.

- **Upload to Jellyfin:** the season keeps its season poster, and the TMDB show poster goes straight to the Jellyfin show. This needs the Jellyfin URL and an API key.
- **TMDB show poster on main season:** the show and that season share the show poster.
- **Same as main season:** the show and that season share the season poster.

A Shokofin refresh replaces every image on an item, including an uploaded show poster. The **Restore Jellyfin Show Posters** action checks every 15 minutes and uploads it again, so a show can briefly lose its poster until the next check.

## Review page

The page opens on the entries that need review, and you can filter by reason or search by title. For each entry you can:

- **Keep this poster**, which clears the flag.
- **Choose another poster**, including ones Shoko hasn't downloaded. Those are downloaded when chosen, and their thumbnails load from the source, such as TMDB.
- **Upload your own.**
- **Return to automatic**, which also hands over a poster that was set outside this plugin.

A preferred poster set outside this plugin is never replaced unless you choose that here. Choices are kept in `state.json` in the plugin's data folder, which Shoko includes in its backups.

## Scheduled actions

Both appear in Shoko's scheduled actions, where you can change when they run.

| Action | Default | What it does |
|---|---|---|
| Scan Season Posters | Every 6 hours | Updates the plan, and applies it when automatic apply is on. |
| Restore Jellyfin Show Posters | Every 15 minutes | Uploads a show poster again if a refresh replaced it. |

Changes to TMDB links or groups also start a scan after two quiet minutes.

## Known limits

- Two TMDB entries can hold the same artwork as separate images, for example a show and its only season. These are compared by image ID, so that isn't detected. Choose another poster on the review page if it bothers you.
- Only direct-to-Jellyfin uploads can give a show a different poster from its main season.
- The show poster mode that sets the main season assumes Shokofin's default season ordering, where the Shoko main series is the show's default season.
- TMDB is the only high-resolution source so far.

## Development

You need the .NET 10 SDK.

```sh
dotnet test Shoko.SeasonPosters.slnx -c Release
```

The poster decisions are pure functions in `src/Planning`, so most behaviour can be tested without a server. `src/Library` reads from Shoko, `src/Jellyfin` handles uploads, `src/Web` holds the API and review page, and `src/Background` holds the triggers.

### Staying current with daily Shoko

The plugin API moves with Shoko's daily builds, and a plugin built against a newer API than the server has will not load, so keep your server up to date.

- **`follow-shoko-daily.yml`** runs daily and moves `Directory.Build.props` to the newest packages. If the build and tests pass, it commits the bump and publishes. If they fail, it opens an issue and the feed stays on the last working version.
- **`publish.yml`** builds with `shoko-build`, creates a prerelease and updates `manifest.json` on the `metadata` branch. The feed lives there because Shoko needs a text content type, which raw GitHub files have and release assets do not. It can also be run by hand.
- **`ci.yml`** runs the tests on pushes and pull requests.

Passing tests show the plugin compiles and its logic holds against the new API. They don't prove it runs correctly on the new server.
