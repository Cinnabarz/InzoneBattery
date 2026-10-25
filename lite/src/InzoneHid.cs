using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace InzoneBattery
{
    /// <summary>一次电量读取的结果。</summary>
    public sealed class BatteryReading
    {
        public bool Online;          // 耳机是否在链路另一端（关机/离座时为 false）
        public int Percent;          // 0..100，Online 为 false 时是 -1
        public int Status;           // 协议里的状态字节
        public DateTime Timestamp;

        public bool Charging { get { return Online && Status != 0; } }
    }

    /// <summary>
    /// INZONE 系列 2.4GHz 接收器的 HID 控制通道。
    ///
    /// 接收器是一个 USB 复合设备，控制协议在 usage page 0xFF04 的集合上，
    /// 报告固定 64 字节（含 Report ID 0x02）。负载是一层仿蓝牙 HCI 的
    /// Sony 私有包：命令 opcode 0xFC00、key 0xC396，事件码 0xFF。
    ///
    /// 注意：Windows HID 驱动在同一句柄上同时挂起读和写会互相阻塞，
    /// 所以这里开两个句柄：一个专用于后台读，一个专用于写。
    /// </summary>
    public sealed class InzoneDevice : IDisposable
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x1;
        const uint FILE_SHARE_WRITE = 0x2;
        const uint OPEN_EXISTING = 3;
        const uint DIGCF_PRESENT = 0x2;
        const uint DIGCF_DEVICEINTERFACE = 0x10;

        static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        public const ushort SonyVendorId = 0x054C;
        public const ushort UsagePageControl = 0xFF04;

        public const byte EventBattery = 0x04;
        public const byte EventModelInfo = 0x02;
        public const byte TypeGet = 0x01;
        public const byte TypeRet = 0x10;

        sealed class EventPacket
        {
            public byte EventId;
            public byte EventType;
            public ushort Tid;
            public byte[] Param;
        }

        IntPtr _readHandle = INVALID_HANDLE;
        IntPtr _writeHandle = INVALID_HANDLE;
        Thread _reader;
        volatile bool _running;
        readonly object _sync = new object();
        readonly LinkedList<EventPacket> _events = new LinkedList<EventPacket>();
        ushort _tid = 2;
        int _disposed;

        public string DevicePath;
        public ushort ProductId;
        public string ModelName = "INZONE";

        // ------------------------------------------------------------------
        // 打开 / 关闭
        // ------------------------------------------------------------------

        /// <summary>找到并打开接收器的控制通道；没有接收器时返回 null。</summary>
        public static InzoneDevice Open()
        {
            string path;
            ushort pid;
            if (!FindControlPath(out path, out pid))
                return null;

            var dev = new InzoneDevice();
            dev.DevicePath = path;
            dev.ProductId = pid;

            dev._readHandle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (dev._readHandle == INVALID_HANDLE)
                return null;

            dev._writeHandle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (dev._writeHandle == INVALID_HANDLE)
            {
                CloseHandle(dev._readHandle);
                dev._readHandle = INVALID_HANDLE;
                return null;
            }

            dev._running = true;
            dev._reader = new Thread(dev.ReadLoop);
            dev._reader.IsBackground = true;
            dev._reader.Start();

            dev.ModelName = ModelNameFor(pid);
            return dev;
        }

        static string ModelNameFor(ushort pid)
        {
            switch (pid)
            {
                case 0x0EBF: return "INZONE H5";
                case 0x0DFD: return "INZONE H9";
                case 0x0E47: return "INZONE H7";
                case 0x0E53: return "INZONE H3";
                case 0x0EC2: return "INZONE Buds";
                case 0x0FA8: return "INZONE H9 II";
                default: return "INZONE";
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _running = false;
            if (_readHandle != INVALID_HANDLE)
            {
                CancelIoEx(_readHandle, IntPtr.Zero);
                CloseHandle(_readHandle);
                _readHandle = INVALID_HANDLE;
            }
            if (_writeHandle != INVALID_HANDLE) { CloseHandle(_writeHandle); _writeHandle = INVALID_HANDLE; }
        }

        // ------------------------------------------------------------------
        // 协议
        // ------------------------------------------------------------------

        /// <summary>读电量。percent 为 0xFF 表示耳机不在线。</summary>
        public BatteryReading GetBattery(int timeoutMs)
        {
            lock (_sync) { }

            ushort tid;
            lock (_sync)
            {
                tid = _tid++;
                if (_tid < 2) _tid = 2;
            }

            WriteCommand(BuildCommand(EventBattery, TypeGet, tid));

            EventPacket ev = WaitFor(EventBattery, tid, timeoutMs);
            if (ev == null)
                throw new TimeoutException("接收器没有回应电量查询");

            if (ev.Param == null || ev.Param.Length < 2)
                throw new InvalidOperationException("电量数据不完整");

            var reading = new BatteryReading();
            reading.Timestamp = DateTime.Now;
            reading.Status = ev.Param[0];

            int percent;
            if (ev.Param.Length >= 6)
            {
                // 豆状耳机：左/右/盒 各一对状态+电量
                percent = -1;
                for (int i = 1; i < ev.Param.Length; i += 2)
                {
                    if (ev.Param[i] != 0xFF && ev.Param[i] > percent)
                        percent = ev.Param[i];
                }
            }
            else
            {
                percent = ev.Param[1];
            }

            if (percent < 0 || percent == 0xFF)
            {
                reading.Online = false;
                reading.Percent = -1;
            }
            else
            {
                reading.Online = true;
                reading.Percent = percent > 100 ? 100 : percent;
            }
            return reading;
        }

        /// <summary>读型号信息（用于确认设备确实是 INZONE）。</summary>
        public byte[] GetModelInfo(int timeoutMs)
        {
            ushort tid;
            lock (_sync)
            {
                tid = _tid++;
                if (_tid < 2) _tid = 2;
            }
            WriteCommand(BuildCommand(EventModelInfo, TypeGet, tid));
            EventPacket ev = WaitFor(EventModelInfo, tid, timeoutMs);
            return ev == null ? null : ev.Param;
        }

        static byte[] BuildCommand(byte eventId, byte eventType, ushort tid)
        {
            var p = new byte[12];
            p[0] = 0x01;        // HCI command
            p[1] = 0x00;
            p[2] = 0xFC;        // opcode 0xFC00
            p[3] = 0x08;        // 8 + payload
            p[4] = 0x96;        // key 0xC396
            p[5] = 0xC3;
            p[6] = 0x41;        // destination 4 (耳机) << 4 | source 1 (PC)
            p[7] = eventId;
            p[8] = eventType;
            p[9] = (byte)(tid & 0xFF);
            p[10] = (byte)(tid >> 8);

            int sum = 0;
            for (int i = 4; i <= 10; i++) sum += p[i];
            p[11] = (byte)(sum & 0xFF);
            return p;
        }

        void WriteCommand(byte[] packet)
        {
            var report = new byte[64];
            report[0] = 0x02;               // Report ID
            report[1] = (byte)packet.Length; // HCI 字节数
            Array.Copy(packet, 0, report, 2, packet.Length);

            int written;
            if (!WriteFile(_writeHandle, report, report.Length, out written, IntPtr.Zero))
                throw new IOException("写入接收器失败（错误码 " + Marshal.GetLastWin32Error() + "）");
        }

        EventPacket WaitFor(byte eventId, ushort tid, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            lock (_sync)
            {
                while (true)
                {
                    LinkedListNode<EventPacket> node = _events.First;
                    while (node != null)
                    {
                        EventPacket e = node.Value;
                        if (e.EventId == eventId && e.Tid == tid)
                        {
                            _events.Remove(node);
                            return e;
                        }
                        node = node.Next;
                    }

                    int remain = deadline - Environment.TickCount;
                    if (remain <= 0)
                        return null;
                    Monitor.Wait(_sync, remain);
                }
            }
        }

        void ReadLoop()
        {
            var buf = new byte[64];
            while (_running)
            {
                int n;
                if (!ReadFile(_readHandle, buf, buf.Length, out n, IntPtr.Zero))
                    break;
                if (n <= 0)
                    continue;

                EventPacket p = Parse(buf, n);
                if (p == null)
                    continue;

                lock (_sync)
                {
                    _events.AddLast(p);
                    while (_events.Count > 64) _events.RemoveFirst();
                    Monitor.PulseAll(_sync);
                }
            }
        }

        /// <summary>把一个 64 字节输入报告解析成事件包；不是合法事件时返回 null。</summary>
        static EventPacket Parse(byte[] r, int n)
        {
            if (n < 14 || r[0] != 0x02)
                return null;

            int len = r[1];
            if (len < 12 || 2 + len > n)
                return null;

            // 事件包从 r[2] 开始：04 FF len 00 96 C3 地址 事件ID 类型 TID... 校验
            if (r[2] != 0x04 || r[3] != 0xFF || r[6] != 0x96 || r[7] != 0xC3)
                return null;
            if ((r[8] >> 4) != 0x1)      // 目的地址必须是 PC
                return null;

            int paramLen = len - 12;
            if (paramLen < 0)
                return null;

            var param = new byte[paramLen];
            if (paramLen > 0)
                Array.Copy(r, 2 + 11, param, 0, paramLen);

            var ev = new EventPacket();
            ev.EventId = r[9];
            ev.EventType = r[10];
            ev.Tid = (ushort)(r[11] | (r[12] << 8));
            ev.Param = param;
            return ev;
        }

        // ------------------------------------------------------------------
        // 设备发现（SetupAPI）
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")]
        static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")]
        static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
        [DllImport("hid.dll")]
        static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int needed, IntPtr info);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr ov);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(IntPtr h, IntPtr ov);

        static bool FindControlPath(out string path, out ushort pid)
        {
            path = null;
            pid = 0;

            Guid guid;
            HidD_GetHidGuid(out guid);

            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE)
                return false;

            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));

                for (uint i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did))
                        break;

                    string candidate = GetDevicePath(set, ref did);
                    if (candidate == null)
                        continue;

                    IntPtr h = CreateFile(candidate, GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == INVALID_HANDLE)
                        continue;

                    try
                    {
                        var attr = new HIDD_ATTRIBUTES();
                        attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        if (!HidD_GetAttributes(h, ref attr))
                            continue;
                        if (attr.VendorID != SonyVendorId)
                            continue;

                        IntPtr preparsed;
                        if (!HidD_GetPreparsedData(h, out preparsed))
                            continue;

                        var caps = new HIDP_CAPS();
                        int status;
                        try
                        {
                            status = HidP_GetCaps(preparsed, ref caps);
                        }
                        finally
                        {
                            HidD_FreePreparsedData(preparsed);
                        }

                        if (status < 0)
                            continue;
                        if (caps.UsagePage != UsagePageControl)
                            continue;
                        if (caps.OutputReportByteLength < 64)
                            continue;

                        path = candidate;
                        pid = attr.ProductID;
                        return true;
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return false;
        }

        static string GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did)
        {
            int needed;
            SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out needed, IntPtr.Zero);
            if (needed <= 0)
                return null;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                // 32 位：cbSize=6，路径从偏移 4 开始
                // 64 位：cbSize=8，路径从偏移 8 开始
                bool x64 = IntPtr.Size == 8;
                int pathOffset = x64 ? 8 : 4;
                Marshal.WriteInt32(buffer, 0, x64 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, needed, out needed, IntPtr.Zero))
                    return null;

                // 不同位数/系统版本下字符串起始偏移不一致，直接扫一遍找 "\\?\"
                long baseAddr = buffer.ToInt64();
                for (int off = 4; off <= 16; off += 2)
                {
                    string s = Marshal.PtrToStringUni(new IntPtr(baseAddr + off));
                    if (s != null && (s.StartsWith("\\\\?\\") || s.StartsWith("\\??\\")))
                        return s;
                }

                return Marshal.PtrToStringUni(new IntPtr(baseAddr + pathOffset));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>列出所有 HID 集合，排障用。</summary>
        public static string Diagnose()
        {
            var sb = new System.Text.StringBuilder();
            Guid guid;
            HidD_GetHidGuid(out guid);

            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE)
                return "SetupDiGetClassDevs failed: " + Marshal.GetLastWin32Error();

            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));

                for (uint i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did))
                        break;

                    string p = GetDevicePath(set, ref did);
                    if (p == null)
                    {
                        sb.AppendLine("path unavailable");
                        continue;
                    }

                    IntPtr h = CreateFile(p, GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == INVALID_HANDLE)
                    {
                        sb.AppendLine("open failed err=" + Marshal.GetLastWin32Error() + " " + p);
                        continue;
                    }

                    try
                    {
                        var attr = new HIDD_ATTRIBUTES();
                        attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        HidD_GetAttributes(h, ref attr);

                        var caps = new HIDP_CAPS();
                        int status = -1;
                        IntPtr preparsed;
                        if (HidD_GetPreparsedData(h, out preparsed))
                        {
                            status = HidP_GetCaps(preparsed, ref caps);
                            HidD_FreePreparsedData(preparsed);
                        }

                        sb.AppendLine(string.Format(
                            "VID={0:X4} PID={1:X4} UP={2:X4} USAGE={3:X4} OUT={4} IN={5} st={6:X8} {7}",
                            attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage,
                            caps.OutputReportByteLength, caps.InputReportByteLength, status, p));
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return sb.ToString();
        }
    }
}
