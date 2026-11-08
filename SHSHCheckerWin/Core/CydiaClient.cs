using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SHSHCheckerWin.Core;

/// <summary>Cydia 备份列表里的一条记录。</summary>
public sealed record CydiaBackup(
    string Model,
    string Firmware,
    string Build,
    string Chip,
    string Board)
{
    /// <summary>界面显示的版本列，如 "5.1.1(9B206)"。</summary>
    public string Version => Build.Length > 0 ? $"{Firmware}({Build})" : Firmware;

    /// <summary>界面显示的其它信息，如 "chip=35136  board=8"。</summary>
    public string Extras => string.Join(
        "   ",
        new[] { ("chip", Chip), ("board", Board) }
            .Where(pair => pair.Item2.Length > 0)
            .Select(pair => $"{pair.Item1}={pair.Item2}"));
}

/// <summary>一次 Cydia 查询的结果。</summary>
public sealed class CydiaQueryResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<CydiaBackup> Items { get; init; } = Array.Empty<CydiaBackup>();
    public string RawResponse { get; init; } = "";
    public string RequestUrl { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
}

/// <summary>一次 Cydia 下载的结果。</summary>
public sealed class CydiaDownloadResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public byte[]? Content { get; init; }
    public string ManifestUrl { get; init; } = "";
    public string RawResponse { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Cydia 的 SHSH 备份查询与下载（对应 iFaith 1.5.9 的 iAcqua 实现，反编译确认）：
///   列表：GET  {CheckUrlPrefix}&lt;十进制 ECID&gt;
///   清单：GET  {ManifestUrlPrefix}&lt;build&gt;/&lt;chip&gt;/&lt;board&gt;
///   下载：POST {TssUrl}  （body = 把清单里的 $ecid 换成十进制 ECID）
/// 说明：POST 返回 "STATUS=0&amp;MESSAGE=SUCCESS&amp;REQUEST_STRING=&lt;?xml … &lt;/plist&gt;"，
/// 取 &lt;?xml 到 &lt;/plist&gt; 之间的 plist 即为 .shsh。
/// </summary>
public sealed class CydiaClient : IDisposable
{
    public const string CheckUrlPrefix = "https://cydia.saurik.com/tss@home/api/check/";
    public const string ManifestUrlPrefix = "https://cydia.saurik.com/tss@home/api/manifest.xml/";
    public const string TssUrl = "https://cydia.saurik.com/TSS/controller?action=2";

    /// <summary>iFaith 请求 TSS 时使用的 UA；服务器按它识别来源，原样保留。</summary>
    public const string TssUserAgent = "iFaith-v1.5.9";

    private readonly HttpClient _http;

    public CydiaClient(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    /// <summary>ECID 为十进制（Cydia 接口用十进制，不是十六进制）。</summary>
    public static string BuildUrl(ulong ecid) => CheckUrlPrefix + ecid.ToString(CultureInfo.InvariantCulture);

    /// <summary>该 build 对应的 BuildManifest 地址。</summary>
    public static string BuildManifestUrl(CydiaBackup backup)
        => ManifestUrlPrefix + backup.Build + "/" + backup.Chip + "/" + backup.Board;

    public async Task<CydiaQueryResult> QueryAsync(ulong ecid, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var url = BuildUrl(ecid);

        string raw;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(raw))
            {
                return Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", raw, url, sw.Elapsed);
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("请求超时（服务器无响应，或本机网络无法访问 cydia.saurik.com）", "", url, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return Fail("网络错误：" + ex.Message, "", url, sw.Elapsed);
        }

        sw.Stop();
        return Parse(raw, url, sw.Elapsed);
    }

    /// <summary>
    /// 下载一条 Cydia 备份：取 BuildManifest → 替换 $ecid → POST 到 Cydia 的 TSS 代理 → 取出 plist。
    /// 这条链路是反编译 iFaith 1.5.9 得到的，实测产出的 .shsh 与 iFaith 下载的字节一致。
    /// </summary>
    public async Task<CydiaDownloadResult> DownloadAsync(ulong ecid, CydiaBackup backup, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var ecidDecimal = ecid.ToString(CultureInfo.InvariantCulture);
        var manifestUrl = BuildManifestUrl(backup);

        if (backup.Build.Length == 0 || backup.Chip.Length == 0 || backup.Board.Length == 0)
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = "该记录缺少 build / chip / board，无法下载",
                ManifestUrl = manifestUrl,
                Elapsed = sw.Elapsed,
            };
        }

        string manifest;
        try
        {
            using var manifestResponse = await _http.GetAsync(manifestUrl, ct).ConfigureAwait(false);
            manifest = await manifestResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!manifestResponse.IsSuccessStatusCode || !manifest.Contains("<plist", StringComparison.Ordinal))
            {
                return new CydiaDownloadResult
                {
                    Success = false,
                    Message = $"取 BuildManifest 失败（HTTP {(int)manifestResponse.StatusCode}）",
                    ManifestUrl = manifestUrl,
                    RawResponse = manifest,
                    Elapsed = sw.Elapsed,
                };
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = "取 BuildManifest 超时",
                ManifestUrl = manifestUrl,
                Elapsed = sw.Elapsed,
            };
        }
        catch (HttpRequestException ex)
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = "网络错误：" + ex.Message,
                ManifestUrl = manifestUrl,
                Elapsed = sw.Elapsed,
            };
        }

        var requestBody = manifest
            .Replace("$ECID$", ecidDecimal, StringComparison.Ordinal)
            .Replace("$ecid", ecidDecimal, StringComparison.Ordinal);

        string raw;
        var httpOk = true;
        var httpCode = 200;
        string httpReason = string.Empty;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TssUrl)
            {
                // plist 可能含非 ASCII 注释，用 UTF-8（无 BOM）
                Content = new StringContent(requestBody, new UTF8Encoding(false), "text/xml"),
            };
            request.Headers.UserAgent.ParseAdd(TssUserAgent);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            httpOk = response.IsSuccessStatusCode;
            httpCode = (int)response.StatusCode;
            httpReason = response.ReasonPhrase ?? string.Empty;
            raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = "请求 Cydia TSS 超时",
                ManifestUrl = manifestUrl,
                Elapsed = sw.Elapsed,
            };
        }
        catch (HttpRequestException ex)
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = "网络错误：" + ex.Message,
                ManifestUrl = manifestUrl,
                Elapsed = sw.Elapsed,
            };
        }

        sw.Stop();

        if (!httpOk)
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = $"Cydia TSS 返回 HTTP {httpCode} {httpReason}".Trim(),
                ManifestUrl = manifestUrl,
                RawResponse = raw,
                Elapsed = sw.Elapsed,
            };
        }

        if (!raw.Contains("STATUS=0&MESSAGE=SUCCESS", StringComparison.Ordinal))
        {
            var message = ExtractStatusMessage(raw);
            return new CydiaDownloadResult
            {
                Success = false,
                Message = message,
                ManifestUrl = manifestUrl,
                RawResponse = raw,
                Elapsed = sw.Elapsed,
            };
        }

        var start = raw.IndexOf("<?xml", StringComparison.Ordinal);
        var end = raw.IndexOf("</plist>", StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = "返回里没有找到 plist 内容",
                ManifestUrl = manifestUrl,
                RawResponse = raw,
                Elapsed = sw.Elapsed,
            };
        }

        var plist = raw.Substring(start, end - start + "</plist>".Length).TrimStart();

        // 与 README 承诺一致：必须含「非空」的 APTicket <data>，只有字样不算数
        if (!HasNonEmptyPlistData(plist, "APTicket"))
        {
            return new CydiaDownloadResult
            {
                Success = false,
                Message = "返回的 plist 里没有有效的 APTicket，不是可用的 SHSH 备份",
                ManifestUrl = manifestUrl,
                RawResponse = raw,
                Elapsed = sw.Elapsed,
            };
        }

        return new CydiaDownloadResult
        {
            Success = true,
            Content = new UTF8Encoding(false).GetBytes(plist),
            ManifestUrl = manifestUrl,
            RawResponse = raw,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// 校验 plist 里某个 key 后面紧跟的 &lt;data&gt; 是否有实际内容（Base64 非空），
    /// 避免只凭「出现 APTicket 字样」就判定成功。
    /// </summary>
    public static bool HasNonEmptyPlistData(string plist, string keyName)
    {
        var keyTag = "<key>" + keyName + "</key>";
        var keyIndex = plist.IndexOf(keyTag, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return false;
        }

        var dataStart = plist.IndexOf("<data>", keyIndex + keyTag.Length, StringComparison.Ordinal);
        if (dataStart < 0)
        {
            return false;
        }

        var dataEnd = plist.IndexOf("</data>", dataStart, StringComparison.Ordinal);
        if (dataEnd < 0)
        {
            return false;
        }

        var content = plist.Substring(dataStart + "<data>".Length, dataEnd - dataStart - "<data>".Length);
        return content.Any(ch => !char.IsWhiteSpace(ch));
    }

    private static string ExtractStatusMessage(string raw)
    {
        var status = System.Text.RegularExpressions.Regex.Match(raw, @"STATUS=(-?\d+)");
        var message = System.Text.RegularExpressions.Regex.Match(raw, @"MESSAGE=([^&\r\n]+)");
        if (message.Success)
        {
            // 服务器返回的是 URL 编码文本，解码后再展示
            return Uri.UnescapeDataString(message.Groups[1].Value.Trim().Replace('+', ' '));
        }

        return status.Success ? $"Cydia 返回 STATUS={status.Groups[1].Value}" : "Cydia 拒绝了该请求";
    }

    /// <summary>解析 Cydia 返回的 JSON 数组。</summary>
    public static CydiaQueryResult Parse(string raw, string url, TimeSpan elapsed)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return Fail("服务器返回空内容", raw, url, elapsed);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                return Fail("返回内容不是 JSON 数组", raw, url, elapsed);
            }

            var items = new List<CydiaBackup>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var item = new CydiaBackup(
                    ReadScalar(element, "model"),
                    ReadScalar(element, "firmware"),
                    ReadScalar(element, "build"),
                    ReadScalar(element, "chip"),
                    ReadScalar(element, "board"));

                if (item.Model.Length == 0 && item.Firmware.Length == 0)
                {
                    continue;
                }

                items.Add(item);
            }

            return new CydiaQueryResult
            {
                Success = true,
                Items = items,
                RawResponse = raw,
                RequestUrl = url,
                Elapsed = elapsed,
            };
        }
        catch (JsonException)
        {
            var preview = raw.Length > 300 ? raw[..300] + "…" : raw;
            return Fail("返回内容不是 JSON（可能被风控或网关拦截）：" + preview, raw, url, elapsed);
        }
    }

    private static string ReadScalar(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText(),
        };
    }

    private static CydiaQueryResult Fail(string message, string raw, string url, TimeSpan elapsed) => new()
    {
        Success = false,
        Message = message,
        RawResponse = raw,
        RequestUrl = url,
        Elapsed = elapsed,
    };

    public void Dispose() => _http.Dispose();
}

