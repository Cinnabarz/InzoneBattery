using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InzoneBattery
{
    /// <summary>开机自启动：写 HKCU 的 Run 键，并在失败时留下日志（精简版用独立的键名，避免和完整版互相覆盖）。</summary>
    static class StartupManager
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "InzoneBatteryLite";

        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "InzoneBatteryLite-startup.log"); }
        }

        public static string ExecutablePath
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().MainModule.FileName;
                }
                catch (Exception)
                {
                    return Application.ExecutablePath;
                }
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return key != null && key.GetValue(RunValueName) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string CurrentValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return null;
                    object v = key.GetValue(RunValueName);
                    return v == null ? null : v.ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>从 Run 值里解析出 exe 路径（容忍带引号/带参数/裸路径）。</summary>
        public static string RecordedExecutablePath()
        {
            string value = CurrentValue();
            if (string.IsNullOrEmpty(value)) return null;
            value = value.Trim();
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = value.IndexOf('"', 1);
                if (end > 1) return value.Substring(1, end - 1);
                return value.Trim('"');
            }
            int space = value.IndexOf(' ');
            return space > 0 ? value.Substring(0, space) : value;
        }

        /// <summary>自启动项是否指向一个已经不存在的文件（移动过/删掉了）。</summary>
        public static bool IsStale()
        {
            if (!IsEnabled()) return false;
            string recorded = RecordedExecutablePath();
            if (string.IsNullOrEmpty(recorded)) return true;
            try
            {
                return !File.Exists(recorded);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>开启/关闭自启动；失败时通过 error 返回原因。</summary>
        public static bool Set(bool enabled, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        error = "无法打开 HKCU 的 Run 键";
                        Log("open Run key failed");
                        return false;
                    }

                    if (enabled)
                    {
                        string cmd = "\"" + ExecutablePath + "\"";
                        key.SetValue(RunValueName, cmd);
                        Log("set " + RunValueName + " = " + cmd);
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                        Log("deleted " + RunValueName);
                    }
                }

                bool now = IsEnabled();
                Log("verify: enabled=" + now);
                if (now != enabled)
                {
                    error = "写入后回读状态不一致";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log("error: " + ex);
                return false;
            }
        }

        public static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;

            // 主日志：%TEMP%
            try
            {
                File.AppendAllText(LogPath, line);
                return;
            }
            catch (Exception)
            {
            }

            // 兜底：%TEMP% 不可写时，写到 exe 同目录
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(dir))
                    File.AppendAllText(Path.Combine(dir, "startup.log"), line);
            }
            catch (Exception)
            {
            }
        }
    }
}
