using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters;

public sealed record LogoResult(int Changed, int Failed, int Kept);

/// <summary>
/// Gives each series its best logo in the configured languages as its preferred logo. Shoko otherwise uses the
/// logo its source pins, which for anime from TMDB is usually the Japanese one. Shokofin and the Silo ShokoAnime
/// plugin both show a series' preferred logo, and a group shows its main series' logo.
/// </summary>
public sealed class LogoService(
    IMetadataService metadata,
    IImageManager images,
    StateStore store,
    ConfigurationProvider<SeasonPostersConfiguration> configuration,
    ILogger<LogoService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LogoResult> ApplyAsync(CancellationToken cancellationToken)
    {
        var languages = PosterRanking.ParseLanguages(configuration.Load().LogoLanguages);
        if (languages.Count == 0)
            return new LogoResult(0, 0, 0);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = 0;
            var failed = 0;
            var kept = 0;
            foreach (var series in metadata.GetAllShokoSeries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await ApplyAsync(series, languages).ConfigureAwait(false);
                switch (outcome)
                {
                    case true:
                        changed++;
                        break;
                    case false:
                        failed++;
                        break;
                    default:
                        kept++;
                        break;
                }
            }

            if (changed > 0 || failed > 0)
                logger.LogInformation("Season Posters set {Changed} logos ({Failed} failed, {Kept} kept).", changed, failed, kept);
            return new LogoResult(changed, failed, kept);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns><c>true</c> when the logo changed, <c>false</c> when it could not, and <c>null</c> when it stays.</returns>
    private async Task<bool?> ApplyAsync(IShokoSeries series, IReadOnlyList<string> languages)
    {
        var withImages = (IWithImages)series;
        var candidates = withImages.GetImages(new() { ImageType = ImageEntityType.Logo, IsEnabled = true })
            .DistinctBy(image => image.ID)
            .Select(image => new LogoCandidate(image.ID, image.LanguageCode, image.IsAvailable))
            .ToArray();
        var best = LogoChooser.Choose(candidates, languages);
        var current = withImages.GetImages(new() { ImageType = ImageEntityType.Logo, IsPreferred = true, LinkedEntityImages = false });
        var assigned = store.Read(state => state.AssignedLogos.TryGetValue(series.LocalID, out var id) ? id : (Guid?)null);
        var decision = LogoChooser.Decide(current.Count > 0 ? current[0].ID : null, assigned, best);
        if (decision.Action is LogoAction.Keep)
            return null;

        var image = images.GetImageByID(decision.ImageId!.Value);
        if (image is null)
            return false;
        if (!image.IsAvailable && !await images.DownloadImage(image).ConfigureAwait(false))
        {
            logger.LogWarning("Season Posters could not download logo {ImageId} for series {SeriesId}.", image.ID, series.LocalID);
            return false;
        }

        images.SetPreferredImageForEntity(series, ImageEntityType.Logo, image);
        store.Update(state =>
        {
            state.AssignedLogos[series.LocalID] = image.ID;
            return (true, true);
        });
        return true;
    }
}
