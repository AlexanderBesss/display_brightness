using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DisplayBrightness.Services;

/// <summary>
/// Creates a compact sun-shaped tray icon whose center acts as a brightness meter.
/// </summary>
public static class TrayBrightnessIcon
{
    private const int IconSize = 32;

    public static Icon Create(int? brightness)
    {
        var value = Math.Clamp(brightness ?? 0, 0, 100);
        var ratio = value / 100f;

        using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawRays(graphics, value, brightness.HasValue);
            DrawSunMeter(graphics, ratio, brightness.HasValue);
        }

        var iconHandle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static void DrawRays(Graphics graphics, int brightness, bool hasDisplay)
    {
        const float center = 15.5f;
        const float innerRadius = 11.5f;
        const float outerRadius = 14.2f;
        var activeRays = hasDisplay ? (int)Math.Ceiling(brightness / 12.5) : 0;

        using var shadowPen = new Pen(Color.FromArgb(210, 31, 41, 55), 4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var activePen = new Pen(Color.FromArgb(255, 250, 184, 54), 2.1f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var inactivePen = new Pen(Color.FromArgb(235, 156, 163, 175), 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        for (var index = 0; index < 8; index++)
        {
            var angle = (-90 + index * 45) * Math.PI / 180;
            var start = new PointF(
                center + innerRadius * (float)Math.Cos(angle),
                center + innerRadius * (float)Math.Sin(angle));
            var end = new PointF(
                center + outerRadius * (float)Math.Cos(angle),
                center + outerRadius * (float)Math.Sin(angle));

            graphics.DrawLine(shadowPen, start, end);
            graphics.DrawLine(index < activeRays ? activePen : inactivePen, start, end);
        }
    }

    private static void DrawSunMeter(Graphics graphics, float ratio, bool hasDisplay)
    {
        var sunBounds = new RectangleF(7.5f, 7.5f, 16f, 16f);
        using var outlinePen = new Pen(Color.FromArgb(255, 31, 41, 55), 2.4f);
        using var innerPen = new Pen(Color.FromArgb(245, 229, 231, 235), 1.2f);
        using var emptyBrush = new SolidBrush(Color.FromArgb(245, 75, 85, 99));
        using var fillBrush = new SolidBrush(Color.FromArgb(255, 250, 184, 54));

        graphics.FillEllipse(emptyBrush, sunBounds);

        if (hasDisplay && ratio > 0)
        {
            var state = graphics.Save();
            using var sunPath = new GraphicsPath();
            sunPath.AddEllipse(sunBounds);
            graphics.SetClip(sunPath);

            var fillTop = sunBounds.Bottom - sunBounds.Height * ratio;
            graphics.FillRectangle(
                fillBrush,
                sunBounds.Left,
                fillTop,
                sunBounds.Width,
                sunBounds.Bottom - fillTop);
            graphics.Restore(state);
        }

        graphics.DrawEllipse(outlinePen, sunBounds);
        graphics.DrawEllipse(innerPen, RectangleF.Inflate(sunBounds, -1.4f, -1.4f));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
