# OpenAI Flow Dictation

OpenAIFlow ist eine Windows-Tray-App für browserbasierte ChatGPT-Diktierung. F8 startet die Aufnahme im kleinen Diktier-/Mikrofonbutton des ChatGPT-Composers; ein zweites F8 stoppt die Aufnahme, wartet auf die Transkription und fügt den Text am ursprünglichen Cursor ein. Escape bricht eine laufende Aufnahme ab.

Die App benötigt keinen OpenAI-API-Key. Sie verwendet ausschließlich das bereits vorhandene und bei ChatGPT angemeldete Chrome-Profil `Profile 3`.

## Voraussetzungen und Build

- Windows mit .NET 8 SDK
- Google Chrome unter `C:\Program Files\Google\Chrome\Application\chrome.exe`
- das Chrome-Profil `C:\Users\Admin\AppData\Local\Google\Chrome\User Data\Profile 3`

```powershell
dotnet build -c Release
Start-Process "bin\Release\net8.0-windows\ChatGptDictationBridge.exe"
```

Es gibt kein Hauptfenster. Status, Diagnose und Beenden befinden sich im Infobereich der Taskleiste.

OpenAIFlow hält genau ein eigenes ChatGPT-Fenster dauerhaft im Hintergrund. Es wird minimiert gestartet, über App-Neustarts hinweg wiedererkannt und nur nahezu transparent für die kurzen UI-Automationsschritte aktiviert. Beim normalen F8-Ablauf erscheint deshalb kein Chrome-Fenster auf dem Desktop. Sichtbar geöffnet wird es ausschließlich über den bewusst gewählten Tray-Menüpunkt **ChatGPT Profil öffnen**.

## Chrome Profile 3 einrichten

OpenAIFlow startet Chrome immer mit diesen beiden vorhandenen Profilparametern:

```text
--user-data-dir=C:\Users\Admin\AppData\Local\Google\Chrome\User Data
--profile-directory=Profile 3
```

Gast-, Inkognito- und temporäre Profile sind deaktiviert. Die App weicht nicht still auf ein anderes Profil aus. Vor jeder Aufnahme prüft sie:

- `chrome.exe` ist vorhanden,
- der konfigurierte User-Data-Ordner ist vorhanden,
- der Ordner `Profile 3` ist vorhanden,
- `Profile 3\Preferences` ist vorhanden.

Zur Ersteinrichtung:

1. Im Tray-Menü **ChatGPT Profil öffnen** wählen.
2. In dem geöffneten Profile-3-Fenster einmal bei ChatGPT anmelden.
3. Im Tray-Menü **Chrome-Mikrofon auswählen** öffnen und den gewünschten Eingang einstellen. Auf diesem Rechner ist das **Mikrofon (Logi C525 HD WebCam)**.
4. Den Mikrofonzugriff für `https://chatgpt.com` erlauben.
5. In ein beliebiges Zieltextfeld klicken und F8 testen.

Fehlt das Profil, startet die Diktierung nicht und die Tray-Meldung verweist auf `settings.json`.

## Nutzung

1. Den Cursor in das gewünschte Zieltextfeld setzen.
2. F8 drücken. OpenAIFlow merkt sich Fenster, Eingabefeld und Zwischenablage.
3. Die App verwendet ihr einziges unsichtbares ChatGPT-Hintergrundfenster in Profile 3, prüft Login und Composer, klickt bevorzugt den kleinen Diktierbutton und bestätigt den Aufnahmezustand.
4. Sprechen. Unten mittig zeigt eine fokusfreie Desktop-Pille **Hört zu …** sowie **F8 zum Stoppen · Esc Abbruch**. Die Anzeige ist klickdurchlässig und verändert weder Fokus noch Cursor.
5. F8 erneut drücken. Die App lässt dem letzten gesprochenen Wort noch einen kurzen Audiopuffer, bestätigt den Stop-Zustand und liest den Composer wiederholt über mehrere UI-Automation-Verfahren oder einen abgesicherten Zwischenablage-Fallback. Übernommen wird der Text erst, wenn er sich für die konfigurierte Stabilitätszeit nicht mehr verändert hat.
6. Der Text wird am ursprünglichen Cursor eingefügt und die vorherige Zwischenablage wiederhergestellt.

Der große Audio-/Sprachmodus-Button für Voice Conversations wird nicht als Diktierbutton akzeptiert. `Ctrl+Shift+D` wird nur als Fallback verwendet, wenn kein kleiner Diktier-/Mikrofonbutton gefunden wurde; auch danach muss die Oberfläche den Aufnahme- beziehungsweise Stop-Zustand bestätigen.

