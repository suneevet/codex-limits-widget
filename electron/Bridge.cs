using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    internal sealed class Bridge : IDisposable
    {
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64000000 };
        readonly UsageClient client = new UsageClient();
        readonly PollSchedule schedule = new PollSchedule();
        readonly IslandNative island = new IslandNative();
        readonly Settings settings;
        readonly UsageHistory history;
        readonly AlertEngine alerts;
        readonly Queue<UsageAlert> pendingAlerts = new Queue<UsageAlert>();
        readonly uint ownerProcess;
        readonly bool demo;
        readonly string executable;
        Task<UsageSnapshot> live, log;
        UsageSnapshot snapshot;
        DateTimeOffset nextLog, nextAlert, nextStatusWrite;
        bool suspended, locked, online, suppressed, loginRequired;
        string status;

        Bridge(string[] args)
        {
            ownerProcess = uint.Parse(args[0], CultureInfo.InvariantCulture);
            LocalData.DirectoryPath = args[1];
            demo = args[2] == "demo";
            executable = args[3];
            if (!demo) StorageMigration.Migrate(args.Skip(4).ToArray());
            settings = Settings.Load();
            history = new UsageHistory(Path.Combine(LocalData.DirectoryPath, "history.json"), settings);
            alerts = new AlertEngine(Path.Combine(LocalData.DirectoryPath, "alerts.json"));
            if (demo)
            {
                var now = DateTimeOffset.Now;
                snapshot = new UsageSnapshot {
                    FetchedAt = now, ResetCredits = 2,
                    ResetCreditExpirations = new List<long?> { now.AddDays(7).ToUnixTimeSeconds(), now.AddDays(14).ToUnixTimeSeconds() },
                    FiveHour = new QuotaWindow { Remaining = 60, DurationMinutes = 300, ResetsAt = now.AddHours(3).ToUnixTimeSeconds() },
                    Weekly = new QuotaWindow { Remaining = 80, DurationMinutes = 10080, ResetsAt = now.AddDays(5).ToUnixTimeSeconds() }
                };
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--has-codex")
            {
                try { UsageClient.FindCodex(); return 0; }
                catch (IOException) { return 1; }
                catch (UnauthorizedAccessException) { return 1; }
            }
            if (args.Length < 4) return 2;
            Native.EnableDpi();
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            using (var bridge = new Bridge(args)) bridge.Run();
            return 0;
        }

        void Run()
        {
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                object id = null;
                try
                {
                    var request = json.Deserialize<Dictionary<string, object>>(line);
                    id = request["id"];
                    var result = Execute((string)request["method"], request);
                    Console.WriteLine(json.Serialize(new { id, result }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(json.Serialize(new { id, error = ex.Message }));
                }
            }
        }

        object Execute(string method, Dictionary<string, object> request)
        {
            var now = DateTimeOffset.Now;
            switch (method)
            {
                case "settings": return new { settings, startup = StartupEnabled() };
                case "save": return Save(json.ConvertToType<Dictionary<string, object>>(request["patch"]));
                case "startup":
                    Settings.SetStartup((bool)request["enabled"], executable);
                    return Settings.StartupEnabled;
                case "placement":
                    suppressed = locked || suspended || island.ShouldHide(settings.GameExclusions, ownerProcess);
                    return new { hidden = suppressed, app = island.LastAppName };
                case "glass": return ReadGlass(request);
                case "login-path": return UsageClient.FindCodex();
                case "tick": return Tick(now);
                case "refresh": schedule.Wake(now); return Tick(now);
                case "power":
                    suspended = (bool)request["suspended"];
                    locked = (bool)request["locked"];
                    if (suspended) client.CancelPending();
                    else if (!locked) schedule.Wake(now);
                    return null;
                case "details": return Details(now);
                case "history": return History(now, Convert.ToInt32(request["days"], CultureInfo.InvariantCulture));
                case "clear":
                    history.Clear();
                    if (history.StorageFailed) throw new IOException("Не удалось удалить историю.");
                    return null;
                default: throw new ArgumentException("Неизвестная команда.");
            }
        }

        object ReadGlass(Dictionary<string, object> request)
        {
            if (suspended || locked || suppressed || SystemInformation.HighContrast) return null;

            var bounds = new Rectangle((int)request["x"], (int)request["y"], (int)request["width"], (int)request["height"]);
            if (bounds.Width < 18 || bounds.Width > 2048 || bounds.Height < 18 || bounds.Height > 1024) return null;
            int logicalWidth = (int)request["logicalWidth"];
            if (logicalWidth < 18 || logicalWidth > 1024) return null;
            float scale = bounds.Width / (float)logicalWidth;
            float radius = (bool)request["expanded"] ? 26 : 17;
            var monitor = Screen.FromRectangle(bounds).Bounds;
            // Only sample the narrow perimeter outside the widget. Return the rendered rim, not a desktop image.
            var backdrop = GlassEnvironment.Read(bounds, monitor);
            if (backdrop == null) return null;
            using (var lens = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(lens))
            using (var png = new MemoryStream())
            {
                graphics.ScaleTransform(scale, scale);
                backdrop.DrawEdge(graphics, new RectangleF(0, 0, bounds.Width / scale, bounds.Height / scale),
                    radius, .34f, scale);
                lens.Save(png, ImageFormat.Png);
                return new { image = Convert.ToBase64String(png.ToArray()), bright = backdrop.Bright };
            }
        }

        bool StartupEnabled()
        {
            if (demo) return false;
            try { return Settings.StartupEnabled; }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        object Save(Dictionary<string, object> patch)
        {
            var candidate = json.Deserialize<Settings>(json.Serialize(settings));
            var properties = typeof(Settings).GetProperties().Where(property => property.CanWrite).ToArray();
            foreach (var property in properties)
            {
                object value;
                if (patch.TryGetValue(property.Name, out value))
                    property.SetValue(candidate, json.ConvertToType(value, property.PropertyType), null);
            }
            candidate.Normalize();
            if (!LocalData.Write(Settings.SettingsPath, candidate)) throw new IOException("Не удалось сохранить настройки.");
            bool retentionChanged = candidate.HistoryDays != settings.HistoryDays;
            foreach (var property in properties) property.SetValue(settings, property.GetValue(candidate, null), null);
            if (retentionChanged) history.ApplyRetention();
            pendingAlerts.Clear();
            return settings;
        }

        object Tick(DateTimeOffset now)
        {
            UsageAlert alert = null;
            if (!demo)
            {
                bool connected = NetworkInterface.GetIsNetworkAvailable();
                if (connected && !online) schedule.Wake(now);
                online = connected;
                CompleteReads(now);
                if (!suspended && (!locked || !settings.EfficientPolling))
                {
                    if (log == null && now >= nextLog && (snapshot == null || snapshot.FromSessionLog || status != null || !online))
                    {
                        nextLog = now.AddMinutes(1);
                        log = Task.Run(() => SessionQuota.ReadLatest(now));
                    }
                    if (settings.EfficientPolling && live == null && schedule.ResetDue(snapshot, now)) schedule.Wake(now);
                    if (live == null && schedule.Due(now, online, suspended)) live = client.FetchAsync();
                    if (online && live == null && status == null && snapshot != null && !snapshot.FromSessionLog)
                        foreach (var item in alerts.Evaluate(snapshot, now, settings)) pendingAlerts.Enqueue(item);
                    while (pendingAlerts.Count > 0 && pendingAlerts.Peek().ExpiresAt <= now.ToUnixTimeSeconds()) pendingAlerts.Dequeue();
                    if (online && pendingAlerts.Count > 0 && now >= nextAlert)
                    {
                        alert = pendingAlerts.Dequeue();
                        nextAlert = now.AddSeconds(12);
                    }
                }
                if (now >= nextStatusWrite)
                {
                    nextStatusWrite = now.AddMinutes(1);
                    LocalData.Write(Path.Combine(LocalData.DirectoryPath, "monitor-status.json"), new {
                        CheckedAt = now.ToUnixTimeSeconds(),
                        LastSuccessAt = snapshot == null ? (long?)null : snapshot.FetchedAt.ToUnixTimeSeconds(),
                        HistoryCount = history.Samples.Count, StorageFailed = history.StorageFailed,
                        ActiveRequest = live != null, Phase = client.Stage, CLI = client.ExecutablePath,
                        Suspended = suspended, Locked = locked, Error = status, IslandSuppressed = suppressed
                    });
                }
            }

            return new {
                snapshot = snapshot == null ? null : new {
                    snapshot.FiveHour, snapshot.Weekly, snapshot.Plan, snapshot.ResetCredits,
                    snapshot.FromSessionLog, fetchedAt = snapshot.FetchedAt.ToUnixTimeSeconds(),
                    expiry = ExpiryDisplay.For(snapshot, now)
                },
                busy = live != null, demo, alert, loginRequired,
                status = demo ? null : !online ? "Нет подключения к сети." : status,
                stale = !demo && snapshot != null && (snapshot.FromSessionLog || now - snapshot.FetchedAt > TimeSpan.FromMinutes(5))
            };
        }

        void CompleteReads(DateTimeOffset now)
        {
            if (log != null && log.IsCompleted)
            {
                if (log.Status == TaskStatus.RanToCompletion && log.Result != null &&
                    (snapshot == null || log.Result.FetchedAt > snapshot.FetchedAt)) snapshot = log.Result;
                if (log.IsFaulted) Debug.WriteLine(log.Exception);
                log = null;
            }
            if (live == null || !live.IsCompleted) return;
            bool success = live.Status == TaskStatus.RanToCompletion;
            if (client.ChatGptLoginRequired.HasValue) loginRequired = client.ChatGptLoginRequired.Value;
            if (success)
            {
                snapshot = live.Result;
                status = snapshot.FiveHour == null && snapshot.Weekly == null ? "Для аккаунта нет 5-часового и недельного лимитов." : null;
                if (settings.HistoryEnabled && status == null) history.Record(snapshot);
            }
            else
            {
                var error = live.Exception == null ? null : live.Exception.GetBaseException();
                if (loginRequired) status = "Войдите в аккаунт ChatGPT через Codex CLI, чтобы увидеть лимиты.";
                else if (error is FileNotFoundException) status = "Codex CLI не найден. Установите CLI или укажите CODEX_USAGE_CLI.";
                else if (error is TimeoutException) status = "Codex не ответил вовремя. Виджет повторит запрос.";
                else status = "Не удалось проверить лимиты. Проверьте вход в Codex CLI.";
            }
            live = null;
            schedule.Completed(now, success, settings.EfficientPolling, SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline);
        }

        object Details(DateTimeOffset now)
        {
            if (!settings.HistoryEnabled) return new { enabled = false };
            return new {
                enabled = true, five = history.Forecast(false, snapshot), week = history.Forecast(true, snapshot),
                fiveDetail = history.ForecastDetail(false, snapshot), weekDetail = history.ForecastDetail(true, snapshot),
                fivePattern = history.PatternForecast(false, snapshot), weekPattern = history.PatternForecast(true, snapshot),
                weekDaily = history.WeeklyDailyForecast(snapshot), weekAverage = history.WeeklyDailyText(snapshot, true),
                fiveSpent = history.Consumption(now.AddDays(-1), now, false),
                weekSpent = history.Consumption(now.AddDays(-1), now, true)
            };
        }

        object History(DateTimeOffset now, int days)
        {
            days = Math.Max(1, Math.Min(settings.HistoryDays, days));
            var start = now.AddDays(-days);
            long begin = start.ToUnixTimeSeconds();
            long end = now.ToUnixTimeSeconds();
            return new {
                begin, end, history.StorageFailed, details = Details(now),
                samples = history.Samples.Where(row => row.At >= begin && row.At <= end).ToArray(),
                fiveSpent = history.Consumption(start, now, false), weekSpent = history.Consumption(start, now, true)
            };
        }

        public void Dispose() { client.Dispose(); }
    }
}
