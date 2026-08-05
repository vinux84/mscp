using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SdkRectangle = System.Drawing.Rectangle;

namespace FlexView.Client
{
    // Renders the thumbnail shown next to a layout in Smart Client's Add View picker. A layout
    // without a ViewLayoutIcon shows up blank there, which makes a set of saved layouts impossible
    // to tell apart, so one is always generated.
    //
    // RenderTargetBitmap needs an STA thread with a Dispatcher, so this must be called on the UI
    // thread. Callers render first, then hand the resulting base64 string to the background save.
    internal static class LayoutIconRenderer
    {
        // 16x16 to match the built-in layout icons. The picker draws the bitmap at its native size
        // rather than scaling it to the row, so a larger icon does not render sharper - it stretches
        // the row it sits in and towers over the 1x1 / 2x2 / 3x3 entries beside it.
        private const int IconPixels = 16;

        // Mid-tone fill deliberately: the picker background differs between Smart Client themes, and
        // a mid grey stays legible against both rather than disappearing into one of them.
        private static readonly Brush PaneFill = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
        private static readonly Brush PaneStroke = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));

        static LayoutIconRenderer()
        {
            PaneFill.Freeze();
            PaneStroke.Freeze();
        }

        // Returns a base64 PNG of the arrangement, or null if rendering fails. A missing icon is a
        // cosmetic loss, never a reason to abort the save, so failures are logged and swallowed.
        public static string RenderBase64(IEnumerable<SdkRectangle> rects)
        {
            try
            {
                if (rects == null) return null;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    // Transparent background so the picker's own backdrop shows through.
                    dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, IconPixels, IconPixels));

                    var pen = new Pen(PaneStroke, 1.0);
                    pen.Freeze();

                    var any = false;
                    const double scale = (double)IconPixels / LayoutRepository.SdkMax;

                    foreach (var r in rects)
                    {
                        any = true;

                        // Round each edge independently rather than rounding position and size
                        // separately. At 16px a 5% pane is well under one pixel, and rounding the
                        // size on its own collapses it to zero - the pane would silently vanish
                        // from the icon while still existing in the layout.
                        var x0 = Math.Round(r.X * scale);
                        var y0 = Math.Round(r.Y * scale);
                        var x1 = Math.Round((r.X + r.Width) * scale);
                        var y1 = Math.Round((r.Y + r.Height) * scale);

                        // Half-pixel offset keeps the 1px stroke on a device pixel instead of
                        // straddling two, which otherwise renders as a soft 2px smear. Insetting by
                        // one leaves a hairline between neighbours, which is what separates the
                        // panes visually at this size.
                        var w = Math.Max(1.0, x1 - x0 - 1.0);
                        var h = Math.Max(1.0, y1 - y0 - 1.0);

                        dc.DrawRectangle(PaneFill, pen, new Rect(x0 + 0.5, y0 + 0.5, w, h));
                    }

                    if (!any) return null;
                }

                var bitmap = new RenderTargetBitmap(IconPixels, IconPixels, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    var base64 = Convert.ToBase64String(ms.ToArray());
                    FlexViewDefinition.Log.Info($"[FlexViewLayout] RenderIcon: {IconPixels}x{IconPixels} png, {ms.Length} bytes, {base64.Length} base64 chars.");
                    return base64;
                }
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] RenderIcon failed - {ex.GetType().Name}: {ex.Message}. Layout will be saved without a thumbnail.", ex);
                return null;
            }
        }

        // Same drawing, returned as a live ImageSource for the Save As Layout preview. Kept separate
        // from RenderBase64 so the preview can be sized independently of the stored 64px icon.
        public static ImageSource RenderPreview(IEnumerable<SdkRectangle> rects, int pixelWidth, int pixelHeight)
        {
            try
            {
                if (rects == null) return null;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x05, 0x08, 0x11)), null,
                        new Rect(0, 0, pixelWidth, pixelHeight));

                    var pen = new Pen(PaneStroke, 1.0);
                    pen.Freeze();

                    foreach (var r in rects)
                    {
                        var x = Math.Round(r.X * (double)pixelWidth / LayoutRepository.SdkMax) + 0.5;
                        var y = Math.Round(r.Y * (double)pixelHeight / LayoutRepository.SdkMax) + 0.5;
                        var w = Math.Round(r.Width * (double)pixelWidth / LayoutRepository.SdkMax) - 1.0;
                        var h = Math.Round(r.Height * (double)pixelHeight / LayoutRepository.SdkMax) - 1.0;

                        if (w <= 0 || h <= 0) continue;
                        dc.DrawRectangle(PaneFill, pen, new Rect(x, y, w, h));
                    }
                }

                var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                FlexViewDefinition.Log.Error($"[FlexViewLayout] RenderPreview failed - {ex.GetType().Name}: {ex.Message}", ex);
                return null;
            }
        }
    }
}
