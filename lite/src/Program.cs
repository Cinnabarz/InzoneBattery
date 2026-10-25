using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace InzoneBattery
{
    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                EnableConsole();
                Environment.Exit(RunCli(args));
                return;
            }

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogCrash(e.ExceptionObject as Exception);
            };
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                LogCrash(e.Exception);
            };

            bool created;
            using (var mutex = new System.Threading.Mutex(true, "Local\\InzoneBatteryLite.Tray", out created))
            {
                if (!created)
                    return;

                try
                {
                    using (var app = new TrayApp())
                    {
                        Application.Run(app);
                    }
                }
                catch (Exception ex)
                {
                    LogCrash(ex);
                }
            }
        }

        static void LogCrash(Exception ex)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InzoneBatteryLite-crash.log");
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    (ex == null ? "null" : ex.ToString()) + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static void EnableConsole()
        {
            // 以 WinExe 编译，从命令行运行 --once 时借用父进程的控制台
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();

            var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput());
            stdout.AutoFlush = true;
            Console.SetOut(stdout);
        }

        static int RunCli(string[] args)
        {
            string mode = args[0].ToLowerInvariant();

            if (mode == "--help" || mode == "-h" || mode == "/?")
            {
                Console.WriteLine("InzoneBatteryLite - Sony INZONE 耳机电量（精简版，只有托盘图标）");
                Console.WriteLine();
                Console.WriteLine("  InzoneBatteryLite.exe            启动托盘图标");
                Console.WriteLine("  InzoneBatteryLite.exe --once     输出一次电量（纯数字，离线输出 offline）");
                Console.WriteLine("  InzoneBatteryLite.exe --json     输出一次电量（JSON）");
                Console.WriteLine("  InzoneBatteryLite.exe --watch N  每 N 秒轮询，变化时输出");
                Console.WriteLine("  InzoneBatteryLite.exe --diag     列出 HID 集合，排障用");
                Console.WriteLine("  InzoneBatteryLite.exe --icon F   把托盘图标渲染成 PNG，方便看效果");
                Console.WriteLine("  InzoneBatteryLite.exe --icon-old F  渲染旧版横向图标");
                Console.WriteLine("  InzoneBatteryLite.exe --startup [on|off|status]  开机自启动的查看/开关");
                return 0;
            }

            if (mode == "--diag")
            {
                Console.Write(InzoneDevice.Diagnose());
                return 0;
            }

            if (mode == "--icon" || mode == "--icon-old")
            {
                string file = args.Length > 1 ? args[1] : "tray-icon.png";
                int size = 64;
                if (args.Length > 2) int.TryParse(args[2], out size);
                if (size < 8) size = 8;
                using (var bmp = mode == "--icon-old"
                    ? TrayIcon.RenderHorizontal(size, 100, true)
                    : TrayIcon.Render(size, 100, true))
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("saved " + file);
                return 0;
            }

            if (mode == "--startup")
            {
                string arg = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                if (arg == "on" || arg == "off")
                {
                    string error;
                    bool ok = StartupManager.Set(arg == "on", out error);
                    if (ok)
                        Console.WriteLine("startup " + arg + " ok: " + (StartupManager.CurrentValue() ?? "(none)"));
                    else
                        Console.WriteLine("startup " + arg + " failed: " + (error ?? "unknown"));
                    return ok ? 0 : 1;
                }

                Console.WriteLine("startup = " + (StartupManager.IsEnabled() ? "on" : "off"));
                Console.WriteLine("value   = " + (StartupManager.CurrentValue() ?? "(none)"));
                Console.WriteLine("exe     = " + StartupManager.ExecutablePath);
                Console.WriteLine("log     = " + StartupManager.LogPath);
                return 0;
            }

            if (mode == "--watch")
            {
                int interval = 5;
                if (args.Length > 1)
                    int.TryParse(args[1], out interval);
                if (interval < 1) interval = 1;
                return Watch(interval);
            }

            bool json = mode == "--json";
            return Once(json);
        }

        static int Once(bool json)
        {
            InzoneDevice dev = null;
            try
            {
                dev = InzoneDevice.Open();
                if (dev == null)
                {
                    return Fail(json, "no-receiver", "no INZONE receiver found");
                }

                BatteryReading b = dev.GetBattery(2000);
                if (json)
                {
                    Console.WriteLine("{\"model\":\"" + dev.ModelName + "\",\"online\":" +
                        (b.Online ? "true" : "false") + ",\"percent\":" +
                        (b.Online ? b.Percent.ToString() : "null") + ",\"status\":" +
                        b.Status + ",\"charging\":" + (b.Charging ? "true" : "false") + "}");
                }
                else
                {
                    Console.WriteLine(b.Online ? b.Percent.ToString() : "offline");
                }
                return b.Online ? 0 : 2;
            }
            catch (Exception ex)
            {
                return Fail(json, "error", ex.Message);
            }
            finally
            {
                if (dev != null) dev.Dispose();
            }
        }

        static int Watch(int intervalSeconds)
        {
            InzoneDevice dev = null;
            string last = null;
            try
            {
                while (true)
                {
                    string line;
                    try
                    {
                        if (dev == null) dev = InzoneDevice.Open();
                        if (dev == null) line = "no-receiver";
                        else
                        {
                            BatteryReading b = dev.GetBattery(2000);
                            line = b.Online ? b.Percent.ToString() : "offline";
                        }
                    }
                    catch (Exception)
                    {
                        if (dev != null) { dev.Dispose(); dev = null; }
                        line = "error";
                    }

                    if (line != last)
                    {
                        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
                        last = line;
                    }
                    Thread.Sleep(intervalSeconds * 1000);
                }
            }
            finally
            {
                if (dev != null) dev.Dispose();
            }
        }

        static int Fail(bool json, string code, string message)
        {
            if (json)
                Console.WriteLine("{\"error\":\"" + code + "\",\"message\":\"" + message.Replace("\"", "'") + "\"}");
            else
                Console.WriteLine(code == "no-receiver" ? "no-receiver" : "error: " + message);
            return 1;
        }
    }
}
