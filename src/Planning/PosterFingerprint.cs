namespace Shoko.SeasonPosters.Planning;

/// <summary>How much one poster looks like another.</summary>
public enum Likeness
{
    Different,

    /// <summary>The same artwork with different text, language, crop or zoom, such as a textless version.</summary>
    SameArtwork,

    /// <summary>Looks the same at a glance: the same image uploaded twice, re-encoded or resized.</summary>
    SameImage,
}

/// <summary>
/// A tiny grayscale copy of a poster, used to spot the same artwork uploaded as separate images. TMDB often holds a
/// show's key art several times over, in each language and textless, and those images share nothing but their pixels.
/// </summary>
/// <remarks>
/// The thresholds were tuned on real TMDB posters: every same-artwork pair in the sample scored below 0.34, and the
/// closest pair of different artworks, character posters on matching white backgrounds, scored 0.45. An exact
/// re-encode scores about 0.01.
/// </remarks>
public sealed class PosterFingerprint
{
    public const int Width = 24;
    public const int Height = 36;

    private const int GridWidth = 16;
    private const int GridHeight = 24;
    private const int Bands = 12;
    private const int RowsPerBand = GridHeight / Bands;

    /// <summary>A title or logo usually fills a horizontal band, so the worst bands are left out when comparing artwork.</summary>
    private const int IgnoredBands = 2;
    private const double SameArtworkBelow = 0.40;
    private const double SameImageBelow = 0.12;

    /// <summary>Zooms and offsets tried, since uploads of one artwork are often cropped differently.</summary>
    private static readonly double[] Zooms = [0.9, 0.8];
    private static readonly double[] Offsets = [0, 0.5, 1];

    private readonly Lazy<float[][]> _windows;

    public PosterFingerprint(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != Width * Height)
            throw new ArgumentException($"A fingerprint has {Width * Height} pixels.", nameof(pixels));

        Pixels = pixels;
        _windows = new Lazy<float[][]>(BuildWindows);
    }

    /// <summary>Grayscale values, row by row.</summary>
    public byte[] Pixels { get; }

    private float[] Whole => _windows.Value[0];

    public static Likeness Compare(PosterFingerprint first, PosterFingerprint second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (Distance(first.Whole, second.Whole, 0) < SameImageBelow)
            return Likeness.SameImage;

        return ResemblesWithin(first, second) || ResemblesWithin(second, first) ? Likeness.SameArtwork : Likeness.Different;
    }

    public string ToBase64() => Convert.ToBase64String(Pixels);

    public static PosterFingerprint? FromBase64(string? value)
    {
        try
        {
            var pixels = Convert.FromBase64String(value ?? string.Empty);
            return pixels.Length == Width * Height ? new PosterFingerprint(pixels) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether some zoomed-in part of <paramref name="wider"/> matches the whole of <paramref name="tighter"/>.</summary>
    private static bool ResemblesWithin(PosterFingerprint wider, PosterFingerprint tighter)
    {
        foreach (var window in wider._windows.Value)
        {
            if (Distance(window, tighter.Whole, IgnoredBands) < SameArtworkBelow)
                return true;
        }

        return false;
    }

    /// <summary>The mean difference per cell, leaving out the <paramref name="ignored"/> most different bands.</summary>
    private static double Distance(float[] first, float[] second, int ignored)
    {
        Span<double> bands = stackalloc double[Bands];
        for (var index = 0; index < first.Length; index++)
            bands[index / GridWidth / RowsPerBand] += Math.Abs(first[index] - second[index]);

        bands.Sort();
        var total = 0.0;
        for (var band = 0; band < Bands - ignored; band++)
            total += bands[band];
        return total / ((Bands - ignored) * RowsPerBand * GridWidth);
    }

    private float[][] BuildWindows()
    {
        var windows = new List<float[]> { Window(1, 0, 0) };
        foreach (var zoom in Zooms)
        {
            foreach (var x in Offsets)
            {
                foreach (var y in Offsets)
                    windows.Add(Window(zoom, x, y));
            }
        }

        return [.. windows];
    }

    /// <summary>
    /// Samples part of the poster onto the comparison grid and normalizes its brightness and contrast.
    /// <paramref name="x"/> and <paramref name="y"/> place the window within the space the zoom leaves, from 0 to 1.
    /// </summary>
    private float[] Window(double zoom, double x, double y)
    {
        var width = Width * zoom;
        var height = Height * zoom;
        var left = (Width - width) * x;
        var top = (Height - height) * y;
        var cells = new float[GridWidth * GridHeight];
        for (var row = 0; row < GridHeight; row++)
        {
            for (var column = 0; column < GridWidth; column++)
            {
                var sourceX = left + ((column + 0.5) * width / GridWidth) - 0.5;
                var sourceY = top + ((row + 0.5) * height / GridHeight) - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, Width - 2);
                var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, Height - 2);
                var fx = Math.Clamp(sourceX - x0, 0, 1);
                var fy = Math.Clamp(sourceY - y0, 0, 1);
                cells[(row * GridWidth) + column] = (float)(
                    (Pixel(x0, y0) * (1 - fx) * (1 - fy))
                    + (Pixel(x0 + 1, y0) * fx * (1 - fy))
                    + (Pixel(x0, y0 + 1) * (1 - fx) * fy)
                    + (Pixel(x0 + 1, y0 + 1) * fx * fy));
            }
        }

        var mean = cells.Average();
        var spread = MathF.Sqrt(cells.Select(value => (value - mean) * (value - mean)).Average());
        if (spread < 1e-3f)
            spread = 1;
        for (var index = 0; index < cells.Length; index++)
            cells[index] = (cells[index] - mean) / spread;
        return cells;
    }

    private double Pixel(int x, int y) => Pixels[(y * Width) + x];
}
