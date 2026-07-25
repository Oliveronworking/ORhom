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
            Trimming = StringTrimminÛžú¶‰žËkºwµçy¹…‰±•(€€€€€€€€€€€€€€€€ü½±½È¹É½µÉˆ ÈÈÔ°€ÈÔÐ°€ÈÀÈ°€ÈÀÈ¤(€€€€€€€€€€€€€€€€è½±½È¹É½µÉˆ ÄÀÀ°€ÈÔÐ°€ÈÀÈ°€ÈÀÈ¤°(€€€€€€€€€€€€Ä¸á˜¤(€€€€€€€ì(€€€€€€€€€€€MÑ…ÉÑ…À€ô1¥¹•…À¹I½Õ¹°(€€€€€€€€€€€¹‘…À€ô1¥¹•…À¹I½Õ¹(€€€€€€€ôì(€€€€€€€É…Á¡¥Ì¹É…Ý1¥¹”¡…‰½ÉÑA•¸°€ÈÜÀ°€ÈÈ°€ÈàÀ°€ÌÈ¤ì(€€€€€€€É…Á¡¥Ì¹É…Ý1¥¹”¡…‰½ÉÑA•¸°€ÈàÀ°€ÈÈ°€ÈÜÀ°€ÌÈ¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”€¡ÍÑÉ¥¹œQ¥Ñ±”°ÍÑÉ¥¹œ!¥¹Ð¤•ÑQ•áÐ ¤(€€€ì(€€€€€€€¥˜€¡}ÑÉ…¹Í¥•¹ÑÉÉ½È¹1•¹Ñ €ø€À¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸€ ‰¥­Ñ¥•ÉÕ¹œ™•¡±•Í¡±…•¸ˆ°}ÑÉ…¹Í¥•¹ÑÉÉ½È¤ì(€€€€€€€ô((€€€€€€€¥˜€¡}½Á•É…Ñ¥½¹Q¥Ñ±”¹1•¹Ñ €ø€À¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸€¡}½Á•É…Ñ¥½¹Q¥Ñ±”°}½Á•É…Ñ¥½¹!¥¹Ð¤ì(€€€€€€€ô((€€€€€€€É•ÑÕÉ¸}ÍÑ…ÑÕÌÍÝ¥Ñ (€€€€€€€ì(€€€€€€€€€€€ÁÁMÑ…ÑÕÌ¹%‘±”€ôø€ ‰	•É•¥Ðˆ°€‰í}Ñ½±•!½Ñ­•åôƒ
Ü­±¥­•¸éÕ´MÑ…ÉÐˆ¤°(€€€€€€€€€€€ÁÁMÑ…ÑÕÌ¹MÑ…ÉÑ¥¹œ€ôø€ ‰5¥­É½™½¸Ý¥É…­Ñ¥Ù¥•ÉÐˆ°€‰¥­Ñ¥•ÉÕ¹œÍÑ…ÉÑ•ÐƒŠ˜ˆ¤°(€€€€€€€€€€€ÁÁMÑ…ÑÕÌ¹I•½É‘¥¹œ€ôø€ ‰Õ™¹…¡µ”³‘Õ™Ðˆ°€‰í•ÑI•½É‘¥¹ÕÉ…Ñ¥½¹Q•áÐ ¥ôƒ
Ü£ÙÉÐéÔˆ¤°(€€€€€€€€€€€ÁÁMÑ…ÑÕÌ¹MÑ½ÁÁ¥¹œ€ôø€ ‰Õ™¹…¡µ”‰••¹‘•Ðˆ°€‰Õ‘¥¼Ý¥ÉÙ½É‰•É•¥Ñ•ÐƒŠ˜ˆ¤°(€€€€€€€€€€€ÁÁMÑ…ÑÕÌ¹I•…‘¥¹Q•áÐ€ôø€ ‰QÉ…¹Í­É¥ÁÑ¥½¸³‘Õ™Ðˆ°€‰Õ‘¥¼Ý¥É¥¸Q•áÐÕµ•Ý…¹‘•±ÐƒŠ˜ˆ¤°(€€€€€€€€€€€ÁÁMÑ…ÑÕÌ¹A…ÍÑ¥¹œ€ôø€ ‰Q•áÐÝ¥É•¥¹•›ñÐˆ°€‰±•¥ ™•ÉÑ¥œƒŠ˜ˆ¤°(€€€€€€€€€€€|€ôø€ ‰	•É•¥Ðˆ°€‰í}Ñ½±•!½Ñ­•åôƒ
Ü­±¥­•¸éÕ´MÑ…ÉÐˆ¤(€€€€€€€ôì(€€€ô((€€€¥¹Ñ•É¹…°ÍÑ…Ñ¥ŒÍÑÉ¥¹œ½Éµ…ÑI•½É‘¥¹ÕÉ…Ñ¥½¸¡Q¥µ•MÁ…¸•±…ÁÍ•¤(€€€ì(€€€€€€€¥˜€¡•±…ÁÍ•€ðQ¥µ•MÁ…¸¹i•É¼¤(€€€€€€€ì(€€€€€€€€€€€•±…ÁÍ•€ôQ¥µ•MÁ…¸¹i•É¼ì(€€€€€€€ô((€€€€€€€Ù…ÈÑ½Ñ…±!½ÕÉÌ€ô€¡¥¹Ð¥•±…ÁÍ•¹Q½Ñ…±!½ÕÉÌì(€€€€€€€É•ÑÕÉ¸Ñ½Ñ…±!½ÕÉÌ€ø€À(€€€€€€€€€€€€ü€‰íÑ½Ñ…±!½ÕÉÍôéí•±…ÁÍ•¹5¥¹ÕÑ•ÌèÀÁôéí•±…ÁÍ•¹M•½¹‘ÌèÀÁôˆ(€€€€€€€€€€€€è€‰ì¡¥¹Ð¥•±…ÁÍ•¹Q½Ñ…±5¥¹ÕÑ•ÌèÀÁôéí•±…ÁÍ•¹M•½¹‘ÌèÀÁôˆì(€€€ô((€€€ÁÉ¥Ù…Ñ”‰½½°M¡½ÝÍI•½É‘¥¹½¹ÑÉ½±Ì€ôø(€€€€€€€}ÍÑ…ÑÕÌ€ôôÁÁMÑ…ÑÕÌ¹I•½É‘¥¹œ€˜˜(€€€€€€€}ÑÉ…¹Í¥•¹ÑÉÉ½È¹1•¹Ñ €ôô€À€˜˜(€€€€€€€}½Á•É…Ñ¥½¹Q¥Ñ±”¹1•¹Ñ €ôô€Àì((€€€ÁÉ¥Ù…Ñ”ÍÑÉ¥¹œ•ÑI•½É‘¥¹ÕÉ…Ñ¥½¹Q•áÐ ¤(€€€ì(€€€€€€€Ù…È•±…ÁÍ•€ô}É•½É‘¥¹MÑ…ÉÑ•‘ÑUÑŒ¥ÌìôÍÑ…ÉÑ•‘Ð(€€€€€€€€€€€€ü…Ñ•Q¥µ”¹UÑ9½Ü€´ÍÑ…ÉÑ•‘Ð(€€€€€€€€€€€€èQ¥µ•MÁ…¸¹i•É¼ì(€€€€€€€É•ÑÕÉ¸½Éµ…ÑI•½É‘¥¹ÕÉ…Ñ¥½¸¡•±…ÁÍ•¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”%¹Ñ•É…Ñ¥½¹Q…É•Ð•Ñ%¹Ñ•É…Ñ¥½¹Q…É•Ð¡A½¥¹Ð±¥•¹ÑA½¥¹Ð¤(€€€ì(€€€€€€€¥˜€ …±¥•¹ÑI•Ñ…¹±”¹½¹Ñ…¥¹Ì¡±¥•¹ÑA½¥¹Ð¤¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸%¹Ñ•É…Ñ¥½¹Q…É•Ð¹9½¹”ì(€€€€€€€ô((€€€€€€€¥˜€ …M¡½ÝÍI•½É‘¥¹½¹ÑÉ½±Ì¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸%¹Ñ•É…Ñ¥½¹Q…É•Ð¹	½‘äì(€€€€€€€ô((€€€€€€€Ù…ÈÍ…±”€ô•ÑM…±” ¤ì(€€€€€€€Ù…È±½¥…±A½¥¹Ð€ô¹•ÜA½¥¹Ñ (€€€€€€€€€€€±¥•¹ÑA½¥¹Ð¹`€¼Í…±”°(€€€€€€€€€€€±¥•¹ÑA½¥¹Ð¹d€¼Í…±”¤ì(€€€€€€€¥˜€¡I•½É‘¥¹‰½ÉÑÑ¥½¹	½Õ¹‘Ì¹½¹Ñ…¥¹Ì¡±½¥…±A½¥¹Ð¤¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸%¹Ñ•É…Ñ¥½¹Q…É•Ð¹‰½ÉÑÑ¥½¸ì(€€€€€€€ô((€€€€€€€É•ÑÕÉ¸I•½É‘¥¹MÑ½ÁÑ¥½¹	½Õ¹‘Ì¹½¹Ñ…¥¹Ì¡±½¥…±A½¥¹Ð¤(€€€€€€€€€€€€ü%¹Ñ•É…Ñ¥½¹Q…É•Ð¹AÉ¥µ…ÉåÑ¥½¸(€€€€€€€€€€€€è%¹Ñ•É…Ñ¥½¹Q…É•Ð¹	½‘äì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥I•™É•Í¡%¹Ñ•É…Ñ¥½¹AÉ•Í•¹Ñ…Ñ¥½¸ ¤(€€€ì(€€€€€€€}¡½Ù•É•‘Q…É•Ð€ô}¡½Ù•É•€˜˜%Í!…¹‘±•É•…Ñ•(€€€€€€€€€€€€ü•Ñ%¹Ñ•É…Ñ¥½¹Q…É•Ð¡A½¥¹ÑQ½±¥•¹Ð¡ÕÉÍ½È¹A½Í¥Ñ¥½¸¤¤(€€€€€€€€€€€€è%¹Ñ•É…Ñ¥½¹Q…É•Ð¹9½¹”ì(€€€€€€€UÁ‘…Ñ•ÕÉÍ½È¡}¡½Ù•É•‘Q…É•Ð¤ì(€€€€€€€UÁ‘…Ñ•Q½½±Q¥À¡}¡½Ù•É•‘Q…É•Ð¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥UÁ‘…Ñ•ÕÉÍ½È¡%¹Ñ•É…Ñ¥½¹Q…É•ÐÑ…É•Ð¤(€€€ì(€€€€€€€¥˜€ …}¥¹Ñ•É…Ñ¥½¹¹…‰±•¤(€€€€€€€ì(€€€€€€€€€€€ÕÉÍ½È€ôÕÉÍ½ÉÌ¹•™…Õ±Ðì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€¥˜€¡}‘É…¥¹œñð(€€€€€€€€€€€M¡½ÝÍI•½É‘¥¹½¹ÑÉ½±Ì€˜˜Ñ…É•Ð€ôô%¹Ñ•É…Ñ¥½¹Q…É•Ð¹	½‘ä¤(€€€€€€€ì(€€€€€€€€€€€ÕÉÍ½È€ôÕÉÍ½ÉÌ¹M¥é•±°ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€ÕÉÍ½È€ôÑ…É•Ð¥Ì%¹Ñ•É…Ñ¥½¹Q…É•Ð¹	½‘ä½È(€€€€€€€€€€€%¹Ñ•É…Ñ¥½¹Q…É•Ð¹AÉ¥µ…ÉåÑ¥½¸½È(€€€€€€€€€€€%¹Ñ•É…Ñ¥½¹Q…É•Ð¹‰½ÉÑÑ¥½¸(€€€€€€€€€€€€€€€€üÕÉÍ½ÉÌ¹!…¹(€€€€€€€€€€€€€€€€èÕÉÍ½ÉÌ¹•™…Õ±Ðì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥UÁ‘…Ñ•Q½½±Q¥À¡%¹Ñ•É…Ñ¥½¹Q…É•ÐÑ…É•Ð¤(€€€ì(€€€€€€€Ù…ÈÑ•áÐ€ô€…}¥¹Ñ•É…Ñ¥½¹¹…‰±•(€€€€€€€€€€€€ü€‰MÑ•Õ•ÉÕ¹œÙ½Ëñ‰•É•¡•¹‘•…­Ñ¥Ù¥•ÉÐˆ(€€€€€€€€€€€€èM¡½ÝÍI•½É‘¥¹½¹ÑÉ½±Ì(€€€€€€€€€€€€€€€€üÑ…É•ÐÍÝ¥Ñ (€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€%¹Ñ•É…Ñ¥½¹Q…É•Ð¹AÉ¥µ…ÉåÑ¥½¸€ôø(€€€€€€€€€€€€€€€€€€€€€€€€‰Õ™¹…¡µ”ÍÑ½ÁÁ•¸Õ¹Q•áÐ•¥¹›ñ•¸ˆ°(€€€€€€€€€€€€€€€€€€€%¹Ñ•É…Ñ¥½¹Q…É•Ð¹‰½ÉÑÑ¥½¸€ôø(€€€€€€€€€€€€€€€€€€€€€€€€‰Õ™¹…¡µ”Ù•ÉÝ•É™•¸€¡ÍŒ¤ˆ°(€€€€€€€€€€€€€€€€€€€%¹Ñ•É…Ñ¥½¹Q…É•Ð¹	½‘ä€ôø(€€€€€€€€€€€€€€€€€€€€€€€€‰iÕ´Y•ÉÍ¡¥•‰•¸é¥•¡•¸ˆ°(€€€€€€€€€€€€€€€€€€€|€ôøÍÑÉ¥¹œ¹µÁÑä(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€èÑ…É•Ð€ôô%¹Ñ•É…Ñ¥½¹Q…É•Ð¹	½‘ä(€€€€€€€€€€€€€€€€€€€€ü€‰¥­Ñ¥•ÉÕ¹œÕµÍ¡…±Ñ•¸€¡í}Ñ½±•!½Ñ­•åô¤ˆ(€€€€€€€€€€€€€€€€€€€€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€¥˜€¡Ñ•áÐ€ôô}Ñ½½±Q¥ÁQ•áÐ¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€}Ñ½½±Q¥ÁQ•áÐ€ôÑ•áÐì(€€€€€€€}Ñ½½±Q¥À¹M•ÑQ½½±Q¥À¡Ñ¡¥Ì°Ñ•áÐ¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥UÁ‘…Ñ••ÍÍ¥‰¥±¥ÑåQ•áÐ ¤(€€€ì(€€€€€€€¥˜€¡M¡½ÝÍI•½É‘¥¹½¹ÑÉ½±Ì¤(€€€€€€€ì(€€€€€€€€€€€•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ô(€€€€€€€€€€€€€€€€‰Õ™¹…¡µ”³‘Õ™Ð¸MÑ½ÁÀÕ¹¥¹›ñ•¸‰••¹‘•Ð‘¥”Õ™¹…¡µ”€ˆ€¬(€€€€€€€€€€€€€€€€‰Õ¹›ñÐ‘•¸Q•áÐ•¥¸¸`Ù•ÉÝ¥É™Ð‘¥”Õ™¹…¡µ”¸€ˆ€¬(€€€€€€€€€€€€€€€€‰•ÈMÑ…ÑÕÍ‰•É•¥ ­…¹¸éÕ´Y•ÉÍ¡¥•‰•¸•é½•¸Ý•É‘•¸¸ˆì(€€€€€€€€€€€•ÍÍ¥‰±••™…Õ±ÑÑ¥½¹•ÍÉ¥ÁÑ¥½¸€ô(€€€€€€€€€€€€€€€€‰Õ™¹…¡µ”ÍÑ½ÁÁ•¸Õ¹Q•áÐ•¥¹›ñ•¸ˆì(€€€€€€€ô(€€€€€€€•±Í”(€€€€€€€ì(€€€€€€€€€€€Ù…È€¡Ñ¥Ñ±”°¡¥¹Ð¤€ô•ÑQ•áÐ ¤ì(€€€€€€€€€€€•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ô(€€€€€€€€€€€€€€€€‰íÑ¥Ñ±•ô¸í¡¥¹Ñô¸-±¥­•¸Í¡…±Ñ•Ð‘¥”¥­Ñ¥•ÉÕ¹œÕ´¸€ˆ€¬(€€€€€€€€€€€€€€€€‰i¥•¡•¸Ù•ÉÍ¡¥•‰Ð‘¥”1•¥ÍÑ”¸ˆì(€€€€€€€€€€€•ÍÍ¥‰±••™…Õ±ÑÑ¥½¹•ÍÉ¥ÁÑ¥½¸€ô€‰¥­Ñ¥•ÉÕ¹œÕµÍ¡…±Ñ•¸ˆì(€€€€€€€ô((€€€€€€€¥˜€ …}¥¹Ñ•É…Ñ¥½¹¹…‰±•¤(€€€€€€€ì(€€€€€€€€€€€•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€¬ô€ˆMÑ•Õ•ÉÕ¹œÙ½Ëñ‰•É•¡•¹‘•…­Ñ¥Ù¥•ÉÐ¸ˆì(€€€€€€€ô((€€€€€€€¥˜€¡%Í!…¹‘±•É•…Ñ•¤(€€€€€€€ì(€€€€€€€€€€€•ÍÍ¥‰¥±¥Ñå9½Ñ¥™å±¥•¹ÑÌ (€€€€€€€€€€€€€€€•ÍÍ¥‰±•Ù•¹ÑÌ¹•ÍÉ¥ÁÑ¥½¹¡…¹”°(€€€€€€€€€€€€€€€€´Ä¤ì(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥¹ÍÕÉ•A½Í¥Ñ¥½¸ ¤(€€€ì(€€€€€€€¥˜€¡}Á½Í¥Ñ¥½¹%¹¥Ñ¥…±¥é•¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€Ù…ÈÍÉ••¸€ôI•Í½±Ù•A±…•µ•¹ÑMÉ••¸ ¤ì(€€€€€€€1½…Ñ¥½¸€ô}Á±…•µ•¹Ð¥Ì¹Õ±°(€€€€€€€€€€€€üI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ñ…±Õ±…Ñ½È¹•Ñ•™…Õ±Ñ1½…Ñ¥½¸ (€€€€€€€€€€€€€€€ÍÉ••¸¹]½É­¥¹É•„°(€€€€€€€€€€€€€€€M¥é”°(€€€€€€€€€€€€€€€M…±•Y…±Õ”¡}‰½ÑÑ½µ=™™Í•ÑAà¤¤(€€€€€€€€€€€€èI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ñ…±Õ±…Ñ½È¹I•ÍÑ½É” (€€€€€€€€€€€€€€€ÍÉ••¸¹]½É­¥¹É•„°(€€€€€€€€€€€€€€€M¥é”°(€€€€€€€€€€€€€€€}Á±…•µ•¹Ð¹I•±…Ñ¥Ù•`°(€€€€€€€€€€€€€€€}Á±…•µ•¹Ð¹I•±…Ñ¥Ù•d¤ì(€€€€€€€}Á½Í¥Ñ¥½¹%¹¥Ñ¥…±¥é•€ôÑÉÕ”ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥ÁÁ±åÁ¥M¥é•¹‘A±…•µ•¹Ð ¤(€€€ì(€€€€€€€Ù…È¹•ÝM¥é”€ôM…±•1½¥…±M¥é”¡•Ù¥•Á¤¤ì(€€€€€€€¥˜€¡±¥•¹ÑM¥é”€„ô¹•ÝM¥é”¤(€€€€€€€ì(€€€€€€€€€€€±¥•¹ÑM¥é”€ô¹•ÝM¥é”ì(€€€€€€€ô((€€€€€€€¥˜€¡}‘É…¥¹œ¤(€€€€€€€ì(€€€€€€€€€€€€¼¼A•É5½¹¥Ñ½ÉXÈA$¡…¹”…¸¡…ÁÁ•¸Ý¡¥±”É½ÍÍ¥¹œ„µ½¹¥Ñ½È(€€€€€€€€€€€€¼¼‰½Õ¹‘…Éä¸-••À™½±±½Ý¥¹œÑ¡”Á½¥¹Ñ•È¥¹ÍÑ•…½˜É•ÍÑ½É¥¹œÑ¡”(€€€€€€€€€€€€¼¼Á±…•µ•¹Ð™É½´Ñ¡”µ½¹¥Ñ½ÈÝ¡•É”Ñ¡”‘É…œ‰•…¸¸(€€€€€€€€€€€}‘É…MÑ…ÉÑÕÉÍ½È€ôÕÉÍ½È¹A½Í¥Ñ¥½¸ì(€€€€€€€€€€€}‘É…MÑ…ÉÑ1½…Ñ¥½¸€ô1½…Ñ¥½¸ì(€€€€€€€€€€€UÁ‘…Ñ•I½Õ¹‘•‘I•¥½¸ ¤ì(€€€€€€€€€€€%¹Ù…±¥‘…Ñ” ¤ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€¥˜€¡}Á±…•µ•¹Ð¥Ì¹½Ð¹Õ±°¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÉ••¸€ôI•Í½±Ù•A±…•µ•¹ÑMÉ••¸ ¤ì(€€€€€€€€€€€1½…Ñ¥½¸€ôI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ñ…±Õ±…Ñ½È¹I•ÍÑ½É” (€€€€€€€€€€€€€€€ÍÉ••¸¹]½É­¥¹É•„°(€€€€€€€€€€€€€€€M¥é”°(€€€€€€€€€€€€€€€}Á±…•µ•¹Ð¹I•±…Ñ¥Ù•`°(€€€€€€€€€€€€€€€}Á±…•µ•¹Ð¹I•±…Ñ¥Ù•d¤ì(€€€€€€€€€€€}Á½Í¥Ñ¥½¹%¹¥Ñ¥…±¥é•€ôÑÉÕ”ì(€€€€€€€ô(€€€€€€€•±Í”¥˜€¡}Á½Í¥Ñ¥½¹%¹¥Ñ¥…±¥é•¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÉ••¸€ôMÉ••¸¹É½µI•Ñ…¹±”¡	½Õ¹‘Ì¤ì(€€€€€€€€€€€1½…Ñ¥½¸€ôI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ñ…±Õ±…Ñ½È¹±…µÀ (€€€€€€€€€€€€€€€1½…Ñ¥½¸°(€€€€€€€€€€€€€€€M¥é”°(€€€€€€€€€€€€€€€ÍÉ••¸¹]½É­¥¹É•„¤ì(€€€€€€€ô((€€€€€€€UÁ‘…Ñ•I½Õ¹‘•‘I•¥½¸ ¤ì(€€€€€€€%¹Ù…±¥‘…Ñ” ¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥½µµ¥ÑÕÉÉ•¹ÑA±…•µ•¹Ð ¤(€€€ì(€€€€€€€Ù…ÈÍÉ••¸€ôMÉ••¸¹É½µA½¥¹Ð¡ÕÉÍ½È¹A½Í¥Ñ¥½¸¤ì(€€€€€€€1½…Ñ¥½¸€ôI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ñ…±Õ±…Ñ½È¹±…µÀ (€€€€€€€€€€€1½…Ñ¥½¸°(€€€€€€€€€€€M¥é”°(€€€€€€€€€€€ÍÉ••¸¹]½É­¥¹É•„¤ì(€€€€€€€}Á±…•µ•¹Ð€ôI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ñ…±Õ±…Ñ½È¹…ÁÑÕÉ” (€€€€€€€€€€€ÍÉ••¸¹•Ù¥•9…µ”°(€€€€€€€€€€€ÍÉ••¸¹]½É­¥¹É•„°(€€€€€€€€€€€	½Õ¹‘Ì¤ì(€€€€€€€A±…•µ•¹Ñ½µµ¥ÑÑ•ü¹%¹Ù½­” (€€€€€€€€€€€Ñ¡¥Ì°(€€€€€€€€€€€¹•ÜI•½É‘¥¹=Ù•É±…åA±…•µ•¹ÑÙ•¹ÑÉÌ¡}Á±…•µ•¹Ð¤¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥5…¥¹Ñ…¥¹Q½Á5½ÍÐ (€€€€€€€‰½½°™½É”€ô™…±Í”°(€€€€€€€‰½½°¡•­½É=±ÕÍ¥½¸€ô™…±Í”¤(€€€ì(€€€€€€€¥˜€ …Y¥Í¥‰±”ñð€…%Í!…¹‘±•É•…Ñ•ñð%Í¥ÍÁ½Í•ñð¥ÍÁ½Í¥¹œ¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€Ù…È™½É•É½Õ¹‘]¥¹‘½Ü€ô9…Ñ¥Ù•5•Ñ¡½‘Ì¹•Ñ½É•É½Õ¹‘]¥¹‘½Ü ¤ì(€€€€€€€¥˜€ …™½É”€˜˜(€€€€€€€€€€€™½É•É½Õ¹‘]¥¹‘½Ü€ôô}±…ÍÑ½É•É½Õ¹‘]¥¹‘½Ü€˜˜(€€€€€€€€€€€€ …¡•­½É=±ÕÍ¥½¸ñð(€€€€€€€€€€€€€…9…Ñ¥Ù•5•Ñ¡½‘Ì¹%Í]¥¹‘½Ý½Ù•É•‘ÑAÉ½‰•A½¥¹ÑÌ¡!…¹‘±”¤¤¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€}±…ÍÑ½É•É½Õ¹‘]¥¹‘½Ü€ô9…Ñ¥Ù•5•Ñ¡½‘Ì¹I•…ÍÍ•ÉÑ]¥¹‘½ÝQ½Á5½ÍÐ¡!…¹‘±”¤(€€€€€€€€€€€€ü™½É•É½Õ¹‘]¥¹‘½Ü(€€€€€€€€€€€€è¹Õ±°ì(€€€ô((€€€ÁÉ¥Ù…Ñ”MÉ••¸I•Í½±Ù•A±…•µ•¹ÑMÉ••¸ ¤(€€€ì(€€€€€€€¥˜€¡}Á±…•µ•¹Ð¥Ì¹½Ð¹Õ±°¤(€€€€€€€ì(€€€€€€€€€€€Ù…È½¹™¥ÕÉ•‘MÉ••¸€ôMÉ••¸¹±±MÉ••¹Ì¹¥ÉÍÑ=É•™…Õ±Ð¡ÍÉ••¸€ôø(€€€€€€€€€€€€€€€ÍÉ••¸¹•Ù¥•9…µ”¹ÅÕ…±Ì (€€€€€€€€€€€€€€€€€€€}Á±…•µ•¹Ð¹5½¹¥Ñ½É•Ù¥•9…µ”°(€€€€€€€€€€€€€€€€€€€MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤¤ì(€€€€€€€€€€€¥˜€¡½¹™¥ÕÉ•‘MÉ••¸¥Ì¹½Ð¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸½¹™¥ÕÉ•‘MÉ••¸ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€É•ÑÕÉ¸MÉ••¸¹AÉ¥µ…ÉåMÉ••¸€üüMÉ••¸¹±±MÉ••¹ÍlÁtì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥UÁ‘…Ñ•I½Õ¹‘•‘I•¥½¸ ¤(€€€ì(€€€€€€€¥˜€¡]¥‘Ñ €ðô€Àñð!•¥¡Ð€ðô€À¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€ÕÍ¥¹œÙ…ÈÁ…Ñ €ôÉ•…Ñ•I½Õ¹‘•‘I•Ñ…¹±” (€€€€€€€€€€€¹•ÜI•Ñ…¹±• À°€À°]¥‘Ñ °!•¥¡Ð¤°(€€€€€€€€€€€M…±•Y…±Õ” ÄØ¤¤ì(€€€€€€€I•¥½¸ü¹¥ÍÁ½Í” ¤ì(€€€€€€€I•¥½¸€ô¹•ÜI•¥½¸¡Á…Ñ ¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”‰½½°!…Íá••‘•‘É…Q¡É•Í¡½±¡M¥é”‘•±Ñ„¤(€€€ì(€€€€€€€Ù…ÈÑ¡É•Í¡½±€ôMåÍÑ•µ%¹™½Éµ…Ñ¥½¸¹É…M¥é”ì(€€€€€€€É•ÑÕÉ¸5…Ñ ¹‰Ì¡‘•±Ñ„¹]¥‘Ñ ¤€øô5…Ñ ¹5…à¡Ñ¡É•Í¡½±¹]¥‘Ñ €¼€È°€È¤ñð(€€€€€€€€€€€€€€5…Ñ ¹‰Ì¡‘•±Ñ„¹!•¥¡Ð¤€øô5…Ñ ¹5…à¡Ñ¡É•Í¡½±¹!•¥¡Ð€¼€È°€È¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”™±½…Ð•ÑM…±” ¤€ôø5…Ñ ¹5…à¡•Ù¥•Á¤°€äØ¤€¼€äÙ˜ì((€€€ÁÉ¥Ù…Ñ”¥¹ÐM…±•Y…±Õ”¡¥¹Ð±½¥…±Y…±Õ”¤€ôø(€€€€€€€€¡¥¹Ð¥5…Ñ ¹I½Õ¹¡±½¥…±Y…±Õ”€¨•ÑM…±” ¤°5¥‘Á½¥¹ÑI½Õ¹‘¥¹œ¹Ý…åÉ½µi•É¼¤ì((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒM¥é”M…±•1½¥…±M¥é”¡¥¹Ð‘Á¤¤(€€€ì(€€€€€€€Ù…ÈÍ…±”€ô5…Ñ ¹5…à¡‘Á¤°€äØ¤€¼€äÙì(€€€€€€€É•ÑÕÉ¸¹•ÜM¥é” (€€€€€€€€€€€€¡¥¹Ð¥5…Ñ ¹I½Õ¹¡1½¥…±M¥é”¹]¥‘Ñ €¨Í…±”°5¥‘Á½¥¹ÑI½Õ¹‘¥¹œ¹Ý…åÉ½µi•É¼¤°(€€€€€€€€€€€€¡¥¹Ð¥5…Ñ ¹I½Õ¹¡1½¥…±M¥é”¹!•¥¡Ð€¨Í…±”°5¥‘Á½¥¹ÑI½Õ¹‘¥¹œ¹Ý…åÉ½µi•É¼¤¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÉ…Á¡¥ÍA…Ñ É•…Ñ•I½Õ¹‘•‘I•Ñ…¹±”¡I•Ñ…¹±•É•Ñ…¹±”°™±½…ÐÉ…‘¥ÕÌ¤(€€€ì(€€€€€€€Ù…È‘¥…µ•Ñ•È€ôÉ…‘¥ÕÌ€¨€Èì(€€€€€€€Ù…ÈÁ…Ñ €ô¹•ÜÉ…Á¡¥ÍA…Ñ  ¤ì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹1•™Ð°É•Ñ…¹±”¹Q½À°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€ÄàÀ°€äÀ¤ì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹I¥¡Ð€´‘¥…µ•Ñ•È°É•Ñ…¹±”¹Q½À°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€ÈÜÀ°€äÀ¤ì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹I¥¡Ð€´‘¥…µ•Ñ•È°É•Ñ…¹±”¹	½ÑÑ½´€´‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€À°€äÀ¤ì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹1•™Ð°É•Ñ…¹±”¹	½ÑÑ½´€´‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€äÀ°€äÀ¤ì(€€€€€€€Á…Ñ ¹±½Í•¥ÕÉ” ¤ì(€€€€€€€É•ÑÕÉ¸Á…Ñ ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÍÑÉ¥¹œ½±±…ÁÍ•]¡¥Ñ•ÍÁ…”¡ÍÑÉ¥¹œµ•ÍÍ…”¤€ôø(€€€€€€€ÍÑÉ¥¹œ¹)½¥¸ œ€œ°µ•ÍÍ…”¹MÁ±¥Ð (€€€€€€€€€€€lœ€œ°€qÐœ°€qÈœ°€q¸t°(€€€€€€€€€€€MÑÉ¥¹MÁ±¥Ñ=ÁÑ¥½¹Ì¹I•µ½Ù•µÁÑå¹ÑÉ¥•Ì¤¤ì((€€€ÁÉ¥Ù…Ñ”•¹Õ´%¹Ñ•É…Ñ¥½¹Q…É•Ð(€€€ì(€€€€€€€9½¹”°(€€€€€€€	½‘ä°(€€€€€€€AÉ¥µ…ÉåÑ¥½¸°(€€€€€€€‰½ÉÑÑ¥½¸(€€€ô)ô()¥¹Ñ•É¹…°Í•…±•É•½ÉI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ð (€€€ÍÑÉ¥¹œ5½¹¥Ñ½É•Ù¥•9…µ”°(€€€‘½Õ‰±”I•±…Ñ¥Ù•`°(€€€‘½Õ‰±”I•±…Ñ¥Ù•d¤ì()¥¹Ñ•É¹…°Í•…±•±…ÍÌI•½É‘¥¹=Ù•É±…åA±…•µ•¹ÑÙ•¹ÑÉÌ€èÙ•¹ÑÉÌ)ì(€€€ÁÕ‰±¥ŒI•½É‘¥¹=Ù•É±…åA±…•µ•¹ÑÙ•¹ÑÉÌ¡I•½É‘¥¹=Ù•É±…åA±…•µ•¹ÐÁ±…•µ•¹Ð¤(€€€ì(€€€€€€€A±…•µ•¹Ð€ôÁ±…•µ•¹Ðì(€€€ô((€€€ÁÕ‰±¥ŒI•½É‘¥¹=Ù•É±…åA±…•µ•¹ÐA±…•µ•¹Ðì•Ðìô)ô()¥¹Ñ•É¹…°ÍÑ…Ñ¥Œ±…ÍÌI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ñ…±Õ±…Ñ½È)ì(€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒA½¥¹Ð•Ñ•™…Õ±Ñ1½…Ñ¥½¸ (€€€€€€€I•Ñ…¹±”Ý½É­¥¹É•„°(€€€€€€€M¥é”½Ù•É±…åM¥é”°(€€€€€€€¥¹Ð‰½ÑÑ½µ=™™Í•ÑAà¤(€€€ì(€€€€€€€É•ÑÕÉ¸±…µÀ (€€€€€€€€€€€¹•ÜA½¥¹Ð (€€€€€€€€€€€€€€€Ý½É­¥¹É•„¹1•™Ð€¬€¡Ý½É­¥¹É•„¹]¥‘Ñ €´½Ù•É±…åM¥é”¹]¥‘Ñ ¤€¼€È°(€€€€€€€€€€€€€€€Ý½É­¥¹É•„¹	½ÑÑ½´€´½Ù•É±…åM¥é”¹!•¥¡Ð€´5…Ñ ¹5…à¡‰½ÑÑ½µ=™™Í•ÑAà°€À¤¤°(€€€€€€€€€€€½Ù•É±…åM¥é”°(€€€€€€€€€€€Ý½É­¥¹É•„¤ì(€€€ô((€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒA½¥¹ÐI•ÍÑ½É” (€€€€€€€I•Ñ…¹±”Ý½É­¥¹É•„°(€€€€€€€M¥é”½Ù•É±…åM¥é”°(€€€€€€€‘½Õ‰±”É•±…Ñ¥Ù•`°(€€€€€€€‘½Õ‰±”É•±…Ñ¥Ù•d¤(€€€ì(€€€€€€€Ù…Èµ½Ù…‰±•]¥‘Ñ €ô5…Ñ ¹5…à¡Ý½É­¥¹É•„¹]¥‘Ñ €´½Ù•É±…åM¥é”¹]¥‘Ñ °€À¤ì(€€€€€€€Ù…Èµ½Ù…‰±•!•¥¡Ð€ô5…Ñ ¹5…à¡Ý½É­¥¹É•„¹!•¥¡Ð€´½Ù•É±…åM¥é”¹!•¥¡Ð°€À¤ì(€€€€€€€Ù…Èà€ôÝ½É­¥¹É•„¹1•™Ð€¬€¡¥¹Ð¥5…Ñ ¹I½Õ¹ (€€€€€€€€€€€µ½Ù…‰±•]¥‘Ñ €¨9½Éµ…±¥é”¡É•±…Ñ¥Ù•`¤°(€€€€€€€€€€€5¥‘Á½¥¹ÑI½Õ¹‘¥¹œ¹Ý…åÉ½µi•É¼¤ì(€€€€€€€Ù…Èä€ôÝ½É­¥¹É•„¹Q½À€¬€¡¥¹Ð¥5…Ñ ¹I½Õ¹ (€€€€€€€€€€€µ½Ù…‰±•!•¥¡Ð€¨9½Éµ…±¥é”¡É•±…Ñ¥Ù•d¤°(€€€€€€€€€€€5¥‘Á½¥¹ÑI½Õ¹‘¥¹œ¹Ý…åÉ½µi•É¼¤ì(€€€€€€€É•ÑÕÉ¸±…µÀ¡¹•ÜA½¥¹Ð¡à°ä¤°½Ù•É±…åM¥é”°Ý½É­¥¹É•„¤ì(€€€ô((€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒA½¥¹Ð±…µÀ (€€€€€€€A½¥¹Ð±½…Ñ¥½¸°(€€€€€€€M¥é”½Ù•É±…åM¥é”°(€€€€€€€I•Ñ…¹±”Ý½É­¥¹É•„¤(€€€ì(€€€€€€€Ù…Èµ…á¥µÕµ`€ô5…Ñ ¹5…à¡Ý½É­¥¹É•„¹1•™Ð°Ý½É­¥¹É•„¹I¥¡Ð€´½Ù•É±…åM¥é”¹]¥‘Ñ ¤ì(€€€€€€€Ù…Èµ…á¥µÕµd€ô5…Ñ ¹5…à¡Ý½É­¥¹É•„¹Q½À°Ý½É­¥¹É•„¹	½ÑÑ½´€´½Ù•É±…åM¥é”¹!•¥¡Ð¤ì(€€€€€€€É•ÑÕÉ¸¹•ÜA½¥¹Ð (€€€€€€€€€€€5…Ñ ¹±…µÀ¡±½…Ñ¥½¸¹`°Ý½É­¥¹É•„¹1•™Ð°µ…á¥µÕµ`¤°(€€€€€€€€€€€5…Ñ ¹±…µÀ¡±½…Ñ¥½¸¹d°Ý½É­¥¹É•„¹Q½À°µ…á¥µÕµd¤¤ì(€€€ô((€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ð…ÁÑÕÉ” (€€€€€€€ÍÑÉ¥¹œµ½¹¥Ñ½É•Ù¥•9…µ”°(€€€€€€€I•Ñ…¹±”Ý½É­¥¹É•„°(€€€€€€€I•Ñ…¹±”½Ù•É±…å	½Õ¹‘Ì¤(€€€ì(€€€€€€€Ù…È±…µÁ•€ô±…µÀ¡½Ù•É±…å	½Õ¹‘Ì¹1½…Ñ¥½¸°½Ù•É±…å	½Õ¹‘Ì¹M¥é”°Ý½É­¥¹É•„¤ì(€€€€€€€Ù…Èµ½Ù…‰±•]¥‘Ñ €ô5…Ñ ¹5…à¡Ý½É­¥¹É•„¹]¥‘Ñ €´½Ù•É±…å	½Õ¹‘Ì¹]¥‘Ñ °€À¤ì(€€€€€€€Ù…Èµ½Ù…‰±•!•¥¡Ð€ô5…Ñ ¹5…à¡Ý½É­¥¹É•„¹!•¥¡Ð€´½Ù•É±…å	½Õ¹‘Ì¹!•¥¡Ð°€À¤ì(€€€€€€€Ù…ÈÉ•±…Ñ¥Ù•`€ôµ½Ù…‰±•]¥‘Ñ €ôô€À(€€€€€€€€€€€€ü€À(€€€€€€€€€€€€è€¡±…µÁ•¹`€´Ý½É­¥¹É•„¹1•™Ð¤€¼€¡‘½Õ‰±”¥µ½Ù…‰±•]¥‘Ñ ì(€€€€€€€Ù…ÈÉ•±…Ñ¥Ù•d€ôµ½Ù…‰±•!•¥¡Ð€ôô€À(€€€€€€€€€€€€ü€À(€€€€€€€€€€€€è€¡±…µÁ•¹d€´Ý½É­¥¹É•„¹Q½À¤€¼€¡‘½Õ‰±”¥µ½Ù…‰±•!•¥¡Ðì(€€€€€€€É•ÑÕÉ¸¹•ÜI•½É‘¥¹=Ù•É±…åA±…•µ•¹Ð (€€€€€€€€€€€µ½¹¥Ñ½É•Ù¥•9…µ”°(€€€€€€€€€€€9½Éµ…±¥é”¡É•±…Ñ¥Ù•`¤°(€€€€€€€€€€€9½Éµ…±¥é”¡É•±…Ñ¥Ù•d¤¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‘½Õ‰±”9½Éµ…±¥é”¡‘½Õ‰±”Ù…±Õ”¤€ôø(€€€€€€€‘½Õ‰±”¹%Í9…8¡Ù…±Õ”¤ñð‘½Õ‰±”¹%Í%¹™¥¹¥Ñä¡Ù…±Õ”¤(€€€€€€€€€€€€ü€À(€€€€€€€€€€€€è5…Ñ ¹±…µÀ¡Ù…±Õ”°€À°€Ä¤ì)ô()¥¹Ñ•É¹…°ÍÑ…Ñ¥Œ±…ÍÌÉ…Á¡¥ÍáÑ•¹Í¥½¹Ì)ì(€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒÙ½¥¥±±I½Õ¹‘•‘I•Ñ…¹±” (€€€€€€€Ñ¡¥ÌÉ…Á¡¥ÌÉ…Á¡¥Ì°(€€€€€€€	ÉÕÍ ‰ÉÕÍ °(€€€€€€€I•Ñ…¹±•É•Ñ…¹±”°(€€€€€€€™±½…ÐÉ…‘¥ÕÌ¤(€€€ì(€€€€€€€ÕÍ¥¹œÙ…ÈÁ…Ñ €ô¹•ÜÉ…Á¡¥ÍA…Ñ  ¤ì(€€€€€€€Ù…È‘¥…µ•Ñ•È€ôÉ…‘¥ÕÌ€¨€Èì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹1•™Ð°É•Ñ…¹±”¹Q½À°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€ÄàÀ°€äÀ¤ì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹I¥¡Ð€´‘¥…µ•Ñ•È°É•Ñ…¹±”¹Q½À°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€ÈÜÀ°€äÀ¤ì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹I¥¡Ð€´‘¥…µ•Ñ•È°É•Ñ…¹±”¹	½ÑÑ½´€´‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€À°€äÀ¤ì(€€€€€€€Á…Ñ ¹‘‘ÉŒ¡É•Ñ…¹±”¹1•™Ð°É•Ñ…¹±”¹	½ÑÑ½´€´‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°‘¥…µ•Ñ•È°€äÀ°€äÀ¤ì(€€€€€€€Á…Ñ ¹±½Í•¥ÕÉ” ¤ì(€€€€€€€É…Á¡¥Ì¹¥±±A…Ñ ¡‰ÉÕÍ °Á…Ñ ¤ì(€€€ô)ô(