using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal sealed class ChatGptDictationController : IDisposable
{
    private static readonly string[] DictationStartMarkers =
    [
        "microphone", "mikrofon", "dictate", "diktieren", "diktat starten",
        "diktierung starten", "spracheingabe", "voice input", "start voice input",
        "start dictation"
    ];

    private static readonly string[] RecordingMarkers =
    [
        "stop dictation", "stop recording", "stop listening", "finish dictation",
        "diktat beenden", "aufnahme stoppen", "zuhören beenden", "listening",
        "recording", "wird aufgenommen", "höre zu", "dictating", "diktat abbrechen",
        "diktat absenden", "submit dictation", "cancel dictation"
    ];

    private static readonly string[] VoiceModeMarkers =
    [
        "voice mode", "advanced voice", "voice conversation", "audio conversation",
        "sprachmodus", "sprachunterhaltung", "audiomodus"
    ];

    private static readonly string[] CancelMarkers = ["cancel", "abbrechen", "verwerfen"];
    private static readonly string[] ConfirmMarkers = ["done", "finish", "accept", "submit", "fertig", "bestätigen", "übernehmen", "absenden"];

    private readonly AppSettings _settings;
    private readonly AppLogger _logger;
    private readonly ChromeProfileLauncher _profileLauncher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IntPtr _chatWindow;
    private System.Windows.Rect? _recordingComposerRect;

    public ChatGptDictationController(AppSettings settings, AppLogger logger, ChromeProfileLauncher profileLauncher)
    {
        _settings = settings;
        _logger = logger;
        _profileLauncher = profileLauncher;
    }

    public IntPtr KnownChatWindow
    {
        get
        {
            if (NativeMethods.IsWindow(_chatWindow))
            {
                return _chatWindow;
            }

            _chatWindow = ChatGptWindowFinder.FindOwnedBackgroundWindow(_settings);
            return _chatWindow;
        }
    }

    public ChromeProfileValidationResult ValidateConfiguredProfile() => _profileLauncher.ValidateConfiguredProfile();

    public async Task<ChatGptReadyResult> ResetChatGptPageAsync(IntPtr excludedWindow)
    {
        await _gate.WaitAsync();
        try
        {
            var validation = _profileLauncher.ValidateConfiguredProfile();
            if (!validation.IsValid)
            {
                return ChatGptReadyResult.Fail(ChatGptFailure.ProfileNotFound, MessageFor(ChatGptFailure.ProfileNotFound));
            }

            var chatWindow = KnownChatWindow;
            if (chatWindow == IntPtr.Zero)
            {
                chatWindow = await LaunchConfiguredWindowCoreAsync(excludedWindow);
            }

            if (chatWindow == IntPtr.Zero ||
                !ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger) ||
                !TryNavigateWindow(chatWindow, _settings.ChatGptUrl))
            {
                return ChatGptReadyResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
            }

            var deadline = Environment.TickCount64 + 12000;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(250);
                var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
                if (AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings))
                {
                    _logger.Info("ChatGPT background page reset completed and composer is ready.");
                    return ChatGptReadyResult.Success(chatWindow, input!);
                }
            }

            return ChatGptReadyResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT background page reset failed.", ex);
            return ChatGptReadyResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
        }
        finally
        {
            HideBackgroundWindow();
            _gate.Release();
        }
    }

    public async Task<ChatGptReadyResult> EnsureChatGptReadyAsync(IntPtr excludedWindow)
    {
        await _gate.WaitAsync();
        try
        {
            return await EnsureChatGptReadyCoreAsync(excludedWindow);
        }
        finally
        {
            HideBackgroundWindow();
            _gate.Release();
        }
    }

    public async Task<ChatGptStartResult> StartDictationAsync(IntPtr excludedWindow)
    {
        await _gate.WaitAsync();
        var recordingConfirmed = false;
        var attemptedWindow = IntPtr.Zero;
        System.Windows.Rect? attemptedComposerRect = null;
        AutomationElement? attemptedInput = null;
        try
        {
            var ready = await EnsureChatGptReadyCoreAsync(excludedWindow);
            if (!ready.Ok)
            {
                return ChatGptStartResult.Fail(ready.Failure, ready.Message);
            }

            var chatWindow = ready.ChatWindow;
            var input = ready.Input;
            attemptedWindow = chatWindow;
            attemptedInput = input;
            if (!AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings))
            {
                return ChatGptStartResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
            }

            var oldTextLength = AutomationHelpers.ReadText(input, _logger).Trim().Length;
            if (oldTextLength > 0)
            {
                _logger.Info($"Clearing pre-existing ChatGPT composer text before dictation. TextLength={oldTextLength}");
                if (!await AutomationHelpers.ClearTextAndConfirmAsync(input, chatWindow, _settings, _logger))
                {
                    return ChatGptStartResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
                }

                input = AutomationHelpers.FocusChatGptInput(chatWindow, _settings, _logger);
                if (!AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings))
                {
                    return ChatGptStartResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
                }
            }

            var composerRect = input!.Current.BoundingRectangle;
            attemptedComposerRect = composerRect;
            var dictationButton = FindDictationStartButton(chatWindow, composerRect);
            var triggered = dictationButton is not null && TryInvokeButton(dictationButton, chatWindow);
            var method = triggered ? "ComposerButton" : "None";
            if (dictationButton is null)
            {
                triggered = TrySendShortcutFallback(chatWindow, input);
                method = triggered ? "ShortcutFallback" : "None";
            }
            else if (!triggered)
            {
                _logger.Info("ChatGPT composer dictation button was found but could not be invoked; shortcut fallback was not sent.");
            }

            var confirmationStartedAt = Environment.TickCount64;
            var confirmationTimeoutMs = GetRecordingStateTimeoutMs();
            var firstAttemptTimeoutMs = Math.Min(confirmationTimeoutMs, 1400);
            var recordingActive = triggered &&
                                  await PollForRecordingStateAsync(
                                      chatWindow,
                                      composerRect,
                                      RecordingUiState.Active,
                                      firstAttemptTimeoutMs);
            if (triggered && !recordingActive)
            {
                _ = ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger);
                input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger) ?? input;
                var retryRect = TryGetBoundingRectangle(input) ?? composerRect;
                var retryButton = FindDictationStartButton(chatWindow, retryRect);
                var retryState = DetectRecordingState(chatWindow, retryRect);
                var retryTriggered = retryState == RecordingUiState.Inactive &&
                                     retryButton is not null &&
                                     TryInvokeButton(retryButton, chatWindow);
                if (retryTriggered)
                {
                    _logger.Info("ChatGPT dictation start was not confirmed quickly; retrying the composer control once.");
                    method = "ComposerButtonRetry";
                    composerRect = retryRect;
                }

                var remainingTimeoutMs = GetRemainingTimeoutMs(
                    confirmationStartedAt,
                    confirmationTimeoutMs,
                    minimumMs: 250);
                recordingActive = await PollForRecordingStateAsync(
                    chatWindow,
                    composerRect,
                    RecordingUiState.Active,
                    remainingTimeoutMs);
            }

            if (!recordingActive)
            {
                _logger.Info($"ChatGPT dictation start was not confirmed. TriggerMethod={method}");
                LogUiState(chatWindow, "start-failed");
                var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                    chatWindow,
                    composerRect,
                    "start-failed");
                return ChatGptStartResult.Fail(
                    ChatGptFailure.StartFailed,
                    MessageFor(ChatGptFailure.StartFailed),
                    cleanup.IsTerminationConfirmed,
                    chatWindow,
                    input);
            }

            input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger) ?? input;
            _recordingComposerRect = composerRect;
            recordingConfirmed = true;
            _logger.Info($"ChatGPT dictation start confirmed. TriggerMethod={method} ChatWindow=0x{chatWindow.ToInt64():X}");
            return ChatGptStartResult.Success(chatWindow, input);
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT dictation start failed.", ex);
            var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                attemptedWindow,
                attemptedComposerRect,
                "start-exception");
            return ChatGptStartResult.Fail(
                ChatGptFailure.StartFailed,
                MessageFor(ChatGptFailure.StartFailed),
                cleanup.IsTerminationConfirmed,
                attemptedWindow,
                attemptedInput);
        }
        finally
        {
            if (!recordingConfirmed)
            {
                HideBackgroundWindow();
            }
            _gate.Release();
        }
    }

    public async Task<ChatGptStopResult> StopDictationAsync(IntPtr chatWindow)
    {
        await _gate.WaitAsync();
        var leavePreparedForRead = false;
        try
        {
            var stopRequestedAt = Environment.TickCount64;
            var stopGracePeriodMs = Math.Max(_settings.DictationStopGracePeriodMs, 0);

            if (chatWindow == IntPtr.Zero || !NativeMethods.IsWindow(chatWindow) ||
                !ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
            {
                var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                    chatWindow,
                    _recordingComposerRect,
                    "stop-window-unavailable",
                    deferUnknownForRecovery: true);
                return ChatGptStopResult.Fail(
                    ChatGptFailure.StopFailed,
                    MessageFor(ChatGptFailure.StopFailed),
                    cleanup.CanRecoverText,
                    cleanup.RequiresDeferredCleanup,
                    cleanup.IsTerminationConfirmed);
            }

            var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
            var composerRect = _recordingComposerRect ?? TryGetBoundingRectangle(input);
            if (composerRect is null)
            {
                var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                    chatWindow,
                    null,
                    "stop-composer-unavailable",
                    deferUnknownForRecovery: true);
                return ChatGptStopResult.Fail(
                    ChatGptFailure.InputNotFound,
                    MessageFor(ChatGptFailure.InputNotFound),
                    cleanup.CanRecoverText,
                    cleanup.RequiresDeferredCleanup,
                    cleanup.IsTerminationConfirmed);
            }

            var remainingGraceMs = stopGracePeriodMs - (Environment.TickCount64 - stopRequestedAt);
            if (remainingGraceMs > 0)
            {
                await Task.Delay((int)remainingGraceMs);
            }

            var stopButton = FindDictationStopButton(chatWindow, composerRect.Value);
            var triggered = stopButton is not null && TryInvokeButton(stopButton, chatWindow);
            var method = triggered ? "ComposerStopButton" : "None";
            if (stopButton is null)
            {
                triggered = AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings)
                    ? TrySendShortcutFallback(chatWindow, input)
                    : TrySendConfirmedRecordingShortcutFallback(chatWindow);
                method = triggered ? "ShortcutFallback" : "None";
            }
            else if (!triggered)
            {
                _logger.Info("ChatGPT dictation stop button was found but could not be invoked; shortcut fallback was not sent.");
            }

            var confirmationStartedAt = Environment.TickCount64;
            var confirmationTimeoutMs = GetRecordingStateTimeoutMs();
            var firstAttemptTimeoutMs = Math.Min(confirmationTimeoutMs, 1800);
            var recordingStopped = triggered && await PollForRecordingStateAsync(
                chatWindow,
                composerRect.Value,
                RecordingUiState.Inactive,
                firstAttemptTimeoutMs);
            if (triggered && !recordingStopped)
            {
                input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger) ?? input;
                var retryRect = TryGetBoundingRectangle(input) ?? composerRect.Value;
                var retryButton = FindDictationStopButton(chatWindow, retryRect);
                var retryState = DetectRecordingState(chatWindow, retryRect);
                if (retryState == RecordingUiState.Active &&
                    retryButton is not null &&
                    TryInvokeButton(retryButton, chatWindow))
                {
                    method = "ComposerStopButtonRetry";
                    composerRect = retryRect;
                    _logger.Info("ChatGPT dictation stop was not confirmed quickly; retrying the stop control once.");
                }

                var remainingTimeoutMs = GetRemainingTimeoutMs(
                    confirmationStartedAt,
                    confirmationTimeoutMs,
                    minimumMs: 250);
                recordingStopped = await PollForRecordingStateAsync(
                    chatWindow,
                    composerRect.Value,
                    RecordingUiState.Inactive,
                    remainingTimeoutMs);
            }

            if (!triggered || !recordingStopped)
            {
                _logger.Info($"ChatGPT dictation stop was not confirmed. TriggerMethod={method}");
                LogUiState(chatWindow, "stop-failed");
                var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                    chatWindow,
                    composerRect,
                    "stop-failed",
                    deferUnknownForRecovery: true);
                return ChatGptStopResult.Fail(
                    ChatGptFailure.StopFailed,
                    MessageFor(ChatGptFailure.StopFailed),
                    cleanup.CanRecoverText,
                    cleanup.RequiresDeferredCleanup,
                    cleanup.IsTerminationConfirmed);
            }

            if (_settings.DictationSettleDelayMs > 0)
            {
                await Task.Delay(_settings.DictationSettleDelayMs);
            }
            _recordingComposerRect = null;
            leavePreparedForRead = true;
            _logger.Info($"ChatGPT dictation stop confirmed. TriggerMethod={method} SettleDelayMs={Math.Max(_settings.DictationSettleDelayMs, 0)}");
            return ChatGptStopResult.Success();
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT dictation stop failed.", ex);
            var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                chatWindow,
                _recordingComposerRect,
                "stop-exception",
                deferUnknownForRecovery: true);
            return ChatGptStopResult.Fail(
                ChatGptFailure.StopFailed,
                MessageFor(ChatGptFailure.StopFailed),
                cleanup.CanRecoverText,
                cleanup.RequiresDeferredCleanup,
                cleanup.IsTerminationConfirmed);
        }
        finally
        {
            if (!leavePreparedForRead)
            {
                HideBackgroundWindow();
            }
            _gate.Release();
        }
    }

    public async Task<ChatGptDictationReadResult> ReadDictatedTextAsync(
        IntPtr chatWindow,
        AutomationElement? preferredInput = null,
        int? timeoutOverrideMs = null)
    {
        await _gate.WaitAsync();
        try
        {
            var result = await AutomationHelpers.ReadChatGptTextRobustlyAsync(
                chatWindow,
                _settings,
                _logger,
                windowAlreadyPrepared: true,
                preferredInput: preferredInput,
                timeoutOverrideMs: timeoutOverrideMs);
            if (result.Text.Length > 0 && result.Input is not null)
            {
                var cleared = await AutomationHelpers.ClearTextAndConfirmAsync(
                    result.Input,
                    chatWindow,
                    _settings,
                    _logger);
                if (!cleared)
                {
                    _logger.Info("ChatGPT composer cleanup was not confirmed; resetting the background page.");
                    _ = TryNavigateWindow(chatWindow, _settings.ChatGptUrl);
                }
            }

            return result;
        }
        finally
        {
            HideBackgroundWindow();
            _gate.Release();
        }
    }

    public async Task AbortDictationAsync(IntPtr chatWindow)
    {
        try
        {
            var result = await StopDictationAsync(chatWindow);
            _logger.Info($"ChatGPT dictation abort stop attempt completed. Success={result.Ok} Failure={result.Failure}");
        }
        finally
        {
            HideBackgroundWindow();
        }
    }

    public async Task<bool> CompleteDeferredStopCleanupAsync(IntPtr chatWindow)
    {
        await _gate.WaitAsync();
        try
        {
            var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                chatWindow,
                _recordingComposerRect,
                "deferred-stop-cleanup");
            return cleanup.IsTerminationConfirmed;
        }
        finally
        {
            HideBackgroundWindow();
            _gate.Release();
        }
    }

    public async Task<bool> TerminateRecordingForShutdownAsync(IntPtr chatWindow)
    {
        await _gate.WaitAsync();
        try
        {
            var window = chatWindow != IntPtr.Zero ? chatWindow : KnownChatWindow;
            var cleanup = await EnsureRecordingTerminatedAfterFailureAsync(
                window,
                _recordingComposerRect,
                "application-exit");
            return cleanup.IsTerminationConfirmed;
        }
        finally
        {
            HideBackgroundWindow();
            _gate.Release();
        }
    }

    public async Task<ChatGptWindowResult> PrepareBackgroundWindowAsync(IntPtr excludedWindow)
    {
        await _gate.WaitAsync();
        try
        {
            var validation = _profileLauncher.ValidateConfiguredProfile();
            if (!validation.IsValid)
            {
                return ChatGptWindowResult.Fail(ChatGptFailure.ProfileNotFound, MessageFor(ChatGptFailure.ProfileNotFound));
            }

            var window = KnownChatWindow;
            if (window == IntPtr.Zero)
            {
                window = await LaunchConfiguredWindowCoreAsync(excludedWindow);
            }

            if (window == IntPtr.Zero)
            {
                return ChatGptWindowResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
            }

            ChatGptWindowFinder.MinimizeBackgroundWindow(window, _settings, _logger);
            return ChatGptWindowResult.Success(window);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatGptWindowResult> OpenConfiguredProfileAsync(IntPtr excludedWindow)
    {
        await _gate.WaitAsync();
        try
        {
            var validation = _profileLauncher.ValidateConfiguredProfile();
            if (!validation.IsValid)
            {
                return ChatGptWindowResult.Fail(ChatGptFailure.ProfileNotFound, MessageFor(ChatGptFailure.ProfileNotFound));
            }

            var window = KnownChatWindow;
            if (window == IntPtr.Zero)
            {
                window = await LaunchConfiguredWindowCoreAsync(excludedWindow);
            }

            if (window == IntPtr.Zero)
            {
                return ChatGptWindowResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
            }

            return ChatGptWindowFinder.ShowForSetup(window, _logger)
                ? ChatGptWindowResult.Success(window)
                : ChatGptWindowResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DiagnoseChatGptUiAsync(IntPtr preferredWindow)
    {
        await _gate.WaitAsync();
        try
        {
            var validation = _profileLauncher.ValidateConfiguredProfile();
            _logger.Info($"ChatGPT diagnostics: BrowserProfileMode='{_settings.BrowserProfileMode}' UserDataDir='{_settings.ChromeUserDataDir}' ProfileDirectory='{_settings.ChromeProfileDirectory}' ProfileFound={validation.IsValid} FailureReason='{validation.FailureReason}'");

            var chatWindow = NativeMethods.IsWindow(preferredWindow)
                ? preferredWindow
                : KnownChatWindow;
            _logger.Info($"ChatGPT diagnostics: WindowFound={chatWindow != IntPtr.Zero} WindowHandle=0x{chatWindow.ToInt64():X}");
            if (chatWindow == IntPtr.Zero)
            {
                return;
            }

            _ = ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger);

            var loginStatus = ChatGptLoginDetector.Detect(chatWindow, _settings, _logger);
            _logger.Info($"ChatGPT diagnostics: LoginStatus={loginStatus}");

            var candidates = AutomationHelpers.FindChatGptInputCandidates(chatWindow, _logger);
            _logger.Info($"ChatGPT diagnostics: InputCandidateCount={candidates.Count}");
            for (var index = 0; index < candidates.Count; index++)
            {
                LogInputCandidate(index + 1, candidates[index], chatWindow);
            }

            var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
            var composerRect = TryGetBoundingRectangle(input) ?? _recordingComposerRect;
            var microphoneCandidates = composerRect is null ? [] : FindComposerButtons(chatWindow, composerRect.Value)
                .Where(button => IsDictationStartDescriptor(GetDescriptor(button)) || IsRecordingDescriptor(GetDescriptor(button)))
                .ToList();
            _logger.Info($"ChatGPT diagnostics: MicrophoneCandidateCount={microphoneCandidates.Count}");
            for (var index = 0; index < microphoneCandidates.Count; index++)
            {
                LogButtonCandidate(index + 1, microphoneCandidates[index]);
            }

            var recordingState = composerRect is null
                ? RecordingUiState.Unknown
                : DetectRecordingState(chatWindow, composerRect.Value);
            _logger.Info($"ChatGPT diagnostics: RecordingState={recordingState}");
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT UI diagnostics failed.", ex);
        }
        finally
        {
            HideBackgroundWindow();
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<ChatGptReadyResult> EnsureChatGptReadyCoreAsync(IntPtr excludedWindow)
    {
        var validation = _profileLauncher.ValidateConfiguredProfile();
        if (!validation.IsValid)
        {
            return ChatGptReadyResult.Fail(ChatGptFailure.ProfileNotFound, MessageFor(ChatGptFailure.ProfileNotFound));
        }

        var chatWindow = KnownChatWindow;
        if (chatWindow == IntPtr.Zero)
        {
            if (!_settings.LaunchChatGptIfMissing)
            {
                return ChatGptReadyResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
            }

            chatWindow = await LaunchConfiguredWindowCoreAsync(excludedWindow);
        }

        if (chatWindow == IntPtr.Zero || !ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
        {
            return ChatGptReadyResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
        }

        var input = AutomationHelpers.FocusChatGptInput(chatWindow, _settings, _logger);
        if (!AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings))
        {
            var loginStatus = ChatGptLoginDetector.Detect(chatWindow, _settings, _logger);
            if (loginStatus == ChatGptLoginStatus.LoggedOut)
            {
                _logger.Info("ChatGPT readiness rejected because a logged-out marker is visible.");
                return ChatGptReadyResult.Fail(ChatGptFailure.NotLoggedIn, MessageFor(ChatGptFailure.NotLoggedIn));
            }

            return ChatGptReadyResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
        }

        _logger.Info("ChatGPT readiness confirmed by the safe authenticated composer.");
        return ChatGptReadyResult.Success(chatWindow, input!);
    }

    private async Task<IntPtr> LaunchConfiguredWindowCoreAsync(IntPtr excludedWindow)
    {
        var chatWindow = await ChatGptWindowFinder.LaunchConfiguredProfileAsync(
            _settings,
            _logger,
            _profileLauncher,
            excludedWindow);
        if (chatWindow != IntPtr.Zero)
        {
            _chatWindow = chatWindow;
        }

        return chatWindow;
    }

    private void HideBackgroundWindow()
    {
        var window = KnownChatWindow;
        if (window != IntPtr.Zero)
        {
            ChatGptWindowFinder.MinimizeBackgroundWindow(window, _settings, _logger);
        }
    }

    private bool TrySendShortcutFallback(IntPtr chatWindow, AutomationElement? input)
    {
        if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
        {
            return false;
        }

        _ = AutomationHelpers.FocusKnownChatGptInput(input, chatWindow, _settings, _logger) ??
            AutomationHelpers.FocusChatGptInput(chatWindow, _settings, _logger);
        var actuallyFocused = AutomationHelpers.GetFocusedElement(_logger);
        if (NativeMethods.GetForegroundWindow() != chatWindow ||
            !AutomationHelpers.IsSafeChatGptInput(actuallyFocused, chatWindow, _settings))
        {
            _logger.Info("ChatGPT dictation shortcut fallback skipped because no safe composer is focused.");
            return false;
        }

        KeyboardHelpers.SendHotkey(_settings.ChatGptDictationHotkey);
        _logger.Info($"ChatGPT dictation shortcut fallback sent: {_settings.ChatGptDictationHotkey}");
        return true;
    }

    private bool TryNavigateWindow(IntPtr chatWindow, string url)
    {
        try
        {
            if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
            {
                return false;
            }

            var root = AutomationElement.FromHandle(chatWindow);
            var omnibox = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "view_2001"));
            omnibox ??= root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>()
                .FirstOrDefault(element => element.Current.ClassName.Contains("Omnibox", StringComparison.OrdinalIgnoreCase));
            if (omnibox is null ||
                !omnibox.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
                valueObject is not ValuePattern valuePattern)
            {
                return false;
            }

            valuePattern.SetValue(url);
            if (!AutomationHelpers.TryFocusElement(omnibox, _logger))
            {
                return false;
            }

            var focused = AutomationHelpers.GetFocusedElement(_logger);
            if (NativeMethods.GetForegroundWindow() != chatWindow ||
                !AreSameAutomationElement(focused, omnibox))
            {
                _logger.Info("ChatGPT background navigation skipped because omnibox focus was not confirmed.");
                return false;
            }

            SendKeys.SendWait("{ENTER}");
            _logger.Info("ChatGPT background page navigation requested.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT background page navigation failed.", ex);
            return false;
        }
    }

    private bool TrySendConfirmedRecordingShortcutFallback(IntPtr chatWindow)
    {
        if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
        {
            return false;
        }

        var focused = AutomationHelpers.GetFocusedElement(_logger);
        if (_recordingComposerRect is null ||
            NativeMethods.GetForegroundWindow() != chatWindow ||
            !AutomationHelpers.IsElementInWindow(focused, chatWindow) ||
            AutomationHelpers.LooksLikeUnsafeHotkeyTarget(focused))
        {
            _logger.Info("ChatGPT stop shortcut fallback skipped because the confirmed recording anchor is unavailable or focus is unsafe.");
            return false;
        }

        KeyboardHelpers.SendHotkey(_settings.ChatGptDictationHotkey);
        _logger.Info($"ChatGPT stop shortcut fallback sent after a previously confirmed recording: {_settings.ChatGptDictationHotkey}");
        return true;
    }

    private static bool AreSameAutomationElement(AutomationElement? left, AutomationElement? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        try
        {
            return left.GetRuntimeId().SequenceEqual(right.GetRuntimeId());
        }
        catch
        {
            return false;
        }
    }

    private AutomationElement? FindDictationStartButton(IntPtr chatWindow, System.Windows.Rect composerRect)
    {
        return FindComposerButtons(chatWindow, composerRect)
            .Where(button => IsDictationStartDescriptor(GetDescriptor(button)))
            .OrderByDescending(ScoreButton)
            .FirstOrDefault();
    }

    private AutomationElement? FindDictationStopButton(IntPtr chatWindow, System.Windows.Rect composerRect)
    {
        var buttons = FindComposerButtons(chatWindow, composerRect);
        var descriptors = buttons.Select(button => (Button: button, Descriptor: GetDescriptor(button))).ToList();
        var confirmation = descriptors
            .Where(item => ContainsAny(item.Descriptor, ConfirmMarkers) && !ContainsAny(item.Descriptor, CancelMarkers))
            .OrderByDescending(item => ScoreButton(item.Button))
            .FirstOrDefault();
        if (confirmation.Button is not null)
        {
            return confirmation.Button;
        }

        var explicitStop = buttons
            .Where(button => IsRecordingDescriptor(GetDescriptor(button)) && !ContainsAny(GetDescriptor(button), CancelMarkers))
            .OrderByDescending(ScoreButton)
            .FirstOrDefault();
        if (explicitStop is not null)
        {
            return explicitStop;
        }

        var hasCancel = descriptors.Any(item => ContainsAny(item.Descriptor, CancelMarkers));
        return hasCancel
            ? descriptors.FirstOrDefault(item => ContainsAny(item.Descriptor, ConfirmMarkers)).Button
            : null;
    }

    private AutomationElement? FindCancelButton(IntPtr chatWindow, System.Windows.Rect composerRect)
    {
        return FindComposerButtons(chatWindow, composerRect)
            .Where(button =>
            {
                var descriptor = GetDescriptor(button);
                return ContainsAny(descriptor, CancelMarkers) &&
                       !ContainsAny(descriptor, VoiceModeMarkers);
            })
            .OrderByDescending(ScoreButton)
            .FirstOrDefault();
    }

    private IReadOnlyList<AutomationElement> FindComposerButtons(IntPtr chatWindow, System.Windows.Rect composerRect)
    {
        try
        {
            var root = AutomationElement.FromHandle(chatWindow);
            if (root is null)
            {
                return [];
            }

            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            return buttons.Cast<AutomationElement>()
                .Where(button => IsNearComposer(button, composerRect, chatWindow))
                .Take(80)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Error("Could not enumerate ChatGPT composer buttons.", ex);
            return [];
        }
    }

    private async Task<bool> PollForRecordingStateAsync(
        IntPtr chatWindow,
        System.Windows.Rect composerRect,
        RecordingUiState expected,
        int? timeoutOverrideMs = null)
    {
        var timeoutMs = timeoutOverrideMs is null
            ? GetRecordingStateTimeoutMs()
            : Math.Clamp(timeoutOverrideMs.Value, 100, GetRecordingStateTimeoutMs());
        var pollMs = Math.Clamp(_settings.DictationResultPollIntervalMs, 100, 500);
        var start = Environment.TickCount64;
        var confirmation = new RecordingStateConfirmationTracker(expected);
        while (Environment.TickCount64 - start < timeoutMs)
        {
            var state = DetectRecordingState(chatWindow, composerRect);
            if (confirmation.Observe(state))
            {
                return true;
            }

            var remainingMs = timeoutMs - (Environment.TickCount64 - start);
            if (remainingMs <= 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(pollMs, remainingMs));
        }

        var finalState = DetectRecordingState(chatWindow, composerRect);
        var confirmed = confirmation.Observe(finalState, finalProbe: true);
        if (confirmed)
        {
            _logger.Info($"ChatGPT recording state confirmed by final deadline probe. Expected={expected}");
        }

        return confirmed;
    }

    private int GetRecordingStateTimeoutMs() => Math.Clamp(_settings.RecordingStateTimeoutMs, 3000, 5000);

    private static int GetRemainingTimeoutMs(long startedAt, int totalTimeoutMs, int minimumMs)
    {
        var remainingMs = totalTimeoutMs - (Environment.TickCount64 - startedAt);
        return (int)Math.Max(remainingMs, minimumMs);
    }

    private RecordingUiState DetectRecordingState(IntPtr chatWindow, System.Windows.Rect composerRect)
    {
        var buttons = FindComposerButtons(chatWindow, composerRect);
        var descriptors = buttons.Select(GetDescriptor).ToList();
        if (descriptors.Any(IsRecordingDescriptor))
        {
            return RecordingUiState.Active;
        }

        try
        {
            foreach (var button in buttons)
            {
                if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var patternObject) &&
                    patternObject is TogglePattern togglePattern &&
                    togglePattern.Current.ToggleState == ToggleState.On &&
                    IsDictationStartDescriptor(GetDescriptor(button)))
                {
                    return RecordingUiState.Active;
                }
            }
        }
        catch
        {
            // A stale button is ignored and the remaining state signals are evaluated.
        }

        var hasCancel = descriptors.Any(descriptor => ContainsAny(descriptor, CancelMarkers));
        var hasConfirm = descriptors.Any(descriptor => ContainsAny(descriptor, ConfirmMarkers));
        var hasStart = descriptors.Any(IsDictationStartDescriptor);
        if (hasCancel && hasConfirm && !hasStart)
        {
            return RecordingUiState.Active;
        }

        return hasStart ? RecordingUiState.Inactive : RecordingUiState.Unknown;
    }

    private async Task<RecordingFailureCleanupResult> EnsureRecordingTerminatedAfterFailureAsync(
        IntPtr chatWindow,
        System.Windows.Rect? composerRect,
        string context,
        bool deferUnknownForRecovery = false)
    {
        var cleanupDeferred = false;
        try
        {
            if (chatWindow == IntPtr.Zero || !NativeMethods.IsWindow(chatWindow))
            {
                return RecordingFailureCleanupResult.NotRecoverable;
            }

            if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
            {
                if (deferUnknownForRecovery)
                {
                    cleanupDeferred = true;
                    _logger.Info($"ChatGPT recording cleanup deferred because the window is temporarily unavailable. Context={context}");
                    return RecordingFailureCleanupResult.DeferredRecovery;
                }

                return await CloseBackgroundWindowAfterFailureAsync(chatWindow, context)
                    ? RecordingFailureCleanupResult.NotRecoverable
                    : RecordingFailureCleanupResult.Unconfirmed;
            }

            var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
            composerRect ??= TryGetBoundingRectangle(input) ?? _recordingComposerRect;
            if (composerRect is not null)
            {
                var state = DetectRecordingState(chatWindow, composerRect.Value);
                if (state == RecordingUiState.Inactive)
                {
                    _logger.Info($"ChatGPT recording was already inactive during failure cleanup. Context={context}");
                    return RecordingFailureCleanupResult.Recoverable;
                }

                if (state == RecordingUiState.Active)
                {
                    var cancelButton = FindCancelButton(chatWindow, composerRect.Value);
                    if (cancelButton is not null && TryInvokeButton(cancelButton, chatWindow) &&
                        await PollForRecordingStateAsync(
                            chatWindow,
                            composerRect.Value,
                            RecordingUiState.Inactive,
                            1200))
                    {
                        _logger.Info($"ChatGPT recording cancelled during failure cleanup. Context={context}");
                        return RecordingFailureCleanupResult.NotRecoverable;
                    }
                }
                else if (deferUnknownForRecovery)
                {
                    cleanupDeferred = true;
                    _logger.Info($"ChatGPT recording cleanup deferred while the transcript is in an unknown transition state. Context={context}");
                    return RecordingFailureCleanupResult.DeferredRecovery;
                }
            }
            else if (deferUnknownForRecovery)
            {
                cleanupDeferred = true;
                _logger.Info($"ChatGPT recording cleanup deferred because no composer anchor is currently available. Context={context}");
                return RecordingFailureCleanupResult.DeferredRecovery;
            }

            if (await TryResetPageAndConfirmTerminationAsync(
                    chatWindow,
                    composerRect,
                    context))
            {
                return RecordingFailureCleanupResult.NotRecoverable;
            }

            return await CloseBackgroundWindowAfterFailureAsync(chatWindow, context)
                ? RecordingFailureCleanupResult.NotRecoverable
                : RecordingFailureCleanupResult.Unconfirmed;
        }
        catch (Exception ex)
        {
            _logger.Error($"ChatGPT recording failure cleanup failed. Context={context}", ex);
            return await CloseBackgroundWindowAfterFailureAsync(chatWindow, context)
                ? RecordingFailureCleanupResult.NotRecoverable
                : RecordingFailureCleanupResult.Unconfirmed;
        }
        finally
        {
            if (!cleanupDeferred)
            {
                _recordingComposerRect = null;
            }
        }
    }

    private async Task<bool> TryResetPageAndConfirmTerminationAsync(
        IntPtr chatWindow,
        System.Windows.Rect? composerRect,
        string context)
    {
        if (!TryNavigateWindow(chatWindow, _settings.ChatGptUrl))
        {
            return false;
        }

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            if (!NativeMethods.IsWindow(chatWindow))
            {
                _logger.Info($"ChatGPT window closed after background reset. Context={context}");
                return true;
            }

            await Task.Delay(150);
            if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
            {
                continue;
            }

            var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
            var currentRect = TryGetBoundingRectangle(input) ?? composerRect;
            if (currentRect is not null &&
                DetectRecordingState(chatWindow, currentRect.Value) == RecordingUiState.Inactive)
            {
                _logger.Info($"ChatGPT background page reset confirmed recording inactive. Context={context}");
                return true;
            }
        }

        _logger.Info($"ChatGPT background reset did not confirm recording termination. Context={context}");
        return false;
    }

    private async Task<bool> CloseBackgroundWindowAfterFailureAsync(
        IntPtr chatWindow,
        string context)
    {
        if (chatWindow == IntPtr.Zero || !NativeMethods.IsWindow(chatWindow))
        {
            return true;
        }

        _logger.Info($"Closing ChatGPT background window to terminate an uncertain recording. Context={context}");
        _ = NativeMethods.PostMessage(chatWindow, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
        if (await WaitForWindowClosedAsync(chatWindow, 2000))
        {
            ForgetClosedChatWindow(chatWindow);
            _logger.Info($"ChatGPT background window closure confirmed. Context={context}");
            return true;
        }

        try
        {
            var root = AutomationElement.FromHandle(chatWindow);
            if (root.TryGetCurrentPattern(WindowPattern.Pattern, out var patternObject) &&
                patternObject is WindowPattern windowPattern)
            {
                windowPattern.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"ChatGPT UI Automation window close failed. Context={context}", ex);
        }

        var closed = await WaitForWindowClosedAsync(chatWindow, 1500);
        if (closed)
        {
            ForgetClosedChatWindow(chatWindow);
            _logger.Info($"ChatGPT background window closure confirmed after fallback. Context={context}");
            return true;
        }

        _logger.Info($"ChatGPT background window closure could not be confirmed. Context={context}");
        return false;
    }

    private static async Task<bool> WaitForWindowClosedAsync(IntPtr window, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 0);
        while (Environment.TickCount64 < deadline)
        {
            if (!NativeMethods.IsWindow(window))
            {
                return true;
            }

            await Task.Delay(75);
        }

        return !NativeMethods.IsWindow(window);
    }

    private void ForgetClosedChatWindow(IntPtr chatWindow)
    {
        if (_chatWindow == chatWindow)
        {
            _chatWindow = IntPtr.Zero;
        }
    }

    private bool TryInvokeButton(AutomationElement button, IntPtr chatWindow)
    {
        try
        {
            if (!ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
            {
                _logger.Info("ChatGPT control invocation skipped because the background window could not be secured.");
                return false;
            }

            var rect = button.Current.BoundingRectangle;
            if (rect.Width >= 10 && rect.Height >= 10 && rect.Width <= 100 && rect.Height <= 100)
            {
                var clickX = (int)Math.Round(rect.Left + rect.Width / 2);
                var clickY = (int)Math.Round(rect.Top + rect.Height / 2);
                if (NativeMethods.ClickAt(clickX, clickY, chatWindow))
                {
                    _logger.Info($"ChatGPT composer dictation control clicked physically. X={clickX} Y={clickY}");
                    return true;
                }
            }

            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject) &&
                invokeObject is InvokePattern invokePattern)
            {
                invokePattern.Invoke();
                _logger.Info("ChatGPT composer dictation control invoked via InvokePattern.");
                return true;
            }

            button.SetFocus();
            Thread.Sleep(60);
            var focused = AutomationHelpers.GetFocusedElement(_logger);
            if (NativeMethods.GetForegroundWindow() != chatWindow ||
                !AutomationHelpers.IsElementInWindow(focused, chatWindow))
            {
                _logger.Info("ChatGPT control keyboard invocation skipped because focus left the background window.");
                return false;
            }

            SendKeys.SendWait(" ");
            _logger.Info("ChatGPT composer dictation control invoked via focused Space key.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Could not invoke ChatGPT composer dictation control.", ex);
            return false;
        }
    }

    private static bool IsNearComposer(AutomationElement button, System.Windows.Rect inputRect, IntPtr chatWindow)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(chatWindow, out var windowRect))
            {
                return false;
            }

            var buttonRect = button.Current.BoundingRectangle;
            if (buttonRect.Width < 10 || buttonRect.Height < 10 || buttonRect.Width > 100 || buttonRect.Height > 100)
            {
                return false;
            }

            var centerX = buttonRect.Left + buttonRect.Width / 2;
            var centerY = buttonRect.Top + buttonRect.Height / 2;
            return centerX >= inputRect.Left - 140 &&
                   centerX <= inputRect.Right + 180 &&
                   centerY >= inputRect.Top - 90 &&
                   centerY <= inputRect.Bottom + 90 &&
                   buttonRect.Top >= windowRect.Top + 80;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDescriptor(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            return string.Join(" ", current.Name, current.AutomationId, current.HelpText, current.ClassName).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsDictationStartDescriptor(string descriptor)
    {
        return !ContainsAny(descriptor, VoiceModeMarkers) && ContainsAny(descriptor, DictationStartMarkers);
    }

    private static bool IsRecordingDescriptor(string descriptor)
    {
        return !ContainsAny(descriptor, VoiceModeMarkers) && ContainsAny(descriptor, RecordingMarkers);
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers)
    {
        return markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static double ScoreButton(AutomationElement button)
    {
        try
        {
            var descriptor = GetDescriptor(button);
            var rect = button.Current.BoundingRectangle;
            var score = rect.Bottom;
            if (descriptor.Contains("dictat", StringComparison.OrdinalIgnoreCase) ||
                descriptor.Contains("dikt", StringComparison.OrdinalIgnoreCase))
            {
                score += 5000;
            }

            if (descriptor.Contains("microphone", StringComparison.OrdinalIgnoreCase) ||
                descriptor.Contains("mikrofon", StringComparison.OrdinalIgnoreCase))
            {
                score += 4000;
            }

            return score;
        }
        catch
        {
            return 0;
        }
    }

    private void LogInputCandidate(int index, AutomationElement candidate, IntPtr chatWindow)
    {
        try
        {
            var current = candidate.Current;
            var rect = current.BoundingRectangle;
            var reason = AutomationHelpers.GetChatGptInputSafetyRejectionReason(candidate, chatWindow, _settings);
            _logger.Info($"ChatGPT diagnostics input #{index}: ControlType='{current.ControlType.ProgrammaticName}' Name='<redacted>' NameLength={(current.Name ?? string.Empty).Length} ClassName='{current.ClassName}' Rect={rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0} IsSafeChatGptInput={reason is null} RejectionReason='{reason ?? string.Empty}'");
        }
        catch (Exception ex)
        {
            _logger.Error($"ChatGPT diagnostics input #{index} could not be inspected.", ex);
        }
    }

    private void LogButtonCandidate(int index, AutomationElement button)
    {
        try
        {
            var current = button.Current;
            var rect = current.BoundingRectangle;
            var label = GetDescriptor(button);
            _logger.Info($"ChatGPT diagnostics microphone #{index}: ControlType='{current.ControlType.ProgrammaticName}' Name='{label}' ClassName='{current.ClassName}' Rect={rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}");
        }
        catch (Exception ex)
        {
            _logger.Error($"ChatGPT diagnostics microphone #{index} could not be inspected.", ex);
        }
    }

    private void LogUiState(IntPtr chatWindow, string context)
    {
        var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
        var composerRect = TryGetBoundingRectangle(input) ?? _recordingComposerRect;
        var state = composerRect is null ? RecordingUiState.Unknown : DetectRecordingState(chatWindow, composerRect.Value);
        var buttons = composerRect is null ? [] : FindComposerButtons(chatWindow, composerRect.Value);
        var dictateCount = buttons.Count(button => IsDictationStartDescriptor(GetDescriptor(button)));
        var recordingCount = buttons.Count(button => IsRecordingDescriptor(GetDescriptor(button)));
        _logger.Info($"ChatGPT UI state diagnostic. Context={context} RecordingState={state} SafeInput={AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings)} DictationButtonCount={dictateCount} RecordingControlCount={recordingCount}");
    }

    private static System.Windows.Rect? TryGetBoundingRectangle(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return element.Current.BoundingRectangle;
        }
        catch
        {
            return null;
        }
    }

    private string MessageFor(ChatGptFailure failure)
    {
        return failure switch
        {
            ChatGptFailure.ProfileNotFound => $"Konfiguriertes Chrome-Profil {_settings.ChromeProfileDirectory} nicht gefunden. Bitte settings.json prüfen.",
            ChatGptFailure.WindowNotFound => "ChatGPT-Profil nicht gefunden.",
            ChatGptFailure.NotLoggedIn => "ChatGPT ist nicht angemeldet.",
            ChatGptFailure.InputNotFound => "ChatGPT-Eingabefeld nicht gefunden.",
            ChatGptFailure.StartFailed => "Aufnahme konnte nicht starten. Bitte Mikrofon in OpenAI Flow neu speichern, ChatGPT-Mikrofonzugriff erlauben und andere Aufnahme-Apps testweise schließen.",
            ChatGptFailure.StopFailed => "Diktierung konnte nicht gestoppt werden.",
            ChatGptFailure.NoText => "Nach dem Stoppen wurde kein Text transkribiert.",
            ChatGptFailure.PasteFailed => "Text wurde gelesen, aber konnte nicht ins Ziel eingefügt werden.",
            _ => "ChatGPT-Diktierung ist fehlgeschlagen."
        };
    }

}

