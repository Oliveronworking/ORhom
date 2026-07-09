using System.Drawing.Drawing2D;

namespace ChatGptDictationBridge;

internal sealed class RecordingOverlayForm : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly int _bottomOffsetPx;
    private string _toggleHotkey;
    private AppStatus _status = AppStatus.Idle;
    private int _animationFrame;

    public RecordingOverlayForm(int bottomOffsetPx, string toggleHotkey)
    {
        _bottomOffsetPx = Math.Max(bottomOffsetPx, 20);
        _toggleHotkey = toggleHotkey;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(24, 24, 27);
        ClientSize = new Size(248, 56);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        _animationTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _animationTimer.Tick += (_, _) =>
        {
            _animationFrame = (_animationFrame + 1) % 60;
            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    public void SetToggleHotkey(string hotkey)
    {
        _toggleHotkey = hotkey;
        Invalidate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    public void ShowStatus(AppStatus status, IntPtr targetWindow)
    {
        if (status == AppStatus.Idle)
        {
            HideOverlay();
            return;
        }

        _status = status;
        PositionNearBottom(targetWindow);
        UpdateRoundedRegion();
        if (!Visible)
        {
            Show();
        }

        _animationTimer.Start();
        Invalidate();
    }

    public void HideOverlay()
    {
        _animationTimer.Stop();
        _status = AppStatus.Idle;
        if (Visible)
        {
            Hide();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var backgroundPath = CreateRoundedRectangle(ClientRectangle, 18);
        using var backgroundBrush = new SolidBrush(Color.FromArgb(245, 24, 24, 27));
        using var borderPen = new Pen(Color.FromArgb(72, 255, 255, 255), 1);
        e.Graphics.FillPath(backgroundBrush, backgroundPath);
        e.Graphics.DrawPath(borderPen, backgroundPath);

        DrawStatusIndicator(e.Graphics);
        using var titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var hintFont = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
        using var titleBrush = new SolidBrush(Color.White);
        using var hintBrush = new SolidBrush(Color.FromArgb(180, 228, 228, 231));

        var (title, hint) = GetText();
        e.Graphics.DrawString(title, titleFont, titleBrush, 63, 9);
        e.Graphics.DrawString(hint, hintFont, hintBrush, 64, 31);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawStatusIndicator(Graphics graphics)
    {
        if (_status == AppStatus.Recording)
        {
            var pulse = (float)(0.5 + 0.5 * Math.Sin(_animationFrame * Math.PI / 10));
            var pulseAlpha = 45 + (int)(pulse * 45);
            using var pulseBrush = new SolidBrush(Color.FromArgb(pulseAlpha, 248, 70, 80));
            graphics.FillEllipse(pulseBrush, 14, 10, 38, 38);

            var heights = new[] { 10, 18, 25, 18, 10 };
            for (var index = 0; index < heights.Length; index++)
            {
                var wave = Math.Sin((_animationFrame + index * 2) * Math.PI / 8);
                var height = Math.Max(5, heights[index] + (int)(wave * 5));
                var x = 21 + index * 6;
                var y = 28 - height / 2;
                using var barBrush = new SolidBrush(Color.FromArgb(255, 248, 70, 80));
                graphics.FillRoundedRectangle(barBrush, new Rectangle(x, y, 3, height), 2);
            }

            return;
        }

        var color = _status == AppStatus.Pasting
            ? Color.FromArgb(74, 222, 128)
            : Color.FromArgb(96, 165, 250);
        using var spinnerPen = new Pen(color, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var startAngle = _animationFrame * 12;
        graphics.DrawArc(spinnerPen, 22, 17, 22, 22, startAngle, 245);
    }

    private (string Title, string Hint) GetText()
    {
        return _status switch
        {
            AppStatus.Starting => ("Diktierung startet", "Einen Moment …"),
            AppStatus.Recording => ("Hört zu …", $"{_toggleHotkey} zum Stoppen  ·  Esc Abbruch"),
            AppStatus.Stopping => ("Aufnahme wird beendet", "ChatGPT verarbeitet …"),
            AppStatus.ReadingText => ("Text wird transkribiert", "Bitte kurz warten …"),
            AppStatus.Pasting => ("Text wird eingefügt", "Fertig"),
            _ => (string.Empty, string.Empty)
        };
    }

    private void PositionNearBottom(IntPtr targetWindow)
    {
        var screen = targetWindow != IntPtr.Zero && NativeMethods.IsWindow(targetWindow)
            ? Screen.FromHandle(targetWindow)
            : Screen.PrimaryScreen;
        var workArea = screen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(
            workArea.Left + (workArea.Width - Width) / 2,
            workArea.Bottom - Height - _bottomOffsetPx);
    }

    private void UpdateRoundedRegion()
    {
        using var path = CreateRoundedRectangle(ClientRectangle, 18);
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter - 1, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter - 1, rectangle.Bottom - diameter - 1, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter - 1, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle rectangle, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
