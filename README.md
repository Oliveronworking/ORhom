# OpenAI Flow Dictation

Windows-Tray-App, die F8 als globalen Toggle fuer lokale Mikrofonaufnahme nutzt und den transkribierten Text automatisch in das zuletzt fokussierte Textfeld einfuegt.

Der Hauptworkflow verwendet keine ChatGPT-Weboberflaeche, keine ChatGPT-Windows-App und keine Browser-Automation mehr.

## Installation

Voraussetzung: .NET 8 SDK oder neuer auf Windows.

```powershell
dotnet restore
dotnet build -c Release
```

Die App liegt danach unter:

```text
bin\Release\net8.0-windows\ChatGptDictationBridge.exe
```

## Start

```powershell
dotnet run -c Release
```

Oder die erzeugte EXE direkt starten. Es erscheint kein Hauptfenster; die App laeuft im Infobereich der Taskleiste.

## Nutzung mit F8

1. In ein Ziel-Textfeld klicken, zum Beispiel Codex, VS Code, Browser, Word oder Discord.
2. F8 druecken: Die App merkt sich Ziel-Fenster, fokussiertes UIAutomation-Element und Clipboard und startet die lokale Mikrofonaufnahme.
3. Sprechen. Der Fokus bleibt im Ziel-Textfeld.
4. Wieder F8 druecken: Die App stoppt die Aufnahme, transkribiert die temporaere WAV-Datei und fuegt den Text automatisch per Clipboard und `Ctrl+V` ein.

Der vorherige Clipboard-Inhalt wird danach wiederhergestellt, sofern `restoreClipboard` in `settings.json` aktiv ist.

## Tray-Menue

- Status: `Idle`, `Recording`, `Transcribing`, `Pasting`, `Error`
- Aufnahme abbrechen
- Einstellungen oeffnen
- Logs oeffnen
- Beenden

## Settings

Die Datei `settings.json` wird neben der EXE verwendet. Wichtige Werte:

```json
{
  "toggleHotkey": "F8",
  "restoreClipboard": true,
  "audioTempFolder": "temp",
  "transcriptionProvider": "openai",
  "transcriptionModel": "gpt-4o-mini-transcribe",
  "language": "de",
  "openAIApiKey": null,
  "openAIApiBaseUrl": "https://api.openai.com",
  "logTranscribedText": false,
  "pasteDelayMs": 100,
  "restoreClipboardDelayMs": 300,
  "blockPasswordFields": true
}
```

Setze den API-Key bevorzugt als Umgebungsvariable:

```powershell
$env:OPENAI_API_KEY = "sk-..."
```

Alternativ kann `openAIApiKey` in `settings.json` gesetzt werden.

## Datenschutz und Logging

Die App loggt keine diktierten Inhalte. Standardmaessig werden nur technische Informationen geschrieben: Statuswechsel, gespeicherte Fensterhandles, Start/Stop der Aufnahme, Audiodauer, Transkriptionsergebnis als Textlaenge, Paste-Erfolg und Fehlerdetails.

Temporaere Audiodateien werden nach Transkription oder Abbruch geloescht.

## F8 testen

1. App starten.
2. In ein Textfeld klicken.
3. F8 druecken. Im Tray sollte `Recording` stehen; Chrome, Edge oder ChatGPT duerfen nicht aktiviert werden.
4. Einen kurzen Satz sprechen.
5. Wieder F8 druecken. Der Status wechselt ueber `Transcribing` und `Pasting` zurueck zu `Idle`.
6. Der Text sollte im urspruenglichen Textfeld erscheinen, ohne dass du selbst `Ctrl+V` drueckst.

Wenn nichts eingefuegt wird, zuerst `logs\app.log` pruefen. Hauefige Ursachen sind fehlender `OPENAI_API_KEY`, kein Mikrofonzugriff oder ein blockiertes Passwortfeld.
