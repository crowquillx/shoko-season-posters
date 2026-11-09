using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata.Services;

namespace Shoko.SeasonPosters.Background;

/// <summary>Runs an automatic pass shortly after TMDB links or groups change, grouping bursts of changes into one pass.</summary>
public sealed class ChangeWatcher(
    IMetadataLinkingService linking,
    IShokoGroupManager groups,
    ISystemService system,
    PosterService posters,
    LogoService logos,
    ILogger<ChangeWatcher> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(2);
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => RunPass());
        linking.LinksChanged += OnChanged;
        groups.SeriesMoved += OnChanged;
        groups.GroupsRecreated += OnChanged;
        system.Started += OnChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        linking.LinksChanged -= OnChanged;
        groups.SeriesMoved -= OnChanged;
        groups.GroupsRecreated -= OnChanged;
        system.Started -= OnChanged;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }

    private void OnChanged<T>(object? sender, T args)
        => _timer?.Change(QuietPeriod, Timeout.InfiniteTimeSpan);

    private async void RunPass()
    {
        try
        {
            await posters.RunAutomaticPassAsync(_stopping.Token).ConfigureAwait(false);
            await logos.ApplyAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A timer callback has nowhere to send an exception, and the next pass can succeed.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogError(exception, "Season Posters automatic pass failed.");
        }
    }
}
