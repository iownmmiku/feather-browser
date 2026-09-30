using FeatherBrowser.Core;

namespace FeatherBrowser.UI;

/// <summary>
/// 管理本程序保存的登录凭据。
///
/// <p>列表里默认**不显示密码**：要按「显示密码」才逐条解密。这样即使有人站在旁边，
/// 打开这个窗口也看不到任何明文。
/// </summary>
internal sealed class PasswordsDialog : Form
{
    private readonly PasswordStore _store;
    private readonly ListBox _list;
    private readonly Label _hint = new();
    private readonly CheckBox _showPasswords = new();

    private List<PasswordEntry> _entries = new();

    public PasswordsDialog(PasswordStore store)
    {
        _store = store;

        Text = "保存的密码 - 轻羽浏览器";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.UiFont;
        Padding = new Padding(Theme.Sx(20));
        ClientSize = new Size(Theme.Sx(720), Theme.Sy(520));
        MinimumSize = new Size(Theme.Sx(560), Theme.Sy(400));

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            Text = "保存的密码",
            Font = Theme.UiFontTitle,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, Theme.Sy(12)),
        }, 0, 0);

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Theme.Sy(40),
            IntegralHeight = false,
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
        };
        _list.DrawItem += OnDrawItem;
        root.Controls.Add(_list, 0, 1);

        _hint.Font = Theme.UiFontSmall;
        _hint.ForeColor = Theme.TextDim;
        _hint.BackColor = Color.Transparent;
        _hint.AutoSize = true;
        _hint.MaximumSize = new Size(Theme.Sx(660), 0);
        _hint.Margin = new Padding(0, Theme.Sy(10), 0, 0);
        root.Controls.Add(_hint, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, Theme.Sy(12), 0, 0),
        };

        _showPasswords.Text = "显示密码";
        _showPasswords.AutoSize = true;
        _showPasswords.ForeColor = Theme.Text;
        _showPasswords.BackColor = Color.Transparent;
        _showPasswords.Margin = new Padding(0, Theme.Sy(10), Theme.Sx(16), 0);
        _showPasswords.CheckedChanged += (_, _) => _list.Invalidate();
        buttons.Controls.Add(_showPasswords);

        buttons.Controls.Add(MakeButton("复制用户名", CopyUsername));
        buttons.Controls.Add(MakeButton("复制密码", CopyPassword));
        buttons.Controls.Add(MakeButton("删除这条", DeleteSelected));
        buttons.Controls.Add(MakeButton("清空全部", ClearAll));
        buttons.Controls.Add(MakeButton("关闭", Close));

        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);

        Theme.ApplyTo(this);
        Theme.Changed += OnThemeChanged;
        FormClosed += (_, _) => Theme.Changed -= OnThemeChanged;

        LoadEntries();
    }

    private void OnThemeChanged()
    {
        if (!IsDisposed)
        {
            Theme.ApplyTo(this);
            WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            Width = Theme.Sx(120),
            Height = Theme.Sy(40),
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 0, Theme.Sx(8), 0),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void LoadEntries()
    {
        _entries = _store.Snapshot();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (PasswordEntry entry in _entries)
        {
            _list.Items.Add(entry);
        }
        _list.EndUpdate();

        int fromQuark = _store.CountBySource("quark");
        _hint.Text = _entries.Count == 0
            ? "还没有保存任何登录信息。在网站上登录后，轻羽会询问是否保存；也可以从菜单里的「从夸克导入」把夸克的密码迁过来。"
            : $"共 {_entries.Count} 条" +
              (fromQuark > 0 ? $"（其中 {fromQuark} 条从夸克导入）" : "") +
              "。密码用当前 Windows 账户加密保存，换账户或换电脑都解不开。";
    }

    private PasswordEntry Selected =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _entries.Count
            ? _entries[_list.SelectedIndex]
            : null;

    private void CopyUsername()
    {
        PasswordEntry entry = Selected;
        if (entry == null)
        {
            return;
        }
        TrySetClipboard(entry.Username, "用户名");
    }

    private void CopyPassword()
    {
        PasswordEntry entry = Selected;
        if (entry == null)
        {
            return;
        }
        string password = _store.RevealPassword(entry);
        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show(this, "这条密码解不开，可能来自另一个 Windows 账户。",
                "保存的密码", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        TrySetClipboard(password, "密码");
    }

    private void TrySetClipboard(string text, string what)
    {
        try
        {
            Clipboard.SetText(text ?? "");
            _hint.Text = $"已复制{what}到剪贴板";
        }
        catch (Exception ex)
        {
            _hint.Text = "复制失败：" + ex.Message;
        }
    }

    private void DeleteSelected()
    {
        PasswordEntry entry = Selected;
        if (entry == null)
        {
            return;
        }
        DialogResult answer = MessageBox.Show(this,
            $"删除 {entry.DisplaySite} 的这条登录信息？", "保存的密码",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _store.RemoveAt(_list.SelectedIndex);
        LoadEntries();
    }

    private void ClearAll()
    {
        if (_entries.Count == 0)
        {
            return;
        }
        DialogResult answer = MessageBox.Show(this,
            $"确定要删除全部 {_entries.Count} 条登录信息吗？此操作不可撤销。",
            "保存的密码", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _store.Clear();
        LoadEntries();
    }

    private void OnDrawItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count)
        {
            return;
        }
        if (_list.Items[e.Index] is not PasswordEntry entry)
        {
            return;
        }

        Graphics g = e.Graphics;
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        using (var back = new SolidBrush(selected ? Theme.AccentSoft : Theme.Background))
        {
            g.FillRectangle(back, e.Bounds);
        }

        int left = e.Bounds.Left + Theme.Sx(10);
        int width = e.Bounds.Width - Theme.Sx(20);

        Rectangle siteRect = new(left, e.Bounds.Top + Theme.Sy(3), width,
            (e.Bounds.Height - Theme.Sy(6)) / 2);
        TextRenderer.DrawText(g, entry.DisplaySite, Theme.UiFont, siteRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        string second = entry.DisplayUser;
        string password = "";
        if (_showPasswords.Checked)
        {
            password = _store.RevealPassword(entry);
            second += password.Length > 0 ? "    ·    " + password : "    ·    （解不开）";
        }

        Rectangle userRect = new(left, siteRect.Bottom, width,
            e.Bounds.Height - siteRect.Height - Theme.Sy(3));
        TextRenderer.DrawText(g, second, Theme.UiFontSmall, userRect, Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
