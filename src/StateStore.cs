using System.Text.Json;
using System.Text.Json.Serialization;
using Shoko.Abstractions.Plugin;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters;

public sealed class ShowSettings
{
    public SplitSeasonPolicy? SplitPolicy { get; set; }

    public ShowPosterMode? ShowMode { get; set; }

    public Guid? PinnedShowPosterImageId { get; set; }
}

public sealed class JellyfinUpload
{
    public required string ItemId { get; set; }

    public required Guid ImageId { get; set; }

    /// <summary>Jellyfin's image tag straight after the upload; a different tag later means the poster was replaced.</summary>
    public required string Tag { get; set; }
}

public sealed class PluginState
{
    /// <summary>The poster this plugin last made preferred, per series. Anything else preferred was set by someone else.</summary>
    public Dictionary<int, Guid> Assigned { get; set; } = [];

    /// <summary>Choices made on the review page, per series.</summary>
    public Dictionary<int, Guid> Pins { get; set; } = [];

    /// <summary>Overrides per show key.</summary>
    public Dictionary<string, ShowSettings> Shows { get; set; } = [];

    /// <summary>Posters uploaded straight to Jellyfin, per show key.</summary>
    public Dictionary<string, JellyfinUpload> Uploads { get; set; } = [];
}

/// <summary>Keeps the state file in the plugin's data folder, which the server includes in its own backups.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly Lock _lock = new();
    private readonly string _path;
    private PluginState? _state;

    public StateStore(PluginPaths<Plugin> paths)
        : this(Path.Combine(paths.DatabasePath, "state.json"))
    {
    }

    internal StateStore(string path) => _path = path;

    /// <summary>Reads or changes the state under a lock, saving afterwards when <paramref name="change"/> says something changed.</summary>
    public T Update<T>(Func<PluginState, (T Result, bool Changed)> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var state = _state ??= Load();
            var (result, changed) = change(state);
            if (changed)
                Save(state);
            return result;
        }
    }

    public T Read<T>(Func<PluginState, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_lock)
            return read(_state ??= Load());
    }

    private PluginState Load()
    {
        if (!File.Exists(_path))
            return new PluginState();

        return JsonSerializer.Deserialize<PluginState>(File.ReadAllText(_path), Options) ?? new PluginState();
    }

    private void Save(PluginState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        File.Move(temporary, _path, overwrite: true);
    }
}
