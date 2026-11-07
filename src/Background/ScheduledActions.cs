using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.ScheduledActions;

namespace Shoko.SeasonPosters.Background;

/// <summary>Picks up what no event covers, such as new TMDB posters.</summary>
public class ScanPostersAction(PosterService posters) : IScheduledAction
{
    public string Name => "Scan Season Posters";

    public string? Description => "Plans posters for every show, and applies the plan when automatic apply is on.";

    public ActionCategory Category => ActionCategory.PluginInferred;

    public IReadOnlyList<ActionTrigger> DefaultTriggers => [ActionTrigger.Every(TimeSpan.FromHours(6))];

    public Task Execute(IProgress<decimal> progress, CancellationToken token)
        => posters.RunAutomaticPassAsync(token);
}

/// <summary>Puts back uploaded show posters after a Shokofin refresh replaces them.</summary>
public class RestoreShowPostersAction(PosterService posters, ILogger<RestoreShowPostersAction> logger) : IScheduledAction
{
    public string Name => "Restore Jellyfin Show Posters";

    public string? Description => "Uploads a show poster again when a Jellyfin refresh has replaced it.";

    public ActionCategory Category => ActionCategory.PluginInferred;

    public IReadOnlyList<ActionTrigger> DefaultTriggers => [ActionTrigger.Every(TimeSpan.FromMinutes(15))];

    public async Task Execute(IProgress<decimal> progress, CancellationToken token)
    {
        var (uploaded, failed) = await posters.ReconcileJellyfinAsync(token).ConfigureAwait(false);
        if (uploaded > 0 || failed > 0)
            logger.LogInformation("Season Posters restored {Uploaded} Jellyfin show posters ({Failed} failed).", uploaded, failed);
    }
}
