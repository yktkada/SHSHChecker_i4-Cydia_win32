using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SHSHCheckerWin.Core;

namespace SHSHCheckerWin;

/// <summary>
/// 上传区（窗口底部一行）：选本地 shsh 文件 → 自动解析 ECID/机型/iOS 并显示在路径旁 → 上传到爱思服务器。
/// </summary>
public sealed partial class MainForm
{
    private Control BuildUploadPanel()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 2,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54f));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "上传 SHSH",
            AutoSize = true,
            Margin = new Padding(3, 7, 6, 3),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        grid.Controls.Add(label, 0, 0);

        var fileCell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        fileCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        fileCell.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _upFileBox.Dock = DockStyle.Fill;
        _upFileBox.PlaceholderText = string.Empty;
        fileCell.Controls.Add(_upFileBox, 0, 0);

        _upBrowseButton.Text = "浏览…";
        _upBrowseButton.AutoSize = true;
        _upBrowseButton.Margin = new Padding(6, 0, 0, 0);
        fileCell.Controls.Add(_upBrowseButton, 1, 0);

        grid.Controls.Add(fileCell, 1, 0);

        _upButton.Text = "上传到爱思服务器";
        _upButton.AutoSize = true;
        _upButton.Margin = new Padding(8, 0, 0, 0);
        grid.Controls.Add(_upButton, 2, 0);

        // 解析结果显示在文件路径旁边（单行，超出省略）
        _upStatusLabel.AutoSize = false;
        _upStatusLabel.Dock = DockStyle.Fill;
        _upStatusLabel.AutoEllipsis = true;
        _upStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _upStatusLabel.ForeColor = Color.DimGray;
        _upStatusLabel.Margin = new Padding(10, 8, 3, 0);
        grid.Controls.Add(_upStatusLabel, 4, 0);

        // 底部提示
        var hints = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(3, 2, 3, 0),
        };
        hints.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hints.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hints.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hintModern = new Label
        {
            Text = "现代设备：请先用 Blobsaver 保存 SHSH，再选择文件上传。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0),
        };
        hints.Controls.Add(hintModern, 0, 0);

        var hintLegacy = new Label
        {
            Text = "iOS 11 及以下：上传后可能被精简。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0),
        };
        hints.Controls.Add(hintLegacy, 0, 1);

        var hintKeep = new Label
        {
            Text = "无论如何，都应该在本地保存有效的 shsh 原件！",
            AutoSize = true,
            ForeColor = Color.FromArgb(0xC0, 0x00, 0x00),
            Margin = new Padding(0),
        };
        hints.Controls.Add(hintKeep, 0, 2);

        grid.Controls.Add(hints, 0, 1);
        grid.SetColumnSpan(hints, 5);

        Resize += (_, _) => _upStatusLabel.Invalidate();
        return grid;
    }

    /// <summary>去掉首尾空白与引号（资源管理器「复制为路径」会带引号）。</summary>
    private static string NormalizePath(string? raw) => UploadPathResolver.NormalizePath(raw);

    private void BrowseUploadFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 SHSH 文件（可多选）",
            Filter = "SHSH 文件 (*.shsh;*.shsh2;*.plist)|*.shsh;*.shsh2;*.plist|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetUploadSelection(dialog.FileNames);
    }

    /// <summary>输入框失焦：内容与上次成功解析的不同就重新解析（失败不缓存，允许再试，I-5）。</summary>
    private void OnUploadFileBoxLeave()
    {
        _uploadInputTimer.Stop();
        _ = ResolveUploadInputAsync(force: false);
    }

    /// <summary>
    /// Enter 在单行 TextBox 里不是输入键，默认会走 Form.ProcessDialogKey 命中 AcceptButton（触发查询）。
    /// 这里优先拦下：输入框有焦点时 Enter = 解析/扫描上传路径（I-1）。
    /// </summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter && _upFileBox.Focused && !_busy)
        {
            _uploadInputTimer.Stop();
            _ = ResolveUploadInputAsync(force: true);
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    /// <summary>
    /// 后台解析输入（检查路径/扫描文件夹都可能阻塞，尤其 UNC）：避免 UI 线程卡死（I-3/I-4）。
    /// 支持单文件、带引号路径、多路径、文件夹（含网络共享）。
    /// </summary>
    private async Task ResolveUploadInputAsync(bool force)
    {
        // 注意：这里不能用 NormalizePath 去整串引号，否则 "A";"B" 会被拆坏
        var text = (_upFileBox.Text ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            _uploadResolveToken++;
            _lastResolvedText = string.Empty;
            _uploadFiles.Clear();
            _uploadInfos.Clear();
            _uploadStamps.Clear();
            SetUploadStatus(string.Empty, Color.DimGray);
            _toolTip.SetToolTip(_upStatusLabel, string.Empty);
            UpdateUploadButton();
            return;
        }

        if (!force && string.Equals(text, _lastResolvedText, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var token = ++_uploadResolveToken;
        SetUploadStatus("正在检查路径 …", Color.FromArgb(0x33, 0x33, 0x33));

        List<string> files;
        List<string> problems;
        try
        {
            (files, problems) = await Task.Run(() =>
            {
                var expanded = UploadPathResolver.Expand(text, out var found);
                return (expanded, found);
            });
        }
        catch (Exception ex)
        {
            if (!_closing && token == _uploadResolveToken)
            {
                SetUploadStatus("路径解析失败", Color.Firebrick);
                _toolTip.SetToolTip(_upStatusLabel, ex.Message);
            }

            return;
        }

        if (_closing || token != _uploadResolveToken)
        {
            return;
        }

        if (files.Count == 0)
        {
            _lastResolvedText = string.Empty; // 失败不缓存，允许重试（I-5）
            _uploadFiles.Clear();
            _uploadInfos.Clear();
            _uploadStamps.Clear();
            SetUploadStatus(problems.Count > 0 ? problems[0] : "没有可加载的 SHSH 文件", Color.Firebrick);
            _toolTip.SetToolTip(_upStatusLabel, string.Join(Environment.NewLine, problems));
            UpdateUploadButton();
            return;
        }

        _lastResolvedText = text;
        SetUploadSelection(files);
        if (problems.Count > 0)
        {
            _toolTip.SetToolTip(_upStatusLabel, string.Join(Environment.NewLine, problems.Take(10)));
        }
    }

    /// <summary>设置当前选中的文件集合（1 个或多个），并立刻解析。</summary>
    private void SetUploadSelection(IReadOnlyList<string> files)
    {
        _uploadFiles.Clear();
        _uploadFiles.AddRange(files
            .Select(NormalizePath)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        _suppressUploadEvents = true;
        try
        {
            _upFileBox.Text = _uploadFiles.Count switch
            {
                0 => string.Empty,
                1 => _uploadFiles[0],
                _ => $"已选择 {_uploadFiles.Count} 个文件（{Path.GetFileName(_uploadFiles[0])} 等）",
            };
        }
        finally
        {
            _suppressUploadEvents = false;
        }

        RefreshUploadSelection();
    }

    /// <summary>文件指纹：修改时间 + 大小，用来判断缓存是否过期（N-1）。</summary>
    private static long FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks ^ info.Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>界面上方选择框里的机型（用于文件名识别不出机型时兜底）。</summary>
    private string? SelectedProductTypeModel()
    {
        if (_productTypeCombo.SelectedItem is ProductTypeChoice { Variant: { } variant })
        {
            return variant.ProductType;
        }

        if (_modelCombo.SelectedItem is DeviceGroup group && group.Variants.Count > 0)
        {
            return group.Variants[0].ProductType;
        }

        return null;
    }

    /// <summary>某个文件是否具备上传条件（缺机型时可用上方选择框兜底）。</summary>
    private bool CanUpload(ShshFileInfo? info)
        => ShshUploadPlanner.TryResolve(info, SelectedProductTypeModel(), out _, out _);

    /// <summary>重新解析当前选中的全部文件，再刷新状态与按钮。</summary>
    private void RefreshUploadSelection()
    {
        _uploadInfos.Clear();
        _uploadStamps.Clear();

        foreach (var path in _uploadFiles)
        {
            var stamp = FileStamp(path);
            var info = File.Exists(path) && ShshFileInfo.TryParse(path, out var parsed) ? parsed : null;
            _uploadInfos.Add(info);
            _uploadStamps.Add(stamp);
        }

        // 机型优先级：SHSH 文件自带的机型 > 上方选择框。
        // 单文件且文件能识别机型时，把选择框同步过去，避免界面与实际上传参数不一致。
        if (_uploadFiles.Count == 1 && _uploadInfos.Count == 1)
        {
            SyncModelSelectionToFile(_uploadInfos[0]);
        }

        UpdateUploadSummary();
    }

    /// <summary>把上方「机型 / ProductType」同步成 SHSH 文件里的机型（找不到就保持原样）。</summary>
    private void SyncModelSelectionToFile(ShshFileInfo? info)
    {
        if (info is null || string.IsNullOrEmpty(info.ProductType))
        {
            return;
        }

        DeviceGroup? targetGroup = null;
        DeviceModel? targetVariant = null;
        foreach (var item in _modelCombo.Items)
        {
            if (item is not DeviceGroup group)
            {
                continue;
            }

            var variant = group.Variants.FirstOrDefault(v =>
                string.Equals(v.ProductType, info.ProductType, StringComparison.OrdinalIgnoreCase));
            if (variant is null)
            {
                continue;
            }

            targetGroup = group;
            targetVariant = variant;
            break;
        }

        if (targetGroup is null || targetVariant is null
            || ReferenceEquals(_modelCombo.SelectedItem, targetGroup))
        {
            return;
        }

        _suppressEvents = true;
        try
        {
            _modelCombo.SelectedItem = targetGroup;
            RebuildProductTypeChoices();
            foreach (var item in _productTypeCombo.Items)
            {
                if (item is ProductTypeChoice { Variant: { } variant }
                    && string.Equals(variant.ProductType, info.ProductType, StringComparison.OrdinalIgnoreCase))
                {
                    _productTypeCombo.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateModelHint();
    }

    /// <summary>
    /// 只用缓存结果重算状态/tooltip/按钮（不重新读文件）。
    /// 上方机型/ProductType 改变时调用，让“手动选机型兜底”立刻生效。
    /// </summary>
    private void UpdateUploadSummary()
    {
        var problems = new List<string>();
        var ready = 0;
        var manualUsed = new List<string>();

        for (var i = 0; i < _uploadFiles.Count; i++)
        {
            var path = _uploadFiles[i];
            var info = i < _uploadInfos.Count ? _uploadInfos[i] : null;
            if (ShshUploadPlanner.TryResolve(info, SelectedProductTypeModel(), out var planned, out var reason))
            {
                ready++;
                if (info is not null && string.IsNullOrEmpty(info.ProductType))
                {
                    manualUsed.Add($"{Path.GetFileName(path)} → {planned.ProductType}");
                }
            }
            else
            {
                problems.Add($"{Path.GetFileName(path)}：{reason}");
            }
        }

        if (_uploadFiles.Count == 0)
        {
            SetUploadStatus(string.Empty, Color.DimGray);
            _toolTip.SetToolTip(_upStatusLabel, string.Empty);
        }
        else if (_uploadFiles.Count == 1)
        {
            var info = _uploadInfos[0];
            if (ShshUploadPlanner.TryResolve(info, SelectedProductTypeModel(), out var resolved, out _))
            {
                var manual = string.IsNullOrEmpty(info!.ProductType);
                SetUploadStatus(
                    $"{resolved.ProductType} · {resolved.IosLabel} · ECID {resolved.Ecid}"
                    + (resolved.IsShsh2 ? " · shsh2" : string.Empty)
                    + (manual ? $"（未识别机型，使用上方选择：{resolved.ProductType}）" : string.Empty),
                    manual ? Color.FromArgb(0x8A, 0x6D, 0x00) : Color.DimGray);
            }
            else
            {
                SetUploadStatus(
                    problems[0].Contains('：') ? problems[0][(problems[0].IndexOf('：') + 1)..] : "无法上传",
                    Color.FromArgb(0x8A, 0x6D, 0x00));
            }

            _toolTip.SetToolTip(_upStatusLabel, problems.Count > 0 ? problems[0] : _upStatusLabel.Text);
        }
        else
        {
            SetUploadStatus(
                $"已选 {_uploadFiles.Count} 个文件：{ready} 个可上传"
                + (problems.Count > 0 ? $"，{problems.Count} 个有问题" : string.Empty)
                + (manualUsed.Count > 0 ? $"，{manualUsed.Count} 个使用上方机型" : string.Empty),
                ready > 0
                    ? (manualUsed.Count > 0 ? Color.FromArgb(0x8A, 0x6D, 0x00) : Color.DimGray)
                    : Color.FromArgb(0x8A, 0x6D, 0x00));
            _toolTip.SetToolTip(
                _upStatusLabel,
                problems.Count == 0
                    ? $"共 {_uploadFiles.Count} 个文件，全部可上传"
                      + (manualUsed.Count > 0
                          ? Environment.NewLine + "使用上方选择框机型的文件：" + Environment.NewLine
                            + string.Join(Environment.NewLine, manualUsed.Take(10))
                          : string.Empty)
                    : string.Join(Environment.NewLine, problems.Take(15))
                      + (problems.Count > 15 ? $"{Environment.NewLine}…另有 {problems.Count - 15} 个" : string.Empty));
        }

        UpdateUploadButton();
    }

    /// <summary>
    /// 刷新上传/浏览按钮。先做一次轻量指纹检查：文件被改内容、删除或修复后才重新解析（N-1 / N-2），
    /// 忙时一并锁住「浏览…」（N-3）。
    /// </summary>
    private void UpdateUploadButton()
    {
        if (_busy)
        {
            _upButton.Enabled = false;
            _upBrowseButton.Enabled = false;
            return;
        }

        _upBrowseButton.Enabled = true;

        var changed = _uploadFiles.Count != _uploadStamps.Count;
        for (var i = 0; !changed && i < _uploadFiles.Count; i++)
        {
            changed = FileStamp(_uploadFiles[i]) != _uploadStamps[i];
        }

        if (changed)
        {
            RefreshUploadSelection();
            return;
        }

        _upButton.Enabled = _uploadInfos.Any(CanUpload);
    }

    /// <summary>
    /// 输入变化：只重置防抖计时器，不做任何文件系统调用（UNC 的 File.Exists / Directory.Exists 也会阻塞，I-3）。
    /// 450ms 后停在输入框再解析。
    /// </summary>
    private void OnUploadFileChanged()
    {
        if (_suppressUploadEvents)
        {
            return;
        }

        _uploadInputTimer.Stop();
        _uploadInputTimer.Start();
    }

    /// <summary>上传流程：解析文件（可多个）→ 二次确认（U-5）→ 逐个上传。</summary>
    private async Task UploadFromPanelAsync()
    {
        var jobs = new List<(string Path, ShshFileInfo Info, byte[] Content)>();
        var skipped = 0;

        for (var i = 0; i < _uploadFiles.Count; i++)
        {
            var path = _uploadFiles[i];
            var info = i < _uploadInfos.Count ? _uploadInfos[i] : null;
            if (!ShshUploadPlanner.TryResolve(info, SelectedProductTypeModel(), out var resolved, out _))
            {
                skipped++;
                continue;
            }

            try
            {
                jobs.Add((path, resolved, await File.ReadAllBytesAsync(path)));
            }
            catch
            {
                skipped++;
            }
        }

        if (jobs.Count == 0)
        {
            SetUploadStatus("没有可上传的文件", Color.Firebrick);
            return;
        }

        // U-5: 上传前确认（单个显示元数据；多选显示数量）
        string body;
        if (jobs.Count == 1)
        {
            var only = jobs[0].Info;
            var source = only.IsShsh2 ? "文件名" : "plist / 文件名";
            body = $"将把下面这条备份上传到爱思服务器：{Environment.NewLine}{Environment.NewLine}"
                   + $"ECID：{only.Ecid}{Environment.NewLine}"
                   + $"机型：{only.ProductType}{Environment.NewLine}"
                   + $"版本：{only.IosLabel}{Environment.NewLine}{Environment.NewLine}"
                   + $"机型与版本取自{source}，请确认无误。继续上传？";
        }
        else
        {
            body = $"将按顺序上传 {jobs.Count} 个文件到爱思服务器。"
                   + (skipped > 0 ? $"{Environment.NewLine}另有 {skipped} 个文件因信息不全会跳过。" : string.Empty)
                   + $"{Environment.NewLine}{Environment.NewLine}机型/版本取自文件名或 plist，请确认无误。继续上传？";
        }

        if (MessageBox.Show(this, body, "确认上传", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            SetUploadStatus("已取消上传", Color.DimGray);
            return;
        }

        // R-1: 上传期间统一置忙
        SetBusy(true);
        _uploadCts?.Cancel();
        var uploadCts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Min(30, Math.Max(2, jobs.Count * 2))));
        _uploadCts = uploadCts;

        var succeeded = 0;
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                uploadCts.Token.ThrowIfCancellationRequested();
                SetUploadStatus(
                    jobs.Count == 1
                        ? "正在上传 …"
                        : $"正在上传 {i + 1}/{jobs.Count}：{Path.GetFileName(jobs[i].Path)} …",
                    Color.FromArgb(0x33, 0x33, 0x33));

                var result = await _client.UploadShshFileAsync(jobs[i].Info, jobs[i].Content, uploadCts.Token);
                if (result.Success)
                {
                    succeeded++;
                }
                else
                {
                    failures.Add($"{Path.GetFileName(jobs[i].Path)}：{result.Message}");
                }
            }

            if (failures.Count == 0)
            {
                SetUploadStatus($"上传成功：{succeeded} 个文件（服务器已接受）", Color.FromArgb(0x1B, 0x6E, 0x2F));
                _toolTip.SetToolTip(_upStatusLabel, $"成功 {succeeded} 个" + (skipped > 0 ? $"，跳过 {skipped} 个" : string.Empty));
            }
            else
            {
                SetUploadStatus(
                    $"上传完成：成功 {succeeded}，失败 {failures.Count}" + (skipped > 0 ? $"，跳过 {skipped}" : string.Empty),
                    Color.Firebrick);
                _toolTip.SetToolTip(_upStatusLabel, string.Join(Environment.NewLine, failures.Take(15)));
                _rawBox.Text = string.Join(Environment.NewLine, failures);
            }
        }
        catch (OperationCanceledException) when (!_closing)
        {
            SetUploadStatus("上传超时或已取消", Color.Firebrick);
        }
        catch (OperationCanceledException)
        {
            // 关窗取消，不更新界面
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SetUploadStatus("上传失败", Color.Firebrick);
                _toolTip.SetToolTip(_upStatusLabel, ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_uploadCts, uploadCts))
            {
                _uploadCts = null;
            }

            uploadCts.Dispose();
            SetBusy(false);
        }
    }

    private void SetUploadStatus(string text, Color color)
    {
        _upStatusLabel.Text = text;
        _upStatusLabel.ForeColor = color;
    }
}











