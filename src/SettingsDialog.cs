using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    public sealed class SettingsDialog : Form
    {
        readonly bool darkCaption;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!darkCaption) return;
            int dark = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
        }

        public void Present(Rectangle anchor)
        {
            // 메뉴가 닫힌 뒤, 현재 작업표시줄이 있는 화면 안에서 독립된 설정 창을 활성화합니다.
            StartPosition=FormStartPosition.Manual;
            WindowState=FormWindowState.Normal;
            TopMost=true;
            Bounds=VisibleBounds(Size,Screen.FromRectangle(anchor).WorkingArea);
            Show();
            Bounds=VisibleBounds(Size,Screen.FromRectangle(anchor).WorkingArea);
            BringToFront(); Activate();
        }
        internal static Rectangle VisibleBounds(Size size,Rectangle area)
        {
            int width=Math.Min(size.Width,area.Width),height=Math.Min(size.Height,area.Height);
            return new Rectangle(area.Left+(area.Width-width)/2,area.Top+(area.Height-height)/2,width,height);
        }
        public SettingsDialog(Settings preferences, Action apply, Action clearHistory)
        {
            darkCaption = preferences.DarkTheme && !SystemInformation.HighContrast;
            Text = "Codex Limits — Настройки";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Segoe UI", 10); ClientSize = new Size(620, Math.Max(450,Math.Min(690,Screen.PrimaryScreen.WorkingArea.Height-100))); AutoScaleMode = AutoScaleMode.Dpi;
            var palette=Palette.For(preferences.DarkTheme);
            if (SystemInformation.HighContrast) palette = new Palette { Background = SystemColors.Window, Card = SystemColors.Window, Track = SystemColors.Highlight, Ink = SystemColors.WindowText, Muted = SystemColors.WindowText, Border = SystemColors.WindowText };
            BackColor=palette.Background; ForeColor=palette.Ink;
            var shell=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute,113));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
            Controls.Add(shell);
            var heading=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            heading.Controls.Add(new Label {Text="Настройки",Font=new Font("Segoe UI",23,FontStyle.Bold),AutoSize=true,Location=new Point(24,14)});
            var navigation = new FlowLayoutPanel { Location = new Point(22, 66), Size = new Size(500, 40), WrapContents = false };
            heading.Controls.Add(navigation);
            shell.Controls.Add(heading,0,0);
            var scroll=new Panel {Name="SettingsScroll",Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty,Padding=new Padding(10,0,10,0),AutoScrollMargin=new Size(0,16)};
            shell.Controls.Add(scroll,0,1);
            var stack=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Padding=new Padding(4,0,4,12)};
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); scroll.Controls.Add(stack);
            stack.SizeChanged+=delegate {scroll.AutoScrollMinSize=new Size(0,stack.Height+16);};
            Action fitCards=delegate {
                stack.MaximumSize=new Size(Math.Max(180,scroll.ClientSize.Width-scroll.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth),0);
                foreach(Control card in stack.Controls)card.MaximumSize=new Size(Math.Max(160,stack.MaximumSize.Width-stack.Padding.Horizontal-card.Margin.Horizontal),0);
            };
            scroll.ClientSizeChanged+=delegate {fitCards();};
            var table = Section(stack, "Внешний вид", palette);
            var appearance = table;
            int selectedTheme = preferences.GlassIsland ? 2 : preferences.DarkTheme ? 1 : 0;
            var themeOptions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
                WrapContents = true, Margin = new Padding(0, 1, 0, 3) };
            var themeButtons = new Button[3];
            string[] themeNames = { "Светлая", "Тёмная", "Liquid Glass" };
            Action refreshTheme = delegate { };
            for (int i = 0; i < themeButtons.Length; i++)
            {
                int index = i;
                var button = StyledButton(themeNames[i], palette.Background, palette.Ink, palette.Border);
                button.Size = new Size(i == 2 ? 106 : 90, 32);
                button.Click += delegate { selectedTheme = index; refreshTheme(); };
                themeButtons[i] = button;
                themeOptions.Controls.Add(button);
            }
            AddWide(table, themeOptions);
            var accent = new AccentPicker(palette, preferences.AccentRgb);
            AddWide(table, new Label { Text = "Цвет акцента", AutoSize = true,
                Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(3, 13, 3, 6) });
            AddWide(table, accent);
            AddWide(table, new Label { Text = "Предпросмотр", AutoSize = true, ForeColor = Color.DimGray,
                Margin = new Padding(3, 12, 3, 4) });
            var preview = new Panel { MinimumSize = new Size(0, 148), MaximumSize = new Size(0, 156), Height = 148, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 4),
                BackColor = palette.Background, AccessibleName = "Предпросмотр акцента с демонстрационными данными" };
            preview.Paint += delegate(object sender, PaintEventArgs e) {
                var now = DateTimeOffset.Now;
                var demo = new UsageSnapshot {
                    FiveHour = new QuotaWindow { Remaining = 72, ResetsAt = now.AddHours(2).ToUnixTimeSeconds() },
                    Weekly = new QuotaWindow { Remaining = 48, ResetsAt = now.AddDays(3).ToUnixTimeSeconds() }
                };
                float scale = Math.Min(e.Graphics.DpiX / 96f, (preview.Width - 22f) / IslandArt.LogicalSize(1).Width);
                scale = Math.Min(scale, (preview.Height - 16f) / IslandArt.LogicalSize(1).Height);
                if (scale <= 0) return;
                using (var image = IslandArt.Render(1, scale, (IslandTheme)selectedTheme, demo, false, false, false,
                    now, 0, translucentGlass: false, accentRgb: accent.Value))
                    e.Graphics.DrawImageUnscaled(image, (preview.Width - image.Width) / 2, (preview.Height - image.Height) / 2);
            };
            accent.Changed += delegate { refreshTheme(); };
            refreshTheme = delegate {
                for (int i = 0; i < themeButtons.Length; i++)
                    StyleButton(themeButtons[i], i == selectedTheme ? palette.Track : palette.Background, palette.Ink,
                        i == selectedTheme ? IslandArt.AccentColor(!preferences.DarkTheme, accent.Value) : palette.Border);
                preview.Invalidate();
            };
            refreshTheme();
            AddWide(table, preview);
            table = Section(stack, "Отображение данных", palette);
            var display = table;
            var font = Number(preferences.FontSize, 11, 14); Add(table, "Размер текста", font);
            var width = Number(preferences.BarWidth, 50, 110); Add(table, "Длина полосы", width);
            var thickness = Number(preferences.BarThickness, 3, 7); Add(table, "Толщина полосы", thickness);
            var reset = Toggle("Показывать кредиты сброса", preferences.ShowReset); AddWide(table, reset);
            var usage = Toggle("Показывать полосы лимитов", preferences.ShowUsage); AddWide(table, usage);
            var remaining = Toggle("Показывать время до сброса", preferences.ShowRemaining); AddWide(table, remaining);
            table=Section(stack,"Уведомления о лимитах",palette);
            var notifications = table;
            var lowFive = Toggle("Мало осталось от 5-часового лимита", preferences.LowQuotaAlert&&preferences.FiveHourLowAlert); AddWide(table, lowFive);
            var lowFiveValue=Number(preferences.FiveHourLowPercent,1,100);Add(table,"Порог остатка, %",lowFiveValue);
            var lowWeek = Toggle("Мало осталось от недельного лимита", preferences.LowQuotaAlert&&preferences.WeeklyLowAlert); AddWide(table, lowWeek);
            var lowWeekValue=Number(preferences.WeeklyLowPercent,1,100);Add(table,"Порог остатка, %",lowWeekValue);
            var hour = Toggle("Напомнить о сбросе 5-часового лимита", preferences.FiveHourResetAlert); AddWide(table, hour);
            var hourValue=Number(preferences.FiveHourResetMinutes,1,300);Add(table,"За сколько минут предупредить",hourValue);
            var day = Toggle("Напомнить о сбросе недельного лимита", preferences.WeeklyResetAlert); AddWide(table, day);
            var weekMode=Choice(new object[]{"По дню и времени (KST)","До сброса"},preferences.WeeklyResetMode);Add(table,"Время напоминания",weekMode);
            var weekDays=Number(preferences.WeeklyReminderDays,0,7);Add(table,"За сколько дней предупредить",weekDays);
            var weekClock=new DateTimePicker {Format=DateTimePickerFormat.Custom,CustomFormat="HH:mm",ShowUpDown=true,Dock=DockStyle.Fill,Value=DateTime.Today.AddHours(preferences.WeeklyReminderHour).AddMinutes(preferences.WeeklyReminderMinute)};Add(table,"Час напоминания",weekClock);
            var weekMinutes=Number(preferences.WeeklyResetMinutes,1,10080);Add(table,"За сколько минут предупредить",weekMinutes);
            Action updateAlertInputs=delegate {lowFiveValue.Enabled=lowFive.Checked;lowWeekValue.Enabled=lowWeek.Checked;hourValue.Enabled=hour.Checked;weekMode.Enabled=day.Checked;weekDays.Enabled=weekClock.Enabled=day.Checked&&weekMode.SelectedIndex==0;weekMinutes.Enabled=day.Checked&&weekMode.SelectedIndex==1;};
            lowFive.CheckedChanged+=delegate {updateAlertInputs();};lowWeek.CheckedChanged+=delegate {updateAlertInputs();};hour.CheckedChanged+=delegate {updateAlertInputs();};day.CheckedChanged+=delegate {updateAlertInputs();};weekMode.SelectedIndexChanged+=delegate {updateAlertInputs();};updateAlertInputs();
            table=Section(stack,"История и обновление",palette);
            var historyPage = table;
            var efficient = Toggle("Адаптировать частоту обновления", preferences.EfficientPolling); AddWide(table, efficient);
            var history = Toggle("Сохранять историю на этом ПК", preferences.HistoryEnabled); AddWide(table, history);
            int[] retentionDays = {30,90,180,365}, forecastDays = {1,3,7,14};
            var retention = Choice(new object[] {"30 дней","90 дней","180 дней","365 дней"}, Array.IndexOf(retentionDays,preferences.HistoryDays));
            Add(table,"Хранить историю",retention);
            var forecast = Choice(new object[] {"1 день","3 дня","7 дней",Ui.Text("14일")}, Array.IndexOf(forecastDays,preferences.ForecastDays));
            Add(table,"Период для прогноза",forecast);
            AddWide(table,new Label {Text="При сокращении срока хранения старые записи будут удалены.",AutoSize=true,MaximumSize=new Size(410,50),ForeColor=Color.DimGray});
            AddWide(table, new Label { Text = "Windows управляет показом уведомлений.", AutoSize = true, MaximumSize = new Size(370, 50), ForeColor = Color.DimGray, Margin = new Padding(3, 8, 3, 8) });
            var delete = new Button {Name="HistoryDelete",Text = "Удалить историю", AutoSize = true,MinimumSize=new Size(160,34),Anchor=AnchorStyles.Left};
            delete.Click += delegate {
                if (MessageBox.Show(this, "Удалить историю расхода на этом ПК?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) clearHistory();
            };
            var buttons = new TableLayoutPanel {Name="SettingsActions",ColumnCount=3,RowCount=1,Padding=new Padding(22,12,22,12),Dock=DockStyle.Fill,Margin=Padding.Empty};
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,88));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,88));
            var save = new Button { Text = "Сохранить", Dock=DockStyle.Fill };
            var cancel = new Button { Text = "Отмена", Dock=DockStyle.Fill, DialogResult = DialogResult.Cancel };
            cancel.Click+=delegate {Close();};
            save.Click += delegate {
                if (!reset.Checked && !usage.Checked && !remaining.Checked) { MessageBox.Show(this, "Выберите хотя бы один элемент для отображения.", Text); return; }
                int chosenDays=retentionDays[retention.SelectedIndex];
                if (chosenDays < preferences.HistoryDays && MessageBox.Show(this,"Записи старше выбранного срока будут удалены. Сохранить?",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
                if (!accent.IsValid) { MessageBox.Show(this, "Введите корректный HEX-цвет или сбросьте акцент.", Text); return; }
                preferences.AccentRgb = accent.Value;
                preferences.GlassIsland = selectedTheme == 2;
                preferences.DarkTheme = selectedTheme != 0; preferences.FontSize = (int)font.Value;
                preferences.BarWidth = (int)width.Value; preferences.BarThickness = (int)thickness.Value;
                preferences.ShowReset = reset.Checked; preferences.ShowUsage = usage.Checked; preferences.ShowRemaining = remaining.Checked;
                preferences.LowQuotaAlert = lowFive.Checked||lowWeek.Checked;preferences.FiveHourLowAlert=lowFive.Checked;preferences.WeeklyLowAlert=lowWeek.Checked;
                preferences.FiveHourLowPercent=(int)lowFiveValue.Value;preferences.WeeklyLowPercent=(int)lowWeekValue.Value;
                preferences.FiveHourResetAlert = hour.Checked; preferences.WeeklyResetAlert = day.Checked;
                preferences.FiveHourResetMinutes=(int)hourValue.Value;preferences.WeeklyResetMinutes=(int)weekMinutes.Value;preferences.WeeklyResetMode=weekMode.SelectedIndex;
                preferences.WeeklyReminderDays=(int)weekDays.Value;preferences.WeeklyReminderHour=weekClock.Value.Hour;preferences.WeeklyReminderMinute=weekClock.Value.Minute;
                preferences.EfficientPolling = efficient.Checked; preferences.HistoryEnabled = history.Checked;
                preferences.HistoryDays=chosenDays; preferences.ForecastDays=forecastDays[forecast.SelectedIndex];
                preferences.Save(); apply(); DialogResult = DialogResult.OK; Close();
            };
            buttons.Controls.Add(delete,0,0);buttons.Controls.Add(cancel,1,0);buttons.Controls.Add(save,2,0);shell.Controls.Add(buttons,0,2);
            AcceptButton = save; CancelButton = cancel;
            StyleControls(stack,palette);
            StyleButton(save,Widget.Accent,Color.White,Widget.Accent);
            StyleButton(cancel,palette.Card,palette.Ink,palette.Border);
            StyleButton(delete,palette.Background,preferences.DarkTheme?Color.FromArgb(255,153,153):Color.FromArgb(181,62,62),palette.Border);
            var tabs = new Button[3];
            string[] labels = { "Оформление", "Уведомления", "Данные" };
            Action<int> selectTab = delegate(int index) {
                stack.SuspendLayout();
                appearance.Visible = index == 0;
                display.Visible = index == 2;
                notifications.Visible = index == 1;
                historyPage.Visible = index == 2;
                delete.Visible = index == 2;
                for (int i = 0; i < tabs.Length; i++)
                    StyleButton(tabs[i], i == index ? palette.Card : palette.Background,
                        i == index ? palette.Ink : palette.Muted, i == index ? palette.Border : palette.Background);
                stack.ResumeLayout(true);
                scroll.AutoScrollPosition = Point.Empty;
                fitCards();
                stack.PerformLayout();
                scroll.AutoScrollMinSize = new Size(0, stack.Height + 16);
            };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                tabs[i] = StyledButton(labels[i], palette.Background, palette.Muted, palette.Background);
                tabs[i].Size = new Size(i == 2 ? 90 : 122, 34);
                tabs[i].Click += delegate { selectTab(index); };
                navigation.Controls.Add(tabs[i]);
            }
            selectTab(0);
        }
        static TableLayoutPanel Section(TableLayoutPanel stack,string title,Palette palette)
        {
            int row=stack.RowCount++;stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var section=new SettingsCard(palette) {AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Dock=DockStyle.Top,ColumnCount=2,Padding=new Padding(18,12,18,14),Margin=new Padding(6,0,6,12)};
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,56));section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,44));
            AddWide(section,new Label {Text=title,AutoSize=true,Font=new Font("Segoe UI",12,FontStyle.Bold),ForeColor=palette.Ink,Margin=new Padding(3,3,3,10)});
            stack.Controls.Add(section,0,row);return section;
        }
        static void StyleControls(Control parent,Palette palette)
        {
            foreach(Control control in parent.Controls) {
                var label=control as Label;
                if(label!=null) {if(label.ForeColor==Color.DimGray)label.ForeColor=palette.Muted;else if(label.ForeColor!=Widget.Accent)label.ForeColor=palette.Ink;label.MaximumSize=new Size(440,0);}
                if(control is CheckBox)control.ForeColor=palette.Ink;
                if(control is NumericUpDown || control is ComboBox || control is DateTimePicker) {control.BackColor=palette.Background;control.ForeColor=palette.Ink;control.Margin=new Padding(3,5,3,5);}
                StyleControls(control,palette);
            }
        }
        internal static Button StyledButton(string text, Color background, Color foreground, Color border)
        {
            var button = new Button { Text = text };
            StyleButton(button, background, foreground, border);
            return button;
        }
        static void StyleButton(Button button,Color background,Color foreground,Color border)
        {
            button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=border;button.FlatAppearance.BorderSize=1;
            button.BackColor=background;button.ForeColor=foreground;button.UseVisualStyleBackColor=false;button.Cursor=Cursors.Hand;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(
                (background.R*9 + foreground.R)/10, (background.G*9 + foreground.G)/10, (background.B*9 + foreground.B)/10);
        }
        static NumericUpDown Number(int value, int min, int max) { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Dock = DockStyle.Fill }; }
        static ComboBox Choice(object[] items,int selected) { var box=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};box.Items.AddRange(items);box.SelectedIndex=Math.Max(0,selected);return box; }
        static CheckBox Toggle(string text, bool value) { return new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }; }
        static void Add(TableLayoutPanel table, string label, Control control)
        {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }, 0, row); table.Controls.Add(control, 1, row);
        }
        static void AddWide(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.Controls.Add(control, 0, row); table.SetColumnSpan(control, 2);
        }
    }
    internal sealed class SettingsCard : TableLayoutPanel
    {
        readonly Palette palette;
        internal SettingsCard(Palette colors){palette=colors;BackColor=colors.Card;DoubleBuffered=true;}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(palette.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float diameter=20;var rect=new RectangleF(.5f,.5f,Math.Max(1,Width-1),Math.Max(1,Height-1));
            using(var path=new GraphicsPath()) {
                path.AddArc(rect.Left,rect.Top,diameter,diameter,180,90);path.AddArc(rect.Right-diameter,rect.Top,diameter,diameter,270,90);
                path.AddArc(rect.Right-diameter,rect.Bottom-diameter,diameter,diameter,0,90);path.AddArc(rect.Left,rect.Bottom-diameter,diameter,diameter,90,90);path.CloseFigure();
                using(var brush=new SolidBrush(palette.Card))e.Graphics.FillPath(brush,path);
                using(var pen=new Pen(palette.Border))e.Graphics.DrawPath(pen,path);
            }
        }
    }
}
