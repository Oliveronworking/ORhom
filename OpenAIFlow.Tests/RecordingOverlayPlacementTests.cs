using System.Drawing;
using System.Runtime.ExceptionServices;

namespace ChatGptDictationBridge.Tests;

public sealed class RecordingOverlayPlacementTests
{
    private static readonly Size OverlaySize = new(216, 48);

    [Fact]
    public void DefaultPlacementMatchesCompactBottomCenteredBar()
    {
        var location = RecordingOverlayPlacementCalculator.GetDefaultLocation(
            new Rectangle(0, 0, 1920, 1040),
            OverlaySize,
            72);

        Assert.Equal(new Point(852, 920), location);
    }

    [Fact]
    public void DefaultPlacementSupportsMonitorLeftOfPrimaryScreen()
    {
        var location = RecordingOverlayPlacementCalculator.GetDefaultLocation(
            new Rectangle(-1920, 0, 1920, 1040),
            OverlaySize,
            72);

        Assert.Equal(new Point(-1068, 920), location);
    }

    [Fact]
    public void RelativePlacementRestoresAtRightBottomEdge()
    {
        var location = RecordingOverlayPlacementCalculator.Restore(
            new Rectangle(-1600, -900, 1600, 900),
            OverlaySize,
            1,
            1);

        Assert.Equal(new Point(-216, -48), location);
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

        Assert.Equal(new Point(100, 852), location);
    }

    [Fact]
    public void IdleBarStaysVisibleAndRendersAtCompactSize()
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
                (int)Math.Round(RecordingOverlayForm.LogicalSize.Width * scale),
                form.ClientSize.Width);
            Assert.Equal(
                (int)Math.Round(RecordingOverlayForm.LogicalSize.Height * scale),
                form.ClientSize.Height);

            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            Assert.True(center.R < 45 && center.G < 45 && center.B < 50);

            var previewPath = Environment.GetEnvironmentVariable(
                "OPENAIFLOW_OVERLAY_PREVIEW_PATH");
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
            GetAnimationTimer(overlay).Start();
            var timeoutAt = Environment.TickCount64 + 2_000;
            while (!IsAbove(overlay.Handle, competingTopMostWindow.Handle) &&
                   Environment.TickCount64 < timeoutAt)
            {
                Application.DoEvents();
                Thread.Sleep(10);
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
        MouseButtons button)
    {
        var method = typeof(RecordingOverlayForm).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(form, [new MouseEventArgs(button, 1, 20, 20, 0)]);
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
