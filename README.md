# OpenAI Flow Dictation

OpenAIFlow ist eine Windows-Tray-App für browserbasierte ChatGPT-Diktierung. F8 startet die Aufnahme im kleinen Diktier-/Mikrofonbutton des ChatGPT-Composers; ein zweites F8 stoppt die Aufnahme, wartet auf die Transkription und fügt den Text am ursprünglichen Cursor ein. Alternativ funktioniert derselbe Hotkey als Push-to-talk: länger halten, sprechen und zum Stoppen loslassen. Ein kurzer Tastendruck behält unverändert den Toggle-Modus. Escape bricht eine laufende Aufnahme ab und versucht, bereits gesprochenen Text im Diktierverlauf zu retten.

Die App benötigt keinen OpenAI-API-Key. Sie verwendet ausschließlich ein vorhandenes, vom Benutzer ausgewähltes und bei ChatGPT angemeldetes Chrome-Profil.

## Voraussetzungen und Build

- Windows mit .NET 8 SDK
- Google Chrome (Standardinstallation für den aktuellen Windows-Benutzer oder systemweit)
- mindestens ein bereits angelegtes Chrome-Profil

```powershell
dotnet build .\OpenAIFlow.sln -c Release
Start-Process "bin\Release\net8.0-windows\ChatGptDictationBridge.exe"
```

Beim ersten Start öffnet sich die OpenAI-Flow-Einrichtung. Dort werden Mikrofon und globale Tastenkombination gewählt. Das X und **Im Hintergrund schließen** blenden nur die Oberfläche aus; Diktierung, Status, Diagnose und Beenden bleiben über den Infobereich der Taskleiste erreichbar. Ein Doppelklick auf das Tray-Symbol öffnet die Einstellungen wieder.

OpenAIFlow hält genau ein eigenes ChatGPT-Fenster dauerhaft im Hintergrund. Es wird minimiert gestartet, über App-Neustarts hinweg wiedererkannt und nur nahezu transparent für die kurzen UI-Automationsschritte aktiviert. Beim normalen F8-Ablauf erscheint deshalb kein Chrome-Fenster auf dem Desktop. Sichtbar geöffnet wird es ausschließlich über den bewusst gewählten Tray-Menüpunkt **ChatGPT Profil öffnen**.

## Chrome-Profil einrichten

OpenAIFlow erkennt die Standardpfade von Chrome und startet es mit den Parametern des ausgewählten Profils, zum Beispiel:

```text
--user-data-dir=C:\Users\<Benutzer>\AppData\Local\Google\Chrome\User Data
--profile-directory=Profile 3
```

Gast-, Inkognito- und temporäre Profile sind deaktiviert. Die App weicht nicht still auf ein anderes Profil aus. Vor jeder Aufnahme prüft sie:

- `chrome.exe` ist vorhanden,
- der konfigurierte User-Data-Ordner ist vorhanden,
- der ausgewählte Profilordner ist vorhanden,
- dessen `Preferences`-Datei ist vorhanden.

Zur Ersteinrichtung:

1. In der OpenAI-Flow-Einrichtung das gewünschte Chrome-Profil auswählen.
2. Das gewünschte Mikrofon auswählen.
3. In das Tastenkombinationsfeld klicken und zum Beispiel `F8` drücken.
4. **Speichern und im Hintergrund starten** wählen. OpenAI Flow setzt den Eingang im unsichtbaren Browser des gewählten Profils und bereitet die ChatGPT-Seite frisch vor.
5. Falls ChatGPT noch nicht angemeldet ist: Im Tray-Menü **ChatGPT Profil öffnen** wählen, anmelden und den Mikrofonzugriff für `https://chatgpt.com` erlauben.
6. In ein beliebiges Zieltextfeld klicken und die gewählte Tastenkombination testen.

