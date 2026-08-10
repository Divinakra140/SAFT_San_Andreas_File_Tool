using SAFT.Core;

namespace SAFT.App;

/// <summary>
/// A small confirmation dialog with fully custom button wording (a plain WinForms MessageBox only
/// offers fixed "Yes"/"No"/"OK"/"Cancel" labels, and several of SAFT's confirmations call for
/// specific, exact button text).
///
/// Optionally shows a traffic-light status icon, so a verdict reads at a glance before any of the
/// text is: green tick for fine, amber for caution, red for serious. WinForms' built-in MessageBox
/// icons have no "all good" option — the closest is a blue information "i", which looks identical
/// whether the news is good or bad.
/// </summary>
public sealed class ConfirmDialog : Form
{
    private const int PreferredContentWidth = 440;
    private const int WideContentWidth = 720;
    private const int DialogMargin = 20;
    private const int IconSize = 32;

    public bool Result { get; private set; }

    /// <summary>Two-button confirmation.</summary>
    public ConfirmDialog(string message, string yesText, string noText, StreamingSeverity? severity = null)
        : this(message, yesText, noText, severity, singleButton: false) { }

    /// <summary>Single-button acknowledgement, for news that isn't a decision.</summary>
    public static ConfirmDialog Acknowledgement(string message, string okText, StreamingSeverity severity) =>
        new(message, okText, string.Empty, severity, singleButton: true);

