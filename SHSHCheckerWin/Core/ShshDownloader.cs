namespace SHSHCheckerWin.Core;

/// <summary>一条待下载任务：显示标签 + 落盘文件名（+ 可选关联对象，如界面结果行）。</summary>
public sealed record ShshJob(string ProductType, string Ios, string FileName, object? Tag = null);

/// <summary>下载到的内容或失败原因。</summary>
public sealed record ShshContent(bool Success, string Message, byte[]? Content);

/// <summary>批量下载后保存下来的一个文件。</summary>
public sealed record ShshSaveItem(string FilePath, string ProductType, string Ios);

/// <summary>批量下载结果；<paramref name="Renamed"/> 为重名后自动加序号的个数。</summary>
public sealed record ShshBatchResult(int Succeeded, IReadOnlyList<string> Failures, IReadOnlyList<ShshSaveItem> Saved, int Renamed = 0);

/// <summary>通用批量下载器：把一组任务交给调用方提供的下载函数，逐个落盘。</summary>
public static class ShshDownloader
{
    public static async Task<ShshBatchResult> RunAsync(
        IReadOnlyList<ShshJob> jobs,
        Func<ShshJob, CancellationToken, Task<ShshContent>> download,
        string folder,
        IProgress<(int Index, int Total, string Label)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);

        var saved = new List<ShshSaveItem>();
        var failures = new List<string>();
        var renamed = 0;

        for (var i = 0; i < jobs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var job = jobs[i];
            progress?.Report((i, jobs.Count, $"{job.ProductType} {job.Ios}"));

            ShshContent content;
            try
            {
                content = await download(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{job.ProductType} {job.Ios}：{ex.Message}");
                continue;
            }

            if (!content.Success || content.Content is null)
            {
                failures.Add($"{job.ProductType} {job.Ios}：{content.Message}");
                continue;
            }

            try
            {
                var intended = Path.Combine(folder, job.FileName);
                var path = EnsureUniquePath(intended);
                if (!string.Equals(path, intended, StringComparison.OrdinalIgnoreCase))
                {
                    renamed++;
                }

                await File.WriteAllBytesAsync(path, content.Content, ct).ConfigureAwait(false);
                saved.Add(new ShshSaveItem(path, job.ProductType, job.Ios));
            }
            catch (Exception ex)
            {
                failures.Add($"{job.ProductType} {job.Ios}：保存失败 {ex.Message}");
            }
        }

        return new ShshBatchResult(saved.Count, failures, saved, renamed);
    }

    /// <summary>爱思来源的便捷入口（探针与旧调用方使用）。</summary>
    public static Task<ShshBatchResult> DownloadAllAsync(
        I4Client client,
        ulong ecid,
        IReadOnlyList<(string ProductType, string Ios)> items,
        string folder,
        string source = "i4",
        IProgress<(int Index, int Total, string Label)>? progress = null,
        CancellationToken ct = default)
    {
        var jobs = items
            .Select(item => new ShshJob(
                item.ProductType,
                item.Ios,
                I4Client.BuildShshFileName(ecid, item.ProductType, item.Ios, source)))
            .ToList();

        return RunAsync(
            jobs,
            async (job, token) =>
            {
                var result = await client.DownloadShshAsync(ecid, job.ProductType, job.Ios, token).ConfigureAwait(false);
                return new ShshContent(result.Success, result.Message, result.Content);
            },
            folder,
            progress,
            ct);
    }

    /// <summary>同名文件已存在时追加 (1)、(2)… 避免覆盖。</summary>
    public static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