Beim ersten Start erkennt OpenAI Flow die lokal vorhandenen Chrome-Profile automatisch. In **OpenAI Flow öffnen** kann das gewünschte Profil anhand seines Chrome-Namens und Profilordners ausgewählt werden. Die Auswahl wird zusammen mit den auf diesem PC erkannten Chrome-Pfaden gespeichert. Dadurch kann dieselbe Anwendung auf einem weiteren PC eingerichtet werden, ohne Benutzerpfade oder `Profile 3` von Hand in `settings.json` einzutragen.

Fehlt das ausgewählte Profil später, startet die Diktierung nicht und die Tray-Meldung fordert zur erneuten Auswahl in OpenAI Flow auf.

## Nutzung

1. Den Cursor in das gewünschte Zieltextfeld setzen.
2. F8 kurz drücken oder gedrückt halten. OpenAIFlow prüft zuerst, ob das konfigurierte Mikrofon noch aktiv ist, und merkt sich Fenster sowie Eingabefeld. Die Zwischenablage wird erst unmittelbar vor dem Einfügen gesichert, damit zwischenzeitliche Kopiervorgänge des Benutzers erhalten bleiben.
3. Die App verwendet ihr einziges unsichtbares ChatGPT-Hintergrundfenster im ausgewählten Profil, prüft Login und Composer, klickt bevorzugt den kleinen Diktierbutton und bestätigt den Aufnahmezustand.
4. Sprechen. Unten mittig zeigt eine fokusfreie Desktop-Pille **Hört zu …** sowie **F8 zum Stoppen · Esc Abbruch**. Die Anzeige ist klickdurchlässig und verändert weder Fokus noch Cursor.
5. F8 erneut drücken oder einen gehaltenen Hotkey loslassen. Die App lässt dem letzten gesprochenen Wort noch einen kurzen Audiopuffer und löst den Stop-Befehl genau einmal aus. Bei langen Diktaten darf ChatGPT anschließend bis zum konfigurierten Transkriptionslimit weiterverarbeiten; ein noch sichtbarer Aufnahme-/Verarbeitungszustand führt nicht mehr nach fünf Sekunden zu einem falschen Fehler oder zu einem zweiten Klick. Der Composer wird wiederholt über mehrere UI-Automation-Verfahren oder einen abgesicherten Zwischenablage-Fallback gelesen. Übernommen wird der Text erst, wenn er sich für die konfigurierte Stabilitätszeit nicht mehr verändert hat. Ein zweiter kurzer Tastendruck während des Starts wird als Stop-Wunsch vorgemerkt statt verworfen.
6. Der Text wird am ursprünglichen Cursor eingefügt und die vorherige Zwischenablage wiederhergestellt.

Jede transkribierte Diktierung wird vor dem Einfügeversuch lokal gespeichert. Über **Diktierverlauf (letzte 10)** im Tray-Menü lassen sich die letzten zehn Einträge ansehen und wieder in die Zwischenablage kopieren. Sobald ein elfter Eintrag hinzukommt, wird automatisch der älteste entfernt. Auch Einfügefehler und abgebrochene Aufnahmen erscheinen im Verlauf; bei Escape wird der Text noch transkribiert und gesichert, aber nicht ins Ziel eingefügt. Der Verlauf liegt ausschließlich lokal unter `%LOCALAPPDATA%\OpenAIFlow\dictation-history.json` und kann im Verlaufsfenster vollständig gelöscht werden.

Falls das Ziel während der Verarbeitung geschlossen wird oder das Einfügen anderweitig fehlschlägt, bleibt der fertige Text zusätzlich direkt in der Zwischenablage, sofern diese noch sicher unter Kontrolle der App ist. Er kann dann sofort mit `Strg+V` eingefügt werden. Hat zwischenzeitlich eine andere Anwendung das Clipboard geändert, überschreibt OpenAIFlow diese Änderung nicht und sichert das Diktat stattdessen im Verlauf.

