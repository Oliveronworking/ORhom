using System.Windows.Automation;

namespace ORhom;

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
        "diktat beenden", "aufnahme stoppen", "zuhÃ¶ren beenden", "listening",
        "recording", "wird aufgenommen", "hÃ¶re zu", "dictating", "diktat abbrechen",
        "diktat absenden", "submit dictation", "cancel dictation"
    ];

    private static readonly string[] VoiceModeMarkers =
    [
        "voice mode", "advanced voice", "voice conversation", "audio conversation",
        "sprachmodus", "sprachunterhaltung", "audiomodus"
    ];

    private static readonly string[] CancelMarkers = ["cancel", "abbrechen", "verwerfen"];
    private static readonly string[] ConfirmMarkers = ["done", "finish", "accept", "submit", "fertig", "bestÃ¤tigen", "Ã¼bernehmen", "absenden"];

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
            if (IsCurrentProfileWindow(_chatWindow))
            {
                return _chatWindow;
            }

            _chatWindow = ChatGptWindowFinder.FindOwnedBackgroundWindow(_settings);
            return _chatWindow;
        }
    }

    public ChromeProfileValidationResult ValidateConfiguredProfile() => _profileLauncher.ValidateConfiguredProfile();

    public PendingComposerInspection InspectPendingComposer()
    {
        _gate.Wait();
        try
        {
            return InspectPendingComposerCore();
        }
        catch (Exception ex)
        {
            _logger.Error("Pending composer inspection failed; owned window will be preserved.", ex);
            return PendingComposerInspection.Unavailable;
        }
        finally
        {
            HideBackgroundWindow();
            _gate.Release();
        }
    }

    private PendingComposerInspection InspectPendingComposerCore()
    {
        var chatWindow = KnownChatWindow;
        if (chatWindow == IntPtr.Zero)
        {
            return PendingComposerInspection.NoWindow;
        }

        if (!ChatGptWindowFinder.PrepareForBackgroundAutomation(
                chatWindow,
                _settings,
                _logger))
        {
            return PendingComposerInspection.Unavailable;
        }

        const int inspectionTimeoutMs = 2000;
        const int requiredStableMs = 400;
        var deadline = Environment.TickCount64 + inspectionTimeoutMs;
        string? observedText = null;
        var observedAt = 0L;
        var inactiveConfirmation = new RecordingStateConfirmationTracker(
            RecordingUiState.Inactive);
        while (Environment.TickCount64 < deadline)
        {
            if (!IsCurrentProfileWindow(chatWindow))
            {
                return PendingComposerInspection.NoWindow;
            }

            var input = AutomationHelpers.FindChatGptInput(
                chatWindow,
                _settings,
                _logger);
            if (AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings) &&
                AutomationHelpers.TryReadText(input, _logger, out var currentText))
            {
                var composerRect = TryGetBoundingRectangle(input) ?? _recordingComposerRect;
                var recordingState = composerRect is null
                    ? RecordingUiState.Unknown
                    : DetectRecordingState(chatWindow, composerRect.Value);
                if (!inactiveConfirmation.Observe(recordingState))
                {
                    observedText = null;
                    observedAt = 0;
                    Thread.Sleep(100);
                    continue;
                }

                currentText = currentText.Trim();
                if (AutomationHelpers.IsUnsafeCapturedText(currentText))
                {
                    _logger.Info("Pending composer inspection rejected unsafe captured text.");
                    return PendingComposerInspection.Unavailable;
                }

                var now = Environment.TickCount64;
                if (!string.Equals(observedText, currentText, StringComparison.Ordinal))
                {
                    observedText = currentText;
                    observedAt = now;
                }
                else if (now - observedAt >= requiredStableMs)
                {
                    _logger.Info($"Pending composer inspection completed. TextLength={currentText.Length}");
                    return currentText.Length == 0
                        ? PendingComposerInspection.Empty
                        : PendingComposerInspection.WithText(currentText);
                }
            }

            Thread.Sleep(100);
        }

        var loginStatus = ChatGptLoginDetector.Detect(
            chatWindow,
            _settings,
            _logger);
        if (loginStatus == ChatGptLoginStatus.LoggedOut)
        {
            return PendingComposerInspection.Empty;
        }

        _logger.Info("Pending composer inspection remained unavailable; owned window will be preserved.");
        return PendingComposerInspection.Unavailable;
    }

    public async Task<ChatGptReadyResult> ResetChatGptPageAsync(
        IntPtr excludedWindow,
        bool forceNewProfileWindow = false,
        ChromeProfileIdentity? previousProfileIdentity = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var validation = _profileLauncher.ValidateConfiguredProfile();
            if (!validation.IsValid)
            {
                return ChatGptReadyResult.Fail(ChatGptFailure.ProfileNotFound, MessageFor(ChatGptFailure.ProfileNotFound));
            }

            if (forceNewProfileWindow)
            {
                var identityToClose = previousProfileIdentity is { IsConfigured: true }
                    ? previousProfileIdentity
                    : ChromeProfileIdentity.From(_settings);
                var previousProfileReleased = ChatGptWindowFinder.IsOwnedBackgroundWindow(
                        _chatWindow,
                        identityToClose)
                    ? await ChatGptWindowFinder.CloseOwnedBackgroundWindowAndWaitAsync(
                        _chatWindow,
                        identityToClose,
                        _logger,
                        cancellationToken: cancellationToken)
                    : await ChatGptWindowFinder.CloseOwnedBackgroundWindowAndWaitAsync(
                        identityToClose,
                        _logger,
                        cancellationToken: cancellationToken);
                if (!previousProfileReleased)
                {
                    previousProfileReleased = ChatGptWindowFinder.ReleaseOwnedBackgroundWindowToUser(
                        identityToClose,
                        _logger);
                    if (previousProfileReleased)
                    {
                        _logger.Info("Previous background window did not close and was handed back as a visible user window before the profile switch.");
                    }
                }

                if (!previousProfileReleased)
                {
                    _logger.Info("Chrome profile switch stopped because the previous owned background window did not close.");
                    return ChatGptReadyResult.Fail(
                        ChatGptFailure.WindowNotFound,
                        "Das bisherige ORhom-Hintergrundfenster konnte nicht sicher geschlossen werden.");
                }

                _chatWindow = IntPtr.Zero;
            }

            var chatWindow = forceNewProfileWindow ? IntPtr.Zero : KnownChatWindow;
            if (chatWindow == IntPtr.Zero)
            {
                chatWindow = await LaunchConfiguredWindowCoreAsync(
                    excludedWindow,
                    cancellationToken);
            }

            if (!IsCurrentProfileWindow(chatWindow) ||
                !ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger) ||
                !TryNavigateWindow(chatWindow, _settings.ChatGptUrl))
            {
                return ChatGptReadyResult.Fail(ChatGptFailure.WindowNotFound, MessageFor(ChatGptFailure.WindowNotFound));
            }

            var deadline = Environment.TickCount64 + 12000;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(250, cancellationToken);
                if (!IsCurrentProfileWindow(chatWindow))
                {
                    return ChatGptReadyResult.Fail(
                        ChatGptFailure.WindowNotFound,
                        MessageFor(ChatGptFailure.WindowNotFound));
                }

                var input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger);
                if (AutomationHelpers.IsSafeChatGptInput(input, chatWindow, _settings))
                {
                    _logger.Info("ChatGPT background page reset completed and composer is ready.");
                    return ChatGptReadyResult.Success(chatWindow, input!);
                }
            }

            return ChatGptReadyResult.Fail(ChatGptFailure.InputNotFound, MessageFor(ChatGptFailure.InputNotFound));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("ChatGPT background page reset cancelled during application shutdown.");
            throw;
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

            var pendingText = AutomationHelpers.ReadText(input, _logger).Trim();
            if (pendingText.Length > 0)
            {
                _logger.Info($"New dictation blocked because the ChatGPT composer still contains unacknowledged text. TextLength={pendingText.Length}");
                return ChatGptStartResult.Fail(
                    ChatGptFailure.PendingText,
                    MessageFor(ChatGptFailure.PendingText),
                    chatWindow: chatWindow,
                    input: input,
                    pendingText: pendingText);
            }

            var composerRect = input!.Current.BoundingRectangle;
            attemptedComposerRect = composerRect;
            var dictationButton = FindDictationStartButton(chatWindow, composerRect);
            var triggered = dictationButton is not null &&
                            TryInvokeButton(dictationButton, chatWindow) != ControlInvocationOutcome.NotDispatched;
            var method = triggered ? "ComposerButton" : "None";
            if (dictationButton is null)
            {
                _logger.Info("ChatGPT composer dictation control was not found; no browser keyboard shortcut was sent.");
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
                if (!IsCurrentProfileWindow(chatWindow))
                {
                    return ChatGptStartResult.Fail(
                        ChatGptFailure.StartFailed,
                        MessageFor(ChatGptFailure.StartFailed),
                        isTerminationConfirmed: true);
                }

                _ = ChatGptWindowFinder.PrepareForAutomation(chatWindow, _settings, _logger);
                input = AutomationHelpers.FindChatGptInput(chatWindow, _settings, _logger) ?? input;
                var retryRect = TryGetBoundingRectangle(input) ?? composerRect;
                var retryButton = FindDictationStartButton(chatWindow,×®4êÚ$z{-®éÜj×WÆ–6FR6öçG&öÂ6öÖÖæBv–ÆÂ&R6VçBâ"ÂW‚“°Ğ¢&WGW&â6öçG&öÄ–çfö6F–öä÷WF6öÖRåVæ6W'F–ã°Ğ¢ĞĞ¢ĞĞ Ğ¢–b‚—47W'&VçE&öf–ÆUv–æF÷r†6†Ev–æF÷r’ÇÀĞ¢6†DwEv–æF÷tf–æFW"å&W&Tf÷$WFöÖF–öâ†6†Ev–æF÷rÂ÷6WGF–æw2ÂöÆövvW"’Ğ¢°Ğ¢öÆövvW"ä–æfò‚$6†DuB‡—6–6Â6öçG&öÂ–çfö6F–öâ6¶—VB&V6W6Rf÷&Vw&÷VæB7F—fF–öâf–ÆVBâ"“°Ğ¢&WGW&â6öçG&öÄ–çfö6F–öä÷WF6öÖRäæ÷DF—7F6†VC°Ğ¢ĞĞ Ğ¢f"&V7BÒ'WGFöâä7W'&VçBä&÷VæF–æu&V7FævÆS°Ğ¢–b‡&V7Båv–GF‚ãÒbb&V7Bä†V–v‡BãÒbb&V7Båv–GF‚ÃÒbb&V7Bä†V–v‡BÃÒĞ¢°Ğ¢f"6Æ–6µ‚Ò†–çB”ÖF‚å&÷VæB‡&V7BäÆVgB²&V7Båv–GF‚ò"“°Ğ¢f"6Æ–6µ’Ò†–çB”ÖF‚å&÷VæB‡&V7BåF÷²&V7Bä†V–v‡Bò"“°Ğ¢–b„—47W'&VçE&öf–ÆUv–æF÷r†6†Ev–æF÷r’b`Ğ¢WFöÖF–öä†VÇW'2ä—4VÆVÖVçD–åv–æF÷r†'WGFöâÂ6†Ev–æF÷r’b`Ğ¢æF—fTÖWF†öG2ä6Æ–6´B†6Æ–6µ‚Â6Æ–6µ’Â6†Ev–æF÷r’Ğ¢°Ğ¢öÆövvW"ä–æfò‚B$6†DuB6ö×÷6W"F–7FF–öâ6öçG&öÂ6Æ–6¶VB‡—6–6ÆÇ’âƒ×¶6Æ–6µ‡Ò“×¶6Æ–6µ—Ò"“°Ğ¢&WGW&â6öçG&öÄ–çfö6F–öä÷WF6öÖRäF—7F6†VC°Ğ¢ĞĞ¢ĞĞ Ğ¢'WGFöâå6WDfö7W2‚“°Ğ¢F‡&VBå6ÆVWƒc“°Ğ¢f"fö7W6VBÒWFöÖF–öä†VÇW'2ävWDfö7W6VDVÆVÖVçB…öÆövvW"“°Ğ¢–b„æF—fTÖWF†öG2ävWDf÷&Vw&÷VæEv–æF÷r‚’Ò6†Ev–æF÷rÇÀĞ¢WFöÖF–öä†VÇW'2ä—4VÆVÖVçD–åv–æF÷r†fö7W6VBÂ6†Ev–æF÷r’ÇÀĞ¢—47W'&VçE&öf–ÆUv–æF÷r†6†Ev–æF÷r’Ğ¢°Ğ¢öÆövvW"ä–æfò‚$6†DuB6öçG&öÂ¶W–&ö&B–çfö6F–öâ6¶—VB&V6W6Rfö7W2ÆVgBF†R&6¶w&÷VæBv–æF÷râ"“°Ğ¢&WGW&â6öçG&öÄ–çfö6F–öä÷WF6öÖRäæ÷DF—7F6†VC°Ğ¢ĞĞ Ğ¢6VæD¶W—2å6VæEv—B‚""“°Ğ¢öÆövvW"ä–æfò‚$6†DuB6ö×÷6W"F–7FF–öâ6öçG&öÂ–çfö¶VBf–fö7W6VB76R¶W’â"“°Ğ¢&WGW&â6öçG&öÄ–çfö6F–öä÷WF6öÖRäF—7F6†VC°Ğ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢öÆövvW"äW'&÷"‚$6÷VÆBæ÷B–çfö¶R6†DuB6ö×÷6W"F–7FF–öâ6öçG&öÂâ"ÂW‚“°Ğ¢&WGW&â6öçG&öÄ–çfö6F–öä÷WF6öÖRäæ÷DF—7F6†VC°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFRfö–BG'•&W7F÷&UF&vWDfö7W2„7F–öãò&W7F÷&UF&vWDfö7W2Â7G&–ær6öçFW‡BĞ¢°Ğ¢–b‡&W7F÷&UF&vWDfö7W2—2çVÆÂĞ¢°Ğ¢&WGW&ã°Ğ¢ĞĞ Ğ¢G'Ğ¢°Ğ¢&W7F÷&UF&vWDfö7W2‚“°Ğ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢öÆövvW"äW'&÷"‚B%F&vWBfö7W26ÆÆ&6²f–ÆVBâ6öçFW‡C×¶6öçFW‡GÒ"ÂW‚“°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂ—4æV$6ö×÷6W"„WFöÖF–öäVÆVÖVçB'WGFöâÂ7—7FVÒåv–æF÷w2å&V7B–çWE&V7BÂ–çEG"6†Ev–æF÷rĞ¢°Ğ¢G'Ğ¢°Ğ¢–b‚æF—fTÖWF†öG2ävWEv–æF÷u&V7B†6†Ev–æF÷rÂ÷WBf"v–æF÷u&V7B’Ğ¢°Ğ¢&WGW&âfÇ6S°Ğ¢ĞĞ Ğ¢f"'WGFöå&V7BÒ'WGFöâä7W'&VçBä&÷VæF–æu&V7FævÆS°Ğ¢–b†'WGFöå&V7Båv–GF‚ÂÇÂ'WGFöå&V7Bä†V–v‡BÂÇÂ'WGFöå&V7Båv–GF‚âÇÂ'WGFöå&V7Bä†V–v‡BâĞ¢°Ğ¢&WGW&âfÇ6S°Ğ¢ĞĞ Ğ¢f"6VçFW%‚Ò'WGFöå&V7BäÆVgB²'WGFöå&V7Båv–GF‚ò#°Ğ¢f"6VçFW%’Ò'WGFöå&V7BåF÷²'WGFöå&V7Bä†V–v‡Bò#°Ğ¢&WGW&â6VçFW%‚ãÒ–çWE&V7BäÆVgBÒCb`Ğ¢6VçFW%‚ÃÒ–çWE&V7Bå&–v‡B²ƒb`Ğ¢6VçFW%’ãÒ–çWE&V7BåF÷Ò“b`Ğ¢6VçFW%’ÃÒ–çWE&V7Bä&÷GFöÒ²“b`Ğ¢'WGFöå&V7BåF÷ãÒv–æF÷u&V7BåF÷²ƒ°Ğ¢ĞĞ¢6F6€Ğ¢°Ğ¢&WGW&âfÇ6S°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFR7FF–27G&–ærvWDFW67&—F÷"„WFöÖF–öäVÆVÖVçBVÆVÖVçBĞ¢°Ğ¢G'Ğ¢°Ğ¢f"7W'&VçBÒVÆVÖVçBä7W'&VçC°Ğ¢&WGW&â7G&–ærä¦ö–â‚""Â7W'&VçBäæÖRÂ7W'&VçBäWFöÖF–öä–BÂ7W'&VçBä†VÇFW‡BÂ7W'&VçBä6Æ74æÖR’åG&–Ò‚“°Ğ¢ĞĞ¢6F6€Ğ¢°Ğ¢&WGW&â7G&–æräV×G“°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂ—4F–7FF–öå7F'DFW67&—F÷"‡7G&–ærFW67&—F÷"Ğ¢°Ğ¢&WGW&â6öçF–ç4ç’†FW67&—F÷"Âfö–6TÖöFTÖ&¶W'2’bb6öçF–ç4ç’†FW67&—F÷"ÂF–7FF–öå7F'DÖ&¶W'2“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂ—5&V6÷&F–ætFW67&—F÷"‡7G&–ærFW67&—F÷"Ğ¢°Ğ¢&WGW&â6öçF–ç4ç’†FW67&—F÷"Âfö–6TÖöFTÖ&¶W'2’bb6öçF–ç4ç’†FW67&—F÷"Â&V6÷&F–ætÖ&¶W'2“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2&ööÂ6öçF–ç4ç’‡7G&–ærfÇVRÂ”VçVÖW&&ÆSÇ7G&–æsâÖ&¶W'2Ğ¢°Ğ¢&WGW&âÖ&¶W'2äç’†Ö&¶W"ÓâfÇVRä6öçF–ç2†Ö&¶W"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’“°Ğ¢ĞĞ Ğ¢&—fFR7FF–2F÷V&ÆR66÷&T'WGFöâ„WFöÖF–öäVÆVÖVçB'WGFöâĞ¢°Ğ¢G'Ğ¢°Ğ¢f"FW67&—F÷"ÒvWDFW67&—F÷"†'WGFöâ“°Ğ¢f"&V7BÒ'WGFöâä7W'&VçBä&÷VæF–æu&V7FævÆS°Ğ¢f"66÷&RÒ&V7Bä&÷GFöÓ°Ğ¢–b†FW67&—F÷"ä6öçF–ç2‚&F–7FB"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW67&—F÷"ä6öçF–ç2‚&F–·B"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’Ğ¢°Ğ¢66÷&R³ÒS°Ğ¢ĞĞ Ğ¢–b†FW67&—F÷"ä6öçF–ç2‚&Ö–7&÷†öæR"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’ÇÀĞ¢FW67&—F÷"ä6öçF–ç2‚&Ö–·&öföâ"Â7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’Ğ¢°Ğ¢66÷&R³ÒC°Ğ¢ĞĞ Ğ¢&WGW&â66÷&S°Ğ¢ĞĞ¢6F6€Ğ¢°Ğ¢&WGW&â°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFRfö–BÆöt–çWD6æF–FFR†–çB–æFW‚ÂWFöÖF–öäVÆVÖVçB6æF–FFRÂ–çEG"6†Ev–æF÷rĞ¢°Ğ¢G'Ğ¢°Ğ¢f"7W'&VçBÒ6æF–FFRä7W'&VçC°Ğ¢f"&V7BÒ7W'&VçBä&÷VæF–æu&V7FævÆS°Ğ¢f"&V6öâÒWFöÖF–öä†VÇW'2ävWD6†DwD–çWE6fWG•&V¦V7F–öå&V6öâ†6æF–FFRÂ6†Ev–æF÷rÂ÷6WGF–æw2“°Ğ¢öÆövvW"ä–æfò‚B$6†DuBF–væ÷7F–72–çWB7¶–æFW‡Ó¢6öçG&öÅG—SÒw¶7W'&VçBä6öçG&öÅG—Rå&öw&ÖÖF–4æÖWÒræÖSÒsÇ&VF7FVCâræÖTÆVæwFƒ×²†7W'&VçBäæÖRóò7G&–æräV×G’’äÆVæwF‡Ò6Æ74æÖSÒw¶7W'&VçBä6Æ74æÖWÒr&V7C×·&V7BäÆVgC£ÒÇ·&V7BåF÷£ÒÇ·&V7Båv–GFƒ£ÒÇ·&V7Bä†V–v‡C£Ò—56fT6†DwD–çWC×·&V6öâ—2çVÆÇÒ&V¦V7F–öå&V6öãÒw·&V6öâóò7G&–æräV×G—Òr"“°Ğ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢öÆövvW"äW'&÷"‚B$6†DuBF–væ÷7F–72–çWB7¶–æFW‡Ò6÷VÆBæ÷B&R–ç7V7FVBâ"ÂW‚“°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFRfö–BÆöt'WGFöä6æF–FFR†–çB–æFW‚ÂWFöÖF–öäVÆVÖVçB'WGFöâĞ¢°Ğ¢G'Ğ¢°Ğ¢f"7W'&VçBÒ'WGFöâä7W'&VçC°Ğ¢f"&V7BÒ7W'&VçBä&÷VæF–æu&V7FævÆS°Ğ¢f"Æ&VÂÒvWDFW67&—F÷"†'WGFöâ“°Ğ¢öÆövvW"ä–æfò‚B$6†DuBF–væ÷7F–72Ö–7&÷†öæR7¶–æFW‡Ó¢6öçG&öÅG—SÒw¶7W'&VçBä6öçG&öÅG—Rå&öw&ÖÖF–4æÖWÒræÖSÒw¶Æ&VÇÒr6Æ74æÖSÒw¶7W'&VçBä6Æ74æÖWÒr&V7C×·&V7BäÆVgC£ÒÇ·&V7BåF÷£ÒÇ·&V7Båv–GFƒ£ÒÇ·&V7Bä†V–v‡C£Ò"“°Ğ¢ĞĞ¢6F6‚„W†6WF–öâW‚Ğ¢°Ğ¢öÆövvW"äW'&÷"‚B$6†DuBF–væ÷7F–72Ö–7&÷†öæR7¶–æFW‡Ò6÷VÆBæ÷B&R–ç7V7FVBâ"ÂW‚“°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFRfö–BÆöuV•7FFR„–çEG"6†Ev–æF÷rÂ7G&–ær6öçFW‡BĞ¢°Ğ¢f"–çWBÒWFöÖF–öä†VÇW'2äf–æD6†DwD–çWB†6†Ev–æF÷rÂ÷6WGF–æw2ÂöÆövvW"“°Ğ¢f"6ö×÷6W%&V7BÒG'”vWD&÷VæF–æu&V7FævÆR†–çWB’óò÷&V6÷&F–æt6ö×÷6W%&V7C°Ğ¢f"7FFRÒ6ö×÷6W%&V7B—2çVÆÂò&V6÷&F–æuV•7FFRåVæ¶æ÷vâ¢FWFV7E&V6÷&F–æu7FFR†6†Ev–æF÷rÂ6ö×÷6W%&V7BåfÇVR“°Ğ¢f"'WGFöç2Ò6ö×÷6W%&V7B—2çVÆÂòµÒ¢f–æD6ö×÷6W$'WGFöç2†6†Ev–æF÷rÂ6ö×÷6W%&V7BåfÇVR“°Ğ¢f"F–7FFT6÷VçBÒ'WGFöç2ä6÷VçB†'WGFöâÓâ—4F–7FF–öå7F'DFW67&—F÷"„vWDFW67&—F÷"†'WGFöâ’’“°Ğ¢f"&V6÷&F–æt6÷VçBÒ'WGFöç2ä6÷VçB†'WGFöâÓâ—5&V6÷&F–ætFW67&—F÷"„vWDFW67&—F÷"†'WGFöâ’’“°Ğ¢öÆövvW"ä–æfò‚B$6†DuBT’7FFRF–væ÷7F–2â6öçFW‡C×¶6öçFW‡GÒ&V6÷&F–æu7FFS×·7FFWÒ6fT–çWC×´WFöÖF–öä†VÇW'2ä—56fT6†DwD–çWB†–çWBÂ6†Ev–æF÷rÂ÷6WGF–æw2—ÒF–7FF–öä'WGFöä6÷VçC×¶F–7FFT6÷VçGÒ&V6÷&F–æt6öçG&öÄ6÷VçC×·&V6÷&F–æt6÷VçGÒ"“°Ğ¢ĞĞ Ğ¢&—fFR7FF–27—7FVÒåv–æF÷w2å&V7CòG'”vWD&÷VæF–æu&V7FævÆR„WFöÖF–öäVÆVÖVçCòVÆVÖVçBĞ¢°Ğ¢–b†VÆVÖVçB—2çVÆÂĞ¢°Ğ¢&WGW&âçVÆÃ°Ğ¢ĞĞ Ğ¢G'Ğ¢°Ğ¢&WGW&âVÆVÖVçBä7W'&VçBä&÷VæF–æu&V7FævÆS°Ğ¢ĞĞ¢6F6€Ğ¢°Ğ¢&WGW&âçVÆÃ°Ğ¢ĞĞ¢ĞĞ Ğ¢&—fFR7G&–ærÖW76vTf÷"„6†DwDf–ÇW&Rf–ÇW&RĞ¢°Ğ¢&WGW&âf–ÇW&R7v—F6€Ğ¢°Ğ¢6†DwDf–ÇW&Rå&öf–ÆTæ÷Df÷VæBÓâB$¶öæf–wW&–W'FW26‡&öÖRÕ&öf–Âµ÷6WGF–æw2ä6‡&öÖU&öf–ÆTF—&V7F÷'—Òæ–6‡BvVgVæFVââ&—GFR6WGF–æw2æ§6öâ,;ÆfVââ"ÀĞ¢6†DwDf–ÇW&Råv–æF÷tæ÷Df÷VæBÓâ$6†DuBÕ&öf–Âæ–6‡BvVgVæFVââ"ÀĞ¢6†DwDf–ÇW&Räæ÷DÆövvVD–âÓâ$6†DuB—7Bæ–6‡BævVÖVÆFWBâ"ÀĞ¢6†DwDf–ÇW&Rä–çWDæ÷Df÷VæBÓâ$6†DuBÔV–æv&VfVÆBæ–6‡BvVgVæFVââ"ÀĞ¢6†DwDf–ÇW&RåVæF–æuFW‡BÓâ$–Ò6†DuBÔV–æv&VfVÆBÆ–VwBæö6‚V–âæ–6‡BvVÌ;g66‡FW2F–·FBâW2wW&FRW26–6†W&†V—G6w,;ÆæFVâæ–6‡B;Æ&W'66‡&–V&Vââ&—GFR§VW'7BFVâF–·F–W'fW&ÆVb,;ÆfVâöFW"F26†DuBÕ&öf–Â;fffæVââ"ÀĞ¢6†DwDf–ÇW&Rå7F'Df–ÆVBÓâ$Vfæ†ÖR¶öæçFRæ–6‡B7F'FVââ&—GFRÖ–·&öföâ–âõ&†öÒæWR7V–6†W&âÂ6†DuBÔÖ–·&öföç§Vw&–fbW&ÆV&VâVæBæFW&RVfæ†ÖRÔ2FW7GvV—6R66†Æ–\9öVââ"ÀĞ¢6†DwDf–ÇW&Rå7F÷f–ÆVBÓâ$F–·F–W'Vær¶öæçFRæ–6‡BvW7F÷BvW&FVââ"ÀĞ¢6†DwDf–ÇW&RäæõFW‡BÓâ$æ6‚FVÒ7F÷VâwW&FR¶V–âFW‡BG&ç6·&–&–W'Bâ"ÀĞ¢6†DwDf–ÇW&Rå7FTf–ÆVBÓâ%FW‡BwW&FRvVÆW6VâÂ&W"¶öæçFRæ–6‡B–ç2¦–VÂV–ævVl;ÆwBvW&FVââ"ÀĞ¢òÓâ$6†DuBÔF–·F–W'Vær—7BfV†ÆvW66†ÆvVââ Ğ¢Ó°Ğ¢ĞĞ Ğ§ĞĞ Ğ¦–çFW&æÂVçVÒ6†DwDf–ÇW&PĞ§°Ğ¢æöæRÀĞ¢&öf–ÆTæ÷Df÷VæBÀĞ¢v–æF÷tæ÷Df÷VæBÀĞ¢æ÷DÆövvVD–âÀĞ¢–çWDæ÷Df÷VæBÀĞ¢VæF–æuFW‡BÀĞ¢7F'Df–ÆVBÀĞ¢7F÷f–ÆVBÀĞ¢æõFW‡BÀĞ¢7FTf–ÆV@Ğ§ĞĞ Ğ¦–çFW&æÂVçVÒ6öçG&öÄ–çfö6F–öä÷WF6öÖPĞ§°Ğ¢æ÷DF—7F6†VBÀĞ¢F—7F6†VBÀĞ¢Væ6W'F–àĞ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6†DwE&VG•&W7VÇB€Ğ¢&ööÂö²ÀĞ¢6†DwDf–ÇW&Rf–ÇW&RÀĞ¢7G&–ærÖW76vRÀĞ¢–çEG"6†Ev–æF÷rÀĞ¢WFöÖF–öäVÆVÖVçCò–çWBĞ§°Ğ¢V&Æ–27FF–26†DwE&VG•&W7VÇB7V66W72„–çEG"v–æF÷rÂWFöÖF–öäVÆVÖVçB–çWB’ÓâæWr‡G'VRÂ6†DwDf–ÇW&RäæöæRÂ7G&–æräV×G’Âv–æF÷rÂ–çWB“°Ğ¢V&Æ–27FF–26†DwE&VG•&W7VÇBf–Â„6†DwDf–ÇW&Rf–ÇW&RÂ7G&–ærÖW76vR’ÓâæWr†fÇ6RÂf–ÇW&RÂÖW76vRÂ–çEG"å¦W&òÂçVÆÂ“°Ğ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6†DwE7F'E&W7VÇB€¢&ööÂö²ÀĞ¢6†DwDf–ÇW&Rf–ÇW&RÀĞ¢7G&–ærÖW76vRÀĞ¢–çEG"6†Ev–æF÷rÀ¢WFöÖF–öäVÆVÖVçCò–çWBÀ¢&ööÂ—5FW&Ö–æF–öä6öæf—&ÖVBÀ¢7G&–ærVæF–æuFW‡B§°¢V&Æ–27FF–26†DwE7F'E&W7VÇB7V66W72„–çEG"v–æF÷rÂWFöÖF–öäVÆVÖVçCò–çWB’Óà¢æWr‡G'VRÂ6†DwDf–ÇW&RäæöæRÂ7G&–æräV×G’Âv–æF÷rÂ–çWBÂG'VRÂ7G&–æräV×G’“° Ğ¢V&Æ–27FF–26†DwE7F'E&W7VÇBf–Â€Ğ¢6†DwDf–ÇW&Rf–ÇW&RÀĞ¢7G&–ærÖW76vRÀĞ¢&ööÂ—5FW&Ö–æF–öä6öæf—&ÖVBÒG'VRÀ¢–çEG"6†Ev–æF÷rÒFVfVÇBÀ¢WFöÖF–öäVÆVÖVçCò–çWBÒçVÆÂÀ¢7G&–ærVæF–æuFW‡BÒ""’Óà¢æWr†fÇ6RÂf–ÇW&RÂÖW76vRÂ6†Ev–æF÷rÂ–çWBÂ—5FW&Ö–æF–öä6öæf—&ÖVBÂVæF–æuFW‡B“°§Ğ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6†DwE7F÷&W7VÇB€Ğ¢&ööÂö²ÀĞ¢6†DwDf–ÇW&Rf–ÇW&RÀĞ¢7G&–ærÖW76vRÀĞ¢&ööÂ6å&V6÷fW%FW‡BÀĞ¢&ööÂ&WV—&W4FVfW'&VD6ÆVçWÀĞ¢&ööÂ—5FW&Ö–æF–öä6öæf—&ÖVBĞ§°Ğ¢V&Æ–27FF–26†DwE7F÷&W7VÇB7V66W72‚’ÓàĞ¢æWr‡G'VRÂ6†DwDf–ÇW&RäæöæRÂ7G&–æräV×G’ÂG'VRÂfÇ6RÂG'VR“°Ğ Ğ¢V&Æ–27FF–26†DwE7F÷&W7VÇBF—7F6†VEVæ6öæf—&ÖVB‚’ÓàĞ¢æWr‡G'VRÂ6†DwDf–ÇW&RäæöæRÂ7G&–æräV×G’ÂG'VRÂfÇ6RÂfÇ6R“°Ğ Ğ¢V&Æ–27FF–26†DwE7F÷&W7VÇBf–Â€Ğ¢6†DwDf–ÇW&Rf–ÇW&RÀĞ¢7G&–ærÖW76vRÀĞ¢&ööÂ6å&V6÷fW%FW‡BÒfÇ6RÀĞ¢&ööÂ&WV—&W4FVfW'&VD6ÆVçWÒfÇ6RÀĞ¢&ööÂ—5FW&Ö–æF–öä6öæf—&ÖVBÒfÇ6R’ÓàĞ¢æWr€Ğ¢fÇ6RÀĞ¢f–ÇW&RÀĞ¢ÖW76vRÀĞ¢6å&V6÷fW%FW‡BÀĞ¢&WV—&W4FVfW'&VD6ÆVçWÀĞ¢—5FW&Ö–æF–öä6öæf—&ÖVB“°Ğ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&B&V6÷&F–ætf–ÇW&T6ÆVçW&W7VÇB€Ğ¢&ööÂ6å&V6÷fW%FW‡BÀĞ¢&ööÂ&WV—&W4FVfW'&VD6ÆVçWÀĞ¢&ööÂ—5FW&Ö–æF–öä6öæf—&ÖVBĞ§°Ğ¢V&Æ–27FF–2&V6÷&F–ætf–ÇW&T6ÆVçW&W7VÇB&V6÷fW&&ÆR²vWC²ÒÒæWr‡G'VRÂfÇ6RÂG'VR“°Ğ¢V&Æ–27FF–2&V6÷&F–ætf–ÇW&T6ÆVçW&W7VÇBFVfW'&VE&V6÷fW'’²vWC²ÒÒæWr‡G'VRÂG'VRÂfÇ6R“°Ğ¢V&Æ–27FF–2&V6÷&F–ætf–ÇW&T6ÆVçW&W7VÇBæ÷E&V6÷fW&&ÆR²vWC²ÒÒæWr†fÇ6RÂfÇ6RÂG'VR“°Ğ¢V&Æ–27FF–2&V6÷&F–ætf–ÇW&T6ÆVçW&W7VÇBVæ6öæf—&ÖVB²vWC²ÒÒæWr†fÇ6RÂfÇ6RÂfÇ6R“°Ğ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&B6†DwEv–æF÷u&W7VÇB†&ööÂö²Â6†DwDf–ÇW&Rf–ÇW&RÂ7G&–ærÖW76vRÂ–çEG"6†Ev–æF÷rĞ§°Ğ¢V&Æ–27FF–26†DwEv–æF÷u&W7VÇB7V66W72„–çEG"v–æF÷r’ÓâæWr‡G'VRÂ6†DwDf–ÇW&RäæöæRÂ7G&–æräV×G’Âv–æF÷r“°Ğ¢V&Æ–27FF–26†DwEv–æF÷u&W7VÇBf–Â„6†DwDf–ÇW&Rf–ÇW&RÂ7G&–ærÖW76vR’ÓâæWr†fÇ6RÂf–ÇW&RÂÖW76vRÂ–çEG"å¦W&ò“°Ğ§ĞĞ Ğ¦–çFW&æÂVçVÒVæF–æt6ö×÷6W%7FFPĞ§°Ğ¢æõv–æF÷rÀĞ¢V×G’ÀĞ¢FW‡BÀĞ¢Væf–Æ&ÆPĞ§ĞĞ Ğ¦–çFW&æÂ6VÆVB&V6÷&BVæF–æt6ö×÷6W$–ç7V7F–öâ€Ğ¢VæF–æt6ö×÷6W%7FFR7FFRÀĞ¢7G&–ærFW‡BĞ§°Ğ¢V&Æ–27FF–2VæF–æt6ö×÷6W$–ç7V7F–öâæõv–æF÷r²vWC²ÒĞĞ¢æWr…VæF–æt6ö×÷6W%7FFRäæõv–æF÷rÂ7G&–æräV×G’“°Ğ Ğ¢V&Æ–27FF–2VæF–æt6ö×÷6W$–ç7V7F–öâV×G’²vWC²ÒĞĞ¢æWr…VæF–æt6ö×÷6W%7FFRäV×G’Â7G&–æräV×G’“°Ğ Ğ¢V&Æ–27FF–2VæF–æt6ö×÷6W$–ç7V7F–öâVæf–Æ&ÆR²vWC²ÒĞĞ¢æWr…VæF–æt6ö×÷6W%7FFRåVæf–Æ&ÆRÂ7G&–æräV×G’“°Ğ Ğ¢V&Æ–27FF–2VæF–æt6ö×÷6W$–ç7V7F–öâv—F…FW‡B‡7G&–ærFW‡B’ÓàĞ¢æWr…VæF–æt6ö×÷6W%7FFRåFW‡BÂFW‡B“°Ğ§ĞĞ Ğ¦–çFW&æÂVçVÒVæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖPĞ§°Ğ¢6fUv—F†÷WEFW‡BÀĞ¢Ç&VG•W'6—7FVBÀĞ¢W'6—7FVDæ÷rÀĞ¢W'6—7FVæ6Tf–ÆVBÀĞ¢–ç7V7F–öåVæf–Æ&ÆPĞ§ĞĞ Ğ¦–çFW&æÂ7FF–26Æ72VæF–æt6ö×÷6W%&W6W'fF–öàĞ§°Ğ¢V&Æ–27FF–2VæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖR&W6W'fR€Ğ¢VæF–æt6ö×÷6W$–ç7V7F–öâ–ç7V7F–öâÀĞ¢gVæ3Ç7G&–ærÂ&ööÃâ—4Ç&VG•W'6—7FVBÀĞ¢gVæ3Ç7G&–ærÂ&ööÃâW'6—7BĞ¢°Ğ¢–b†–ç7V7F–öâå7FFR—2VæF–æt6ö×÷6W%7FFRäæõv–æF÷r÷"VæF–æt6ö×÷6W%7FFRäV×G’Ğ¢°Ğ¢&WGW&âVæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRå6fUv—F†÷WEFW‡C°Ğ¢ĞĞ Ğ¢–b†–ç7V7F–öâå7FFRÒVæF–æt6ö×÷6W%7FFRåFW‡BĞ¢°Ğ¢&WGW&âVæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRä–ç7V7F–öåVæf–Æ&ÆS°Ğ¢ĞĞ Ğ¢–b†—4Ç&VG•W'6—7FVB†–ç7V7F–öâåFW‡B’Ğ¢°Ğ¢&WGW&âVæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRäÇ&VG•W'6—7FVC°Ğ¢ĞĞ Ğ¢&WGW&âW'6—7B†–ç7V7F–öâåFW‡BĞ¢òVæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRåW'6—7FVDæ÷pĞ¢¢VæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRåW'6—7FVæ6Tf–ÆVC°Ğ¢ĞĞ Ğ¢V&Æ–27FF–2&ööÂ—56fUFô6Æ÷6R…VæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖR÷WF6öÖR’ÓàĞ¢÷WF6öÖR—0Ğ¢VæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRå6fUv—F†÷WEFW‡B÷ Ğ¢VæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRäÇ&VG•W'6—7FVB÷ Ğ¢VæF–æt6ö×÷6W%&W6W'fF–öä÷WF6öÖRåW'6—7FVDæ÷s°Ğ§ĞĞ 