    private ConfirmDialog(string message, string yesText, string noText, StreamingSeverity? severity, bool singleButton)
    {
        // Building a window is the step most likely to fault inside an emulated Windows rather than
        // throw something catchable, and a dialog that never appeared is exactly what a user sees as
        // "it crashed before the popup". Logged on the way in so that case leaves a trace.
        ActivityLog.Note($"dialog: building - \"{Shorten(message)}\"");

        Text = "SAFT";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        // Winlator's recommended resolution is 960x544 — a screen far shorter than a desktop's, where
        // height is the scarce resource and width is not. So the dialog spends width to buy height:
        // wrapping the same text at 720px instead of 440px removes roughly a third of the lines, and
        // that is usually the difference between reading the message and scrolling it.
        // Guarded because this is the first thing the dialog does and it asks the OS about screens
        // and cursors — exactly the surface most likely to behave differently under an emulated
        // Windows. A dialog that opens at a guessed size beats one that takes the app down.
        Rectangle screen;
        try
        {
            screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (screen.Width < 320 || screen.Height < 200) screen = new Rectangle(0, 0, 960, 544);
        }
        catch
        {
            screen = new Rectangle(0, 0, 960, 544);   // Winlator's recommended resolution
        }

        var maxClientHeight = Math.Max(200, screen.Height - 70);
        var maxClientWidth = Math.Max(320, screen.Width - 40);

        var buttons = new List<Button> { MakeButton(yesText, true) };
        if (!singleButton) buttons.Add(MakeButton(noText, false));

        const int spacing = 8;
        var buttonsWidth = buttons.Sum(b => b.Width) + spacing * (buttons.Count - 1);

        // Short messages keep the narrower, easier-to-read measure; only long ones spread out.
        var roomy = Math.Min(WideContentWidth, maxClientWidth - DialogMargin * 2);
        var wantsWidth = message.Length > 320 || message.Count(c => c == '\n') >= 4;
        var preferred = wantsWidth ? Math.Max(PreferredContentWidth, roomy) : PreferredContentWidth;

        // Buttons are sized to their own text rather than capped at a fixed width, since a cap
        // silently clipped longer labels mid-word. On a narrow screen they stack instead of being
        // squeezed, which keeps every label readable.
        var contentWidth = Math.Min(Math.Max(preferred, buttonsWidth), maxClientWidth - DialogMargin * 2);
        var stackButtons = buttonsWidth > contentWidth;

        var textLeft = severity is null ? DialogMargin : DialogMargin + IconSize + 12;
        var textWidth = contentWidth - (textLeft - DialogMargin);

        // Line breaks are normalised to CRLF. A bare "\n" is fine in a Label on Windows, but under
        // Wine a long message with bare newlines rendered as the start of the text followed by
        // whatever happened to sit next to it in memory and a run of missing-glyph boxes. Windows
        // text controls want CRLF, so give them CRLF.
        var text = message.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        // A read-only TextBox rather than a Label: it is built for long, wrapped, scrollable text,
        // where a Label is a thin wrapper over a static control and behaves badly under Wine once
        // the string gets long. Chrome removed so it still reads as plain dialog text.
        var messageBox = new TextBox
        {
            Text = text,
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.ControlText,
            TabStop = false,
            Left = textLeft,
            Top = DialogMargin,
            Width = textWidth,
        };

        var textHeight = TextRenderer.MeasureText(
            text, messageBox.Font, new Size(textWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 8;

        var buttonHeight = buttons[0].Height;
        var buttonBlockHeight = stackButtons
            ? buttons.Count * buttonHeight + (buttons.Count - 1) * spacing
            : buttonHeight;

        var chromeHeight = DialogMargin * 2 + 16 + buttonBlockHeight;
        var availableForText = maxClientHeight - chromeHeight;
        var visibleTextHeight = Math.Min(textHeight, Math.Max(60, availableForText));

        // Only scrolls when it genuinely cannot fit; the extra width usually means it doesn't.
        if (textHeight > visibleTextHeight) messageBox.ScrollBars = ScrollBars.Vertical;
        messageBox.Height = visibleTextHeight;
        Controls.Add(messageBox);

        // Selection would otherwise be visible on open, which looks like a text field rather than a
        // message; deselecting on first show keeps it looking like prose.
        Shown += (_, _) => { messageBox.SelectionLength = 0; buttons[0].Focus(); };

        if (severity is { } level)
        {
            Controls.Add(new PictureBox
            {
                Left = DialogMargin,
                Top = DialogMargin,
                Width = IconSize,
                Height = IconSize,
                Image = DrawStatusIcon(level),
                SizeMode = PictureBoxSizeMode.Normal,
            });
        }

        var buttonTop = messageBox.Top + messageBox.Height + 16;
        if (stackButtons)
        {
            var y = buttonTop;
            foreach (var button in buttons)
            {
                button.Left = DialogMargin + contentWidth - button.Width;
                button.Top = y;
                y += buttonHeight + spacing;
                Controls.Add(button);
            }
        }
        else
        {
            var x = DialogMargin + contentWidth;
            foreach (var button in buttons)
            {
                x -= button.Width;
                button.Left = x;
                button.Top = buttonTop;
                x -= spacing;
                Controls.Add(button);
            }
        }

        ClientSize = new Size(
            contentWidth + DialogMargin * 2,
            buttonTop + buttonBlockHeight + DialogMargin);
    }

    private Button MakeButton(string text, bool isYes)
    {
        var button = new Button { Text = text, Height = 30, AutoSize = false };
        var textSize = TextRenderer.MeasureText(text, button.Font);
        button.Width = textSize.Width + 28;
        button.Click += (_, _) => { Result = isYes; Close(); };
        return button;
    }

    /// <summary>Enough of the message to tell two dialogs apart in the log, and no more.</summary>
    private static string Shorten(string message)
    {
        var flat = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 60 ? flat : flat[..60] + "...";
    }

    /// <summary>Records that the window really did appear, which is the step before it can be read.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActivityLog.Note("dialog: shown");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        ActivityLog.Note($"dialog: closed, answer {(Result ? "yes" : "no")}");
    }

    /// <summary>
    /// Draws a filled circle with a symbol: a tick for fine, "i" for caution, "!" for serious.
    ///
    /// The symbols are drawn as SHAPES rather than as text. A tick used to be the character U+2713,
    /// which rendered as an empty box under Winlator because the font Wine falls back to has no
    /// glyph for it. Lines and dots have no such dependency and look identical everywhere.
    /// </summary>
    private static Bitmap DrawStatusIcon(StreamingSeverity severity)
    {
        var fill = severity switch
        {
            StreamingSeverity.Fine => Color.FromArgb(30, 140, 60),
            StreamingSeverity.Caution => Color.FromArgb(210, 160, 20),
            _ => Color.FromArgb(190, 40, 40),
        };

        var bitmap = new Bitmap(IconSize, IconSize);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using (var brush = new SolidBrush(fill))
            g.FillEllipse(brush, 1, 1, IconSize - 3, IconSize - 3);

        // Everything below is in fractions of the icon, so the shapes stay centred at any size.
        float S(double f) => (float)(f * IconSize);
        using var pen = new Pen(Color.White, Math.Max(2f, IconSize / 9f))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };

        if (severity == StreamingSeverity.Fine)
        {
            g.DrawLines(pen, new[]
            {
                new PointF(S(0.27), S(0.52)),
                new PointF(S(0.43), S(0.68)),
                new PointF(S(0.74), S(0.34)),
            });
            return bitmap;
        }

        // "!" is a bar above a dot; "i" is the same thing upside down.
        var dotRadius = Math.Max(1.5f, IconSize / 12f);
        var (barTop, barBottom, dotY) = severity == StreamingSeverity.Serious
            ? (0.24, 0.56, 0.74)
            : (0.42, 0.74, 0.26);

        g.DrawLine(pen, S(0.5), S(barTop), S(0.5), S(barBottom));
        g.FillEllipse(Brushes.White, S(0.5) - dotRadius, S(dotY) - dotRadius, dotRadius * 2, dotRadius * 2);

        return bitmap;
    }
}
