namespace SHSHCheckerWin.Core;

/// <summary>设备位宽档位：A4–A6X 为 32bit；A7–A11 为 arm64；A12 起为 arm64e。</summary>
public enum DeviceTier
{
    Arm32 = 0,
    Arm64 = 1,
    Arm64e = 2,
}

/// <summary>SoC 名称的排序与分档（新增芯片只需在 Order / 集合里补一行）。</summary>
public static class DeviceSoc
{
    private static readonly string[] Order =
    [
        "S5L8900", "S5L8720", "S5L8920", "S5L8922",
        "A4", "A5", "A5X", "A6", "A6X", "A7",
        "A8", "A8X", "A9", "A9X", "A10", "A10X", "A11",
        "A12", "A12X", "A12Z", "A13", "A14", "M1",
        "A15", "M2", "A16", "M3", "A17 Pro", "M4", "A18", "M5",
        "A19", "A19 Pro", "A20", "A20 Pro", "S10",
    ];

    private static readonly HashSet<string> Arm32Socs =
        ["S5L8900", "S5L8720", "S5L8920", "S5L8922", "A4", "A5", "A5X", "A6", "A6X"];

    private static readonly HashSet<string> Arm64Socs =
        ["A7", "A8", "A8X", "A9", "A9X", "A10", "A10X", "A11"];

    /// <summary>按发布先后排序用的序号，未知 SoC 排最后。</summary>
    public static int Rank(string soc)
    {
        var index = Array.IndexOf(Order, soc);
        return index < 0 ? Order.Length : index;
    }

    /// <summary>
    /// SoC 是否已登记（在 Order 表里）。未登记的 SoC 说明生成器已引入新芯片，
    /// 需要人工补表；这类设备仍会显示，避免被静默隐藏（F-9）。
    /// </summary>
    public static bool IsKnown(string soc) => Array.IndexOf(Order, soc) >= 0;

    public static DeviceTier Tier(string soc)
    {
        if (Arm32Socs.Contains(soc))
        {
            return DeviceTier.Arm32;
        }

        return Arm64Socs.Contains(soc) ? DeviceTier.Arm64 : DeviceTier.Arm64e;
    }
}

