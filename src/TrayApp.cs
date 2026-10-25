using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InzoneBattery
{
    /// <summary>托盘图标 + 悬浮窗 + 30 秒轮询。</summary>
    sealed class TrayApp : ApplicationContext
    {
        const int PollSeconds = 30;

        readonly WidgetForm _widget;
        readonly TrayHost _trayHost;
        readonly ContextMenuStrip _menu;
        readonly System.Windows.Forms.Timer _timer;
        readonly System.Windows.Forms.Timer _trayRetry;
        readonly List<IntPtr> _iconHandles = new List<IntPtr>();
        readonly ToolStripMenuItem _menuShow;
        readonly ToolStripMenuItem _menuStartup;
        readonly ToolStripMenuItem _menuTopmost;
        readonly ToolStripMenuItem _menuLocked;
        readonly ToolStripMenuItem _menuCustom;
        readonly ToolStripMenuItem _menuTaskbar;
        readonly System.Windows.Forms.Timer _topTimer;
        readonly List<ToolStripMenuItem> _scaleItems = new List<ToolStripMenuItem>();

        InzoneDevice _device;
        string _error;
        bool _busy;
        bool _exiting;
        int _lowWarn = 100;

        string _iconKey;
        string _tip = "INZONE 电量";
        int _trayRetryMs = 400;
        int _trayRetryCount;
        int _lastPercent = -1;
        bool _lastOnline;
        Point _savedPos = new Point(int.MinValue, int.MinValue);
        bool _widgetVisible = true;
        float _w = WidgetForm.DefaultWidth;
        float _h = WidgetForm.DefaultHeight;
        float _customW = WidgetForm.DefaultWidth;
        float _customH = WidgetForm.DefaultHeight;
        float _opacity = 0.97f;
        bool _followSystem = true;
        bool _topmost = true;
        bool _locked;
        SettingsForm _settings;

        public TrayApp()
        {
            _widget = new WidgetForm(this);

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("立即刷新", null, delegate { RefreshNow(); }));
            _menuShow = new ToolStripMenuItem("显示悬浮窗", null, delegate { ToggleWidget(); });
            menu.Items.Add(_menuShow);
            menu.Items.Add(new ToolStripSeparator());

            var sizeMenu = new ToolStripMenuItem("悬浮窗大小");

            _menuTaskbar = new ToolStripMenuItem("任务栏大小");
            _menuTaskbar.Click += delegate
            {
                _w = WidgetForm.TaskbarWidth;
                _h = _widget.MinHeight;
                _widget.SetSize(_w, _h);
                ClampWidget();
                SaveConfig();
            };
            sizeMenu.DropDownItems.Add(_menuTaskbar);

            _menuCustom = new ToolStripMenuItem("我的尺寸");
            _menuCustom.Click += delegate
            {
                _w = _customW;
                _h = _customH;
                _widget.SetSize(_w, _h);
                ClampWidget();
                SaveConfig();
            };
            sizeMenu.DropDownItems.Add(_menuCustom);
            sizeMenu.DropDownItems.Add(new ToolStripMenuItem("记录当前尺寸为我的尺寸", null,
                delegate { RecordCustomSize(); }));
            sizeMenu.DropDownItems.Add(new ToolStripSeparator());

            AddSizeItem(sizeMenu, "小 (85%)", 0.85f);
            AddSizeItem(sizeMenu, "中 (100%)", 1.0f);
            AddSizeItem(sizeMenu, "大 (125%)", 1.25f);
            AddSizeItem(sizeMenu, "特大 (150%)", 1.5f);
            menu.Items.Add(sizeMenu);

            menu.Items.Add(new ToolStripMenuItem("背景透明度与效果…", null, delegate { ShowSettings(); }));

            _menuTopmost = new ToolStripMenuItem("窗口置顶", null, delegate
            {
                _topmost = !_topmost;
                ApplyWidgetSettings();
                SaveConfig();
            });
            menu.Items.Add(_menuTopmost);

            _menuLocked = new ToolStripMenuItem("锁定位置", null, delegate
            {
                _locked = !_locked;
                ApplyWidgetSettings();
                SaveConfig();
            });
            menu.Items.Add(_menuLocked);

            menu.Items.Add(new ToolStripSeparator());
            _menuStartup = new ToolStripMenuItem("开机自动启动", null, delegate { ToggleStartup(); });
            menu.Items.Add(_menuStartup);
            menu.Items.Add(new ToolStripMenuItem("重新显示托盘图标", null, delegate { RefreshTrayIcon(); }));
            menu.Items.Add(new ToolStripMenuItem("恢复默认", null, delegate { ResetDefaults(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitApp(); }));
            menu.Opening += delegate { UpdateMenuChecks(); };

            _widget.ContextMenuStrip = menu;

            _menu = menu;

            _trayRetry = new System.Windows.Forms.Timer();
            _trayRetry.Tick += delegate
            {
                _trayRetry.Stop();
                if (_exiting) return;
                StartupManager.Log("tray retry #" + _trayRetryCount);
                ApplyTrayIcon();
            };

            _trayHost = new TrayHost();
            StartupManager.Log("TrayHost ready");
            _trayHost.LeftClick += delegate { ToggleWidget(); };
            _trayHost.RightClick += delegate { ShowTrayMenu(); };
            _trayHost.ShellRestarted += delegate
            {
                StartupManager.Log("TaskbarCreated -> re-add tray icon");
                _trayRetryCount = 0;
                _trayRetryMs = 400;
                _iconKey = null;   // 让图标重新画一遍
                UpdateIcon(_lastPercent, _lastOnline);
            };

            LoadConfig();
            _tip = "INZONE · 正在读取";
            UpdateIcon(-1, false);

            ApplyWidgetSettings();
            PlaceWidget();
            ClampWidget();

            // 即使悬浮窗默认隐藏，也要先建好句柄，轮询结果才能回到 UI 线程
            _widget.CreateControl();

            if (_widgetVisible)
                _widget.Show();

            _widget.RefreshSurface();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = PollSeconds * 1000;
            _timer.Tick += delegate { RefreshNow(); };
            _timer.Start();

            // 被任务栏压下去后自动抬回来
            _topTimer = new System.Windows.Forms.Timer();
            _topTimer.Interval = 150;
            _topTimer.Tick += delegate
            {
                if (!_topmost || !_widgetVisible) return;
                if (_settings != null && _settings.Visible) return;
                if (_widget.IsBusy) return;
                if (_widget.ContextMenuStrip != null && _widget.ContextMenuStrip.Visible) return;
                _widget.EnsureTopMost();
            };
            _topTimer.Start();

            // 自启动项若指向一个已经不存在的文件，顺手修正（指向别处但文件还在的不动）
            try
            {
                if (StartupManager.IsStale())
                {
                    string ignored;
                    StartupManager.Set(true, out ignored);
                }
            }
            catch (Exception)
            {
            }

            RefreshNow();
        }

        // ------------------------------------------------------------------
        // 轮询
        // ------------------------------------------------------------------

        public void RefreshNow()
        {
            if (_busy || _exiting) return;
            _busy = true;
            ThreadPool.QueueUserWorkItem(delegate { DoRefresh(); });
        }

        void DoRefresh()
        {
            BatteryReading reading = null;
            string error = null;

            try
            {
                if (_device == null)
                    _device = InzoneDevice.Open();

                if (_device == null)
                    error = "no-receiver";
                else
                    reading = _device.GetBattery(2000);
            }
            catch (Exception)
            {
                if (_device != null)
                {
                    try { _device.Dispose(); } catch { }
                    _device = null;
                }
                error = "error";
            }

            _busy = false;
            if (_exiting) return;

            try
            {
                if (_widget.IsHandleCreated && !_widget.IsDisposed)
                {
                    _widget.BeginInvoke((Action)delegate { Apply(reading, error); });
                }
            }
            catch (Exception)
            {
                // 窗口正在关闭
            }
        }

        void Apply(BatteryReading reading, string error)
        {
            if (_exiting) return;

            _error = error;
            string model = _device != null ? _device.ModelName : "INZONE";

            _widget.UpdateState(model, reading, error);

            bool online = reading != null && reading.Online;
            UpdateIcon(online ? reading.Percent : -1, online);
            UpdateTooltip(model, reading, error);
            CheckLowBattery(reading);
        }

        void UpdateTooltip(string model, BatteryReading reading, string error)
        {
            string text;
            if (error == "no-receiver") text = "INZONE · 未检测到接收器";
            else if (error != null) text = "INZONE · 读取失败";
            else if (reading == null || !reading.Online) text = "INZONE · 耳机未连接";
            else text = model + " · " + reading.Percent + "%" + (reading.Charging ? " · 充电中" : "");

            if (text.Length > 63) text = text.Substring(0, 63);
            _tip = text;
            ApplyTrayIcon();
        }

        void CheckLowBattery(BatteryReading reading)
        {
            if (reading == null || !reading.Online || reading.Charging)
                return;

            if (reading.Percent <= 10 && _lowWarn > 10)
            {
                _lowWarn = reading.Percent;
                ShowBalloon("电量极低", "INZONE 剩余 " + reading.Percent + "%，请尽快充电。");
            }
            else if (reading.Percent <= 20 && _lowWarn > 20)
            {
                _lowWarn = reading.Percent;
                ShowBalloon("电量偏低", "INZONE 剩余 " + reading.Percent + "%。");
            }
            else if (reading.Percent > 25)
            {
                _lowWarn = 100;
            }
        }

        void ShowBalloon(string title, string text)
        {
            try { _trayHost.ShowBalloon(title, text, true); }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 托盘图标
        // ------------------------------------------------------------------

        void UpdateIcon(int percent, bool online)
        {
            _lastPercent = percent;
            _lastOnline = online;

            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;

            string key = size + "|" + percent + "|" + online;
            if (key == _iconKey)
                return;

            _iconKey = key;

            using (Bitmap bmp = TrayIcon.Render(size, percent, online))
            {
                IntPtr handle = bmp.GetHicon();
                _iconHandles.Add(handle);

                // 只保留最近 3 个句柄：shell 可能还引用着上一个，晚一点再销毁
                while (_iconHandles.Count > 3)
                {
                    IntPtr old = _iconHandles[0];
                    _iconHandles.RemoveAt(0);
                    DestroyIcon(old);
                }
            }

            ApplyTrayIcon();
        }

        IntPtr CurrentIconHandle()
        {
            return _iconHandles.Count == 0 ? IntPtr.Zero : _iconHandles[_iconHandles.Count - 1];
        }

        /// <summary>把图标和提示同步给 shell；失败就排一次重试。</summary>
        void ApplyTrayIcon()
        {
            if (_exiting)
                return;

            IntPtr handle = CurrentIconHandle();

            if (_trayHost.IsAdded)
            {
                if (_trayHost.Modify(handle, _tip))
                    return;

                StartupManager.Log("tray modify failed err=" + _trayHost.LastError);
            }
            else
            {
                if (_trayHost.Add(handle, _tip))
                {
                    StartupManager.Log("tray icon added");
                    _trayRetryCount = 0;
                    _trayRetryMs = 400;
                    return;
                }

                StartupManager.Log("tray add failed err=" + _trayHost.LastError);
            }

            ScheduleTrayRetry();
        }

        void ScheduleTrayRetry()
        {
            if (_exiting || _trayRetry == null)
                return;
            if (_trayRetryCount >= 40)
                return;

            _trayRetryCount++;
            _trayRetry.Interval = _trayRetryMs;
            _trayRetryMs = Math.Min(5000, _trayRetryMs * 2);
            _trayRetry.Stop();
            _trayRetry.Start();
        }

        void ShowTrayMenu()
        {
            if (_menu == null || _menu.IsDisposed)
                return;

            _trayHost.Activate();
            _menu.Show(Cursor.Position);
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>菜单里「重新显示托盘图标」：删掉再加一次。</summary>
        public void RefreshTrayIcon()
        {
            if (_exiting)
                return;

            StartupManager.Log("manual tray refresh");
            _trayHost.Remove();
            _iconKey = null;
            _trayRetryCount = 0;
            _trayRetryMs = 400;
            UpdateIcon(_lastPercent, _lastOnline);
        }

        void ToggleStartup()
        {
            bool want = !StartupManager.IsEnabled();
            string error;

            if (StartupManager.Set(want, out error))
            {
                UpdateMenuChecks();
                try
                {
                    _trayHost.ShowBalloon("InzoneBattery",
                        want ? "已开启开机自启动" : "已关闭开机自启动", false);
                }
                catch (Exception)
                {
                }
            }
            else
            {
                StartupManager.Log("toggle failed: " + error);
                MessageBox.Show(_widget,
                    "设置开机自启动失败：" + Environment.NewLine + (error ?? "未知原因") +
                    Environment.NewLine + Environment.NewLine + "日志：" + StartupManager.LogPath,
                    "InzoneBattery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------
        // 菜单 / 窗口
        // ------------------------------------------------------------------

        void AddSizeItem(ToolStripMenuItem parent, string text, float factor)
        {
            var item = new ToolStripMenuItem(text);
            item.Tag = factor;
            item.Click += delegate
            {
                _w = WidgetForm.DefaultWidth * factor;
                _h = WidgetForm.DefaultHeight * factor;
                _widget.SetSize(_w, _h);
                ClampWidget();
                SaveConfig();
            };
            parent.DropDownItems.Add(item);
            _scaleItems.Add(item);
        }

        void ApplyWidgetSettings()
        {
            _widget.TopMost = _topmost;
            _widget.Locked = _locked;
            _widget.SetSize(_w, _h);
            _widget.SetFollowSystem(_followSystem);
            _widget.SetOpacity(_opacity);
        }

        void RecordCustomSize()
        {
            _customW = _w;
            _customH = _h;
            SaveConfig();
            UpdateMenuChecks();
            ShowBalloon("已记录", "当前尺寸 " + (int)_customW + " × " + (int)_customH + " 已设为「我的尺寸」。");
        }

        void ResetDefaults()
        {
            if (MessageBox.Show(_widget, "恢复默认大小、位置、透明度和置顶设置？", "恢复默认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            _w = WidgetForm.DefaultWidth;
            _h = WidgetForm.DefaultHeight;
            _opacity = 0.97f;
            _followSystem = true;
            _topmost = true;
            _locked = false;
            _widgetVisible = true;
            _savedPos = new Point(int.MinValue, int.MinValue);

            if (_settings != null && !_settings.IsDisposed)
                _settings.Close();

            ApplyWidgetSettings();
            PlaceWidget();
            ClampWidget();

            if (!_widget.Visible)
                _widget.Show();

            _widget.RefreshSurface();
            SaveConfig();
            UpdateMenuChecks();
        }

        public void ShowSettings()
        {
            if (_settings != null && !_settings.IsDisposed)
            {
                _settings.Activate();
                return;
            }

            _settings = new SettingsForm(this, _opacity, _followSystem);
            _settings.FormClosed += delegate { _settings = null; };
            _settings.Show();
            _settings.Activate();
        }

        public void SetOpacity(float value)
        {
            _opacity = value;
            _widget.SetOpacity(value);
            SaveConfig();
        }

        public void SetFollowSystem(bool on)
        {
            _followSystem = on;
            _widget.SetFollowSystem(on);
            SaveConfig();
        }

        void ClampWidget()
        {
            // 用整块屏幕而不是工作区，悬浮窗才能压在任务栏上
            Rectangle bounds = Screen.FromPoint(_widget.Location).Bounds;
            int x = _widget.Left;
            int y = _widget.Top;

            if (x + _widget.Width > bounds.Right) x = bounds.Right - _widget.Width;
            if (y + _widget.Height > bounds.Bottom) y = bounds.Bottom - _widget.Height;
            if (x < bounds.Left) x = bounds.Left;
            if (y < bounds.Top) y = bounds.Top;

            _widget.Location = new Point(x, y);
        }

        public void ToggleWidget()
        {
            if (_widget.Visible)
            {
                _widget.Hide();
            }
            else
            {
                _widget.Show();
                _widget.BringToFront();
                _widget.RefreshSurface();
            }
            _widgetVisible = _widget.Visible;
            SaveConfig();
        }

        void UpdateMenuChecks()
        {
            _menuShow.Checked = _widget.Visible;
            _menuTopmost.Checked = _topmost;
            _menuLocked.Checked = _locked;
            _menuStartup.Checked = StartupManager.IsEnabled();

            bool taskbarMatch = Math.Abs(_w - WidgetForm.TaskbarWidth) < 0.5f &&
                                Math.Abs(_h - _widget.MinHeight) < 0.5f;
            _menuTaskbar.Checked = taskbarMatch;

            _menuCustom.Text = "我的尺寸 (" + (int)_customW + " × " + (int)_customH + ")";
            _menuCustom.Checked = !taskbarMatch &&
                Math.Abs(_w - _customW) < 0.5f && Math.Abs(_h - _customH) < 0.5f;
            bool customChecked = _menuCustom.Checked;

            foreach (ToolStripMenuItem item in _scaleItems)
            {
                float factor = (float)item.Tag;
                item.Checked = !taskbarMatch && !customChecked &&
                               Math.Abs(_w - WidgetForm.DefaultWidth * factor) < 0.5f &&
                               Math.Abs(_h - WidgetForm.DefaultHeight * factor) < 0.5f;
            }
        }

        void PlaceWidget()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;

            if (_savedPos.X != int.MinValue)
            {
                // 只要还有一半在屏幕内就沿用，剩下的交给 ClampWidget 拉回来
                var probe = new Rectangle(_savedPos, _widget.Size);
                Rectangle screen = Screen.FromPoint(_savedPos).Bounds;
                Rectangle visible = Rectangle.Intersect(probe, screen);
                if (visible.Width >= _widget.Width / 2 && visible.Height >= _widget.Height / 2)
                {
                    _widget.Location = _savedPos;
                    return;
                }
            }

            _widget.Location = new Point(work.Right - _widget.Width - 24,
                                        work.Bottom - _widget.Height - 24);
        }

        public void SavePosition(Point p)
        {
            _savedPos = p;
            SaveConfig();
        }

        /// <summary>悬浮窗拖拽缩放结束后调用。</summary>
        public void SaveSize(float w, float h)
        {
            _w = w;
            _h = h;
            ClampWidget();
            SaveConfig();
        }

        // ------------------------------------------------------------------
        // 配置与开机启动
        // ------------------------------------------------------------------

        static string ConfigPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "InzoneBattery", "settings.ini");
            }
        }

        void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return;

                foreach (string line in File.ReadAllLines(ConfigPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (key == "w" || key == "h" || key == "scale" ||
                        key == "customW" || key == "customH" || key == "opacity")
                    {
                        double d;
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            continue;

                        if (key == "w") _w = (float)d;
                        else if (key == "h") _h = (float)d;
                        else if (key == "customW") _customW = (float)d;
                        else if (key == "customH") _customH = (float)d;
                        else if (key == "opacity") _opacity = (float)Math.Max(0.0, Math.Min(1.0, d));
                        else
                        {
                            // 旧版配置只有 scale
                            if (d >= 0.5 && d <= 2.5)
                            {
                                _w = WidgetForm.DefaultWidth * (float)d;
                                _h = WidgetForm.DefaultHeight * (float)d;
                            }
                        }
                        continue;
                    }

                    int n;
                    if (!int.TryParse(value, out n)) continue;

                    if (key == "x") _savedPos.X = n;
                    else if (key == "y") _savedPos.Y = n;
                    else if (key == "widget") _widgetVisible = n != 0;
                    else if (key == "topmost") _topmost = n != 0;
                    else if (key == "locked") _locked = n != 0;
                    else if (key == "follow") _followSystem = n != 0;
                }
            }
            catch (Exception)
            {
            }
        }

        void SaveConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath,
                    "x=" + _widget.Location.X + "\r\n" +
                    "y=" + _widget.Location.Y + "\r\n" +
                    "widget=" + (_widget.Visible ? 1 : 0) + "\r\n" +
                    "w=" + _w.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "h=" + _h.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "topmost=" + (_topmost ? 1 : 0) + "\r\n" +
                    "locked=" + (_locked ? 1 : 0) + "\r\n" +
                    "customW=" + _customW.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "customH=" + _customH.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "opacity=" + _opacity.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "follow=" + (_followSystem ? 1 : 0) + "\r\n");
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // 退出
        // ------------------------------------------------------------------

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;

            try { _timer.Stop(); } catch { }
            try { _topTimer.Stop(); } catch { }
            try { if (_settings != null && !_settings.IsDisposed) _settings.Close(); } catch { }
            SaveConfig();

            try { _trayRetry.Stop(); } catch { }
            try { _trayHost.Stop(); } catch { }

            if (_device != null)
            {
                try { _device.Dispose(); } catch { }
                _device = null;
            }

            foreach (IntPtr h in _iconHandles)
            {
                try { DestroyIcon(h); } catch { }
            }
            _iconHandles.Clear();

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_device != null) { try { _device.Dispose(); } catch { } _device = null; }
                foreach (IntPtr h in _iconHandles)
                {
                    try { DestroyIcon(h); } catch { }
                }
                _iconHandles.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
