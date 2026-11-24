using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    public sealed class Palette
    {
        public Color Background, Ink, Muted, Card, Track, Divider, Border;
        public static Palette For(bool dark)
        {
            return dark ? new Palette { Background = Color.FromArgb(28,30,35), Ink = Color.FromArgb(238,240,246),
                Muted = Color.FromArgb(173,181,197), Card = Color.FromArgb(38,42,51), Track = Color.FromArgb(55,65,89),
                Divider = Color.FromArgb(104,113,132), Border = Color.FromArgb(71,78,93) } :
                new Palette { Background = Color.White, Ink = Color.FromArgb(26,28,31), Muted = Color.FromArgb(105,112,121),
                Card = Color.FromArgb(245,247,252), Track = Color.FromArgb(234,240,254), Divider = Color.FromArgb(190,197,207), Border = Color.FromArgb(228,231,237) };
        }
    }
    public sealed class WidgetLayout
    {
        public int Width, First, Second, Label, Bar, Percent, PercentWidth, Time, TimeWidth, Common, CaptionWidth;
        public bool CommonVisible;
        public string FiveText, WeekText;
        public static int Measure(string text, int size, string family = "Malgun Gothic", FontStyle style = FontStyle.Regular)
        {
            using (var bitmap = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font(family, size, style, GraphicsUnit.Pixel))
                return TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }
        public static WidgetLayout Create(UsageSnapshot data, bool stale, bool demo, Settings preferences, DateTimeOffset now)
        {
            var s = preferences ?? new Settings();
            var layout = new WidgetLayout { First = -1, Second = -1 };
            string caption = Widget.CardCaption(data, stale, demo);
            layout.CaptionWidth = Measure(caption, s.FontSize - 1);
            int cursor = 0;
            if (s.ShowReset) {
                cursor = Math.Max(100, 16 + Math.Max(layout.CaptionWidth, Measure("Обновление…", s.FontSize - 1)));
                cursor = Math.Max(cursor, Ui.MeasureWidgetTitle(s.FontSize + 1) + 29);
                if (s.ShowUsage || s.ShowRemaining) layout.First = cursor;
            }
            if (s.ShowUsage) {
                layout.Label = cursor + 10;
                layout.Bar = layout.Label + Math.Max(36, Math.Max(Measure("5 часов", s.FontSize), Measure("Неделя", s.FontSize)) + 2) + 4;
                layout.Percent = layout.Bar + s.BarWidth + 7;
                string fivePercent = data == null || data.FiveHour == null ? "—" : data.FiveHour.PercentText;
                string weekPercent = data == null || data.Weekly == null ? "—" : data.Weekly.PercentText;
                layout.PercentWidth = Math.Max(Measure("100%", s.FontSize, "Segoe UI Semibold"), Math.Max(Measure(fivePercent, s.FontSize, "Segoe UI Semibold"), Measure(weekPercent, s.FontSize, "Segoe UI Semibold"))) + 2;
                cursor = layout.Percent + layout.PercentWidth + 10;
                if (s.ShowRemaining) layout.Second = cursor;
            }
            if (s.ShowRemaining) {
                layout.Time = cursor + (cursor > 0 ? 10 : 8);
                layout.FiveText = Widget.CardCountdown(data == null ? null : data.FiveHour, now);
                layout.WeekText = Widget.CardCountdown(data == null ? null : data.Weekly, now);
                layout.CommonVisible = true;
                int textWidth = Math.Max(Measure("99 ч 59 мин", s.FontSize), Math.Max(Measure(layout.FiveText, s.FontSize), Measure(layout.WeekText, s.FontSize)));
                layout.TimeWidth = textWidth;
                layout.Common = layout.Time + textWidth + 4;
                cursor = layout.CommonVisible ? layout.Common + Measure("до сброса", s.FontSize) : layout.Time + textWidth;
            }
            layout.Width = Math.Max(100, cursor + 10); return layout;
        }
    }
}
