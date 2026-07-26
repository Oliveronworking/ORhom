using System.Drawing;
using System.Runtime.ExceptionServices;

namespace ORhom.Tests;

public sealed class RecordingOverlayPlacementTests
{
    private static readonly Size OverlaySize = new(300, 56);

    [Fact]
    public void DefaultPlacementMatchesCompactBottomCenteredBar()
    {
        var location = RecordingOverlayPlacementCalculator.GetDefaultLocation(
            new Rectangle(0, 0, 1920, 1040),
            OverlaySize,
            72);

        Assert.Equal(new Point(810, 912), location);
    }

    [Fact]
    public void DefaultPlacementSupportsMonitorLeftOfPrimaryScreen()
    {
        var location = RecordingOverlayPlacementCalculator.GetDefaultLocation(
            new Rectangle(-1920, 0, 1920, 1040),
            OverlaySize,
            72);

        Assert.Equal(new Point(-1110, 912), location);
    }

    [Fact]
    public void RelativePlacementRestoresAtRightBottomEdge()
    {
        var location = RecordingOverlayPlacementCalculator.Restore(
            new Rectangle(-1600, -900, 1600, 900),
            OverlaySize,
            1,
            1);

        Assert.Equal(new Point(-300, -56), location);
    }

    [Fact]
    public void CaptureAndRestoreRoundTripKeepsPosition()
    {
        var workingArea = new Rectangle(-1920, 0, 1920, 1040);
        var bounds = new Rectangle(-1500, 411, OverlaySize.Width, OverlaySize.Height);

        var placement = RecordingOverlayPlacementCalculator.Capture(
            @"\\.\DISPLAY2",
            workingArea,
            bounds);
        var restored = RecordingOverlayPlacementCalculator.Restore(
            workingArea,
            OverlaySize,
            placement.RelativeX,
            placement.RelativeY);

        Assert.Equal(bounds.Location, restored);
        Assert.Equal(@"\\.\DISPLAY2", placement.MonitorDeviceName);
    }

    [Fact]
    public void RestoreClampsInvalidRelativeCoordinates()
    {
        var workingArea = new Rectangle(100, 200, 1000, 700);

        var location = RecordingOverlayPlacementCalculator.Restore(
            workingArea,
            OverlaySize,
            -5,
            4);

        Assert.Equal(new Point(100, 844), location);
    }

