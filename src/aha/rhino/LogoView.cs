using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;

namespace Aha.Runtime;

// Decorative content takes only the remaining panel space; it never requests a scrollbar.
internal sealed class LogoView : Drawable
{
    private Bitmap? image;
    private bool dark;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        bool currentDark = Rhino.Runtime.HostUtils.RunningInDarkMode;
        if (image == null || dark != currentDark)
        {
            using var stream = typeof(LogoView).Assembly.GetManifestResourceStream("Aha.Logo.svg")!;
            using var reader = new StreamReader(stream);
            var pixels = DrawingUtilities.PixelsFromSvg(reader.ReadToEnd(), 256, 256, false,
                System.Drawing.Color.Empty);
            if (currentDark) DrawingUtilities.DarkModeConvertPixels(ref pixels);
            image?.Dispose();
            image = new Bitmap(256, 256, PixelFormat.Format32bppRgba,
                Enumerable.Range(0, 256 * 256).Select(i => Color.FromArgb(
                    pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3])));
            dark = currentDark;
        }
        float size = Math.Min(112, Math.Min(ClientSize.Width, ClientSize.Height));
        if (size > 0)
            e.Graphics.DrawImage(image, new RectangleF((ClientSize.Width - size) / 2,
                ClientSize.Height - size, size, size));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { image?.Dispose(); image = null; }
        base.Dispose(disposing);
    }
}
