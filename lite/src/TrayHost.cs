using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InzoneBattery
{
    /// <summary>
    /// 自己管托盘图标：直接调 Shell_NotifyIcon，每一步都有返回值，
    /// 失败能写日志、能重试。WinForms 的 NotifyIcon 失败是静默的，查不了。
    /// </summary>
    sealed class TrayHost : NativeWindow
    {
        const int WM_APP = 0x8000;
        public const int WM_TRAYICON = WM_APP + 1;
        const int WM_APP_INVOKE = WM_APP + 2;

        const uint NIM_ADD = 0;
        const uint NIM_MODIFY = 1;
        const uint NIM_DELETE = 2;

        const uint NIF_MESSAGE = 0x01;
        const uint NIF_ICON = 0x02;
        const uint NIF_TIP = 0x04;
        const uint NIF_INFO = 0x10;

        const uint NIIF_INFO = 0x01;
        const uint NIIF_WARNING = 0x02;

        const int WM_LBUTTONUP = 0x0202;
        const int WM_RBUTTONUP = 0x0205;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        static extern void SetLastError(int error);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        readonly int _taskbarCreated;
        readonly System.Collections.Generic.Queue<Action> _queue =
            new System.Collections.Generic.Queue<Action>();
        readonly object _queueLock = new object();

        public event Action LeftClick;
        public event Action RightClick;
        public event Action ShellRestarted;

        /// <summary>最近一次失败的错误码。</summary>
        public int LastError { get; private set; }

        public bool IsAdded { get; private set; }

        public TrayHost()
        {
            var cp = new CreateParams();
            cp.Caption = "InzoneBatteryTray";
            cp.Style = 0;
            cp.ExStyle = 0;
            CreateHandle(cp);

            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        }

        protected override void WndProc(ref Message m)
        {
            if (_taskbarCreated != 0 && m.Msg == _taskbarCreated)
            {
                IsAdded = false;
                try { if (ShellRestarted != null) ShellRestarted(); }
                catch (Exception) { }
            }
            else if (m.Msg == WM_APP_INVOKE)
            {
                Action[] pending;
                lock (_queueLock)
                {
                    pending = _queue.ToArray();
                    _queue.Clear();
                }
                foreach (Action action in pending)
                {
                    try { action(); } catch (Exception) { }
                }
            }
            else if (m.Msg == WM_TRAYICON)
            {
                int mouse = (int)((long)m.LParam & 0xFFFFL);
                if (mouse == WM_LBUTTONUP)
                {
                    try { if (LeftClick != null) LeftClick(); } catch (Exception) { }
                }
                else if (mouse == WM_RBUTTONUP)
                {
                    try { if (RightClick != null) RightClick(); } catch (Exception) { }
                }
            }

            base.WndProc(ref m);
        }

        NOTIFYICONDATA Make(uint flags, IntPtr icon, string tip)
        {
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = Handle;
            d.uID = 1;
            d.uFlags = flags;
            d.uCallbackMessage = WM_TRAYICON;
            d.hIcon = icon;
            d.szTip = tip ?? "";
            d.szInfo = "";
            d.szInfoTitle = "";
            return d;
        }

        /// <summary>添加图标；返回 shell 是否接受。已存在时先删再加，保证幂等。</summary>
        public bool Add(IntPtr icon, string tip)
        {
            SetLastError(0);
            var d = Make(NIF_MESSAGE | NIF_ICON | NIF_TIP, icon, tip);
            if (Shell_NotifyIcon(NIM_ADD, ref d))
            {
                IsAdded = true;
                LastError = 0;
                return true;
            }

            // TaskbarCreated 之后 shell 可能还留着旧条目：先删掉再加一次
            var del = Make(0, IntPtr.Zero, "");
            Shell_NotifyIcon(NIM_DELETE, ref del);

            SetLastError(0);
            d = Make(NIF_MESSAGE | NIF_ICON | NIF_TIP, icon, tip);
            if (Shell_NotifyIcon(NIM_ADD, ref d))
            {
                IsAdded = true;
                LastError = 0;
                return true;
            }

            LastError = Marshal.GetLastWin32Error();
            IsAdded = false;
            return false;
        }

        /// <summary>更新图标与提示；失败说明图标已经不在托盘里。</summary>
        public bool Modify(IntPtr icon, string tip)
        {
            if (!IsAdded)
                return Add(icon, tip);

            var d = Make(NIF_ICON | NIF_TIP, icon, tip);
            if (Shell_NotifyIcon(NIM_MODIFY, ref d))
            {
                LastError = 0;
                return true;
            }

            LastError = Marshal.GetLastWin32Error();
            IsAdded = false;
            return false;
        }

        public void Remove()
        {
            if (!IsAdded)
                return;

            var d = Make(0, IntPtr.Zero, "");
            Shell_NotifyIcon(NIM_DELETE, ref d);
            IsAdded = false;
        }

        public bool ShowBalloon(string title, string text, bool warning)
        {
            if (!IsAdded)
                return false;

            var d = Make(NIF_INFO, IntPtr.Zero, "");
            d.szInfoTitle = title ?? "";
            d.szInfo = text ?? "";
            d.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
            d.uVersion = 5000;
            return Shell_NotifyIcon(NIM_MODIFY, ref d);
        }

        /// <summary>把回调排到 UI 线程执行（精简版没有窗口，用这个回主线程）。</summary>
        public void Post(Action action)
        {
            if (action == null || Handle == IntPtr.Zero)
                return;

            lock (_queueLock)
                _queue.Enqueue(action);

            PostMessage(Handle, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>右键菜单要先把自己切到前台，否则点别处不消失。</summary>
        public void Activate()
        {
            if (Handle == IntPtr.Zero)
                return;

            SetForegroundWindow(Handle);
        }

        public void Stop()
        {
            try { Remove(); } catch (Exception) { }
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
    }
}
