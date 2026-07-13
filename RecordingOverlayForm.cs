using System.Drawing.Drawing2D;

namespace ChatGptDictationBridge;

internal sealed class RecordingOverlayForm : Form
{
    internal static readonly Size LogicalSize = new(216, 48);

    private static readonly Size DesignSize = new(248, 56);
    private const float CompactContentScale = 48f / 56f;

    private const int WsExTopMost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int MaNoActivate = 3;

    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly System.Windows.Forms.Timer _errorTimer;
    private readonly int _bottomOffsetPx;
    private RecordingOverlayPlacement? _placement;
    private string _toggleHotkey;
    private string _transientError = string.Empty;
    private string _operationTitle = string.Empty;
    private string _operationHint = string.Empty;
    private AppStatus _status = AppStatus.Idle;
    private Point _dragStartCursor;
    private Point _dragStartLocation;
    private bool _mouseDown;
    private bool _dragging;
    private bool _hovered;
    private bool _interactionEnabled = true;
    private bool _positionInitialized;
    private int _animationFrame;
    private IntPtr? _lastForegroundWindow;

    public RecordingOverlayForm(
        int bottomOffsetPx,
        string toggleHotkey,
        RecordingOverlayPlacement? placement = null)
    {
        _bottomOffsetPx = Math.Max(bottomOffsetPx, 20);
        _toggleHotkey = toggleHotkey;
        _placement = placement;

        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(24, 24, 27);
        ClientSize = ScaleLogicalSize(DeviceDpi);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Cursor = Cursors.Hand;

        _animationTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _animationTimer.Tick += (_, _) =>
        {
            _animationFrame = (_animationFrame + 1) % 120;
            MaintainTopMost(checkForOcclusion: _animationFrame % 3 == 0);
            Invalidate();
        };

        _errorTimer = new System.Windows.Forms.Timer { Interval = 4500 };
        _errorTimer.Tick += (_, _) =>
        {
            _errorTimer.Stop();
            _transientError = string.Empty;
            Invalidate();
        };
    }

    public event EventHandler? ToggleRequested;

    public event EventHandler<RecordingOverlayPlacementEventArgs>? PlacementCommitted;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTopMost | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    public void SetToggleHotkey(string hotkey)
    {
        _toggleHotkey = hotkey;
        Invalidate();
    }