/// <summary>
/// 一条机型对照记录。数据来源：D:\Working\Apple producttype对照表.txt
/// （HardwareModel ↔ ProductType ↔ 机型，含同型号多个 ProductType 变体）。
/// </summary>
/// <param name="MarketingName">营销机型名，如 “iPhone 8”。</param>
/// <param name="ProductType">Apple 设备型号标识，如 “iPhone10,1”——这就是接口的 model 参数。</param>
/// <param name="HardwareModel">硬件代号，如 “D20AP”；iPod touch 第 5 代有两个代号。</param>
/// <param name="Category">产品大类：iPhone / iPad / iPod。</param>
/// <param name="ScreenSize">屏幕规格（对照表里的分组：3.5/4/4.7/5.5 英寸、2048×1536 等）。</param>
/// <param name="VariantGroup">同型号变体分组键，例如 iPhone 8 的 iPhone10,1 与 iPhone10,4 同属一组。</param>
public sealed record DeviceModel(
    string MarketingName,
    string ProductType,
    string HardwareModel,
    string Category,
    string ScreenSize,
    string VariantGroup)
{
    /// <summary>SoC 营销名，如 A5 / A12X / M1；由 ipsw.me 的 cpid 映射得到。</summary>
    public string Soc { get; init; } = string.Empty;

    public DeviceTier Tier => DeviceSoc.Tier(Soc);

    /// <summary>Cydia 是否可能有该设备的 SHSH：A7 起没有；iPhone 5c（A6）也禁用（按需求）。</summary>
    public bool CydiaAvailable =>
        Tier == DeviceTier.Arm32
        && !ProductType.StartsWith("iPhone5,3", StringComparison.OrdinalIgnoreCase)
        && !ProductType.StartsWith("iPhone5,4", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => HardwareModel.Length > 0
        ? $"{MarketingName} - {ProductType}（{HardwareModel}）"
        : $"{MarketingName} - {ProductType}";
}

/// <summary>一个机型分组（同一营销机型的全部 ProductType 变体），下拉框里显示为「SoC - 机型」。</summary>
public sealed record DeviceGroup(
    string Name,
    string Category,
    string ScreenSize,
    IReadOnlyList<DeviceModel> Variants)
{
    /// <summary>组内 SoC（同组机型同一芯片）。</summary>
    public string Soc => Variants.Count > 0 ? Variants[0].Soc : string.Empty;

    public DeviceTier Tier => DeviceSoc.Tier(Soc);

    /// <summary>组内任一变体可用 Cydia 即视为可用。</summary>
    public bool CydiaAvailable => Variants.Any(v => v.CydiaAvailable);

    /// <summary>组内全部 ProductType，用 " / " 连接。</summary>
    public string ProductTypes => string.Join(" / ", Variants.Select(v => v.ProductType));

    /// <summary>下拉框显示文本：如 “A5 - iPad 2”。</summary>
    public string Display => Soc.Length > 0 ? $"{Soc} - {Name}" : Name;

    public override string ToString() => Display;
}

/// <summary>内置机型库（iPhone / iPad / iPod）。</summary>
public static class DeviceCatalog
{
    /// <summary>明确从机型库移除的老设备：iPhone 2G / 3G、iPod touch 1 / 2。</summary>
    private static readonly HashSet<string> ExcludedProductTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "iPhone1,1",
        "iPhone1,2",
        "iPod1,1",
        "iPod2,1",
    };

    /// <summary>全部 ProductType 记录（手写机型库 + ipsw.me 生成数据），按 SoC 代际、分类、机型排序。</summary>
    public static IReadOnlyList<DeviceModel> All { get; } = BuildAll();

    /// <summary>全部机型分组（每个分组含同型号的所有 ProductType 变体），保持机型库顺序。</summary>
    public static IReadOnlyList<DeviceGroup> Groups { get; } = BuildGroups();

    /// <summary>按勾选档位过滤后的机型分组（32bit 始终显示；未登记 SoC 始终显示，避免被静默隐藏）。</summary>
    public static IReadOnlyList<DeviceGroup> VisibleGroups(bool includeArm64, bool includeArm64e)
        => Groups.Where(g => g.Variants.Any(v => IsVariantVisible(v, includeArm64, includeArm64e))).ToList();

    /// <summary>单个变体是否应显示。</summary>
    public static bool IsVariantVisible(DeviceModel model, bool includeArm64, bool includeArm64e)
        => !DeviceSoc.IsKnown(model.Soc) || IsTierVisible(model.Tier, includeArm64, includeArm64e);

    /// <summary>某档位设备是否应显示。</summary>
    public static bool IsTierVisible(DeviceTier tier, bool includeArm64, bool includeArm64e) => tier switch
    {
        DeviceTier.Arm32 => true,
        DeviceTier.Arm64 => includeArm64,
        DeviceTier.Arm64e => includeArm64e,
        _ => false,
    };

    /// <summary>合并手写机型库与 ipsw.me 生成数据：按 ProductType 去重，手写数据优先（带屏幕尺寸、中文名）。</summary>
    private static DeviceModel[] BuildAll()
    {
        var seedSoc = IpswDevices.All.ToDictionary(s => s.ProductType, s => s.Soc, StringComparer.OrdinalIgnoreCase);
        var merged = new List<DeviceModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in BuildCurated())
        {
            if (ExcludedProductTypes.Contains(model.ProductType) || !seen.Add(model.ProductType))
            {
                continue;
            }

            merged.Add(model with { Soc = seedSoc.GetValueOrDefault(model.ProductType, string.Empty) });
        }

        foreach (var seed in IpswDevices.All)
        {
            if (ExcludedProductTypes.Contains(seed.ProductType) || !seen.Add(seed.ProductType))
            {
                continue;
            }

            merged.Add(new DeviceModel(seed.Name, seed.ProductType, seed.Hardware, seed.Category, string.Empty, seed.Group)
            {
                Soc = seed.Soc,
            });
        }

        // 排序：SoC 代际 → 同一 SoC 内 iPhone > iPad > iPod > AppleTV > Vision → 机型名 → ProductType
        return merged
            .OrderBy(model => DeviceSoc.Rank(model.Soc))
            .ThenBy(model => CategoryRank(model.Category))
            .ThenBy(model => model.VariantGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.ProductType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CategoryRank(string category) => category switch
    {
        "iPhone" => 0,
        "iPad" => 1,
        "iPod" => 2,
        "AppleTV" => 3,
        "Vision" => 4,
        _ => 5,
    };

    private static DeviceGroup[] BuildGroups()
    {
        var groups = new List<DeviceGroup>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in All)
        {
            if (!seen.Add(model.VariantGroup))
            {
                continue;
            }

            var variants = All.Where(m => m.VariantGroup == model.VariantGroup).ToList();
            groups.Add(new DeviceGroup(model.VariantGroup, model.Category, model.ScreenSize, variants));
        }

        return groups.ToArray();
    }

    /// <summary>手写机型库：来自 D:\Working\Apple producttype对照表.txt（含屏幕尺寸与硬件代号）。</summary>
    private static DeviceModel[] BuildCurated() =>
    [
        // ---------- iPhone 3.5 英寸 ----------
        new("iPhone 2G", "iPhone1,1", "M68AP", "iPhone", "3.5 英寸", "iPhone 2G"),
        new("iPhone 3G", "iPhone1,2", "N82AP", "iPhone", "3.5 英寸", "iPhone 3G"),
        new("iPhone 3GS", "iPhone2,1", "N88AP", "iPhone", "3.5 英寸", "iPhone 3GS"),
        new("iPhone 4", "iPhone3,1", "N90AP", "iPhone", "3.5 英寸", "iPhone 4"),
        new("iPhone 4 (Rev.A)", "iPhone3,2", "N90BAP", "iPhone", "3.5 英寸", "iPhone 4"),
        new("iPhone 4 (CDMA)", "iPhone3,3", "N92AP", "iPhone", "3.5 英寸", "iPhone 4"),
        new("iPhone 4S", "iPhone4,1", "N94AP", "iPhone", "3.5 英寸", "iPhone 4S"),

        // ---------- iPhone 4 英寸 ----------
        new("iPhone 5", "iPhone5,1", "N41AP", "iPhone", "4 英寸", "iPhone 5"),
        new("iPhone 5", "iPhone5,2", "N42AP", "iPhone", "4 英寸", "iPhone 5"),
        new("iPhone 5c", "iPhone5,3", "N48AP", "iPhone", "4 英寸", "iPhone 5c"),
        new("iPhone 5c", "iPhone5,4", "N49AP", "iPhone", "4 英寸", "iPhone 5c"),
        new("iPhone 5s", "iPhone6,1", "N51AP", "iPhone", "4 英寸", "iPhone 5s"),
        new("iPhone 5s", "iPhone6,2", "N53AP", "iPhone", "4 英寸", "iPhone 5s"),
        new("iPhone SE (第 1 代)", "iPhone8,4", "N69AP", "iPhone", "4 英寸", "iPhone SE (第 1 代)"),

        // ---------- iPhone 4.7 英寸 ----------
        new("iPhone 6", "iPhone7,2", "N61AP", "iPhone", "4.7 英寸", "iPhone 6"),
        new("iPhone 6s", "iPhone8,1", "N71AP", "iPhone", "4.7 英寸", "iPhone 6s"),
        new("iPhone 7", "iPhone9,1", "D10AP", "iPhone", "4.7 英寸", "iPhone 7"),
        new("iPhone 7", "iPhone9,3", "D101AP", "iPhone", "4.7 英寸", "iPhone 7"),
        new("iPhone 8", "iPhone10,1", "D20AP", "iPhone", "4.7 英寸", "iPhone 8"),
        new("iPhone 8", "iPhone10,4", "D201AP", "iPhone", "4.7 英寸", "iPhone 8"),
        new("iPhone SE (第 2 代)", "iPhone12,8", "D79AP", "iPhone", "4.7 英寸", "iPhone SE (第 2 代)"),
        new("iPhone SE (第 3 代)", "iPhone14,6", "D49AP", "iPhone", "4.7 英寸", "iPhone SE (第 3 代)"),

        // ---------- iPhone 5.5 英寸 ----------
        new("iPhone 6 Plus", "iPhone7,1", "N56AP", "iPhone", "5.5 英寸", "iPhone 6 Plus"),
        new("iPhone 6s Plus", "iPhone8,2", "N66AP", "iPhone", "5.5 英寸", "iPhone 6s Plus"),
        new("iPhone 7 Plus", "iPhone9,2", "D11AP", "iPhone", "5.5 英寸", "iPhone 7 Plus"),
        new("iPhone 7 Plus", "iPhone9,4", "D111AP", "iPhone", "5.5 英寸", "iPhone 7 Plus"),
        new("iPhone 8 Plus", "iPhone10,2", "D21AP", "iPhone", "5.5 英寸", "iPhone 8 Plus"),
        new("iPhone 8 Plus", "iPhone10,5", "D211AP", "iPhone", "5.5 英寸", "iPhone 8 Plus"),

        // ---------- iPad 1024×768 ----------
        new("iPad 1", "iPad1,1", "K48AP", "iPad", "1024×768", "iPad 1"),
        new("iPad 2 Wi-Fi", "iPad2,1", "K93AP", "iPad", "1024×768", "iPad 2"),
        new("iPad 2 GSM", "iPad2,2", "K94AP", "iPad", "1024×768", "iPad 2"),
        new("iPad 2 CDMA", "iPad2,3", "K95AP", "iPad", "1024×768", "iPad 2"),
        new("iPad 2 (2012)", "iPad2,4", "K93AAP", "iPad", "1024×768", "iPad 2"),
        new("iPad mini 1 Wi-Fi", "iPad2,5", "P105AP", "iPad", "1024×768", "iPad mini 1"),
        new("iPad mini 1 Cellular", "iPad2,6", "P106AP", "iPad", "1024×768", "iPad mini 1"),
        new("iPad mini 1 Cellular（中国大陆）", "iPad2,7", "P107AP", "iPad", "1024×768", "iPad mini 1"),

        // ---------- iPad 2048×1536 ----------
        new("iPad 3 Wi-Fi", "iPad3,1", "J1AP", "iPad", "2048×1536", "iPad 3"),
        new("iPad 3 Cellular (VZ)", "iPad3,2", "J2AP", "iPad", "2048×1536", "iPad 3"),
        new("iPad 3 Cellular", "iPad3,3", "J2AAP", "iPad", "2048×1536", "iPad 3"),
        new("iPad 4 Wi-Fi", "iPad3,4", "P101AP", "iPad", "2048×1536", "iPad 4"),
        new("iPad 4 Cellular", "iPad3,5", "P102AP", "iPad", "2048×1536", "iPad 4"),
        new("iPad 4 Cellular（中国大陆）", "iPad3,6", "P103AP", "iPad", "2048×1536", "iPad 4"),
        new("iPad Air Wi-Fi", "iPad4,1", "J71AP", "iPad", "2048×1536", "iPad Air"),
        new("iPad Air Cellular", "iPad4,2", "J72AP", "iPad", "2048×1536", "iPad Air"),
        new("iPad Air TD-LTE", "iPad4,3", "J73AP", "iPad", "2048×1536", "iPad Air"),
        new("iPad mini 2 Wi-Fi", "iPad4,4", "J85AP", "iPad", "2048×1536", "iPad mini 2"),
        new("iPad mini 2 Cellular", "iPad4,5", "J86AP", "iPad", "2048×1536", "iPad mini 2"),
        new("iPad mini 2 TD-LTE", "iPad4,6", "J87AP", "iPad", "2048×1536", "iPad mini 2"),
        new("iPad mini 3 Wi-Fi", "iPad4,7", "J85mAP", "iPad", "2048×1536", "iPad mini 3"),
        new("iPad mini 3 Cellular", "iPad4,8", "J86mAP", "iPad", "2048×1536", "iPad mini 3"),
        new("iPad mini 3 TD-LTE", "iPad4,9", "J87mAP", "iPad", "2048×1536", "iPad mini 3"),
        new("iPad mini 4 Wi-Fi", "iPad5,1", "J96AP", "iPad", "2048×1536", "iPad mini 4"),
        new("iPad mini 4 Cellular", "iPad5,2", "J97AP", "iPad", "2048×1536", "iPad mini 4"),
        new("iPad Air 2 Wi-Fi", "iPad5,3", "J81AP", "iPad", "2048×1536", "iPad Air 2"),
        new("iPad Air 2 Cellular", "iPad5,4", "J82AP", "iPad", "2048×1536", "iPad Air 2"),
        new("iPad Pro 9.7 Wi-Fi", "iPad6,3", "J127AP", "iPad", "2048×1536", "iPad Pro 9.7"),
        new("iPad Pro 9.7 Cellular", "iPad6,4", "J128AP", "iPad", "2048×1536", "iPad Pro 9.7"),
        new("iPad 5 Wi-Fi", "iPad6,11", "J71sAP", "iPad", "2048×1536", "iPad 5"),
        new("iPad 5 Cellular", "iPad6,12", "J72sAP", "iPad", "2048×1536", "iPad 5"),
        new("iPad 6 Wi-Fi", "iPad7,5", "J71bAP", "iPad", "2048×1536", "iPad 6"),
        new("iPad 6 Cellular", "iPad7,6", "J72bAP", "iPad", "2048×1536", "iPad 6"),
        new("iPad mini 5 Wi-Fi", "iPad11,1", "J210AP", "iPad", "2048×1536", "iPad mini 5"),
        new("iPad mini 5 Cellular", "iPad11,2", "J211AP", "iPad", "2048×1536", "iPad mini 5"),

        // ---------- iPod touch ----------
        new("iPod touch (第 1 代)", "iPod1,1", "N45AP", "iPod", "3.5 英寸", "iPod touch 1"),
        new("iPod touch (第 2 代)", "iPod2,1", "N72AP", "iPod", "3.5 英寸", "iPod touch 2"),
        new("iPod touch (第 3 代)", "iPod3,1", "N18AP", "iPod", "3.5 英寸", "iPod touch 3"),
        new("iPod touch (第 4 代)", "iPod4,1", "N81AP", "iPod", "3.5 英寸", "iPod touch 4"),
        // 第 5 代两个 HardwareModel 共用 iPod5,1（A1421 / A1509，不能只靠 ProductType 反推）
        new("iPod touch (第 5 代)", "iPod5,1", "N78AP / N78aAP", "iPod", "4 英寸", "iPod touch 5"),
        new("iPod touch (第 6 代)", "iPod7,1", "N102AP", "iPod", "4 英寸", "iPod touch 6"),
        new("iPod touch (第 7 代)", "iPod9,1", "N112AP", "iPod", "4 英寸", "iPod touch 7"),
    ];
}

