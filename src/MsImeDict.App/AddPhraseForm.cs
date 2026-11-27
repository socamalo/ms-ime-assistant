using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>确认加入词库的对话框：词条、全拼编码、简拼编码、候选位。</summary>
public sealed class AddPhraseForm : Form
{
    private readonly TextBox _wordBox = new() { Dock = DockStyle.Top, Height = 60, Multiline = true };
    private readonly TextBox _codeBox = new() { Dock = DockStyle.Top };
    private readonly TextBox _abbrBox = new() { Dock = DockStyle.Top };
    private readonly ComboBox _rankBox = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private bool _syncing;

    public string Word => _wordBox.Text.Trim();
    public string Code => _codeBox.Text.Trim().ToLowerInvariant();
    public string Abbr => _abbrBox.Text.Trim().ToLowerInvariant();
    public int Rank => _rankBox.SelectedIndex + 1;

    public AddPhraseForm(string selectedText, EncodedInfo info, int defaultRank)
    {
        Text = "加入个人词典";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 300);
        AutoScaleMode = AutoScaleMode.Dpi;

        var wordLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "词条" };
        var codeLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "编码（全拼，打这几个字母就能打出该词）" };
        var abbrLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "简拼编码（首字母，如 zrr；留空表示不写）" };
        var rankLabel = new Label { Dock = DockStyle.Top, Height = 22, Text = "候选位置" };

        for (var i = 1; i <= 9; i++) _rankBox.Items.Add(i);
        _rankBox.SelectedIndex = Math.Clamp(defaultRank, 1, 9) - 1;

        _wordBox.Text = selectedText;
        _codeBox.Text = info.Codes.FirstOrDefault() ?? "";
        _abbrBox.Text = PinyinEncoder.AbbreviationOf(selectedText);

        // 改词条时同步重算两个编码，用户随后仍可手工改
        _wordBox.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            var w = _wordBox.Text.Trim();
            _codeBox.Text = new PinyinEncoder().Encode(w).CandidateCodes.FirstOrDefault() ?? "";
            _abbrBox.Text = PinyinEncoder.AbbreviationOf(w);
            _syncing = false;
        };

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
            Text = info.Notes.Count > 0 ? string.Join("；", info.Notes) : "写入后直接打字即可，不需要切换输入法"
        };

        // DockStyle.Top 是后加的先贴顶，按视觉顺序倒着 Add
        Controls.Add(buttons);
        Controls.Add(notes);
        Controls.Add(rankLabel);
        Controls.Add(_rankBox);
        Controls.Add(abbrLabel);
        Controls.Add(_abbrBox);
        Controls.Add(codeLabel);
        Controls.Add(_codeBox);
        Controls.Add(wordLabel);
        Controls.Add(_wordBox);
    }
}

/// <summary>给对话框用的编码信息。</summary>
public sealed class EncodedInfo
{
    public List<string> Codes { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}
