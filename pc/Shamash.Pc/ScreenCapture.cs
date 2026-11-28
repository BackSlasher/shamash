using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Shamash.Pc;

/// <summary>
/// Screenshot of the whole virtual screen (every monitor), scaled so the longest side is at most
/// <see cref="MaxSide"/> pixels (about 1200 image tokens for the model) and encoded as PNG.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCapture : IScreenCapture
{
    public const int MaxSide = 1280;

    public ToolImage? Capture()
    {
        var area = SystemInformation.VirtualScreen;
        if (area.Width <= 0 || area.Height <= 0) return null;
        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(area.Location, Point.Empty, area.Size);

        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(area.Width, area.Height));
        var w = Math.Max(1, (int)Math.Round(area.Width * scale));
        var h = Math.Max(1, (int)Math.Round(area.Height * scale));
        using var small = scale < 1.0 ? new Bitmap(w, h, PixelFormat.Format32bppArgb) : null;
        if (small is not null)
        {
            using var g = Graphics.FromImage(small);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, w, h);
        }
        using var ms = new MemoryStream();
        (small ?? full).Save(ms, ImageFormat.Png);
        return new ToolImage(ms.ToArray(), "image/png", w, h);
    }
}
