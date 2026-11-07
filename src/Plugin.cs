using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.SeasonPosters.Background;
using Shoko.SeasonPosters.Jellyfin;
using Shoko.SeasonPosters.Library;

namespace Shoko.SeasonPosters;

/// <summary>The build generates the rest of this class (ID, name and description) from the project file.</summary>
public partial class Plugin : IPlugin<SeasonPostersConfiguration>, IPluginServiceRegistration
{
    public const string Namespace = "season-posters";

    public IReadOnlyList<PluginPage> GetPages()
        => [new PluginPage { Name = "Poster review", Url = $"/plugin/{Namespace}/" }];

    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddHttpClient<JellyfinClient>();
        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddSingleton<ShokoLibrary>();
        serviceCollection.AddSingleton<JellyfinShowPosters>();
        serviceCollection.AddSingleton<PosterService>();
        serviceCollection.AddSingleton<ChangeWatcher>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<ChangeWatcher>());
    }
}
