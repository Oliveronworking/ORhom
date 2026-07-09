# OpenAI Flow Dictation

Windows-Tray-App, die F8 als globalen Toggle verwendet, ChatGPT im Browser per `Ctrl+Shift+D` diktieren laesst und den fertigen Text automatisch in das urspruengliche Textfeld einfuegt.

Die App braucht keinen OpenAI API-Key. Sie nutzt den bereits angemeldeten ChatGPT-Tab im fest konfigurierten Chrome-Profil und kann ihn beim Start automatisch vorbereiten.

## Start

Voraussetzung: .NET 8 SDK oder die gebaute Release-EXE.

```powershell
dotnet build -c Release
Start-Process "bin\Release\net8.0-windows\ChatGptDictationBridge.exe"
```

Es erscheint kein Hauptfenster; die App laeuft im Infobereich der Taskleiste.
Wenn ChatGPT noch nicht offen ist, wird es beim Start der Tray-App mit dem konfigurierten Chrome-Profil automatisch gestartet und danach wieder minimiert. F8 verwendet dieses vorbereitete Fenster. Wenn es nicht mehr vorhanden ist, startet die App ChatGPT erneut mit genau diesem Profil.

## Festes Chrome-Profil

OpenAIFlow verwendet standardmaessig dieses bestehende Chrome-Profil, weil dort ChatGPT angemeldet und die Diktierfunktion sichtbar ist:

```text
C:\Users\Admin\AppData\Local\Google\Chrome\User Data\Profile 3
```

Daraus ergeben sich diese Einstellungen:

```text
chromeUserDataDir: C:\Users\Admin\AppData\Local\Google\Chrome\User Data
chromeProfileDirectory: Profile 3
```

Beim Start wird geprueft, ob die Chrome-EXE, `chromeUserDataDir`, der Ordner `Profile 3` und dessen Datei `Preferences` vorhanden sind. Fehlt etwas, zeigt die App `Konfiguriertes Chrome-Profil nicht gefunden. Bitte settings.json prüfen.`, startet keine Diktierung und wechselt nicht still auf ein anderes, ein Gast-, Inkognito- oder temporaeres Profil.

ChatGPT wird sichtbar so gestartet:

```text
"C:\Program Files\Google\Chrome\Application\chrome.exe" --profile-directory="Profile 3" --no-first-run --no-default-browser-check https://chatgpt.com
```

Dabei wird absichtlich kein temporaeres `--user-data-dir` gesetzt. Ist Chrome mit `Profile 3` bereits offen, oeffnet Chrome den ChatGPT-Tab im vorhandenen Profil. Die App verwendet sichtbare UI-Automation; sie benoetigt kein Remote-Debugging/CDP.

Um ein anderes bestehendes Profil einzurichten, im gewuenschten Chrome-Profil `chrome://version` oeffnen und den Wert bei **Profilpfad** kopieren. Der letzte Ordner ist `chromeProfileDirectory`; alles davor bis einschliesslich `User Data` ist `chromeUserDataDir`.

## Nutzung

1. In dein Ziel-Textfeld klicken, zum Beispiel Codex, VS Code, Browser, Word oder Discord.
2. F8 druecken.
3. Die App fokussiert kurz das ChatGPT-Eingabefeld, sendet dort `Ctrl+Shift+D` und springt zurueck zum Ziel.
4. Sprechen.
5. Wieder F8 druecken.
6. Die App fokussiert wieder ChatGPT, sendet dort `Ctrl+Shift+D`, liest den diktierten Text aus dem ChatGPT-Eingabefeld und fuegt ihn bei deinem urspruenglichen Cursor ein.

Wichtig: `Ctrl+Shift+D` wird nur gesendet, wenn vorher ein sicheres ChatGPT-Eingabefeld fokussiert wurde. Wenn die App nur die Chrome-Adressleiste oder kein passendes Feld findet, sendet sie keinen Shortcut.

Wenn im Profil eine Anmeldeseite erkannt wird (zum Beispiel `Anmelden`, `Log in`, `Sign up` oder `Thanks for trying ChatGPT`), startet die App keine Diktierung. Dann im Tray-Menue **ChatGPT Profil öffnen** waehlen, die Anmeldung sowie die Mikrofonfreigabe pruefen und danach erneut F8 druecken.

