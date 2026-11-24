using System.Globalization;
using System.Resources;

namespace SHSHCheckerWin;

/// <summary>
/// 界面文案。中文构建始终中文，英文构建强制英文（不受系统语言影响）。
/// 资源：Strings.resx（中文，默认）+ Strings.en.resx（英文）。
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("SHSHCheckerWin.Strings", typeof(Strings).Assembly);

    internal static bool ForceEnglish { get; set; }

    private static CultureInfo Culture => ForceEnglish
        ? CultureInfo.GetCultureInfo("en")
        : CultureInfo.GetCultureInfo("zh-CN");

    internal static string Get(string key) => Manager.GetString(key, Culture) ?? key;

    internal static string Format(string key, params object?[] args) => string.Format(Culture, Get(key), args);
}
