namespace SAFT.App;

/// <summary>
/// A small Yes/No confirmation dialog with fully custom button wording (a plain WinForms
/// MessageBox only offers fixed "Yes"/"No"/"OK"/"Cancel" labels, and several of SAFT's
/// confirmations call for specific, exact button text).
/// </summary>
public sealed class ConfirmDialog : Form
{
    private const int ContentWidth = 440;
    private const int DialogMargin = 20;

    public bool Result { get; private set; }

    public ConfirmDialog(string message, string yesText, string noText)
    {
        Text = "SAFT";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        // Measured and sized BEFORE anything is added to Controls — an explicit ClientSize set
        // afterward, not Form.AutoSize, since AutoSize locks in the form's size at whatever moment
        // a child control's height happened to change, and (like the same bug already found and
        // fixed in MainForm) that moment doesn't reliably line up with when the real, computed
        // height gets applied — the form ends up sized for the label's tiny default height instead
        // of its real wrapped-text height, cutting off both the message and the buttons below it.
        var messageLabel = new Label
        {
            Text = message,
            AutoSize = false,
            Left = DialogMargin,
            Top = DialogMargin,
            Width = ContentWidth,
        };
        using (var g = CreateGraphics())
        {
            var size = g.MeasureString(message, messageLabel.Font, ContentWidth);
            messageLabel.Height = (int)Math.Ceiling(size.Height) + 4;
        }

        var yesButton = new Button { Text = yesText, AutoSize = true, MaximumSize = new Size(200, 0) };
        yesButton.Click += (_, _) => { Result = true; Close(); };

        var noButton = new Button { Text = noText, AutoSize = true, MaximumSize = new Size(200, 0) };
        noButton.Click += (_, _) => { Result = false; Close(); };

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Left = DialogMargin,
            Top = messageLabel.Top + messageLabel.Height + 16,
            Width = ContentWidth,
        };
        // FlowDirection.RightToLeft lays out left-to-right-added controls starting from the right,
        // so adding Yes first puts it rightmost — matching the original Yes-on-the-right layout.
        buttonRow.Controls.Add(yesButton);
        buttonRow.Controls.Add(noButton);

        Controls.Add(messageLabel);
        Controls.Add(buttonRow);

        ClientSize = new Size(
            ContentWidth + DialogMargin * 2,
            buttonRow.Top + buttonRow.PreferredSize.Height + DialogMargin);
    }
}