internal enum ChatGptFailure
{
    None,
    ProfileNotFound,
    WindowNotFound,
    NotLoggedIn,
    InputNotFound,
    StartFailed,
    StopFailed,
    NoText,
    PasteFailed
}

internal sealed record ChatGptReadyResult(
    bool Ok,
    ChatGptFailure Failure,
    string Message,
    IntPtr ChatWindow,
    AutomationElement? Input)
{
    public static ChatGptReadyResult Success(IntPtr window, AutomationElement input) => new(true, ChatGptFailure.None, string.Empty, window, input);
    public static ChatGptReadyResult Fail(ChatGptFailure failure, string message) => new(false, failure, message, IntPtr.Zero, null);
}

internal sealed record ChatGptStartResult(
    bool Ok,
    ChatGptFailure Failure,
    string Message,
    IntPtr ChatWindow,
    AutomationElement? Input,
    bool IsTerminationConfirmed)
{
    public static ChatGptStartResult Success(IntPtr window, AutomationElement? input) =>
        new(true, ChatGptFailure.None, string.Empty, window, input, true);

    public static ChatGptStartResult Fail(
        ChatGptFailure failure,
        string message,
        bool isTerminationConfirmed = true,
        IntPtr chatWindow = default,
        AutomationElement? input = null) =>
        new(false, failure, message, chatWindow, input, isTerminationConfirmed);
}