Der große Audio-/Sprachmodus-Button für Voice Conversations wird nicht als Diktierbutton akzeptiert. `Ctrl+Shift+D` wird nur als Fallback verwendet, wenn kein kleiner Diktier-/Mikrofonbutton gefunden wurde; auch danach muss die Oberfläche den Aufnahme- beziehungsweise Stop-Zustand bestätigen.

Passwortfelder werden blockiert. Browser-Adressleiste, Lesezeichendialoge und URLs werden nicht als Diktat übernommen. Diktierte Inhalte werden nie geloggt. Nur der lokale Diktierverlauf enthält die letzten zehn Texte.

## Status und Fehler

Die State-Machine lautet:

```text
Idle -> Starting -> Recording -> Stopping -> ReadingText -> Pasting -> Idle
```

`Recording` wird erst gesetzt, wenn ChatGPT den Aufnahmezustand sichtbar bestätigt. Nach einem Fehler wird die Aufnahme durch einen bestätigten Cancel, einen bestätigten Seiten-Reset oder notfalls durch das bestätigte Schließen des Hintergrundfensters beendet. Kann das nicht bestätigt werden, meldet die App bewusst nicht `Idle`, sondern bleibt bedienbar und fordert zum erneuten Stoppen auf. Auch **Beenden** wartet während einer Aufnahme auf diesen Cleanup. Clipboard-Restore-Fehler sperren weitere Clipboard-Leseversuche der laufenden Sitzung und werden ausdrücklich gemeldet. Die Tray-Meldungen unterscheiden Profil-, Login-, Composer-, Start-, Stop-, Transkriptions- und Einfügefehler.

Die Desktop-Anzeige spiegelt diese Zustände als `Diktierung startet`, `Hört zu`, `Aufnahme wird beendet`, `Text wird transkribiert` und `Text wird eingefügt`. Im Zustand `Idle` ist sie vollständig ausgeblendet. Stop- und Statusabfragen verwenden bevorzugt nicht aktivierende UI Automation. Falls für den Stop ausnahmsweise ein physischer Klick nötig ist, wird das ursprüngliche Ziel unmittelbar danach wieder aktiviert; die mehrsekündige Verarbeitung lässt den sichtbaren Cursor daher nicht mehr scheinbar verschwinden.

Beim Einfügen wird das ursprüngliche Zielfenster verifiziert aktiviert. In VS Code/Codex und anderen Chromium-/Electron-WebViews wird der interne `RootWebArea`-/`ProseMirror`-Fokus bewusst nicht überschrieben, damit Cursor und `activeElement` erhalten bleiben. `Ctrl+V` wird über Win32 `SendInput` versendet; nur ein bestätigter Dispatch wird als Erfolg protokolliert.

## Diagnose im Tray-Menü

- **Diktierverlauf (letzte 10)** zeigt erfolgreiche, fehlgeschlagene und abgebrochene Diktierungen. Einträge mit erkanntem Text können dort kopiert werden.
- **ChatGPT Profil öffnen** öffnet ChatGPT sichtbar mit dem ausgewählten Profil für Anmeldung und Mikrofonfreigabe.
- **Chrome-Profil, Mikrofon & Hotkey einstellen** öffnet die OpenAI-Flow-Oberfläche. Die Auswahl wird automatisch in das gewählte Profil übernommen; ein separates Chrome-Einstellungsfenster ist nicht nötig.
- **Chrome-Profil prüfen** validiert `chrome.exe`, User-Data-Ordner, den gewählten Profilordner und dessen `Preferences`-Datei.
- **ChatGPT Diagnose speichern** protokolliert Profilstatus, Fenster, Login-Eindruck, sicher redigierte Composer-Kandidaten, Diktierbutton-Kandidaten und erkannten Aufnahmezustand.
- **Chrome-Profilordner öffnen** öffnet den validierten, ausgewählten Profilordner.

