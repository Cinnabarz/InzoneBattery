using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace InzoneBattery
{
    /// <summary>托盘图标的绘制。竖向版是默认，横向版保留备用。</summary>
    static class TrayIcon
    {
        public static readonly Color Good = Color.FromArgb(74, 222, 128);
        public static readonly Color Warn = Color.FromArgb(251, 191, 36);
        public static readonly Color Bad = Color.FromArgb(248, 113, 113);
        public static readonly Color Idle = Color.FromArgb(100, 116, 139);

        static readonly Color Outline = Color.FromArgb(236, 242, 248);
        static readonly Color Inner = Color.FromArgb(15, 23, 42);

        /// <summary>竖向电池、直角，按 16px 像素对齐，1080P 下最清晰。</summary>
        public static Bitmap Render(int size, int percent, bool online)
        {
            if (size < 8) size = 8;

            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Color.Transparent);

                int border = Math.Max(1, (int)Math.Round(size / 16.0));

                int bodyW = Math.Max(5, (int)Math.Round(8.0 * size / 16.0));
                int bodyH = Math.Max(8, (int)Math.Round(12.0 * size / 16.0));
                int bodyX = (size - bodyW) / 2;
                int bodyY = size - bodyH - Math.Max(1, (int)Math.Round(size / 16.0));

                int tipW = Math.Max(2, (int)Math.Round(4.0 * size / 16.0));
                int tipH = Math.Max(1, (int)Math.Round(2.0 * size / 16.0));
                int tipX = bodyX + (bodyW - tipW) / 2;
                int tipY = bodyY - tipH;

                var body = new Rectangle(bodyX, bodyY, bodyW, bodyH);
                var inner = new Rectangle(body.X + border, body.Y + border,
                                          body.Width - border * 2, body.Height - border * 2);

                Color color = !online ? Idle : (percent >= 60 ? Good : (percent >= 20 ? Warn : Bad));

                using (var outline = new SolidBrush(Outline))
                {
                    g.FillRectangle(outline, body);
                    if (tipY >= 0 && tipW > 0 && tipH > 0)
                        g.FillRectangle(outline, tipX, tipY, tipW, tipH);
                }

                using (var bg = new SolidBrush(Inner))
                    g.FillRectangle(bg, inner);

                if (online && percent > 0)
                {
                    int fillH = (int)Math.Round(inner.Height * (percent / 100.0));
                    if (fillH < 1) fillH = 1;
                    using (var fill = new SolidBrush(color))
                        g.FillRectangle(fill, inner.X, inner.Bottom - fillH, inner.Width, fillH);
                }
                else if (!online)
                {
                    int dashH = Math.Max(1, inner.Height / 5);
                    using (var dash = new SolidBrush(Color.FromArgb(160, 170, 185)))
                        g.FillRectangle(dash, inner.X, inner.Y + (inner.Height - dashH) / 2, inner.Width, dashH);
                }
            }

            return bmp;
        }

        /// <summary>旧版横向电池图标，保留备用。</summary>
        public static Bitmap RenderHorizontal(int size, int percent, bool online)
        {
            if (size < 8) size = 8;

            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                float u = size / 16f;

                var body = new RectangleF(0.55f * u, 3.0f * u, 13.1f * u, 10.0f * u);
                var tip = new RectangleF(13.5f * u, 6.2f * u, 2.1f * u, 3.6f * u);

                Color color = !online ? Idle : (percent >= 60 ? Good : (percent >= 20 ? Warn : Bad));

                using (GraphicsPath path = Rounded(body, 2.6f * u))
                {
                    if (online && percent > 0)
                    {
                        g.SetClip(path);
                        using (var fill = new SolidBrush(color))
                            g.FillRectangle(fill, body.X, body.Y,
                                body.Width * (percent / 100f), body.Height);
                        g.ResetClip();
                    }

                    using (var pen = new Pen(Color.FromArgb(236, 242, 248), Math.Max(1.3f, 1.45f * u)))
                        g.DrawPath(pen, path);
                }

                if (!online)
                {
                    using (var dash = new SolidBrush(Color.FromArgb(150, 160, 175)))
                        g.FillRectangle(dash, body.X + 2.0f * u, body.Y + body.Height / 2f - 0.8f * u,
                            body.Width - 4.0f * u, 1.6f * u);
                }

                using (var tipBrush = new SolidBrush(Color.FromArgb(236, 242, 248)))
                    g.FillRectangle(tipBrush, tip);
            }

            return bmp;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
