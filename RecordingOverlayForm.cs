using System.Drawing.Drawing2D;

namespace ORhom;

internal sealed class RecordingOverlayForm : Form
{
    internal static readonly Size LogicalSize = new(300, 56);
    internal static readonly RectangleF RecordingStopActionBounds =
        new(150, 9, 104, 38);
    internal static readonly RectangleF RecordingAbortActionBounds =
        new(260, 9, 30, 38);

    private const int WsExTopMost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int MaNoActivate = 3;

    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly System.Windows.Forms.Timer _errorTimer;
    private readonly ToolTip _toolTip;
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
    private DateTime? _recordingStartedAtUtc;
    private IntPtr? _lastForegroundWindow;
    private InteractionTarget _pressedTarget;
    private InteractionTarget _hoveredTarget;
    private string _toolTipText = string.Empty;

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
        TabStop = false;
        TopMost = true;
        Cursor = Cursors.Hand;
        AccessibleName = "ORhom Diktierleiste";
        AccessibleRole = AccessibleRole.ToolBar;

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
            RefreshInteractionPresentation();
            UpdateAccessibilityText();
            Invalidate();
        };

        _toolTip = new ToolTip
        {
            AutoPopDelay = 5000,
            InitialDelay = 450,
            ReshowDelay = 100,
            ShowAlways = true
        };
        UpdateAccessibilityText();
        UpdateToolTip(InteractionTarget.Body);
    }

    public event EventHandler? ToggleRequested;

    public event EventHandler? AbortRequested;

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
        UpdateAccessibilityText();
        RefreshInteractionPresentation();
        Invalidate();
    }

    public void SetInteractionEnabled(bool enabled)
    {
        _interactionEnabled = enabled;
        RefreshInteractionPresentation();
        UpdateAccessibilityText();
        Invalidate();
    }

    public void ShowStatus(AppStatus status)
    {
        if (status == AppStatus.Recording &&
            (_status != AppStatus.Recording ||
             _recordingStartedAtUtc is null))
        {
            _recordingStartedAtUtc = DateTime.UtcNow;
        }
        else if (status != AppStatus.Recording)
        {
            _recordingStartedAtUtc = null;
        }

        _status = status;
        _operationTitle = string.Empty;
        _operationHint = string.Empty;
        _pressedTarget = InteractionTarget.None;
        RefreshInteractionPresentation();
        UpdateAccessibilityText();
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
        RefreshInteractionPresentation();
        UpdateAccessibilityText();
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
        RefreshInteractionPresentation();
        UpdateAccessibilityText();
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
        RefreshInteractionPresentation();
        UpdateAccessibilityText();
        Invalidate();
    }

    public void HideOverlay()
    {
        _animationTimer.Stop();
        _errorTimer.Stop();
        _transientError = string.Empty;
        _operationTitle = string.Empty;
        _operationHint = string.Empty;
        _recordingStartedAtUtc = null;
        _pressedTarget = InteractionTarget.None;
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
        _hoveredTarget = GetInteractionTarget(PointToClient(Cursor.Position));
        UpdateCursor(_hoveredTarget);
        UpdateToolTip(_hoveredTarget);
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        if (!_mouseDown)
        {
            _hoveredTarget = InteractionTarget.None;
            UpdateCursor(_hoveredTarget);
            UpdateToolTip(_hoveredTarget);
        }

        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _interactionEnabled)
        {
            _mouseDown = true;
            _dragging = false;
            _pressedTarget = GetInteractionTarget(e.Location);
            _hoveredTarget = _pressedTarget;
            _dragStartCursor = Cursor.Position;
            _dragStartLocation = Location;
            Capture = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var currentTarget = GetInteractionTarget(e.Location);
        if (currentTarget != _hoveredTarget)
        {
            _hoveredTarget = currentTarget;
            UpdateToolTip(currentTarget);
            Invalidate();
        }

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

        UpdateCursor(currentTarget);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _mouseDown)
        {
            var wasDragging = _dragging;
            var pressedTarget = _pressedTarget;
            var releasedTarget = GetInteractionTarget(e.Location);
            _mouseDown = false;
            _dragging = false;
            _pressedTarget = InteractionTarget.None;
            Capture = false;
            _hoveredTarget = releasedTarget;
            UpdateCursor(releasedTarget);

            if (wasDragging)
            {
                CommitCurrentPlacement();
            }
            else if (_interactionEnabled && pressedTarget == releasedTarget)
            {
                if (ShowsRecordingControls)
                {
                    if (releasedTarget == InteractionTarget.PrimaryAction)
                    {
                        ToggleRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (releasedTarget == InteractionTarget.AbortAction)
                    {
                        AbortRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (releasedTarget == InteractionTarget.Body)
                {
                    ToggleRequested?.Invoke(this, EventArgs.Empty);
                }
            }

            Invalidate();
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
            _pressedTarget = InteractionTarget.None;
            UpdateCursor(_hoveredTarget);
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
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.TextRenderingHint =
            System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var scale = GetScale();
        e.Graphics.ScaleTransform(scale, scale);

        var logicalBounds = new RectangleF(0.5f, 0.5f, LogicalSize.Width - 1, LogicalSize.Height - 1);
        using var backgroundPath = CreateRoundedRectangle(logicalBounds, 16);
        var backgroundAlpha = _hovered ? 253 : 248;
        using var backgroundBrush = new LinearGradientBrush(
            logicalBounds,
            Color.FromArgb(backgroundAlpha, 34, 35, 42),
            Color.FromArgb(backgroundAlpha, 18, 19, 23),
            LinearGradientMode.Vertical);
        using var borderPen = new Pen(
            _hovered
                ? Color.FromArgb(104, 255, 255, 255)
                : Color.FromArgb(62, 255, 255, 255),
            1);
        e.Graphics.FillPath(backgroundBrush, backgroundPath);
        e.Graphics.DrawPath(borderPen, backgroundPath);

        DrawStatusIndicator(e.Graphics);
        DrawText(e.Graphics);
        if (ShowsRecordingControls)
        {
            DrawRecordingActions(e.Graphics);
        }
        else
        {
            DrawDragHandle(e.Graphics);
        }
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
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawText(Graphics graphics)
    {
        if (ShowsRecordingControls)
        {
            using var recordingTitleFont = new Font(
                "Segoe UI",
                13,
                FontStyle.Bold,
                GraphicsUnit.Pixel);
            using var durationFont = new Font(
                "Segoe UI",
                13,
                FontStyle.Bold,
                GraphicsUnit.Pixel);
            using var recordingTitleBrush = new SolidBrush(
                Color.FromArgb(236, 250, 250, 252));
            using var durationBrush = new SolidBrush(Color.FromArgb(255, 251, 113, 133));
            using var recordingFormat = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            graphics.DrawString(
                "Aufnahme",
                recordingTitleFont,
                recordingTitleBrush,
                new RectangleF(59, 9, 84, 18),
                recordingFormat);
            graphics.DrawString(
                GetRecordingDurationText(),
                durationFont,
                durationBrush,
                new RectangleF(59, 29, 84, 18),
                recordingFormat);
            return;
        }

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

        graphics.DrawString(title, titleFont, titleBrush, new RectangleF(62, 8, 210, 19), format);
        graphics.DrawString(hint, hintFont, hintBrush, new RectangleF(63, 30, 211, 16), format);
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

        if (ShowsRecordingControls)
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
            graphics.FillEllipse(brush, 282, 20 + row * 6, 2.3f, 2.3f);
            graphics.FillEllipse(brush, 287, 20 + row * 6, 2.3f, 2.3f);
        }
    }

    private void DrawRecordingActions(Graphics graphics)
    {
        var primaryHovered =
            _interactionEnabled &&
            _hoveredTarget == InteractionTarget.PrimaryAction;
        var primaryPressed =
            primaryHovered &&
            _mouseDown &&
            _pressedTarget == InteractionTarget.PrimaryAction;
        var primaryFill = !_interactionEnabled
            ? Color.FromArgb(24, 255, 255, 255)
            : primaryPressed
                ? Color.FromArgb(70, 255, 255, 255)
                : primaryHovered
                    ? Color.FromArgb(58, 255, 255, 255)
                    : Color.FromArgb(38, 255, 255, 255);
        using var primaryBrush = new SolidBrush(primaryFill);
        using var primaryBorder = new Pen(
            primaryHovered
                ? Color.FromArgb(105, 255, 255, 255)
                : Color.FromArgb(55, 255, 255, 255));
        using var primaryPath = CreateRoundedRectangle(
            RecordingStopActionBounds,
            10);
        graphics.FillPath(primaryBrush, primaryPath);
        graphics.DrawPath(primaryBorder, primaryPath);

        using var stopBrush = new SolidBrush(
            _interactionEnabled
                ? Color.FromArgb(255, 251, 113, 133)
                : Color.FromArgb(115, 251, 113, 133));
        graphics.FillRoundedRectangle(
            stopBrush,
            new RectangleF(161, 22, 10, 10),
            2);

        using var actionTitleFont = new Font(
            "Segoe UI",
            11,
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var actionHintFont = new Font(
            "Segoe UI",
            9.5f,
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        using var actionTitleBrush = new SolidBrush(
            _interactionEnabled
                ? Color.FromArgb(245, 250, 250, 252)
                : Color.FromArgb(125, 250, 250, 252));
        using var actionHintBrush = new SolidBrush(
            _interactionEnabled
                ? Color.FromArgb(185, 228, 228, 231)
                : Color.FromArgb(95, 228, 228, 231));
        using var actionFormat = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(
            "Stopp",
            actionTitleFont,
            actionTitleBrush,
            new RectangleF(178, 12, 66, 15),
            actionFormat);
        graphics.DrawString(
            "& einfügen",
            actionHintFont,
            actionHintBrush,
            new RectangleF(178, 27, 68, 14),
            actionFormat);

        var abortHovered =
            _interactionEnabled &&
            _hoveredTarget == InteractionTarget.AbortAction;
        var abortPressed =
            abortHovered &&
            _mouseDown &&
            _pressedTarget == InteractionTarget.AbortAction;
        var abortFill = !_interactionEnabled
            ? Color.FromArgb(18, 248, 113, 113)
            : abortPressed
                ? Color.FromArgb(82, 248, 113, 113)
                : abortHovered
                    ? Color.FromArgb(58, 248, 113, 113)
                    : Color.FromArgb(18, 248, 113, 113);
        using var abortBrush = new SolidBrush(abortFill);
        using var abortBorder = new Pen(
            abortHovered
                ? Color.FromArgb(115, 248, 113, 113)
                : Color.FromArgb(42, 248, 113, 113));
        using var abortPath = CreateRoundedRectangle(
            RecordingAbortActionBounds,
            10);
        graphics.FillPath(abortBrush, abortPath);
        graphics.DrawPath(abortBorder, abortPath);

        using var abortPen = new Pen(
            _interactionEnabled
                ? Color.FromArgb(225, 254, 202, 202)
                : Color.FromArgb(100, 254, 202, 202),
            1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(abortPen, 270, 22, 280, 32);
        graphics.DrawLine(abortPen, 280, 22, 270, 32);
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
            AppStatus.Idle => ("Bereit", $"{_toggleHotkey} · klicken zum Start"),
            AppStatus.Starting => ("Mikrofon wird aktiviert", "Diktierung startet …"),
            AppStatus.Recording => ("Aufnahme läuft", $"{GetRecordingDurationText()} · hört zu"),
            AppStatus.Stopping => ("Aufnahme beendet", "Audio wird vorbereitet …"),
            AppStatus.ReadingText => ("Transkription läuft", "Audio wird in Text umgewandelt …"),
            AppStatus.Pasting => ("Text wird eingefügt", "Gleich fertig …"),
            _ => ("Bereit", $"{_toggleHotkey} · klicken zum Start")
        };
    }

    internal static string FormatRecordingDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var totalHours = (int)elapsed.TotalHours;
        return totalHours > 0
            ? $"{totalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    private bool ShowsRecordingControls =>
        _status == AppStatus.Recording &&
        _transientError.Length == 0 &&
        _operationTitle.Length == 0;

    private string GetRecordingDurationText()
    {
        var elapsed = _recordingStartedAtUtc is { } startedAt
            ? DateTime.UtcNow - startedAt
            : TimeSpan.Zero;
        return FormatRecordingDuration(elapsed);
    }

    private InteractionTarget GetInteractionTarget(Point clientPoint)
    {
        if (!ClientRectangle.Contains(clientPoint))
        {
            return InteractionTarget.None;
        }

        if (!ShowsRecordingControls)
        {
            return InteractionTarget.Body;
        }

        var scale = GetScale();
        var logicalPoint = new PointF(
            clientPoint.X / scale,
            clientPoint.Y / scale);
        if (RecordingAbortActionBounds.Contains(logicalPoint))
        {
            return InteractionTarget.AbortAction;
        }

        return RecordingStopActionBounds.Contains(logicalPoint)
            ? InteractionTarget.PrimaryAction
            : InteractionTarget.Body;
    }

    private void RefreshInteractionPresentation()
    {
        _hoveredTarget = _hovered && IsHandleCreated
            ? GetInteractionTarget(PointToClient(Cursor.Position))
            : InteractionTarget.None;
        UpdateCursor(_hoveredTarget);
        UpdateToolTip(_hoveredTarget);
    }

    private void UpdateCursor(InteractionTarget target)
    {
        if (!_interactionEnabled)
        {
            Cursor = Cursors.Default;
            return;
        }

        if (_dragging ||
            ShowsRecordingControls && target == InteractionTarget.Body)
        {
            Cursor = Cursors.SizeAll;
            return;
        }

        Cursor = target is InteractionTarget.Body or
            InteractionTarget.PrimaryAction or
            InteractionTarget.AbortAction
                ? Cursors.Hand
                : Cursors.Default;
    }

    private void UpdateToolTip(InteractionTarget target)
    {
        var text = !_interactionEnabled
            ? "Steuerung vorübergehend deaktiviert"
            : ShowsRecordingControls
                ? target switch
                {
                    InteractionTarget.PrimaryAction =>
                        "Aufnahme stoppen und Text einfügen",
                    InteractionTarget.AbortAction =>
                        "Aufnahme verwerfen (Esc)",
                    InteractionTarget.Body =>
                        "Zum Verschieben ziehen",
                    _ => string.Empty
                }
                : target == InteractionTarget.Body
                    ? $"Diktierung umschalten ({_toggleHotkey})"
                    : string.Empty;
        if (text == _toolTipText)
        {
            return;
        }

        _toolTipText = text;
        _toolTip.SetToolTip(this, text);
    }

    private void UpdateAccessibilityText()
    {
        if (ShowsRecordingControls)
        {
            AccessibleDescription =
                "Aufnahme läuft. Stopp und Einfügen beendet die Aufnahme " +
                "und fügt den Text ein. X verwirft die Aufnahme. " +
                "Der Statusbereich kann zum Verschieben gezogen werden.";
            AccessibleDefaultActionDescription =
                "Aufnahme stoppen und Text einfügen";
        }
        else
        {
            var (title, hint) = GetText();
            AccessibleDescription =
                $"{title}. {hint}. Klicken schaltet die Diktierung um. " +
                "Ziehen verschiebt die Leiste.";
            AccessibleDefaultActionDescription = "Diktierung umschalten";
        }

        if (!_interactionEnabled)
        {
            AccessibleDescription += " Steuerung vorübergehend deaktiviert.";
        }

        if (IsHandleCreated)
        {
            AccessibilityNotifyClients(
                AccessibleEvents.DescriptionChange,
                -1);
        }
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
            ScaleValue(16));
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

    private enum InteractionTarget
    {
        None,
        Body,
        PrimaryAction,
        AbortAction
    }
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
