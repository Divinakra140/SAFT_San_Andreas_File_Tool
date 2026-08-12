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

    /// <summary>
    /// The status icon, held so it can be released. A PictureBox does NOT own the image it is given -
    /// disposing the control leaves the bitmap alive - so without this every dialog that showed a
    /// verdict left a GDI bitmap behind for the rest of the session. SAFT is a 32-bit process running
    /// through Wine, where GDI objects are a scarcer resource than memory.
    /// </summary>
    private Bitmap? _statusIcon;

    /// <summary>Winlator's recommended resolution, and the answer if the OS won't give a better one.</summary>
    private static readonly Rectangle AssumedScreen = new(0, 0, 960, 544);

    /// <summary>
    /// The usable screen, asked for ONCE per run and remembered.
    ///
    /// This is where a crash happened. A run died four milliseconds into this constructor, after the
    /// "building" line and before anything else it writes — and what sat in that gap was a question to
    /// the OS about the cursor's position and which monitor it was on, asked afresh every single time
    /// a dialog opened. It was already wrapped in a try/catch and already carried a comment calling it
    /// the surface most likely to behave differently under an emulated Windows; what a try/catch
    /// cannot do is survive a native fault inside Wine's display code, which is precisely the failure
    /// this app has: the process dies without the CLR ever seeing an exception.
    ///
    /// A screen does not change size while SAFT is open, so asking once is not a cache in the risky
    /// sense — it is not holding anything that can go stale. It also drops <c>Cursor.Position</c>
    /// entirely: a cursor is a strange thing to ask about on a touchscreen, and every device SAFT runs
    /// on has one screen, so the primary one is the same answer with one less question.
    /// </summary>
    private static Rectangle? _screen;

    private static Rectangle ScreenWorkingArea()
    {
        if (_screen is { } known) return known;

        Rectangle found;
        try
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? AssumedScreen;
            found = area.Width < 320 || area.Height < 200 ? AssumedScreen : area;
        }
        catch
        {
            found = AssumedScreen;
        }

        _screen = found;
        ActivityLog.Note($"dialog: screen is {found.Width}x{found.Height}, asked once for this run");
        return found;
    }

    /// <summary>Two-button confirmation.</summary>
    public ConfirmDialog(string message, string yesText, string noText, StreamingSeverity? severity = null)
        : this(message, yesText, noText, severity, singleButton: false) { }

    /// <summary>Single-button acknowledgement, for news that isn't a decision.</summary>
    public static ConfirmDialog Acknowledgement(string message, string okText, StreamingSeverity severity) =>
        new(message, okText, string.Empty, severity, singleButton: true);

    /// <summary>
    /// Single-button note with no status icon — plain information, no verdict attached.
    ///
    /// Exists so nothing in an install has to fall back to a raw MessageBox. A MessageBox is a native
    /// Win32 dialog with a system icon, drawn by Wine rather than by SAFT, and it writes NOTHING to
    /// the activity log: the one shown after "yes, add new assets" was a 1.8 second hole in the log
    /// with a crash landing on either side of it, and no way to tell whether the process died before
    /// it, inside it, or after. This is the dialog the app already opens hundreds of times without
    /// trouble, and it says so in the log.
    /// </summary>
    public static ConfirmDialog Note(string message, string okText) =>
        new(message, okText, string.Empty, null, singleButton: true);

    /// <summary>
    /// Two buttons, where the FIRST is disabled for a fixed number of seconds and the second is live
    /// immediately. The user is held back from the thing that needs the wait, and free to do the
    /// thing that does not.
    ///
    /// Used after an install: launching the game right away is what breaks, so that button waits.
    /// Carrying on inside SAFT is harmless, so that one does not. The wait is not superstition -
    /// writing an archive finishes long before an SD card has committed it, and a game started inside
    /// that window opens a half-written archive and hangs on a black screen. Flushing to disk did not
    /// close the gap; it was measured still happening afterwards.
    /// </summary>
    public static ConfirmDialog Wait(string message, string waitingText, string skipText, int seconds) =>
        new(message, waitingText, skipText, null, singleButton: false, countdownSeconds: seconds);

    private ConfirmDialog(
        string message, string yesText, string noText, StreamingSeverity? severity, bool singleButton,
        int countdownSeconds = 0)
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

        var screen = ScreenWorkingArea();
        ActivityLog.Note("dialog: measuring");

        var maxClientHeight = Math.Max(200, screen.Height - 70);
        var maxClientWidth = Math.Max(320, screen.Width - 40);

        var buttons = new List<Button> { MakeButton(yesText, true) };
        if (!singleButton) buttons.Add(MakeButton(noText, false));

        // Only the first button waits; any second button stays live, so the user always has a way
        // out of the dialog that does not involve sitting through a countdown they do not need.
        // Done before the layout below measures anything, so the button is already at its final width.
        if (countdownSeconds > 0) StartCountdown(buttons[0], yesText, countdownSeconds);

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

        // Measured against a large but FINITE height. This asks Wine's GDI to lay the whole message
        // out and hand back the rectangle it would fill, and it used to pass int.MaxValue as the
        // height to measure into — a value no real dialog needs and the kind of extreme that native
        // layout code is least likely to have been written carefully around. 20,000 pixels is more
        // than any message here will ever want, and anything that somehow exceeded it scrolls, which
        // this dialog already handles.
        const int measurementCeiling = 20_000;
        var textHeight = TextRenderer.MeasureText(
            text, messageBox.Font, new Size(textWidth, measurementCeiling),
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
        ActivityLog.Note("dialog: text laid out");

        // Selection would otherwise be visible on open, which looks like a text field rather than a
        // message; deselecting on first show keeps it looking like prose.
        Shown += (_, _) => { messageBox.SelectionLength = 0; buttons[0].Focus(); };

        if (severity is { } level)
        {
            // The icon is decoration. It must never be the reason a dialog fails to appear, and it
            // has been exactly that: drawing the green tick took the whole process down under
            // Winlator, so the one verdict that means "your mod is fine" was the one nobody could
            // ever see. GDI+ is the least portable surface in the app and the icon is worth the
            // least, so any failure here drops the icon and keeps the message.
            // Logged either side, because a hard GDI+ fault is not catchable and leaves nothing
            // behind. Without this the log jumped straight from "building" to the next session and
            // the icon was indistinguishable from the rest of the layout as a suspect.
            ActivityLog.Note($"dialog: drawing {level} status icon");

            try
            {
                _statusIcon = DrawStatusIcon(level);
                ActivityLog.Note("dialog: status icon drawn");
            }
            catch (Exception ex)
            {
                ActivityLog.Note($"dialog: status icon could not be drawn, continuing without it - {ex.Message}");
            }

            if (_statusIcon is not null)
            {
                Controls.Add(new PictureBox
                {
                    Left = DialogMargin,
                    Top = DialogMargin,
                    Width = IconSize,
                    Height = IconSize,
                    Image = _statusIcon,
                    SizeMode = PictureBoxSizeMode.Normal,
                });
            }
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

    /// <summary>The button's caption while it is still counting down.</summary>
    private static string Counting(string readyText, int seconds) => $"{readyText}  ({seconds})";

    /// <summary>
    /// Disables a button and re-enables it once <paramref name="seconds"/> have actually elapsed.
    /// </summary>
    private void StartCountdown(Button button, string readyText, int seconds)
    {
        // Sized up front for the widest caption it will ever hold, so it does not resize under the
        // user's thumb as the number counts down from two digits to one.
        var widest = Counting(readyText, seconds);
        button.Width = TextRenderer.MeasureText(widest, button.Font).Width + 28;
        button.Text = widest;
        button.Enabled = false;

        // Driven off the wall clock, not off a count of ticks. Under an emulated Windows a timer
        // that misses ticks would otherwise hold the button disabled well past the time it promised.
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var timer = new System.Windows.Forms.Timer { Interval = 250 };

        void Release()
        {
            timer.Stop();
            button.Text = readyText;
            button.Enabled = true;
        }

        timer.Tick += (_, _) =>
        {
            var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
            if (remaining <= 0) { Release(); return; }

            var caption = Counting(readyText, remaining);
            if (button.Text != caption) button.Text = caption;
        };

        // Fail OPEN, never closed. The one way this feature could do real harm is a timer that never
        // fires, leaving the only button permanently disabled and the app needing to be killed. The
        // install has already finished by the time this dialog appears, so skipping the wait costs a
        // recoverable freeze, while a dead timer costs the user their whole session.
        try
        {
            timer.Start();
        }
        catch (Exception ex)
        {
            ActivityLog.Note($"dialog: countdown timer would not start, enabling immediately - {ex.Message}");
            Release();
        }

        FormClosed += (_, _) => { try { timer.Dispose(); } catch { /* nothing left to do about it */ } };
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
    /// Releases the status icon along with the window.
    ///
    /// ShowDialog does NOT dispose the form it shows - deliberately, since the caller still has to
    /// read <see cref="Result"/> afterwards - so every caller has to dispose it, and until now none
    /// of them did. That left a form and its bitmap alive for the whole session, once per popup.
    /// Disposing the icon here rather than in OnFormClosed keeps it valid for as long as the window
    /// it is painted in.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusIcon?.Dispose();
            _statusIcon = null;
        }
        base.Dispose(disposing);
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
            // Two separate DrawLine calls rather than one DrawLines polyline. The amber and red icons
            // below have always drawn correctly using DrawLine and FillEllipse; DrawLines was used
            // only here, only for the tick, and it is the one call in this method that had never run
            // on the target platform. It crashed the process under Winlator rather than throwing.
            // The round line caps close the corner, so the tick looks the same as it did.
            g.DrawLine(pen, S(0.27), S(0.52), S(0.43), S(0.68));
            g.DrawLine(pen, S(0.43), S(0.68), S(0.74), S(0.34));
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