    public void SetInteractionEnabled(bool enabled)
    {
        _interactionEnabled = enabled;
        Cursor = enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    public void ShowStatus(AppStatus status)
    {
        _status = status;
        _operationTitle = string.Empty;
        _operationHint = string.Empty;
        EnsurePosition();
        UpdateRoundedRegion();
        if (!Visible)
        {
            Show();
        }

        MaintainTopMost(force: true);
        _animationTimer.Start();
        Invalidate();
    }

    public void ShowTransientError(string message)
    {
        _transientError = CollapseWhitespace(message);
        _errorTimer.Stop();
        _errorTimer.Start();
        if (!Visible)
        {
            ShowStatus(_status);
        }
        else
        {
            MaintainTopMost(force: true);
            Invalidate();
        }
    }

    public void ShowOperationProgress(string title, string hint)
    {
        _operationTitle = CollapseWhitespace(title);
        _operationHint = CollapseWhitespace(hint);
        if (!Visible)
        {
            EnsurePosition();
            UpdateRoundedRegion();
            Show();
        }

        MaintainTopMost(force: true);
        _animationTimer.Start();
        Invalidate();
    }

    public void ClearOperationProgress()
    {
        _operationTitle = string.Empty;
        _operationHint = string.Empty;
        Invalidate();
    }

    public void HideOverlay()
    {
        _animationTimer.Stop();
        _errorTimer.Stop();
        _transientError = string.Empty;
        _operationTitle = string.Empty;
        _operationHint = string.Empty;
        if (Visible)
        {
            Hide();
        }

        _lastForegroundWindow = null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _lastForegroundWindow = null;
        ApplyDpiSizeAndPlacement();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MaintainTopMost(force: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpiSizeAndPlacement();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _interactionEnabled)
        {
            _mouseDown = true;
            _dragging = false;
            _dragStartCursor = Cursor.Position;
            _dragStartLocation = Location;
            Capture = true;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mouseDown && (Control.MouseButtons & MouseButtons.Left) != 0)
        {
            var cursor = Cursor.Position;
            var delta = new Size(
                cursor.X - _dragStartCursor.X,
                cursor.Y - _dragStartCursor.Y);
            if (!_dragging && HasExceededDragThreshold(delta))
            {
                _dragging = true;
                Cursor = Cursors.SizeAll;
            }

            if (_dragging)
            {
                Location = _dragStartLocation + delta;
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _mouseDown)
        {
            var wasDragging = _dragging;
            _mouseDown = false;
            _dragging = false;
            Capture = false;
            Cursor = _interactionEnabled ? Cursors.Hand : Cursors.Default;

            if (wasDragging)
            {
                CommitCurrentPlacement();
            }
            else if (_interactionEnabled)
            {
                ToggleRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture && _mouseDown)
        {
            var shouldCommitPlacement =
                _dragging &&
                !Disposing &&
                !IsDisposed &&
                Visible;
            _mouseDown = false;
            _dragging = false;
            Cursor = _interactionEnabled ? Cursors.Hand : Cursors.Default;
            if (shouldCommitPlacement)
            {
                CommitCurrentPlacement();
            }
        }

        base.OnMouseCaptureChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = GetScale();
        e.Graphics.ScaleTransform(scale, scale);

        var logicalBounds = new RectangleF(0.5f, 0.5f, LogicalSize.Width - 1, LogicalSize.Height - 1);
        using var backgroundPath = CreateRoundedRectangle(logicalBounds, 15);
        var backgroundAlpha = _hovered ? 252 : 245;
        using var backgroundBrush = new SolidBrush(Color.FromArgb(backgroundAlpha, 24, 24, 27));
        using var borderPen = new Pen(
            _hovered
                ? Color.FromArgb(112, 255, 255, 255)
                : Color.FromArgb(72, 255, 255, 255),
            1);
        e.Graphics.FillPath(backgroundBrush, backgroundPath);
        e.Graphics.DrawPath(borderPen, backgroundPath);

        var contentState = e.Graphics.Save();
        var horizontalInset =
            (LogicalSize.Width - DesignSize.Width * CompactContentScale) / 2f;
        e.Graphics.TranslateTransform(horizontalInset, 0);
        e.Graphics.ScaleTransform(CompactContentScale, CompactContentScale);
        DrawStatusIndicator(e.Graphics);
        DrawText(e.Graphics);
        DrawDragHandle(e.Graphics);
        e.Graphics.Restore(contentState);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate)
        {
            message.Result = (IntPtr)MaNoActivate;
            return;
        }

        base.WndProc(ref message);
        if (message.Msg is WmDisplayChange or WmSettingChange &&
            IsHandleCreated &&
            !IsDisposed)
        {
            BeginInvoke(ApplyDpiSizeAndPlacement);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            _errorTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawText(Graphics graphics)
    {
        var (title, hint) = GetText();
        using var titleFont = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using var hintFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.White);
        using var hintBrush = new SolidBrush(Color.FromArgb(185, 228, 228, 231));
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        graphics.DrawString(title, titleFont, titleBrush, new RectangleF(62, 8, 159, 19), format);
        graphics.DrawString(hint, hintFont, hintBrush, new RectangleF(63, 30, 158, 16), format);
    }

    private void DrawStatusIndicator(Graphics graphics)
    {
        if (_transientError.Length > 0)
        {
            using var errorBrush = new SolidBrush(Color.FromArgb(58, 248, 70, 80));
            using var errorPen = new Pen(Color.FromArgb(255, 248, 70, 80), 2.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.FillEllipse(errorBrush, 14, 10, 38, 38);
            graphics.DrawEllipse(errorPen, 23, 17, 20, 20);
            graphics.DrawLine(errorPen, 33, 22, 33, 28);
            graphics.DrawLine(errorPen, 33, 32, 33, 32.2f);
            return;
        }

        if (_status == AppStatus.Idle)
        {
            var pulse = (float)(0.5 + 0.5 * Math.Sin(_animationFrame * Math.PI / 18));
            using var pulseBrush = new SolidBrush(Color.FromArgb(22 + (int)(pulse * 24), 96, 165, 250));
            using var microphonePen = new Pen(Color.FromArgb(255, 96, 165, 250), 2.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.FillEllipse(pulseBrush, 14, 10, 38, 38);
            graphics.DrawArc(microphonePen, 27, 17, 12, 17, 0, 180);
            graphics.DrawLine(microphonePen, 27, 25, 27, 27);
            graphics.DrawLine(microphonePen, 39, 25, 39, 27);
            graphics.DrawArc(microphonePen, 24, 21, 18, 14, 0, 180);
            graphics.DrawLine(microphonePen, 33, 35, 33, 39);
            graphics.DrawLine(microphonePen, 29, 39, 37, 39);
            return;
        }

        if (_status == AppStatus.Recording)
        {
            var pulse = (float)(0.5 + 0.5 * Math.Sin(_animationFrame * Math.PI / 10));
            using var pulseBrush = new SolidBrush(Color.FromArgb(45 + (int)(pulse * 45), 248, 70, 80));
            graphics.FillEllipse(pulseBrush, 14, 10, 38, 38);

            var heights = new[] { 10, 18, 25, 18, 10 };
            for (var index = 0; index < heights.Length; index++)
            {
                var wave = Math.Sin((_animationFrame + index * 2) * Math.PI / 8);
                var height = Math.Max(5, heights[index] + (int)(wave * 5));
                var x = 21 + index * 6;
                var y = 28 - height / 2;
                using var barBrush = new SolidBrush(Color.FromArgb(255, 248, 70, 80));
                graphics.FillRoundedRectangle(barBrush, new RectangleF(x, y, 3, height), 1.5f);
            }

            return;
        }

        if (_status == AppStatus.Pasting)
        {
            using var successBrush = new SolidBrush(Color.FromArgb(45, 74, 222, 128));
            using var successPen = new Pen(Color.FromArgb(255, 74, 222, 128), 3)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.FillEllipse(successBrush, 14, 10, 38, 38);
            graphics.DrawLines(successPen, [new PointF(24, 28), new PointF(30, 34), new PointF(42, 21)]);
            return;
        }

        using var spinnerPen = new Pen(Color.FromArgb(255, 96, 165, 250), 3.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(spinnerPen, 22, 17, 22, 22, _animationFrame * 12, 245);
    }

    private void DrawDragHandle(Graphics graphics)
    {
        using var brush = new SolidBrush(Color.FromArgb(
            _interactionEnabled ? 115 : 65,
            228,
            228,
            231));
        for (var row = 0; row < 3; row++)
        {
            graphics.FillEllipse(brush, 231, 20 + row * 6, 2.3f, 2.3f);
            graphics.FillEllipse(brush, 236, 20 + row * 6, 2.3f, 2.3f);
        }
    }

    private (string Title, string Hint) GetText()
    {
        if (_transientError.Length > 0)
        {
            return ("Diktierung fehlgeschlagen", _transientError);
        }

        if (_operationTitle.Length > 0)
        {
            return (_operationTitle, _operationHint);
        }

        return _status switch
        {
            AppStatus.Idle => ("Bereit zum Diktieren", $"{_toggleHotkey} oder klicken"),
            AppStatus.Starting => ("Diktierung startet", "Einen Moment …"),
            AppStatus.Recording => ("Hört zu …", $"{_toggleHotkey} / Klick: Stopp · Esc: Abbruch"),
            AppStatus.Stopping => ("Aufnahme wird beendet", "Audio wird verarbeitet …"),
            AppStatus.ReadingText => ("Text wird transkribiert", "Bitte kurz warten …"),
            AppStatus.Pasting => ("Text wird eingefügt", "Einen Moment …"),
            _ => ("Bereit zum Diktieren", $"{_toggleHotkey} oder klicken")
        };
    }

    private void EnsurePosition()
    {
        if (_positionInitialized)
        {
            return;
        }

        var screen = ResolvePlacementScreen();
        Location = _placement is null
            ? RecordingOverlayPlacementCalculator.GetDefaultLocation(
                screen.WorkingArea,
                Size,
                ScaleValue(_bottomOffsetPx))
            : RecordingOverlayPlacementCalculator.Restore(
                screen.WorkingArea,
                Size,
                _placement.RelativeX,
                _placement.RelativeY);
        _positionInitialized = true;
    }

    private void ApplyDpiSizeAndPlacement()
    {
        var newSize = ScaleLogicalSize(DeviceDpi);
        if (ClientSize != newSize)
        {
            ClientSize = newSize;
        }

        if (_dragging)
        {
            // A PerMonitorV2 DPI change can happen while crossing a monitor
            // boundary. Keep following the pointer instead of restoring the
            // placement from the monitor where the drag began.
            _dragStartCursor = Cursor.Position;
            _dragStartLocation = Location;
            UpdateRoundedRegion();
            Invalidate();
            return;
        }

        if (_placement is not null)
        {
            var screen = ResolvePlacementScreen();
            Location = RecordingOverlayPlacementCalculator.Restore(
                screen.WorkingArea,
                Size,
                _placement.RelativeX,
                _placement.RelativeY);
            _positionInitialized = true;
        }
        else if (_positionInitialized)
        {
            var screen = Screen.FromRectangle(Bounds);
            Location = RecordingOverlayPlacementCalculator.Clamp(
                Location,
                Size,
                screen.WorkingArea);
        }

        UpdateRoundedRegion();
        Invalidate();
    }

    private void CommitCurrentPlacement()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        Location = RecordingOverlayPlacementCalculator.Clamp(
            Location,
            Size,
            screen.WorkingArea);
        _placement = RecordingOverlayPlacementCalculator.Capture(
            screen.DeviceName,
            screen.WorkingArea,
            Bounds);
        PlacementCommitted?.Invoke(
            this,
            new RecordingOverlayPlacementEventArgs(_placement));
    }

    private void MaintainTopMost(
        bool force = false,
        bool checkForOcclusion = false)
    {
        if (!Visible || !IsHandleCreated || IsDisposed || Disposing)
        {
            return;
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (!force &&
            foregroundWindow == _lastForegroundWindow &&
            (!checkForOcclusion ||
             !NativeMethods.IsWindowCoveredAtProbePoints(Handle)))
        {
            return;
        }

        _lastForegroundWindow = NativeMethods.ReassertWindowTopMost(Handle)
            ? foregroundWindow
            : null;
    }

    private Screen ResolvePlacementScreen()
    {
        if (_placement is not null)
        {
            var configuredScreen = Screen.AllScreens.FirstOrDefault(screen =>
                screen.DeviceName.Equals(
                    _placement.MonitorDeviceName,
                    StringComparison.OrdinalIgnoreCase));
            if (configuredScreen is not null)
            {
                return configuredScreen;
            }
        }

        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = CreateRoundedRectangle(
            new RectangleF(0, 0, Width, Height),
            ScaleValue(15));
        Region?.Dispose();
        Region = new Region(path);
    }

    private bool HasExceededDragThreshold(Size delta)
    {
        var threshold = SystemInformation.DragSize;
        return Math.Abs(delta.Width) >= Math.Max(threshold.Width / 2, 2) ||
               Math.Abs(delta.Height) >= Math.Max(threshold.Height / 2, 2);
    }

    private float GetScale() => Math.Max(DeviceDpi, 96) / 96f;

    private int ScaleValue(int logicalValue) =>
        (int)Math.Round(logicalValue * GetScale(), MidpointRounding.AwayFromZero);

    private static Size ScaleLogicalSize(int dpi)
    {
        var scale = Math.Max(dpi, 96) / 96d;
        return new Size(
            (int)Math.Round(LogicalSize.Width * scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(LogicalSize.Height * scale, MidpointRounding.AwayFromZero));
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string CollapseWhitespace(string message) =>
        string.Join(' ', message.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries));
}

internal sealed record RecordingOverlayPlacement(
    string MonitorDeviceName,
    double RelativeX,
    double RelativeY);

internal sealed class RecordingOverlayPlacementEventArgs : EventArgs
{
    public RecordingOverlayPlacementEventArgs(RecordingOverlayPlacement placement)
    {
        Placement = placement;
    }

    public RecordingOverlayPlacement Placement { get; }
}

internal static class RecordingOverlayPlacementCalculator
{
    public static Point GetDefaultLocation(
        Rectangle workingArea,
        Size overlaySize,
        int bottomOffsetPx)
    {
        return Clamp(
            new Point(
                workingArea.Left + (workingArea.Width - overlaySize.Width) / 2,
                workingArea.Bottom - overlaySize.Height - Math.Max(bottomOffsetPx, 0)),
            overlaySize,
            workingArea);
    }

    public static Point Restore(
        Rectangle workingArea,
        Size overlaySize,
        double relativeX,
        double relativeY)
    {
        var movableWidth = Math.Max(workingArea.Width - overlaySize.Width, 0);
        var movableHeight = Math.Max(workingArea.Height - overlaySize.Height, 0);
        var x = workingArea.Left + (int)Math.Round(
            movableWidth * Normalize(relativeX),
            MidpointRounding.AwayFromZero);
        var y = workingArea.Top + (int)Math.Round(
            movableHeight * Normalize(relativeY),
            MidpointRounding.AwayFromZero);
        return Clamp(new Point(x, y), overlaySize, workingArea);
    }

    public static Point Clamp(
        Point location,
        Size overlaySize,
        Rectangle workingArea)
    {
        var maximumX = Math.Max(workingArea.Left, workingArea.Right - overlaySize.Width);
        var maximumY = Math.Max(workingArea.Top, workingArea.Bottom - overlaySize.Height);
        return new Point(
            Math.Clamp(location.X, workingArea.Left, maximumX),
            Math.Clamp(location.Y, workingArea.Top, maximumY));
    }

    public static RecordingOverlayPlacement Capture(
        string monitorDeviceName,
        Rectangle workingArea,
        Rectangle overlayBounds)
    {
        var clamped = Clamp(overlayBounds.Location, overlayBounds.Size, workingArea);
        var movableWidth = Math.Max(workingArea.Width - overlayBounds.Width, 0);
        var movableHeight = Math.Max(workingArea.Height - overlayBounds.Height, 0);
        var relativeX = movableWidth == 0
            ? 0
            : (clamped.X - workingArea.Left) / (double)movableWidth;
        var relativeY = movableHeight == 0
            ? 0
            : (clamped.Y - workingArea.Top) / (double)movableHeight;
        return new RecordingOverlayPlacement(
            monitorDeviceName,
            Normalize(relativeX),
            Normalize(relativeY));
    }

    private static double Normalize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? 0
            : Math.Clamp(value, 0, 1);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        RectangleF rectangle,
        float radius)
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
