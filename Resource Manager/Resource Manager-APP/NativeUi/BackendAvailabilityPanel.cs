namespace ResourceManager.NativeUi;

internal sealed class BackendAvailabilityPanel : UserControl
{
    private readonly Label stateLabel;
    private readonly Label detailLabel;
    private readonly Button retryButton;

    public BackendAvailabilityPanel()
    {
        AccessibleName = "本地服务状态";
        AccessibleRole = AccessibleRole.Pane;
        BackColor = SystemColors.Window;
        Dock = DockStyle.Fill;
        Padding = new Padding(48);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        stateLabel = new Label
        {
            AutoSize = true,
            Font = new Font(
                SystemFonts.MessageBoxFont?.FontFamily ?? DefaultFont.FontFamily,
                18,
                FontStyle.Bold),
            ForeColor = SystemColors.WindowText,
            Margin = new Padding(0, 24, 0, 12),
            Text = "正在连接本地服务"
        };
        detailLabel = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.WindowText,
            Margin = new Padding(0, 0, 0, 24),
            Text = "正在验证服务身份并建立会话。"
        };

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            WrapContents = true
        };
        retryButton = CreateButton("重试", "立即重新连接本地服务");
        var diagnosticsButton = CreateButton("打开诊断目录", "打开本地诊断文件目录");
        var exitButton = CreateButton("退出", "退出资源管理器");
        retryButton.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        diagnosticsButton.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        exitButton.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.AddRange([retryButton, diagnosticsButton, exitButton]);

        layout.Controls.Add(stateLabel, 0, 0);
        layout.Controls.Add(detailLabel, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        Resize += (_, _) => UpdateTextWidth();
        UpdateTextWidth();
    }

    public event EventHandler? RetryRequested;

    public event EventHandler? DiagnosticsRequested;

    public event EventHandler? ExitRequested;

    public void ShowConnecting(bool reconnecting)
    {
        stateLabel.Text = reconnecting
            ? "正在重新连接本地服务"
            : "正在连接本地服务";
        detailLabel.Text = "正在验证服务身份并建立会话。";
        retryButton.Enabled = false;
        Visible = true;
        BringToFront();
    }

    public void ShowUnavailable(string detail)
    {
        stateLabel.Text = "本地服务暂时不可用";
        detailLabel.Text = string.IsNullOrWhiteSpace(detail)
            ? "资源管理器会在后台继续尝试连接。"
            : $"{detail}\r\n资源管理器会在后台继续尝试连接。";
        retryButton.Enabled = true;
        Visible = true;
        BringToFront();
    }

    public void ShowFrontendLoading()
    {
        stateLabel.Text = "正在加载界面";
        detailLabel.Text = "本地服务已通过身份验证，正在加载应用界面。";
        retryButton.Enabled = false;
        Visible = true;
        BringToFront();
    }

    public void ShowFrontendUnavailable(string detail)
    {
        stateLabel.Text = "应用界面暂时不可用";
        detailLabel.Text = string.IsNullOrWhiteSpace(detail)
            ? "本地服务仍在运行，可以重新加载界面。"
            : $"{detail}\r\n本地服务仍在运行，可以重新加载界面。";
        retryButton.Enabled = true;
        Visible = true;
        BringToFront();
    }

    private static Button CreateButton(string text, string accessibleDescription) =>
        new()
        {
            AutoSize = true,
            AccessibleName = text,
            AccessibleDescription = accessibleDescription,
            Margin = new Padding(0, 0, 12, 8),
            Padding = new Padding(12, 6, 12, 6),
            Text = text,
            UseVisualStyleBackColor = true
        };

    private void UpdateTextWidth()
    {
        var width = Math.Max(240, ClientSize.Width - Padding.Horizontal);
        stateLabel.MaximumSize = new Size(width, 0);
        detailLabel.MaximumSize = new Size(width, 0);
    }
}
