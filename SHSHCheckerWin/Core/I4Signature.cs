using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SHSHCheckerWin.Core;

/// <summary>
/// 复刻原项目 index.php / 3des.js 里的签名逻辑：
///   encrypt_3DES(json, '2015aisi1234sj7890smartflashi4pc', 0 /*iv*/, 1 /*mode=ECB*/, 0 /*padding=PKCS7*/)
///
/// 三个容易踩错的细节：
///  1. CryptoJS 的 TripleDES 只使用密钥的前 24 字节，超长部分被丢弃 —— 所以真正生效的是
///     "2015aisi1234sj7890smartf"（原串 32 字节）。
///  2. 原调用开启的 encrypt_mode = 1 是 ECB；padding = 0 是 PKCS7。
///  3. 密钥/IV 都是直接传入的 WordArray，CryptoJS 不会做 OpenSSL 的 "Salted__" 加盐，
///     因此 ciphertext.toString() 就是裸 Base64。
/// </summary>
public static class I4Signature
{
    /// <summary>原项目里写死的密钥串（实际使用前 24 字节）。</summary>
    public const string RawKey = "2015aisi1234sj7890smartflashi4pc";

    /// <summary>3DES 实际生效的 24 字节密钥（UTF-8）。</summary>
    private static readonly byte[] KeyBytes = Encoding.UTF8.GetBytes(RawKey[..24]);

    /// <summary>按原 JS 顺序拼出紧凑 JSON，字段顺序必须是 ecid、model、time、[ios]。</summary>
    public static string BuildJson(ulong ecid, string model, long timeMs, string? ios = null)
    {
        var sb = new StringBuilder(128);
        sb.Append("{\"ecid\":").Append(ecid.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"model\":\"").Append(EscapeJson(model)).Append('"');
        sb.Append(",\"time\":").Append(timeMs.ToString(CultureInfo.InvariantCulture));
        if (ios is not null)
        {
            sb.Append(",\"ios\":\"").Append(EscapeJson(ios)).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>3DES/ECB/PKCS7 加密，返回裸 Base64（对应 CryptoJS 的 encrypted.toString()）。</summary>
    public static string EncryptToBase64(string plainText) => EncryptToBase64WithKey(plainText, RawKey);

    /// <summary>用指定密钥串（取前 24 字节）做 3DES/ECB/PKCS7 加密，输出裸 Base64。</summary>
    public static string EncryptToBase64WithKey(string plainText, string rawKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(rawKey.Length >= 24 ? rawKey[..24] : rawKey);
        using var tdes = TripleDES.Create();
        tdes.Mode = CipherMode.ECB;
        tdes.Padding = PaddingMode.PKCS7;
        tdes.Key = keyBytes;

        var data = Encoding.UTF8.GetBytes(plainText);
        using var encryptor = tdes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(data, 0, data.Length));
    }

    /// <summary>反向解密，仅用于自检（验证模式/填充/密钥是否自洽）。</summary>
    public static string DecryptFromBase64(string cipherBase64) => DecryptFromBase64WithKey(cipherBase64, RawKey);

    /// <summary>用指定密钥串（取前 24 字节）解密裸 Base64 密文。</summary>
    public static string DecryptFromBase64WithKey(string cipherBase64, string rawKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(rawKey.Length >= 24 ? rawKey[..24] : rawKey);
        using var tdes = TripleDES.Create();
        tdes.Mode = CipherMode.ECB;
        tdes.Padding = PaddingMode.PKCS7;
        tdes.Key = keyBytes;

        var data = Convert.FromBase64String(cipherBase64);
        using var decryptor = tdes.CreateDecryptor();
        return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(data, 0, data.Length));
    }

    /// <summary>加密 → 解密往返自检，启动时可调用一次确认算法可用。</summary>
    public static bool SelfTest()
    {
        const string probe = "{\"ecid\":123456789,\"model\":\"iPhone8,1\",\"time\":1700000000000}";
        try
        {
            return DecryptFromBase64(EncryptToBase64(probe)) == probe;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>与 JSON.stringify 等价的转义：引号、反斜杠、控制字符（\b \f \n \r \t 与 \u00XX）。</summary>
    private static string EscapeJson(string value)
    {
        if (value.AsSpan().IndexOfAny('\\', '"') < 0 && !value.Any(char.IsControl))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
