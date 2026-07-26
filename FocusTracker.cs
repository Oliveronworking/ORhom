namespace ORhom;

internal sealed class FocusTracker
{
    private const int LocalReprobeTimeoutMs = 350;
    private const int CaptureAttemptCount = 3;
    private const int CaptureRetryDelayMs = 15;
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
        if (sameWindowIdentity && candidate.IsPasswordFieldOrUnverifiable)
        {
            _logger.Info("Local target re-probe found a password field or could not verify the field safely; recording will be discarded.");
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
                candidate.IsPasswordFieldOrUnverifiable))
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
        var fallbackWindow = IntPtr.Zero;
        var fallbackProcessId = 0U;
        var hasCoherentFallback = false;
        var consecutiveMissingElements = 0;

        for (var attempt = 1; attempt <= CaptureAttemptCount; attempt++)
        {
            var windowBefore = NativeMethods.GetForegroundWindow();
            var processBefore = windowBefore == IntPtr.Zero
                ? 0
                : NativeMethods.GetOwningProcessId(windowBefore);
            var candidate = AutomationHelpers.GetFocusedElement(_logger);
            var windowAfter = NativeMethods.GetForegroundWindow();
            var processAfter = windowAfter == IntPtr.Zero
                ? 0
                : NativeMethods.GetOwningProcessId(windowAfter);
            var coherentWindow = windowBefore == windowAfter &&
                                 processBefore == processAfter;
            if (coherentWindow)
            {
                fallbackWindow = windowBefore;
                fallbackProcessId = processBefore;
                hasCoherentFallback = true;
                if (candidate is not null &&
                    AutomationHelpers.IsElementInWindow(candidate, windowBefore))
                {
                    return CreateTarget(
                        windowBefore,
                        processBefore,
                        candidate,
                        blockPasswordFields,
                        logPrefix);
                }

                consecutiveMissingElements++;
                if (candidate is not null)
                {
                    _logger.Info($"{logPrefix} retrying because UI Automation reported an element outside the stable foreground window. Attempt={attempt} WindowHandle=0x{windowBefore.ToInt64():X}");
                }

                if (consecutiveMissingElements >= 2)
                {
                    return CreateTarget(
                        windowBefore,
                        processBefore,
                        element: null,
                        blockPasswordFields,
                        logPrefix);
                }
            }
            else
            {
                consecutiveMissingElements = 0;
                _logger.Info($"{logPrefix} retrying because the foreground window changed during the UI Automation sample. Attempt={attempt} Before=0x{windowBefore.ToInt64():X} After=0x{windowAfter.ToInt64():X}");
            }

            if (attempt < CaptureAttemptCount)
            {
                Thread.Sleep(CaptureRetryDelayMs);
            }
        }

        if (!hasCoherentFallback)
        {
            fallbackWindow = NativeMethods.GetForegroundWindow();
            fallbackProcessId = fallbackWindow == IntPtr.Zero
                ? 0
                : NativeMethods.GetOwningProcessId(fallbackWindow);
        }

        return CreateTarget(
            fallbackWindow,
            fallbackProcessId,
            element: null,
            blockPasswordFields,
            logPrefix);
    }

    private FocusTarget CreateTarget(
        IntPtr window,
        uint owningProcessId,
        System.Windows.Automation.AutomationElement? element,
        bool blockPasswordFields,
        string logPrefix)
    {
        var title = window == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowTitle(window);
        var className = window == IntPtr.Zero ? string.Empty : NativeMethods.GetWindowClass(window);
        var avoidElementFocus = FocusRestorationPolicy.ShouldAvoidAutomationElementFocus(
            className,
            AutomationHelpers.ShouldPreserveWebViewFocus(element));
        var passwordFieldState = blockPasswordFields
            ? AutomationHelpers.GetPasswordFieldState(element)
            : PasswordFieldState.NotPassword;
        var isPasswordFieldOrUnverifiable = AutomationHelpers.ShouldBlockPasswordField(
            passwordFieldState,
            blockPasswordFields);

        var targetMetadata = AutomationHelpers.GetSafeFocusMetadata(element);
        var webViewRootIdentity = AutomationHelpers.GetSafeWebViewRootIdentity(element);
        var layoutFingerprint = AutomationHelpers.GetSafeFocusLayoutFingerprint(
            element,
            window);
        _logger.Info($"{logPrefix}. WindowHandle=0x{window.ToInt64():X} ProcessId={owningProcessId} Class='{className}' TargetControlType='{targetMetadata.ControlType}' TargetClass='{targetMetadata.ClassName}' TargetAutomationId='{targetMetadata.AutomationId}' WebViewRootIdentified={webViewRootIdentity.Length > 0} LayoutIdentified={layoutFingerprint.IsValid} PreserveWebViewFocus={avoidElementFocus} PasswordFieldState={passwordFieldState} PasswordTargetBlocked={isPasswordFieldOrUnverifiable}");

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
            isPasswordFieldOrUnverifiable);
    }
}

internal sealed record FocusTargetReprobeResult(
    FocusTarget Target,
    bool PasswordFieldDetected,
    bool TargetPromoted);
