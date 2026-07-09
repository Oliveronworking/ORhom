# OpenAI Flow Dictation

Windows-Tray-App, die F8 als globalen Toggle verwendet, ChatGPT im Browser per `Ctrl+Shift+D` diktieren laesst und den fertigen Text automatisch in das urspruengliche Textfeld einfuegt.

Die App braucht keinen OpenAI API-Key. Sie nutzt deine bereits geoeffnete ChatGPT-Webseite.

## Start

Voraussetzung: .NET 8 SDK oder die gebaute Release-EXE.

```powershell
dotnet build -c Release
Start-Process "bin\Release\net8.0-windows\ChatGptDictationBridge.exe"
```

Es erscheint kein Hauptfenster; die App laeuft im Infobereich der Taskleiste.

## Nutzung

1. ChatGPT im Browser oeffnen und angemeldet lassen.
2. In dein Ziel-Textfeld klicken, zum Beispiel Codex, VS Code, Browser, Word oder Discord.
3. F8 druecken.
4. Die App fokussiert kurz das ChatGPT-Eingabefeld, sendet dort `Ctrl+Shift+D` und springt zurueck zum Ziel.
5. Sprechen.
6. Wieder F8 druecken.
7. Die App fokussiert wieder das ChatGPT-Eingabefeld, sendet dort `Ctrl+Shift+D`, liest den diktierten Text aus dem ChatGPT-Eingabefeld und fuegt ihn bei deinem urspruenglichen Cursor ein.

Wichtig: `Ctrl+Shift+D` wird nur gesendet, wenn vorher ein sicheres ChatGPT-Eingabefeld fokussiert wurde. Wenn die App nur die Chrome-Adressleiste oder kein passendes Feld findet, sendet sie keinen Shortcut.

## Settings

`settings.json` liegt neben der EXE:

```json
{
  "toggleHotkey": "F8",
  "chatGptDictationHotkey": "Ctrl+Shift+D",
  "chatGptUrl": "https://chatgpt.com",
  "chatGptWindowTitleContains": [ "ChatGPT", "chatgpt.com" ],
  "launchChatGptIfMissing": true,
  "restoreTargetAfterStart": true,
  "restoreClipboard": true,
  "browserChromeExclusionTopPx": 120,
  "settleDelayMs": 500,
  "readTextTimeoutMs": 12000,
  "pasteDelayMs": 100,
  "restoreClipboardDelayMs": 300,
  "blockPasswordFields": true
}
```

Wenn ChatGPT zwar geoeffnet ist, aber das Eingabefeld nicht gefunden wird, pruefe zuerst:

- Ist der ChatGPT-Tab sichtbar und angemeldet?
- Ist der Fenstertitel in `chatGptWindowTitleContains` enthalten?
- Ist `browserChromeExclusionTopPx` gross genug, damit Adressleiste und Tabs nie als Eingabefeld gelten?

## Logging

Logs liegen unter:

```text
bin\Release\net8.0-windows\logs\app.log
```

Es werden keine diktierten Inhalte geloggt, nur technische Informationen wie Statuswechsel, Fensterhandle, sicher fokussiertes ChatGPT-Feld, Textlaenge und Paste-Erfolg.
