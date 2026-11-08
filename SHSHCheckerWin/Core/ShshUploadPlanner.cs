namespace SHSHCheckerWin.Core;

/// <summary>
/// 上传前决定「这条 SHSH 用什么元数据提交」。
/// 关键点：shsh2 的机型/版本来自文件名，文件被改名或其它工具命名时可能识别不出来，
/// 这时可以退回界面上方选择框里的机型（<paramref name="manualProductType"/>）。
/// </summary>
public static class ShshUploadPlanner
{
    /// <summary>解析出可提交的元数据；失败时给出原因。</summary>
    public static bool TryResolve(
        ShshFileInfo? info,
        string? manualProductType,
        out ShshFileInfo resolved,
        out string error)
    {
        resolved = info!;
        error = string.Empty;

        if (info is null)
        {
            error = "不是可识别的 SHSH 文件";
            return false;
        }

        if (info.Ecid == 0)
        {
            error = "没找到 ECID";
            return false;
        }

        var model = string.IsNullOrEmpty(info.ProductType)
            ? manualProductType?.Trim() ?? string.Empty
            : info.ProductType;
        if (model.Length == 0)
        {
            error = "没识别出机型，可在上方「机型 / ProductType」选择后重试";
            return false;
        }

        if (string.IsNullOrEmpty(info.Version))
        {
            error = "没识别出系统版本";
            return false;
        }

        if (info.IsShsh2 && string.IsNullOrEmpty(info.Nonce))
        {
            error = "shsh2 缺少 APNonce";
            return false;
        }

        resolved = info with { ProductType = model };
        return true;
    }

    /// <summary>这条 SHSH 是否可以用界面上方选择的机型兜底（即仅缺机型，其余齐全）。</summary>
    public static bool NeedsManualProductType(ShshFileInfo? info)
        => info is not null
           && info.Ecid != 0
           && string.IsNullOrEmpty(info.ProductType)
           && !string.IsNullOrEmpty(info.Version)
           && !(info.IsShsh2 && string.IsNullOrEmpty(info.Nonce));
}
