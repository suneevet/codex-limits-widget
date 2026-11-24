using System;

namespace CodexUsageTaskbar
{
    public static class QuotaText
    {
        public static string FormatResetTime(QuotaWindow window, bool full = false)
        {
            if (window == null || !window.ResetsAt.HasValue) return "—";
            try {
                string format = full ? "dd.MM.yyyy HH:mm" : window.DurationMinutes == 10080 ? "dd.MM" : "HH:mm";
                return DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt.Value).ToOffset(TimeSpan.FromHours(3)).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException) { return "—"; }
        }
        public static string FormatRemainingTime(QuotaWindow window, DateTimeOffset now)
        {
            if (window == null || !window.ResetsAt.HasValue) return "—";
            try
            {
                TimeSpan remaining = DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt.Value) - now;
                if (remaining <= TimeSpan.Zero) return Ui.Text("재설정 대기");
                if (window.DurationMinutes == 10080 && remaining.TotalDays >= 1)
                    return Math.Floor(remaining.TotalDays).ToString("0") + Ui.Text("일 남음");
                if (remaining.TotalHours >= 1)
                    return Math.Floor(remaining.TotalHours).ToString("0") + Ui.Text("시간 남음");
                return Math.Ceiling(remaining.TotalMinutes).ToString("0") + Ui.Text("분 남음");
            }
            catch (ArgumentOutOfRangeException) { return "—"; }
        }
        public static string RemainingAmount(string text)
        {
            string suffix = Ui.Text(" 남음");
            return text.EndsWith(suffix, StringComparison.Ordinal) ? text.Substring(0, text.Length - suffix.Length) : text == Ui.Text("재설정 대기") ? Ui.Text("대기") : text;
        }
    }
}
