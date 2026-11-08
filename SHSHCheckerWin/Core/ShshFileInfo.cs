using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SHSHCheckerWin.Core;

/// <summary>
/// 一个本地 SHSH 文件的元信息。兼容两种来源：
/// 1) 老设备（A4–A6）的 plist：<c>ecid / productType / productVersion / buildVersion / nonce</c> 都在 plist 里；
/// 2) 现代设备（A7+，shsh2）的 plist 只有 <c>ApImg4Ticket</c>：ECID 与 APNonce 从 IMG4 ticket 的
///    <c>ECID</c> / <c>BNCH</c> 属性里解出，机型与版本只能从文件名里取。
/// </summary>
public sealed record ShshFileInfo(
    ulong Ecid,
    string ProductType,
    string Version,
    string Build,
    string Nonce,
    bool IsShsh2,
    bool HasBbTicket)
{
    /// <summary>上传用的 iOS 版本串，形如 <c>9.3.5(13G36)</c>。</summary>
    public string IosLabel => string.IsNullOrEmpty(Build) ? Version : $"{Version}({Build})";

    /// <summary>缺少必要字段时返回的原因，全部齐备时返回 null。</summary>
    public string? MissingReason()
    {
        if (Ecid == 0)
        {
            return "没找到 ECID";
        }

        if (string.IsNullOrEmpty(ProductType))
        {
            return "没识别出机型（文件名需包含 ProductType，如 iPhone14,5）";
        }

        if (string.IsNullOrEmpty(Version))
        {
            return "没识别出系统版本（文件名需包含版本，如 17.6-21G5052e）";
        }

        // 老格式（iOS 11 及以下）plist 里通常没有 APNonce：允许用占位 nonce 尝试上传
        return null;
    }

    /// <summary>读取文件并解析元信息。</summary>
    public static bool TryParse(string path, out ShshFileInfo info)
    {
        info = null!;
        string text;
        try
        {
            var bytes = File.ReadAllBytes(path);
            text = Encoding.UTF8.GetString(bytes);
            return TryParse(path, text, bytes, out info);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryParse(string path, string text, byte[] rawBytes, out ShshFileInfo info)
    {
        info = null!;

        // 1) 老格式：plist 自带全部字段
        var legacy = TryParseLegacyPlist(text);

        // 2) 新格式：从 IMG4 ticket 里取 ECID / nonce
        ulong ticketEcid = 0;
        var ticketNonce = string.Empty;
        var isShsh2 = text.Contains("<key>ApImg4Ticket</key>", StringComparison.Ordinal);
        if (isShsh2)
        {
            var data = Regex.Match(text, "<key>ApImg4Ticket</key>\\s*<data>([\\s\\S]*?)</data>");
            if (data.Success)
            {
                try
                {
                    var der = Convert.FromBase64String(Regex.Replace(data.Groups[1].Value, "\\s+", string.Empty));
                    TryParseImg4(der, out ticketEcid, out ticketNonce);
                }
                catch (FormatException)
                {
                    // ticket 坏了就当没有
                }
            }
        }

        // 3) 文件名里取机型 / 版本 / build / ECID
        var fileName = Path.GetFileName(path);
        TryParseFileName(fileName, out var nameProductType, out var nameVersion, out var nameBuild, out var nameNonce, out var nameEcid);

        var ecid = legacy.Ecid != 0 ? legacy.Ecid : (ticketEcid != 0 ? ticketEcid : nameEcid);
        var productType = !string.IsNullOrEmpty(legacy.ProductType) ? legacy.ProductType : nameProductType;
        var version = !string.IsNullOrEmpty(legacy.Version) ? legacy.Version : nameVersion;
        var build = !string.IsNullOrEmpty(legacy.Build) ? legacy.Build : nameBuild;
        var nonce = !string.IsNullOrEmpty(legacy.Nonce) ? legacy.Nonce : (!string.IsNullOrEmpty(ticketNonce) ? ticketNonce : nameNonce);

        var hasBbTicket = text.Contains("<key>BBTicket</key>", StringComparison.Ordinal);

        // 内容基本校验：不是 plist、或没有任何 ticket 内容的文件不能算 SHSH
        // （否则仅凭文件名就能把任意文本当成备份，N-1/N-2 的按钮状态会过度乐观）
        var looksLikeShsh = text.Contains("<plist", StringComparison.Ordinal)
                            && (text.Contains("<key>APTicket</key>", StringComparison.Ordinal)
                                || text.Contains("ApImg4Ticket", StringComparison.Ordinal)
                                || text.Contains("<key>Blob</key>", StringComparison.Ordinal));
        if (!looksLikeShsh)
        {
            return false;
        }

        info = new ShshFileInfo(
            ecid,
            productType,
            version,
            build,
            nonce,
            isShsh2,
            hasBbTicket);
        return ecid != 0 || !string.IsNullOrEmpty(productType);
    }

    /// <summary>老格式 plist：字段直接可读。</summary>
    private static (ulong Ecid, string ProductType, string Version, string Build, string Nonce) TryParseLegacyPlist(string text)
    {
        ulong ecid = 0;
        var ecidMatch = Regex.Match(text, "<key>ecid</key>\\s*<integer>\\s*(\\d+)\\s*</integer>");
        if (ecidMatch.Success)
        {
            ulong.TryParse(ecidMatch.Groups[1].Value, out ecid);
        }

        var productType = Regex.Match(text, "<key>productType</key>\\s*<string>\\s*([^<]+?)\\s*</string>").Groups[1].Value;
        var version = Regex.Match(text, "<key>productVersion</key>\\s*<string>\\s*([^<]+?)\\s*</string>").Groups[1].Value;
        var build = Regex.Match(text, "<key>buildVersion</key>\\s*<string>\\s*([^<]+?)\\s*</string>").Groups[1].Value;

        // nonce 的解析统一走 I4Client.TryExtractNonce，避免多份正则各说各话
        var nonce = I4Client.TryExtractNonce(text, out var parsedNonce) ? parsedNonce : string.Empty;

        return (ecid, productType, version, build, nonce);
    }

    /// <summary>
    /// 从 IMG4（ApImg4Ticket）里取属性：<c>ECID</c>（INTEGER）与 <c>BNCH</c>（OCTET STRING）。
    /// 属性编码是 `标签4字节 + DER长度(0x02/0x04) + 长度 + 内容`；签名段里同样的标签要跳过。
    /// </summary>
    public static bool TryParseImg4(byte[] der, out ulong ecid, out string nonceHex)
    {
        ecid = 0;
        nonceHex = string.Empty;

        for (var i = 0; i <= der.Length - 6; i++)
        {
            if (ecid == 0 && MatchTag(der, i, "ECID") && der[i + 4] == 0x02)
            {
                var (len, valueOffset) = ReadDerLength(der, i + 5);
                if (len is > 0 and <= 8 && valueOffset + len <= der.Length)
                {
                    ulong value = 0;
                    for (var k = 0; k < len; k++)
                    {
                        value = (value << 8) | der[valueOffset + k];
                    }

                    if (value != 0)
                    {
                        ecid = value;
                    }
                }
            }
            else if (nonceHex.Length == 0 && MatchTag(der, i, "BNCH") && der[i + 4] == 0x04)
            {
                var (len, valueOffset) = ReadDerLength(der, i + 5);
                if (len is > 0 and <= 64 && valueOffset + len <= der.Length)
                {
                    nonceHex = Convert.ToHexString(der, valueOffset, len).ToLowerInvariant();
                }
            }

            if (ecid != 0 && nonceHex.Length > 0)
            {
                return true;
            }
        }

        return ecid != 0 || nonceHex.Length > 0;
    }

    private static bool MatchTag(byte[] data, int offset, string tag)
        => data[offset] == tag[0] && data[offset + 1] == tag[1] && data[offset + 2] == tag[2] && data[offset + 3] == tag[3];

    private static (int Length, int ValueOffset) ReadDerLength(byte[] data, int offset)
    {
        var first = data[offset];
        if ((first & 0x80) == 0)
        {
            return (first, offset + 1);
        }

        var count = first & 0x7F;
        if (count is < 1 or > 4 || offset + count >= data.Length)
        {
            return (0, offset + 1);
        }

        var length = 0;
        for (var i = 0; i < count; i++)
        {
            length = (length << 8) | data[offset + 1 + i];
        }

        return (length, offset + 1 + count);
    }

    /// <summary>从文件名里取机型 / 版本 / build / nonce / ECID（容忍任意自定义命名，只要有这些信息）。</summary>
    public static void TryParseFileName(
        string fileName,
        out string productType,
        out string version,
        out string build,
        out string nonce,
        out ulong ecid)
    {
        productType = Regex.Match(fileName, "(?:iPhone|iPad|iPod|AppleTV|RealityDevice)\\d+,\\d+", RegexOptions.IgnoreCase).Value;
        version = string.Empty;
        build = string.Empty;
        nonce = string.Empty;
        ecid = 0;

        // ECID 三种写法：
        //   000000AABBCCDDEE_...            16 位十六进制
        //   000000AABBCCDDEE(733800360430)-  十六进制 + 括号内十进制
        //   1234567890123-iPad2,1-...       十进制
        var hexEcid = Regex.Match(fileName, @"(?<![0-9A-Fa-f])([0-9A-Fa-f]{8,16})(?![0-9A-Fa-f])");
        var parenEcid = Regex.Match(fileName, @"\((\d{10,17})\)");
        var decEcid = Regex.Match(fileName, @"(?<!\d)(\d{10,17})(?!\d)");
        if (parenEcid.Success)
        {
            ulong.TryParse(parenEcid.Groups[1].Value, out ecid);
        }
        else if (hexEcid.Success && hexEcid.Groups[1].Value.Any(ch => char.IsLetter(ch)))
        {
            ulong.TryParse(hexEcid.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ecid);
        }
        else if (decEcid.Success)
        {
            ulong.TryParse(decEcid.Groups[1].Value, out ecid);
        }
        else if (hexEcid.Success)
        {
            ulong.TryParse(hexEcid.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ecid);
        }

        // 版本+build：17.4-21E217 / 17.4_21E217 / 7.0.4-11B554a
        var pair = Regex.Match(fileName, @"(\d+(?:\.\d+){1,2})[-_ ]([0-9]{1,2}[A-Z][0-9A-Za-z]+)");
        if (pair.Success)
        {
            version = pair.Groups[1].Value;
            build = pair.Groups[2].Value;
        }
        else
        {
            var v = Regex.Match(fileName, @"(\d+(?:\.\d+){1,2})");
            if (v.Success)
            {
                version = v.Groups[1].Value;
            }
        }

        // nonce：40 或 64 位十六进制，且不是纯数字的 ECID
        foreach (Match m in Regex.Matches(fileName, "[0-9a-fA-F]{40}|[0-9a-fA-F]{64}"))
        {
            if (m.Value.Any(ch => ch is >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                nonce = m.Value.ToLowerInvariant();
                break;
            }
        }
    }
}





