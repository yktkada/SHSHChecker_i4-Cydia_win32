using System.Diagnostics;
using System.Globalization;
using SHSHCheckerWin.Core;

namespace SHSHCheckerWin;

public sealed partial class MainForm : Form
{
    private readonly TextBox _ecidBox = new();
    private readonly Label _ecidPreview = new();
    private readonly ComboBox _modelCombo = new();
    private readonly ComboBox _productTypeCombo = new();
    private readonly CheckBox _arm64Check = new();
    private readonly CheckBox _arm64eCheck = new();
    private readonly Label _modelHint = new();
    private readonly Button _queryButton = new();
    private readonly Button _cydiaButton = new();
    private readonly Button _downloadButton = new();
    private readonly Button _downloadAllButton = new();
    private readonly Label _statusLabel = new();
    private readonly ListView _listView = new();
    private readonly RichTextBox _rawBox = new();
    private readonly TextBox _paramBox = new();
    private readonly ToolTip _toolTip = new();
    private readonly I4Client _client = new();
    private readonly CydiaClient _cydiaClient = new();

    // ---- 上传区（支持多选：选一批文件 → 逐个上传）----
    private readonly TextBox _upFileBox = new();
    private readonly Button _upBrowseButton = new();
    private readonly Button _upButton = new();
    private readonly Label _upStatusLabel = new();
    private readonly List<string> _uploadFiles = new();
    private readonly List<ShshFileInfo?> _uploadInfos = new();
    private readonly List<long> _uploadStamps = new();
    private bool _suppressUploadEvents;
    private readonly System.Windows.Forms.Timer _uploadInputTimer = new() { Interval = 450 };
    private int _uploadResolveToken;
    private string _lastResolvedText = string.Empty;
    private readonly System.Windows.Forms.Timer _uploadWatchTimer = new() { Interval = 1000 };

    private SplitContainer _split = null!;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _uploadCts;
    private bool _busy;
    private bool _closing;
    private bool _suppressEvents;
    private bool _suppressArchEvents;

    /// <summary>结果行：来源（i4 / cydia）+ 查询时的 ECID + ProductType + 版本；Device 或 Cydia 非空表示可下载。</summary>
    private sealed record ResultRow(
        string Source,
        ulong Ecid,
        string ProductType,
        string Ios,
        DeviceModel? Device,
        CydiaBackup? Cydia)
    {
        public bool CanDownload => Device is not null || Cydia is not null;
    }

    /// <summary>一次单条下载的结果。</summary>
    private sealed record DownloadOutcome(bool Success, string Message, byte[]? Content, TimeSpan Elapsed);

    /// <summary>右边下拉框的一项：「全部 ProductType」或某个具体变体。</summary>
    private sealed record ProductTypeChoice(string Label, DeviceModel? Variant)
    {
        public override string ToString() => Label;
    }

    public MainForm()
    {
        Text = "SHSH Checker（i4 / Cydia）";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1040, 742);
        MinimumSize = new Size(900, 660);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        AcceptButton = _queryButton;

