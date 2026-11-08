using System.Globalization;

namespace SHSHCheckerWin.Core;

/// <summary>
/// ECID 解析：把用户输入解析成 64 位无符号整数（原 JS 的 parseInt 会在 2^53 以上丢精度）。
/// 输入可以是：
///   - 十六进制（可带 0x 前缀、大小写、空格/下划线），如 <c>0011AABBCCDDEEFF</c>、<c>0x00AABBCCDD</c>
///   - 十进制（13–16 位纯数字），如 <c>1234567890123</c>、<c>1234567890123456</c>
/// 纯数字且长度 ≥13 时按十进制处理（这是从 shsh 文件名开头复制的常见形式）；
/// 想按十六进制解析纯数字时加 <c>0x</c> 前缀即可。
/// </summary>
public static class EcidParser
{
    /// <summary>把内容按十进制解析时的最小长度（十进制 ECID 通常是 13–16 位）。</summary>
    private const int DecimalAutoDetectMinLength = 13;

    /// <summary>解析输入；<paramref name="usedDecimal"/> 表示本次是按十进制解释的。</summary>
    public static bool TryParse(string? text, out ulong ecid, out string error)
        => TryParse(text, out ecid, out error, out _);

    /// <summary>解析输入，并返回具体按哪种进制解释（便于界面提示）。</summary>
    public static bool TryParse(string? text, out ulong ecid, out string error, out bool usedDecimal)
    {
        ecid = 0;
        error = string.Empty;
        usedDecimal = false;

        if (text is null)
        {
            error = "ECID 不能为空";
            return false;
        }

        var value = text.Trim().Replace(" ", string.Empty).Replace("_", string.Empty);
        var explicitHex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (explicitHex)
        {
            value = value[2..];
        }

        if (value.Length == 0)
        {
            error = "ECID 不能为空";
            return false;
        }

        // 纯数字且足够长 → 十进制（shsh 文件名开头就是十进制 ECID）
        var allDigits = value.All(char.IsAsciiDigit);
        if (!explicitHex && allDigits && value.Length >= DecimalAutoDetectMinLength)
        {
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ecid))
            {
                error = "ECID 超出 64 位范围";
                return false;
            }

            usedDecimal = true;
        }
        else
        {
            if (value.Length > 16)
            {
                error = "十六进制 ECID 最多 16 位（64 位）；如果是十进制 ECID，请直接粘贴（13 位以上会自动识别）";
                return false;
            }

            if (!ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ecid))
            {
                error = "ECID 含有非十六进制字符";
                return false;
            }
        }

        if (ecid == 0)
        {
            error = "ECID 不能为 0";
            return false;
        }

        return true;
    }

    /// <summary>格式化成 16 位大写十六进制（与 iFaith 的 ECID 写法一致）。</summary>
    public static string ToHex16(ulong ecid) => ecid.ToString("X16", CultureInfo.InvariantCulture);
}

