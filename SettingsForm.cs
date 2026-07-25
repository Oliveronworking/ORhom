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
        Text = "ORhom â€“ Diktieren";
        AccessibleName = "ORhom Einstellungen";
        AccessibleDescription =
            "Richtet Diktiermodus, Mikrofon und Tastenkombination fÃ¼r ORhom ein.";
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
            AccessibleDescription = "EnthÃ¤lt die drei Schritte zur Einrichtung von ORhom."
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
            "Modus wÃ¤hlen",
            "StandardmÃ¤ÃŸig lÃ¤uft die Spracherkennung schnell und lokal auf diesem PC.");
        providerStep.Name = "providerStepCard";
        providerStep.TabIndex = 0;
        AddStackRow(stepsLayout, providerStep);

        _providerCombo = CreateComboBox(
            "Diktiermodus",
            "WÃ¤hlt zwischen lokaler Spracherkennung und dem ChatGPT-Browser-Fallback.");
        _providerCombo.Name = "providerCombo";
        _providerCombo.TabIndex = 0;
        _providerCombo.Items.Add(new DictationProviderOption(
            DictationProviders.LocalWhisper,
            "Lokal Â· whisper.cpp Â· Vulkan Â· Deutsch"));
        _providerCombo.Items.Add(new DictationProviderOption(
            DictationProviders.ChatGptBrowser,
            "ChatGPT-Browser-Diktierung Â· Fallback"));
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
            AccessibleName = "Chrome-Profil fÃ¼r den Browser-Fallback",
            AccessibleDescription =
                "WÃ¤hlt das Chrome-Profil, in dem ChatGPT bereits angemeldet ist."
        };
        _profileCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var profileTitle = CreateBodyLabel(
            "Chrome-Profil",
            TextPrimary,
            new Font("Segoe UI", 9.25f, FontStyle.Bold));
        AddStackRow(_profileCard, profileTitle);

        var profileHelp = CreateBodyLabel(
            "WÃ¤hle das Profil, in dem du bei ChatGPT angemeldet bist.",
            TextSecondary);
        AddStackRow(_profileCard, profileHelp, new Padding(0, 3, 0, 10));

        _chromeProfileCombo = CreateComboBox(
            "Chrome-Profil",
            "Das Chrome-Profil, das ORhom fÃ¼r die ChatGPT-Browser-Diktierung verwendet.");
        _chromeProfileCombo.Name = "chromeProfileCombo";
        _chromeProfileCombo.TabIndex = 0;
        _profileRefreshButton = CreateRefreshButton(
            "Chrome-Profile aktualisieren",
            "Sucht erneut nach verfÃ¼gbaren Chrome-Profilen.");
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
            "Mikrofon auswÃ¤hlen",
            "WÃ¤hle den Windows-Audioeingang, den ORhom fÃ¼r deine Diktate verwenden soll.");
        microphoneStep.Name = "microphoneStepCard";
        microphoneStep.TabIndex = 1;
        AddStackRow(stepsLayout, microphoneStep);

        _microphoneCombo = CreateComboBox(
            "Mikrofon",
            "Der Windows-Audioeingang, Ã¼ber den ORhom Sprache aufnimmt.");
        _microphoneCombo.Name = "microphoneCombo";
        _microphoneCombo.TabIndex = 0;
        _microphoneCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_reloadingMicrophones)
            {
                SetStatus("Mikrofon ausgewÃ¤hlt. Zum Aktivieren speichern.", TextMuted);
            }
        };
        _microphoneRefreshButton = CreateRefreshButton(
            "Mikrofone aktualisieren",
            "Sucht erneut nach aktiven Windows-AudioeingÃ¤ngen.");
        _microphoneRefreshButton.Name = "microphoneRefreshButton";
        _microphoneRefreshButton.TabIndex = 1;
        _microphoneRefreshButton.Click += (_, _) => ReloadMicrophones(showStatus: true);
        AddFullWidthRow(
            microphoneStep,
            CreateFieldRow(_microphoneCombo, _microphoneRefreshButton),
            new Padding(44, 14, 0, 0));

        var microphoneHelp = CreateBodyLabel(
            "Neu verbundene GerÃ¤te erscheinen automatisch. Du kannst die Liste auch manuell aktualisieren.",
            TextMuted,
            new Font("Segoe UI", 8.75f));
        AddFullWidthRow(microphoneStep, microphoneHelp, new Padding(44, 7, 0, 0));

        var hotkeyStep = CreateStepCard(
            "3",
            "Shortcut festlegen",
            "DrÃ¼cke ihn in jeder App, um die Aufnahme zu starten oder zu stoppen.");
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
            "Klicke rechts in das Feld und drÃ¼cke die gewÃ¼nschte Tastenkombination.",
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
                "Klicken und anschlieÃŸend die gewÃ¼nschte Tastenkombination drÃ¼cken."
        };
        _hotkeyBox.KeyDown += CaptureHotkey;
        hotkeyLayout.Controls.Add(_hotkeyBox, 1, 0);
        AddFullWidthRow(hotkeyStep, hotkeyLayout, new Padding(44, 14, 0, 0));

        var hotkeyHelp = CreateBodyLabel(
            "Tipp: Strg, Alt oder Umschalt helfen, versehentliche Aufnahmen zu vermeiden.",
            TextMuted,
            new Font("Segoe UI", 8.75f));
        AddFullWidthRow(hotkeyStep, hotkeyHelp, new Padding(44, 7, 0, 0));

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
            "Bereit. ORhom lÃ¤uft nach dem SchlieÃŸen im Infobereich weiter.",
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
            "Speichern & losdiktiereÛNý¶‰žËkºwµçI‘¥¹…±%¹½É•…Í”¤¤ì(€€€€€€€€€€€¥˜€¡µ…Ñ¡¥¹M•±•Ñ¥½¸¥Ì¹½Ð¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}µ¥É½Á¡½¹•½µ‰¼¹M•±•Ñ•‘%Ñ•´€ôµ…Ñ¡¥¹M•±•Ñ¥½¸ì(€€€€€€€€€€€ô(€€€€€€€€€€€•±Í”¥˜€¡}µ¥É½Á¡½¹•½µ‰¼¹%Ñ•µÌ¹½Õ¹Ð€ø€À¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}µ¥É½Á¡½¹•½µ‰¼¹M•±•Ñ•‘%¹‘•à€ô€Àì(€€€€€€€€€€€ô(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€}µ¥É½Á¡½¹•½µ‰¼¹¹‘UÁ‘…Ñ” ¤ì(€€€€€€€€€€€}É•±½…‘¥¹5¥É½Á¡½¹•Ì€ô™…±Í”ì(€€€€€€€ô((€€€€€€€¥˜€¡‘•Ù¥•Ì¹½Õ¹Ð€ôô€À¤(€€€€€€€ì(€€€€€€€€€€€M•ÑMÑ…ÑÕÌ ‰-•¥¸…­Ñ¥Ù•Ì5¥­É½™½¸•É­…¹¹Ð¸	¥ÑÑ”•¥¸•Ë‘ÐÙ•É‰¥¹‘•¸¸ˆ°ÉÉ½È¤ì(€€€€€€€ô(€€€€€€€•±Í”¥˜€¡Í¡½ÝMÑ…ÑÕÌñð‘•Ù¥•Í¡…¹•¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÕ™™¥à€ô‘•Ù¥•Ì¹½Õ¹Ð€ôô€Ä€ü€‰5¥­É½™½¸•É­…¹¹Ð¸ˆ€è€‰5¥­É½™½¹”•É­…¹¹Ð¸ˆì(€€€€€€€€€€€M•ÑMÑ…ÑÕÌ ‰í‘•Ù¥•Ì¹½Õ¹ÑôíÍÕ™™¥áôˆ°MÕ•ÍÌ¤ì(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥I•±½…‘¡É½µ•AÉ½™¥±•Ì¡‰½½°Í¡½ÝMÑ…ÑÕÌ°‰½½°ÁÉ•™•ÉM•ÑÑ¥¹Ì€ô™…±Í”¤(€€€ì(€€€€€€€Ù…ÈÍ•±•Ñ•‘¥É•Ñ½Éä€ôÁÉ•™•ÉM•ÑÑ¥¹Ì(€€€€€€€€€€€€ü}Í•ÑÑ¥¹Ì¹¡É½µ•AÉ½™¥±•¥É•Ñ½Éä(€€€€€€€€€€€€è€¡}¡É½µ•AÉ½™¥±•½µ‰¼¹M•±•Ñ•‘%Ñ•´…Ì¡É½µ•AÉ½™¥±•%¹™¼¤ü¹¥É•Ñ½Éå9…µ”(€€€€€€€€€€€€€€€€üü}Í•ÑÑ¥¹Ì¹¡É½µ•AÉ½™¥±•¥É•Ñ½Éäì(€€€€€€€Ù…ÈÉ•ÍÕ±Ð€ô}¡É½µ•AÉ½™¥±•Ì¹¥Í½Ù•È¡}Í•ÑÑ¥¹Ì¹¡É½µ•á•ÕÑ…‰±•A…Ñ °}Í•ÑÑ¥¹Ì¹¡É½µ•UÍ•É…Ñ…¥È¤ì(€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹	•¥¹UÁ‘…Ñ” ¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹%Ñ•µÌ¹±•…È ¤ì(€€€€€€€€€€€™½É•… €¡Ù…ÈÁÉ½™¥±”¥¸É•ÍÕ±Ð¹AÉ½™¥±•Ì¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹%Ñ•µÌ¹‘¡ÁÉ½™¥±”¤ì(€€€€€€€€€€€ô((€€€€€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹M•±•Ñ•‘%Ñ•´€ôÉ•ÍÕ±Ð¹AÉ½™¥±•Ì¹¥ÉÍÑ=É•™…Õ±Ð¡ÁÉ½™¥±”€ôø(€€€€€€€€€€€€€€€ÁÉ½™¥±”¹¥É•Ñ½Éå9…µ”¹ÅÕ…±Ì¡Í•±•Ñ•‘¥É•Ñ½Éä°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤¤ì(€€€€€€€€€€€¥˜€¡}¡É½µ•AÉ½™¥±•½µ‰¼¹M•±•Ñ•‘%¹‘•à€ð€À€˜˜}¡É½µ•AÉ½™¥±•½µ‰¼¹%Ñ•µÌ¹½Õ¹Ð€ø€À¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹M•±•Ñ•‘%¹‘•à€ô€Àì(€€€€€€€€€€€ô(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹¹‘UÁ‘…Ñ” ¤ì(€€€€€€€ô((€€€€€€€¥˜€¡Í¡½ÝMÑ…ÑÕÌ€˜˜€…%Í1½…±AÉ½Ù¥‘•È¤(€€€€€€€ì(€€€€€€€€€€€M•ÑMÑ…ÑÕÌ (€€€€€€€€€€€€€€€É•ÍÕ±Ð¹AÉ½™¥±•Ì¹½Õ¹Ð€ôô€À(€€€€€€€€€€€€€€€€€€€€ü€‰-•¥¹”¡É½µ”µAÉ½™¥±”•™Õ¹‘•¸¸%ÍÐ½½±”¡É½µ”¥¹ÍÑ…±±¥•ÉÐüˆ(€€€€€€€€€€€€€€€€€€€€è€‰íÉ•ÍÕ±Ð¹AÉ½™¥±•Ì¹½Õ¹Ñô¡É½µ”µAÉ½™¥°¡”¤•™Õ¹‘•¸¸ˆ°(€€€€€€€€€€€€€€€É•ÍÕ±Ð¹AÉ½™¥±•Ì¹½Õ¹Ð€ôô€À€üÉÉ½È€èQ•áÑ5ÕÑ•¤ì(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”…Íå¹ŒQ…Í¬M…Ù•Íå¹Œ ¤(€€€ì(€€€€€€€¥˜€¡}µ¥É½Á¡½¹•½µ‰¼¹M•±•Ñ•‘%Ñ•´¥Ì¹½ÐÕ‘¥½%¹ÁÕÑ•Ù¥•%¹™¼µ¥É½Á¡½¹”¤(€€€€€€€ì(€€€€€€€€€€€M•ÑMÑ…ÑÕÌ ‰	¥ÑÑ”éÕ•ÉÍÐ•¥¸…­Ñ¥Ù•Ì5¥­É½™½¸…ÕÍß‘¡±•¸¸ˆ°ÉÉ½È¤ì(€€€€€€€€€€€}µ¥É½Á¡½¹•½µ‰¼¹½ÕÌ ¤ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€Ù…ÈÁÉ½Ù¥‘•È€ô€¡}ÁÉ½Ù¥‘•É½µ‰¼¹M•±•Ñ•‘%Ñ•´…Ì¥Ñ…Ñ¥½¹AÉ½Ù¥‘•É=ÁÑ¥½¸¤ü¹Y…±Õ”(€€€€€€€€€€€€üü¥Ñ…Ñ¥½¹AÉ½Ù¥‘•ÉÌ¹1½…±]¡¥ÍÁ•Èì(€€€€€€€Ù…È¡É½µ•AÉ½™¥±”€ô}¡É½µ•AÉ½™¥±•½µ‰¼¹M•±•Ñ•‘%Ñ•´…Ì¡É½µ•AÉ½™¥±•%¹™¼ì(€€€€€€€¥˜€ …¥Ñ…Ñ¥½¹AÉ½Ù¥‘•ÉÌ¹%Í1½…°¡ÁÉ½Ù¥‘•È¤€˜˜¡É½µ•AÉ½™¥±”¥Ì¹Õ±°¤(€€€€€€€ì(€€€€€€€€€€€M•ÑMÑ…ÑÕÌ ‰	¥ÑÑ”›ñÈ‘•¸	É½ÝÍ•Èµ…±±‰…¬•¥¸¡É½µ”µAÉ½™¥°…ÕÍß‘¡±•¸¸ˆ°ÉÉ½È¤ì(€€€€€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹½ÕÌ ¤ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€}Í…Ù•%¹AÉ½É•ÍÌ€ôÑÉÕ”ì(€€€€€€€}µ¥É½Á¡½¹•I•™É•Í¡Q¥µ•È¹MÑ½À ¤ì(€€€€€€€M•Ñ%¹ÁÕÑÍ¹…‰±•¡™…±Í”¤ì(€€€€€€€M•ÑMÑ…ÑÕÌ ‰¥¹ÍÑ•±±Õ¹•¸Ý•É‘•¸Í¥¡•Èƒñ‰•É¹½µµ•¸ƒŠ˜ˆ°%¹™¼¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÉ•ÍÕ±Ð€ô…Ý…¥Ð}Í…Ù•Íå¹Œ¡¹•ÜM•ÑÑ¥¹Í½ÉµY…±Õ•Ì (€€€€€€€€€€€€€€€ÁÉ½Ù¥‘•È°(€€€€€€€€€€€€€€€µ¥É½Á¡½¹”¹%°(€€€€€€€€€€€€€€€µ¥É½Á¡½¹”¹¥ÍÁ±…å9…µ”°(€€€€€€€€€€€€€€€}¡½Ñ­•å	½à¹Q•áÐ¹QÉ¥´ ¤°(€€€€€€€€€€€€€€€¡É½µ•AÉ½™¥±”¤¤ì(€€€€€€€€€€€¥˜€ …É•ÍÕ±Ð¹=¬¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€M•ÑMÑ…ÑÕÌ¡É•ÍÕ±Ð¹5•ÍÍ…”°ÉÉ½È¤ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€€€€€ô((€€€€€€€€€€€M•ÑMÑ…ÑÕÌ (€€€€€€€€€€€€€€€€‰	•É•¥Ðƒ