        _ecidBox.TextChanged += (_, _) => UpdateEcidPreview();
        _modelCombo.SelectedIndexChanged += (_, _) => OnGroupChanged();
        _productTypeCombo.SelectedIndexChanged += (_, _) => UpdateModelHint();
        _arm64Check.CheckedChanged += (_, _) => OnArchFilterChanged();
        _arm64eCheck.CheckedChanged += (_, _) => OnArchFilterChanged();
        _queryButton.Click += async (_, _) => await RunQueryAsync();
        _cydiaButton.Click += async (_, _) => await RunCydiaQueryAsync();
        _downloadButton.Click += async (_, _) => await DownloadSelectedAsync();
        _downloadAllButton.Click += async (_, _) => await DownloadAllAsync();
        _upBrowseButton.Click += (_, _) => BrowseUploadFile();
        _upButton.Click += async (_, _) => await UploadFromPanelAsync();
        _upFileBox.TextChanged += (_, _) => OnUploadFileChanged();
        _upFileBox.Leave += (_, _) => OnUploadFileBoxLeave();
        _uploadInputTimer.Tick += (_, _) =>
        {
            _uploadInputTimer.Stop();
            _ = ResolveUploadInputAsync(force: false);
        };
        _listView.DoubleClick += async (_, _) => await DownloadSelectedAsync();
        _listView.SelectedIndexChanged += (_, _) => UpdateActionStates();
        Activated += (_, _) => UpdateUploadButton();
        _uploadWatchTimer.Tick += (_, _) =>
        {
            if (!_busy && _upFileBox.Text.Trim().Length > 0)
            {
                UpdateUploadButton();
            }
        };
        _uploadWatchTimer.Start();
        FormClosed += (_, _) =>
        {
            _closing = true;
            _cts?.Cancel();
            _downloadCts?.Cancel();
            _uploadCts?.Cancel();
            _uploadWatchTimer.Stop();
            _uploadWatchTimer.Dispose();
        };

        LoadGroups();
        UpdateEcidPreview();
        OnUploadFileChanged();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // F-8: 合法区间是 [Panel1MinSize, Height - Panel2MinSize - SplitterWidth]，上下界都要钳
        var maxDistance = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
        if (maxDistance > _split.Panel1MinSize)
        {
            _split.SplitterDistance = Math.Clamp(_split.Height / 2, _split.Panel1MinSize, maxDistance);
        }

