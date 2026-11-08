using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SHSHCheckerWin.Core;

/// <summary>爱思备份列表里的一条记录。</summary>
public sealed record I4BackupItem(string Ios, IReadOnlyList<string> Extras);

/// <summary>一次 SHSH 下载的结果。</summary>
public sealed class ShshDownloadResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public byte[]? Content { get; init; }
    public string SuggestedFileName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string RequestUrl { get; init; } = "";
    public string PayloadJson { get; init; } = "";
    public string CipherBase64 { get; init; } = "";
    public string RawText { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
}

/// <summary>一次查询的结果（包括调试用的原文、请求 URL）。</summary>
public sealed class I4QueryResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? Code { get; init; }
    public IReadOnlyList<I4BackupItem> Items { get; init; } = Array.Empty<I4BackupItem>();
    public string RawResponse { get; init; } = "";
    public string RequestUrl { get; init; } = "";
    public string PayloadJson { get; init; } = "";
    public string CipherBase64 { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
}

/// <summary>一次上传（备份到 i4 服务器）的结果。</summary>
public sealed record I4UploadResult(
    bool Success,
    string Message,
    string PayloadJson,
    string RequestUrl,
    string RawResponse,
    TimeSpan Elapsed);

/// <summary>
/// 爱思（i4）SHSH 备份列表接口客户端。
/// 对应原项目 index.php 里这一段：
///   $i4_url    = 'https://i4tool2.i4.cn/requestBackupSHSHList.xhtml?param=' . urlencode(encrypt_param);
///   $i4_data   = json_decode(file_get_contents($i4_url), true);
/// 原生程序里不需要 PHP 代理，直接发请求即可（没有 CORS 限制）。
/// </summary>
public sealed class I4Client : IDisposable
{
    public const string ListUrlPrefix = "https://i4tool2.i4.cn/requestBackupSHSHList.xhtml?param=";
    public const string DownloadUrlPrefix = "https://i4tool2.i4.cn/downloadConvertSHSH.xhtml?param=";
    public const string UploadUrlPrefix = "https://i4tool2.i4.cn/uploadSHSH.xhtml?param=";

    /// <summary>上传参数里的客户端版本号（i4Tools 7.91 抓包值，服务器只做记录）。</summary>
    public const string ClientVersion = "7.91.025";

    private readonly HttpClient _http;

