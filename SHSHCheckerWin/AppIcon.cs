using System.Drawing;
using System.Reflection;

namespace SHSHCheckerWin;

/// <summary>程序图标：优先用嵌入的 app.ico（多尺寸），失败再退回 exe 资源。</summary>
internal static class AppIcon
{
    private const string ResourceName = "SHSHCheckerWin.app.ico";

    internal static Icon? Load()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                return new Icon(stream);
            }
        }
        catch (Exception)
        {
            // 落到下面的备用方案
        }

        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
