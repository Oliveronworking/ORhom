namespace ORhom;

internal sealed class SettingsForm : Form
{
    private static readonly Color WindowBackground = Color.FromArgb(15, 18, 24);
    private static readonly Color Surface = Color.FromArgb(24, 28, 36);
    private static readonly Color RaisedSurface = Color.FromArgb(31, 36, 46);
    private static readonly Color Border = Color.FromArgb(52, 60, 74);
    private static readonly Color Primary = Color.FromArgb(99, 102, 241);
    private static readonly Color PrimaryHover = Color.FromArgb(79, 70, 229);
    private static readonly Color PrimaryPressed = Color.FromArgb(67, 56, 202);
    private static readonly Color TextPrimary = Color.FromArgb(248, 250, 252);
    private static readonly Color TextSecondary = Color.FromArgb(176, 185, 201);
    private static readonly Color TextMuted = Color.FromArgb(139, 149, 168);
    private static readonly Color Success = Color.FromArgb(74, 222, 128);
    private static readonly Color Error = Color.FromArgb(248, 113, 113);
    private static readonly Color Info = Color.FromArgb(125, 160, 255);

    private readonly AppSettings _settings;
    private readonly AudioInputDeviceService _audioDevices;
    private readonly ChromeProfileDiscovery _chromeProfiles;
    private readonly Func<SettingsFormValues, Task<SettingsApplyResult>> _saveAsync;
    private readonly ComboBox _providerCombo;
    private readonly ComboBox _microphoneCombo;
    private readonly ComboBox _chromeProfileCombo;
    private readonly TableLayoutPanel _profileCard;
    private readonly Label _providerHintLabel;
    private readonly TextBox _hotkeyBox;
    private readonly TableLayoutPanel _dictationCard;
    private readonly ComboBox _languageCombo;
    private readonly CheckBox _smartFormattingCheck;
    private readonly CheckBox _fillersCheck;
    private readonly CheckBox _commandsCheck;
    private readonly DataGridView _dictionaryGrid;
    private readonly DataGridView _snippetsGrid;
    private readonly TextBox _previewInput;
    private readonly TextBox _previewOutput;
    private readonly Label _statusLabel;
    private readonly Button _profileRefreshButton;
    private readonly Button _microphoneRefreshButton;
    private readonly Button _saveButton;
    private readonly Button _hideButton;
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

        Name = "settingsForm";
        Text = "ORhom – Diktieren";
        AccessibleName = "ORhom Einstellungen";
        AccessibleDescription =
            "Richtet Diktiermodus, Mikrofon und Tastenkombination für ORhom ein.";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = WindowBackground;
        ClientSize = new Size(740, 720);
        MinimumSize = new Size(560, 440);
        Font = new Font("Segoe UI", 9.5f);
        ForeColor = TextPrimary;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        DoubleBuffered = true;

