using ResourceManager.NativeUi.Localization;

namespace ResourceManager.NativeUi;

internal sealed class BackendAvailabilityPanel : UserControl
{
    private readonly Label stateLabel;
    private readonly Label detailLabel;
    private readonly Button retryButton;
    private readonly Button diagnosticsButton;
    private readonly Button exitButton;

    public BackendAvailabilityPanel()
    {
        AccessibleName = NativeUiText.Current.AvailabilityPanelName;
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
            Text = NativeUiText.Current.ConnectingTitle
        };
        detailLabel = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.WindowText,
            Margin = new Padding(0, 0, 0, 24),
            Text = NativeUiText.Current.ConnectingDetail
        };

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            WrapContents = true
        };
        var text = NativeUiText.Current;
        retryButton = CreateButton(text.RetryButton, text.RetryButtonDescription);
        diagnosticsButton = CreateButton(text.DiagnosticsButton, text.DiagnosticsButtonDescription);
        exitButton = CreateButton(text.ExitButton, text.ExitButtonDescription);
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

    /// <summary>语言切换后重新取一遍常驻文案；随状态变化的文案由 Show* 在调用时取。</summary>
    public void ApplyText()
    {
        var text = NativeUiText.Current;
        AccessibleName = text.AvailabilityPanelName;
        ApplyButtonText(retryButton, text.RetryButton, text.RetryButtonDescription);
        ApplyButtonText(diagnosticsButton, text.DiagnosticsButton, text.DiagnosticsButtonDescription);
        ApplyButtonText(exitButton, text.ExitButton, text.ExitButtonDescription);
    }

    public void ShowConnecting(bool reconnecting)
    {
        var text = NativeUiText.Current;
        stateLabel.Text = reconnecting
            ? text.ReconnectingTitle
            : text.ConnectingTitle;
        detailLabel.Text = text.ConnectingDetail;
        retryButton.Enabled = false;
        Visible = true;
        BringToFront();
    }

    public void ShowUnavailable(string detail)
    {
        var text = NativeUiText.Current;
        stateLabel.Text = text.BackendUnavailableTitle;
        detailLabel.Text = string.IsNullOrWhiteSpace(detail)
            ? text.BackendUnavailableDetail
            : $"{detail}\r\n{text.BackendUnavailableDetail}";
        retryButton.Enabled = true;
        Visible = true;
        BringToFront();
    }

    public void ShowFrontendLoading()
    {
        var text = NativeUiText.Current;
        stateLabel.Text = text.FrontendLoadingTitle;
        detailLabel.Text = text.FrontendLoadingDetail;
        retryButton.Enabled = false;
        Visible = true;
        BringToFront();
    }

    public void ShowFrontendUnavailable(string detail)
    {
        var text = NativeUiText.Current;
        stateLabel.Text = text.FrontendUnavailableTitle;
        detailLabel.Text = string.IsNullOrWhiteSpace(detail)
            ? text.FrontendUnavailableDetail
            : $"{detail}\r\n{text.FrontendUnavailableDetail}";
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

    private static void ApplyButtonText(Button button, string text, string accessibleDescription)
    {
        button.AccessibleName = text;
        button.AccessibleDescription = accessibleDescription;
        button.Text = text;
    }

    private void UpdateTextWidth()
    {
        var width = Math.Max(240, ClientSize.Width - Padding.Horizontal);
        stateLabel.MaximumSize = new Size(width, 0);
        detailLabel.MaximumSize = new Size(width, 0);
    }
}
