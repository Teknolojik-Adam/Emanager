using System.IO;

namespace Emanager.App;

/// <summary>
/// Image decoding used by thumbnails and the preview panel. Avalonia's
/// Bitmap.DecodeToWidth throws a raw NullReferenceException from
/// Avalonia.Skia.ImmutableBitmap for corrupt or unusual files (observed with
/// sparse files); that must not escape into faulted background tasks, which
/// previously surfaced as unobserved-task failures in crash.log.
/// </summary>
public static class ImageDecode
{
    public static Avalonia.Media.Imaging.Bitmap? TryDecodeToWidth(Stream stream, int width)
    {
        try { return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, width); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or TimeoutException or OperationCanceledException
                                   or NullReferenceException)
        { return null; }
    }
}
