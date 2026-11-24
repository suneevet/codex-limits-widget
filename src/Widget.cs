using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    public sealed partial class Widget : Form
    {
        readonly UsageClient client = new UsageClient();
        readonly Settings settings;
        readonly PollSchedule schedule = new PollSchedule();
        readonly UsageHistory history;
        readonly AlertEngine alerts;
        readonly Queue<UsageAlert> pendingAlerts = new Queue<UsageAlert>();
        DateTimeOffset lastAlert = DateTimeOffset.MinValue;
        DateTimeOffset lastStatusWrite = DateTimeOffset.MinValue;
        bool suspended, sessionLocked, forceRefresh, readingLog;
        DateTimeOffset nextLogRead = DateTimeOffset.MinValue;
        readonly Timer placementTimer = new Timer { Interval = 200 };
        readonly Timer usageTimer = new Timer { Interval = 1000 };
        readonly Timer animationTimer = new Timer { Interval = 33 };
        readonly Stopwatch refreshClock = new Stopwatch();
        readonly HoverDetails details = new HoverDetails();
        readonly NotifyIcon tray;
        readonly Icon appIcon;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        Form settingsWindow;
        UsageSnapshot snapshot;
        string status = Ui.Text("사용량을 불러오는 중…");
        bool busy;
        bool closing;
        bool demo;
        float scale = 1;
        string lastTooltip;
        public static readonly Color Accent = Color.FromArgb(53, 102, 240); // Screenshot: #3566F0
        public const int LogicalWidth = 388;
        public Widget(bool demoMode)
        {
            demo = demoMode;
            settings = Settings.Load();
            history = new UsageHistory(Path.Combine(LocalData.DirectoryPath,"history.json"),settings);
            alerts = new AlertEngine(Path.Combine(LocalData.DirectoryPath,"alerts.json"));
            Text = "Codex Limits Widget";
            appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
            Icon = appIcon;
            FormBorderStyle = FormBorderStyle.None;
            // NOACTIVATE without APPWINDOW hides the taskbar button without an invisible owner.
            ShowInTaskbar = true;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Palette.For(settings.DarkTheme).Background;
            DoubleBuffered = true;
            ClientSize = IslandArt.LogicalSize(0);
            Cursor = Native.SystemHand();
            AccessibleName = "Остаток лимитов Codex";
            BuildMenu();
            details.RefreshRequested += RefreshUsage;
            details.HistoryRequested += OpenHistory;
            ContextMenuStrip = menu;
            tray = new NotifyIcon { Icon = appIcon, Text = "Codex Limits Widget", ContextMenuStrip = menu, Visible = true };
            tray.MouseUp += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RefreshUsage(); };
            placementTimer.Tick += delegate {
                if (DateTimeOffset.Now - lastStatusWrite >= TimeSpan.FromMinutes(1)) SaveMonitorStatus();
                PollIsland();
                UpdateTooltip();
            };
            islandTimer.Tick += delegate { AnimateIsland(); };
            usageTimer.Tick += delegate { MonitorTick(); };
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
            animationTimer.Tick += delegate { if (busy) RequestIslandDraw(); };
            Load += delegate {
                SaveMonitorStatus();
                PollIsland(); placementTimer.Start();
                if (demo) {
                    snapshot = new UsageSnapshot { FetchedAt = DateTimeOffset.Now, ResetCredits = 2,
                        ResetCreditExpirations = new System.Collections.Generic.List<long?> {
                            DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), DateTimeOffset.Now.AddDays(14).ToUnixTimeSeconds() },
                        FiveHour = new QuotaWindow { Remaining = 60, DurationMinutes = 300, ResetsAt = DateTimeOffset.Now.AddHours(3).ToUnixTimeSeconds() },
                        Weekly = new QuotaWindow { Remaining = 80, DurationMinutes = 10080, ResetsAt = DateTimeOffset.Now.AddDays(5).ToUnixTimeSeconds() } };
                    status = Ui.Text("데모 — 실제 계정 사용량이 아닙니다."); UpdateTooltip(); RequestIslandDraw();
                } else { usageTimer.Start(); RefreshUsage(); }
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle &= ~(0x00040000 | 0x00000080); cp.ExStyle |= IslandNative.NoActivateStyle | IslandNative.LayeredStyle; return cp; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
        void BuildMenu()
        {
            menu.Items.Add("Обновить сейчас", null, delegate { RefreshUsage(); });
            menu.Items.Add("Показать виджет", null, delegate { Post(RestoreWidget); });
            menu.Items.Add("Подробнее", null, delegate { ShowDetails(); });
            menu.Items.Add("История расхода", null, delegate {OpenHistory();});
            menu.Items.Add("Настройки", null, delegate { Post(OpenSettings); });
            menu.Items.Add(new ToolStripSeparator());
            AddIslandMenu();
            var startup = new ToolStripMenuItem("Запускать при входе в Windows");
            startup.Click += delegate {
                try { Settings.SetStartup(!Settings.StartupEnabled); }
                catch (Exception ex) { if (!(ex is UnauthorizedAccessException) && !(ex is System.Security.SecurityException) && !(ex is IOException)) throw;
                    MessageBox.Show(Ui.Text("자동 실행 설정을 변경할 수 없습니다."), "GPT"); }
            };
            menu.Items.Add(startup);
            menu.Closed += delegate { StartIslandAnimation(); };
            menu.Opening += delegate { details.Hide(); startup.Checked = Settings.StartupEnabled; };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Закрыть", null, delegate { Close(); });
        }
        void OpenHistory()
        {
            history.ReloadStoredSamples();
            PresentDetails(DetailText());
            details.ShowHistory(history,snapshot,settings,Bounds,scale);
        }
        void OpenSettings()
        {
            details.Hide();
            if(settingsWindow==null || settingsWindow.IsDisposed) {
                settingsWindow=new SettingsDialog(settings,ApplyPreferences,delegate {history.Clear();});
                settingsWindow.FormClosed+=delegate {settingsWindow=null;Post(delegate {Place(true);});};
            }
            ((SettingsDialog)settingsWindow).Present(Bounds);
        }
        async void RefreshUsage()
        {
            if (busy || closing || demo || suspended) return;
            busy = true;
            refreshClock.Restart(); animationTimer.Start();
            status = Ui.Text("사용량을 불러오는 중…"); UpdateTooltip(); RequestIslandDraw();
            SaveMonitorStatus();
            bool success = false;
            try
            {
                await RefreshSessionLog();
                if (closing) return;
                if (!Online) {
                    status = "Нет соединения с Codex.";
                    return;
                }
                var live = await client.FetchAsync();
                if (closing) return;
                snapshot = live;
                success = true;
                if (settings.HistoryEnabled) history.Record(snapshot);
                status = snapshot.FiveHour == null && snapshot.Weekly == null ? Ui.Text("이 계정에서 5시간·주간 한도가 제공되지 않습니다.") : null;
            }
            catch (Exception ex)
            {
                if (ex is FileNotFoundException || ex is IOException || ex is TimeoutException || ex is InvalidDataException)
                    status = ex.Message;
                else status = Ui.Text("사용량 조회를 완료하지 못했습니다. 잠시 후 다시 시도합니다.");
            }
            finally
            {
                busy = false;
                schedule.Completed(DateTimeOffset.Now, success, settings.EfficientPolling, SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline);
                animationTimer.Stop(); refreshClock.Stop();
                if (!closing) { UpdateTooltip(); RequestIslandDraw(); }
                SaveMonitorStatus();
            }
        }
        async Task RefreshSessionLog()
        {
            if (readingLog || closing) return;
            readingLog = true;
            nextLogRead = DateTimeOffset.Now.AddMinutes(1);
            try
            {
                var logged = await Task.Run(() => SessionQuota.ReadLatest(DateTimeOffset.Now));
                if (closing || logged == null || (snapshot != null && logged.FetchedAt <= snapshot.FetchedAt)) return;
                snapshot = logged;
                UpdateTooltip();
                RequestIslandDraw();
            }
            finally { readingLog = false; }
        }
        void SaveMonitorStatus()
        {
            lastStatusWrite=DateTimeOffset.Now;
            LocalData.Write(Path.Combine(LocalData.DirectoryPath,"monitor-status.json"),new {
                CheckedAt=lastStatusWrite.ToUnixTimeSeconds(),
                LastSuccessAt=snapshot==null?(long?)null:snapshot.FetchedAt.ToUnixTimeSeconds(),
                HistoryCount=history.Samples.Count,LastRecordAt=history.Samples.Count==0?(long?)null:history.Samples[history.Samples.Count-1].At,
                StorageFailed=history.StorageFailed,ActiveRequest=busy,Phase=client.Stage,CLI=client.ExecutablePath,
                Suspended=suspended,Locked=sessionLocked,Error=status,
                IslandVisible=IsHandleCreated && Native.IsWindowVisible(Handle),
                IslandSuppressed=islandHidden,IslandTheme=Theme.ToString()
            });
        }
        bool Online
        {
            get { try { return NetworkInterface.GetIsNetworkAvailable(); } catch (NetworkInformationException) { return false; } }
        }
        void ApplyPreferences()
        {
            history.ApplyRetention();
            BackColor = Palette.For(settings.DarkTheme).Background; pendingAlerts.Clear();
            details.Hide(); RequestIslandDraw(); schedule.Wake(DateTimeOffset.Now);
        }
        void Post(Action action)
        {
            if (closing || !IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }
        void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            Post(delegate {
                if (e.Mode == PowerModes.Suspend) { suspended = true; client.CancelPending(); }
                else if (e.Mode == PowerModes.Resume) { suspended = false; forceRefresh = true; }
            });
        }
        void SessionChanged(object sender, SessionSwitchEventArgs e)
        {
            Post(delegate {
                if (e.Reason == SessionSwitchReason.SessionLock) sessionLocked = true;
                else if (e.Reason == SessionSwitchReason.SessionUnlock) { sessionLocked = false; forceRefresh = true; }
            });
        }
        void NetworkChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            Post(delegate { if (e.IsAvailable) forceRefresh = true; else { status = Ui.Text("인터넷 연결을 기다리는 중입니다."); UpdateTooltip(); RequestIslandDraw(); } });
        }
        async void MonitorTick()
        {
            if (closing || demo || suspended || (settings.EfficientPolling && sessionLocked)) return;
            DateTimeOffset now = DateTimeOffset.Now;
            bool online = Online;
            if (!busy) {
                if (forceRefresh) { forceRefresh = false; schedule.Wake(now); }
                if (settings.EfficientPolling && schedule.ResetDue(snapshot, now)) schedule.Wake(now);
                if (schedule.Due(now, true, suspended)) RefreshUsage();
                else if ((snapshot == null || snapshot.FromSessionLog || status != null) && now >= nextLogRead) await RefreshSessionLog();
            }
            if (online && status == null && !busy) {
                foreach (var notification in alerts.Evaluate(snapshot,now,settings)) pendingAlerts.Enqueue(notification);
            }
            while (pendingAlerts.Count > 0 && pendingAlerts.Peek().ExpiresAt <= now.ToUnixTimeSeconds()) pendingAlerts.Dequeue();
            if (online && pendingAlerts.Count > 0 && now-lastAlert >= TimeSpan.FromSeconds(12)) {
                var notification=pendingAlerts.Dequeue(); lastAlert=now;
                tray.ShowBalloonTip(10000,notification.Title,notification.Message,ToolTipIcon.Info);
            }
        }
        void RestoreWidget()
        {
            if (closing) return;
            details.Hide();
            PollIsland();
            RequestIslandDraw();
        }
        bool IsStale { get { return snapshot != null && (DateTimeOffset.Now - snapshot.FetchedAt).TotalMinutes > 5; } }
        public string DetailText()
        {
            string text = "Codex · остаток лимитов\n";
            if (snapshot != null && snapshot.FromSessionLog) text += "Последний снимок из локального журнала Codex.\n";
            if (demo) text += Ui.Text("데모 — 실제 계정 사용량이 아닙니다.\n");
            if (status != null && !demo) text += status + "\n";
            if (snapshot == null) return text + Ui.Text("Codex 앱에서 ChatGPT 계정으로 로그인해 주세요.");
            text += Describe(Ui.Text("5시간"), snapshot.FiveHour) + "\n" + Describe(Ui.Text("주간"), snapshot.Weekly);
            text += Ui.Text("\n초기화: ") + (snapshot.ResetCredits.HasValue ? snapshot.ResetCredits.Value + Ui.Text("개") : Ui.Text("제공되지 않음"));
            if (snapshot.ResetCreditExpirations != null && snapshot.ResetCreditExpirations.Count > 0)
            {
                int number = 0;
                foreach (var expiry in snapshot.ResetCreditExpirations.OrderBy(x => x ?? long.MaxValue))
                {
                    text += "\n  " + (++number) + ". " + (expiry.HasValue ? Ui.Text("만료 ") +
                        ExpiryDisplay.InKorea(DateTimeOffset.FromUnixTimeSeconds(expiry.Value)).ToString("yyyy/MM/dd HH:mm") + Ui.Text(" (한국 시간)") : Ui.Text("만료 시각 미제공"));
                }
                if (snapshot.ResetCredits.HasValue && snapshot.ResetCreditExpirations.Count < snapshot.ResetCredits.Value)
                    text += Ui.Text("\n  일부 초기화 항목의 만료 정보만 제공되었습니다.");
            }
            else if (snapshot.ResetCredits.GetValueOrDefault() > 0) text += Ui.Text("\n만료 정보: 제공되지 않음");
            text += Ui.Text("\n마지막 조회: ") + snapshot.FetchedAt.ToLocalTime().ToString("MM/dd HH:mm:ss");
            if (IsStale || (status != null && !busy && !demo)) text += Ui.Text("\n마지막 조회 값입니다. 현재 사용량과 다를 수 있습니다.");
            text += "\n\nЩелчок обновляет лимиты. Правая кнопка открывает настройки.";
            return text;
        }
        static string Describe(string label, QuotaWindow window)
        {
            if (window == null) return label + Ui.Text(": 제공되지 않음");
            string text = label + ": " + window.PercentText + Ui.Text(" 남음");
            if (window.ResetsAt.HasValue)
            {
                try { text += Ui.Text(" · 초기화 ") + DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt.Value).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm") + " (МСК)"; }
                catch (ArgumentOutOfRangeException) { }
            }
            return text;
        }
        public static string FormatResetTime(QuotaWindow window, bool full = false) { return QuotaText.FormatResetTime(window, full); }
        public static string CardCaption(UsageSnapshot data, bool stale, bool demo)
        {
            if (demo) return "Пример";
            if (data == null) return "Нет данных";
            if (data.FromSessionLog) return "Из журнала";
            return stale ? "Пред. данные" : "Остаток лимита";
        }
        static QuotaWindow VisibleQuota(QuotaWindow quota, DateTimeOffset now)
        {
            if (quota != null && quota.ResetsAt.HasValue && quota.ResetsAt.Value <= now.ToUnixTimeSeconds()) return null;
            return quota;
        }
        public static string CardCountdown(QuotaWindow window, DateTimeOffset now)
        {
            if (window == null || !window.ResetsAt.HasValue) return "—";
            long reset = window.ResetsAt.Value;
            if (reset < -62135596800L || reset > 253402300799L) return "—";
            long seconds = reset - now.ToUnixTimeSeconds();
            if (seconds <= 0) return "ожидание";
            long minutes = (seconds + 59) / 60;
            if (minutes >= 1440) return (minutes / 1440) + " д " + (minutes % 1440 / 60) + " ч";
            if (minutes >= 60) return (minutes / 60) + " ч " + (minutes % 60) + " мин";
            return minutes + " мин";
        }
        public static string FormatRemainingTime(QuotaWindow window, DateTimeOffset now) { return QuotaText.FormatRemainingTime(window, now); }
        public static string RemainingAmount(string text) { return QuotaText.RemainingAmount(text); }
        void UpdateTooltip()
        {
            string text = DetailText();
            if (lastTooltip != text) { lastTooltip = text; if (details.Visible) PresentDetails(text); }
            string five = snapshot == null || snapshot.FiveHour == null ? "—" : snapshot.FiveHour.PercentText;
            string week = snapshot == null || snapshot.Weekly == null ? "—" : snapshot.Weekly.PercentText;
            string trayText = Ui.Text("GPT · 5시간 ") + five + Ui.Text(" · 주간 ") + week;
            tray.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;
            AccessibleDescription = text;
        }
        void PresentDetails(string text) { details.Present(snapshot, status, busy, IsStale || (snapshot != null && snapshot.FromSessionLog), demo, text, Bounds, scale, settings, settings.HistoryEnabled ? history : null); }
        void ShowDetails() { if (!closing) PresentDetails(DetailText()); }
        public static void PaintCard(Graphics graphics, float dpiScale, Size size, UsageSnapshot data, bool loading, bool stale, bool demoMode, float rotation = 0, Settings preferences = null)
        {
            var s = preferences ?? new Settings();
            var p = Palette.For(s.DarkTheme);
            Color valueInk = stale ? p.Muted : s.DarkTheme ? Color.FromArgb(143, 180, 255) : Accent;
            var now = DateTimeOffset.Now;
            var layout = WidgetLayout.Create(data, stale, demoMode, s, now);
            var five = VisibleQuota(data == null ? null : data.FiveHour, now);
            var week = VisibleQuota(data == null ? null : data.Weekly, now);
            int titleWidth = Ui.MeasureWidgetTitle(s.FontSize + 1);
            int titleStart = ((layout.First >= 0 ? layout.First : layout.Width) - (13 + titleWidth)) / 2;
            using (var artwork = new Bitmap(size.Width * 3, size.Height * 3))
            using (var canvas = Graphics.FromImage(artwork))
            {
                canvas.Clear(p.Background); canvas.SmoothingMode = SmoothingMode.HighQuality; canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
                canvas.ScaleTransform(dpiScale * 3, dpiScale * 3);
                using (var border = new Pen(p.Border))
                using (var path = Rounded(new RectangleF(.5f, .5f, size.Width / dpiScale - 1, size.Height / dpiScale - 1), 9)) canvas.DrawPath(border, path);
                if (s.ShowReset) {
                    Color indicator = stale ? Color.FromArgb(213,146,37) : Accent;
                    if (loading) PaintRefresh(canvas, rotation, indicator, titleStart + 3);
                    else using (var dot = new SolidBrush(indicator)) canvas.FillEllipse(dot, titleStart, 10, 6, 6);
                }
                using (var divider = new Pen(p.Divider)) {
                    if (layout.First >= 0) canvas.DrawLine(divider, layout.First, 8, layout.First, 32);
                    if (layout.Second >= 0) canvas.DrawLine(divider, layout.Second, 8, layout.Second, 32);
                }
                if (s.ShowUsage) {
                    PaintTrack(canvas, 0, five, stale, layout.Bar, s, p);
                    PaintTrack(canvas, 20, week, stale, layout.Bar, s, p);
                }
                var saved = graphics.Save(); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(artwork, new Rectangle(Point.Empty, size), 0, 0, artwork.Width, artwork.Height, GraphicsUnit.Pixel); graphics.Restore(saved);
            }
            var textState = graphics.Save(); graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var captionFont = new Font("Malgun Gothic", (s.FontSize - 1) * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var percentFont = new Font("Segoe UI Semibold", s.FontSize * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var body = new Font("Malgun Gothic", s.FontSize * dpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                if (s.ShowReset) {
                    Ui.DrawWidgetTitle(graphics, dpiScale, s.FontSize + 1, p.Ink, p.Background, new Rectangle(titleStart + 13,2,titleWidth + 4,20));
                    string caption = loading && data == null ? "Обновление…" : CardCaption(data, stale, demoMode);
                    int end = layout.First >= 0 ? layout.First - 8 : layout.Width - 10;
                    DrawText(graphics, caption, captionFont, p.Ink, dpiScale, new Rectangle(8,21,end - 8,18), false,p.Background);
                }
                if (s.ShowUsage) {
                    DrawText(graphics,"5 часов",body,p.Ink,dpiScale,new Rectangle(layout.Label,0,layout.Bar-layout.Label-4,20),false,p.Background,true);
                    DrawText(graphics,"Неделя",body,p.Ink,dpiScale,new Rectangle(layout.Label,20,layout.Bar-layout.Label-4,20),false,p.Background,true);
                    int width = layout.PercentWidth;
                    DrawText(graphics,five == null ? "—" : five.PercentText,percentFont,valueInk,dpiScale,new Rectangle(layout.Percent,0,width,20),false,p.Background);
                    DrawText(graphics,week == null ? "—" : week.PercentText,percentFont,valueInk,dpiScale,new Rectangle(layout.Percent,20,width,20),false,p.Background);
                }
                if (s.ShowRemaining) {
                    DrawText(graphics,layout.FiveText,body,valueInk,dpiScale,new Rectangle(layout.Time,0,layout.TimeWidth,20),false,p.Background,true);
                    DrawText(graphics,layout.WeekText,body,valueInk,dpiScale,new Rectangle(layout.Time,20,layout.TimeWidth,20),false,p.Background,true);
                    if (layout.CommonVisible) DrawText(graphics,"до сброса",body,p.Ink,dpiScale,new Rectangle(layout.Common,10,WidgetLayout.Measure("до сброса",s.FontSize),20),false,p.Background);
                }
            }
            graphics.Restore(textState);
        }
        static void DrawText(Graphics g, string text, Font font, Color color, float scale, Rectangle rect, bool right, Color background, bool center = false)
        {
            var pixels = Rectangle.FromLTRB((int)Math.Round(rect.Left*scale),(int)Math.Round(rect.Top*scale),(int)Math.Round(rect.Right*scale),(int)Math.Round(rect.Bottom*scale));
            TextRenderer.DrawText(g,text,font,pixels,color,background,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|
                (right ? TextFormatFlags.Right : center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
        }
        static void PaintTrack(Graphics g, float y, QuotaWindow window, bool stale, int x, Settings s, Palette p)
        {
            var bar = new RectangleF(x,y+10-s.BarThickness/2f,s.BarWidth,s.BarThickness);
            float radius = s.BarThickness/2f;
            using (var track = new SolidBrush(p.Track)) using (var path = Rounded(bar,radius)) g.FillPath(track,path);
            if (window != null && window.Remaining > 0) {
                Color color = stale ? Color.FromArgb(155,165,181) : Accent;
                float fillWidth = (float)(bar.Width*window.Remaining/100);
                using (var fill = new LinearGradientBrush(bar,color,stale ? color : Color.FromArgb(87,137,255),LinearGradientMode.Horizontal))
                using (var path = Rounded(bar,radius)) {
                    var saved=g.Save();g.SetClip(path);
                    if (fillWidth >= bar.Height) using (var fillPath=Rounded(new RectangleF(bar.X,bar.Y,fillWidth,bar.Height),radius)) g.FillPath(fill,fillPath);
                    else g.FillRectangle(fill,bar.X,bar.Y,fillWidth,bar.Height);
                    g.Restore(saved);
                }
            }
            using (var outline=new Pen(stale ? Color.FromArgb(170,182,199) : Color.FromArgb(142,171,239),.75f))
            using (var path=Rounded(bar,radius)) g.DrawPath(outline,path);
        }
        static void PaintRefresh(Graphics g, float rotation, Color color, float centerX)
        {
            var saved = g.Save();
            g.TranslateTransform(centerX, 13); g.RotateTransform(rotation);
            using (var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(color))
            {
                foreach (float start in new[] { 10f, 190f })
                {
                    g.DrawArc(pen, -5, -5, 10, 10, start, 135);
                    double angle = (start + 135) * Math.PI / 180;
                    var tip = new PointF((float)(5 * Math.Cos(angle)), (float)(5 * Math.Sin(angle)));
                    var tangent = new PointF(-(float)Math.Sin(angle), (float)Math.Cos(angle));
                    var radial = new PointF((float)Math.Cos(angle), (float)Math.Sin(angle));
                    g.FillPolygon(brush, new[] { new PointF(tip.X + tangent.X * 1.7f, tip.Y + tangent.Y * 1.7f),
                        new PointF(tip.X - tangent.X * 2.1f + radial.X * 1.8f, tip.Y - tangent.Y * 2.1f + radial.Y * 1.8f),
                        new PointF(tip.X - tangent.X * 2.1f - radial.X * 1.8f, tip.Y - tangent.Y * 2.1f - radial.Y * 1.8f) });
                }
            }
            g.Restore(saved);
        }
        static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            float diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closing = true;
            if(settingsWindow!=null&&!settingsWindow.IsDisposed)settingsWindow.Close();
            placementTimer.Stop(); usageTimer.Stop(); animationTimer.Stop(); islandTimer.Stop();
            SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
            client.Dispose(); tray.Visible = false; tray.Dispose(); appIcon.Dispose(); details.Dispose();
            placementTimer.Dispose(); usageTimer.Dispose(); animationTimer.Dispose(); islandTimer.Dispose(); menu.Dispose();
            base.OnFormClosed(e);
        }
    }
}