        AdjustResultColumns();
        ActiveControl = _ecidBox;
    }



    /// <summary>构造「标签在上、下拉框在下」的一格；两格宽度相同，保证下拉框始终平分。</summary>




    /// <summary>按当前 32bit/arm64/arm64e 勾选状态填充机型下拉框。</summary>
    private void LoadGroups()
    {
        var current = _modelCombo.SelectedItem as DeviceGroup;
        var visible = DeviceCatalog.VisibleGroups(_arm64Check.Checked, _arm64eCheck.Checked);

        _suppressEvents = true;
        try
        {
            _modelCombo.BeginUpdate();
            _modelCombo.Items.Clear();
            foreach (var group in visible)
            {
                _modelCombo.Items.Add(group);
            }

            var index = current is null ? -1 : visible.ToList().IndexOf(current);
            _modelCombo.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _modelCombo.EndUpdate();
            _suppressEvents = false;
        }

        RebuildProductTypeChoices();
    }

    /// <summary>arm64 / arm64e 勾选变化：arm64e 依赖 arm64；重新加载机型列表。</summary>
    private void OnArchFilterChanged()
    {
        if (_suppressArchEvents)
        {
            return;
        }

        _suppressArchEvents = true;
        try
        {
            if (!_arm64Check.Checked && _arm64eCheck.Checked)
            {
                _arm64eCheck.Checked = false;
            }

            _arm64eCheck.Enabled = _arm64Check.Checked;
        }
        finally
        {
            _suppressArchEvents = false;
        }

        LoadGroups();
        UpdateCydiaState();
    }

    /// <summary>左侧机型变化：右侧下拉框重新填入该机型的全部 ProductType（默认「全部」）。</summary>
    private void OnGroupChanged()
    {
        if (_suppressEvents)
        {
            return;
        }

        RebuildProductTypeChoices();
    }

    private void RebuildProductTypeChoices()
    {
        _suppressEvents = true;
        try
        {
            _productTypeCombo.BeginUpdate();
            _productTypeCombo.Items.Clear();

            if (_modelCombo.SelectedItem is DeviceGroup group)
            {
                _productTypeCombo.Items.Add(new ProductTypeChoice("全部 ProductType", null));

                foreach (var variant in group.Variants.Where(v => DeviceCatalog.IsVariantVisible(v, _arm64Check.Checked, _arm64eCheck.Checked)))
                {
                    _productTypeCombo.Items.Add(new ProductTypeChoice(variant.ProductType, variant));
                }
            }

            _productTypeCombo.SelectedIndex = _productTypeCombo.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            _productTypeCombo.EndUpdate();
            _suppressEvents = false;
        }

        UpdateModelHint();
    }

    private void UpdateModelHint()
    {
        if (_suppressEvents)
        {
            return;
        }

        if (_modelCombo.SelectedItem is not DeviceGroup group)
        {
            _modelHint.Text = "没有可选机型";
            _modelHint.ForeColor = Color.Firebrick;
            _toolTip.SetToolTip(_modelHint, string.Empty);
            return;
        }

        var choice = _productTypeCombo.SelectedItem as ProductTypeChoice;
        _modelHint.Text = choice?.Variant is { } single
            ? $"将查询 {single.ProductType}"
            : $"将查询 {group.ProductTypes}";
        _modelHint.ForeColor = Color.DimGray;
        _toolTip.SetToolTip(_modelHint, _modelHint.Text);
        UpdateCydiaState();
    }

    /// <summary>Cydia 按钮可用性：A7 及以后（含 iPhone 5c）无 Cydia SHSH 缓存。</summary>
    private void UpdateCydiaState()
    {
        var available = !_busy
                        && _modelCombo.SelectedItem is DeviceGroup { CydiaAvailable: true };
        _cydiaButton.Enabled = available;
        _toolTip.SetToolTip(
            _cydiaButton,
            available
                ? "查询 Cydia 备份"
                : "A7 及以后设备（含 iPhone 5c）Cydia 无 SHSH 缓存，只能用爱思查询/下载");
    }

    /// <summary>统一忙闲状态：查询/下载期间禁用所有查询与下载按钮。</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _queryButton.Enabled = !busy;
        UpdateCydiaState();
        UpdateActionStates();
        UpdateUploadButton();
    }

    private void UpdateActionStates()
    {
        var idle = !_busy;
        _downloadButton.Enabled = idle
                                  && _listView.SelectedItems.Count > 0
                                  && _listView.SelectedItems[0].Tag is ResultRow { CanDownload: true };
        _downloadAllButton.Enabled = idle && CollectRows().Count > 0;
    }

    /// <summary>开启新一轮查询：取消上一轮；旧源由它自己的 finally 负责释放。</summary>
    private CancellationTokenSource BeginOperation()
    {
        var previous = _cts;
        _cts = new CancellationTokenSource();
        previous?.Cancel();
        return _cts;
    }

    /// <summary>结束本轮查询：无论是否仍被引用都释放自己持有的源，只在引用相等时清空字段（F-2）。</summary>
    private void EndOperation(CancellationTokenSource source)
    {
        if (ReferenceEquals(_cts, source))
        {
            _cts = null;
        }

        source.Dispose();
    }

    /// <summary>只收集可下载的行（i4 与 cydia 均支持）。</summary>
    private List<ResultRow> CollectRows()
        => _listView.Items.Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<ResultRow>()
            .Where(row => row.CanDownload)
            .ToList();

    /// <summary>当前选择的查询目标：具体某个 ProductType，或该机型当前可见档位的全部变体。</summary>
    private IReadOnlyList<DeviceModel> ResolveTargets(DeviceGroup group)
    {
        var choice = _productTypeCombo.SelectedItem as ProductTypeChoice;
        if (choice?.Variant is { } single)
        {
            return [single];
        }

        var visible = group.Variants
            .Where(v => DeviceCatalog.IsVariantVisible(v, _arm64Check.Checked, _arm64eCheck.Checked))
            .ToList();
        return visible.Count > 0 ? visible : group.Variants;
    }

    private async Task RunQueryAsync()
    {
        if (!EcidParser.TryParse(_ecidBox.Text, out var ecid, out var error))
        {
            SetStatus("输入有误：" + error, Color.Firebrick);
            _ecidBox.Focus();
            _ecidBox.SelectAll();
            return;
        }

        if (_modelCombo.SelectedItem is not DeviceGroup group)
        {
            SetStatus("请先选择机型", Color.Firebrick);
            return;
        }

        var targets = ResolveTargets(group);

        var cts = BeginOperation();
        var ct = cts.Token;

        SetBusy(true);
        SetStatus($"正在查询 {group.Name} · ECID {ecid} · {targets.Count} 个 ProductType …", Color.FromArgb(0x33, 0x33, 0x33));
        _listView.Items.Clear();
        _rawBox.Clear();
        _paramBox.Clear();

        var results = new List<(DeviceModel Device, I4QueryResult Result)>(targets.Count);
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                SetStatus(
                    $"正在查询 {targets[i].ProductType}（{i + 1}/{targets.Count}）…",
                    Color.FromArgb(0x33, 0x33, 0x33));
                results.Add((targets[i], await _client.QueryBackupsAsync(ecid, targets[i].ProductType, ct)));
            }

            RenderResults(results, ecid, group);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!_closing) SetStatus("查询超时：服务器无响应或本机网络无法访问 i4tool2.i4.cn", Color.Firebrick);
        }
        catch (OperationCanceledException)
        {
            if (!_closing) SetStatus("已取消", Color.DimGray);
        }
        catch (Exception ex)
        {
            if (_closing) return;
            SetStatus("查询失败：" + ex.Message, Color.Firebrick);
            _rawBox.Text = ex.ToString();
        }
        finally
        {
            SetBusy(false);
            EndOperation(cts);
        }
    }

    /// <summary>下载选中行对应的 SHSH：i4 走 JSON 解包，Cydia 走 TSS@Home 代理。</summary>
    private async Task DownloadSelectedAsync()
    {
        if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not ResultRow row)
        {
            SetStatus("请先在结果列表里选中一条 SHSH 记录（双击该行也可下载）", Color.Firebrick);
            return;
        }

        // 用行里记录的 ECID，而不是输入框当前值（避免查询 A 设备后改输入再去下 B 的备份）
        var ecid = row.Ecid;

        using var dialog = new SaveFileDialog
        {
            Title = "保存 SHSH 文件",
            FileName = FileNameFor(ecid, row),
            Filter = "SHSH 文件 (*.shsh)|*.shsh|所有文件 (*.*)|*.*",
            DefaultExt = "shsh",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _downloadCts?.Cancel();
        var downloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _downloadCts = downloadCts;
        var token = downloadCts.Token;
        SetBusy(true); // F-1: 单条下载同样置忙，避免与批量下载互相释放 CTS
        try
        {
            var result = await DownloadRowAsync(ecid, row, token);
            if (!result.Success || result.Content is null)
            {
                SetStatus("下载失败：" + result.Message, Color.Firebrick);
                return;
            }

            await File.WriteAllBytesAsync(dialog.FileName, result.Content);
            var size = result.Content.Length.ToString("N0", CultureInfo.InvariantCulture);
            SetStatus(
                $"已保存：{Path.GetFileName(dialog.FileName)}（{size} 字节，用时 {result.Elapsed.TotalMilliseconds:0} ms）",
                Color.FromArgb(0x1B, 0x6E, 0x2F));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!_closing) SetStatus("下载超时（90 秒）", Color.Firebrick);
        }
        catch (OperationCanceledException)
        {
            if (!_closing) SetStatus("已取消下载", Color.DimGray);
        }
        catch (Exception ex)
        {
            if (!_closing) SetStatus("保存失败：" + ex.Message, Color.Firebrick);
        }
        finally
        {
            // F-1: 只释放本轮自己的 CTS；字段只在本轮仍是当前值时才清空
            if (ReferenceEquals(_downloadCts, downloadCts))
            {
                _downloadCts = null;
            }

            downloadCts.Dispose();
            SetBusy(false);
        }
    }

    /// <summary>下载文件名：{ProductType}_{版本}_{build}_{ECID}_{来源}.shsh。</summary>
    /// <summary>
    /// 下载文件名：{ProductType}_{版本}_{build}_{ECID}_{来源}。现代设备（A7 及以后）保存为 .shsh2。
    /// </summary>
    private static string FileNameFor(ulong ecid, ResultRow row)
    {
        if (row.Device is { } device)
        {
            return I4Client.BuildShshFileName(ecid, device.ProductType, row.Ios, "i4", device.Tier != DeviceTier.Arm32);
        }

        return I4Client.BuildShshFileName(ecid, row.Cydia!.Model, row.Ios, "cydia");
    }

    /// <summary>按来源分派到对应的下载实现。</summary>
    private async Task<DownloadOutcome> DownloadRowAsync(ulong ecid, ResultRow row, CancellationToken ct)
    {
        if (row.Device is { } device)
        {
            var result = await _client.DownloadShshAsync(ecid, device.ProductType, row.Ios, ct);
            return new DownloadOutcome(result.Success, result.Message, result.Content, result.Elapsed);
        }

        if (row.Cydia is { } cydia)
        {
            var result = await _cydiaClient.DownloadAsync(ecid, cydia, ct);
            return new DownloadOutcome(result.Success, result.Message, result.Content, result.Elapsed);
        }

        return new DownloadOutcome(false, "该记录不可下载", null, TimeSpan.Zero);
    }


    /// <summary>查询 Cydia 的备份列表（A7 及以后 / iPhone 5c 已无 Cydia 缓存，按钮会被禁用）。</summary>
    private async Task RunCydiaQueryAsync()
    {
        if (_modelCombo.SelectedItem is not DeviceGroup { CydiaAvailable: true })
        {
            SetStatus("该设备（A7 及以后 / iPhone 5c）Cydia 无 SHSH 缓存，仅支持爱思查询", Color.Firebrick);
            return;
        }

        if (!EcidParser.TryParse(_ecidBox.Text, out var ecid, out var error))
        {
            SetStatus("输入有误：" + error, Color.Firebrick);
            _ecidBox.Focus();
            _ecidBox.SelectAll();
            return;
        }

        var cts = BeginOperation();
        var ct = cts.Token;

        SetBusy(true);
        SetStatus($"正在查询 Cydia（ECID {ecid}）…", Color.FromArgb(0x33, 0x33, 0x33));
        _listView.Items.Clear();
        _rawBox.Clear();
        _paramBox.Clear();

        try
        {
            var result = await _cydiaClient.QueryAsync(ecid, ct);
            RenderCydiaResult(result, ecid);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!_closing) SetStatus("Cydia 查询超时：无法访问 cydia.saurik.com", Color.Firebrick);
        }
        catch (OperationCanceledException)
        {
            if (!_closing) SetStatus("已取消", Color.DimGray);
        }
        catch (Exception ex)
        {
            if (_closing) return;
            SetStatus("Cydia 查询失败：" + ex.Message, Color.Firebrick);
            _rawBox.Text = ex.ToString();
        }
        finally
        {
            SetBusy(false);
            EndOperation(cts);
        }
    }

    private void RenderCydiaResult(CydiaQueryResult result, ulong ecid)
    {
        _paramBox.Text = string.Join(
            Environment.NewLine,
            $"ECID：{ecid}（hex {ecid:x}）",
            "Cydia 用十进制 ECID、无需签名：",
            result.RequestUrl);
        _rawBox.Text = result.RawResponse;
        _rawBox.SelectionStart = 0;

        _listView.BeginUpdate();
        try
        {
            foreach (var item in result.Items)
            {
                _listView.Items.Add(new ListViewItem(
                [
                    "cydia",
                    item.Model,
                    item.Version,
                    item.Extras,
                ])
                {
                    Tag = new ResultRow("cydia", ecid, item.Model, item.Version, null, item),
                });
            }

            if (!result.Success)
            {
                _listView.Items.Add(new ListViewItem(["cydia(失败)", "-", "-", result.Message])
                {
                    ForeColor = Color.Firebrick,
                });
            }
        }
        finally
        {
            _listView.EndUpdate();
        }

        UpdateActionStates();

        var ms = result.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        if (!result.Success)
        {
            SetStatus($"Cydia 查询失败：{result.Message}（用时 {ms} ms）", Color.Firebrick);
            return;
        }

        SetStatus(
            result.Items.Count > 0
                ? $"Cydia 查询成功：共 {result.Items.Count} 条备份（用时 {ms} ms）；可「下载选中」或「全部下载」"
                : $"Cydia 查询成功：该 ECID 没有备份记录（用时 {ms} ms）",
            result.Items.Count > 0 ? Color.FromArgb(0x1B, 0x6E, 0x2F) : Color.FromArgb(0x8A, 0x6D, 0x00));
    }

    /// <summary>把结果列表里的所有记录批量下载到指定文件夹。</summary>
    private async Task DownloadAllAsync()
    {
        var rows = CollectRows();
        if (rows.Count == 0)
        {
            SetStatus("当前没有可下载的结果", Color.Firebrick);
            return;
        }

        // 批量按行里记录的 ECID 下载（同一批结果都来自同一次查询）
        var ecid = rows[0].Ecid;

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 SHSH 文件的保存文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetBusy(true);
        _downloadCts?.Cancel();
        var batchCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _downloadCts = batchCts;
        var token = batchCts.Token;

        try
        {
            var progress = new Progress<(int Index, int Total, string Label)>(p =>
                SetStatus($"准备下载 {p.Index + 1}/{p.Total}：{p.Label} …", Color.FromArgb(0x33, 0x33, 0x33)));

            var jobs = rows
                .Select(row => new ShshJob(row.ProductType, row.Ios, FileNameFor(ecid, row), row))
                .ToList();

            var batch = await ShshDownloader.RunAsync(
                jobs,
                async (job, downloadToken) =>
                {
                    var outcome = await DownloadRowAsync(ecid, (ResultRow)job.Tag!, downloadToken);
                    return new ShshContent(outcome.Success, outcome.Message, outcome.Content);
                },
                dialog.SelectedPath,
                progress,
                token);

            var renamed = batch.Renamed > 0 ? $"（{batch.Renamed} 个重名文件已自动加序号）" : string.Empty;
            if (batch.Failures.Count == 0)
            {
                SetStatus(
                    $"全部下载完成：{batch.Succeeded} 个文件已保存到 {dialog.SelectedPath}{renamed}",
                    Color.FromArgb(0x1B, 0x6E, 0x2F));
            }
            else
            {
                SetStatus(
                    $"全部下载结束：成功 {batch.Succeeded} 个，失败 {batch.Failures.Count} 个，保存到 {dialog.SelectedPath}{renamed}",
                    Color.Firebrick);
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, batch.Failures),
                    "部分文件下载失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!_closing) SetStatus("批量下载超时（10 分钟）", Color.Firebrick);
        }
        catch (OperationCanceledException)
        {
            if (!_closing) SetStatus("已取消批量下载", Color.DimGray);
        }
        catch (Exception ex)
        {
            if (!_closing) SetStatus("批量下载失败：" + ex.Message, Color.Firebrick);
        }
        finally
        {
            SetBusy(false);
            if (ReferenceEquals(_downloadCts, batchCts))
            {
                _downloadCts = null;
            }

            batchCts.Dispose();
        }
    }

    private void RenderResults(
        IReadOnlyList<(DeviceModel Device, I4QueryResult Result)> results,
        ulong ecid,
        DeviceGroup group)
    {
        var totalItems = 0;
        var failed = 0;
        var elapsed = TimeSpan.Zero;
        var listRows = new List<ListViewItem>();
        var paramBlocks = new List<string>();
        var rawBlocks = new List<string>();

        foreach (var (device, result) in results)
        {
            elapsed += result.Elapsed;
            totalItems += result.Items.Count;
            if (!result.Success)
            {
                failed++;
            }

            foreach (var item in result.Items)
            {
                listRows.Add(new ListViewItem(
                [
                    "i4",
                    device.ProductType,
                    item.Ios,
                    string.Join("   ", item.Extras),
                ])
                {
                    Tag = new ResultRow("i4", ecid, device.ProductType, item.Ios, device, null),
                });
            }

            if (!result.Success)
            {
                listRows.Add(new ListViewItem(["i4(失败)", device.ProductType, "-", result.Message])
                {
                    ForeColor = Color.Firebrick,
                });
            }

            paramBlocks.Add(string.Join(
                Environment.NewLine,
                $"[{device.ProductType} · {device.MarketingName}]",
                "URL：" + result.RequestUrl,
                "明文：" + result.PayloadJson,
                "密文：" + result.CipherBase64));

            rawBlocks.Add(string.Join(
                Environment.NewLine,
                $"[{device.ProductType} · {device.MarketingName}] success={result.Success} code={result.Code} msg={result.Message}",
                result.RawResponse));
        }

        paramBlocks.Insert(0, $"ECID：{ecid}（hex {ecid:x}）　机型：{group.Name}　ProductType：{group.ProductTypes}");
        _paramBox.Text = string.Join(Environment.NewLine + Environment.NewLine, paramBlocks);
        _rawBox.Text = string.Join(Environment.NewLine + Environment.NewLine, rawBlocks);
        _rawBox.SelectionStart = 0;

        _listView.BeginUpdate();
        try
        {
            _listView.Items.AddRange(listRows.ToArray());
        }
        finally
        {
            _listView.EndUpdate();
        }

        UpdateActionStates();

        var ms = elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        if (failed == 0)
        {
            SetStatus(
                totalItems > 0
                    ? $"查询成功：{group.Name} · {results.Count} 个 ProductType，共 {totalItems} 条 SHSH 备份（用时 {ms} ms）；可「下载选中」或「全部下载」"
                    : $"查询成功：{group.Name} 没有 SHSH 备份记录 · {results.Count} 个 ProductType（用时 {ms} ms）",
                totalItems > 0 ? Color.FromArgb(0x1B, 0x6E, 0x2F) : Color.FromArgb(0x8A, 0x6D, 0x00));
        }
        else
        {
            var firstFailure = results.First(r => !r.Result.Success).Result;
            var headline = failed == results.Count
                ? $"全部失败：{results.Count} 个 ProductType 均未查询成功"
                : $"部分失败：{results.Count - failed}/{results.Count} 个 ProductType 查询成功，{totalItems} 条记录";
            SetStatus(
                $"{headline}；首个错误：{firstFailure.Message}（用时 {ms} ms）",
                Color.Firebrick);
        }
    }

    private void UpdateEcidPreview()
    {
        if (EcidParser.TryParse(_ecidBox.Text, out var ecid, out _, out var usedDecimal))
        {
            var hex = EcidParser.ToHex16(ecid);
            _ecidPreview.Text = usedDecimal
                ? $"按十进制解析：{ecid}（十六进制 {hex}）"
                : $"ECID 十进制：{ecid}（十六进制 {hex}）";
            _ecidPreview.ForeColor = Color.DimGray;
        }
        else if (string.IsNullOrWhiteSpace(_ecidBox.Text))
        {
            _ecidPreview.Text = "等待输入；ECID 可填十六进制（如 0011AABBCCDDEEFF）或十进制（如 1234567890123，13 位以上自动识别）";
            _ecidPreview.ForeColor = Color.DimGray;
        }
        else
        {
            _ecidPreview.Text = "ECID 格式不正确：十六进制最多 16 位；十进制 13 位以上自动识别（十六进制想用纯数字请加 0x）";
            _ecidPreview.ForeColor = Color.Firebrick;
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

}
















