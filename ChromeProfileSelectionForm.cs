namespace ChatGptDictationBridge;

internal sealed class ChromeProfileSelectionForm : Form
{
    private readonly AppSettings _settings;
    private readonly ChromeProfileDiscovery _discovery;
    private readonly ListBox _profilesList;
    private readonly Label _statusLabel;
    private readonly Button _confirmButton;

    public ChromeProfileSelectionForm(AppSettings settings, ChromeProfileDiscovery discovery)
    {
        _settings = settings;
        _discovery = discovery;

        Text = "Chrome-Profil auswählen";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(17, 18, 22);
        ClientSize = new Size(540, 430);
        Font = new Font("Segoe UI", 10f);
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        Controls.Add(new Label
        {
            Text = "Welches Chrome-Profil möchtest du verwenden?",
            Font = new Font("Segoe UI", 17f, FontStyle.Bold),
            Location = new Point(30, 25),
            Size = new Size(480, 40),
            ForeColor = Color.White
        });
        Controls.Add(new Label
        {
            Text = "OpenAI Flow verwendet dieses Profil für ChatGPT. Du kannst es bei jedem Start neu auswählen.",
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(33, 70),
            Size = new Size(465, 45),
            ForeColor = Color.FromArgb(180, 184, 194)
        });

        _profilesList = new ListBox
        {
            Location = new Point(32, 122),
            Size = new Size(476, 190),
            BackColor = Color.FromArgb(27, 29, 35),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 11f),
            IntegralHeight = false,
            ItemHeight = 34
        };
        _profilesList.SelectedIndexChanged += (_, _) => UpdateConfirmState();
        _profilesList.DoubleClick += (_, _) => ConfirmSelection();
        Controls.Add(_profilesList);

        _statusLabel = new Label
        {
            Location = new Point(33, 321),
            Size = new Size(350, 30),
            ForeColor = Color.FromArgb(148, 163, 184)
        };
        Controls.Add(_statusLabel);

        var refreshButton = new Button
        {
            Text = "Neu laden",
            Location = new Point(32, 365),
            Size = new Size(125, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(39, 41, 49),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        refreshButton.FlatAppearance.BorderColor = Color.FromArgb(61, 64, 75);
        refreshButton.Click += (_, _) => ReloadProfiles();
        Controls.Add(refreshButton);

        _confirmButton = new Button
        {
            Text = "Profil verwenden",
            Location = new Point(326, 365),
            Size = new Size(182, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(79, 70, 229),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _confirmButton.FlatAppearance.BorderSize = 0;
        _confirmButton.Click += (_, _) => ConfirmSelection();
        Controls.Add(_confirmButton);

        AcceptButton = _confirmButton;
        Shown += (_, _) => ReloadProfiles();
    }

    public ChromeProfileInfo? SelectedProfile { get; private set; }

    private void ReloadProfiles()
    {
        var selectedDirectory = (_profilesList.SelectedItem as ChromeProfileInfo)?.DirectoryName
            ?? _settings.ChromeProfileDirectory;
        var result = _discovery.Discover(_settings.ChromeExecutablePath, _settings.ChromeUserDataDir);
        _profilesList.BeginUpdate();
        _profilesList.Items.Clear();
        foreach (var profile in result.Profiles)
        {
            _profilesList.Items.Add(profile);
        }

        _profilesList.SelectedItem = result.Profiles.FirstOrDefault(profile =>
            profile.DirectoryName.Equals(selectedDirectory, StringComparison.OrdinalIgnoreCase));
        if (_profilesList.SelectedIndex < 0 && _profilesList.Items.Count == 1)
        {
            _profilesList.SelectedIndex = 0;
        }
        _profilesList.EndUpdate();
        _statusLabel.Text = result.Profiles.Count == 0
            ? "Keine Chrome-Profile gefunden. Bitte Chrome starten und erneut laden."
            : $"{result.Profiles.Count} Profil(e) gefunden";
        _statusLabel.ForeColor = result.Profiles.Count == 0
            ? Color.FromArgb(248, 113, 113)
            : Color.FromArgb(148, 163, 184);
        UpdateConfirmState();
    }

    private void UpdateConfirmState() =>
        _confirmButton.Enabled = _profilesList.SelectedItem is ChromeProfileInfo;

    private void ConfirmSelection()
    {
        if (_profilesList.SelectedItem is not ChromeProfileInfo profile)
        {
            return;
        }

        SelectedProfile = profile;
        DialogResult = DialogResult.OK;
        Close();
    }
}
