using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace CodexUsageTaskbar
{
    internal static class SessionQuota
    {
        const int MaxTailBytes = 1024 * 1024;
        const int MaxRecentFiles = 16;

        public static UsageSnapshot ReadLatest(DateTimeOffset now)
        {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            string sessions = Path.Combine(home, "sessions");
            try
            {
                var paths = new List<FileInfo>();
                for (int i = 0; i <= 7; i++)
                {
                    var date = now.UtcDateTime.AddDays(-i);
                    string day = Path.Combine(sessions, date.ToString("yyyy", CultureInfo.InvariantCulture),
                        date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture));
                    if (Directory.Exists(day)) paths.AddRange(new DirectoryInfo(day).EnumerateFiles("rollout-*.jsonl"));
                }

                UsageSnapshot newest = null;
                var serializer = new JavaScriptSerializer();
                foreach (var file in paths.OrderByDescending(file => file.LastWriteTimeUtc).Take(MaxRecentFiles))
                {
                    var reading = ReadTail(file.FullName, serializer, now);
                    if (reading != null && (newest == null || reading.FetchedAt > newest.FetchedAt))
                        newest = reading;
                }
                return newest;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        static UsageSnapshot ReadTail(string path, JavaScriptSerializer serializer, DateTimeOffset now)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    long start = Math.Max(0, stream.Length - MaxTailBytes);
                    stream.Position = start;
                    if (start > 0) reader.ReadLine();
                    string line;
                    UsageSnapshot latest = null;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0 ||
                            line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) continue;
                        var current = ReadEvent(line, serializer, now);
                        if (current != null && (latest == null || current.FetchedAt >= latest.FetchedAt)) latest = current;
                    }
                    return latest;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static UsageSnapshot ReadEvent(string line, JavaScriptSerializer serializer, DateTimeOffset now)
        {
            Dictionary<string, object> row;
            try { row = UsageParser.Object(serializer.DeserializeObject(line)); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }

            var payload = UsageParser.Object(UsageParser.Get(row, "payload"));
            if (!Equals(UsageParser.Get(row, "type"), "event_msg") || !Equals(UsageParser.Get(payload, "type"), "token_count"))
                return null;
            var limits = UsageParser.Object(UsageParser.Get(payload, "rate_limits"));
            if (limits == null) return null;
            var bucket = UsageParser.Get(limits, "limit_id");
            if (bucket != null && !Equals(bucket, "codex")) return null;

            DateTimeOffset timestamp;
            if (!DateTimeOffset.TryParse(UsageParser.Get(row, "timestamp") as string, out timestamp) || timestamp > now.AddMinutes(1))
                return null;
            var result = new UsageSnapshot { FromSessionLog = true, FetchedAt = timestamp };
            foreach (var key in new[] { "primary", "secondary" })
            {
                var window = UsageParser.ParseWindow(UsageParser.Object(UsageParser.Get(limits, key)));
                if (window == null) continue;
                if (window.DurationMinutes == 300) result.FiveHour = window;
                if (window.DurationMinutes == 10080) result.Weekly = window;
            }
            return result.FiveHour == null && result.Weekly == null ? null : result;
        }
    }
}