Passwortfelder werden blockiert. Browser-Adressleiste, Lesezeichendialoge und URLs werden nicht als Diktat übernommen. Diktierte Inhalte werden nie geloggt.

## Status und Fehler

Die State-Machine lautet:

```text
Idle -> Starting -> Recording -> Stopping -> ReadingText -> Pasting -> Idle
```

`Recording` wird erst gesetzt, wenn ChatGPT den Aufnahmezustand sichtbar bestätigt. Nach einem Fehler werden Fokus, Zwischenablage und Status bereinigt. Die Tray-Meldungen unterscheiden Profil-, Login-, Composer-, Start-, Stop-, Transkriptions- und Einfügefehler.

Die Desktop-Anzeige spiegelt diese Zustände als `Diktierung startet`, `Hört zu`, `Aufnahme wird beendet`, `Text wird transkribiert` und `Text wird eingefügt`. Im Zustand `Idle` ist sie vollständig ausgeblendet.

Beim Einfügen wird das ursprüngliche Zielfenster verifiziert aktiviert. In VS Code/Codex und anderen Chromium-/Electron-WebViews wird der interne `RootWebArea`-/`ProseMirror`-Fokus bewusst nicht überschrieben, damit Cursor und `activeElement` erhalten bleiben. `Ctrl+V` wird über Win32 `SendInput` versendet; nur ein bestätigter Dispatch wird als Erfolg protokolliert.

## Diagnose im Tray-Menü

- **ChatGPT Profil öffnen** öffnet ChatGPT sichtbar mit Profile 3 für Anmeldung und Mikrofonfreigabe.
- **Chrome-Mikrofon auswählen** öffnet direkt die Mikrofon-Auswahl von Profile 3. Dadurch kann Chrome denselben Eingang wie eine andere Diktier-App verwenden, statt unbemerkt dem Windows-Standardgerät zu folgen.
- **Chrome-Profil prüfen** validiert `chrome.exe`, User-Data-Ordner, `Profile 3` und dessen `Preferences`-Datei.
- **ChatGPT Diagnose speichern** protokolliert Profilstatus, Fenster, Login-Eindruck, sicher redigierte Composer-Kandidaten, Diktierbutton-Kandidaten und erkannten Aufnahmezustand.
- **Chrome-Profilordner öffnen** öffnet den validierten Ordner `Profile 3`.

Die Diagnose protokolliert keine ChatGPT-Inhalte, Cookies, Tokens oder diktierten Texte. Namen möglicher Texteingaben werden redigiert; lediglich technische Metadaten und Textlängen werden gespeichert.

## Relevante Einstellungen

Die mitgelieferte `settings.json` enthält insbesondere:

```json
{
  "browserProfileMode": "ExistingChromeProfile",
  "chromeExecutablePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  "chromeUserDataDir": "C:\\Users\\Admin\\AppData\\Local\\Google\\Chrome\\User Data",
  "chromeProfileDirectory": "Profile 3",
  "requireConfiguredChromeProfile": true,
  "allowGuestProfile": false,
  "allowIncognitoProfile": false,
  "allowTemporaryProfile": false,
  "keepChatGptWindowHidden": true,
  "showRecordingOverlay": true,
  "recordingOverlayBottomOffsetPx": 72,
  "recordingStateTimeoutMs": 5000,
  "dictationResultTimeoutMs": 30000,
  "dictationResultPollIntervalMs": 100,
  "dictationSettleDelayMs": 0,
  "dictationTextStableMs": 1100,
  "dictationStopGracePeriodMs": 250,
  "pasteDelayMs": 25,
  "restoreClipboardDelayMs": 180
}
```

`dictationResultTimeoutMs` gilt für das wiederholte frische Suchen und Lesen des ChatGPT-Composers. Ein einzelnes leeres Ergebnis beendet die Suche nicht. `dictationTextStableMs` verhindert, dass ein frühes Teiltranskript eingefügt wird; die Wartezeit beginnt bei jeder Textänderung neu. `dictationStopGracePeriodMs` schützt das letzte gesprochene Wort vor einem zu harten Aufnahmeende. Der alte feste Settle-Delay bleibt deaktiviert, damit ein fertiges Ergebnis ohne unnötige Mehrsekundenpause übernommen wird.

## Logs

Die Release-Logs liegen hier:

```text
bin\Release\net8.0-windows\logs\app.log
```

Bei Problemen zuerst **Chrome-Profil prüfen** und danach **ChatGPT Diagnose speichern** ausführen. Im Log stehen nur technische Zustände wie Profilvalidierung, Kandidatentypen, Aufnahmeerkennung, Textlänge und Einfügeerfolg.