Die Diagnose protokolliert keine ChatGPT-Inhalte, Cookies, Tokens oder diktierten Texte. Namen möglicher Texteingaben werden redigiert; lediglich technische Metadaten und Textlängen werden gespeichert.

## Relevante Einstellungen

Die mitgelieferte `settings.json` enthält insbesondere:

```json
{
  "browserProfileMode": "ExistingChromeProfile",
  "preferredMicrophoneName": "Mikrofon (Logi C525 HD WebCam)",
  "setupCompleted": false,
  "chromeExecutablePath": "",
  "chromeUserDataDir": "",
  "chromeProfileDirectory": "",
  "requireConfiguredChromeProfile": true,
  "allowGuestProfile": false,
  "allowIncognitoProfile": false,
  "allowTemporaryProfile": false,
  "keepChatGptWindowHidden": true,
  "showRecordingOverlay": true,
  "recordingOverlayBottomOffsetPx": 72,
  "enableHybridPushToTalk": true,
  "pushToTalkHoldThresholdMs": 350,
  "recordingStateTimeoutMs": 5000,
  "dictationResultTimeoutMs": 30000,
  "dictationResultPollIntervalMs": 100,
  "dictationSettleDelayMs": 0,
  "dictationTextStableMs": 1100,
  "dictationStopGracePeriodMs": 250,
  "enableAudioDucking": true,
  "audioDuckingVolumePercent": 10,
  "pasteDelayMs": 25,
  "restoreClipboardDelayMs": 180
}
```

`recordingStateTimeoutMs` bleibt die kurze Bestätigungsfrist für den Aufnahmestart. Beim Stoppen gilt mindestens `dictationResultTimeoutMs`, damit auch lange Diktate vollständig verarbeitet werden können. Derselbe Wert gilt anschließend für das wiederholte frische Suchen und Lesen des ChatGPT-Composers; ein einzelnes leeres Ergebnis beendet die Suche nicht. `dictationTextStableMs` verhindert, dass ein frühes Teiltranskript eingefügt wird; die Wartezeit beginnt bei jeder Textänderung neu. `dictationStopGracePeriodMs` schützt das letzte gesprochene Wort vor einem zu harten Aufnahmeende. Der alte feste Settle-Delay bleibt deaktiviert, damit ein fertiges Ergebnis ohne unnötige Mehrsekundenpause übernommen wird.

`enableHybridPushToTalk` lässt den vorhandenen Hotkey gleichzeitig als Toggle und als Halten-zum-Sprechen-Taste arbeiten. Ein Tastendruck ab `pushToTalkHoldThresholdMs` wird beim Loslassen automatisch beendet; kürzere Tastendrücke verhalten sich weiterhin wie bisher.

Mit `enableAudioDucking` werden andere laufende Windows-Wiedergabesitzungen erst nach einem bestätigten Aufnahmestart leiser. `audioDuckingVolumePercent` legt ihren verbleibenden Anteil am jeweiligen Ausgangspegel fest (Standard: 10 %). Sobald die Aufnahme beendet oder abgebrochen wird, ein Fehler zurück auf Idle führt oder die App geschlossen wird, werden die zuvor gespeicherten Pegel wiederhergestellt.

## Logs

Die Release-Logs liegen hier:

```text
bin\Release\net8.0-windows\logs\app.log
```

Bei Problemen zuerst **Chrome-Profil prüfen** und danach **ChatGPT Diagnose speichern** ausführen. Im Log stehen nur technische Zustände wie Profilvalidierung, Kandidatentypen, Aufnahmeerkennung, Textlänge und Einfügeerfolg.

## Tests

Die Solution enthält deterministische Regressionstests für Zustandsbestätigung am Timeout-Rand, Transkriptstabilität, Push-to-talk, Clipboard-Snapshots, Sicherheitsregeln und Verlaufspersistenz:

```powershell
dotnet test .\OpenAIFlow.sln -c Release
```
