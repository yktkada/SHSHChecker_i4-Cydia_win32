using System.Runtime.InteropServices;

namespace SHSHCheckerWin;

internal static class Program
{
    /// <summary>固定 AppUserModelID：让任务栏/固定快捷方式归到同一个图标，而不是 python/默认图标。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static void Main()
    {
        SetCurrentProcessExplicitAppUserModelID("yktkada.SHSHCheckerI4");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