        var rootLayout = new TableLayoutPanel
        {
            Name = "settingsRootLayout",
            Dock = DockStyle.Fill,
            BackColor = WindowBackground,
            ColumnCount = 1,
            RowCount = 4,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            TabStop = false
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(rootLayout);

        var accentBar = new Panel
        {
            Name = "accentBar",
            Dock = DockStyle.Fill,
            BackColor = Primary,
            Margin = Padding.Empty,
            TabStop = false,
            AccessibleRole = AccessibleRole.Separator
        };
        rootLayout.Controls.Add(accentBar, 0, 0);

        var header = CreateHeader();
        header.Name = "settingsHeader";
        rootLayout.Controls.Add(header, 0, 1);

        var scrollPanel = new Panel
        {
            Name = "settingsScrollPanel",
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = WindowBackground,
            Padding = new Padding(36, 4, 36, 18),
            Margin = Padding.Empty,
            TabStop = false,
            AccessibleName = "Einrichtungsschritte",
            AccessibleDescription = "Enthält Einrichtung und persönliche Diktiereinstellungen für ORhom."
        };
        rootLayout.Controls.Add(scrollPanel, 0, 2);

        var stepsLayout = new TableLayoutPanel
        {
            Name = "settingsStepsLayout",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = WindowBackground,
            ColumnCount = 1,
            RowCount = 0,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            TabStop = false
        };
        stepsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scrollPanel.Controls.Add(stepsLayout);

        var providerStep = CreateStepCard(
            "1",
            "Modus wählen",
            "Standardmäßig läuft die Spracherkennung schnell und lokal auf diesem PC.");
        providerStep.Name = "providerStepCard";
        providerStep.TabIndex = 0;
        AddStackRow(stepsLayout, providerStep);

        _providerCombo = CreateComboBox(
            "Diktiermodus",
            "Wählt zwischen lokaler Spracherkennung und dem ChatGPT-Browser-Fallback.");
        _providerCombo.Name = "providerCombo";
        _providerCombo.TabIndex = 0;
        _providerCombo.Items.Add(new DictationProviderOption(
            DictationProviders.LocalWhisper,
            "Lokal · whisper.cpp · Vulkan · Mehrsprachig"));
        _providerCombo.Items.Add(new DictationProviderOption(
            DictationProviders.ChatGptBrowser,
            "ChatGPT-Browser-Diktierung · Fallback"));
        _providerCombo.SelectedItem = _providerCombo.Items
            .Cast<DictationProviderOption>()
            .First(option => option.Value.Equals(
                DictationProviders.Normalize(_settings.DictationProvider),
                StringComparison.OrdinalIgnoreCase));
        AddFullWidthRow(providerStep, _providerCombo, new Padding(44, 14, 0, 0));

        _providerHintLabel = CreateBodyLabel(
            string.Empty,
            TextMuted,
            new Font("Segoe UI", 8.75f));
        _providerHintLabel.Name = "providerHintLabel";
        _providerHintLabel.AccessibleName = "Hinweis zum Diktiermodus";
        AddFullWidthRow(providerStep, _providerHintLabel, new Padding(44, 7, 0, 0));

        _profileCard = new TableLayoutPanel
        {
            Name = "chromeProfilePanel",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = RaisedSurface,
            BorderStyle = BorderStyle.FixedSingle,
            ColumnCount = 1,
            RowCount = 0,
            Padding = new Padding(14),
            Margin = Padding.Empty,
            TabIndex = 1,
            TabStop = false,
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "Chrome-Profil für den Browser-Fallback",
            AccessibleDescription =
                "Wählt das Chrome-Profil, in dem ChatGPT bereits angemeldet ist."
        };
        _profileCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var profileTitle = CreateBodyLabel(
            "Chrome-Profil",
            TextPrimary,
            new Font("Segoe UI", 9.25f, FontStyle.Bold));
        AddStackRow(_profileCard, profileTitle);

        var profileHelp = CreateBodyLabel(
            "Wähle das Profil, in dem du bei ChatGPT angemeldet bist.",
            TextSecondary);
        AddStackRow(_profileCard, profileHelp, new Padding(0, 3, 0, 10));

        _chromeProfileCombo = CreateComboBox(
            "Chrome-Profil",
            "Das Chrome-Profil, das ORhom für die ChatGPT-Browser-Diktierung verwendet.");
        _chromeProfileCombo.Name = "chromeProfileCombo";
        _chromeProfileCombo.TabIndex = 0;
        _profileRefreshButton = CreateRefreshButton(
            "Chrome-Profile aktualisieren",
            "Sucht erneut nach verfügbaren Chrome-Profilen.");
        _profileRefreshButton.Name = "chromeProfileRefreshButton";
        _profileRefreshButton.TabIndex = 1;
        _profileRefreshButton.Click += (_, _) => ReloadChromeProfiles(showStatus: true);
        AddStackRow(
            _profileCard,
            CreateFieldRow(_chromeProfileCombo, _profileRefreshButton),
            Padding.Empty);
        AddFullWidthRow(providerStep, _profileCard, new Padding(44, 14, 0, 0));

        var microphoneStep = CreateStepCard(
            "2",
            "Mikrofon auswählen",
            "Wähle den Windows-Audioeingang, den ORhom für deine Diktate verwenden soll.");
        microphoneStep.Name = "microphoneStepCard";
        microphoneStep.TabIndex = 1;
        AddStackRow(stepsLayout, microphoneStep);

        _microphoneCombo = CreateComboBox(
            "Mikrofon",
            "Der Windows-Audioeingang, über den ORhom Sprache aufnimmt.");
        _microphoneCombo.Name = "microphoneCombo";
        _microphoneCombo.TabIndex = 0;
        _microphoneCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_reloadingMicrophones)
            {
                SetStatus("Mikrofon ausgewählt. Zum Aktivieren speichern.", TextMuted);
            }
        };
        _microphoneRefreshButton = CreateRefreshButton(
            "Mikrofone aktualisieren",
            "Sucht erneut nach aktiven Windows-Audioeingängen.");
        _microphoneRefreshButton.Name = "microphoneRefreshButton";
        _microphoneRefreshButton.TabIndex = 1;
        _microphoneRefreshButton.Click += (_, _) => ReloadMicrophones(showStatus: true);
        AddFullWidthRow(
            microphoneStep,
            CreateFieldRow(_microphoneCombo, _microphoneRefreshButton),
            new Padding(44, 14, 0, 0));

        var microphoneHelp = CreateBodyLabel(
            "Neu verbundene Geräte erscheinen automatisch. Du kannst die Liste auch manuell aktualisieren.",
            TextMuted,
            new Font("Segoe UI", 8.75f));
        AddFullWidthRow(microphoneStep, microphoneHelp, new Padding(44, 7, 0, 0));

        var hotkeyStep = CreateStepCard(
            "3",
            "Shortcut festlegen",
            "Drücke ihn in jeder App, um die Aufnahme zu starten oder zu stoppen.");
        hotkeyStep.Name = "hotkeyStepCard";
        hotkeyStep.TabIndex = 2;
        AddStackRow(stepsLayout, hotkeyStep, Padding.Empty);

        var hotkeyLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 1,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            TabStop = false
        };
        hotkeyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        hotkeyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hotkeyInstruction = CreateBodyLabel(
            "Klicke rechts in das Feld und drücke die gewünschte Tastenkombination.",
            TextSecondary);
        hotkeyInstruction.Margin = new Padding(0, 7, 14, 0);
        hotkeyLayout.Controls.Add(hotkeyInstruction, 0, 0);

        _hotkeyBox = new TextBox
        {
            Name = "hotkeyTextBox",
            ReadOnly = true,
            Text = _settings.ToggleHotkey,
            TextAlign = HorizontalAlignment.Center,
            BackColor = RaisedSurface,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            TabIndex = 0,
            TabStop = true,
            AccessibleName = "Aufnahme-Shortcut",
            AccessibleDescription =
                "Klicken und anschließend die gewünschte Tastenkombination drücken."
        };
        _hotkeyBox.KeyDown += CaptureHotkey;
        hotkeyLayout.Controls.Add(_hotkeyBox, 1, 0);
        AddFullWidthRow(hotkeyStep, hotkeyLayout, new Padding(44, 14, 0, 0));

        var hotkeyHelp = CreateBodyLabel(
            "Tipp: Strg, Alt oder Umschalt helfen, versehentliche Aufnahmen zu vermeiden.",
            TextMuted,
            new Font("Segoe UI", 8.75f));
        AddFullWidthRow(hotkeyStep, hotkeyHelp, new Padding(44, 7, 0, 0));

        _dictationCard = CreateStepCard(
            "4", "Diktate anpassen",
            "Persönliches Wörterbuch, Sprachkürzel und Textbefehle für die lokale Erkennung.");
        _dictationCard.Name = "dictationStepCard";
        _dictationCard.TabIndex = 3;
        AddStackRow(stepsLayout, _dictationCard, new Padding(0, 14, 0, 0));
        _languageCombo = CreateComboBox("Erkennungssprache", "Sprache für das lokale Whisper-Modell.");
        _languageCombo.Name = "dictationLanguageCombo";
        foreach (var (code, label) in DictationLanguages.Options)
            _languageCombo.Items.Add(new DictationLanguageOption(code, label));
        AddFullWidthRow(_dictationCard, _languageCombo, new Padding(44, 14, 0, 0));
        _smartFormattingCheck = CreatePreferenceCheck("Satzzeichen und Abstände formatieren", "smartFormattingCheck");
        _fillersCheck = CreatePreferenceCheck("Zögerlaute entfernen (äh, ähm, uh, um)", "removeFillersCheck");
        _commandsCheck = CreatePreferenceCheck("Gesprochene Textbefehle anwenden", "spokenCommandsCheck");
        foreach (var check in new[] { _smartFormattingCheck, _fillersCheck, _commandsCheck })
            AddFullWidthRow(_dictationCard, check, new Padding(44, 7, 0, 0));
        AddFullWidthRow(_dictationCard, CreateBodyLabel(
            "Befehle: Sprachbefehl Komma · Sprachbefehl neuer Absatz · Sprachbefehl letztes Wort löschen · Sprachbefehl letzten Satz löschen.\n" +
            "Listen: Sprachbefehl Listenpunkt · Sprachbefehl Nummer eins (bis zehn).\n" +
            "Korrektur: Sprachbefehl ersetze Dienstag durch Mittwoch Sprachbefehl Korrektur Ende.\n" +
            "Auch Diktat, ORhom und englische Befehle funktionieren. Korrekturen betreffen nur dieses Diktat.",
            TextMuted, new Font("Segoe UI", 8.75f)), new Padding(44, 8, 0, 0));
        AddFullWidthRow(_dictationCard, CreateBodyLabel(
            "Wörterbuch · Gesprochene Variante → exakte Schreibweise (z. B. ore home → ORhom)", TextSecondary),
            new Padding(44, 14, 0, 6));
        _dictionaryGrid = CreateReplacementGrid("dictionaryGrid", 112);
        AddFullWidthRow(_dictationCard, _dictionaryGrid, new Padding(44, 0, 0, 0));
        AddFullWidthRow(_dictationCard, CreateBodyLabel(
            "Sprachkürzel · Auslöser → fertiger Text. Zeilenumbrüche im Text: Umschalt + Eingabe.", TextSecondary),
            new Padding(44, 14, 0, 6));
        _snippetsGrid = CreateReplacementGrid("snippetsGrid", 138);
        AddFullWidthRow(_dictationCard, _snippetsGrid, new Padding(44, 0, 0, 0));
        _previewInput = CreatePreviewBox("dictationPreviewInput", readOnly: false);
        _previewInput.Text = "ähm Hallo Sprachbefehl Komma neuer Absatz Dienstag Sprachbefehl ersetze Dienstag durch Mittwoch Sprachbefehl Korrektur Ende";
        _previewInput.AccessibleName = "Vorschau: erkannter Text";
        AddFullWidthRow(_dictationCard, _previewInput, new Padding(44, 14, 0, 0));
        var previewButton = CreateButton("Textvorschau testen", secondary: true,
            "Testet Wörterbuch, Kürzel und Befehle auf dem eingegebenen Text, ohne Einstellungen zu speichern.");
        previewButton.Height = 34;
        previewButton.Name = "dictationPreviewButton";
        _previewOutput = CreatePreviewBox("dictationPreviewOutput", readOnly: true);
        previewButton.Click += (_, _) =>
        {
            if (TryReadDictationPreferences(out var preferences))
                _previewOutput.Text = DictationTextProcessor.Process(_previewInput.Text, preferences!);
        };
        AddFullWidthRow(_dictationCard, previewButton, new Padding(44, 7, 0, 0));
        _previewOutput.AccessibleName = "Vorschau: fertiger Text";
        AddFullWidthRow(_dictationCard, _previewOutput, new Padding(44, 7, 0, 0));
        LoadDictationPreferences();

        var footer = new TableLayoutPanel
        {
            Name = "settingsFooter",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Surface,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(36, 12, 36, 18),
            Margin = Padding.Empty,
            TabStop = false
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        rootLayout.Controls.Add(footer, 0, 3);

        _statusLabel = CreateBodyLabel(
            "Bereit. ORhom läuft nach dem Schließen im Infobereich weiter.",
            TextMuted,
            new Font("Segoe UI", 9f));
        _statusLabel.Name = "statusLabel";
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Margin = new Padding(2, 0, 2, 10);
        _statusLabel.AccessibleRole = AccessibleRole.StaticText;
        _statusLabel.AccessibleName = "Status";
        _statusLabel.AccessibleDescription = _statusLabel.Text;
        footer.Controls.Add(_statusLabel, 0, 0);

        var buttonLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 1,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            TabStop = false
        };
        buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        buttonLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(buttonLayout, 0, 1);

        _saveButton = CreateButton(
            "Speichern & losdiktieren",
            secondary: false,
            "Einstellungen speichern und ORhom im Hintergrund zum Diktieren bereitstellen.");
        _saveButton.Name = "saveAndStartButton";
        _saveButton.Margin = new Padding(0, 0, 6, 0);
        _saveButton.TabIndex = 0;
        _saveButton.Click += async (_, _) => await SaveAsync();
        buttonLayout.Controls.Add(_saveButton, 0, 0);

        _hideButton = CreateButton(
            "Im Hintergrund schließen",
            secondary: true,
            "Fenster ausblenden; ORhom läuft im Infobereich weiter.");
        _hideButton.Name = "hideToTrayButton";
        _hideButton.Margin = new Padding(6, 0, 0, 0);
        _hideButton.TabIndex = 1;
        _hideButton.Click += (_, _) => Hide();
        buttonLayout.Controls.Add(_hideButton, 1, 0);
        AcceptButton = _saveButton;

        _providerCombo.SelectedIndexChanged += (_, _) => UpdateProviderUi();

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
                RefreshFromSettings(showMicrophoneStatus: true);
                _microphoneRefreshTimer.Start();
            }
            else
            {
                _microphoneRefreshTimer.Stop();
            }
        };
        FormClosing += OnFormClosing;
        UpdateProviderUi(updateStatus: false);
    }

    public void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
        }
        else
        {
            RefreshFromSettings(showMicrophoneStatus: true);
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        var workingArea = Screen.FromControl(this).WorkingArea;
        var maximumWidth = Math.Max(MinimumSize.Width, workingArea.Width - 32);
        var maximumHeight = Math.Max(MinimumSize.Height, workingArea.Height - 32);
        if (Width <= maximumWidth && Height <= maximumHeight)
        {
            return;
        }

        var fittedSize = new Size(
            Math.Min(Width, maximumWidth),
            Math.Min(Height, maximumHeight));
        Bounds = new Rectangle(
            workingArea.Left + ((workingArea.Width - fittedSize.Width) / 2),
            workingArea.Top + ((workingArea.Height - fittedSize.Height) / 2),
            fittedSize.Width,
            fittedSize.Height);
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

    private void RefreshFromSettings(bool showMicrophoneStatus)
    {
        var configuredProvider = DictationProviders.Normalize(_settings.DictationProvider);
        _providerCombo.SelectedItem = _providerCombo.Items
            .Cast<DictationProviderOption>()
            .First(option => option.Value.Equals(
                configuredProvider,
                StringComparison.OrdinalIgnoreCase));

        _hotkeyBox.Text = _settings.ToggleHotkey;
        LoadDictationPreferences();
        _hotkeyBox.AccessibleDescription =
            $"Gewählter Aufnahme-Shortcut: {_hotkeyBox.Text}. Klicken und drücken, um ihn zu ändern.";

        UpdateProviderUi(updateStatus: false);
        ReloadChromeProfiles(showStatus: false, preferSettings: true);
        ReloadMicrophones(showMicrophoneStatus, preferSettings: true);
    }

    private void UpdateProviderUi(bool updateStatus = true)
    {
        _profileCard.Visible = !IsLocalProvider;
        _profileCard.Enabled = !_saveInProgress && !IsLocalProvider;
        _dictationCard.Enabled = !_saveInProgress && IsLocalProvider;
        _providerHintLabel.Text = IsLocalProvider
            ? "Empfohlen · Die Audiodaten bleiben auf deinem PC."
            : "Fallback · ORhom verwendet dein angemeldetes ChatGPT-Chrome-Profil.";

        if (updateStatus && !_saveInProgress)
        {
            SetStatus(
                IsLocalProvider
                    ? "Lokale mehrsprachige Erkennung über whisper.cpp und Vulkan ist ausgewählt."
                    : "Browser-Fallback ausgewählt. Bitte darunter das passende Chrome-Profil prüfen.",
                TextMuted);
        }
    }

    private void ReloadMicrophones(bool showStatus, bool preferSettings = false)
    {
        var selected = _microphoneCombo.SelectedItem as AudioInputDeviceInfo;
        var devices = _audioDevices.GetActiveMicrophoneDevices();
        var signatures = devices.Select(device => $"{device.Id}\u001e{device.DisplayName}").ToList();
        var devicesChanged = !_knownMicrophones.SetEquals(signatures);
        if (!devicesChanged && !showStatus && !preferSettings)
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

            var selectedId = preferSettings
                ? _settings.PreferredMicrophoneId
                : selected?.Id ?? _settings.PreferredMicrophoneId;
            var selectedName = preferSettings
                ? _settings.PreferredMicrophoneName
                : selected?.DisplayName ?? _settings.PreferredMicrophoneName;
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
            SetStatus("Kein aktives Mikrofon erkannt. Bitte ein Gerät verbinden.", Error);
        }
        else if (showStatus || devicesChanged)
        {
            var suffix = devices.Count == 1 ? "Mikrofon erkannt." : "Mikrofone erkannt.";
            SetStatus($"{devices.Count} {suffix}", Success);
        }
    }

    private void ReloadChromeProfiles(bool showStatus, bool preferSettings = false)
    {
        var selectedDirectory = preferSettings
            ? _settings.ChromeProfileDirectory
            : (_chromeProfileCombo.SelectedItem as ChromeProfileInfo)?.DirectoryName
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
                result.Profiles.Count == 0 ? Error : TextMuted);
        }
    }

    private static CheckBox CreatePreferenceCheck(string text, string name) => new()
    {
        Name = name,
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        ForeColor = TextSecondary,
        BackColor = Color.Transparent,
        AccessibleName = text,
        Margin = Padding.Empty
    };

    private static DataGridView CreateReplacementGrid(string name, int height)
    {
        var grid = new DataGridView
        {
            Name = name,
            Height = height,
            Dock = DockStyle.Fill,
            BackgroundColor = RaisedSurface,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToOrderColumns = false,
            AllowUserToResizeRows = false,
            EnableHeadersVisualStyles = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AccessibleName = name == "dictionaryGrid" ? "Persönliches Wörterbuch" : "Sprachkürzel",
            AccessibleDescription = "Gesprochener Auslöser und exakter Ersatztext. Leere Zeilen werden ignoriert."
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
        grid.DefaultCellStyle.BackColor = RaisedSurface;
        grid.DefaultCellStyle.ForeColor = TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = Primary;
        grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "cue", HeaderText = "Gesprochen", FillWeight = 35 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "replacement", HeaderText = "Schreiben", FillWeight = 65 });
        return grid;
    }

    private static TextBox CreatePreviewBox(string name, bool readOnly) => new()
    {
        Name = name,
        Multiline = true,
        ReadOnly = readOnly,
        Height = 66,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical,
        BackColor = RaisedSurface,
        ForeColor = TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        AcceptsReturn = true
    };

    private void LoadDictationPreferences()
    {
        var preferences = _settings.DictationPreferences.NormalizedCopy();
        _languageCombo.SelectedItem = _languageCombo.Items.Cast<DictationLanguageOption>()
            .First(option => option.Code == preferences.Language);
        _smartFormattingCheck.Checked = preferences.SmartFormatting;
        _fillersCheck.Checked = preferences.RemoveFillers;
        _commandsCheck.Checked = preferences.SpokenCommands;
        LoadEntries(_dictionaryGrid, preferences.Dictionary);
        LoadEntries(_snippetsGrid, preferences.Snippets);
    }

    private static void LoadEntries(DataGridView grid, IEnumerable<DictationReplacement> entries)
    {
        grid.Rows.Clear();
        foreach (var entry in entries) grid.Rows.Add(entry.Cue, entry.Replacement);
    }

    private bool TryReadDictationPreferences(out DictationPreferences? preferences)
    {
        preferences = null;
        if (!TryReadEntries(_dictionaryGrid, out var dictionary) ||
            !TryReadEntries(_snippetsGrid, out var snippets)) return false;
        preferences = new DictationPreferences
        {
            Language = (_languageCombo.SelectedItem as DictationLanguageOption)?.Code ?? "de",
            SmartFormatting = _smartFormattingCheck.Checked,
            RemoveFillers = _fillersCheck.Checked,
            SpokenCommands = _commandsCheck.Checked,
            Dictionary = dictionary,
            Snippets = snippets
        };
        return true;
    }

    private bool TryReadEntries(DataGridView grid, out DictationReplacement[] entries)
    {
        grid.EndEdit();
        var result = new List<DictationReplacement>();
        var cues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;
            var cue = Convert.ToString(row.Cells["cue"].Value)?.Trim() ?? string.Empty;
            var replacement = Convert.ToString(row.Cells["replacement"].Value) ?? string.Empty;
            if (cue.Length == 0 && replacement.Length == 0) continue;
            cue = System.Text.RegularExpressions.Regex.Replace(cue, @"\s+", " ");
            if (cue.Length == 0 || replacement.Length == 0 || !cues.Add(cue))
            {
                SetStatus("Jeder Wörterbuch- oder Kürzeleintrag braucht einen eindeutigen Auslöser und einen Ersatztext.", Error);
                grid.Focus();
                entries = [];
                return false;
            }
            result.Add(new DictationReplacement { Cue = cue, Replacement = replacement });
        }
        entries = result.ToArray();
        return true;
    }

    private async Task SaveAsync()
    {
        if (_microphoneCombo.SelectedItem is not AudioInputDeviceInfo microphone)
        {
            SetStatus("Bitte zuerst ein aktives Mikrofon auswählen.", Error);
            _microphoneCombo.Focus();
            return;
        }

        var provider = (_providerCombo.SelectedItem as DictationProviderOption)?.Value
            ?? DictationProviders.LocalWhisper;
        var chromeProfile = _chromeProfileCombo.SelectedItem as ChromeProfileInfo;
        if (!DictationProviders.IsLocal(provider) && chromeProfile is null)
        {
            SetStatus("Bitte für den Browser-Fallback ein Chrome-Profil auswählen.", Error);
            _chromeProfileCombo.Focus();
            return;
        }

        if (!TryReadDictationPreferences(out var preferences)) return;

        _saveInProgress = true;
        _microphoneRefreshTimer.Stop();
        SetInputsEnabled(false);
        SetStatus("Einstellungen werden sicher übernommen …", Info);
        try
        {
            var result = await _saveAsync(new SettingsFormValues(
                provider,
                microphone.Id,
                microphone.DisplayName,
                _hotkeyBox.Text.Trim(),
                chromeProfile,
                preferences));
            if (!result.Ok)
            {
                SetStatus(result.Message, Error);
                return;
            }

            SetStatus(
                $"Bereit · {result.MicrophoneName} · {result.Hotkey}",
                Success);
            await Task.Delay(550);
            Hide();
        }
        finally
        {
            _saveInProgress = false;
            SetInputsEnabled(true);
            UpdateProviderUi(updateStatus: false);
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
        _microphoneRefreshButton.Enabled = enabled;
        _chromeProfileCombo.Enabled = enabled && !IsLocalProvider;
        _profileRefreshButton.Enabled = enabled && !IsLocalProvider;
        _profileCard.Enabled = enabled && !IsLocalProvider;
        _hotkeyBox.Enabled = enabled;
        _dictationCard.Enabled = enabled && IsLocalProvider;
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
            SetStatus("Escape ist für den Aufnahmeabbruch reserviert.", Error);
            return;
        }

        if (!IsSafeHotkeySelection(e.KeyCode, e.Modifiers))
        {
            SetStatus(
                "Einzelne Buchstaben, Zahlen und Leertaste sind als globaler Shortcut zu leicht auslösbar. Bitte Strg, Alt oder Umschalt verwenden – oder eine F-Taste wählen.",
                Error);
            return;
        }

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        parts.Add(e.KeyCode.ToString());
        _hotkeyBox.Text = string.Join("+", parts);
        _hotkeyBox.AccessibleDescription =
            $"Gewählter Aufnahme-Shortcut: {_hotkeyBox.Text}. Klicken und drücken, um ihn zu ändern.";
        SetStatus("Neue Tastenkombination gewählt. Zum Aktivieren speichern.", TextMuted);
    }

    internal static bool IsSafeHotkeySelection(
        Keys keyCode,
        Keys modifiers)
    {
        var hasModifier =
            (modifiers & (Keys.Control | Keys.Alt | Keys.Shift)) != Keys.None;
        var keyValue = (int)keyCode;
        return hasModifier ||
               keyValue >= (int)Keys.F1 &&
               keyValue <= (int)Keys.F24;
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
        _statusLabel.AccessibleDescription = text;
    }

    private static TableLayoutPanel CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = WindowBackground,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(40, 22, 40, 18),
            Margin = Padding.Empty,
            TabStop = false,
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "ORhom",
            AccessibleDescription = "Einfaches Diktieren in jedes Textfeld."
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var brand = CreateBodyLabel(
            "ORHOM · DIKTIEREN",
            Info,
            new Font("Segoe UI", 8.5f, FontStyle.Bold));
        brand.Margin = new Padding(0, 0, 0, 4);
        header.Controls.Add(brand, 0, 0);

        var hero = CreateBodyLabel(
            "Sprich. ORhom schreibt.",
            TextPrimary,
            new Font("Segoe UI", 24, FontStyle.Bold));
        hero.Margin = new Padding(0);
        hero.AccessibleRole = AccessibleRole.StaticText;
        header.Controls.Add(hero, 0, 1);

        var subtitle = CreateBodyLabel(
            "Diktiere direkt in jedes Textfeld – lokal und ohne Fensterwechsel.",
            TextSecondary,
            new Font("Segoe UI", 10.5f));
        subtitle.Margin = new Padding(2, 5, 0, 0);
        header.Controls.Add(subtitle, 0, 2);
        return header;
    }

    private static TableLayoutPanel CreateStepCard(string number, string title, string description)
    {
        var card = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Surface,
            BorderStyle = BorderStyle.FixedSingle,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(20, 18, 20, 18),
            Margin = Padding.Empty,
            TabStop = false,
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = $"Schritt {number}: {title}",
            AccessibleDescription = description
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var badge = new Label
        {
            Text = number,
            AutoSize = false,
            Size = new Size(32, 32),
            Margin = new Padding(0, 1, 12, 0),
            BackColor = Primary,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            TabStop = false,
            AccessibleName = $"Schritt {number}"
        };
        card.Controls.Add(badge, 0, 0);
        card.SetRowSpan(badge, 2);

        var titleLabel = CreateBodyLabel(
            title,
            TextPrimary,
            new Font("Segoe UI", 12.5f, FontStyle.Bold));
        titleLabel.Margin = Padding.Empty;
        card.Controls.Add(titleLabel, 1, 0);

        var descriptionLabel = CreateBodyLabel(description, TextSecondary);
        descriptionLabel.Margin = new Padding(0, 3, 0, 0);
        card.Controls.Add(descriptionLabel, 1, 1);
        return card;
    }

    private static TableLayoutPanel CreateFieldRow(Control field, Button refreshButton)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 1,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            TabStop = false
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 4, 12, 3);
        refreshButton.Dock = DockStyle.Fill;
        refreshButton.Margin = Padding.Empty;
        row.Controls.Add(field, 0, 0);
        row.Controls.Add(refreshButton, 1, 0);
        return row;
    }

    private static ComboBox CreateComboBox(string accessibleName, string accessibleDescription) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = RaisedSurface,
        ForeColor = TextPrimary,
        IntegralHeight = false,
        DropDownHeight = 240,
        MaxDropDownItems = 10,
        AccessibleName = accessibleName,
        AccessibleDescription = accessibleDescription
    };

    private static Label CreateBodyLabel(string text, Color color, Font? font = null) => new()
    {
        Text = text,
        Font = font ?? new Font("Segoe UI", 9.25f),
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoSize = true,
        AutoEllipsis = false,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft,
        TabStop = false
    };

    private static Button CreateRefreshButton(
        string accessibleName,
        string accessibleDescription)
    {
        var button = CreateButton(
            "↻ Aktualisieren",
            secondary: true,
            accessibleDescription);
        button.Font = new Font("Segoe UI", 8.75f, FontStyle.Bold);
        button.AccessibleName = accessibleName;
        return button;
    }

    private static Button CreateButton(
        string text,
        bool secondary,
        string accessibleDescription)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = secondary ? RaisedSurface : Primary,
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            UseMnemonic = false,
            UseVisualStyleBackColor = false,
            AccessibleName = text,
            AccessibleDescription = accessibleDescription
        };
        button.FlatAppearance.BorderSize = secondary ? 1 : 0;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor =
            secondary ? Color.FromArgb(42, 48, 60) : PrimaryHover;
        button.FlatAppearance.MouseDownBackColor =
            secondary ? Color.FromArgb(36, 41, 52) : PrimaryPressed;
        return button;
    }

    private static void AddFullWidthRow(
        TableLayoutPanel table,
        Control control,
        Padding margin)
    {
        var row = table.RowCount;
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Fill;
        control.Margin = margin;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, table.ColumnCount);
    }

    private static void AddStackRow(
        TableLayoutPanel table,
        Control control,
        Padding? margin = null)
    {
        var row = table.RowCount;
        table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Fill;
        control.Margin = margin ?? new Padding(0, 0, 0, 14);
        table.Controls.Add(control, 0, row);
    }
}

internal sealed record SettingsFormValues(
    string DictationProvider,
    string MicrophoneId,
    string MicrophoneName,
    string Hotkey,
    ChromeProfileInfo? ChromeProfile,
    DictationPreferences? DictationPreferences = null);

internal sealed record DictationLanguageOption(string Code, string Label)
{
    public override string ToString() => Label;
}

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
