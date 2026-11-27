using System.Drawing;
using System.Windows.Forms;
using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>词库管理界面：看已加入的词条，支持新增、删除、改候选位。</summary>
public sealed class PhraseManagerForm : Form
{
    private readonly PhraseStore _store;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly TextBox _search;

    public PhraseManagerForm(PhraseStore store)
    {
        _store = store;

        Text = "个人词库";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 460);
        MinimumSize = new Size(620, 360);
        Font = new Font("Microsoft YaHei UI", 9f);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            GridLines = false
        };
        _list.Columns.Add("词条", 240);
        _list.Columns.Add("编码", 160);
        _list.Columns.Add("简拼编码", 120);
        _list.Columns.Add("候选位", 60, HorizontalAlignment.Center);
        _list.Columns.Add("加入时间", 140);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 6, 8, 0)
        };
        toolbar.Controls.Add(MakeButton("新增词条", (_, _) => AddPhrase()));
        toolbar.Controls.Add(MakeButton("删除所选", (_, _) => DeleteSelected()));
        toolbar.Controls.Add(MakeButton("去重", (_, _) => Deduplicate()));
        toolbar.Controls.Add(MakeButton("刷新", (_, _) => Reload()));

        _search = new TextBox
        {
            Width = 190,
            Margin = new Padding(10, 2, 4, 0),
            PlaceholderText = "搜索词条或编码"
        };
        _search.TextChanged += (_, _) => Reload();
        toolbar.Controls.Add(_search);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = Color.DimGray
        };

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(toolbar);

        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
        };

        Reload();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, Width = 96, Height = 28, FlatStyle = FlatStyle.System };
        b.Click += onClick;
        return b;
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        var phrases = _store.Search(_search?.Text ?? string.Empty);
        foreach (var p in phrases)
        {
            var item = new ListViewItem(p.Text) { Tag = p };
            item.SubItems.Add(p.Code);
            item.SubItems.Add(p.Abbr.Length == 0 ? "（无）" : p.Abbr);
            item.SubItems.Add(p.Rank.ToString());
            item.SubItems.Add(DateTimeOffset.FromUnixTimeSeconds(p.CreatedAt).ToLocalTime().ToString("MM-dd HH:mm"));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _status.Text = $"共 {phrases.Count} 个词（一个词一行，部署时展开成全拼 + 简拼两条）　·　编码文件：{Paths.LexFile}";
    }

    private void AddPhrase()
    {
        using var input = new TextInputDialog("新增词条", "词条内容：");
        if (input.ShowDialog(this) != DialogResult.OK) return;

        var text = input.Value.Trim();
        if (text.Length == 0) return;

        if (_store.FindByText(text) is not null)
        {
            MessageBox.Show(this, $"「{text}」已在词库中。词库一个词只有一行。", "已存在",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var encoded = new PinyinEncoder().Encode(text);
        if (encoded.CandidateCodes.Count == 0)
        {
            MessageBox.Show(this, "这个内容无法生成可用编码。", "无法加入",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var code = encoded.CandidateCodes[0];
        var phrase = _store.Add(text, code, 1, source: "manual");

        var result = new ImeDeployer().Deploy(_store.Project());
        Reload();
        if (!result.Ok)
            MessageBox.Show(this, result.Message, "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            _status.Text = $"已加入「{text}」：全拼 {code}，简拼 {(phrase.Abbr.Length == 0 ? "（无）" : phrase.Abbr)}";
    }

    private void Deduplicate()
    {
        var removed = _store.RemoveDuplicates();
        if (removed == 0)
        {
            MessageBox.Show(this, "没有重复条目。", "去重", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var result = new ImeDeployer().Deploy(_store.Project());
        Reload();
        MessageBox.Show(this,
            result.Ok ? $"已删除 {removed} 条重复条目，并重新写入输入法。" : $"已删除 {removed} 条，但写入输入法失败：{result.Message}",
            "去重", MessageBoxButtons.OK, result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var names = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => ((Phrase)i.Tag!).Text).Distinct().Take(5).ToList();
        var confirm = MessageBox.Show(this,
            $"确定删除这 {_list.SelectedItems.Count} 条？\n\n{string.Join("\n", names)}",
            "删除词条", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        foreach (ListViewItem item in _list.SelectedItems)
        {
            var phrase = (Phrase)item.Tag!;
            _store.Remove(phrase.Id);
        }

        var result = new ImeDeployer().Deploy(_store.Project());
        Reload();
        if (!result.Ok)
            MessageBox.Show(this, result.Message, "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

/// <summary>单行输入对话框。</summary>
public sealed class TextInputDialog : Form
{
    private readonly TextBox _box;
    public string Value => _box.Text;

    public TextInputDialog(string title, string label)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(420, 130);
        Font = new Font("Microsoft YaHei UI", 9f);

        var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 6, 0, 0) };
        _box = new TextBox { Dock = DockStyle.Top, Margin = new Padding(8) };

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 84, Height = 28 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84, Height = 28 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 6, 8, 0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(buttons);
        Controls.Add(_box);
        Controls.Add(lbl);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
