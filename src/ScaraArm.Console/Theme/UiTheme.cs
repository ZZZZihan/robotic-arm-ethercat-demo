using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScaraArm.Console.Theme
{
    /// <summary>
    /// Shared color palette, font resources, and WinForms layout helpers.
    /// </summary>
    internal sealed class UiTheme : IDisposable
    {
        public static readonly Color Bg = Color.FromArgb(245, 247, 250);
        public static readonly Color CardBg = Color.White;
        public static readonly Color SelectedBg = Color.FromArgb(238, 246, 253);
        public static readonly Color SuccessBg = Color.FromArgb(236, 248, 241);
        public static readonly Color DangerBg = Color.FromArgb(253, 240, 239);
        public static readonly Color BorderColor = Color.FromArgb(221, 227, 234);
        public static readonly Color Accent = Color.FromArgb(22, 119, 255);
        public static readonly Color Success = Color.FromArgb(22, 148, 82);
        public static readonly Color Warning = Color.FromArgb(194, 120, 3);
        public static readonly Color WarningBg = Color.FromArgb(254, 249, 195);
        public static readonly Color Danger = Color.FromArgb(214, 48, 49);
        public static readonly Color Ink = Color.FromArgb(30, 41, 59);
        public static readonly Color Muted = Color.FromArgb(100, 116, 139);
        public static readonly Color DisabledBg = Color.FromArgb(226, 232, 240);

        public Font Title { get; private set; }
        public Font Section { get; private set; }
        public Font AxisValue { get; private set; }
        public Font Bold { get; private set; }
        public Font Regular { get; private set; }
        public Font Small { get; private set; }
        public Font Input { get; private set; }

        public UiTheme()
        {
            Title = new Font("Microsoft YaHei UI", 13.5F, FontStyle.Bold);
            Section = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold);
            AxisValue = new Font("Microsoft YaHei UI", 14.5F, FontStyle.Bold);
            Bold = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            Regular = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
            Small = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
            Input = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);
        }

        public Label CreateCaption(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = Small,
                ForeColor = Muted,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(0)
            };
        }

        public Label CreateDialogLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = Regular,
                ForeColor = Ink,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
        }

        public void ConfigureDecimalInput(
            NumericUpDown input, decimal min, decimal max, decimal value, decimal increment, int decimals)
        {
            input.DecimalPlaces = decimals;
            input.Increment = increment;
            input.Minimum = min;
            input.Maximum = max;
            input.Value = value;
            input.Dock = DockStyle.Fill;
            input.Font = Input;
        }

        public static TableLayoutPanel CreateGrid(int columns, int rows)
        {
            return new TableLayoutPanel
            {
                ColumnCount = columns,
                RowCount = rows,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
        }

        public static void StyleButton(Button button, string text, Color backColor, Color foreColor, int borderSize)
        {
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = BorderColor;
            button.FlatAppearance.BorderSize = borderSize;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        public static void SetNumericClamped(NumericUpDown input, decimal value)
        {
            if (value < input.Minimum) value = input.Minimum;
            if (value > input.Maximum) value = input.Maximum;
            input.Value = value;
        }

        public void Dispose()
        {
            Title.Dispose();
            Section.Dispose();
            AxisValue.Dispose();
            Bold.Dispose();
            Regular.Dispose();
            Small.Dispose();
            Input.Dispose();
        }
    }
}