ÜíÉ•ÍÕ±Ð¹5¥É½Á¡½¹•9…µ•ôƒ
ÜíÉ•ÍÕ±Ð¹!½Ñ­•åôˆ°(€€€€€€€€€€€€€€€MÕ•ÍÌ¤ì(€€€€€€€€€€€…Ý…¥ÐQ…Í¬¹•±…ä ÔÔÀ¤ì(€€€€€€€€€€€!¥‘” ¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€}Í…Ù•%¹AÉ½É•ÍÌ€ô™…±Í”ì(€€€€€€€€€€€M•Ñ%¹ÁÕÑÍ¹…‰±•¡ÑÉÕ”¤ì(€€€€€€€€€€€UÁ‘…Ñ•AÉ½Ù¥‘•ÉU¤¡ÕÁ‘…Ñ•MÑ…ÑÕÌè™…±Í”¤ì(€€€€€€€€€€€¥˜€¡Y¥Í¥‰±”¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}µ¥É½Á¡½¹•I•™É•Í¡Q¥µ•È¹MÑ…ÉÐ ¤ì(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥M•Ñ%¹ÁÕÑÍ¹…‰±•¡‰½½°•¹…‰±•¤(€€€ì(€€€€€€€}Í…Ù•	ÕÑÑ½¸¹¹…‰±•€ô•¹…‰±•ì(€€€€€€€}ÁÉ½Ù¥‘•É½µ‰¼¹¹…‰±•€ô•¹…‰±•ì(€€€€€€€}µ¥É½Á¡½¹•½µ‰¼¹¹…‰±•€ô•¹…‰±•ì(€€€€€€€}µ¥É½Á¡½¹•I•™É•Í¡	ÕÑÑ½¸¹¹…‰±•€ô•¹…‰±•ì(€€€€€€€}¡É½µ•AÉ½™¥±•½µ‰¼¹¹…‰±•€ô•¹…‰±•€˜˜€…%Í1½…±AÉ½Ù¥‘•Èì(€€€€€€€}ÁÉ½™¥±•I•™É•Í¡	ÕÑÑ½¸¹¹…‰±•€ô•¹…‰±•€˜˜€…%Í1½…±AÉ½Ù¥‘•Èì(€€€€€€€}ÁÉ½™¥±•…É¹¹…‰±•€ô•¹…‰±•€˜˜€…%Í1½…±AÉ½Ù¥‘•Èì(€€€€€€€}¡½Ñ­•å	½à¹¹…‰±•€ô•¹…‰±•ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥…ÁÑÕÉ•!½Ñ­•ä¡½‰©•ÐüÍ•¹‘•È°-•åÙ•¹ÑÉÌ”¤(€€€ì(€€€€€€€”¹MÕÁÁÉ•ÍÍ-•åAÉ•ÍÌ€ôÑÉÕ”ì(€€€€€€€”¹!…¹‘±•€ôÑÉÕ”ì(€€€€€€€¥˜€¡”¹-•å½‘”¥Ì-•åÌ¹½¹ÑÉ½±-•ä½È-•åÌ¹M¡¥™Ñ-•ä½È-•åÌ¹5•¹Ô½È-•åÌ¹1]¥¸½È-•åÌ¹I]¥¸¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€¥˜€¡”¹-•å½‘”€ôô-•åÌ¹Í…Á”¤(€€€€€€€ì(€€€€€€€€€€€M•ÑMÑ…ÑÕÌ ‰Í…Á”¥ÍÐ›ñÈ‘•¸Õ™¹…¡µ•…‰‰ÉÕ É•Í•ÉÙ¥•ÉÐ¸ˆ°ÉÉ½È¤ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€¥˜€ …%ÍM…™•!½Ñ­•åM•±•Ñ¥½¸¡”¹-•å½‘”°”¹5½‘¥™¥•ÉÌ¤¤(€€€€€€€ì(€€€€€€€€€€€M•ÑMÑ…ÑÕÌ (€€€€€€€€€€€€€€€€‰¥¹é•±¹”	Õ¡ÍÑ…‰•¸°i…¡±•¸Õ¹1••ÉÑ…ÍÑ”Í¥¹…±Ì±½‰…±•ÈM¡½ÉÑÕÐéÔ±•¥¡Ð…ÕÍ³ÙÍ‰…È¸	¥ÑÑ”MÑÉœ°±Ð½‘•ÈUµÍ¡…±ÐÙ•ÉÝ•¹‘•¸ƒŠL½‘•È•¥¹”µQ…ÍÑ”ß‘¡±•¸¸ˆ°(€€€€€€€€€€€€€€€ÉÉ½È¤ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€Ù…ÈÁ…ÉÑÌ€ô¹•Ü1¥ÍÐñÍÑÉ¥¹œø ¤ì(€€€€€€€¥˜€¡”¹½¹ÑÉ½°¤Á…ÉÑÌ¹‘ ‰ÑÉ°ˆ¤ì(€€€€€€€¥˜€¡”¹±Ð¤Á…ÉÑÌ¹‘ ‰±Ðˆ¤ì(€€€€€€€¥˜€¡”¹M¡¥™Ð¤Á…ÉÑÌ¹‘ ‰M¡¥™Ðˆ¤ì(€€€€€€€Á…ÉÑÌ¹‘¡”¹-•å½‘”¹Q½MÑÉ¥¹œ ¤¤ì(€€€€€€€}¡½Ñ­•å	½à¹Q•áÐ€ôÍÑÉ¥¹œ¹)½¥¸ ˆ¬ˆ°Á…ÉÑÌ¤ì(€€€€€€€}¡½Ñ­•å	½à¹•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ô(€€€€€€€€€€€€‰•ß‘¡±Ñ•ÈÕ™¹…¡µ”µM¡½ÉÑÕÐèí}¡½Ñ­•å	½à¹Q•áÑô¸-±¥­•¸Õ¹‘Ëñ­•¸°Õ´¥¡¸éÔƒ‘¹‘•É¸¸ˆì(€€€€€€€M•ÑMÑ…ÑÕÌ ‰9•Õ”Q…ÍÑ•¹­½µ‰¥¹…Ñ¥½¸•ß‘¡±Ð¸iÕ´­Ñ¥Ù¥•É•¸ÍÁ•¥¡•É¸¸ˆ°Q•áÑ5ÕÑ•¤ì(€€€ô((€€€¥¹Ñ•É¹…°ÍÑ…Ñ¥Œ‰½½°%ÍM…™•!½Ñ­•åM•±•Ñ¥½¸ (€€€€€€€-•åÌ­•å½‘”°(€€€€€€€-•åÌµ½‘¥™¥•ÉÌ¤(€€€ì(€€€€€€€Ù…È¡…Í5½‘¥™¥•È€ô(€€€€€€€€€€€€¡µ½‘¥™¥•ÉÌ€˜€¡-•åÌ¹½¹ÑÉ½°ð-•åÌ¹±Ðð-•åÌ¹M¡¥™Ð¤¤€„ô-•åÌ¹9½¹”ì(€€€€€€€Ù…È­•åY…±Õ”€ô€¡¥¹Ð¥­•å½‘”ì(€€€€€€€É•ÑÕÉ¸¡…Í5½‘¥™¥•Èñð(€€€€€€€€€€€€€€­•åY…±Õ”€øô€¡¥¹Ð¥-•åÌ¹Ä€˜˜(€€€€€€€€€€€€€€­•åY…±Õ”€ðô€¡¥¹Ð¥-•åÌ¹ÈÐì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥=¹½Éµ±½Í¥¹œ¡½‰©•ÐüÍ•¹‘•È°½Éµ±½Í¥¹Ù•¹ÑÉÌ”¤(€€€ì(€€€€€€€¥˜€¡}…±±½Ý±½Í”ñð”¹±½Í•I•…Í½¸€ôô±½Í•I•…Í½¸¹ÁÁ±¥…Ñ¥½¹á¥Ñ…±°¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ì(€€€€€€€ô((€€€€€€€”¹…¹•°€ôÑÉÕ”ì(€€€€€€€!¥‘” ¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥M•ÑMÑ…ÑÕÌ¡ÍÑÉ¥¹œÑ•áÐ°½±½È½±½È¤(€€€ì(€€€€€€€}ÍÑ…ÑÕÍ1…‰•°¹Q•áÐ€ôÑ•áÐì(€€€€€€€}ÍÑ…ÑÕÍ1…‰•°¹½É•½±½È€ô½±½Èì(€€€€€€€}ÍÑ…ÑÕÍ1…‰•°¹•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ôÑ•áÐì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒQ…‰±•1…å½ÕÑA…¹•°É•…Ñ•!•…‘•È ¤(€€€ì(€€€€€€€Ù…È¡•…‘•È€ô¹•ÜQ…‰±•1…å½ÕÑA…¹•°(€€€€€€€ì(€€€€€€€€€€€½¬€ô½­MÑå±”¹¥±°°(€€€€€€€€€€€ÕÑ½M¥é”€ôÑÉÕ”°(€€€€€€€€€€€ÕÑ½M¥é•5½‘”€ôÕÑ½M¥é•5½‘”¹É½Ý¹‘M¡É¥¹¬°(€€€€€€€€€€€	…­½±½È€ô]¥¹‘½Ý	…­É½Õ¹°(€€€€€€€€€€€½±Õµ¹½Õ¹Ð€ô€Ä°(€€€€€€€€€€€I½Ý½Õ¹Ð€ô€Ì°(€€€€€€€€€€€A…‘‘¥¹œ€ô¹•ÜA…‘‘¥¹œ ÐÀ°€ÈÈ°€ÐÀ°€Äà¤°(€€€€€€€€€€€5…É¥¸€ôA…‘‘¥¹œ¹µÁÑä°(€€€€€€€€€€€Q…‰MÑ½À€ô™…±Í”°(€€€€€€€€€€€•ÍÍ¥‰±•I½±”€ô•ÍÍ¥‰±•I½±”¹É½ÕÁ¥¹œ°(€€€€€€€€€€€•ÍÍ¥‰±•9…µ”€ô€‰=I¡½´ˆ°(€€€€€€€€€€€•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ô€‰¥¹™…¡•Ì¥­Ñ¥•É•¸¥¸©•‘•ÌQ•áÑ™•±¸ˆ(€€€€€€€ôì(€€€€€€€¡•…‘•È¹½±Õµ¹MÑå±•Ì¹‘¡¹•Ü½±Õµ¹MÑå±”¡M¥é•QåÁ”¹A•É•¹Ð°€ÄÀÀ¤¤ì(€€€€€€€¡•…‘•È¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹ÕÑ½M¥é”¤¤ì(€€€€€€€¡•…‘•È¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹ÕÑ½M¥é”¤¤ì(€€€€€€€¡•…‘•È¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹ÕÑ½M¥é”¤¤ì((€€€€€€€Ù…È‰É…¹€ôÉ•…Ñ•	½‘å1…‰•° (€€€€€€€€€€€€‰=I!=4ƒ
Ü%-Q%I8ˆ°(€€€€€€€€€€€%¹™¼°(€€€€€€€€€€€¹•Ü½¹Ð ‰M•½”U$ˆ°€à¸Õ˜°½¹ÑMÑå±”¹	½±¤¤ì(€€€€€€€‰É…¹¹5…É¥¸€ô¹•ÜA…‘‘¥¹œ À°€À°€À°€Ð¤ì(€€€€€€€¡•…‘•È¹½¹ÑÉ½±Ì¹‘¡‰É…¹°€À°€À¤ì((€€€€€€€Ù…È¡•É¼€ôÉ•…Ñ•	½‘å1…‰•° (€€€€€€€€€€€€‰MÁÉ¥ ¸=I¡½´Í¡É•¥‰Ð¸ˆ°(€€€€€€€€€€€Q•áÑAÉ¥µ…Éä°(€€€€€€€€€€€¹•Ü½¹Ð ‰M•½”U$ˆ°€ÈÐ°½¹ÑMÑå±”¹	½±¤¤ì(€€€€€€€¡•É¼¹5…É¥¸€ô¹•ÜA…‘‘¥¹œ À¤ì(€€€€€€€¡•É¼¹•ÍÍ¥‰±•I½±”€ô•ÍÍ¥‰±•I½±”¹MÑ…Ñ¥Q•áÐì(€€€€€€€¡•…‘•È¹½¹ÑÉ½±Ì¹‘¡¡•É¼°€À°€Ä¤ì((€€€€€€€Ù…ÈÍÕ‰Ñ¥Ñ±”€ôÉ•…Ñ•	½‘å1…‰•° (€€€€€€€€€€€€‰¥­Ñ¥•É”‘¥É•­Ð¥¸©•‘•ÌQ•áÑ™•±ƒŠL±½­…°Õ¹½¡¹”•¹ÍÑ•ÉÝ•¡Í•°¸ˆ°(€€€€€€€€€€€Q•áÑM•½¹‘…Éä°(€€€€€€€€€€€¹•Ü½¹Ð ‰M•½”U$ˆ°€ÄÀ¸Õ˜¤¤ì(€€€€€€€ÍÕ‰Ñ¥Ñ±”¹5…É¥¸€ô¹•ÜA…‘‘¥¹œ È°€Ô°€À°€À¤ì(€€€€€€€¡•…‘•È¹½¹ÑÉ½±Ì¹‘¡ÍÕ‰Ñ¥Ñ±”°€À°€È¤ì(€€€€€€€É•ÑÕÉ¸¡•…‘•Èì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒQ…‰±•1…å½ÕÑA…¹•°É•…Ñ•MÑ•Á…É¡ÍÑÉ¥¹œ¹Õµ‰•È°ÍÑÉ¥¹œÑ¥Ñ±”°ÍÑÉ¥¹œ‘•ÍÉ¥ÁÑ¥½¸¤(€€€ì(€€€€€€€Ù…È…É€ô¹•ÜQ…‰±•1…å½ÕÑA…¹•°(€€€€€€€ì(€€€€€€€€€€€ÕÑ½M¥é”€ôÑÉÕ”°(€€€€€€€€€€€ÕÑ½M¥é•5½‘”€ôÕÑ½M¥é•5½‘”¹É½Ý¹‘M¡É¥¹¬°(€€€€€€€€€€€½¬€ô½­MÑå±”¹¥±°°(€€€€€€€€€€€	…­½±½È€ôMÕÉ™…”°(€€€€€€€€€€€	½É‘•ÉMÑå±”€ô	½É‘•ÉMÑå±”¹¥á•‘M¥¹±”°(€€€€€€€€€€€½±Õµ¹½Õ¹Ð€ô€È°(€€€€€€€€€€€I½Ý½Õ¹Ð€ô€È°(€€€€€€€€€€€A…‘‘¥¹œ€ô¹•ÜA…‘‘¥¹œ ÈÀ°€Äà°€ÈÀ°€Äà¤°(€€€€€€€€€€€5…É¥¸€ôA…‘‘¥¹œ¹µÁÑä°(€€€€€€€€€€€Q…‰MÑ½À€ô™…±Í”°(€€€€€€€€€€€•ÍÍ¥‰±•I½±”€ô•ÍÍ¥‰±•I½±”¹É½ÕÁ¥¹œ°(€€€€€€€€€€€•ÍÍ¥‰±•9…µ”€ô€‰M¡É¥ÑÐí¹Õµ‰•ÉôèíÑ¥Ñ±•ôˆ°(€€€€€€€€€€€•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ô‘•ÍÉ¥ÁÑ¥½¸(€€€€€€€ôì(€€€€€€€…É¹½±Õµ¹MÑå±•Ì¹‘¡¹•Ü½±Õµ¹MÑå±”¡M¥é•QåÁ”¹‰Í½±ÕÑ”°€ÐÐ¤¤ì(€€€€€€€…É¹½±Õµ¹MÑå±•Ì¹‘¡¹•Ü½±Õµ¹MÑå±”¡M¥é•QåÁ”¹A•É•¹Ð°€ÄÀÀ¤¤ì(€€€€€€€…É¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹ÕÑ½M¥é”¤¤ì(€€€€€€€…É¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹ÕÑ½M¥é”¤¤ì((€€€€€€€Ù…È‰…‘”€ô¹•Ü1…‰•°(€€€€€€€ì(€€€€€€€€€€€Q•áÐ€ô¹Õµ‰•È°(€€€€€€€€€€€ÕÑ½M¥é”€ô™…±Í”°(€€€€€€€€€€€M¥é”€ô¹•ÜM¥é” ÌÈ°€ÌÈ¤°(€€€€€€€€€€€5…É¥¸€ô¹•ÜA…‘‘¥¹œ À°€Ä°€ÄÈ°€À¤°(€€€€€€€€€€€	…­½±½È€ôAÉ¥µ…Éä°(€€€€€€€€€€€½É•½±½È€ô½±½È¹]¡¥Ñ”°(€€€€€€€€€€€½¹Ð€ô¹•Ü½¹Ð ‰M•½”U$ˆ°€ÄÀ°½¹ÑMÑå±”¹	½±¤°(€€€€€€€€€€€Q•áÑ±¥¸€ô½¹Ñ•¹Ñ±¥¹µ•¹Ð¹5¥‘‘±••¹Ñ•È°(€€€€€€€€€€€Q…‰MÑ½À€ô™…±Í”°(€€€€€€€€€€€•ÍÍ¥‰±•9…µ”€ô€‰M¡É¥ÑÐí¹Õµ‰•Éôˆ(€€€€€€€ôì(€€€€€€€…É¹½¹ÑÉ½±Ì¹‘¡‰…‘”°€À°€À¤ì(€€€€€€€…É¹M•ÑI½ÝMÁ…¸¡‰…‘”°€È¤ì((€€€€€€€Ù…ÈÑ¥Ñ±•1…‰•°€ôÉ•…Ñ•	½‘å1…‰•° (€€€€€€€€€€€Ñ¥Ñ±”°(€€€€€€€€€€€Q•áÑAÉ¥µ…Éä°(€€€€€€€€€€€¹•Ü½¹Ð ‰M•½”U$ˆ°€ÄÈ¸Õ˜°½¹ÑMÑå±”¹	½±¤¤ì(€€€€€€€Ñ¥Ñ±•1…‰•°¹5…É¥¸€ôA…‘‘¥¹œ¹µÁÑäì(€€€€€€€…É¹½¹ÑÉ½±Ì¹‘¡Ñ¥Ñ±•1…‰•°°€Ä°€À¤ì((€€€€€€€Ù…È‘•ÍÉ¥ÁÑ¥½¹1…‰•°€ôÉ•…Ñ•	½‘å1…‰•°¡‘•ÍÉ¥ÁÑ¥½¸°Q•áÑM•½¹‘…Éä¤ì(€€€€€€€‘•ÍÉ¥ÁÑ¥½¹1…‰•°¹5…É¥¸€ô¹•ÜA…‘‘¥¹œ À°€Ì°€À°€À¤ì(€€€€€€€…É¹½¹ÑÉ½±Ì¹‘¡‘•ÍÉ¥ÁÑ¥½¹1…‰•°°€Ä°€Ä¤ì(€€€€€€€É•ÑÕÉ¸…Éì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒQ…‰±•1…å½ÕÑA…¹•°É•…Ñ•¥•±‘I½Ü¡½¹ÑÉ½°™¥•±°	ÕÑÑ½¸É•™É•Í¡	ÕÑÑ½¸¤(€€€ì(€€€€€€€Ù…ÈÉ½Ü€ô¹•ÜQ…‰±•1…å½ÕÑA…¹•°(€€€€€€€ì(€€€€€€€€€€€ÕÑ½M¥é”€ôÑÉÕ”°(€€€€€€€€€€€ÕÑ½M¥é•5½‘”€ôÕÑ½M¥é•5½‘”¹É½Ý¹‘M¡É¥¹¬°(€€€€€€€€€€€½¬€ô½­MÑå±”¹¥±°°(€€€€€€€€€€€	…­½±½È€ô½±½È¹QÉ…¹ÍÁ…É•¹Ð°(€€€€€€€€€€€½±Õµ¹½Õ¹Ð€ô€È°(€€€€€€€€€€€I½Ý½Õ¹Ð€ô€Ä°(€€€€€€€€€€€A…‘‘¥¹œ€ôA…‘‘¥¹œ¹µÁÑä°(€€€€€€€€€€€5…É¥¸€ôA…‘‘¥¹œ¹µÁÑä°(€€€€€€€€€€€Q…‰MÑ½À€ô™…±Í”(€€€€€€€ôì(€€€€€€€É½Ü¹½±Õµ¹MÑå±•Ì¹‘¡¹•Ü½±Õµ¹MÑå±”¡M¥é•QåÁ”¹A•É•¹Ð°€ÄÀÀ¤¤ì(€€€€€€€É½Ü¹½±Õµ¹MÑå±•Ì¹‘¡¹•Ü½±Õµ¹MÑå±”¡M¥é•QåÁ”¹‰Í½±ÕÑ”°€ÄÌà¤¤ì(€€€€€€€É½Ü¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹‰Í½±ÕÑ”°€Ìà¤¤ì(€€€€€€€™¥•±¹½¬€ô½­MÑå±”¹¥±°ì(€€€€€€€™¥•±¹5…É¥¸€ô¹•ÜA…‘‘¥¹œ À°€Ð°€ÄÈ°€Ì¤ì(€€€€€€€É•™É•Í¡	ÕÑÑ½¸¹½¬€ô½­MÑå±”¹¥±°ì(€€€€€€€É•™É•Í¡	ÕÑÑ½¸¹5…É¥¸€ôA…‘‘¥¹œ¹µÁÑäì(€€€€€€€É½Ü¹½¹ÑÉ½±Ì¹‘¡™¥•±°€À°€À¤ì(€€€€€€€É½Ü¹½¹ÑÉ½±Ì¹‘¡É•™É•Í¡	ÕÑÑ½¸°€Ä°€À¤ì(€€€€€€€É•ÑÕÉ¸É½Üì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ½µ‰½	½àÉ•…Ñ•½µ‰½	½à¡ÍÑÉ¥¹œ…•ÍÍ¥‰±•9…µ”°ÍÑÉ¥¹œ…•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸¤€ôø¹•Ü ¤(€€€ì(€€€€€€€É½Á½Ý¹MÑå±”€ô½µ‰½	½áMÑå±”¹É½Á½Ý¹1¥ÍÐ°(€€€€€€€±…ÑMÑå±”€ô±…ÑMÑå±”¹±…Ð°(€€€€€€€	…­½±½È€ôI…¥Í•‘MÕÉ™…”°(€€€€€€€½É•½±½È€ôQ•áÑAÉ¥µ…Éä°(€€€€€€€%¹Ñ•É…±!•¥¡Ð€ô™…±Í”°(€€€€€€€É½Á½Ý¹!•¥¡Ð€ô€ÈÐÀ°(€€€€€€€5…áÉ½Á½Ý¹%Ñ•µÌ€ô€ÄÀ°(€€€€€€€•ÍÍ¥‰±•9…µ”€ô…•ÍÍ¥‰±•9…µ”°(€€€€€€€•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ô…•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸(€€€ôì((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ1…‰•°É•…Ñ•	½‘å1…‰•°¡ÍÑÉ¥¹œÑ•áÐ°½±½È½±½È°½¹Ðü™½¹Ð€ô¹Õ±°¤€ôø¹•Ü ¤(€€€ì(€€€€€€€Q•áÐ€ôÑ•áÐ°(€€€€€€€½¹Ð€ô™½¹Ð€üü¹•Ü½¹Ð ‰M•½”U$ˆ°€ä¸ÈÕ˜¤°(€€€€€€€½É•½±½È€ô½±½È°(€€€€€€€	…­½±½È€ô½±½È¹QÉ…¹ÍÁ…É•¹Ð°(€€€€€€€ÕÑ½M¥é”€ôÑÉÕ”°(€€€€€€€ÕÑ½±±¥ÁÍ¥Ì€ô™…±Í”°(€€€€€€€½¬€ô½­MÑå±”¹¥±°°(€€€€€€€5…É¥¸€ôA…‘‘¥¹œ¹µÁÑä°(€€€€€€€Q•áÑ±¥¸€ô½¹Ñ•¹Ñ±¥¹µ•¹Ð¹5¥‘‘±•1•™Ð°(€€€€€€€Q…‰MÑ½À€ô™…±Í”(€€€ôì((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ	ÕÑÑ½¸É•…Ñ•I•™É•Í¡	ÕÑÑ½¸ (€€€€€€€ÍÑÉ¥¹œ…•ÍÍ¥‰±•9…µ”°(€€€€€€€ÍÑÉ¥¹œ…•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸¤(€€€ì(€€€€€€€Ù…È‰ÕÑÑ½¸€ôÉ•…Ñ•	ÕÑÑ½¸ (€€€€€€€€€€€€‹Šì­ÑÕ…±¥Í¥•É•¸ˆ°(€€€€€€€€€€€Í•½¹‘…ÉäèÑÉÕ”°(€€€€€€€€€€€…•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸¤ì(€€€€€€€‰ÕÑÑ½¸¹½¹Ð€ô¹•Ü½¹Ð ‰M•½”U$ˆ°€à¸ÜÕ˜°½¹ÑMÑå±”¹	½±¤ì(€€€€€€€‰ÕÑÑ½¸¹•ÍÍ¥‰±•9…µ”€ô…•ÍÍ¥‰±•9…µ”ì(€€€€€€€É•ÑÕÉ¸‰ÕÑÑ½¸ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ	ÕÑÑ½¸É•…Ñ•	ÕÑÑ½¸ (€€€€€€€ÍÑÉ¥¹œÑ•áÐ°(€€€€€€€‰½½°Í•½¹‘…Éä°(€€€€€€€ÍÑÉ¥¹œ…•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸¤(€€€ì(€€€€€€€Ù…È‰ÕÑÑ½¸€ô¹•Ü	ÕÑÑ½¸(€€€€€€€ì(€€€€€€€€€€€Q•áÐ€ôÑ•áÐ°(€€€€€€€€€€€½¬€ô½­MÑå±”¹¥±°°(€€€€€€€€€€€±…ÑMÑå±”€ô±…ÑMÑå±”¹±…Ð°(€€€€€€€€€€€	…­½±½È€ôÍ•½¹‘…Éä€üI…¥Í•‘MÕÉ™…”€èAÉ¥µ…Éä°(€€€€€€€€€€€½É•½±½È€ôQ•áÑAÉ¥µ…Éä°(€€€€€€€€€€€ÕÉÍ½È€ôÕÉÍ½ÉÌ¹!…¹°(€€€€€€€€€€€½¹Ð€ô¹•Ü½¹Ð ‰M•½”U$ˆ°€ä¸Õ˜°½¹ÑMÑå±”¹	½±¤°(€€€€€€€€€€€UÍ•5¹•µ½¹¥Œ€ô™…±Í”°(€€€€€€€€€€€UÍ•Y¥ÍÕ…±MÑå±•	…­½±½È€ô™…±Í”°(€€€€€€€€€€€•ÍÍ¥‰±•9…µ”€ôÑ•áÐ°(€€€€€€€€€€€•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸€ô…•ÍÍ¥‰±••ÍÉ¥ÁÑ¥½¸(€€€€€€€ôì(€€€€€€€‰ÕÑÑ½¸¹±…ÑÁÁ•…É…¹”¹	½É‘•ÉM¥é”€ôÍ•½¹‘…Éä€ü€Ä€è€Àì(€€€€€€€‰ÕÑÑ½¸¹±…ÑÁÁ•…É…¹”¹	½É‘•É½±½È€ô	½É‘•Èì(€€€€€€€‰ÕÑÑ½¸¹±…ÑÁÁ•…É…¹”¹5½ÕÍ•=Ù•É	…­½±½È€ô(€€€€€€€€€€€Í•½¹‘…Éä€ü½±½È¹É½µÉˆ ÐÈ°€Ðà°€ØÀ¤€èAÉ¥µ…Éå!½Ù•Èì(€€€€€€€‰ÕÑÑ½¸¹±…ÑÁÁ•…É…¹”¹5½ÕÍ•½Ý¹	…­½±½È€ô(€€€€€€€€€€€Í•½¹‘…Éä€ü½±½È¹É½µÉˆ ÌØ°€ÐÄ°€ÔÈ¤€èAÉ¥µ…ÉåAÉ•ÍÍ•ì(€€€€€€€É•ÑÕÉ¸‰ÕÑÑ½¸ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥‘‘Õ±±]¥‘Ñ¡I½Ü (€€€€€€€Q…‰±•1…å½ÕÑA…¹•°Ñ…‰±”°(€€€€€€€½¹ÑÉ½°½¹ÑÉ½°°(€€€€€€€A…‘‘¥¹œµ…É¥¸¤(€€€ì(€€€€€€€Ù…ÈÉ½Ü€ôÑ…‰±”¹I½Ý½Õ¹Ðì(€€€€€€€Ñ…‰±”¹I½Ý½Õ¹Ð¬¬ì(€€€€€€€Ñ…‰±”¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹ÕÑ½M¥é”¤¤ì(€€€€€€€½¹ÑÉ½°¹½¬€ô½­MÑå±”¹¥±°ì(€€€€€€€½¹ÑÉ½°¹5…É¥¸€ôµ…É¥¸ì(€€€€€€€Ñ…‰±”¹½¹ÑÉ½±Ì¹‘¡½¹ÑÉ½°°€À°É½Ü¤ì(€€€€€€€Ñ…‰±”¹M•Ñ½±Õµ¹MÁ…¸¡½¹ÑÉ½°°Ñ…‰±”¹½±Õµ¹½Õ¹Ð¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥‘‘MÑ…­I½Ü (€€€€€€€Q…‰±•1…å½ÕÑA…¹•°Ñ…‰±”°(€€€€€€€½¹ÑÉ½°½¹ÑÉ½°°(€€€€€€€A…‘‘¥¹œüµ…É¥¸€ô¹Õ±°¤(€€€ì(€€€€€€€Ù…ÈÉ½Ü€ôÑ…‰±”¹I½Ý½Õ¹Ðì(€€€€€€€Ñ…‰±”¹I½Ý½Õ¹Ð¬¬ì(€€€€€€€Ñ…‰±”¹I½ÝMÑå±•Ì¹‘¡¹•ÜI½ÝMÑå±”¡M¥é•QåÁ”¹ÕÑ½M¥é”¤¤ì(€€€€€€€½¹ÑÉ½°¹½¬€ô½­MÑå±”¹¥±°ì(€€€€€€€½¹ÑÉ½°¹5…É¥¸€ôµ…É¥¸€üü¹•ÜA…‘‘¥¹œ À°€À°€À°€ÄÐ¤ì(€€€€€€€Ñ…‰±”¹½¹ÑÉ½±Ì¹‘¡½¹ÑÉ½°°€À°É½Ü¤ì(€€€ô)ô()¥¹Ñ•É¹…°Í•…±•É•½ÉM•ÑÑ¥¹Í½ÉµY…±Õ•Ì (€€€ÍÑÉ¥¹œ¥Ñ…Ñ¥½¹AÉ½Ù¥‘•È°(€€€ÍÑÉ¥¹œ5¥É½Á¡½¹•%°(€€€ÍÑÉ¥¹œ5¥É½Á¡½¹•9…µ”°(€€€ÍÑÉ¥¹œ!½Ñ­•ä°(€€€¡É½µ•AÉ½™¥±•%¹™¼ü¡É½µ•AÉ½™¥±”¤ì()¥¹Ñ•É¹…°Í•…±•É•½ÉM•ÑÑ¥¹ÍÁÁ±åI•ÍÕ±Ð (€€€‰½½°=¬°(€€€ÍÑÉ¥¹œ5•ÍÍ…”°(€€€ÍÑÉ¥¹œ5¥É½Á¡½¹•9…µ”°(€€€ÍÑÉ¥¹œ!½Ñ­•ä¤)ì(€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒM•ÑÑ¥¹ÍÁÁ±åI•ÍÕ±ÐMÕ•ÍÌ¡ÍÑÉ¥¹œµ¥É½Á¡½¹•9…µ”°ÍÑÉ¥¹œ¡½Ñ­•ä¤€ôø(€€€€€€€¹•Ü¡ÑÉÕ”°ÍÑÉ¥¹œ¹µÁÑä°µ¥É½Á¡½¹•9…µ”°¡½Ñ­•ä¤ì((€€€ÁÕ‰±¥ŒÍÑ…Ñ¥ŒM•ÑÑ¥¹ÍÁÁ±åI•ÍÕ±Ð…¥°¡ÍÑÉ¥¹œµ•ÍÍ…”¤€ôø(€€€€€€€¹•Ü¡™…±Í”°µ•ÍÍ…”°ÍÑÉ¥¹œ¹µÁÑä°ÍÑÉ¥¹œ¹µÁÑä¤ì)ô(