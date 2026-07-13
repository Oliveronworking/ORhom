namespace ChatGptDictationBridge;

internal sealed class FocusTracker
{
    private const int LocalReprobeTimeoutMs = 180;
    private readonly AppLogger _logger;
    private int _localReprobeInFlight;

    public FocusTracker(AppLogger logger)
    {
        _logger = logger;
    }

    public FocusTarget Capture(AppSettings settings)
    {
        return CaptureCore(settings.BlockPasswordFields, "Focus captured");
    }

    public async Task<FocusTargetReprobeResult> ReprobeAfterLocalCaptureStartedAsync(
        FocusTarget originalTarget,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _localReprobeInFlight, 1, 0) != 0)
        {
            _logger.Info("Local focus re-probe skipped because an earlier timed-out probe is still running.");
            return new FocusTargetReprobeResult(originalTarget, false, false);
        }

        var blockPasswordFields = settings.BlockPasswordFields;
        var reprobeTask = Task.Run(() =>
        {
            try
            {
                return CaptureCore(
                    blockPasswordFields,
                    "Focus re-probed after local capture start");
            }
            finally
            {
                Interlocked.Exchange(ref _localReprobeInFlight, 0);
            }
        });

        FocusTarget candidate;
        try
        {
            candidate = await reprobeTask.WaitAsync(
                TimeSpan.FromMilliseconds(LocalReprobeTimeoutMs),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.Info($"Local focus re-probe exceeded its deadline and was ignored. TimeoutMs={LocalReprobeTimeoutMs}");
            _ = ObserveLateReprobeAsync(reprobeTask, "timeout");
            return new FocusTargetReprobeResult(originalTarget, false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveLateReprobeAsync(reprobeTask, "cancellation");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Local focus re-probe failed and the original target was retained.", ex);
            return new FocusTargetReprobeResult(originalTarget, false, false);
        }

        var sameWindowIdentity = FocusTargetSafetyPolicy.HasSameWindowIdentity(
            originalTarget.WindowHandle,
            originalTarget.OwningProcessId,
            candidate.WindowHandle,
            candidate.OwningProcessId);
        if (sameWindowIdentity && candidate.IsPasswordField)
        {
            _logger.Info("Local target re-probe found a password field in the captured window; recording will be discarded.");
            return new FocusTargetReprobeResult(originalTarget, true, false);
        }

        if (FocusTargetSafetyPolicy.ShouldPromoteTransientChromiumTarget(
                originalTarget.WindowHandle,
                originalTarget.OwningProcessId,
                originalTarget.WindowClass,
                originalTarget.WindowTitle,
                originalTarget.FocusMetadata,
                originalTarget.WebViewRootIdentity,
                candidate.WindowHandle,
                candidate.OwningProcessId,
                candidate.WindowClass,
                candidate.WindowTitle,
                candidate.FocusMetadata,
                candidate.WebViewRootIdentity,
                candidate.IsPasswordField))
        {
            _logger.Info($"Transient Chromium focus target promoted to a strong semantic editor. ControlType='{candidate.FocusMetadata.ControlType}' Class='{candidate.FocusMetadata.ClassName}' AutomationId='{candidate.FocusMetadata.AutomationId}'");
            return new FocusTargetReprobeResult(candidate, false, true);
        }

        return new FocusTargetReprobeResult(originalTarget, false, false);
    }

    private async Task ObserveLateReprobeAsync(
        Task<FocusTarget> reprobeTask,
        string context)
    {
        try
        {
            _ = await reprobeTask.ConfigureAwait(false);
            _logger.Info($"Late local focus re-probe completed and remained ignored. Context={context}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Late local focus re-probe failed after it had already been ignored. Context={context}", ex);
        }
    }

    private FocusTarget CaptureCore(bool blockPasswordFields, string logPrefix)
    {
        var window = NativeMethods.GetForegroundWindow();
        var owningProcessId = window == IntPtr.Zero
            ? 0
            : NativeMethods.GetOwningProcessId(window);
        var element = AutomationHelpers.GetFocusedElement(_logger);
        if (element is not null &&
            !AutomationHelpers.IsElementInWindow(element, window))
        {
            _logger.Info($"{logPrefix} ignored a focused UI Automation element outside the captured foreground window. WindowHandle=0x{window.ToInt64():X}");
            element = null;
        }

        var title = window == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowTitle(window);
        var className = window == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowClass(window);
        var avoidElementFocus = FocusRestorationPolicy.ShouldAvoidAutomationElementFocus(
            className,
            AutomationHelpers.ShouldPreserveWebViewFocus(element));
        var isPassword = blockPasswordFields && AutomationHelpers.IsPasswordElement(element);

        var targetMetadata = AutomationHelpers.GetSafeFocusMetadata(element);
        var webViewRootIdentity = AutomationHelpers.GetSafeWebViewRootIdentity(element);
        var layoutFingerprint = AutomationHelpers.GetSafeFocusLayoutFingerprint(
            element,
            window);
        _logger.Info($"{logPrefix}. WindowHandle=0x{window.ToInt64():X} ProcessId={owningProcessId} Class='{className}' TargetControlType='{targetMetadata.ControlType}' TargetClass='{targetMetadata.ClassName}' TargetAutomationId='{targetMetadata.AutomationId}' WebViewRootIdentified={webViewRootIdentity.Length > 0} LayoutIdentified={layoutFingerprint.IsValid} PreserveWebViewFocus={avoidElementFocus} PasswordTarget={isPassword}");

        return new FocusTarget(
            window,
            owningProcessId,
            title,
            className,
            element,
            targetMetadata,
            webViewRootIdentity,
            layoutFingerprint,
            avoidElementFocus,
            isPassword);
    }
}

internal sealed record FocusTargetReprobeResult(
    FocusTarget Target,
    bool PasswordFieldDetected,
    bool TargetPromoted);
