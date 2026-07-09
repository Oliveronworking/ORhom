using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Playwright;

namespace ChatGptDictationBridge;

internal sealed class BackgroundChatGptBrowserService : IDisposable
{
    private const int ConnectTimeoutMs = 3000;
    private readonly AppSettings _settings;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private Process? _browserProcess;
    private bool _setupWindowVisible;
    private bool _disposed;

    public BackgroundChatGptBrowserService(AppSettings settings, AppLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<BackgroundDictationResult> StartDictationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var page = await EnsureReadyPageAsync();
            if (page is null)
            {
                return BackgroundDictationResult.Failure("Hintergrundbrowser konnte nicht vorbereitet werden.");
            }

            if (!await FocusChatInputAsync(page))
            {
                if (_settings.OpenSetupOnBackgroundFailure)
                {
                    await OpenForSetupCoreAsync();
                    return BackgroundDictationResult.Failure("ChatGPT ist noch nicht bereit. Das separate Profil wurde fuer Login/Mikrofonwahl geoeffnet.");
                }

                return BackgroundDictationResult.Failure("ChatGPT-Eingabefeld nicht gefunden. Bitte im separaten Profil anmelden und Mikrofon erlauben.");
            }

            await SendDictationHotkeyAsync(page);
            if (_setupWindowVisible && _settings.MinimizeBackgroundBrowserAfterSuccessfulStart)
            {
                await MinimizeBrowserWindowAsync(page);
            }

            _logger.Info("Background dictation start triggered.");
            return BackgroundDictationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.Error("Background dictation start failed.", ex);
            return BackgroundDictationResult.Failure("Hintergrund-Diktat konnte nicht gestartet werden. Bitte ChatGPT einmal oeffnen/anmelden/Mikrofon erlauben.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackgroundTextResult> StopDictationAndReadTextAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var page = await EnsureReadyPageAsync();
            if (page is null)
            {
                return BackgroundTextResult.Failure("Hintergrundbrowser ist nicht verbunden.");
            }

            await SendDictationHotkeyAsync(page);
            _logger.Info("Background dictation stop triggered.");

            await Task.Delay(Math.Max(_settings.SettleDelayMs, 0));
            var text = await WaitForPromptTextAsync(page);
            _logger.Info($"Background ChatGPT input text read. Length={text.Length}");

            if (text.Length > 0)
            {
                await ClearPromptAsync(page);
            }

            return BackgroundTextResult.Success(text);
        }
        catch (Exception ex)
        {
            _logger.Error("Background dictation stop/read failed.", ex);
            return BackgroundTextResult.Failure("Diktat konnte im Hintergrund nicht gestoppt oder gelesen werden.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AbortDictationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var page = await EnsureReadyPageAsync();
            if (page is null)
            {
                return;
            }

            await SendDictationHotkeyAsync(page);
            await ClearPromptAsync(page);
            _logger.Info("Background dictation abort triggered.");
        }
        catch (Exception ex)
        {
            _logger.Error("Background dictation abort failed.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PrepareAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _ = await EnsureReadyPageAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Background ChatGPT startup preparation failed.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackgroundDictationResult> OpenForSetupAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await OpenForSetupCoreAsync();
            return BackgroundDictationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.Error("Background browser explicit setup open failed.", ex);
            return BackgroundDictationResult.Failure("ChatGPT-Profil konnte nicht geoeffnet werden.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenForSetupCoreAsync()
    {
        if (!await EnsureConnectedAsync(visibleForSetup: true))
        {
            throw new InvalidOperationException("Background browser could not be connected for setup.");
        }

        _context = _browser!.Contexts.FirstOrDefault();
        if (_context is null)
        {
            throw new InvalidOperationException("Background browser context was not available for setup.");
        }

        _page = await EnsureChatGptPageAsync(_context);
        if (_settings.OpenMicrophoneSettingsOnSetup)
        {
            await OpenMicrophoneSettingsPageAsync(_context);
        }

        await ShowBrowserWindowForSetupAsync(_page);
        _setupWindowVisible = true;
        _logger.Info("Background browser opened visibly for setup.");
    }

    private async Task<IPage?> EnsureReadyPageAsync()
    {
        if (_page is { IsClosed: false } page && IsChatGptUrl(page.Url))
        {
            _logger.Info("Background ChatGPT tab reused.");
            return page;
        }

        if (!await EnsureConnectedAsync(visibleForSetup: false))
        {
            return null;
        }

        _context = _browser!.Contexts.FirstOrDefault();
        if (_context is null)
        {
            _logger.Info("CDP connection did not expose a browser context.");
            return null;
        }

        await GrantMicrophonePermissionAsync(_context);

        _page = await EnsureChatGptPageAsync(_context);
        _logger.Info("Background ChatGPT tab found.");
        return _page;
    }

    private async Task<bool> EnsureConnectedAsync(bool visibleForSetup)
    {
        if (_browser?.IsConnected == true)
        {
            return true;
        }

        _browser = null;
        _context = null;
        _page = null;

        if (await TryConnectAsync())
        {
            return true;
        }

        if (!await LaunchBrowserAsync(visibleForSetup))
        {
            return false;
        }

        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 10000)
        {
            if (await TryConnectAsync())
            {
                return true;
            }

            await Task.Delay(300);
        }

        _logger.Info("CDP connection timed out after launching background browser.");
        return false;
    }

    private async Task<bool> TryConnectAsync()
    {
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.ConnectOverCDPAsync(
                $"http://127.0.0.1:{_settings.BackgroundBrowserDebugPort}",
                new BrowserTypeConnectOverCDPOptions { Timeout = ConnectTimeoutMs });
            _logger.Info("CDP/Playwright connected to background browser.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("CDP/Playwright connection attempt failed.", ex);
            return false;
        }
    }

    private async Task<bool> LaunchBrowserAsync(bool visibleForSetup)
    {
        if (!StartBrowserProcess(visibleForSetup))
        {
            return false;
        }

        if (_settings.KeepBackgroundBrowserMinimized && !visibleForSetup)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                MinimizeOwnedBrowserWindow();
            });
        }

        await Task.Delay(500);
        return true;
    }

    private bool StartBrowserProcess(bool visibleForSetup)
    {
        var executable = ResolveBrowserExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            _logger.Info("No Chrome or Edge executable found for background browser.");
            return false;
        }

        var userDataDir = ExpandPath(_settings.BackgroundBrowserUserDataDir);
        Directory.CreateDirectory(userDataDir);

        var shouldMinimize = _settings.KeepBackgroundBrowserMinimized && !visibleForSetup;
        var args = new List<string>
        {
            $"--remote-debugging-port={_settings.BackgroundBrowserDebugPort}",
            $"--user-data-dir=\"{userDataDir}\"",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--window-size=1200,900",
            "--new-window",
            _settings.ChatGptUrl
        };

        if (visibleForSetup && _settings.OpenMicrophoneSettingsOnSetup)
        {
            args.Add(GetMicrophoneSettingsUrl(executable));
        }

        if (shouldMinimize)
        {
            args.Insert(args.Count - 2, "--start-minimized");
        }

        var startInfo = new ProcessStartInfo(executable, string.Join(" ", args))
        {
            UseShellExecute = false,
            WindowStyle = shouldMinimize
                ? ProcessWindowStyle.Minimized
                : ProcessWindowStyle.Normal
        };

        _browserProcess = Process.Start(startInfo);
        if (_browserProcess is null)
        {
            _logger.Info("Background browser process could not be started.");
            return false;
        }

        _logger.Info($"Background browser started. Browser='{Path.GetFileName(executable)}' DebugPort={_settings.BackgroundBrowserDebugPort} VisibleSetup={visibleForSetup}");
        return true;
    }

