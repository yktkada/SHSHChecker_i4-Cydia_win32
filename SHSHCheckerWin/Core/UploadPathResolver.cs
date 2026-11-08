using System.Text;

namespace SHSHCheckerWin.Core;

/// <summary>
/// 上传路径解析：把输入框里的一串文本展开成待上传的 SHSH 文件列表。
/// 支持：单个文件、带引号的路径（资源管理器「复制为路径」）、分号/换行分隔的多路径、
/// 以及文件夹（递归扫描，忽略无权限子目录，UNC 也可）。
/// </summary>
public static class UploadPathResolver
{
    /// <summary>去掉首尾空白与成对引号。</summary>
    public static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        while (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Trim();
        }

        return value;
    }

    /// <summary>是否像 SHSH 文件（只按扩展名判断，避免把 ipsw 等大文件卷进来）。</summary>
    public static bool LooksLikeShshFile(string path)
        => path.EndsWith(".shsh", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".shsh2", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".plist", StringComparison.OrdinalIgnoreCase);

    /// <summary>把一段文本拆成路径片段（引号包裹的整体算一个，分号/换行也分隔）。</summary>
    public static List<string> SplitPaths(string text)
    {
        var parts = new List<string>();
        var buffer = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                if (!inQuotes)
                {
                    parts.Add(buffer.ToString());
                    buffer.Clear();
                }

                continue;
            }

            if (!inQuotes && (ch == ';' || ch == '\r' || ch == '\n'))
            {
                if (buffer.Length > 0)
                {
                    parts.Add(buffer.ToString());
                    buffer.Clear();
                }

                continue;
            }

            buffer.Append(ch);
        }

        if (buffer.Length > 0)
        {
            parts.Add(buffer.ToString());
        }

        return parts;
    }

    /// <summary>一次最多展开多少个文件（防止误粘 C:\ 之类的目录把界面拖死）。</summary>
    public const int DefaultMaxFiles = 1000;

    /// <summary>展开成文件列表；无法处理的输入会写进 <paramref name="problems"/>。</summary>
    public static List<string> Expand(string text, out List<string> problems, int maxFiles = DefaultMaxFiles)
    {
        problems = new List<string>();
        var result = new List<string>();

        foreach (var part in SplitPaths(text ?? string.Empty))
        {
            var path = NormalizePath(part);
            if (path.Length == 0)
            {
                continue;
            }

            if (File.Exists(path))
            {
                result.Add(path);
                continue;
            }

            if (Directory.Exists(path))
            {
                var remaining = Math.Max(0, maxFiles - result.Count);
                var folderFiles = EnumerateFolder(path, problems, remaining);
                result.AddRange(folderFiles);
                if (folderFiles.Count >= remaining && remaining > 0)
                {
                    problems.Add($"{path}：文件超过上限 {maxFiles} 个，已截断（请选更小的目录）");
                }

                continue;
            }

            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root)
                && root.StartsWith(@"\\", StringComparison.Ordinal)
                && !Directory.Exists(root))
            {
                problems.Add($"{path}：网络路径不可达（检查共享名、凭据或网络）");
            }
            else
            {
                problems.Add($"{path}：文件不存在");
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>递归扫描文件夹里的 SHSH 文件；忽略无权限子目录（UNC 常见 $RECYCLE.BIN 等）。</summary>
    public static List<string> EnumerateFolder(string folder, List<string> problems, int maxFiles = DefaultMaxFiles)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            var found = Directory.EnumerateFiles(folder, "*", options)
                .Where(LooksLikeShshFile)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maxFiles))
                .ToList();
            if (found.Count == 0)
            {
                problems.Add($"{folder}：文件夹里没有 .shsh / .shsh2 / .plist");
            }

            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{folder}：无法读取文件夹（{ex.Message}）");
            return new List<string>();
        }
    }
}
