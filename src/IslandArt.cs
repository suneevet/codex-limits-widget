using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    internal enum IslandTheme { Light, Dark, Glass }

    internal static class IslandArt
    {
        internal const int ClosedWidth = 204;
        internal const int ClosedHeight = 34;
        internal const int OpenWidth = 352;
        internal const int OpenHeight = 152;
        internal const int Padding = 2;

        internal static float Smooth(float t) { return t * t * (3 - 2 * t); }
        static float Mix(float a, float b, float t) { return a + (b - a) * t; }

        internal static string CompactQuota(QuotaWindow five, DateTimeOffset now)
        {
            if (five == null) return "5 ч · —";
            if (Active(five, now) == null) return "Ждём обновления";
            if (five.Remaining > 0) return "5 ч · " + (five.Remaining < 1 ? "<1%" : five.PercentText);
            string countdown = Widget.CardCountdown(five, now);
            return countdown == "—" ? "5 ч · 0%" : "Сброс через " + countdown;
        }

        internal static Size LogicalSize(float progress)
        {
            float amount = Smooth(progress);
            return new Size((int)Math.Round(Mix(ClosedWidth, OpenWidth, amount)) + 2 * Padding,
                (int)Math.Round(Mix(ClosedHeight, OpenHeight, amount)) + 2 * Padding);
        }

        internal static Bitmap Render(float progress, float scale, IslandTheme theme, UsageSnapshot data,
            bool loading, bool stale, bool demo, DateTimeOffset now, double loadingSeconds, bool translucentGlass = true, float lightPosition = .34f, GlassEnvironment environment = null, int? accentRgb = null)
        {
            var size = LogicalSize(progress);
            var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(size.Width * scale)),
                Math.Max(1, (int)Math.Round(size.Height * scale)), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.ScaleTransform(scale, scale);
                var body = new RectangleF(Padding, Padding, size.Width - 2 * Padding, size.Height - 2 * Padding);
                float amount = Smooth(progress);
                bool light = theme == IslandTheme.Light;
                bool clearGlass = theme == IslandTheme.Glass && translucentGlass;
                var ink = light ? Color.FromArgb(24, 33, 48) : Color.FromArgb(245, 248, 255);
                var muted = light ? Color.FromArgb(75, 87, 108) : Color.FromArgb(197, 209, 230);
                var accent = AccentColor(light, accentRgb);
                var accentFill = accentRgb.HasValue ? Color.FromArgb(accentRgb.Value | unchecked((int)0xff000000)) : accent;
                var rim = light ? Color.FromArgb(132, 158, 197) : Color.FromArgb(100, 124, 166);
                float radius = Mix(17, 26, amount);
                using (var shape = Round(body, radius))
                {
                    if (theme == IslandTheme.Glass)
                    {
                        // Resolve the glass silhouette at 2x device resolution. Labels still render at native DPI.
                        using (var surface = new Bitmap(bitmap.Width * 2, bitmap.Height * 2, PixelFormat.Format32bppPArgb))
                        {
                            using (var glass = Graphics.FromImage(surface))
                            {
                                glass.SmoothingMode = SmoothingMode.AntiAlias;
                                glass.PixelOffsetMode = PixelOffsetMode.Half;
                                glass.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                glass.ScaleTransform(scale * 2, scale * 2);
                                DrawSurface(glass, shape, body, radius, theme, rim, translucentGlass,
                                    lightPosition, environment, scale * 2);
                            }
                            var transform = g.Save();
                            g.ResetTransform();
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            using (var attributes = new ImageAttributes())
                            {
                                attributes.SetWrapMode(WrapMode.TileFlipXY);
                                g.DrawImage(surface, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                    0, 0, surface.Width, surface.Height, GraphicsUnit.Pixel, attributes);
                            }
                            g.Restore(transform);
                        }
                    }
                    else DrawSurface(g, shape, body, radius, theme, rim, translucentGlass, lightPosition, null, scale);
                    var clip = g.Save();
                    g.SetClip(shape);
                    float labelX = Mix(30, 35, amount) + body.X;
                    float labelY = Mix(6, 9, amount) + body.Y;
                    var five = Active(data == null ? null : data.FiveHour, now);
                    var week = Active(data == null ? null : data.Weekly, now);
                    Color signal = (five != null && five.Remaining <= 15) || (week != null && week.Remaining <= 15)
                        ? Color.FromArgb(238, 150, 91) : accentFill;
                    if (loading)
                        signal = Color.FromArgb((int)(175 + 80 * (.5 + .5 * Math.Sin(loadingSeconds * 6))), signal);
                    using (var glow = new SolidBrush(Color.FromArgb(40, signal)))
                        g.FillEllipse(glow, labelX - 17, labelY + 4, 13, 13);
                    using (var dot = new SolidBrush(signal)) g.FillEllipse(dot, labelX - 14, labelY + 7, 7, 7);
                    Label(g, "Codex", labelX, labelY, 87, 22, 14, ink, true, shadow: clearGlass);

                    float compactAlpha = 1 - Smooth(Math.Min(1, progress / .38f));
                    if (compactAlpha > 0)
                    {
                        Label(g, CompactQuota(data == null ? null : data.FiveHour, now), body.X + 78, labelY,
                            ClosedWidth - 84, 22, 11, Fade(stale ? muted : ink, compactAlpha), true, true, shadow: clearGlass);
                    }

                    float reveal = Smooth(Math.Max(0, (progress - .37f) / .63f));
                    if (reveal > 0)
                    {
                        float lift = (1 - reveal) * 9;
                        string status = loading ? "Обновление…" : Widget.CardCaption(data, stale, demo);
                        Label(g, status, body.X + 220, labelY, 112, 22, 11, Fade(muted, reveal), false, true, shadow: clearGlass);
                        using (var divider = new Pen(Fade(Color.FromArgb(85, rim), reveal)))
                            g.DrawLine(divider, body.X + 17, body.Y + 40, body.X + 335, body.Y + 40);
                        Row(g, body.X, body.Y + 47 + lift, "5 часов", data == null ? null : data.FiveHour,
                            five, now, ink, muted, accent, accentFill, rim, reveal, stale, clearGlass);
                        Row(g, body.X, body.Y + 95 + lift, "Неделя", data == null ? null : data.Weekly,
                            week, now, ink, muted, accent, accentFill, rim, reveal, stale, clearGlass);
                    }
                    g.Restore(clip);
                }
            }
            return bitmap;
        }

        static void DrawSurface(Graphics g, GraphicsPath shape, RectangleF body, float radius, IslandTheme theme, Color rim, bool translucentGlass, float lightPosition, GlassEnvironment environment, float rasterScale)
        {
            Color top, bottom;
            switch (theme)
            {
                case IslandTheme.Light:
                    top = Color.FromArgb(255, 255, 255); bottom = Color.FromArgb(225, 235, 250); break;
                case IslandTheme.Glass:
                    top = Color.FromArgb(translucentGlass ? 36 : 255, 25, 34, 48);
                    bottom = Color.FromArgb(translucentGlass ? 52 : 255, 18, 26, 40);
                    break;
                default:
                    top = Color.FromArgb(37, 43, 60); bottom = Color.FromArgb(17, 21, 32); break;
            }
            using (var fill = new LinearGradientBrush(body, top, bottom, 90f)) g.FillPath(fill, shape);
            if (theme == IslandTheme.Glass)
            {
                var state = g.Save();
                g.SetClip(shape);
                if (translucentGlass && environment != null)
                    environment.DrawEdge(g, body, radius, lightPosition, rasterScale);
                float focus = Math.Max(.1f, Math.Min(.9f, lightPosition));
                using (var reflection = new LinearGradientBrush(body, Color.White, Color.White, 14f))
                {
                    reflection.InterpolationColors = new ColorBlend {
                        Positions = new[] { 0f, focus * .6f, focus, focus + (1 - focus) * .48f, 1f },
                        Colors = new[] { Color.FromArgb(56, Color.White), Color.FromArgb(14, Color.White),
                            Color.FromArgb(220, Color.White), Color.FromArgb(28, Color.White), Color.FromArgb(157, Color.White) }
                    };
                    using (var lip = new Pen(reflection, 1.4f)) g.DrawPath(lip, shape);
                }
                var inner = body;
                inner.Inflate(-1.5f, -1.5f);
                using (var bevel = Round(inner, Math.Min(23, inner.Height / 2)))
                using (var shade = new Pen(Color.FromArgb(35, 230, 240, 255), 1f)) g.DrawPath(shade, bevel);
                g.Restore(state);
            }
            else using (var outline = new Pen(Color.FromArgb(165, rim))) g.DrawPath(outline, shape);
        }

        static void Row(Graphics g, float x, float y, string label, QuotaWindow quota, QuotaWindow active,
            DateTimeOffset now, Color ink, Color muted, Color accent, Color accentFill, Color rim, float reveal, bool stale, bool clearGlass)
        {
            var valueColor = stale ? muted : accent;
            Label(g, label, x + 18, y, 130, 20, 13, Fade(ink, reveal), true, shadow: clearGlass);
            Label(g, active == null ? "—" : active.PercentText, x + 260, y - 1, 73, 22, 16, Fade(valueColor, reveal), true, true, shadow: clearGlass);
            var track = new RectangleF(x + 18, y + 27, 154, 5);
            using (var shape = Round(track, 2.5f))
            using (var brush = new SolidBrush(Fade(Color.FromArgb(115, rim), reveal)))
                g.FillPath(brush, shape);
            float fillWidth = active == null ? 0 : (float)(active.Remaining * track.Width / 100 * reveal);
            if (fillWidth > 0)
            {
                using (var brush = new LinearGradientBrush(track, Fade(stale ? muted : accentFill, reveal),
                    Fade(stale ? muted : MixColor(accentFill, Color.White, .34f), reveal), 0f))
                using (var shape = Round(new RectangleF(track.X, track.Y, fillWidth, track.Height), Math.Min(2.5f, fillWidth / 2)))
                    g.FillPath(brush, shape);
            }
            string countdown = Widget.CardCountdown(quota, now);
            string time = countdown == "—" ? "Время сброса неизвестно"
                : countdown == "ожидание" ? "Ожидаем сброс" : "Сброс через " + countdown;
            Label(g, time, x + 178, y + 20, 155, 20, 11, Fade(muted, reveal), false, true, shadow: clearGlass);
        }

        internal static Color AccentColor(bool light, int? rgb)
        {
            if (!rgb.HasValue) return light ? Color.FromArgb(54, 93, 206) : Color.FromArgb(125, 170, 255);
            var color = Color.FromArgb(rgb.Value | unchecked((int)0xff000000));
            // Keep the selected hue while nudging numeric labels toward readable tones.
            return MixColor(color, light ? Color.FromArgb(20, 25, 38) : Color.White, light ? .35f : .28f);
        }

        static Color MixColor(Color from, Color to, float amount)
        {
            return Color.FromArgb((int)Mix(from.R, to.R, amount), (int)Mix(from.G, to.G, amount),
                (int)Mix(from.B, to.B, amount));
        }

        internal static QuotaWindow Active(QuotaWindow quota, DateTimeOffset now)
        {
            return quota != null && (!quota.ResetsAt.HasValue || quota.ResetsAt.Value > now.ToUnixTimeSeconds()) ? quota : null;
        }

        static Color Fade(Color color, float alpha) { return Color.FromArgb((int)(color.A * alpha), color); }

        static void Label(Graphics g, string text, float x, float y, float width, float height, float size, Color color,
            bool bold, bool right = false, bool shadow = false)
        {
            using (var font = new Font("Calibri", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = right ? StringAlignment.Far : StringAlignment.Near,
                LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                var bounds = new RectangleF(x, y, width, height);
                if (shadow)
                    using (var shade = new SolidBrush(Color.FromArgb(color.A * 3 / 4, 8, 14, 23)))
                    {
                        var offset = bounds;
                        offset.Offset(0, 1);
                        g.DrawString(text, font, shade, offset, format);
                    }
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