internal sealed record ChatGptStopResult(
    bool Ok,
    ChatGptFailure Failure,
    string Message,
    bool CanRecoverText,
    bool RequiresDeferredCleanup,
    bool IsTerminationConfirmed)
{
    public static ChatGptStopResult Success() =>
        new(true, ChatGptFailure.None, string.Empty, true, false, true);

    public static ChatGptStopResult Fail(
        ChatGptFailure failure,
        string message,
        bool canRecoverText = false,
        bool requiresDeferredCleanup = false,
        bool isTerminationConfirmed = false) =>
        new(
            false,
            failure,
            message,
            canRecoverText,
            requiresDeferredCleanup,
            isTerminationConfirmed);
}

internal sealed record RecordingFailureCleanupResult(
    bool CanRecoverText,
    bool RequiresDeferredCleanup,
    bool IsTerminationConfirmed)
{
    public static RecordingFailureCleanupResult Recoverable { get; } = new(true, false, true);
    public static RecordingFailureCleanupResult DeferredRecovery { get; } = new(true, true, false);
    public static RecordingFailureCleanupResult NotRecoverable { get; } = new(false, false, true);
    public static RecordingFailureCleanupResult Unconfirmed { get; } = new(false, false, false);
}

internal sealed record ChatGptWindowResult(bool Ok, ChatGptFailure Failure, string Message, IntPtr ChatWindow)
{
    public static ChatGptWindowResult Success(IntPtr window) => new(true, ChatGptFailure.None, string.Empty, window);
    public static ChatGptWindowResult Fail(ChatGptFailure failure, string message) => new(false, failure, message, IntPtr.Zero);
}
