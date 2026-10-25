using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InzoneBattery
{
    /// <summary>无边框悬浮窗：宽高任意，8px 圆角，右下角拖拽缩放，靠近边缘自动吸附。</summary>
    sealed class WidgetForm : Form
    {
        static readonly Color Bg = Color.FromArgb(5, 7, 10);
        static readonly Color Fore = Color.White;
        static readonly Color Sub = Color.FromArgb(156, 163, 175);
        static readonly Color Track = Color.FromArgb(31, 41, 55);
        static readonly Color Good = Color.FromArgb(34, 197, 94);
        static readonly Color Warn = Color.FromArgb(245, 158, 11);
        static readonly Color Bad = Color.FromArgb(239, 68, 68);
        static readonly Color Idle = Color.FromArgb(107, 114, 128);

        // 逻辑尺寸（96 DPI 下的像素）
        public const float DefaultWidth = 210;
        public const float DefaultHeight = 92;
        public const float TaskbarWidth = 130;
        const float MinWidth = 130;
        const float MaxWidth = 720;
        const float MaxHeight = 420;

        readonly TrayApp _app;
        readonly ToolTip _tip = new ToolTip();

        float _s = 1f;      // 系统 DPI 比例
        float _w = DefaultWidth;
        float _h = DefaultHeight;
        float _minH = 44f;  // 最小高度 = 任务栏高度 - 1
        float _bgAlpha = 0.97f;      // 用户设置的背景不透明度
        float _effectiveBg = 0.97f;  // 结合系统设置后的实际值
        bool _followSystem = true;
        bool _locked;

        string _model = "INZONE";
        string _error;
        bool _online;
        bool _charging;
        int _percent = -1;

        bool _dragging;
        Point _dragOrigin;
        Point _formOrigin;

        bool _resizing;
        Point _resizeOrigin;
        float _resizeStartW;
        float _resizeStartH;
        bool _hover;

        public WidgetForm(TrayApp app)
        {
            _app = app;

            using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero))
                _s = screen.DpiX / 96f;

            UpdateTaskbarMetrics();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            ApplySize();
        }

        void UpdateTaskbarMetrics()
        {
            int tb = Screen.PrimaryScreen.Bounds.Height - Screen.PrimaryScreen.WorkingArea.Height;
            float logical = tb > 1 ? tb / _s : 40f;
            logical -= 1f; // 比任务栏矮 1px，避免压到任务栏边缘
            if (logical < 36f) logical = 36f;
            _minH = logical;
        }

        public bool Locked
        {
            get { return _locked; }
            set
            {
                _locked = value;
                Cursor = value ? Cursors.Default : Cursors.SizeAll;
                RenderLayered();
            }
        }

        public float UiWidth { get { return _w; } }
        public float UiHeight { get { return _h; } }
        public float MinHeight { get { return _minH; } }

        public bool IsBusy { get { return _dragging || _resizing; } }

        // ------------------------------------------------------------------
        // 大小
        // ------------------------------------------------------------------

        public void SetSize(float w, float h)
        {
            if (w < MinWidth) w = MinWidth;
            if (w > MaxWidth) w = MaxWidth;
            if (h < _minH) h = _minH;
            if (h > MaxHeight) h = MaxHeight;

            _w = w;
            _h = h;
            ApplySize();
        }

        void ApplySize()
        {
            Size = new Size((int)(_w * _s), (int)(_h * _s));
            RenderLayered();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics g = CreateGraphics())
                _s = g.DpiX / 96f;
            UpdateTaskbarMetrics();
            ApplySize();
            ApplyBackground();
        }

        // ------------------------------------------------------------------
        // 背景透明度 / Win11 透明效果
        // ------------------------------------------------------------------

        public void SetOpacity(float value)
        {
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;
            _bgAlpha = value;
            ApplyBackground();
        }

        public void SetFollowSystem(bool on)
        {
            _followSystem = on;
            ApplyBackground();
        }

        void ApplyBackground()
        {
            _effectiveBg = _bgAlpha;
            bool systemOn = SystemTransparencyEnabled();
            if (_followSystem && !systemOn)
                _effectiveBg = 1f;

            RenderLayered();
        }

        /// <summary>Windows「透明效果」总开关。</summary>
        public static bool SystemTransparencyEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return true;
                    object value = key.GetValue("EnableTransparency");
                    if (value == null) return true;
                    return Convert.ToInt32(value) != 0;
                }
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------
        // 逐像素分层窗口（只让背景半透明，文字保持不透明）
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        void RenderLayered()
        {
            if (!IsHandleCreated || IsDisposed) return;
            int w = Width;
            int h = Height;
            if (w <= 0 || h <= 0) return;

            // UpdateLayeredWindow 要 premultiplied alpha，所以整张表面用 PArgb
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    RenderTo(g);
                }

                PushLayered(bmp);
            }
        }

        void PushLayered(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBitmap);

            var dst = new POINT { X = Left, Y = Top };
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1 // AC_SRC_ALPHA
            };

            try
            {
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public void RefreshSurface()
        {
            RenderLayered();
        }

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        public void UpdateState(string model, BatteryReading reading, string error)
        {
            _model = string.IsNullOrEmpty(model) ? "INZONE" : model;
            _error = error;

            if (reading != null)
            {
                _online = reading.Online;
                _charging = reading.Charging;
                _percent = reading.Online ? reading.Percent : -1;
            }
            else
            {
                _online = false;
                _charging = false;
                _percent = -1;
            }

            RenderLayered();
        }

        Color Accent
        {
            get
            {
                if (!_online) return Idle;
                if (_percent >= 60) return Good;
                if (_percent >= 20) return Warn;
                return Bad;
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            if (d <= 0) d = 1;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static float FitFont(Graphics g, string text, string family, FontStyle style,
                             float startPt, float maxW, float maxH)
        {
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                float pt = startPt;
                while (pt > 6.5f)
                {
                    using (var f = new Font(family, pt, style))
                    {
                        SizeF sz = g.MeasureString(text, f, PointF.Empty, sf);
                        if (sz.Width <= maxW && sz.Height <= maxH)
                            return pt;
                    }
                    pt -= 1f;
                }
            }
            return 6.5f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 实际显示走 UpdateLayeredWindow，这里只在系统强制重绘时兜底
            RenderTo(e.Graphics);
        }

        void RenderTo(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 不用 ClearType：亚像素渲染假设背景不透明，透明卡片上会留彩边
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;

            int W = Width;
            int H = Height;
            float s = _s;

            float k = Math.Min(W / (210f * s), H / (92f * s));
            if (k < 0.6f) k = 0.6f;
            if (k > 2.5f) k = 2.5f;

            int pad = (int)(12 * s * Math.Min(1.4f, Math.Max(0.65f, k)));

            using (GraphicsPath path = Rounded(new Rectangle(0, 0, W, H), 8 * s))
            {
                int alpha = (int)Math.Round(255 * _effectiveBg);
                // 0% 时按 6/255（约 2.4%）绘制：肉眼不可见，但保留命中区域，
                // 拖动/右键才不会被当成透明区域穿透掉
                if (alpha < 6) alpha = 6;
                if (alpha > 255) alpha = 255;

                using (var brush = new SolidBrush(Color.FromArgb(alpha, Bg)))
                    g.FillPath(brush, path);
            }

            // 状态
            string status;
            Color statusColor;
            if (_error == "no-receiver") { status = "无接收器"; statusColor = Idle; }
            else if (_error != null) { status = "读取失败"; statusColor = Bad; }
            else if (!_online) { status = "未连接"; statusColor = Idle; }
            else if (_charging) { status = "充电中"; statusColor = Good; }
            else { status = "已连接"; statusColor = Good; }

            int barH = Math.Max(3, (int)(5 * s * Math.Min(2f, Math.Max(0.7f, k))));
            int barY = H - pad - barH;
            int contentTop = pad;
            int contentBottom = barY - Math.Max(2, (int)(4 * s));
            int contentH = Math.Max(8, contentBottom - contentTop);

            float textPt = 8.5f * Math.Min(1.7f, Math.Max(0.72f, k));
            if (textPt < 7.2f) textPt = 7.2f;

            int leftW = (int)(W * 0.52f);

            using (var f = new Font("Segoe UI Semibold", textPt, FontStyle.Bold))
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;

                SizeF lineSize = g.MeasureString("Ag", f, PointF.Empty, sf);
                int lineH = (int)Math.Ceiling(lineSize.Height);
                int gap = Math.Max(1, (int)(2 * s));
                int totalH = lineH * 2 + gap;
                int startY = contentTop + Math.Max(0, (contentH - totalH) / 2);

                int dot = Math.Max(4, (int)(lineH * 0.5f));

                using (var dotBrush = new SolidBrush(statusColor))
                    g.FillEllipse(dotBrush, pad, startY + (lineH - dot) / 2f, dot, dot);

                using (var statusBrush = new SolidBrush(statusColor))
                    g.DrawString(status, f, statusBrush, pad + dot + gap, startY, sf);

                using (var modelBrush = new SolidBrush(Fore))
                using (var rectFmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    rectFmt.FormatFlags |= StringFormatFlags.NoWrap;
                    rectFmt.Trimming = StringTrimming.EllipsisCharacter;
                    rectFmt.Alignment = StringAlignment.Near;
                    rectFmt.LineAlignment = StringAlignment.Center;
                    g.DrawString(_model, f, modelBrush,
                        new RectangleF(pad, startY + lineH + gap, Math.Max(10, leftW), lineH), rectFmt);
                }
            }

            // 大号百分比，放在右侧
            string big = _online ? _percent.ToString() + "%" : "--";
            int rightX = pad + leftW;
            int rightW = Math.Max(30, W - pad - rightX);
            float startPt = Math.Max(10f, contentH * 0.95f);
            float pt = FitFont(g, big, "Segoe UI Semibold", FontStyle.Bold, startPt, rightW, contentH);

            using (var bf = new Font("Segoe UI Semibold", pt, FontStyle.Bold))
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (var brush = new SolidBrush(_online ? Fore : Sub))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                SizeF sz = g.MeasureString(big, bf, PointF.Empty, sf);
                float x = W - pad - sz.Width;
                float y = contentTop + (contentH - sz.Height) / 2f;
                g.DrawString(big, bf, brush, x, y, sf);
            }

            // 进度条
            int barW = W - pad * 2;
            using (GraphicsPath trackPath = Rounded(new Rectangle(pad, barY, barW, barH), barH / 2f))
            {
                using (var tb = new SolidBrush(Track))
                    g.FillPath(tb, trackPath);
            }

            if (_online && _percent > 0)
            {
                int w = (int)(barW * (_percent / 100f));
                if (w < barH) w = barH;
                using (GraphicsPath fillPath = Rounded(new Rectangle(pad, barY, w, barH), barH / 2f))
                {
                    using (var fb = new SolidBrush(Accent))
                        g.FillPath(fb, fillPath);
                }
            }

            // 右下角缩放把手：悬停时才显示
            if (!_locked && _hover)
            {
                using (var pen = new Pen(Color.FromArgb(90, 148, 163, 184), Math.Max(1f, 1f * s)))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float off = (4 + i * 2.5f) * s;
                        g.DrawLine(pen, W - off, H - 2 * s, W - 2 * s, H - off);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // 拖动 / 缩放 / 吸附
        // ------------------------------------------------------------------

        bool InResizeGrip(Point p)
        {
            int grip = (int)(18 * _s);
            return p.X >= Width - grip && p.Y >= Height - grip;
        }

        Point SnapLocation(Point proposed)
        {
            int t = (int)(12 * _s);
            var center = new Point(proposed.X + Width / 2, proposed.Y + Height / 2);
            Rectangle b = Screen.FromPoint(center).Bounds;
            Rectangle wa = Screen.FromPoint(center).WorkingArea;

            if (Math.Abs(proposed.X - b.Left) <= t) proposed.X = b.Left;
            else if (Math.Abs(proposed.X + Width - b.Right) <= t) proposed.X = b.Right - Width;

            if (Math.Abs(proposed.Y - b.Top) <= t) proposed.Y = b.Top;
            else if (Math.Abs(proposed.Y + Height - wa.Bottom) <= t) proposed.Y = wa.Bottom - Height;
            else if (Math.Abs(proposed.Y + Height - b.Bottom) <= t) proposed.Y = b.Bottom - Height;

            int cx = b.Left + b.Width / 2;
            if (Math.Abs(proposed.X + Width / 2 - cx) <= t) proposed.X = cx - Width / 2;

            return proposed;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Middle)
            {
                _app.ShowSettings();
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (_locked)
            {
                _tip.Show("位置已锁定，右键菜单可解锁", this, e.X + 10, e.Y + 10, 1500);
                return;
            }

            if (InResizeGrip(e.Location))
            {
                _resizing = true;
                _resizeOrigin = Cursor.Position;
                _resizeStartW = _w;
                _resizeStartH = _h;
                return;
            }

            _dragging = true;
            _dragOrigin = Cursor.Position;
            _formOrigin = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_resizing)
            {
                int dx = Cursor.Position.X - _resizeOrigin.X;
                int dy = Cursor.Position.Y - _resizeOrigin.Y;

                float nw = _resizeStartW + dx / _s;
                float nh = _resizeStartH + dy / _s;

                int t = (int)(12 * _s);
                Rectangle b = Screen.FromRectangle(Bounds).Bounds;
                Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;

                int pw = (int)(nw * _s);
                int ph = (int)(nh * _s);
                if (Math.Abs(Left + pw - b.Right) <= t) nw = (b.Right - Left) / _s;
                if (Math.Abs(Top + ph - wa.Bottom) <= t) nh = (wa.Bottom - Top) / _s;
                else if (Math.Abs(Top + ph - b.Bottom) <= t) nh = (b.Bottom - Top) / _s;

                SetSize(nw, nh);
                return;
            }

            if (_dragging)
            {
                Point now = Cursor.Position;
                var proposed = new Point(_formOrigin.X + now.X - _dragOrigin.X,
                                         _formOrigin.Y + now.Y - _dragOrigin.Y);
                Location = SnapLocation(proposed);
                return;
            }

            if (_locked) return;
            Cursor = InResizeGrip(e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_resizing)
            {
                _resizing = false;
                _app.SaveSize(_w, _h);
                return;
            }

            if (_dragging)
            {
                _dragging = false;
                _app.SavePosition(Location);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            RenderLayered();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            if (!_dragging && !_resizing && !_locked)
                Cursor = Cursors.SizeAll;
            RenderLayered();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (InResizeGrip(PointToClient(Cursor.Position))) return;
            _app.RefreshNow();
        }

        // ------------------------------------------------------------------
        // 置顶 / Alt+Tab
        // ------------------------------------------------------------------

        public void EnsureTopMost()
        {
            if (!IsHandleCreated || !Visible) return;
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED：逐像素透明
                return cp;
            }
        }
    }
}
