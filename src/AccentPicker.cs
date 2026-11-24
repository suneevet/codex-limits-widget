using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexUsageTaskbar
{
    internal sealed class AccentPicker : TableLayoutPanel
    {
        readonly TextBox hex;
        readonly Label feedback;
        readonly AccentSwatch[] swatches;
        readonly Button reset;
        bool updating;
        int? value;
        internal event Action Changed;
        internal bool IsValid { get; private set; }
        internal int? Value { get { return value; } }

        internal AccentPicker(Palette palette, int? initial)
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Fill;
            ColumnCount = 1;
            Margin = Padding.Empty;
            var choices = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
                WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
            int[] colors = { 0x7daaff, 0xa88bff, 0xe786be, 0xf58987, 0xf5ae65, 0x68ce9f, 0x55c7d9 };
            string[] names = { "Синий", "Фиолетовый", "Розовый", "Коралловый", "Янтарный", "Мятный", "Бирюзовый" };
            swatches = new AccentSwatch[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                int rgb = colors[i];
                var swatch = new AccentSwatch(palette, Color.FromArgb(rgb | unchecked((int)0xff000000)), names[i]);
                swatch.Click += delegate { SetValue(rgb); };
                swatches[i] = swatch;
                choices.Controls.Add(swatch);
            }
            Controls.Add(choices, 0, 0);

            var custom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
                WrapContents = true, Margin = Padding.Empty };
            custom.Controls.Add(new Label { Text = "HEX", ForeColor = palette.Muted,
                AutoSize = true, Margin = new Padding(4, 9, 8, 0) });
            var field = new Panel { BackColor = palette.Background, Size = new Size(105, 30), Margin = new Padding(0, 2, 8, 0) };
            hex = new TextBox { BorderStyle = BorderStyle.None, BackColor = palette.Background, ForeColor = palette.Ink,
                Font = new Font("Consolas", 10), AccessibleName = "Цвет акцента в формате HEX", MaxLength = 7,
                CharacterCasing = CharacterCasing.Upper, Location = new Point(8, 7), Width = 89 };
            field.Controls.Add(hex);
            custom.Controls.Add(field);
            reset = SettingsDialog.StyledButton("По умолчанию", palette.Card, palette.Ink, palette.Border);
            reset.Size = new Size(118, 30);
            reset.Margin = new Padding(0, 2, 0, 0);
            reset.Click += delegate { SetValue(null); };
            custom.Controls.Add(reset);
            Controls.Add(custom, 0, 1);
            feedback = new Label { ForeColor = palette.Muted, AutoSize = true, Margin = new Padding(4, 5, 0, 0) };
            Controls.Add(feedback, 0, 2);
            hex.TextChanged += delegate {
                if (updating) return;
                string raw = hex.Text.TrimStart('#');
                int rgb = 0;
                IsValid = raw.Length == 6 && int.TryParse(raw, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out rgb);
                if (IsValid) value = rgb;
                UpdateState();
            };
            SetValue(initial);
        }

        void SetValue(int? color)
        {
            value = color;
            IsValid = true;
            updating = true;
            hex.Text = "#" + (color ?? 0x7daaff).ToString("X6");
            updating = false;
            UpdateState();
        }

        void UpdateState()
        {
            feedback.Text = !IsValid ? "Введите 6 цифр или букв A–F."
                : value.HasValue ? "Свой цвет" : "Стандартный цвет темы";
            reset.Enabled = value.HasValue || !IsValid;
            foreach (var swatch in swatches)
                swatch.SetSelected(IsValid && value.HasValue && (swatch.SwatchColor.ToArgb() & 0xffffff) == value.Value);
            if (Changed != null) Changed();
        }
    }

    internal sealed class AccentSwatch : Button
    {
        readonly Palette palette;
        bool selected, hovered;
        internal Color SwatchColor { get; private set; }

        internal AccentSwatch(Palette palette, Color color, string name)
        {
            this.palette = palette;
            SwatchColor = color;
            AccessibleName = name;
            AccessibleRole = AccessibleRole.PushButton;
            Size = new Size(40, 38);
            Margin = new Padding(2, 2, 5, 2);
            Cursor = Native.SystemHand();
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = palette.Card;
            MouseEnter += delegate { hovered = true; Invalidate(); };
            MouseLeave += delegate { hovered = false; Invalidate(); };
        }

        internal void SetSelected(bool value) { selected = value; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(palette.Card);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int diameter = Math.Min(Width, Height) - 12;
            var dot = new Rectangle((Width - diameter) / 2, (Height - diameter) / 2, diameter, diameter);
            using (var fill = new SolidBrush(SwatchColor)) e.Graphics.FillEllipse(fill, dot);
            if (selected || hovered || Focused)
            {
                var ring = Rectangle.Inflate(dot, 4, 4);
                using (var pen = new Pen(selected || Focused ? palette.Ink : palette.Muted, 1.5f))
                    e.Graphics.DrawEllipse(pen, ring);
            }
            if (selected)
            {
                float x = dot.X + dot.Width / 2f, y = dot.Y + dot.Height / 2f;
                using (var pen = new Pen(Color.FromArgb(19, 26, 39), 1.8f))
                    e.Graphics.DrawLines(pen, new[] { new PointF(x - 5, y), new PointF(x - 1, y + 4), new PointF(x + 6, y - 4) });
            }
        }
    }
}