    [Fact]
    public void RecordingDurationUsesCompactClockFormat()
    {
        Assert.Equal(
            "00:00",
            RecordingOverlayForm.FormatRecordingDuration(TimeSpan.Zero));
        Assert.Equal(
            "01:05",
            RecordingOverlayForm.FormatRecordingDuration(
                TimeSpan.FromSeconds(65)));
        Assert.Equal(
            "1:02:03",
            RecordingOverlayForm.FormatRecordingDuration(
                new TimeSpan(1, 2, 3)));
        Assert.Equal(
            "00:00",
            RecordingOverlayForm.FormatRecordingDuration(
                TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void RefreshTimerAdaptsCadenceAndStopsWhenOverlayIsHidden()
    {
        RunOnStaThread(() =>
        {
            using var form = new RecordingOverlayForm(72, "Ctrl+Space")
            {
                Opacity = 0
            };
            var timer = GetAnimationTimer(form);

            form.ShowStatus(AppStatus.Idle);
            Application.DoEvents();
            Assert.True(timer.Enabled);
            Assert.Equal(500, timer.Interval);

            form.ShowStatus(AppStatus.Recording);
            Assert.True(timer.Enabled);
            Assert.Equal(90, timer.Interval);

            form.ShowStatus(AppStatus.Pasting);
            Assert.True(timer.Enabled);
            Assert.Equal(2000, timer.Interval);

            form.ShowStatus(AppStatus.Idle);
            form.ShowOperationProgress("Modell wird vorbereitet", "Bitte warten");
            Assert.Equal(90, timer.Interval);

            form.ClearOperationProgress();
            Assert.Equal(500, timer.Interval);

            form.ShowTransientError("Testfehler");
            Assert.Equal(2000, timer.Interval);

            form.HideOverlay();
            Assert.False(timer.Enabled);
        });
    }

    [Fact]
    public void RefreshTickKeepsActiveAndIdleAnimationsButLeavesStaticStateUnchanged()
    {
        RunOnStaThread(() =>
        {
            using var form = new RecordingOverlayForm(72, "Ctrl+Space")
            {
                Opacity = 0
            };

            form.ShowStatus(AppStatus.Idle);
            Application.DoEvents();
            StopAnimationTimer(form);
            SetPrivateField(form, "_animationFrame", 10);
            InvokeParameterless(form, "OnRefreshTimerTick");
            Assert.Equal(15, GetPrivateField<int>(form, "_animationFrame"));

            form.ShowStatus(AppStatus.Recording);
            StopAnimationTimer(form);
            SetPrivateField(form, "_animationFrame", 10);
            InvokeParameterless(form, "OnRefreshTimerTick");
            Assert.Equal(11, GetPrivateField<int>(form, "_animationFrame"));

            form.ShowStatus(AppStatus.Pasting);
            StopAnimationTimer(form);
            SetPrivateField(form, "_animationFrame", 10);
            InvokeParameterless(form, "OnRefreshTimerTick");
            Assert.Equal(10, GetPrivateField<int>(form, "_animationFrame"));
        });
    }

    [Theory]
    [InlineData(0, 210, 42)]
    [InlineData(1, 255, 50)]
    [InlineData(2, 300, 56)]
    public void SizePresetsRenderAtExpectedLogicalSize(
        int sizeValue,
        int expectedWidth,
        int expectedHeight)
    {
        RunOnStaThread(() =>
        {
            var size = (RecordingOverlaySize)sizeValue;
            using var form = new RecordingOverlayForm(72, "Ctrl+Space")
            {
                Opacity = 0
            };

            form.ApplySizePreset(size);
            form.ShowStatus(AppStatus.Idle);
            Application.DoEvents();
            StopAnimationTimer(form);

            var scale = Math.Max(form.DeviceDpi, 96) / 96d;
            Assert.Equal(size, form.SizePreset);
            Assert.Equal(
                new Size(expectedWidth, expectedHeight),
                form.LogicalSize);
            Assert.Equal(
                (int)Math.Round(
                    expectedWidth * scale,
                    MidpointRounding.AwayFromZero),
                form.ClientSize.Width);
            Assert.Equal(
                (int)Math.Round(
                    expectedHeight * scale,
                    MidpointRounding.AwayFromZero),
                form.ClientSize.Height);

            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(
                bitmap,
                new Rectangle(Point.Empty, bitmap.Size));
            var center = bitmap.GetPixel(
                bitmap.Width / 2,
                bitmap.Height / 2);
            Assert.True(
                center.R < 45 &&
                center.G < 45 &&
                center.B < 50);
            SaveSizePreviewIfRequested(
                bitmap,
                "ORHOM_OVERLAY_PREVIEW_PATH",
                size);
        });
    }

    [Fact]
    public void IdleBarStaysVisibleAndRendersAtFlowBarSize()
    {
        RunOnStaThread(() =>
        {
            using var form = new RecordingOverlayForm(72, "Ctrl+Space")
            {
                Opacity = 0
            };

            var createParamsProperty = typeof(RecordingOverlayForm).GetProperty(
                "CreateParams",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            var createParams = Assert.IsType<CreateParams>(
                createParamsProperty?.GetValue(form));
            Assert.NotEqual(0, createParams.ExStyle & 0x00000008);
            Assert.NotEqual(0, createParams.ExStyle & 0x08000000);
            Assert.Equal(0, createParams.ExStyle & 0x00000020);
            Assert.True(form.TopMost);

            form.ShowStatus(AppStatus.Starting);
            form.ShowStatus(AppStatus.Recording);
            form.ShowStatus(AppStatus.Idle);
            Application.DoEvents();

            var toggleRequests = 0;
            form.ToggleRequested += (_, _) => toggleRequests++;
            InvokeMouse(form, "OnMouseDown", MouseButtons.Left);
            InvokeMouse(form, "OnMouseUp", MouseButtons.Left);
            form.SetInteractionEnabled(false);
            InvokeMouse(form, "OnMouseDown", MouseButtons.Left);
            InvokeMouse(form, "OnMouseUp", MouseButtons.Left);
            form.SetInteractionEnabled(true);

            var placementCommits = 0;
            form.PlacementCommitted += (_, _) => placementCommits++;
            form.Location = new Point(100_000, 100_000);
            SetPrivateField(form, "_mouseDown", true);
            SetPrivateField(form, "_dragging", true);
            InvokeNonPublic(form, "OnMouseCaptureChanged", EventArgs.Empty);

            var scale = Math.Max(form.DeviceDpi, 96) / 96d;
            Assert.True(form.Visible);
            Assert.Equal(1, toggleRequests);
            Assert.Equal(1, placementCommits);
            Assert.True(
                Screen.FromPoint(Cursor.Position).WorkingArea.Contains(form.Bounds));
            Assert.Equal(
                (int)Math.Round(form.LogicalSize.Width * scale),
                form.ClientSize.Width);
            Assert.Equal(
                (int)Math.Round(form.LogicalSize.Height * scale),
                form.ClientSize.Height);

            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            Assert.True(center.R < 45 && center.G < 45 && center.B < 50);

            var previewPath = Environment.GetEnvironmentVariable(
                "ORHOM_OVERLAY_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(Path.GetFullPath(previewPath))!);
                bitmap.Save(previewPath);
            }

            form.HideOverlay();
            Assert.False(form.Visible);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RecordingActionsHaveIndependentSafeHitTargets(
        int sizeValue)
    {
        RunOnStaThread(() =>
        {
            var size = (RecordingOverlaySize)sizeValue;
            using var form = new RecordingOverlayForm(72, "Ctrl+Space")
            {
                Opacity = 0
            };
            form.ApplySizePreset(size);
            var toggleRequests = 0;
            var abortRequests = 0;
            form.ToggleRequested += (_, _) => toggleRequests++;
            form.AbortRequested += (_, _) => abortRequests++;

            form.ShowStatus(AppStatus.Recording);
            Application.DoEvents();

            Assert.True(form.RecordingStopActionBounds.Left >= 0);
            Assert.True(form.RecordingStopActionBounds.Top >= 0);
            Assert.True(
                form.RecordingStopActionBounds.Right <=
                form.LogicalSize.Width);
            Assert.True(
                form.RecordingStopActionBounds.Bottom <=
                form.LogicalSize.Height);
            Assert.True(form.RecordingStopActionBounds.Width >= 70);
            Assert.True(form.RecordingStopActionBounds.Height >= 32);
            Assert.True(
                form.RecordingAbortActionBounds.Left >
                form.RecordingStopActionBounds.Right);
            Assert.True(
                form.RecordingAbortActionBounds.Right <=
                form.LogicalSize.Width);
            Assert.True(
                form.RecordingAbortActionBounds.Bottom <=
                form.LogicalSize.Height);
            Assert.True(form.RecordingAbortActionBounds.Width >= 30);
            Assert.True(form.RecordingAbortActionBounds.Height >= 32);

            var scale = Math.Max(form.DeviceDpi, 96) / 96f;
            ClickAt(
                form,
                GetScaledCenter(
                    form.RecordingStopActionBounds,
                    scale));
            ClickAt(
                form,
                GetScaledCenter(
                    form.RecordingAbortActionBounds,
                    scale));
            ClickAt(form, new Point((int)(90 * scale), (int)(28 * scale)));

            Assert.Equal(1, toggleRequests);
            Assert.Equal(1, abortRequests);
            Assert.Contains("Stopp", form.AccessibleDescription);
            Assert.Contains("verwirft", form.AccessibleDescription);

            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(
                bitmap,
                new Rectangle(Point.Empty, bitmap.Size));
            SaveSizePreviewIfRequested(
                bitmap,
                "ORHOM_OVERLAY_RECORDING_PREVIEW_PATH",
                size);

            form.SetInteractionEnabled(false);
            ClickAt(
                form,
                GetScaledCenter(
                    form.RecordingStopActionBounds,
                    scale));
            ClickAt(
                form,
                GetScaledCenter(
                    form.RecordingAbortActionBounds,
                    scale));

            Assert.Equal(1, toggleRequests);
            Assert.Equal(1, abortRequests);
        });
    }

    [Fact]
    public void StatusRefreshReassertsNativeTopMostOrderWithoutTakingFocus()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new RecordingOverlayForm(72, "Ctrl+Space")
            {
                Opacity = 0
            };
            overlay.ShowStatus(AppStatus.Idle);
            Application.DoEvents();
            StopAnimationTimer(overlay);

            using var competingTopMostWindow = new Form
            {
                Bounds = overlay.Bounds,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true
            };
            competingTopMostWindow.Show();
            Application.DoEvents();
            Assert.True(NativeMethods.ReassertWindowTopMost(
                competingTopMostWindow.Handle));
            Application.DoEvents();
            Assert.True(IsAbove(
                competingTopMostWindow.Handle,
                overlay.Handle));

            var activeWindowBeforeRefresh = GetActiveWindow();
            overlay.ShowStatus(AppStatus.Idle);
            Application.DoEvents();

            Assert.True(IsAbove(
                overlay.Handle,
                competingTopMostWindow.Handle));
            Assert.Equal(
                activeWindowBeforeRefresh,
                GetActiveWindow());
        });
    }

    [Fact]
    public void OcclusionProbeReassertsNativeTopMostOrderWithoutTakingFocus()
    {
        RunOnStaThread(() =>
        {
            using var overlay = new RecordingOverlayForm(72, "Ctrl+Space")
            {
                Opacity = 0.01
            };
            overlay.ShowStatus(AppStatus.Idle);
            Application.DoEvents();
            StopAnimationTimer(overlay);

            using var competingTopMostWindow = new Form
            {
                Bounds = overlay.Bounds,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0.01,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                TopMost = true
            };
            competingTopMostWindow.Show();
            Application.DoEvents();
            Assert.True(NativeMethods.ReassertWindowTopMost(
                competingTopMostWindow.Handle));
            Application.DoEvents();
            Assert.True(IsAbove(
                competingTopMostWindow.Handle,
                overlay.Handle));
            Assert.True(NativeMethods.IsWindowCoveredAtProbePoints(
                overlay.Handle));

            var foregroundBeforeRefresh = NativeMethods.GetForegroundWindow();
            var activeWindowBeforeRefresh = GetActiveWindow();
            SetPrivateField(
                overlay,
                "_lastForegroundWindow",
                foregroundBeforeRefresh);
            SetPrivateField(overlay, "_refreshTick", 0);
            for (var tick = 0; tick < 4; tick++)
            {
                InvokeParameterless(overlay, "OnRefreshTimerTick");
            }

            Assert.True(IsAbove(
                overlay.Handle,
                competingTopMostWindow.Handle));
            Assert.Equal(
                activeWindowBeforeRefresh,
                GetActiveWindow());
        });
    }

    private static void InvokeMouse(
        RecordingOverlayForm form,
        string methodName,
        MouseButtons button,
        Point? location = null)
    {
        var clickLocation = location ?? new Point(20, 20);
        var method = typeof(RecordingOverlayForm).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(
            form,
            [
                new MouseEventArgs(
                    button,
                    1,
                    clickLocation.X,
                    clickLocation.Y,
                    0)
            ]);
    }

    private static void ClickAt(
        RecordingOverlayForm form,
        Point location)
    {
        InvokeMouse(form, "OnMouseDown", MouseButtons.Left, location);
        InvokeMouse(form, "OnMouseUp", MouseButtons.Left, location);
    }

    private static Point GetScaledCenter(
        RectangleF bounds,
        float scale) =>
        new(
            (int)Math.Round(
                (bounds.Left + bounds.Width / 2) * scale,
                MidpointRounding.AwayFromZero),
            (int)Math.Round(
                (bounds.Top + bounds.Height / 2) * scale,
                MidpointRounding.AwayFromZero));

    private static void SaveSizePreviewIfRequested(
        Bitmap bitmap,
        string baseVariable,
        RecordingOverlaySize size)
    {
        var specificVariable =
            $"{baseVariable[..^"_PATH".Length]}_{size.ToString().ToUpperInvariant()}_PATH";
        var previewPath = Environment.GetEnvironmentVariable(
            specificVariable);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            previewPath = Environment.GetEnvironmentVariable(baseVariable);
            if (string.IsNullOrWhiteSpace(previewPath))
            {
                return;
            }

            var sizeName = size.ToString().ToLowerInvariant();
            if (previewPath.Contains(
                    "{size}",
                    StringComparison.OrdinalIgnoreCase))
            {
                previewPath = previewPath.Replace(
                    "{size}",
                    sizeName,
                    StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var fullPath = Path.GetFullPath(previewPath);
                previewPath = Path.Combine(
                    Path.GetDirectoryName(fullPath)!,
                    $"{Path.GetFileNameWithoutExtension(fullPath)}-{sizeName}" +
                    Path.GetExtension(fullPath));
            }
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(previewPath))!);
        bitmap.Save(previewPath);
    }

    private static void InvokeNonPublic(
        RecordingOverlayForm form,
        string methodName,
        EventArgs eventArgs)
    {
        var method = typeof(RecordingOverlayForm).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(form, [eventArgs]);
    }

    private static void InvokeParameterless(
        RecordingOverlayForm form,
        string methodName)
    {
        var method = typeof(RecordingOverlayForm).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(form, null);
    }

    private static void SetPrivateField(
        RecordingOverlayForm form,
        string fieldName,
        object? value)
    {
        var field = typeof(RecordingOverlayForm).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(form, value);
    }

    private static T GetPrivateField<T>(
        RecordingOverlayForm form,
        string fieldName)
    {
        var field = typeof(RecordingOverlayForm).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(form));
    }

    private static void StopAnimationTimer(RecordingOverlayForm form)
    {
        GetAnimationTimer(form).Stop();
    }

    private static System.Windows.Forms.Timer GetAnimationTimer(
        RecordingOverlayForm form)
    {
        var field = typeof(RecordingOverlayForm).GetField(
            "_animationTimer",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<System.Windows.Forms.Timer>(
            field?.GetValue(form));
    }

    private static bool IsAbove(IntPtr expectedHigher, IntPtr expectedLower)
    {
        var current = GetWindow(expectedHigher, 0);
        while (current != IntPtr.Zero)
        {
            if (current == expectedHigher)
            {
                return true;
            }

            if (current == expectedLower)
            {
                return false;
            }

            current = GetWindow(current, 2);
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA overlay test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
