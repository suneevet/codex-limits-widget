using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    public sealed partial class Widget
    {
        readonly IslandNative islandNative = new IslandNative();
        readonly Timer islandTimer = new Timer { Interval = 15 };
        readonly Stopwatch islandClock = Stopwatch.StartNew();
        double lastIslandFrame, collapseAt;
        float islandProgress;
        bool islandHovered, islandHidden, islandDirty = true;
        Rectangle lastIslandBounds;
        GlassEnvironment environment;
        double nextSample;
        float glassFocus = .34f;
        string islandDataKey;

        IslandTheme Theme
        {
            get
            {
                if (settings.GlassIsland) return IslandTheme.Glass;
                return settings.DarkTheme ? IslandTheme.Dark : IslandTheme.Light;
            }
        }

        void AddIslandMenu()
        {
            var themes = new ToolStripMenuItem("Тема");
            foreach (IslandTheme theme in Enum.GetValues(typeof(IslandTheme)))
            {
                var selected = theme;
                string label = theme == IslandTheme.Light ? "Светлая" : theme == IslandTheme.Dark ? "Тёмная" : "Liquid Glass";
                var item = new ToolStripMenuItem(label);
                themes.DropDownItems.Add(item);
                item.Click += delegate {
                    settings.DarkTheme = selected != IslandTheme.Light;
                    settings.GlassIsland = selected == IslandTheme.Glass;
                    settings.Save();
                    ApplyPreferences();
                };
                themes.DropDownOpening += delegate { item.Checked = Theme == selected; };
            }
            menu.Items.Add(themes);

            var games = new ToolStripMenuItem("Исключения для игр");
            games.DropDownOpening += delegate {
                games.DropDownItems.Clear();
                string app = islandNative.LastAppName;
                string[] excluded = settings.GameExclusions ?? new string[0];
                bool alreadyAdded = app != null && excluded.Contains(app, StringComparer.OrdinalIgnoreCase);
                var add = new ToolStripMenuItem(app == null ? "Переключитесь на нужную игру" : "Скрывать в " + app);
                add.Enabled = app != null && !alreadyAdded;
                add.Click += delegate {
                    settings.GameExclusions = excluded.Concat(new[] { app }).ToArray();
                    settings.Save();
                };
                games.DropDownItems.Add(add);
                if (excluded.Length > 0) games.DropDownItems.Add(new ToolStripSeparator());
                foreach (string name in excluded)
                {
                    string remove = name;
                    games.DropDownItems.Add("Убрать " + name, null, delegate {
                        settings.GameExclusions = excluded.Where(item => !string.Equals(item, remove, StringComparison.OrdinalIgnoreCase)).ToArray();
                        settings.Save();
                    });
                }
            };
            games.DropDownItems.Add(new ToolStripMenuItem());
            menu.Items.Add(games);
        }

        void PollIsland()
        {
            if (closing || !IsHandleCreated) return;
            bool suppress = sessionLocked || suspended || islandNative.ShouldHide(settings.GameExclusions);
            if (suppress)
            {
                if (!islandHidden)
                {
                    islandHidden = true;
                    islandProgress = 0;
                    islandHovered = false;
                    islandTimer.Stop();
                    details.Hide();
                    Hide();
                    SaveMonitorStatus();
                }
                return;
            }
            if (islandHidden)
            {
                islandHidden = false;
                islandDirty = true;
                Show();
                SaveMonitorStatus();
            }
            var now = DateTimeOffset.Now;
            string next = CardCountdown(snapshot == null ? null : snapshot.FiveHour, now) + "|" +
                CardCountdown(snapshot == null ? null : snapshot.Weekly, now) + "|" + IsStale;
            if (next != islandDataKey)
            {
                islandDataKey = next;
                islandDirty = true;
            }
            if (!menu.Visible && !islandHovered && islandProgress > 0 && !islandTimer.Enabled) StartIslandAnimation();
            Place();
            IslandNative.KeepTopmost(Handle);
        }

        void RequestIslandDraw()
        {
            islandDirty = true;
            if (!islandTimer.Enabled) Place();
        }

        void Place(bool force = false)
        {
            if (closing || islandHidden || !IsHandleCreated) return;

            var screen = Screen.PrimaryScreen;
            if (screen == null) return;
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            scale = Native.Dpi(taskbar == IntPtr.Zero ? Handle : taskbar);
            var size = IslandArt.LogicalSize(islandProgress);
            int width = Math.Max(1, (int)Math.Round(size.Width * scale));
            int height = Math.Max(1, (int)Math.Round(size.Height * scale));
            var area = screen.WorkingArea;
            var bounds = new Rectangle(area.Left + (area.Width - width) / 2,
                area.Top + Math.Max(2, (int)Math.Round(10 * scale)), width, height);
            bool glassTransparency = !SystemInformation.HighContrast;
            if (Theme == IslandTheme.Glass && !sessionLocked && !suspended && glassTransparency)
            {
                // Sample outside the settled capsule only. A shrinking frame may still cover its new perimeter.
                double now = islandClock.Elapsed.TotalMilliseconds;
                if (bounds == lastIslandBounds && now >= nextSample && !menu.Visible)
                {
                    environment = GlassEnvironment.Read(bounds, screen.Bounds);
                    nextSample = now + 350;
                    islandDirty = true;
                }
                var pointer = Cursor.Position;
                float target = bounds.Contains(pointer) ? (pointer.X - bounds.Left) / (float)width : .34f;
                if (Math.Abs(target - glassFocus) > .005f)
                {
                    glassFocus += (target - glassFocus) * .55f;
                    islandDirty = true;
                }
            }
            if (!force && !islandDirty && bounds == lastIslandBounds) return;

            using (var art = IslandArt.Render(islandProgress, scale, Theme, snapshot, busy,
                IsStale || (status != null && !busy && !demo), demo, DateTimeOffset.Now, refreshClock.Elapsed.TotalSeconds, glassTransparency, glassFocus, environment, settings.AccentRgb))
            {
                if (IslandNative.Present(Handle, bounds.Location, art))
                {
                    lastIslandBounds = bounds;
                    islandDirty = false;
                }
            }
        }

        void StartIslandAnimation()
        {
            if (islandHidden || closing || islandTimer.Enabled) return;
            lastIslandFrame = islandClock.Elapsed.TotalMilliseconds;
            islandTimer.Start();
        }

        void AnimateIsland()
        {
            double now = islandClock.Elapsed.TotalMilliseconds;
            double elapsed = Math.Min(50, now - lastIslandFrame);
            lastIslandFrame = now;
            float before = islandProgress;
            bool open = islandHovered || (menu.Visible && islandProgress > 0);
            if (open) islandProgress = Math.Min(1f, islandProgress + (float)(elapsed / 320));
            else if (now >= collapseAt) islandProgress = Math.Max(0f, islandProgress - (float)(elapsed / 265));
            if (islandProgress != before) islandDirty = true;
            Place();
            if ((open && islandProgress == 1) || (!open && islandProgress == 0)) islandTimer.Stop();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            islandHovered = true;
            StartIslandAnimation();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            islandHovered = false;
            collapseAt = islandClock.Elapsed.TotalMilliseconds + 155;
            StartIslandAnimation();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) RefreshUsage();
        }
    }
}