Bugfix: Nach dem zweiten F8 wird nicht mehr blind das beim Start gefundene ChatGPT-Element wiederverwendet. Die App wartet jetzt laenger auf das fertige Diktat, sucht das ChatGPT-Eingabefeld wiederholt frisch, prueft jeden Kandidaten gegen Browser-Chrome/Passwortfeld-Regeln und liest den Text ueber ValuePattern, TextPattern und einen bewachten Clipboard-Fallback. Die Fehlermeldung `Kein sicherer diktierter Text...` erscheint erst nach Ablauf des Ergebnis-Timeouts.

Im Tray-Menue kann `ChatGPT UI Diagnose speichern` ausgefuehrt werden. Der Punkt schreibt technische UIAutomation-Diagnose in die Logdatei, unter anderem ob ein ChatGPT-Fenster gefunden wurde und welche Input-Kandidaten als sicher oder abgelehnt bewertet wurden. Inhalte aus Textfeldern werden dabei nicht gelesen oder geloggt.

## Settings

`settings.json` liegt neben der EXE:

```json
{
  "toggleHotkey": "F8",
  "chatGptDictationHotkey": "Ctrl+Shift+D",
  "chatGptUrl": "https://chatgpt.com",
  "chatGptWindowTitleContains": [ "ChatGPT", "chatgpt.com" ],
  "browserProfileMode": "ExistingChromeProfile",
  "chromeExecutablePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  "chromeUserDataDir": "C:\\Users\\Admin\\AppData\\Local\\Google\\Chrome\\User Data",
  "chromeProfileDirectory": "Profile 3",
  "requireConfiguredChromeProfile": true,
  "allowGuestProfile": false,
  "allowIncognitoProfile": false,
  "allowTemporaryProfile": false,
  "openChatGptProfileVisibleForSetup": true,
  "warnIfConfiguredChromeProfileUnavailable": true,
  "launchChatGptIfMissing": true,
  "launchChatGptOnHotkey": false,
  "prepareChatGptOnStartup": true,
  "minimizeChatGptAfterStartup": true,
  "restoreTargetAfterStart": true,
  "restoreClipboard": true,
  "browserChromeExclusionTopPx": 120,
  "maxChatGptInputHeightPx": 260,
  "maxChatGptInputWindowWidthRatio": 0.85,
  "settleDelayMs": 1000,
  "readTextTimeoutMs": 20000,
  "dictationResultTimeoutMs": 20000,
  "dictationResultPollIntervalMs": 250,
  "enableChatGptInputDiagnostics": true,
  "pasteDelayMs": 100,
  "restoreClipboardDelayMs": 300,
  "blockPasswordFields": true
}
```

Das Tray-Menue enthaelt zusaetzlich:

- **ChatGPT Profil öffnen**: startet ChatGPT sichtbar im konfigurierten Chrome-Profil, damit Anmeldung und Mikrofonberechtigung geprueft werden koennen.
- **Chrome-Profil prüfen**: prueft die konfigurierten Pfade und schreibt das Ergebnis in das Log.
- **Chrome-Profilordner öffnen**: oeffnet direkt den Ordner `Profile 3`.

Wenn ChatGPT zwar geoeffnet ist, aber das Eingabefeld nicht gefunden wird, pruefe zuerst:

- Ist der ChatGPT-Tab sichtbar und angemeldet?
- Ist der Fenstertitel in `chatGptWindowTitleContains` enthalten?
- Ist `browserChromeExclusionTopPx` gross genug, damit Adressleiste und Tabs nie als Eingabefeld gelten?
- `settleDelayMs`: kurze Wartezeit nach dem Stop-Shortcut, bevor der fertige Prompt gelesen wird.
- `readTextTimeoutMs`: allgemeiner Timeout fuer Textsuche.
- `dictationResultTimeoutMs`: Timeout fuer die robuste UIAutomation-Ergebnislesung nach dem zweiten F8.
- `dictationResultPollIntervalMs`: Polling-Intervall fuer frisches Suchen und Lesen des ChatGPT-Eingabefelds.
- `enableChatGptInputDiagnostics`: zeigt den Tray-Menuepunkt fuer technische ChatGPT-UI-Diagnose.

## Logging

Logs liegen unter:

```text
bin\Release\net8.0-windows\logs\app.log
```

Es werden keine diktierten Inhalte geloggt, nur technische Informationen wie Statuswechsel, Fensterhandle, sicher fokussiertes ChatGPT-Feld, Diktat-Start/Stop, Textlaenge und Paste-Erfolg.

Bei Problemen mit der Foreground/UIAutomation-Route ist `ChatGPT UI Diagnose speichern` der schnellste naechste Schritt. Danach die aktuelle Logdatei unter `bin\Release\net8.0-windows\logs\app.log` pruefen.
