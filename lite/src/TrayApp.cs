using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace InzoneBattery
{
    /// <summary>精简版：只有托盘图标，没有悬浮窗、没有设置窗口、不写配置文件。</summary>
    sealed class TrayApp : ApplicationContext
    {
        const int PollSeconds = 30;

        readonly TrayHost _trayHost;
        readonly ContextMenuStrip _menu;
        readonly System.Windows.Forms.Timer _timer;
        readonly System.Windows.Forms.Timer _trayRetry;
        readonly List<IntPtr> _iconHandles = new List<IntPtr>();
        readonly ToolStripMenuItem _menuStatus;
        readonly ToolStripMenuItem _menuStartup;

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
        string _lastLine = "正在读取…";

        public TrayApp()
        {
            var menu = new ContextMenuStrip();

            _menuStatus = new ToolStripMenuItem("正在读取…");
            _menuStatus.Enabled = false;
            menu.Items.Add(_menuStatus);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("立即刷新", null, delegate { RefreshNow(); }));

            _menuStartup = new ToolStripMenuItem("开机自动启动", null, delegate { ToggleStartup(); });
            menu.Items.Add(_menuStartup);

            menu.Items.Add(new ToolStripMenuItem("重新显示托盘图标", null, delegate { RefreshTrayIcon(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitApp(); }));
            menu.Opening += delegate { UpdateMenuChecks(); };
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
            _trayHost.LeftClick += delegate { ShowBatteryTip(); };
            _trayHost.RightClick += delegate { ShowMenu(); };
            _trayHost.ShellRestarted += delegate
            {
                StartupManager.Log("TaskbarCreated -> re-add tray icon");
                _trayRetryCount = 0;
                _trayRetryMs = 400;
                _iconKey = null;
                UpdateIcon(_lastPercent, _lastOnline);
            };

            UpdateIcon(-1, false);

            // 自启动项若指向一个已经不存在的文件，顺手修正
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

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = PollSeconds * 1000;
            _timer.Tick += delegate { RefreshNow(); };
            _timer.Start();

            RefreshNow();
        }

        // ------------------------------------------------------------------
        // 轮询
        // ------------------------------------------------------------------

        void RefreshNow()
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

            // 没有窗口，借用托盘宿主窗口把结果送回 UI 线程
            _trayHost.Post(delegate { Apply(reading, error); });
        }

        void Apply(BatteryReading reading, string error)
        {
            if (_exiting) return;

            _error = error;
            string model = _device != null ? _device.ModelName : "INZONE";

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
            _lastLine = BuildStatusLine(model, reading, error);
            ApplyTrayIcon();
        }

        string BuildStatusLine(string model, BatteryReading reading, string error)
        {
            if (error == "no-receiver") return "未检测到接收器";
            if (error != null) return "读取失败";
            if (reading == null || !reading.Online) return "耳机未连接";
            return model + " · " + reading.Percent + "%" + (reading.Charging ? "（充电中）" : "");
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

        /// <summary>左键点图标：弹一个气泡显示当前电量（替代悬浮窗）。</summary>
        void ShowBatteryTip()
        {
            if (_busy && _lastPercent < 0)
                RefreshNow();

            ShowBalloon("INZONE 电量", _lastLine + "（每 30 秒自动刷新）");
        }

        void ShowBalloon(string title, string text)
        {
            try { _trayHost.ShowBalloon(title, text, false); }
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

        void ShowMenu()
        {
            ShowTrayMenu();
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        void RefreshTrayIcon()
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
                ShowBalloon("InzoneBatteryLite", want ? "已开启开机自启动" : "已关闭开机自启动");
            }
            else
            {
                StartupManager.Log("toggle failed: " + error);
                MessageBox.Show(
                    "设置开机自启动失败：" + Environment.NewLine + (error ?? "未知原因") +
                    Environment.NewLine + Environment.NewLine + "日志：" + StartupManager.LogPath,
                    "InzoneBatteryLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void UpdateMenuChecks()
        {
            _menuStartup.Checked = StartupManager.IsEnabled();
            _menuStatus.Text = _lastLine;
        }

        // ------------------------------------------------------------------
        // 退出
        // ------------------------------------------------------------------

        void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;

            try { _timer.Stop(); } catch { }
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
