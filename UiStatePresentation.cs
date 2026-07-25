namespace ORhom;

/// <summary>
/// User-facing German copy and interaction capabilities for an application state.
/// </summary>
internal sealed record UiStatePresentation(
    string VisibleStatus,
    string TrayTooltip,
    string PrimaryAction,
    string Hint,
    bool CanToggle,
    bool CanAbort,
    bool IsBusy)
{
    public static UiStatePresentation For(AppStatus status, string toggleHotkey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toggleHotkey);
        var hotkey = toggleHotkey.Trim();

        return status switch
        {
            AppStatus.Idle => new(
                VisibleStatus: "Bereit",
                TrayTooltip: "ORhom – Bereit",
                PrimaryAction: "Diktieren",
                Hint: $"{hotkey} drücken, um zu diktieren",
                CanToggle: true,
                CanAbort: false,
                IsBusy: false),
            AppStatus.Starting => new(
                VisibleStatus: "Wird gestartet",
                TrayTooltip: "ORhom – Wird gestartet",
                PrimaryAction: "Stoppen",
                Hint: $"{hotkey} drücken, um direkt wieder zu stoppen",
                CanToggle: true,
                CanAbort: false,
                IsBusy: true),
            AppStatus.Recording => new(
                VisibleStatus: "Hört zu",
                TrayTooltip: "ORhom – Hört zu",
                PrimaryAction: "Aufnahme stoppen",
                Hint: $"{hotkey} zum Stoppen · Esc zum Abbrechen",
                CanToggle: true,
                CanAbort: true,
                IsBusy: false),
            AppStatus.Stopping => new(
                VisibleStatus: "Aufnahme wird beendet",
                TrayTooltip: "ORhom – Aufnahme wird beendet",
                PrimaryAction: "Verarbeitung abbrechen",
                Hint: $"{hotkey} oder Esc zum Abbrechen",
                CanToggle: true,
                CanAbort: true,
                IsBusy: true),
            AppStatus.ReadingText => new(
                VisibleStatus: "Wird transkribiert",
                TrayTooltip: "ORhom – Wird transkribiert",
                PrimaryAction: "Verarbeitung abbrechen",
                Hint: $"{hotkey} oder Esc zum Abbrechen",
                CanToggle: true,
                CanAbort: true,
                IsBusy: true),
            AppStatus.Pasting => new(
                VisibleStatus: "Wird eingefügt",
                TrayTooltip: "ORhom – Wird eingefügt",
                PrimaryAction: "Bitte warten",
                Hint: "Der Text erscheint gleich im zuletzt aktiven Feld",
                CanToggle: false,
                CanAbort: false,
                IsBusy: true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Für diesen App-Status ist keine UI-Darstellung definiert.")
        };
    }
}
