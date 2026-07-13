namespace ChatGptDictationBridge;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly AudioInputDeviceService _audioDevices;
    private readonly ChromeProfileDiscovery _chromeProfiles;
    private readonly Func<SettingsFormValues, Task<SettingsApplyResult>> _saveAsync;
    private readonly ComboBox _providerCombo;
    private readonly ComboBox _microphoneCombo;
    private readonly ComboBox _chromeProfileCombo;
    private readonly Panel _profileCard;
    private readonly TextBox _hotkeyBox;
    private readonly Label _statusLabel;
    private readonly Button _saveButton;
    private readonly System.Windows.Forms.Timer _microphoneRefreshTimer;
    private readonly HashSet<string> _knownMicrophones = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowClose;
    private bool _reloadingMicrophones;
    private bool _saveInProgress;

    public SettingsForm(
        AppSettings settings,
        AudioInputDeviceService audioDevices,
        ChromeProfileDiscovery chromeProfiles,
        Func<SettingsFormValues, Task<SettingsApplyResult>> saveAsync)
    {
        _settings = settings;
        _audioDevices = audioDevices;
        _chromeProfiles = chromeProfiles;
        _saveAsync = saveAsync;

        Text = "OpenAI Flow";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(17, 18, 22);
        ClientSize = new Size(620, 720);
        Font = new Font("Segoe UI", 9.5f);
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        Controls.Add(CreateLabel(
            "OpenAI Flow",
            new Font("Segoe UI", 22, FontStyle.Bold),
            new Point(36, 26),
            new Size(400, 42)));
        Controls.Add(CreateLabel(
            "Diktieren, wo dein Cursor gerade steht.",
            new Font("Segoe UI", 10.5f),
            new Point(39, 70),
            new Size(500, 28),
            Color.FromArgb(166, 170, 180)));

        var providerCard = CreateCard(new Point(32, 112), new Size(556, 104));
        providerCard.Controls.Add(CreateLabel(
            "SPRACHERKENNUNG",
            new Font("Segoe UI", 8.5f, FontStyle.Bold),
            new Point(20, 14),
            new Size(220, 22),
            Color.FromArgb(150, 155, 168)));
        providerCard.Controls.Add(CreateLabel(
            "Lokal über Vulkan ist Standard; ChatGPT bleibt als Browser-Fallback verfügbar.",
            new Font("Segoe UI", 9f),
            new Point(20, 35),
            new Size(510, 22),
            Color.FromArgb(190, 193, 201)));
        _providerCombo = CreateComboBox(new Point(20, 65), new Size(508, 30));
        _providerCombo.Items.Add(new DictationProviderOption(
            DictationProviders.LocalWhisper,
            "Lokal · whisper.cpp · Vulkan · Deutsch"));
        _providerCombo.Items.Add(new DictationProviderOption(
            DictationProviders.ChatGptBrowser,
            "ChatGPT-Browser-Diktierung (Fallback)"));
        _providerCombo.SelectedItem = _providerCombo.Items
            .Cast<DictationProviderOption>()
            .First(option => option.Value.Equals(
                DictationProviders.Normalize(_settings.DictationProvider),
                StringComparison.OrdinalIgnoreCase));
        _providerCombo.SelectedIndexChanged += (_, _) => UpdateProviderUi();
        providerCard.Controls.Add(_providerCombo);
        Controls.Add(providerCard);

        _profileCard = CreateCard(new Point(32, 230), new Size(556, 118));
        _profileCard.Controls.Add(CreateLabel(
            "CHROME-PROFIL · NUR FALLBACK",
            new Font("Segoe UI", 8.5f, FontStyle.Bold),
            new Point(20, 14),
            new Size(260, 22),
            Color.FromArgb(150, 155, 168)));
        _profileCard.Controls.Add(CreateLabel(
            "Wähle das Profil, in dem du bei ChatGPT angemeldet bist.",
            new Font("Segoe UI", 9f),
            new Point(20, 35),
            new Size(500, 22),
            Color.FromArgb(190, 193, 201)));
        _chromeProfileCombo = CreateComboBox(new Point(20, 69), new Size(448, 30));
        var profileRefreshButton = CreateButton("↻", new Point(480, 67), new Size(48, 32), secondary: true);
        profileRefreshButton.Font = new Font("Segoe UI Symbol", 12, FontStyle.Bold);
        profileRefreshButton.Click += (_, _) => ReloadChromeProfiles(showStatus: true);
        _profileCard.Controls.Add(_chromeProfileCombo);
        _profileCard.Controls.Add(profileRefreshButton);
        Controls.Add(_profileCard);

        var deviceCard = CreateCard(new Point(32, 362), new Size(556, 132));
        deviceCard.Controls.Add(CreateLabel(
            "MIKROFON",
            new Font("Segoe UI", 8.5f, FontStyle.Bold),
            new Point(20, 16),
            new Size(200, 22),
            Color.FromArgb(150, 155, 168)));
        deviceCard.Controls.Add(CreateLabel(
            "Wähle den lokalen Windows-Audioeingang für deine Diktate.",
            new Font("Segoe UI", 9f),
            new Point(20, 39),
            new Size(500, 22),
            Color.FromArgb(190, 193, 201)));
        _microphoneCombo = CreateComboBox(new Point(20, 72), new Size(448, 30));
        _microphoneCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_reloadingMicrophones)
            {
                SetStatus("Mikrofon ausgewählt. Zum Aktivieren speichern.", Color.FromArgb(148, 163, 184));
            }
        };
        var refreshButton = CreateButton("↻", new Point(480, 70), new Size(48, 32), secondary: true);
        refreshButton.Font = new Font("Segoe UI Symbol", 12, FontStyle.Bold);
        refreshButton.Click += (_, _) => ReloadMicrophones(showStatus: true);
        deviceCard.Controls.Add(_microphoneCombo);
        deviceCard.Controls.Add(refreshButton);
        Controls.Add(deviceCard);

        var hotkeyCard = CreateCard(new Point(32, 508), new Size(556, 104));
        hotkeyCard.Controls.Add(CreateLabel(
            "TASTENKOMBINATION",
            new Font("Segoe UI", 8.5f, FontStyle.Bold),
            new Point(20, 15),
            new Size(220, 22),
            Color.FromArgb(150, 155, 168)));
        hotkeyCard.Controls.Add(CreateLabel(
            "In das Feld klicken und die gewünschte Kombination drücken.",
            new Font("Segoe UI", 9f),
            new Point(20, 37),
            new Size(360, 22),
            Color.FromArgb(190, 193, 201)));
        _hotkeyBox = new TextBox
        {
            ReadOnly = true,
            Text = _settings.ToggleHotkey,
            TextAlign = HorizontalAlignment.Center,
            BackColor = Color.FromArgb(39, 41, 49),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Location = new Point(398, 29),
            Size = new Size(130, 32),
            TabStop = true
        };
        _hotkeyBox.KeyDown += CaptureHotkey;
        hotkeyCard.Controls.Add(_hotkeyBox);
        Controls.Add(hotkeyCard);

        _statusLabel = CreateLabel(
            "Bereit. Nach dem Schließen läuft OpenAI Flow im Infobereich weiter.",
            new Font("Segoe UI", 9f),
            new Point(39, 627),
            new Size(545, 24),
            Color.FromArgb(148, 163, 184));
        Controls.Add(_statusLabel);

        _saveButton = CreateButton(
            "Speichern und im Hintergrund starten",
            new Point(32, 662),
            new Size(352, 40),
            secondary: false);
        _saveButton.Click += async (_, _) => await SaveAsync();
        var hideButton = CreateButton(
            "Im Hintergrund schließen",
            new Point(396, 662),
            new Size(192, 40),
            secondary: true);
        hideButton.Click += (_, _) => Hide();
        Controls.Add(_saveButton);
        Controls.Add(hideButton);

        _microphoneRefreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _microphoneRefreshTimer.Tick += (_, _) =>
        {
            if (Visible && !_saveInProgress)
            {
                ReloadMicrophones(showStatus: false);
            }
        };
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                ReloadChromeProfiles(showStatus: false);
                ReloadMicrophones(showStatus: true);
                UpdateProviderUi();
                _microphoneRefreshTimer.Start();
            }
            else
            {
                _microphoneRefreshTimer.Stop();
            }
        };
        FormClosing += OnFormClosing;
        UpdateProviderUi();
    }

    public void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
        }
        else
        {
            ReloadMicrophones(showStatus: true);
        }

        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _microphoneRefreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool IsLocalProvider =>
        _providerCombo.SelectedItem is not DictationProviderOption option ||
        DictationProviders.IsLocal(option.Value);

    private void UpdateProviderUi()
    {
        foreach (Control control in _profileCard.Controls)
        {
            control.Enabled = !IsLocalProvider;
        }

        if (!_saveInProgress)
        {
            SetStatus(
                IsLocalProvider
                    ? "Lokale deutsche Erkennung über whisper.cpp/Vulkan ist ausgewählt."
                    : "Browser-Fallback ausgewählt; hierfür wird das Chrome-Profil verwendet.",
                Color.FromArgb(148, 163, 184));
        }
    }

    private void ReloadMicrophones(bool showStatus)
    {
        var selected = _microphoneCombo.SelectedItem as AudioInputDeviceInfo;
        var devices = _audioDevices.GetActiveMicrophoneDevices();
        var signatures = devices.Select(device => $"{device.Id}\u001e{device.DisplayName}").ToList();
        var devicesChanged = !_knownMicrophones.SetEquals(signatures);
        if (!devicesChanged && !showStatus)
        {
            return;
        }

        _knownMicrophones.Clear();
        _knownMicrophones.UnionWith(signatures);
        _reloadingMicrophones = true;
        _microphoneCombo.BeginUpdate();
        try
        {
            _microphoneCombo.Items.Clear();
            foreach (var device in devices)
            {
                _microphoneCombo.Items.Add(device);
            }

            var selectedId = selected?.Id ?? _settings.PreferredMicrophoneId;
            var selectedName = selected?.DisplayName ?? _settings.PreferredMicrophoneName;
            var matchingSelection = devices.FirstOrDefault(device =>
                !string.IsNullOrWhiteSpace(selectedId) &&
                device.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            matchingSelection ??= devices.FirstOrDefault(device =>
                device.DisplayName.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            if (matchingSelection is not null)
            {
                _microphoneCombo.SelectedItem = matchingSelection;
            }
            else if (_microphoneCombo.Items.Count > 0)
            {
                _microphoneCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _microphoneCombo.EndUpdate();
            _reloadingMicrophones = false;
        }

        if (devices.Count == 0)
        {
            SetStatus("Kein aktives Mikrofon erkannt. Bitte ein Gerät verbinden.", Color.FromArgb(248, 113, 113));
        }
        else if (showStatus || devicesChanged)
        {
            var suffix = devices.Count == 1 ? "Mikrofon erkannt." : "Mikrofone erkannt.";
            SetStatus($"{devices.Count} {suffix}", Color.FromArgb(74, 222, 128));
        }
    }

    private void ReloadChromeProfiles(bool showStatus)
    {
        var selectedDirectory = (_chromeProfileCombo.SelectedItem as ChromeProfileInfo)?.DirectoryName
            ?? _settings.ChromeProfileDirectory;
        var result = _chromeProfiles.Discover(_settings.ChromeExecutablePath, _settings.ChromeUserDataDir);
        _chromeProfileCombo.BeginUpdate();
        try
        {
            _chromeProfileCombo.Items.Clear();
            foreach (var profile in result.Profiles)
            {
                _chromeProfileCombo.Items.Add(profile);
            }

            _chromeProfileCombo.SelectedItem = result.Profiles.FirstOrDefault(profile =>
                profile.DirectoryName.Equals(selectedDirectory, StringComparison.OrdinalIgnoreCase));
            if (_chromeProfileCombo.SelectedIndex < 0 && _chromeProfileCombo.Items.Count > 0)
            {
                _chromeProfileCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _chromeProfileCombo.EndUpdate();
        }

        if (showStatus && !IsLocalProvider)
        {
            SetStatus(
                result.Profiles.Count == 0
                    ? "Keine Chrome-Profile gefunden. Ist Google Chrome installiert?"
                    : $"{result.Profiles.Count} Chrome-Profil(e) gefunden.",
                result.Profiles.Count == 0
                    ? Color.FromArgb(248, 113, 113)
                    : Color.FromArgb(148, 163, 184));
        }
    }

    private async Task SaveAsync()
    {
        if (_microphoneCombo.SelectedItem is not AudioInputDeviceInfo microphone)
        {
            SetStatus("Bitte zuerst ein aktives Mikrofon auswählen.", Color.FromArgb(248, 113, 113));
            return;
        }

        var provider = (_providerCombo.SelectedItem as DictationProviderOption)?.Value
            ?? DictationProviders.LocalWhisper;
        var chromeProfile = _chromeProfileCombo.SelectedItem as ChromeProfileInfo;
        if (!DictationProviders.IsLocal(provider) && chromeProfile is null)
        {
            SetStatus("Bitte für den Browser-Fallback ein Chrome-Profil auswählen.", Color.FromArgb(248, 113, 113));
            return;
        }

        _saveInProgress = true;
        _microphoneRefreshTimer.Stop();
        SetInputsEnabled(false);
        SetStatus("Einstellungen werden sicher übernommen …", Color.FromArgb(96, 165, 250));
        try
        {
            var result = await _saveAsync(new SettingsFormValues(
                provider,
                microphone.Id,
                microphone.DisplayName,
                _hotkeyBox.Text.Trim(),
                chromeProfile));
            if (!result.Ok)
            {
                SetStatus(result.Message, Color.FromArgb(248, 113, 113));
                return;
            }

            SetStatus(
                $"Bereit · {result.MicrophoneName} · {result.Hotkey}",
                Color.FromArgb(74, 222, 128));
            await Task.Delay(550);
            Hide();
        }
        finally
        {
            _saveInProgress = false;
            SetInputsEnabled(true);
            UpdateProviderUi();
            if (Visible)
            {
                _microphoneRefreshTimer.Start();
            }
        }
    }

    private void SetInputsEnabled(bool enabled)
    {
        _saveButton.Enabled = enabled;
        _providerCombo.Enabled = enabled;
        _microphoneCombo.Enabled = enabled;
        _chromeProfileCombo.Enabled = enabled && !IsLocalProvider;
        _hotkeyBox.Enabled = enabled;
    }

    private void CaptureHotkey(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            SetStatus("Escape ist für den Aufnahmeabbruch reserviert.", Color.FromArgb(248, 113, 113));
            return;
        }

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        parts.Add(e.KeyCode.ToString());
        _hotkeyBox.Text = string.Join("+", parts);
        SetStatus("Neue Tastenkombination gewählt. Zum Aktivieren speichern.", Color.FromArgb(148, 163, 184));
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason == CloseReason.ApplicationExitCall)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private static ComboBox CreateComboBox(Point location, Size size) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(39, 41, 49),
        ForeColor = Color.White,
        Location = location,
        Size = size,
        IntegralHeight = false,
        DropDownHeight = 220
    };

    private static Panel CreateCard(Point location, Size size) => new()
    {
        Location = location,
        Size = size,
        BackColor = Color.FromArgb(27, 29, 35)
    };

    private static Label CreateLabel(
        string text,
        Font font,
        Point location,
        Size size,
        Color? color = null) => new()
        {
            Text = text,
            Font = font,
            Location = location,
            Size = size,
            ForeColor = color ?? Color.White,
            BackColor = Color.Transparent,
            AutoEllipsis = true
        };

    private static Button CreateButton(string text, Point location, Size size, bool secondary)
    {
        var button = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = secondary ? Color.FromArgb(39, 41, 49) : Color.FromArgb(79, 70, 229),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = secondary ? 1 : 0;
        button.FlatAppearance.BorderColor = Color.FromArgb(61, 64, 75);
        return button;
    }
}

internal sealed record SettingsFormValues(
    string DictationProvider,
    string MicrophoneId,
    string MicrophoneName,
    string Hotkey,
    ChromeProfileInfo? ChromeProfile);

internal sealed record SettingsApplyResult(
    bool Ok,
    string Message,
    string MicrophoneName,
    string Hotkey)
{
    public static SettingsApplyResult Success(string microphoneName, string hotkey) =>
        new(true, string.Empty, microphoneName, hotkey);

    public static SettingsApplyResult Fail(string message) =>
        new(false, message, string.Empty, string.Empty);
}
