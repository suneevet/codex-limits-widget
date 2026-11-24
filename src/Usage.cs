using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageTaskbar
{
    public sealed class QuotaWindow
    {
        public double Remaining;
        public long? ResetsAt;
        public int DurationMinutes;
        public string PercentText { get { return Math.Floor(Remaining).ToString("0") + "%"; } }
    }

    public sealed class UsageSnapshot
    {
        public QuotaWindow FiveHour;
        public QuotaWindow Weekly;
        public DateTimeOffset FetchedAt;
        public string Plan;
        public bool FromSessionLog;
        public int? ResetCredits;
        public List<long?> ResetCreditExpirations;
    }

    public sealed class ExpiryDisplay
    {
        public string Text;
        public bool Emphasize;
        public static DateTimeOffset InKorea(DateTimeOffset time) { return time.ToOffset(TimeSpan.FromHours(9)); }
        public static ExpiryDisplay For(UsageSnapshot data, DateTimeOffset now)
        {
            if (data == null || !data.ResetCredits.HasValue || data.ResetCredits.Value <= 0 || data.ResetCreditExpirations == null) return null;
            var known = data.ResetCreditExpirations.Where(x => x.HasValue).Select(x => x.Value).OrderBy(x => x).ToArray();
            if (known.Length == 0) return null;
            long nowUnix = now.ToUnixTimeSeconds();
            var future = known.Where(x => x > nowUnix).ToArray();
            if (future.Length == 0) return new ExpiryDisplay { Text = Ui.Text("만료됨"), Emphasize = true };
            int days = (InKorea(DateTimeOffset.FromUnixTimeSeconds(future[0])).Date - InKorea(now).Date).Days;
            return new ExpiryDisplay { Text = days == 0 ? Ui.Text("오늘 만료") : "D-" + days, Emphasize = days <= 7 };
        }
    }

    public static class UsageParser
    {
        public static Dictionary<string, object> Object(object value) { return value as Dictionary<string, object>; }
        public static object Get(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }
        public static UsageSnapshot Parse(string json)
        {
            var root = Object(new JavaScriptSerializer().DeserializeObject(json));
            var result = Object(Get(root, "result")) ?? root;
            var buckets = Object(Get(result, "rateLimitsByLimitId"));
            Dictionary<string, object> bucket = null;
            if (buckets != null && buckets.Count > 0)
            {
                bucket = Object(Get(buckets, "codex"));
                // A named model bucket must never silently replace the shared Codex quota.
                if (bucket == null) throw new InvalidDataException(Ui.Text("공통 Codex 사용량이 제공되지 않습니다."));
            }
            else bucket = Object(Get(result, "rateLimits"));
            if (bucket == null) throw new InvalidDataException(Ui.Text("사용량 정보가 없습니다. Codex 로그인을 확인해 주세요."));
            var snapshot = new UsageSnapshot { FetchedAt = DateTimeOffset.Now, Plan = Get(bucket, "planType") as string };
            var resetCredits = Object(Get(result, "rateLimitResetCredits"));
            int availableCount;
            if (int.TryParse(Convert.ToString(Get(resetCredits, "availableCount")), out availableCount) && availableCount >= 0)
                snapshot.ResetCredits = availableCount;
            var details = Get(resetCredits, "credits") as object[];
            if (details != null)
            {
                snapshot.ResetCreditExpirations = new List<long?>();
                foreach (var detail in details)
                {
                    var credit = Object(detail);
                    if (Convert.ToString(Get(credit, "status")) != "available") continue;
                    long timestamp;
                    long? expiration = null;
                    if (long.TryParse(Convert.ToString(Get(credit, "expiresAt")), out timestamp))
                    {
                        try { DateTimeOffset.FromUnixTimeSeconds(timestamp); expiration = timestamp; } catch (ArgumentOutOfRangeException) { }
                    }
                    snapshot.ResetCreditExpirations.Add(expiration);
                }
            }
            foreach (string key in new[] { "primary", "secondary" })
            {
                var window = ParseWindow(Object(Get(bucket, key)));
                if (window == null) continue;
                // Use actual duration rather than assuming primary/secondary order.
                if (window.DurationMinutes == 300) snapshot.FiveHour = window;
                if (window.DurationMinutes == 10080) snapshot.Weekly = window;
            }
            return snapshot;
        }
        internal static QuotaWindow ParseWindow(Dictionary<string, object> obj)
        {
            var usedValue = Get(obj, "usedPercent") ?? Get(obj, "used_percent");
            var duration = Get(obj, "windowDurationMins") ?? Get(obj, "window_minutes");
            if (usedValue == null || duration == null) return null;
            double used;
            int minutes;
            if (!double.TryParse(Convert.ToString(usedValue, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out used) ||
                double.IsNaN(used) || double.IsInfinity(used) || !int.TryParse(Convert.ToString(duration), out minutes)) return null;
            long reset;
            return new QuotaWindow { Remaining = Math.Max(0, Math.Min(100, 100 - used)), DurationMinutes = minutes,
                ResetsAt = long.TryParse(Convert.ToString(Get(obj, "resetsAt") ?? Get(obj, "resets_at")), out reset) ? (long?)reset : null };
        }
    }

    public sealed class UsageClient : IDisposable
    {
        readonly object gate = new object();
        Process process;
        bool disposed;
        public string Stage { get; private set; }
        public string ExecutablePath { get; private set; }
        public bool? ChatGptLoginRequired { get; private set; }
        public static string FindCodex()
        {
            string custom = Environment.GetEnvironmentVariable("CODEX_USAGE_CLI");
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom) && custom.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return custom;
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { string path = Path.Combine(entry.Trim('"'), "codex.exe"); if (File.Exists(path)) return path; } catch (ArgumentException) { }
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string candidate in new[] {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "codex", "bin", "codex.exe"),
                Path.Combine(local, "Programs", "Codex Limits Widget", "resources", "codex", "bin", "codex.exe"),
                Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
                Path.Combine(profile, ".codex", "bin", "codex.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", "codex.exe")
            }) {
                if (File.Exists(candidate)) return candidate;
            }
            string bin = Path.Combine(local, "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin))
            {
                var paths = Directory.GetFiles(bin, "codex.exe", SearchOption.AllDirectories);
                if (paths.Length > 0) return paths.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
            throw new FileNotFoundException("Codex CLI를 찾을 수 없습니다. Codex 앱을 설치하거나 CODEX_USAGE_CLI를 설정해 주세요.");
        }
        public async Task<UsageSnapshot> FetchAsync()
        {
            Stage="start";
            ChatGptLoginRequired = null;
            ExecutablePath=FindCodex();
            var start = new ProcessStartInfo(ExecutablePath, "app-server --listen stdio://") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using (var current = new Process { StartInfo = start })
            {
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException("UsageClient");
                    current.Start(); process = current;
                }
                // Drain stderr without retaining responses, credentials, or account identifiers.
                current.ErrorDataReceived += delegate { };
                current.BeginErrorReadLine();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                using (timeout.Token.Register(delegate { TryKill(current); }))
                {
                    string stage = "initialize/send";Stage=stage;
                    try
                    {
                        await Send(current, new { id = 1, method = "initialize", @params = new { clientInfo = new {
                            name = "gpt_usage_widget", title = "Codex Limits Widget", version = "2.0.2" } } },timeout.Token).ConfigureAwait(false);
                        stage = "initialize/read";Stage=stage;
                        await Receive(current, 1,timeout.Token).ConfigureAwait(false);
                        stage = "initialized/send";Stage=stage;
                        await Send(current, new { method = "initialized", @params = new { } },timeout.Token).ConfigureAwait(false);
                        stage = "account/read";Stage=stage;
                        await Send(current, new { id = 2, method = "account/read", @params = new { refreshToken = false } }, timeout.Token).ConfigureAwait(false);
                        string accountJson = await Receive(current, 2, timeout.Token).ConfigureAwait(false);
                        var envelope = UsageParser.Object(new JavaScriptSerializer().DeserializeObject(accountJson));
                        var accountResult = UsageParser.Object(UsageParser.Get(envelope, "result"));
                        if (accountResult == null || !accountResult.ContainsKey("account"))
                            throw new InvalidDataException("Codex CLI returned an invalid account response.");
                        var account = UsageParser.Object(UsageParser.Get(accountResult, "account"));
                        ChatGptLoginRequired = !string.Equals(UsageParser.Get(account, "type") as string, "chatgpt", StringComparison.Ordinal);
                        if (ChatGptLoginRequired == true)
                            throw new InvalidDataException("Sign in to Codex with ChatGPT.");
                        await Send(current, new { id = 3, method = "account/rateLimits/read", @params = new { } },timeout.Token).ConfigureAwait(false);
                        stage = "rateLimits/read";Stage=stage;
                        string result = await Receive(current, 3,timeout.Token).ConfigureAwait(false);
                        stage = "rateLimits/parse";Stage=stage;
                        return UsageParser.Parse(result);
                    }
                    catch (Exception ex)
                    {
                        if (timeout.IsCancellationRequested) throw new TimeoutException(Ui.Text("사용량 조회 시간이 초과되었습니다. 잠시 후 다시 시도합니다."));
                        if (ex is InvalidDataException) throw;
                        throw new IOException(Ui.Text("Codex 사용량을 조회하지 못했습니다. 로그인과 인터넷 연결을 확인해 주세요. (") + stage + ": " + ex.GetType().Name + ")");
                    }
                    finally
                    {
                        TryKill(current);
                        lock (gate) { if (process == current) process = null; }
                    }
                }
            }
        }
        internal static async Task AwaitOperation(Task operation,CancellationToken cancellation)
        {
            var cancelled=new TaskCompletionSource<bool>();
            using(cancellation.Register(delegate {cancelled.TrySetResult(true);})) {
                await Task.WhenAny(operation,cancelled.Task).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                await operation.ConfigureAwait(false);
            }
        }
        static async Task Send(Process current, object message,CancellationToken cancellation)
        {
            await AwaitOperation(current.StandardInput.WriteLineAsync(new JavaScriptSerializer().Serialize(message)),cancellation).ConfigureAwait(false);
            await AwaitOperation(current.StandardInput.FlushAsync(),cancellation).ConfigureAwait(false);
        }
        static async Task<string> Receive(Process current, int id,CancellationToken cancellation)
        {
            string line;
            while (true)
            {
                var reading=current.StandardOutput.ReadLineAsync();
                await AwaitOperation(reading,cancellation).ConfigureAwait(false);
                line=await reading.ConfigureAwait(false);if(line==null)break;
                Dictionary<string, object> obj;
                try { obj = UsageParser.Object(new JavaScriptSerializer().DeserializeObject(line)); } catch (ArgumentException) { continue; }
                if (Convert.ToString(UsageParser.Get(obj, "id")) != id.ToString()) continue;
                if (UsageParser.Get(obj, "error") != null)
                    throw new InvalidDataException(Ui.Text("Codex 사용량 요청이 거절되었습니다. Codex 앱의 로그인 상태를 확인해 주세요."));
                return line;
            }
            throw new IOException(Ui.Text("Codex 연결이 종료되었습니다."));
        }
        static void TryKill(Process current) { try { if (!current.HasExited) current.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        public void CancelPending() { lock (gate) { if (process != null) TryKill(process); } }
        public void Dispose() { lock (gate) { disposed = true; if (process != null) TryKill(process); } }
    }
}
