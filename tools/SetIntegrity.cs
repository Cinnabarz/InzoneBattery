// 把文件的中完整性标签设为 Medium（S-1-16-8192）。
// 背景：从低完整性标签（Low）的文件启动进程时，Explorer 会用受限令牌创建进程，
// 结果进程处于 Low 完整性级别，无法向中等完整性的任务栏发送 Shell_NotifyIcon 消息。
using System;
using System.Runtime.InteropServices;

class SetIntegrity
{
    const int SE_FILE_OBJECT = 1;
    const uint LABEL_SECURITY_INFORMATION = 0x00000010;
    const uint SDDL_REVISION_1 = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr sd, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSecurityDescriptorSacl(
        IntPtr sd, out bool present, out IntPtr sacl, out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint SetNamedSecurityInfoW(
        string name, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr h);

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: SetIntegrity.exe <file> [sddl]");
            return 2;
        }

        string file = args[0];
        // ME = Medium mandatory level；NW = no-write-up
        string sddl = args.Length > 1 ? args[1] : "S:(ML;;NW;;;ME)";

        IntPtr sd;
        uint size;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, out sd, out size))
        {
            Console.WriteLine("sddl failed err=" + Marshal.GetLastWin32Error());
            return 1;
        }

        try
        {
            bool present, defaulted;
            IntPtr sacl;
            if (!GetSecurityDescriptorSacl(sd, out present, out sacl, out defaulted) || !present)
            {
                Console.WriteLine("no sacl err=" + Marshal.GetLastWin32Error());
                return 1;
            }

            uint rc = SetNamedSecurityInfoW(file, SE_FILE_OBJECT, LABEL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (rc != 0)
            {
                Console.WriteLine("SetNamedSecurityInfo failed rc=" + rc);
                return 1;
            }

            Console.WriteLine("ok: " + file + " -> " + sddl);
            return 0;
        }
        finally
        {
            LocalFree(sd);
        }
    }
}
