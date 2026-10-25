using System;
using System.Drawing;
using System.Windows.Forms;

namespace InzoneBattery
{
    /// <summary>背景不透明度设置。</summary>
    sealed class SettingsForm : Form
    {
        readonly TrayApp _app;
        readonly TrackBar _slider;
        readonly Label _value;

        public SettingsForm(TrayApp app, float opacity)
        {
            _app = app;

            Text = "InzoneBattery 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(380, 170);
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(250, 250, 250);

            var title = new Label();
            title.Text = "背景不透明度";
            title.Location = new Point(16, 14);
            title.AutoSize = true;
            Controls.Add(title);

            _slider = new TrackBar();
            _slider.Minimum = 0;
            _slider.Maximum = 100;
            _slider.TickFrequency = 10;
            _slider.SmallChange = 1;
            _slider.LargeChange = 5;
            _slider.Location = new Point(14, 36);
            _slider.Width = 260;
            _slider.Value = (int)Math.Round(opacity * 100f);
            Controls.Add(_slider);

            _value = new Label();
            _value.Text = _slider.Value + "%";
            _value.Location = new Point(282, 44);
            _value.AutoSize = true;
            Controls.Add(_value);

            var hint = new Label();
            hint.Text = "只影响卡片背景，文字和图形保持不透明。";
            hint.ForeColor = Color.FromArgb(110, 110, 110);
            hint.Location = new Point(16, 96);
            hint.MaximumSize = new Size(348, 0);
            hint.AutoSize = true;
            Controls.Add(hint);

            var close = new Button();
            close.Text = "关闭";
            close.Location = new Point(282, 128);
            close.Size = new Size(82, 28);
            close.Click += delegate { Close(); };
            Controls.Add(close);

            _slider.ValueChanged += delegate
            {
                _value.Text = _slider.Value + "%";
                _app.SetOpacity(_slider.Value / 100f);
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW：不进 Alt+Tab
                return cp;
            }
        }
    }
}
