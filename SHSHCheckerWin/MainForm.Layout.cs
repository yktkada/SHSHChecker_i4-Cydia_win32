using System.Diagnostics;

namespace SHSHCheckerWin;

/// <summary>
/// MainForm 的布局部分：控件层级、尺寸与对齐全部集中在这里，
/// 交互编排留在 MainForm.cs，避免单个文件继续膨胀（审查 M-7）。
/// </summary>
public sealed partial class MainForm
{
    private void BuildUi()
    {
        // 上半：查询区（输入 + 结果）；下半：上传区，两者互不干扰
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12, 10, 12, 10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));

        _statusLabel.AutoSize = true;
        // F-4: 长结论（保存路径、重名提示）不能被窗口右边缘裁掉
        _statusLabel.AutoEllipsis = true;
        _statusLabel.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0);
        _statusLabel.Margin = new Padding(0, 8, 0, 4);
        Resize += (_, _) => _statusLabel.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0);

        root.Controls.Add(BuildInputPanel(), 0, 0);
        root.Controls.Add(_statusLabel, 0, 1);
        root.Controls.Add(BuildResultPanel(), 0, 2);
        root.Controls.Add(BuildUploadPanel(), 0, 3);
        Controls.Add(root);

        SetStatus("就绪：输入 ECID（hex），选择机型后点击查询。", Color.DimGray);
    }

    private Control BuildInputPanel()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 6,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (var i = 0; i < 6; i++)
        {
            grid.RowStyles.Add(i == 4
                ? new RowStyle(SizeType.Absolute, 24f)
                : new RowStyle(SizeType.AutoSize));
        }

        _ecidBox.Dock = DockStyle.Fill;
        _ecidBox.Font = new Font("Consolas", 10.5F);
        _ecidBox.PlaceholderText = "十六进制：0011AABBCCDDEEFF　或　十进制：1234567890123（13 位以上自动识别）";
        grid.Controls.Add(MakeLabel("ECID（hex）"), 0, 0);
        grid.Controls.Add(_ecidBox, 1, 0);

        // 实时十进制预览 / 格式提示
        _ecidPreview.AutoSize = true;
        _ecidPreview.Margin = new Padding(3, 2, 3, 2);
        _ecidPreview.ForeColor = Color.DimGray;
        grid.Controls.Add(_ecidPreview, 1, 1);

        // 机型（大类）/ ProductType（该大类的全部变体）同一行，左右各严格 50%
        var selectorRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 0),
        };
        selectorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        selectorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        _modelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modelCombo.MaxDropDownItems = 22;

        _productTypeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _productTypeCombo.MaxDropDownItems = 22;

        selectorRow.Controls.Add(MakeSelectorCell("机型（大类）", _modelCombo, new Padding(0, 0, 4, 0)), 0, 0);
        selectorRow.Controls.Add(MakeSelectorCell("ProductType", _productTypeCombo, new Padding(4, 0, 0, 0)), 1, 0);

        grid.Controls.Add(selectorRow, 1, 2);

        // 64bit 设备开关：默认只显示 32bit；勾 arm64 才能勾 arm64e
        _arm64Check.Text = "启用 arm64 设备查询";
        _arm64Check.AutoSize = true;
        _arm64Check.Margin = new Padding(3, 4, 16, 2);

        _arm64eCheck.Text = "启用 arm64e 设备查询";
        _arm64eCheck.AutoSize = true;
        _arm64eCheck.Enabled = false;
        _arm64eCheck.Margin = new Padding(0, 4, 3, 2);

        var archRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 0),
        };
        archRow.Controls.Add(_arm64Check);
        archRow.Controls.Add(_arm64eCheck);
        grid.Controls.Add(archRow, 1, 3);

        _modelHint.AutoSize = false;
        _modelHint.Dock = DockStyle.Fill;
        _modelHint.AutoEllipsis = true;
        _modelHint.TextAlign = ContentAlignment.MiddleLeft;
        _modelHint.ForeColor = Color.DimGray;
        _modelHint.Margin = new Padding(3, 2, 3, 2);
        grid.Controls.Add(_modelHint, 1, 4);

        _queryButton.Text = "查询爱思 SHSH 备份";
        _queryButton.AutoSize = true;
        _queryButton.Padding = new Padding(12, 4, 12, 4);
        _queryButton.UseVisualStyleBackColor = true;

        _cydiaButton.Text = "查询 Cydia 备份";
        _cydiaButton.AutoSize = true;
        _cydiaButton.Padding = new Padding(10, 4, 10, 4);
        _cydiaButton.UseVisualStyleBackColor = true;
        _cydiaButton.Margin = new Padding(8, 0, 0, 0);

        _downloadButton.Text = "下载选中";
        _downloadButton.AutoSize = true;
        _downloadButton.Padding = new Padding(10, 4, 10, 4);
        _downloadButton.UseVisualStyleBackColor = true;
        _downloadButton.Enabled = false;
        _downloadButton.Margin = new Padding(8, 0, 0, 0);

        _downloadAllButton.Text = "全部下载";
        _downloadAllButton.AutoSize = true;
        _downloadAllButton.Padding = new Padding(10, 4, 10, 4);
        _downloadAllButton.UseVisualStyleBackColor = true;
        _downloadAllButton.Enabled = false;
        _downloadAllButton.Margin = new Padding(8, 0, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
        };
        buttons.Controls.Add(_queryButton);
        buttons.Controls.Add(_cydiaButton);
        buttons.Controls.Add(_downloadButton);
        buttons.Controls.Add(_downloadAllButton);

        grid.Controls.Add(buttons, 1, 5);
        return grid;
    }

    /// <summary>构造「标签在上、下拉框在下」的一格；两格宽度相同，保证下拉框始终平分。</summary>
    private static TableLayoutPanel MakeSelectorCell(string labelText, ComboBox combo, Padding margin)
    {
        var cell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = margin,
        };
        cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 2),
            ForeColor = Color.FromArgb(0x44, 0x44, 0x44),
        };
        combo.Dock = DockStyle.Fill;

        cell.Controls.Add(label, 0, 0);
        cell.Controls.Add(combo, 0, 1);
        return cell;
    }

    private Control BuildResultPanel()
    {
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            Margin = new Padding(0, 4, 0, 0),
            Panel1MinSize = 120,
            Panel2MinSize = 120,
        };

        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.HideSelection = false;
        _listView.Dock = DockStyle.Fill;
        _listView.Columns.Add("来源", 56);
        _listView.Columns.Add("ProductType / Model", 150);
        _listView.Columns.Add("iOS 版本", 110);
        _listView.Columns.Add("其他信息", 700);
        _listView.Resize += (_, _) => AdjustResultColumns();
        _split.Panel1.Controls.Add(_listView);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var rawTab = new TabPage("原始响应");
        _rawBox.Dock = DockStyle.Fill;
        _rawBox.ReadOnly = true;
        _rawBox.Font = new Font("Consolas", 9F);
        _rawBox.WordWrap = false;
        _rawBox.ScrollBars = RichTextBoxScrollBars.Both;
        _rawBox.BackColor = Color.White;
        _rawBox.DetectUrls = false;
        rawTab.Controls.Add(_rawBox);

        var paramTab = new TabPage("请求参数");
        _paramBox.Dock = DockStyle.Fill;
        _paramBox.Multiline = true;
        _paramBox.ReadOnly = true;
        _paramBox.ScrollBars = ScrollBars.Both;
        _paramBox.WordWrap = false;
        _paramBox.Font = new Font("Consolas", 8.5F);
        _paramBox.BackColor = Color.White;
        paramTab.Controls.Add(_paramBox);

        tabs.TabPages.Add(rawTab);
        tabs.TabPages.Add(paramTab);
        _split.Panel2.Controls.Add(tabs);

        return _split;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(3, 7, 8, 3),
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private void AdjustResultColumns()
    {
        if (_listView.Columns.Count < 4)
        {
            return;
        }

        var used = _listView.Columns[0].Width + _listView.Columns[1].Width + _listView.Columns[2].Width;
        _listView.Columns[3].Width = Math.Max(200, _listView.ClientSize.Width - used - 4);
    }
}












