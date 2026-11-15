using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;

namespace Shoko.SeasonPosters.Library;

internal static class RemoteImages
{
    /// <summary>
    /// The source's own URL for an image, at <paramref name="size"/> where the source offers sizes, such as "w342" on
    /// TMDB. Null when the source has no URL template.
    /// </summary>
    public static string? Url(IImageManager images, IImage image, string size)
    {
        var template = images.GetTemplateUrlForSource(image.Source);
        return string.IsNullOrEmpty(template) || !template.Contains("{0}", StringComparison.Ordinal) || string.IsNullOrEmpty(image.ResourceID)
            ? null
            : template.Replace("/original/", $"/{size}/", StringComparison.Ordinal).Replace("{0}", image.ResourceID, StringComparison.Ordinal);
    }
}
