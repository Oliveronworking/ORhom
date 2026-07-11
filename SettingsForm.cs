namespace ChatGptDictationBridge;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly AudioInputDeviceService _audioDevices;
    private readonly ChromeProfileDiscovery _chromeProfiles;
    private readonly Func<string, string, ChromeProfileInfo, Task<SettingsApplyResult>> _saveAsync;
    private readonly ComboBox _microphoneCombo;
    private readonly ComboBox _chromeProfileCombo;
    private readonly TextBox _hotkeyBox;
    private readonly Label _statusLabel;
    private readonly Button _saveButton;
    private bool _allowClose;

    public SettingsForm(
        AppSettings settings,
        AudioInputDeviceService audioDevices,
        ChromeProfileDiscovery chromeProfiles,
        Func<string, string, ChromeProfileInfo, Task<SettingsApplyResult>> saveAsync)
    {
        _settings = settings;
        _audioDevices = audioDevices;
        _chromeProfiles = chromeProfiles;
        _saveAsync = saveAsync;

        Text = "OpenAI Flow";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(17, 18, 22);
        ClientSize = new Size(620, 596);
        Font = new Font("Segoe UI", 9.5f);
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        var title = CreateLabel("OpenAI Flow", new Font("Segoe UI", 22, FontStyle.Bold), new Point(36, 26), new Size(400, 42));
        var subtitle = CreateLabel(
            "Diktieren, wo dein Cursor gerade steht.",
            new Font("Segoe UI", 10.5f),
            new Point(39, 70),
            new Size(500, 28),
            Color.FromArgb(166, 170, 180));
        Controls.Add(title);
        Controls.Add(subtitle);

        var profileCard = CreateCard(new Point(32, 112), new Size(556, 118));
        profileCard.Controls.Add(CreateLabel("CHROME-PROFIL", new Font("Segoe UI", 8.5f, FontStyle.Bold), new Point(20, 14), new Size(200, 22), Color.FromArgb(150, 155, 168)));
        profileCard.Controls.Add(CreateLabel("Wähle das Chrome-Profil, in dem du bei ChatGPT angemeldet bist.", new Font("Segoe UI", 9f), new Point(20, 35), new Size(500, 22), Color.FromArgb(190, 193, 201)));
        _chromeProfileCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(39, 41, 49),
            ForeColor = Color.White,
            Location = new Point(20, 69),
            Size = new Size(448, 30),
            IntegralHeight = false,
            DropDownHeight = 220
        };
        var profileRefreshButton = CreateButton("↻", new Point(480, 67), new Size(48, 32), secondary: true);
        profileRefreshButton.Font = new Font("Segoe UI Symbol", 12, FontStyle.Bold);
        profileRefreshButton.Click += (_, _) => ReloadChromeProfiles();
        profileCard.Controls.Add(_chromeProfileCombo);
        profileCard.Controls.Add(profileRefreshButton);
        Controls.Add(profileCard);

        var deviceCard = CreateCard(new Point(32, 244), new Size(556, 132));
        deviceCard.Controls.Add(CreateLabel("MIKROFON", new Font("Segoe UI", 8.5f, FontStyle.Bold), new Point(20, 16), new Size(200, 22), Color.FromArgb(150, 155, 168)));
        deviceCard.Controls.Add(CreateLabel("Wähle den Eingang, den OpenAI Flow in Chrome verwenden soll.", new Font("Segoe UI", 9f), new Point(20, 39), new Size(500, 22), Color.FromArgb(190, 193, 201)));
        _microphoneCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(39, 41, 49),
            ForeColor = Color.White,
            Location = new Point(20, 72),
            Size = new Size(448, 30),
            IntegralHeight = false,
            DropDownHeight = 180
        };
        var refreshButton = CreateButton("↻", new Point(480, 70), new Size(48, 32), secondary: true);
        refreshButton.Font = new Font("Segoe UI Symbol", 12, FontStyle.Bold);
        refreshButton.Click += (_, _) => ReloadMicrophones();
        deviceCard.Controls.Add(_microphoneCombo);
        deviceCard.Controls.Add(refreshButton);
        Controls.Add(deviceCard);

        var hotkeyCard = CreateCard(new Point(32, 390), new Size(556, 104));
        hotkeyCard.Controls.Add(CreateLabel("TASTENKOMBINATION", new Font("Segoe UI", 8.5f, FontStyle.Bold), new Point(20, 15), new Size(220, 22), Color.FromArgb(150, 155, 168)));
        hotkeyCard.Controls.Add(CreateLabel("In das Feld klicken und die gewünschte Kombination drücken.", new Font("Segoe UI", 9f), new Point(20, 37), new Size(360, 22), Color.FromArgb(190, 193, 201)));
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
            new Point(39, 509),
            new Size(545, 24),
            Color.FromArgb(148, 163, 184));
        Controls.Add(_statusLabel);

        _saveButton = CreateButton("Speichern und im Hintergrund starten", new Point(32, 538), new Size(352, 40), secondary: false);
        _saveButton.Click += async (_, _) => await SaveAsync();
        var hideButton = CreateButton("Im Hintergrund schließen", new Point(396, 538), new Size(192, 40), secondary: true);
        hideButton.Click += (_, _) => Hide();
        Controls.Add(_saveButton);
        Controls.Add(hideButton);

        Shown += (_, _) =>
        {
            ReloadChromeProfiles();
            ReloadMicrophones();
        };
        FormClosing += OnFormClosing;
    }

    public void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
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

    private void ReloadMicrophones()
    {
        var selected = _microphoneCombo.SelectedItem?.ToString() ?? _settings.PreferredMicrophoneName;
        var microphones = _audioDevices.GetActiveMicrophones();
        _microphoneCombo.BeginUpdate();
        _microphoneCombo.Items.Clear();
        foreach (var microphone in microphones)
        {
            _microphoneCombo.Items.Add(microphone);
        }

        if (!string.IsNullOrWhiteSpace(selected) && !_microphoneCombo.Items.Contains(selected))
        {
            _microphoneCombo.Items.Insert(0, selected);
        }

        _microphoneCombo.SelectedItem = selected;
        if (_microphoneCombo.SelectedIndex < 0 && _microphoneCombo.Items.Count > 0)
        {
            _microphoneCombo.SelectedIndex = 0;
        }
        _microphoneCombo.EndUpdate();
    }

    private void ReloadChromeProfiles()
    {
        var selectedDirectory = (_chromeProfileCombo.SelectedItem as ChromeProfileInfo)?.DirectoryName
            ?? _settings.ChromeProfileDirectory;
        var result = _chromeProfiles.Discover(_settings.ChromeExecutablePath, _settings.ChromeUserDataDir);
        _chromeProfileCombo.BeginUpdate();
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
        _chromeProfileCombo.EndUpdate();

        SetStatus(
            result.Profiles.Count == 0
                ? "Keine Chrome-Profile gefunden. Ist Google Chrome installiert?"
                : $"{result.Profiles.Count} Chrome-Profil(e) gefunden.",
            result.Profiles.Count == 0 ? Color.FromArgb(248, 113, 113) : Color.FromArgb(148, 163, 184));
    }

    private async Task SaveAsync()
    {
        var microphone = _microphoneCombo.SelectedItem?.ToString() ?? string.Empty;
        var hotkey = _hotkeyBox.Text.Trim();
        if (_chromeProfileCombo.SelectedItem is not ChromeProfileInfo chromeProfile)
        {
            SetStatus("Bitte zuerst ein Chrome-Profil auswählen.", Color.FromArgb(248, 113, 113));
            return;
        }
        _saveButton.Enabled = false;
        _microphoneCombo.Enabled = false;
        _chromeProfileCombo.Enabled = false;
        _hotkeyBox.Enabled = false;
        SetStatus("Mikrofon und Tastenkombination werden eingerichtet …", Color.FromArgb(96, 165, 250));
        try
        {
            var result = await _saveAsync(microphone, hotkey, chromeProfile);
            if (!result.Ok)
            {
                SetStatus(result.Message, Color.FromArgb(248, 113, 113));
                return;
            }

            SetStatus($"Bereit · {result.MicrophoneName} · {result.Hotkey}", Color.FromArgb(74, 222, 128));
            await Task.Delay(550);
            Hide();
        }
        finally
        {
            _saveButton.Enabled = true;
            _microphoneCombo.Enabled = true;
            _chromeProfileCombo.Enabled = true;
            _hotkeyBox.Enabled = true;
        }
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

    private static Panel CreateCard(Point location, Size size) => new()
    {
        Location = location,
        Size = size,
        BackColor = Color.FromArgb(27, 29, 35)
    };

    private static Label CreateLabel(string text, Font font, Point location, Size size, Color? color = null) => new()
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

internal sealed record SettingsApplyResult(bool Ok, string Message, string MicrophoneName, string Hotkey)
{
    public static SettingsApplyResult Success(string microphoneName, string hotkey) =>
        new(true, string.Empty, microphoneName, hotkey);

    public static SettingsApplyResult Fail(string message) =>
        new(false, message, string.Empty, string.Empty);
}
