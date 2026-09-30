using FeatherBrowser.Core;
using FeatherBrowser.Services;

namespace FeatherBrowser.UI;

/// <summary>
/// 「从夸克导入」对话框：让用户勾选要迁移哪几类数据。
///
/// <p>复选框用自绘，因为 WinForms 原生复选框在深色主题下会画出白色方块，
/// 与 <see cref="ThemedInputs"/> 的处理方式一致。
/// </summary>
internal sealed class ImportDialog : Form
{
    private readonly CheckBox _bookmarks = new();
    private readonly CheckBox _history = new();
    private readonly CheckBox _passwords = new();
    private readonly NumericUpDown _historyLimit = new();
    private readonly Label _status = new();

    public bool ImportBookmarks => _bookmarks.Checked;

    public bool ImportHistory => _history.Checked;

    public bool ImportPasswords => _passwords.Checked;

    public int HistoryLimit => (int)_historyLimit.Value;

    public ImportDialog()
    {
        Text = "从夸克导入 - 轻羽浏览器";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.UiFont;
        Padding = new Padding(Theme.Sx(24));
        ClientSize = new Size(Theme.Sx(600), Theme.Sy(480));

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            Text = "从夸克导入数据",
            Font = Theme.UiFontTitle,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, Theme.Sy(12)),
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = "数据来源：" + QuarkImporter.QuarkUserDataPath +
                   (QuarkImporter.IsQuarkRunning()
                       ? "\r\n夸克正在运行 —— 导入读取的是文件快照，数据以磁盘上的为准。"
                       : ""),
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            MaximumSize = new Size(Theme.Sx(540), 0),
            Margin = new Padding(0, 0, 0, Theme.Sy(14)),
        }, 0, 1);

        var items = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0),
        };
        AddCheck(items, _bookmarks, "书签", "读取夸克的 Bookmarks，按 URL 去重后合并进来");
        AddCheck(items, _history, "浏览历史", "读取夸克的 History 数据库");
        AddCheck(items, _passwords, "保存的密码",
            "先在内存里解密夸克的密码，立刻用本机 Windows 账户重新加密后写入" +
            "本程序的密码库；不会生成任何明文文件");
        root.Controls.Add(items, 0, 2);

        var limitRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, Theme.Sy(10), 0, 0),
        };
        limitRow.Controls.Add(new Label
        {
            Text = "历史最多导入",
            AutoSize = true,
            ForeColor = Theme.Text,
            Margin = new Padding(0, Theme.Sy(8), Theme.Sx(8), 0),
        });
        _historyLimit.Minimum = 100;
        _historyLimit.Maximum = 20000;
        _historyLimit.Increment = 100;
        _historyLimit.Value = 2000;
        _historyLimit.Width = Theme.Sx(110);
        _historyLimit.Height = Theme.Sy(30);
        limitRow.Controls.Add(_historyLimit);
        limitRow.Controls.Add(new Label
        {
            Text = "条（按访问时间取最近的）",
            AutoSize = true,
            ForeColor = Theme.TextDim,
            Margin = new Padding(Theme.Sx(8), Theme.Sy(8), 0, 0),
        });
        root.Controls.Add(limitRow, 0, 3);

        _status.Text =
            "说明：\r\n" +
            "· 已经在轻羽里的书签不会被覆盖，重复的会跳过。\r\n" +
            "· 历史在内存里只保留最近 800 条，导入超过这个数量的部分会被淘汰。\r\n" +
            "· 密码解密依赖当前 Windows 账户；如果夸克的数据来自别的账户或别的电脑，会解密失败。\r\n" +
            "· 本程序没有同步、没有云端，所有数据都只在这台机器上。";
        _status.Font = Theme.UiFontSmall;
        _status.ForeColor = Theme.TextDim;
        _status.BackColor = Color.Transparent;
        _status.AutoSize = true;
        _status.MaximumSize = new Size(Theme.Sx(540), 0);
        _status.Margin = new Padding(0, Theme.Sy(16), 0, 0);
        root.Controls.Add(_status, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, Theme.Sy(16), 0, 0),
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = Theme.Sx(116),
            Height = Theme.Sy(42),
            FlatStyle = FlatStyle.System,
        };
        var ok = new Button
        {
            Text = "开始导入",
            Width = Theme.Sx(140),
            Height = Theme.Sy(42),
            FlatStyle = FlatStyle.System,
        };
        ok.Click += (_, _) => Confirm();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons, 0, 5);

        Controls.Add(root);

        _bookmarks.Checked = true;
        _history.Checked = true;
        _passwords.Checked = true;

        AcceptButton = ok;
        CancelButton = cancel;

        Theme.ApplyTo(this);
        Theme.Changed += OnThemeChanged;
        FormClosed += (_, _) => Theme.Changed -= OnThemeChanged;
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

    private static void AddCheck(Control parent, CheckBox box, string text, string tip)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Font = Theme.UiFont;
        box.ForeColor = Theme.Text;
        box.BackColor = Color.Transparent;
        box.Margin = new Padding(0, Theme.Sy(7), 0, Theme.Sy(7));
        var holder = new ToolTip { InitialDelay = 400, AutoPopDelay = 12000 };
        holder.SetToolTip(box, tip);
        box.Disposed += (_, _) => holder.Dispose();
        parent.Controls.Add(box);
    }

    private void Confirm()
    {
        if (!_bookmarks.Checked && !_history.Checked && !_passwords.Checked)
        {
            MessageBox.Show(this, "请至少选择一类要导入的数据。", "从夸克导入",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_passwords.Checked)
        {
            DialogResult answer = MessageBox.Show(this,
                "导入密码需要先解密夸克保存的密码。\r\n\r\n" +
                "解密只在本机、本次操作中进行，解密结果会立刻用你的 Windows 账户重新加密，" +
                "不会写出任何明文文件。\r\n\r\n是否继续？",
                "导入密码", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