    public I4Client(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    /// <summary>生成查询 URL（不发送请求），便于界面里展示调试信息。</summary>
    public static (string Json, string Cipher, string Url) BuildListRequest(ulong ecid, string model, long? timeMs = null)
    {
        var time = timeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = I4Signature.BuildJson(ecid, model, time);
        var cipher = I4Signature.EncryptToBase64(json);
        return (json, cipher, ListUrlPrefix + Uri.EscapeDataString(cipher));
    }

    /// <summary>查询指定机型的爱思 SHSH 备份列表。</summary>
    public async Task<I4QueryResult> QueryBackupsAsync(ulong ecid, string model, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (json, cipher, url) = BuildListRequest(ecid, model);

        string raw;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(raw))
            {
                return Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", raw, json, cipher, url, sw.Elapsed);
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("请求超时（服务器无响应，或本机网络无法访问 i4tool2.i4.cn）", "", json, cipher, url, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return Fail("网络错误：" + ex.Message, "", json, cipher, url, sw.Elapsed);
        }

        sw.Stop();
        return Parse(raw, json, cipher, url, sw.Elapsed);
    }

    /// <summary>生成下载 URL（不发送请求）。明文比查询多一个 ios 字段，值为列表里的 ios（带 build，如 9.3.5(13G36)）。</summary>
    public static (string Json, string Cipher, string Url) BuildDownloadRequest(ulong ecid, string model, string ios, long? timeMs = null)
    {
        var time = timeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = I4Signature.BuildJson(ecid, model, time, ios);
        var cipher = I4Signature.EncryptToBase64(json);
        return (json, cipher, DownloadUrlPrefix + Uri.EscapeDataString(cipher));
    }

    /// <summary>
    /// 上传 SHSH 到 i4 服务器的参数（从 i4Tools 7.91 抓包得到）。
    /// 除 ecid/model/ios/time 外，还需要 nonce（设备 APNonce，40 位十六进制）、isSHSH2、hasBbTicket、pcver 字段，
    /// 且 ecid/time 以字符串下发。body 直接是 shsh 文件原文。
    /// </summary>
    public static (string Json, string Cipher, string Url) BuildUploadRequest(
        ulong ecid,
        string model,
        string ios,
        string nonce,
        bool isShsh2 = false,
        bool hasBbTicket = true,
        long? timeMs = null)
    {
        var time = timeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = string.Join(
            ",",
            "{\"ecid\":\"" + ecid.ToString(CultureInfo.InvariantCulture) + "\"",
            "\"hasBbTicket\":\"" + (hasBbTicket ? "1" : "0") + "\"",
            "\"ios\":\"" + EscapeJsonText(ios) + "\"",
            "\"isSHSH2\":\"" + (isShsh2 ? "1" : "0") + "\"",
            "\"model\":\"" + EscapeJsonText(model) + "\"",
            "\"nonce\":\"" + nonce + "\"",
            "\"pcver\":\"" + ClientVersion + "\"",
            "\"time\":\"" + time.ToString(CultureInfo.InvariantCulture) + "\"}");
        var cipher = I4Signature.EncryptToBase64(json);
        return (json, cipher, UploadUrlPrefix + Uri.EscapeDataString(cipher));
    }

    /// <summary>JSON 字符串转义（与 I4Signature 内部实现一致）。</summary>
    private static string EscapeJsonText(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 用已解析好的文件信息上传（新老格式都适用）。
    /// 老格式（iOS 11 及以下）plist 里通常没有 APNonce，用 40 个 0 作为占位值；
    /// 但现代 shsh2 的 nonce 是 ticket 身份，缺失时必须拒绝（R-2）。
    /// </summary>
    public async Task<I4UploadResult> UploadShshFileAsync(ShshFileInfo info, byte[] shsh, CancellationToken ct = default)
    {
        var reason = info.MissingReason();
        if (reason is not null)
        {
            return new I4UploadResult(false, reason, string.Empty, string.Empty, string.Empty, TimeSpan.Zero);
        }

        if (info.IsShsh2 && string.IsNullOrEmpty(info.Nonce))
        {
            return new I4UploadResult(
                false,
                "shsh2 文件里没有 APNonce（ticket 的 BNCH），无法上传",
                string.Empty,
                string.Empty,
                string.Empty,
                TimeSpan.Zero);
        }

        var nonce = string.IsNullOrEmpty(info.Nonce) ? new string('0', 40) : info.Nonce;
        return await UploadCoreAsync(info.Ecid, info.ProductType, info.IosLabel, nonce, info.IsShsh2, info.HasBbTicket, shsh, ct);
    }

    /// <summary>把 i4 上传接口的业务码翻译成可读文案（原始响应仍保留在 RawResponse 里）。</summary>
    private static string ExplainUploadMessage(string rawMessage, bool success)
    {
        var code = rawMessage.Trim();
        if (code is "[1]" or "1")
        {
            return success
                ? "服务器已接受该备份"
                : "服务器拒绝该备份（可能格式或内容不被接受、该版本已不受支持，或触发风控）";
        }

        return rawMessage;
    }

    private async Task<I4UploadResult> UploadCoreAsync(
        ulong ecid,
        string model,
        string ios,
        string nonce,
        bool isShsh2,
        bool hasBbTicket,
        byte[] shsh,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (json, _, url) = BuildUploadRequest(ecid, model, ios, nonce, isShsh2, hasBbTicket);

        try
        {
            using var content = new ByteArrayContent(shsh);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();

            var success = raw.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase);
            var message = raw;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    message = ExplainUploadMessage(MessageOf(root) ?? raw, success);
                }
            }
            catch (JsonException)
            {
                // 保持原文
            }

            return new I4UploadResult(success, message, json, url, raw, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new I4UploadResult(false, "上传超时", json, url, string.Empty, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new I4UploadResult(false, "网络错误：" + ex.Message, json, url, ex.ToString(), sw.Elapsed);
        }
    }

    /// <summary>从 shsh plist 里取 nonce：优先 &lt;key&gt;nonce&lt;/key&gt; 的十六进制字符串，其次 ApNonce 的 data。</summary>
    public static bool TryExtractNonce(string plist, out string nonce)
    {
        nonce = string.Empty;
        var match = Regex.Match(plist, @"<key>nonce</key>\s*<string>\s*([0-9a-fA-F]{16,128})\s*</string>");
        if (match.Success)
        {
            nonce = match.Groups[1].Value.ToLowerInvariant();
            return true;
        }

        var ap = Regex.Match(plist, @"<key>ApNonce</key>\s*<data>\s*([A-Za-z0-9+/=\s]+?)\s*</data>");
        if (ap.Success)
        {
            try
            {
                var bytes = Convert.FromBase64String(Regex.Replace(ap.Groups[1].Value, @"\s+", string.Empty));
                nonce = Convert.ToHexString(bytes).ToLowerInvariant();
                return nonce.Length > 0;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>plist 里某 key 的 &lt;data&gt; 是否有内容。</summary>
    public static bool HasNonEmptyPlistData(string plist, string keyName)
    {
        var keyIndex = plist.IndexOf("<key>" + keyName + "</key>", StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return false;
        }

        var dataStart = plist.IndexOf("<data>", keyIndex, StringComparison.Ordinal);
        if (dataStart < 0)
        {
            return false;
        }

        var dataEnd = plist.IndexOf("</data>", dataStart, StringComparison.Ordinal);
        if (dataEnd < 0)
        {
            return false;
        }

        return plist.Substring(dataStart + 6, dataEnd - dataStart - 6).Any(ch => !char.IsWhiteSpace(ch));
    }

    /// <summary>下载单个 SHSH 文件。服务器正常时直接返回二进制；出错时通常返回 JSON。</summary>
    public async Task<ShshDownloadResult> DownloadShshAsync(ulong ecid, string model, string ios, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (json, cipher, url) = BuildDownloadRequest(ecid, model, ios);

        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            sw.Stop();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var looksJson = LooksLikeJson(bytes);

            if (bytes.Length == 0)
            {
                return new ShshDownloadResult
                {
                    Success = false,
                    Message = $"服务器返回空内容（HTTP {(int)response.StatusCode}）",
                    RequestUrl = url,
                    PayloadJson = json,
                    CipherBase64 = cipher,
                    ContentType = contentType,
                    Elapsed = sw.Elapsed,
                };
            }

            if (looksJson || contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                // 下载接口返回的是 {"success":true,"shsh":"<plist ...>"} —— SHSH 的 plist 文本被包在 JSON 里。
                // 原网页直接跳转该 URL，保存下来的是 JSON 包装；这里解开，落盘真正的 .shsh 文件。
                return UnpackDownloadJson(Encoding.UTF8.GetString(bytes), ecid, model, ios, json, cipher, url, contentType, sw.Elapsed);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ShshDownloadResult
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{bytes.Length} 字节）",
                    RequestUrl = url,
                    PayloadJson = json,
                    CipherBase64 = cipher,
                    ContentType = contentType,
                    Elapsed = sw.Elapsed,
                };
            }

            var serverName = SafeServerFileName(
                response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName);
            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = string.Empty;
            }
            else if (!serverName.Contains('.', StringComparison.Ordinal))
            {
                serverName += ".shsh";
            }

            return new ShshDownloadResult
            {
                Success = true,
                Content = bytes,
                SuggestedFileName = string.IsNullOrWhiteSpace(serverName)
                    ? BuildShshFileName(ecid, model, ios, "i4")
                    : SanitizeFileName(serverName),
                ContentType = contentType,
                RequestUrl = url,
                PayloadJson = json,
                CipherBase64 = cipher,
                Elapsed = sw.Elapsed,
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return FailDownload("下载超时（服务器无响应，或本机网络无法访问 i4tool2.i4.cn）", json, cipher, url, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return FailDownload("网络错误：" + ex.Message, json, cipher, url, sw.Elapsed);
        }
    }

    /// <summary>
    /// 生成下载文件名：{ProductType}_{版本}_{build}_{ECID}_{来源}.shsh
    /// 例：iPad2,1_9.3.5_13G36_0011AABBCCDDEEFF_i4.shsh
    /// ios 形如 "9.3.5(13G36)"；没有 build 时省略该段。
    /// </summary>
    public static string BuildShshFileName(ulong ecid, string model, string ios, string source, bool shsh2 = false)
    {
        var (version, build) = SplitIos(ios);
        var parts = new List<string> { model, version };
        if (!string.IsNullOrEmpty(build))
        {
            parts.Add(build);
        }

        parts.Add(ecid.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
        parts.Add(source);
        return SanitizeFileName(string.Join('_', parts) + (shsh2 ? ".shsh2" : ".shsh"));
    }

    /// <summary>把 "9.3.5(13G36)" 拆成版本与 build。</summary>
    public static (string Version, string Build) SplitIos(string ios)
    {
        var value = (ios ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return ("unknown", string.Empty);
        }

        var match = Regex.Match(value, @"^(?<version>[^(]*?)\s*(?:\((?<build>[^)]+)\))?$");
        if (!match.Success)
        {
            return (value, string.Empty);
        }

        var version = match.Groups["version"].Value.Trim();
        var build = match.Groups["build"].Success ? match.Groups["build"].Value.Trim() : string.Empty;
        return (version.Length == 0 ? "unknown" : version, build);
    }

    /// <summary>去掉 Windows 文件名非法字符。</summary>
    public static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                || char.IsControl(ch)
                ? '_'
                : ch);
        }

        return builder.ToString();
    }

    /// <summary>只接受纯文件名：去掉引号/路径，拒绝 <c>..</c> 与绝对路径，避免落盘到意外位置。</summary>
    private static string SafeServerFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var name = raw.Trim().Trim('"');
        name = name.Replace('/', '_').Replace('\\', '_');

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var file = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(file) || file is "." or "..")
        {
            return string.Empty;
        }

