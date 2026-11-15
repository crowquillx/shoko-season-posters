using System.Runtime.CompilerServices;
using ImageMagick;
using Shoko.SeasonPosters.Planning;

namespace Shoko.SeasonPosters.Imaging;

/// <summary>
/// Shrinks an image to a fingerprint with Magick.NET, which the server ships and lends to plugins. Kept apart so that
/// if a server ever stops shipping it, only this call fails, and the caller can carry on without lookalike checks.
/// </summary>
internal static class FingerprintDecoder
{
    /// <summary>Returns the fingerprint's pixels, or null when the data is not an image Magick can read.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[]? Decode(byte[] data)
    {
        try
        {
            // JPEG can decode straight to a fraction of its size, which saves most of the work on a full-size poster.
            var settings = new MagickReadSettings();
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{PosterFingerprint.Width * 4}x{PosterFingerprint.Height * 4}");
            using var image = new MagickImage(data, settings);
            image.AutoOrient();
            if (image.HasAlpha)
            {
                image.BackgroundColor = MagickColors.Black;
                image.Alpha(AlphaOption.Remove);
            }

            image.ColorSpace = ColorSpace.Gray;
            image.Resize(new MagickGeometry(PosterFingerprint.Width, PosterFingerprint.Height) { IgnoreAspectRatio = true });
            using var pixels = image.GetPixels();
            var rgb = pixels.ToByteArray(PixelMapping.RGB);
            if (rgb is null || rgb.Length < PosterFingerprint.Width * PosterFingerprint.Height * 3)
                return null;

            var gray = new byte[PosterFingerprint.Width * PosterFingerprint.Height];
            for (var index = 0; index < gray.Length; index++)
                gray[index] = rgb[index * 3];
            return gray;
        }
        catch (MagickException)
        {
            return null;
        }
    }
}
