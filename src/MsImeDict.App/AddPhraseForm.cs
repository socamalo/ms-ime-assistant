using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>确认加入词库的对话框：确认词条、编码、候选位。</summary>
public sealed class AddPhraseForm : Form
{
    private readonly TextBox _wordBox = new() { Dock = DockStyle.Top, Height = 72, Multiline = true, ReadOnly = true };
    private readonly TextBox _codeBox = new() { Dock = DockStyle.Top };
    private readonly ComboBox _rankBox = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _alsoAbbr = new() { Text = "同时写入简拼（首字母）", Dock = DockStyle.Top, Height = 24 };

    public string SelectedText { get; }
    public IReadOnlyList<string> Codes { get; }
    public int Rank => _rankBox.SelectedIndex + 1;
    public bool AlsoAbbrev => _alsoAbbr.Checked;

    public AddPhraseForm(string selectedText, EncodedInfo info, int defaultRank)
    {
        SelectedText = selectedText;
        Codes = info.Codes;

        Text = "加入个人词典";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(420, 230);
        AutoScaleMode = AutoScaleMode.Dpi;

        var labels = new Label { Dock = DockStyle.Top, Height = 22, Text = "选中的文字" };
        var codeLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "编码（打这几个字母就能打出该词）" };
        var rankLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "候选位置" };

        for (var i = 1; i <= 9; i++) _rankBox.Items.Add(i);
        _rankBox.SelectedIndex = Math.Clamp(defaultRank, 1, 9) - 1;

        _codeBox.Text = info.Codes.FirstOrDefault() ?? "";
        _alsoAbbr.Checked = info.Codes.Count > 1;

        var ok = new Button { Text = "加入", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 6, 8, 0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var notes = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            ForeColor = Color.DimGray,
            Text = info.Notes.Count > 0 ? string.Join("；", info.Notes) : "写入后切换一次输入法即可看到该词"
        };

        Controls.Add(buttons);
        Controls.Add(notes);
        Controls.Add(rankLabel);
        Controls.Add(_rankBox);
        Controls.Add(codeLabel);
        Controls.Add(_codeBox);
        Controls.Add(labels);
        Controls.Add(_wordBox);
        _wordBox.Text = selectedText;
    }

    /// <summary>最终要写入的编码列表。</summary>
    public List<string> ResolveCodes()
    {
        var codes = new List<string>();
        var manual = _codeBox.Text.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(manual)) codes.Add(manual);
        if (AlsoAbbrev)
        {
            foreach (var c in Codes)
                if (!codes.Contains(c)) codes.Add(c);
        }
        return codes;
    }
}

/// <summary>给对话框用的编码信息。</summary>
public sealed class EncodedInfo
{
    public List<string> Codes { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}
