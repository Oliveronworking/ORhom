# OpenAI Flow Dictation

Windows-Tray-App, die F8 als globalen Toggle verwendet, ChatGPT im Browser per `Ctrl+Shift+D` diktieren laesst und den fertigen Text automatisch in das urspruengliche Textfeld einfuegt.

Die App braucht keinen OpenAI API-Key. Sie nutzt ChatGPT im Browser und bereitet standardmaessig ein separates Chrome- oder Edge-Profil im Hintergrund vor. Beim Druecken von F8 soll kein ChatGPT-Fenster sichtbar in den Vordergrund springen.

## Start

Voraussetzung: .NET 8 SDK oder die gebaute Release-EXE.

```powershell
dotnet build -c Release
Start-Process "bin\Release\net8.0-windows\ChatGptDictationBridge.exe"
```

Es erscheint kein Hauptfenster; die App laeuft im Infobereich der Taskleiste.
Wenn ChatGPT noch nicht offen ist, wird beim Start der Tray-App ein separater Hintergrundbrowser mit eigenem Profil gestartet und danach minimiert gehalten. F8 startet keinen neuen ChatGPT-Tab, sondern nutzt den vorbereiteten ChatGPT-Tab ueber CDP/Playwright.

Beim ersten Start kann einmal Login und Mikrofonfreigabe im separaten Profil noetig sein. Wenn der Hintergrundmodus das ChatGPT-Eingabefeld nicht findet oder die Diktierfunktion nicht starten kann, zeigt die App eine Tray-Meldung. Sie holt ChatGPT standardmaessig nicht ungefragt nach vorne.

Fuer die einmalige Einrichtung gibt es im Tray-Menue den Punkt `ChatGPT-Profil einrichten`. Das oeffnet das separate Profil bewusst sichtbar, damit Login und Mikrofonfreigabe erledigt werden koennen.
Wenn ChatGPT beim ersten F8 noch nicht bereit ist, oeffnet die App dieses Setup-Fenster automatisch. Dabei werden ChatGPT und, wenn der Browser es erlaubt, die Mikrofon-Einstellungsseite des separaten Profils geoeffnet. Nach einem erfolgreichen Diktat-Start wird das Fenster wieder minimiert.

## Nutzung

1. In dein Ziel-Textfeld klicken, zum Beispiel Codex, VS Code, Browser, Word oder Discord.
2. F8 druecken.
3. Die App sendet `Ctrl+Shift+D` per CDP an den ChatGPT-Tab im Hintergrund. Dein Ziel-Textfeld bleibt das aktive Fenster.
4. Sprechen.
5. Wieder F8 druecken.
6. Die App stoppt das Diktat im Hintergrund, liest nur die Textlaenge ins Log und fuegt den Text bei deinem urspruenglichen Cursor ein.

Wichtig: Beim Start wird `Ctrl+Shift+D` nur gesendet, wenn vorher ein ChatGPT-Eingabefeld im Hintergrund-Tab gefunden wurde. Beim Stop wird der Shortcut direkt an die ChatGPT-Seite gesendet und danach auf den fertigen Prompt-Text gewartet, weil ChatGPT waehrend laufender Diktat-Aufnahme das Eingabefeld kurz umbauen kann.

Wenn `useBackgroundChatGptBrowser=false` gesetzt ist, nutzt die App wieder die alte UIAutomation-Logik. Wenn `allowForegroundFallback=true` gesetzt ist, darf diese alte Logik auch als Fallback genutzt werden; standardmaessig ist das deaktiviert, damit ChatGPT bei F8 nicht sichtbar aufpoppt.

## Settings

`settings.json` liegt neben der EXE:

```json
{
  "toggleHotkey": "F8",
  "chatGptDictationHotkey": "Ctrl+Shift+D",
  "chatGptUrl": "https://chatgpt.com",
  "chatGptWindowTitleContains": [ "ChatGPT", "chatgpt.com" ],
  "launchChatGptIfMissing": true,
  "launchChatGptOnHotkey": false,
  "prepareChatGptOnStartup": true,
  "minimizeChatGptAfterStartup": true,
  "useBackgroundChatGptBrowser": true,
  "backgroundBrowserExecutablePath": "",
  "backgroundBrowserUserDataDir": "%LOCALAPPDATA%\\OpenAIFlow\\ChatGptProfile",
  "backgroundBrowserDebugPort": 9227,
  "allowForegroundFallback": false,
  "keepBackgroundBrowserMinimized": true,
  "openSetupOnBackgroundFailure": true,
  "openMicrophoneSettingsOnSetup": true,
  "minimizeBackgroundBrowserAfterSuccessfulStart": true,
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

Neue Hintergrund-Settings:

- `useBackgroundChatGptBrowser`: aktiviert den CDP/Playwright-Hintergrundmodus.
- `backgroundBrowserExecutablePath`: optionaler Pfad zu `chrome.exe` oder `msedge.exe`; leer bedeutet automatische Suche.
- `backgroundBrowserUserDataDir`: dauerhaftes Profil fuer Login und Mikrofonfreigabe.
- `backgroundBrowserDebugPort`: lokaler CDP-Port des Hintergrundbrowsers.
- `allowForegroundFallback`: erlaubt die alte sichtbare UIAutomation nur, wenn bewusst auf `true` gesetzt.
- `keepBackgroundBrowserMinimized`: startet und haelt den separaten Browser minimiert.
- `openSetupOnBackgroundFailure`: oeffnet bei fehlendem ChatGPT-Eingabefeld automatisch das sichtbare Setup-Fenster.
- `openMicrophoneSettingsOnSetup`: oeffnet beim Setup zusaetzlich die Browser-Mikrofonseite des separaten Profils.
- `minimizeBackgroundBrowserAfterSuccessfulStart`: minimiert das Setup-Fenster wieder, sobald Diktat erfolgreich gestartet wurde.

Wenn ChatGPT im Hintergrund nicht vorbereitet werden kann oder das Eingabefeld nicht gefunden wird, pruefe zuerst:

- Ist das Hintergrundprofil unter `backgroundBrowserUserDataDir` bei ChatGPT angemeldet?
- Wurde Mikrofonzugriff fuer `https://chatgpt.com` erlaubt?
- Ist der `backgroundBrowserDebugPort` frei?
- Ist Chrome oder Edge installiert oder in `backgroundBrowserExecutablePath` eingetragen?

## Logging

Logs liegen unter:

```text
bin\Release\net8.0-windows\logs\app.log
```

Es werden keine diktierten Inhalte geloggt, nur technische Informationen wie Statuswechsel, Hintergrundbrowser-Start, CDP-Verbindung, gefundener ChatGPT-Tab, Diktat-Start/Stop, Textlaenge und Paste-Erfolg.
