namespace ChatGptDictationBridge;

internal sealed class DictationHistoryForm : Form
{
    private readonly DictationHistoryStore _history;
    private readonly ListBox _entryList;
    private readonly TextBox _textPreview;
    private readonly Label _detailLabel;
    private readonly Button _copyButton;
    private readonly Button _clearButton;
    private bool _allowClose;

    public DictationHistoryForm(DictationHistoryStore history)
    {
        _history = history;

        Text = "Diktierverlauf · OpenAI Flow";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(17, 18, 22);
        ClientSize = new Size(760, 540);
        Font = new Font("Segoe UI", 9.5f);
        ForeColor = Color.White;
        MinimumSize = new Size(680, 480);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        Controls.Add(CreateLabel(
            "Diktierverlauf",
            new Font("Segoe UI", 20, FontStyle.Bold),
            new Point(28, 20),
            new Size(450, 40)));
        Controls.Add(CreateLabel(
            "Die letzten 10 Diktierungen bleiben lokal gespeichert.",
            new Font("Segoe UI", 9.5f),
            new Point(31, 61),
            new Size(570, 25),
            Color.FromArgb(166, 170, 180)));

        _entryList = new ListBox
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
            BackColor = Color.FromArgb(27, 29, 35),
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = Color.White,
            HorizontalScrollbar = true,
            IntegralHeight = false,
            ItemHeight = 24,
            Location = new Point(28, 98),
            Size = new Size(286, 374)
        };
        _entryList.SelectedIndexChanged += (_, _) => ShowSelectedEntry();
        _entryList.DoubleClick += (_, _) => CopySelectedText();
        Controls.Add(_entryList);

        _detailLabel = CreateLabel(
            "Eintrag auswählen",
            new Font("Segoe UI", 9.5f, FontStyle.Bold),
            new Point(336, 99),
            new Size(394, 26),
            Color.FromArgb(190, 193, 201));
        _detailLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_detailLabel);

        _textPreview = new TextBox
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(27, 29, 35),
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = Color.White,
            Location = new Point(336, 130),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Size = new Size(396, 342)
        };
        Controls.Add(_textPreview);

        _clearButton = CreateButton("Verlauf löschen", new Point(28, 488), new Size(148, 36), secondary: true);
        _clearButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _clearButton.Click += (_, _) => ClearHistory();
        Controls.Add(_clearButton);

        _copyButton = CreateButton("Text kopieren", new Point(584, 488), new Size(148, 36), secondary: false);
        _copyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _copyButton.Enabled = false;
        _copyButton.Click += (_, _) => CopySelectedText();
        Controls.Add(_copyButton);

        _history.Changed += HistoryChanged;
        FormClosing += OnFormClosing;
        RefreshEntries();
    }

    public void ShowAndActivate()
    {
        RefreshEntries();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _history.Changed -= HistoryChanged;
        }

        base.Dispose(disposing);
    }

    private void HistoryChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(RefreshEntries);
            return;
        }

        RefreshEntries();
    }

    private void RefreshEntries()
    {
        var selectedId = (_entryList.SelectedItem as HistoryListItem)?.Entry.Id;
        var entries = _history.GetEntries();
        _entryList.BeginUpdate();
        _entryList.Items.Clear();
        foreach (var entry in entries)
        {
            _entryList.Items.Add(new HistoryListItem(entry));
        }

        if (_entryList.Items.Count > 0)
        {
            var selectedIndex = selectedId is null
                ? 0
                : Enumerable.Range(0, _entryList.Items.Count)
                    .FirstOrDefault(index => (_entryList.Items[index] as HistoryListItem)?.Entry.Id == selectedId);
            _entryList.SelectedIndex = selectedIndex;
        }
        else
        {
            ShowSelectedEntry();
        }

        _entryList.EndUpdate();
        _clearButton.Enabled = entries.Count > 0;
    }

    private void ShowSelectedEntry()
    {
        if (_entryList.SelectedItem is not HistoryListItem item)
        {
            _detailLabel.Text = "Noch keine Diktierungen gespeichert";
            _textPreview.Text = string.Empty;
            _copyButton.Enabled = false;
            return;
        }

        var entry = item.Entry;
        var status = DictationHistoryOutcomes.GetDisplayText(entry.Outcome);
        _detailLabel.Text = $"{entry.CreatedAt:dd.MM.yyyy · HH:mm}  ·  {status}";
        _textPreview.Text = entry.Text.Length > 0
            ? entry.Text
            : "Für diese Diktierung konnte kein Text gesichert werden.";
        _copyButton.Enabled = entry.Text.Length > 0;
    }

    private void CopySelectedText()
    {
        if (_entryList.SelectedItem is not HistoryListItem item || item.Entry.Text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.Entry.Text);
            _copyButton.Text = "Kopiert ✓";
            var timer = new System.Windows.Forms.Timer { Interval = 1200 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!IsDisposed)
                {
                    _copyButton.Text = "Text kopieren";
                }
            };
            timer.Start();
        }
        catch
        {
            _copyButton.Text = "Kopieren fehlgeschlagen";
        }
    }

    private void ClearHistory()
    {
        var result = MessageBox.Show(
            this,
            "Möchtest du alle gespeicherten Diktierungen löschen?",
            "Diktierverlauf löschen",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result == DialogResult.Yes)
        {
            _history.Clear();
        }
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

    private sealed class HistoryListItem(DictationHistoryEntry entry)
    {
        public DictationHistoryEntry Entry { get; } = entry;

        public override string ToString()
        {
            var status = DictationHistoryOutcomes.GetDisplayText(Entry.Outcome);
            var preview = Entry.Text.ReplaceLineEndings(" ").Trim();
            if (preview.Length > 38)
            {
                preview = preview[..38] + "…";
            }

            return preview.Length == 0
                ? $"{Entry.CreatedAt:dd.MM. HH:mm}  {status}"
                : $"{Entry.CreatedAt:dd.MM. HH:mm}  {preview}";
        }
    }
}
