using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Plugin;
using Shoko.SeasonPosters.Library;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Imaging;

/// <summary>
/// Makes and keeps poster fingerprints. A downloaded poster is read from disk. Any other poster is read from a small
/// thumbnail at its source, such as TMDB, so checking a candidate never downloads it into Shoko.
/// </summary>
/// <remarks>
/// Fingerprints are kept by uniqueness key in <c>fingerprints.json</c> beside the state file. The file can be deleted
/// at any time; the fingerprints are made again as they are needed.
/// </remarks>
public sealed class FingerprintService
{
    public const string HttpClientName = "season-posters-fingerprints";

    /// <summary>Raise this when the fingerprint format changes, so stored fingerprints are made again.</summary>
    private const int FormatVersion = 1;
    private const int Parallelism = 4;
    private const long MaxDownloadBytes = 20 * 1024 * 1024;

    private readonly IImageManager _images;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<FingerprintService> _logger;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _failed = [];
    private Dictionary<Guid, PosterFingerprint>? _known;
    private volatile bool _decoderMissing;

    public FingerprintService(IImageManager images, IHttpClientFactory http, PluginPaths<Plugin> paths, ILogger<FingerprintService> logger)
        : this(images, http, Path.Combine(paths.DatabasePath, "fingerprints.json"), logger)
    {
    }

    internal FingerprintService(IImageManager images, IHttpClientFactory http, string path, ILogger<FingerprintService> logger)
    {
        _images = images;
        _http = http;
        _path = path;
        _logger = logger;
    }

    /// <summary>False once decoding has failed for want of Magick.NET, after which no more fingerprints are made.</summary>
    public bool CanDecode => !_decoderMissing;

    public PosterFingerprint? Find(Guid uniquenessKey)
    {
        lock (_lock)
            return Known().GetValueOrDefault(uniquenessKey);
    }

    /// <summary>
    /// Fingerprints the posters given as uniqueness key to image ID, skipping ones already known or that failed
    /// before. Returns how many were added.
    /// </summary>
    public async Task<int> AddAsync(IReadOnlyDictionary<Guid, Guid> wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (_decoderMissing)
            return 0;

        KeyValuePair<Guid, Guid>[] todo;
        lock (_lock)
        {
            var known = Known();
            todo = wanted.Where(pair => !known.ContainsKey(pair.Key) && !_failed.Contains(pair.Key)).ToArray();
        }

        if (todo.Length == 0)
            return 0;

        var added = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(todo, options, async (pair, token) =>
        {
            var fingerprint = await ReadAsync(pair.Value, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (fingerprint is null)
                {
                    _failed.Add(pair.Key);
                }
                else
                {
                    Known()[pair.Key] = fingerprint;
                    added++;
                }
            }
        }).ConfigureAwait(false);

        if (added > 0)
        {
            lock (_lock)
                Save();
            _logger.LogDebug("Season Posters fingerprinted {Added} of {Wanted} posters.", added, todo.Length);
        }

        return added;
    }

    private async Task<PosterFingerprint?> ReadAsync(Guid imageId, CancellationToken cancellationToken)
    {
        try
        {
            var image = _images.GetImageByID(imageId);
            if (image is null)
                return null;

            var data = image.IsAvailable ? await ReadLocalAsync(image, cancellationToken).ConfigureAwait(false) : null;
            if (data is null && RemoteImages.Url(_images, image, "w185") is { } url)
                data = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);

            return data is not null && Decode(data) is { } pixels ? new PosterFingerprint(pixels) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One unreadable poster should not stop a scan; it only goes unchecked.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogDebug(exception, "Season Posters could not fingerprint image {ImageId}.", imageId);
            return null;
        }
    }

    private byte[]? Decode(byte[] data)
    {
        try
        {
            return FingerprintDecoder.Decode(data);
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or TypeLoadException or TypeInitializationException
            or MissingMethodException or MissingFieldException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (!_decoderMissing)
            {
                _decoderMissing = true;
                _logger.LogWarning(exception, "Season Posters could not load Magick.NET from the server, so lookalike posters will not be detected.");
            }

            return null;
        }
    }

    private static async Task<byte[]?> ReadLocalAsync(IImage image, CancellationToken cancellationToken)
    {
        var stream = image.GetStream();
        if (stream is null)
            return null;

        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient(HttpClientName);
        using var response = await client.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
            return null;

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<Guid, PosterFingerprint> Known()
        => _known ??= Load();

    private Dictionary<Guid, PosterFingerprint> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];

            var stored = JsonSerializer.Deserialize<StoredFingerprints>(File.ReadAllText(_path));
            if (stored?.Version != FormatVersion || stored.Fingerprints is null)
                return [];

            var known = new Dictionary<Guid, PosterFingerprint>();
            foreach (var (key, value) in stored.Fingerprints)
            {
                if (PosterFingerprint.FromBase64(value) is { } fingerprint)
                    known[key] = fingerprint;
            }

            return known;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Season Posters could not read its stored fingerprints, so it will make them again.");
            return [];
        }
    }

    private void Save()
    {
        try
        {
            var stored = new StoredFingerprints
            {
                Version = FormatVersion,
                Fingerprints = Known().ToDictionary(pair => pair.Key, pair => pair.Value.ToBase64()),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Season Posters could not store its fingerprints. They stay in memory until a restart.");
        }
    }

    private sealed class StoredFingerprints
    {
        public int Version { get; set; }

        public Dictionary<Guid, string>? Fingerprints { get; set; }
    }
}