    private void MinimizeOwnedBrowserWindow()
    {
        try
        {
            _browserProcess?.Refresh();
            var handle = _browserProcess?.MainWindowHandle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwMinimize);
                _logger.Info("Background browser window minimized.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Could not minimize background browser window.", ex);
        }
    }

    private async Task<IPage> EnsureChatGptPageAsync(IBrowserContext context)
    {
        var page = context.Pages.FirstOrDefault(candidate => !candidate.IsClosed && IsChatGptUrl(candidate.Url));
        if (page is null)
        {
            page = context.Pages.FirstOrDefault(candidate => !candidate.IsClosed && !IsBrowserSettingsUrl(candidate.Url));
        }

        if (page is null)
        {
            page = await context.NewPageAsync();
        }

        if (!IsChatGptUrl(page.Url))
        {
            await page.GotoAsync(_settings.ChatGptUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000
            });
        }

        return page;
    }

    private async Task OpenMicrophoneSettingsPageAsync(IBrowserContext context)
    {
        try
        {
            var url = GetMicrophoneSettingsUrl(ResolveBrowserExecutablePath());
            if (context.Pages.Any(page => !page.IsClosed && page.Url.StartsWith(url, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.Info("Browser microphone settings tab already open.");
                return;
            }

            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 8000
            });
            _logger.Info("Browser microphone settings tab opened for setup.");
        }
        catch (Exception ex)
        {
            _logger.Error("Could not open browser microphone settings tab.", ex);
        }
    }

    private async Task ShowBrowserWindowForSetupAsync(IPage page)
    {
        await SetBrowserWindowStateAsync(page, "normal");
        await page.BringToFrontAsync();
    }

    private async Task MinimizeBrowserWindowAsync(IPage page)
    {
        if (!_settings.KeepBackgroundBrowserMinimized)
        {
            return;
        }

        await SetBrowserWindowStateAsync(page, "minimized");
        _setupWindowVisible = false;
        _logger.Info("Background browser window minimized after successful dictation start.");
    }

    private async Task SetBrowserWindowStateAsync(IPage page, string windowState)
    {
        try
        {
            var session = await page.Context.NewCDPSessionAsync(page);
            var response = await session.SendAsync("Browser.getWindowForTarget");
            if (response is not JsonElement responseElement ||
                responseElement.ValueKind != JsonValueKind.Object ||
                !responseElement.TryGetProperty("windowId", out var windowIdElement) ||
                !windowIdElement.TryGetInt32(out var windowId))
            {
                _logger.Info($"Browser window state '{windowState}' skipped because CDP did not return a window id.");
                return;
            }

            await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["windowState"] = windowState
                }
            });
            _logger.Info($"Browser window state set through CDP. State={windowState}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Could not set browser window state through CDP. State={windowState}", ex);
        }
    }

    private async Task GrantMicrophonePermissionAsync(IBrowserContext context)
    {
        try
        {
            var origin = new Uri(_settings.ChatGptUrl).GetLeftPart(UriPartial.Authority);
            await context.GrantPermissionsAsync(["microphone"], new BrowserContextGrantPermissionsOptions { Origin = origin });
            _logger.Info("Background browser microphone permission requested through CDP.");
        }
        catch (Exception ex)
        {
            _logger.Error("Background browser microphone permission request failed.", ex);
        }
    }

    private async Task<bool> FocusChatInputAsync(IPage page)
    {
        var timeoutMs = Math.Max(_settings.ReadTextTimeoutMs, 3000);
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (await TryFocusChatInputOnceAsync(page))
            {
                return true;
            }

            await Task.Delay(250);
        }

        _logger.Info("Background ChatGPT input was not found or could not be focused.");
        return false;
    }

    private static async Task<bool> TryFocusChatInputOnceAsync(IPage page)
    {
        var focused = await page.EvaluateAsync<bool>(
            """
            () => {
              const input = (() => {
                const selectors = [
                  '#prompt-textarea',
                  '[data-testid="composer-input"]',
                  '[contenteditable="true"][data-lexical-editor="true"]',
                  'div[role="textbox"]',
                  'div.ProseMirror',
                  'textarea[placeholder*="Message"]',
                  'textarea[placeholder*="Nachricht"]',
                  'textarea[aria-label*="Message"]',
                  'textarea[aria-label*="Nachricht"]',
                  'textarea',
                  'div[contenteditable="true"]'
                ];
                for (const selector of selectors) {
                  for (const element of document.querySelectorAll(selector)) {
                    const rect = element.getBoundingClientRect();
                    const disabled = element.disabled || element.getAttribute('aria-disabled') === 'true';
                    if (!disabled && rect.width > 80 && rect.height > 18) {
                      return element;
                    }
                  }
                }
                return null;
              })();

              if (!input) {
                return false;
              }

              input.focus({ preventScroll: true });
              if (input.isContentEditable) {
                const range = document.createRange();
                range.selectNodeContents(input);
                range.collapse(false);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
              } else if (typeof input.selectionStart === 'number') {
                const length = input.value.length;
                input.setSelectionRange(length, length);
              }

              return document.activeElement === input || input.contains(document.activeElement);
            }
            """);

        return focused;
    }

    private async Task SendDictationHotkeyAsync(IPage page)
    {
        await page.Keyboard.PressAsync(ToPlaywrightHotkey(_settings.ChatGptDictationHotkey));
    }

    private async Task<string> WaitForPromptTextAsync(IPage page)
    {
        var timeoutMs = Math.Max(_settings.ReadTextTimeoutMs, 1000);
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            var text = (await ReadPromptTextAsync(page)).Trim();
            if (text.Length > 0)
            {
                return text;
            }

            await Task.Delay(250);
        }

        return string.Empty;
    }

    private static Task<string> ReadPromptTextAsync(IPage page)
    {
        return page.EvaluateAsync<string>(
            """
            () => {
              const selectors = [
                '#prompt-textarea',
                '[data-testid="composer-input"]',
                '[contenteditable="true"][data-lexical-editor="true"]',
                'div[role="textbox"]',
                'div.ProseMirror',
                'textarea',
                'div[contenteditable="true"]'
              ];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const rect = element.getBoundingClientRect();
                  if (rect.width <= 80 || rect.height <= 18) {
                    continue;
                  }

                  const text = element.isContentEditable
                    ? (element.innerText || element.textContent || '')
                    : (element.value || '');
                  if (text && text.trim().length > 0) {
                    return text.trim();
                  }
                }
              }

              return '';
            }
            """);
    }

    private async Task ClearPromptAsync(IPage page)
    {
        var cleared = await page.EvaluateAsync<bool>(
            """
            () => {
              const selectors = [
                '#prompt-textarea',
                '[data-testid="composer-input"]',
                '[contenteditable="true"][data-lexical-editor="true"]',
                'div[role="textbox"]',
                'div.ProseMirror',
                'textarea',
                'div[contenteditable="true"]'
              ];
              for (const selector of selectors) {
                for (const element of document.querySelectorAll(selector)) {
                  const rect = element.getBoundingClientRect();
                  if (rect.width <= 80 || rect.height <= 18) {
                    continue;
                  }

                  element.focus({ preventScroll: true });
                  if (element.isContentEditable) {
                    element.textContent = '';
                  } else {
                    element.value = '';
                  }

                  element.dispatchEvent(new InputEvent('input', {
                    bubbles: true,
                    inputType: 'deleteContentBackward',
                    data: null
                  }));
                  element.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }
              }

              return false;
            }
            """);

        _logger.Info(cleared
            ? "Background ChatGPT input cleared."
            : "Background ChatGPT input clear skipped because no prompt was found.");
    }

    private string ResolveBrowserExecutablePath()
    {
        var configured = ExpandPath(_settings.BackgroundBrowserExecutablePath);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Environment.ExpandEnvironmentVariables(path);
    }

    private static bool IsChatGptUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               url.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserSettingsUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               (url.StartsWith("chrome://settings", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("edge://settings", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetMicrophoneSettingsUrl(string executable)
    {
        return Path.GetFileName(executable).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
            ? "edge://settings/content/microphone"
            : "chrome://settings/content/microphone";
    }

    private static string ToPlaywrightHotkey(string hotkey)
    {
        return string.Join("+", hotkey
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? "Control" : part));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        _browser?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _playwright?.Dispose();
    }
}

internal sealed record BackgroundDictationResult(bool Ok, string Message)
{
    public static BackgroundDictationResult Success() => new(true, string.Empty);

    public static BackgroundDictationResult Failure(string message) => new(false, message);
}

internal sealed record BackgroundTextResult(bool Ok, string Text, string Message)
{
    public static BackgroundTextResult Success(string text) => new(true, text, string.Empty);

    public static BackgroundTextResult Failure(string message) => new(false, string.Empty, message);
}