        if (!file.Contains('.', StringComparison.Ordinal))
        {
            file += ".shsh";
        }

        return SanitizeFileName(file);
    }

    private static bool LooksLikeJson(byte[] bytes)
    {
        var index = 0;

        // 跳过 UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            index = 3;
        }

        for (; index < bytes.Length; index++)
        {
            var b = bytes[index];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            // JSON 对象或数组都算 JSON
            return b is (byte)'{' or (byte)'[';
        }

        return false;
    }

    /// <summary>解开下载接口的 JSON 包装，取出 plist 文本作为 SHSH 文件内容。</summary>
    private static ShshDownloadResult UnpackDownloadJson(
        string text,
        ulong ecid,
        string model,
        string ios,
        string payloadJson,
        string cipher,
        string url,
        string contentType,
        TimeSpan elapsed)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // JSON 数组/数字等根节点没有属性，TryGetProperty 会抛 InvalidOperationException
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ShshDownloadResult
                {
                    Success = false,
                    Message = $"返回内容不是 JSON 对象（{root.ValueKind}）",
                    RequestUrl = url,
                    PayloadJson = payloadJson,
                    CipherBase64 = cipher,
                    ContentType = contentType,
                    RawText = text,
                    Elapsed = elapsed,
                };
            }

            if (root.TryGetProperty("shsh", out var shshElement) && shshElement.ValueKind == JsonValueKind.String)
            {
                var plist = shshElement.GetString() ?? string.Empty;
                if (plist.Length > 0)
                {
                    return new ShshDownloadResult
                    {
                        Success = true,
                        Content = new UTF8Encoding(false).GetBytes(plist),
                        SuggestedFileName = BuildShshFileName(ecid, model, ios, "i4"),
                        ContentType = "application/x-apple-plist",
                        RequestUrl = url,
                        PayloadJson = payloadJson,
                        CipherBase64 = cipher,
                        RawText = text,
                        Elapsed = elapsed,
                    };
                }
            }

            return new ShshDownloadResult
            {
                Success = false,
                Message = MessageOf(root) ?? "服务器没有返回 shsh 内容",
                RequestUrl = url,
                PayloadJson = payloadJson,
                CipherBase64 = cipher,
                ContentType = contentType,
                RawText = text,
                Elapsed = elapsed,
            };
        }
        catch (JsonException)
        {
            return new ShshDownloadResult
            {
                Success = false,
                Message = "返回内容不是有效 JSON",
                RequestUrl = url,
                PayloadJson = payloadJson,
                CipherBase64 = cipher,
                ContentType = contentType,
                RawText = text,
                Elapsed = elapsed,
            };
        }
    }

    /// <summary>从返回 JSON 里取提示信息（兼容 msg / message）。</summary>
    public static string? MessageOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var message = ReadScalar(root, "msg") ?? ReadScalar(root, "message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        var code = ReadScalar(root, "code") ?? ReadScalar(root, "co");
        return string.IsNullOrWhiteSpace(code) ? null : $"服务器返回 code={code}";
    }

    private static ShshDownloadResult FailDownload(string message, string json, string cipher, string url, TimeSpan elapsed) => new()
    {
        Success = false,
        Message = message,
        RequestUrl = url,
        PayloadJson = json,
        CipherBase64 = cipher,
        Elapsed = elapsed,
    };

    /// <summary>解析爱思返回的 JSON。接口字段没有公开文档，这里做宽松解析并把未知字段原样展示。</summary>
    public static I4QueryResult Parse(string raw, string json, string cipher, string url, TimeSpan elapsed)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return Fail("服务器返回空内容", raw, json, cipher, url, elapsed);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            var preview = raw.Length > 300 ? raw[..300] + "…" : raw;
            return Fail("返回内容不是 JSON（可能被风控或网关拦截）：" + preview, raw, json, cipher, url, elapsed);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail("返回内容不是 JSON 对象", raw, json, cipher, url, elapsed);
            }

            var code = ReadScalar(root, "code") ?? ReadScalar(root, "status") ?? ReadScalar(root, "co");
            var message = ReadScalar(root, "msg") ?? ReadScalar(root, "message") ?? "";
            var successFlag = root.TryGetProperty("success", out var successElement)
                ? successElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null,
                }
                : null;

            if (root.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                var items = new List<I4BackupItem>();
                foreach (var element in listElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var ios = ReadScalar(element, "ios") ?? ReadScalar(element, "ios_order") ?? "";
                    var extras = new List<string>();
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name is "ios")
                        {
                            continue;
                        }

                        extras.Add(property.Name + "=" + ScalarText(property.Value));
                    }

                    items.Add(new I4BackupItem(ios, extras));
                }

                return new I4QueryResult
                {
                    Success = true,
                    Message = message,
                    Code = code,
                    Items = items,
                    RawResponse = raw,
                    RequestUrl = url,
                    PayloadJson = json,
                    CipherBase64 = cipher,
                    Elapsed = elapsed,
                };
            }

            // 该接口在“没有备份记录”时返回的是 {"success":true,"co":0} 这种不含 list 的形状，
            // 应视为查询成功但 0 条记录，而不是错误。
            if (successFlag == true)
            {
                return new I4QueryResult
                {
                    Success = true,
                    Message = message.Length > 0 ? message : "服务器返回 success=true，但没有备份记录",
                    Code = code,
                    RawResponse = raw,
                    RequestUrl = url,
                    PayloadJson = json,
                    CipherBase64 = cipher,
                    Elapsed = elapsed,
                };
            }

            return new I4QueryResult
            {
                Success = false,
                Message = message.Length > 0
                    ? message
                    : successFlag == false
                        ? "服务器返回 success=false（签名或参数被拒绝）"
                        : "接口没有返回 list 字段",
                Code = code,
                RawResponse = raw,
                RequestUrl = url,
                PayloadJson = json,
                CipherBase64 = cipher,
                Elapsed = elapsed,
            };
        }
    }

    private static I4QueryResult Fail(string message, string raw, string json, string cipher, string url, TimeSpan elapsed) => new()
    {
        Success = false,
        Message = message,
        RawResponse = raw,
        RequestUrl = url,
        PayloadJson = json,
        CipherBase64 = cipher,
        Elapsed = elapsed,
    };

    private static string? ReadScalar(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? ScalarText(value) : null;

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText(),
    };

    public void Dispose() => _http.Dispose();
}



