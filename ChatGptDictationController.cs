using System.Windows.Automation;

namespace ChatGptDictationBridge;

internal sealed class ChatGptDictationController : IDisposable
{
    private const int MinimumStopConfirmationTimeoutMs = 8000;
    private const int MaximumStopConfirmationTimeoutMs = 15000;
    private const int StopConfirmationTailGraceMs = 750;

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
        try
        {
            var ready = await EnsureChatGptReadyCoreAsync(excludedWindow);
            if (!ready.Ok)
            {
                return ChatGptStartResult.Fail(ready.Failure, ready.Message);
            }

            var chatWindow = ready.ChatWindow;
            var input = ready.Input;
            if (!AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings))
            {
                return ChatGptStartResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
            }

            var oldTextLength = AutomationHelpers.ReadText(input, _logger).Trim().Length;
            if (oldTextLength > 0)
            {
                _logger.Info($"Clearing pre-existing ChatGPT composer text before dictation. TextLength={oldTextLength}");
                if (!AutomationHelpers.ClearTextSafely(input, chatWindow, _settings, _logger))
                {
                    return ChatGptStartResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
                }

                await Task.Delay(150);
                input = AutomationHelpers.FocusChatGptInput(chatWindow, _settings, _logger);
                if (!AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings))
                {
                    return ChatGptStartResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
                }
            }

            var composerRect = input!.Current.BoundingRectangle;
            var dictationButton = FindDictationStartButton(chatWindow, composerRect);
            var triggered = dictationButton is not null && TryInvokeButton(dictationButton);
            var method = triggered ? "ComposerButton" : "None";
            if (dictationButton is null)
            {
                _logger.Info("ChatGPT composer dictation button was not found; browser shortcut fallback is disabled.");
            }
            else if (!triggered)
            {
                _logger.Info("ChatGPT composer dictation button was found but could not be invoked; shortcut fallback was not sent.");
            }

            var recordingActive = triggered &&
                                  await PollForRecordingStateAsync(chatWindow, composerRect, RecordingUiState.Active);
            if (triggered && !recordingActive)
            {
                _logger.Info("ChatGPT dictation start was not confirmed on the first attempt; retrying the composer control once.");
                _ = ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger);
                input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger) ?? input;
                var retryRect = TryGetBoundingRectangle(input) ?? composerRect;
                var retryButton = FindDictationStartButton(chatWindow, retryRect);
                var retryTriggered = retryButton is not null && TryInvokeButton(retryButton);
                if (retryTriggered)
                {
                    method = "ComposerButtonRetry";
                    composerRect = retryRect;
                    recordingActive = await PollForRecordingStateAsync(chatWindow, composerRect, RecordingUiState.Active);
                }
            }

            if (!recordingActive)
            {
                _logger.Info($"ChatGPT dictation start was not confirmed. TriggerMethod={method}");
                LogUiState(chatWindow, "start-failed");
                return ChatGptStartResult.Fail(ChatGptFailure.StartFailed, MessageFor(ChatGptFailure.StartFailed));
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
            return ChatGptStartResult.Fail(ChatGptFailure.StartFailed, MessageFor(ChatGptFailure.StartFailed));
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
            if (_settings.DictationStopGracePeriodMs > 0)
            {
                await Task.Delay(_settings.DictationStopGracePeriodMs);
            }

            if (chatWindow == IntPtr.Zero || !NativeMethods.IsWindow(chatWindow) ||
                !ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger))
            {
                return ChatGptStopResult.Fail(ChatGptFailure.StopFailed, MessageFor(ChatGptFailure.StopFailed));
            }

            var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
            var composerRect = TryGetBoundingRectangle(input) ?? _recordingComposerRect;
            if (composerRect is null)
            {
                return ChatGptStopResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
            }

            var stopButton = FindDictationStopButton(chatWindow, composerRect.Value);
            var triggered = stopButton is not null && TryInvokeButton(stopButton);
            var method = triggered ? "ComposerStopButton" : "None";
            if (stopButton is null)
            {
                _logger.Info("ChatGPT composer stop button was not found; browser shortcut fallback is disabled.");
            }
            else if (!triggered)
            {
                _logger.Info("ChatGPT dictation stop button was found but could not be invoked; shortcut fallback was not sent.");
            }

            if (!triggered)
            {
                _logger.Info($"ChatGPT dictation stop was not confirmed. TriggerMethod={method}");
                LogUiState(chatWindow, "stop-failed");
                return ChatGptStopResult.Fail(ChatGptFailure.StopFailed, MessageFor(ChatGptFailure.StopFailed));
            }

            var stopState = await PollForStopRecordingStateAsync(chatWindow, composerRect.Value);
            if (stopState != RecordingUiState.Inactive)
            {
                _logger.Info($"ChatGPT dictation stop was not confirmed. TriggerMethod={method} FinalState={stopState}");
                LogUiState(chatWindow, "stop-failed");
                return ChatGptStopResult.Fail(ChatGptFailure.StopFailed, MessageFor(ChatGptFailure.StopFailed));
            }

            if (_settings.DictationSettleDelayMs > 0)
            {
                await Task.Delay(_settings.DictationSettleDelayMs);
            }
            leavePreparedForRead = true;
            _logger.Info($"ChatGPT dictation stop confirmed. TriggerMethod={method} SettleDelayMs={Math.Max(_settings.DictationSettleDelayMs, 0)}");
            return ChatGptStopResult.Success();
        }
        catch (Exception ex)
        {
            _logger.Error("ChatGPT dictation stop failed.", ex);
            return ChatGptStopResult.Fail(ChatGptFailure.StopFailed, MessageFor(ChatGptFailure.StopFailed));
        }
        finally
        {
            _recordingComposerRect = null;
            if (!leavePreparedForRead)
            {
                HideBackgroundWindow();
            }
            _gate.Release();
        }
    }

    public async Task<ChatGptDictationReadResult> ReadDictatedTextAsync(IntPtr chatWindow)
    {
        await _gate.WaitAsync();
        try
        {
            var result = await AutomationHelpers.ReadChatGptTextRobustlyAsync(
                chatWindow,
                _settings,
                _logger,
                windowAlreadyPrepared: true);
            if (result.Text.Length > 0 && result.Input is not null)
            {
                _ = AutomationHelpers.ClearTextSafely(result.Input, chatWindow, _settings, _logger);
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

    private bool TryNavigateWindow(IntPtr chatWindow, string url)
    {
        try
        {
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
            omnibox.SetFocus();
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

    private async Task<bool> PollForRecordingStateAsync(IntPtr chatWindow, System.Windows.Rect composerRect, RecordingUiState expected)
    {
        var timeoutMs = Math.Clamp(_settings.RecordingStateTimeoutMs, 3000, 5000);
        var pollMs = Math.Clamp(_settings.DictationResultPollIntervalMs, 100, 500);
        var start = Environment.TickCount64;
        var consecutiveMatches = 0;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            var state = DetectRecordingState(chatWindow, composerRect);
            consecutiveMatches = state == expected ? consecutiveMatches + 1 : 0;
            if (consecutiveMatches >= 2)
            {
                return true;
            }

            await Task.Delay(pollMs);
        }

        return false;
    }

    private async Task<RecordingUiState> PollForStopRecordingStateAsync(
        IntPtr chatWindow,
        System.Windows.Rect fallbackComposerRect)
    {
        var timeoutMs = Math.Clamp(
            _settings.DictationStopConfirmationTimeoutMs,
            MinimumStopConfirmationTimeoutMs,
            MaximumStopConfirmationTimeoutMs);
        var pollMs = Math.Clamp(_settings.DictationResultPollIntervalMs, 100, 500);
        var currentComposerRect = fallbackComposerRect;

        RecordingUiState ReadCurrentState()
        {
            var currentInput = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
            currentComposerRect = TryGetBoundingRectangle(currentInput) ?? currentComposerRect;
            return DetectRecordingState(chatWindow, currentComposerRect);
        }

        var startedAt = Environment.TickCount64;
        var lastState = RecordingUiState.Unknown;
        var consecutiveInactive = 0;
        while (Environment.TickCount64 - startedAt < timeoutMs)
        {
            lastState = ReadCurrentState();
            consecutiveInactive = lastState == RecordingUiState.Inactive ? consecutiveInactive + 1 : 0;
            if (consecutiveInactive >= 2)
            {
                return RecordingUiState.Inactive;
            }

            await Task.Delay(pollMs);
        }

        _logger.Info($"ChatGPT stop confirmation entered tail grace. TimeoutMs={timeoutMs} LastState={lastState}");
        var tailStartedAt = Environment.TickCount64;
        do
        {
            lastState = ReadCurrentState();
            consecutiveInactive = lastState == RecordingUiState.Inactive ? consecutiveInactive + 1 : 0;
            if (consecutiveInactive >= 2)
            {
                return RecordingUiState.Inactive;
            }

            await Task.Delay(pollMs);
        }
        while (Environment.TickCount64 - tailStartedAt < StopConfirmationTailGraceMs);

        lastState = ReadCurrentState();
        consecutiveInactive = lastState == RecordingUiState.Inactive ? consecutiveInactive + 1 : 0;
        if (consecutiveInactive == 1)
        {
            await Task.Delay(pollMs);
            lastState = ReadCurrentState();
            consecutiveInactive = lastState == RecordingUiState.Inactive ? 2 : 0;
        }

        return consecutiveInactive >= 2
            ? RecordingUiState.Inactive
            : lastState == RecordingUiState.Active
                ? RecordingUiState.Active
                : RecordingUiState.Unknown;
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

    private bool TryInvokeButton(AutomationElement button)
    {
        try
        {
            var rect = button.Current.BoundingRectangle;
            if (rect.Width >= 10 && rect.Height >= 10 && rect.Width <= 100 && rect.Height <= 100)
            {
                var clickX = (int)Math.Round(rect.Left + rect.Width / 2);
                var clickY = (int)Math.Round(rect.Top + rect.Height / 2);
                if (NativeMethods.ClickAt(clickX, clickY))
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

    private enum RecordingUiState
    {
        Unknown,
        Inactive,
        Active
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
    AutomationElement? Input)
{
    public static ChatGptStartResult Success(IntPtr window, AutomationElement? input) => new(true, ChatGptFailure.None, string.Empty, window, input);
    public static ChatGptStartResult Fail(ChatGptFailure failure, string message) => new(false, failure, message, IntPtr.Zero, null);
}

internal sealed record ChatGptStopResult(bool Ok, ChatGptFailure Failure, string Message)
{
    public static ChatGptStopResult Success() => new(true, ChatGptFailure.None, string.Empty);
    public static ChatGptStopResult Fail(ChatGptFailure failure, string message) => new(false, failure, message);
}

internal sealed record ChatGptWindowResult(bool Ok, ChatGptFailure Failure, string Message, IntPtr ChatWindow)
{
    public static ChatGptWindowResult Success(IntPtr window) => new(true, ChatGptFailure.None, string.Empty, window);
    public static ChatGptWindowResult Fail(ChatGptFailure failure, string message) => new(false, failure, message, IntPtr.Zero);
}
