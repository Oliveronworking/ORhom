# ORhom

ORhom ist eine Windows-Tray-App für lokale deutsche Diktierung. Ein Klick auf die kompakte Diktierleiste oder der eingestellte Hotkey startet die Aufnahme direkt am gewählten Windows-Mikrofon. Während der Aufnahme stehen **Stopp & einfügen** und ein separates **X** zum Abbrechen sichtbar nebeneinander. Der reguläre Stopp transkribiert lokal mit `whisper.cpp` über Vulkan und fügt den Text am ursprünglichen Cursor ein. Alternativ funktioniert derselbe Hotkey als Push-to-talk: länger halten, sprechen und zum Stoppen loslassen. Ein kurzer Tastendruck behält den Toggle-Modus. Escape entspricht dem Abbruch und versucht, bereits gesprochenen Text im lokalen Diktierverlauf zu retten.

Die Spracherkennung läuft standardmäßig vollständig lokal, fest auf Deutsch (`de`) und ohne OpenAI-API-Key. Als ausdrücklich auswählbarer Fallback bleibt die bisherige ChatGPT-Browser-Diktierung erhalten.

## Download und Installation

Die aktuelle Windows-Version steht auf der
[GitHub-Releases-Seite](https://github.com/Oliveronworking/ORhom/releases/latest)
bereit:

1. `ORhom-Setup-<Version>-win-x64.exe` herunterladen.
2. ORhom beenden, falls bereits eine ältere Version im Tray läuft.
3. Den Installer starten. Er installiert ORhom mit der eingebetteten
   .NET-8-Laufzeit, richtet bei Bedarf die Microsoft-Visual-C++-Laufzeit ein und
   legt eine Startmenü-Verknüpfung an.
4. ORhom starten und Mikrofon sowie Hotkey auswählen.

Alternativ enthält jedes Release eine portable
`ORhom-<Version>-win-x64-portable.exe`. Dafür muss die aktuelle Microsoft Visual
C++ 2015–2022 Redistributable (x64) bereits installiert sein; auf einem neuen
Rechner wird deshalb der Installer empfohlen. Die Datei `SHA256SUMS.txt` enthält
die Prüfsummen aller Downloads.

Derzeit wird **Windows 10/11 x64** unterstützt. macOS und Linux benötigen wegen
WinForms, WASAPI, globaler Win32-Hotkeys sowie Windows-UI-Automation eigene
Plattformimplementierungen; eine Windows-EXE kann auf diesen Systemen nicht
ausgeführt werden.

## Bedienung auf einen Blick

- Das Hauptfenster führt in drei Schritten durch **Modus wählen**, **Mikrofon auswählen** und **Shortcut festlegen**.
- Unter **Größe des Sprachfelds** lässt sich die Diktierleiste jederzeit auf **Klein**, **Mittel** oder **Groß** stellen. Neue und bisherige Installationen starten platzsparend mit **Klein**.
- Im Bereitschaftszustand startet ein Klick auf die Diktierleiste das Diktat; der konfigurierte Hotkey funktioniert app-übergreifend.
- Während der Aufnahme beendet **Stopp & einfügen** das Diktat regulär. Das separate **X** beziehungsweise Escape bricht ohne Einfügen ab.
- Das Tray-Menü zeigt Status und Hinweise auf Deutsch. Seine Primäraktion passt sich dem Zustand an, etwa **Diktieren**, **Aufnahme stoppen** oder **Verarbeitung abbrechen**.
- Im Tray lassen sich das Mikrofon schnell auswählen, das letzte Diktat erneut einfügen und die Diktierleiste ein- oder ausblenden.
- Technische Funktionen und die ChatGPT-Diagnose liegen gesammelt unter **Erweitert**.

## Voraussetzungen und Build

- Windows 10 ab Version 1809 oder Windows 11, jeweils x64
- aktueller AMD-Adrenalin-Treiber mit Vulkan-Unterstützung; Zielsystem ist eine Radeon RX 7700 XT
- ungefähr 2 GB freier Speicher für Modell, Prüfdatei und Downloadreserve
- Google Chrome und ein angemeldetes Profil nur für den optionalen Browser-Fallback

Der Release-Installer bringt die benötigte .NET-8- und
Microsoft-Visual-C++-Laufzeit mit. Nur für einen Build aus dem Quellcode wird
zusätzlich das .NET 8 SDK benötigt:

```powershell
dotnet build .\ORhom.sln -c Release
Start-Process "bin\Release\net8.0-windows\ORhom.exe"
```

Für die lokale Einzeldatei-Installation zuerst eine laufende Vorgängerversion über
das Tray-Menü beenden und anschließend aus dem Repository ausführen:

```powershell
.\scripts\Install-ORhom.ps1
```

Das Skript veröffentlicht und installiert `ORhom.exe`, legt Desktop- und
Startmenü-Verknüpfungen namens **ORhom** an und übernimmt vorhandene Daten aus
`%LOCALAPPDATA%\OliSpeechToText`.
Eine alte Taskleisten-Anheftung wird sicher entfernt; **ORhom** muss danach
einmal neu an die Taskleiste angeheftet werden.

Beim ersten Start öffnet sich das Hauptfenster mit den drei Schritten **Modus wählen**, **Mikrofon auswählen** und **Shortcut festlegen**. **Speichern & losdiktieren** übernimmt die Auswahl und stellt ORhom im Hintergrund bereit. Einzelne Buchstaben, Zahlen und die Leertaste werden nicht als globale Shortcuts akzeptiert; sicher sind eine F-Taste oder eine Kombination mit Strg, Alt oder Umschalt. Gleichzeitig lädt ORhom das gepinnte Modell aus der offiziellen, von `whisper.cpp` verlinkten Hugging-Face-Ablage. Downloadfortschritt und Prüfschritte erscheinen in der Diktierleiste. Nach erfolgreicher Größen- und SHA-256-Prüfung liegt das Modell unter `%LOCALAPPDATA%\ORhom\models\whisper.cpp` und wird bei späteren Starts wiederverwendet. Unvollständige oder beschädigte Dateien werden nie als fertiges Modell veröffentlicht.

Das X des Hauptfensters und **Im Hintergrund schließen** blenden nur die Einstellungsoberfläche aus; Diktierung, Status und Beenden bleiben über den Infobereich der Taskleiste erreichbar. Ein Doppelklick auf das Tray-Symbol öffnet die Einstellungen wieder. Eine Chrome-Profilauswahl erscheint nur im Browser-Fallback.

## Lokale Spracherkennung

- Engine: `whisper.cpp`, eingebettet über `Whisper.net.Runtime.Vulkan` 1.9.1 (native Basis: gepinnter whisper.cpp-Commit `f24588a`, entsprechend v1.8.5)
- Aufnahme: `NAudio.Wasapi` 2.3.0 im gemeinsam genutzten Windows-Audiomodus
- Modell: unquantisiertes, mehrsprachiges `ggml-large-v3-turbo.bin`
- Sprache/Aufgabe: fest `de`, Transkription und keine automatische Spracherkennung
- Decoder: Greedy-Decoding mit Ausgangstemperatur `0`; ein kurzer lokaler Fachwort-Prompt stabilisiert insbesondere Schreibweisen wie `ORhom`, `Codex`, `GitHub`, `PowerShell`, `.NET`, `JSON`, `Vulkan` und `Whisper Large V3 Turbo`
- GPU: vollständiger GPU-Offload und Flash Attention; die RX 7700 XT wird aus der Vulkan-Geräteliste erkannt und auch dann gezielt gewählt, wenn sie nicht Gerät 0 ist. Ein fehlendes Vulkan-Backend wird als Fehler gemeldet, nicht still als CPU-Lauf fortgesetzt
- Modellquelle: immutable Revision `5359861c739e955e79d9a303bcbc70fb988958b1` von `ggerganov/whisper.cpp`
- Modellgröße: `1.624.555.275` Bytes
- SHA-256: `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`

Das Modell und der Vulkan-Kontext werden im Hintergrund einmal geladen und mit einer verworfenen synthetischen Inferenz vollständig vorgewärmt, bevor die App „bereit“ meldet. Dadurch trifft auch das erste echte Diktat auf den warmen nativen Inferenzpfad; es muss weder ein neuer `whisper-cli`-Prozess starten noch das 1,6-GB-Modell erneut eingelesen werden. Ein deutsches Community-Finetuning wurde bewusst nicht zum automatischen Standard gemacht: Das derzeit stärkste gefundene Modell veröffentlicht kein autorenseitiges GGML-Artefakt mit gleichwertig belastbarer Provenienz.

Die App nimmt per WASAPI über die stabile Windows-Geräte-ID auf, mischt Mehrkanalton phasenrobust zu Mono und resampelt im Speicher auf 16 kHz. Vor der Inferenz werden nicht-finite Samples neutralisiert und nahezu digitale Stille an den äußeren Rändern konservativ entfernt. Die dafür verwendete sehr niedrige Content-Schwelle ist bewusst vom eigentlichen Sprach-Gate getrennt; zusammen mit 300 ms Schutzpolster bleiben dadurch auch leise Vor- und Nachsilben erhalten. Die vorhandene Fokus-, Clipboard-, Paste-, Audio-Ducking- und Verlaufslogik wird als gemeinsamer Abschlussweg verwendet.

Im Browser-Fallback hält ORhom während seiner Laufzeit genau ein eigenes ChatGPT-Fenster im Hintergrund. Es wird minimiert gestartet, über einen generischen App-Marker und einen profilgebundenen Hash-Marker wiedererkannt und nur nahezu transparent für kurze UI-Automationsschritte aktiviert. Ein einmaliger URL-Marker ordnet einen neuen Chrome-Start eindeutig zu; andere gleichzeitig geöffnete Chrome-Profile werden nicht verändert. **Erweitert > ChatGPT-Profil öffnen** öffnet dagegen bewusst ein separates, unmarkiertes Nutzerfenster, das ORhom weder minimiert noch schließt.

## Browser-Fallback einrichten

In **ORhom öffnen** kann unter **Modus wählen** jederzeit `ChatGPT-Browser-Diktierung (Fallback)` ausgewählt werden. Nur in diesem Modus erkennt ORhom die Standardpfade von Chrome und startet es mit den Parametern des ausgewählten Profils, zum Beispiel:

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

1. In der ORhom-Einrichtung das gewünschte Chrome-Profil auswählen.
2. Das gewünschte Mikrofon auswählen.
3. In das Tastenkombinationsfeld klicken und zum Beispiel `F8` drücken.
4. **Speichern & losdiktieren** wählen. ORhom setzt den Eingang im unsichtbaren Browser des gewählten Profils und bereitet die ChatGPT-Seite frisch vor.
5. Falls ChatGPT noch nicht angemeldet ist: Im Tray-Menü **Erweitert > ChatGPT-Profil öffnen** wählen, anmelden und den Mikrofonzugriff für `https://chatgpt.com` erlauben.
6. In ein beliebiges Zieltextfeld klicken und die gewählte Tastenkombination testen.

Beim ersten Start erkennt ORhom die lokal vorhandenen Chrome-Profile automatisch. In **ORhom öffnen** kann das gewünschte Profil anhand seines Chrome-Namens und Profilordners ausgewählt werden. Die Auswahl wird zusammen mit den auf diesem PC erkannten Chrome-Pfaden gespeichert. Dadurch kann dieselbe Anwendung auf einem weiteren PC eingerichtet werden, ohne Benutzerpfade oder `Profile 3` von Hand in `settings.json` einzutragen.

Fehlt das ausgewählte Profil später, startet die Diktierung nicht und die Tray-Meldung fordert zur erneuten Auswahl in ORhom auf.

## Nutzung

1. Den Cursor in das gewünschte Zieltextfeld setzen.
2. Die Diktierleiste anklicken oder den eingestellten Hotkey – standardmäßig F8 – kurz drücken beziehungsweise gedrückt halten. ORhom prüft zuerst, ob das konfigurierte Mikrofon noch aktiv ist, und merkt sich Fenster sowie Eingabefeld. Die Zwischenablage wird erst unmittelbar vor dem Einfügen gesichert, damit zwischenzeitliche Kopiervorgänge des Benutzers erhalten bleiben.
3. Im Standardmodus startet die App eine lokale WASAPI-Aufnahme. Im Browser-Fallback verwendet sie stattdessen ihr unsichtbares ChatGPT-Hintergrundfenster, prüft Login und Composer und bestätigt dort den Aufnahmezustand.
4. Sprechen. Die fokusfreie Diktierleiste zeigt **Aufnahme läuft**, die Dauer und zwei getrennte Aktionen: **Stopp & einfügen** beendet regulär, das **X** beziehungsweise Escape bricht ohne Einfügen ab. Die Leiste bleibt auch im Bereitschaftszustand sichtbar, kann ohne Fokusverlust angeklickt und mit gedrückter linker Maustaste über alle Monitore verschoben werden. Die Position wird relativ zur Arbeitsfläche des gewählten Monitors gespeichert und bleibt bei DPI-, Auflösungs- und Monitoränderungen vollständig sichtbar.
5. **Stopp & einfügen** anklicken, den Hotkey erneut drücken oder einen gehaltenen Hotkey loslassen. Die App lässt dem letzten gesprochenen Wort noch einen kurzen Audiopuffer und löst den Stop-Befehl genau einmal aus. Lokal wird das auf 16-kHz-Mono normalisierte Audio unmittelbar mit dem warmen Vulkan-Modell transkribiert. Die längeren Stabilitäts- und Composer-Prüfungen gelten nur für den Browser-Fallback.
6. Der Text wird am ursprünglichen Cursor eingefügt und die vorherige Zwischenablage wiederhergestellt.

Jede transkribierte Diktierung wird vor dem Einfügeversuch lokal gespeichert. Über **Diktierverlauf** im Tray-Menü lassen sich die letzten zehn Einträge ansehen und wieder in die Zwischenablage kopieren. Sobald ein elfter Eintrag hinzukommt, wird automatisch der älteste entfernt. Auch Einfügefehler und abgebrochene Aufnahmen erscheinen im Verlauf; bei Escape wird eine lokale Aufnahme noch transkribiert und zuerst gesichert. Im Browser-Fallback wird danach der zugehörige Composer geleert, damit die nächste Aufnahme nicht durch einen wiederhergestellten Entwurf blockiert wird. Vor Profilwechsel oder Beenden prüft ORhom einen verbliebenen Browser-Composer erneut und sichert dessen stabilen Text atomar im Verlauf. Scheitern Prüfung oder Speicherung, wird das Fenster nicht unsichtbar verworfen, sondern bei Bedarf sichtbar zur manuellen Rettung freigegeben. Der Verlauf liegt ausschließlich lokal unter `%LOCALAPPDATA%\ORhom\dictation-history.json` und kann im Verlaufsfenster vollständig gelöscht werden.

Falls das Ziel während der Verarbeitung geschlossen wird oder das Einfügen anderweitig fehlschlägt, bleibt der fertige Text zusätzlich direkt in der Zwischenablage, sofern diese noch sicher unter Kontrolle der App ist. Er kann dann sofort mit `Strg+V` eingefügt werden. Hat zwischenzeitlich eine andere Anwendung das Clipboard geändert, überschreibt ORhom diese Änderung nicht und sichert das Diktat stattdessen im Verlauf.

Der große Audio-/Sprachmodus-Button für Voice Conversations wird nicht als Diktierbutton akzeptiert. `Ctrl+Shift+D` wird nur als Fallback verwendet, wenn kein kleiner Diktier-/Mikrofonbutton gefunden wurde; auch danach muss die Oberfläche den Aufnahme- beziehungsweise Stop-Zustand bestätigen.

Passwortfelder werden blockiert. Browser-Adressleiste, Lesezeichendialoge und URLs werden nicht als Diktat übernommen. Diktierte Inhalte werden nie geloggt. Nur der lokale Diktierverlauf enthält die letzten zehn Texte.

## Status und Fehler

Die State-Machine lautet:

```text
Idle -> Starting -> Recording -> Stopping -> ReadingText -> Pasting -> Idle
```

`Recording` wird lokal erst nach erfolgreichem Start der WASAPI-Aufnahme gesetzt; im Browser-Fallback erst, wenn ChatGPT den Aufnahmezustand sichtbar bestätigt. Die nachfolgenden Composer-Sicherungen gelten nur für den Browser-Fallback: Sobald dort ein regulärer Stop-Befehl gesendet wurde, führt die App keinen automatischen Cancel, Seiten-Reset oder Fensterschluss mehr aus. Der Composer wird erst geleert, nachdem das Transkript synchron im lokalen Diktierverlauf gespeichert wurde. Entspricht ein übrig gebliebener Composer-Text exakt dem neuesten, höchstens 24 Stunden alten Eintrag `Abgebrochen · Text gerettet` oder `Fehler · Text gerettet`, wird er vor dem nächsten Start bestätigt gelöscht und die Aufnahme genau einmal erneut gestartet. Älterer, bereits abgeschlossener oder unbekannter Text blockiert weiterhin sicher, statt überschrieben zu werden. Beenden während einer noch aktiven Aufnahme wird abgelehnt; zuerst muss der eingestellte Hotkey oder Escape den Zustand sicher abschließen. Ein reguläres Beenden schließt nur das mit App- und Profilmarker versehene ORhom-Hintergrundfenster; sichtbare, unmarkierte Chrome-Fenster bleiben unangetastet. Reagiert Chrome nicht auf den Schließbefehl, wird das eigene Fenster vollständig sichtbar und als Nutzerfenster freigegeben. Ein expliziter Abbruch beendet nur die laufende Aufnahme und sichert verwertbaren Text. Clipboard-Restore-Fehler sperren weitere Clipboard-Leseversuche der laufenden Sitzung und werden ausdrücklich gemeldet.

Die Diktierleiste spiegelt diese Zustände als `Bereit`, `Mikrofon wird aktiviert`, `Aufnahme läuft`, `Aufnahme beendet`, `Transkription läuft` und `Text wird eingefügt`. Das Tray formuliert dieselben Phasen kompakt als `Bereit`, `Wird gestartet`, `Hört zu`, `Aufnahme wird beendet`, `Wird transkribiert` und `Wird eingefügt`; Hinweis und Primäraktion ändern sich passend dazu. Im Zustand `Idle` bleibt die Leiste als sichtbares Aktivitätszeichen eingeblendet; Fehler erscheinen kurz direkt in der Leiste. Die Leiste verwendet `WS_EX_NOACTIVATE`, damit ein Klick das zuvor aktive Textfeld nicht fokussiert. Im Pending-Text-Fehlerpfad wird nur das ursprüngliche Fenster mit einer begrenzten Win32-Operation wieder aktiviert; blockierende UI-Automation auf veralteten Electron-/WebView-Elementen wird dort nicht mehr ausgeführt.

Beim Einfügen wird das ursprüngliche Zielfenster verifiziert aktiviert. In VS Code/Codex und anderen Chromium-/Electron-WebViews wird der interne `RootWebArea`-/`ProseMirror`-Fokus bewusst nicht überschrieben, damit Cursor und `activeElement` erhalten bleiben. `Ctrl+V` wird über Win32 `SendInput` versendet; nur ein bestätigter Dispatch wird als Erfolg protokolliert.

Chromium kann unmittelbar nach dem globalen Hotkey kurz den übergeordneten `main`-Knoten statt des eigentlichen ProseMirror-Editors melden. Sobald WASAPI bereits aufnimmt, prüft ORhom diesen Fokus deshalb ein zweites Mal und übernimmt ausschließlich einen starken Editor-Fingerprint im selben Fenster, Prozess und derselben nichtleeren `RootWebArea`. Diese erneute Prüfung läuft außerhalb des UI-Threads und wird nach 180 ms sicher ignoriert. Auch beim späteren Einfügen werden Fensterhandle, Prozess-ID, unveränderter Fenstertitel, WebView-Wurzel und ein inhaltsfreier relativer Layout-Fingerprint erneut abgeglichen. Ein wiederverwendetes Handle, ein anderer Tab beziehungsweise eine andere WebView-Wurzel, ein geänderter View-Titel, ein Passwortfeld oder eine abweichende Editor-Geometrie autorisiert dadurch keinen semantischen Ersatz für das ursprüngliche Ziel.

## Tray-Menü und Diagnose

- Die oberste Zeile zeigt den aktuellen deutschen Status samt Hotkey-Hinweis; direkt darunter steht die passende Primäraktion.
- **Letztes Diktat einfügen** setzt den zuletzt erkannten Text sicher in das zuvor aktive Feld; wenn das Ziel nicht mehr eindeutig ist, bleibt der Text für `Strg+V` in der Zwischenablage. **Diktierverlauf** öffnet erfolgreiche, fehlgeschlagene und abgebrochene Diktierungen.
- **Mikrofon** erlaubt im Bereitschaftszustand die Schnellauswahl eines aktiven Windows-Audioeingangs. **Mikrofon & Einstellungen öffnen …** führt bei Bedarf in das Hauptfenster.
- **Diktierleiste anzeigen** blendet die Leiste ein oder aus. **ORhom öffnen** zeigt das Hauptfenster mit den drei Einrichtungsschritten.
- Unter **Erweitert** liegen Konfiguration und Logs sowie die Funktionen des Browser-Fallbacks: **ChatGPT-Profil öffnen**, **Chrome-Profil prüfen**, **ChatGPT-Diagnose speichern** und **Chrome-Profilordner öffnen**.
- **ChatGPT-Diagnose speichern** protokolliert Profilstatus, Fenster, Login-Eindruck, sicher redigierte Composer-Kandidaten, Diktierbutton-Kandidaten und erkannten Aufnahmezustand.

Die Diagnose protokolliert keine ChatGPT-Inhalte, Cookies, Tokens oder diktierten Texte. Namen möglicher Texteingaben werden redigiert; lediglich technische Metadaten und Textlängen werden gespeichert.

## Relevante Einstellungen

Die mitgelieferte `settings.json` enthält insbesondere:

```json
{
  "dictationProvider": "LocalWhisper",
  "preferredMicrophoneId": "",
  "browserProfileMode": "ExistingChromeProfile",
  "preferredMicrophoneName": "",
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
  "recordingOverlaySize": "Small",
  "recordingOverlayBottomOffsetPx": 72,
  "recordingOverlayMonitorDeviceName": "",
  "recordingOverlayRelativeX": null,
  "recordingOverlayRelativeY": null,
  "enableHybridPushToTalk": true,
  "pushToTalkHoldThresholdMs": 350,
  "recordingStateTimeoutMs": 5000,
  "dictationResultTimeoutMs": 300000,
  "dictationResultPollIntervalMs": 100,
  "dictationSettleDelayMs": 0,
  "dictationTextStableMs": 3000,
  "dictationStopGracePeriodMs": 250,
  "localMaxRecordingSeconds": 300,
  "enableAudioDucking": true,
  "audioDuckingVolumePercent": 10,
  "pasteDelayMs": 25,
  "restoreClipboardDelayMs": 180
}
```

`dictationProvider` akzeptiert `LocalWhisper` (Standard) oder `ChatGptBrowser`. Unbekannte beziehungsweise fehlende Werte werden datenschutzfreundlich auf die lokale Engine normalisiert. Die Sprache ist absichtlich keine frei editierbare Einstellung, sondern im Inferenzpfad fest auf `de` verdrahtet. `preferredMicrophoneId` ist die stabile WASAPI-Geräte-ID; für bestehende Installationen bleibt der Anzeigename als Migrations-Fallback erhalten.

`recordingOverlaySize` akzeptiert `Small`, `Medium` oder `Large` und kann bequem im Einstellungsfenster geändert werden. Fehlende oder ungültige Werte werden sicher auf die kompakte Größe `Small` gesetzt.

`recordingStateTimeoutMs` bleibt die kurze Bestätigungsfrist für den Aufnahmestart. Für die Transkription gilt unabhängig von älteren lokalen Einstellungen eine Sicherheitsuntergrenze von fünf Minuten; konfigurierbar sind bis zu 15 Minuten. Ein einzelnes leeres Ergebnis beendet die Suche nicht. Für `dictationTextStableMs` gilt eine Sicherheitsuntergrenze von drei Sekunden, damit Verarbeitungspausen bei langen Texten nicht als Fertigstellung gelten. `dictationStopGracePeriodMs` schützt das letzte gesprochene Wort vor einem zu harten Aufnahmeende.

`enableHybridPushToTalk` lässt den vorhandenen Hotkey gleichzeitig als Toggle und als Halten-zum-Sprechen-Taste arbeiten. Ein Tastendruck ab `pushToTalkHoldThresholdMs` wird beim Loslassen automatisch beendet; kürzere Tastendrücke verhalten sich weiterhin wie bisher.

Einstellungs- und Verlaufsdateien werden atomar ersetzt. Eine vorübergehend nicht lesbare vorhandene Datei wird niemals mit leeren Standardwerten überschrieben. Defektes JSON wird zuerst als zeitgestempelte `*.unreadable-*.json`-Sicherung im selben Ordner erhalten; erst danach darf eine neue Datei entstehen.

Mit `enableAudioDucking` werden andere laufende Windows-Wiedergabesitzungen erst nach einem bestätigten Aufnahmestart leiser. `audioDuckingVolumePercent` legt ihren verbleibenden Anteil am jeweiligen Ausgangspegel fest (Standard: 10 %). Sobald die Aufnahme beendet oder abgebrochen wird, ein Fehler zurück auf Idle führt oder die App geschlossen wird, werden die zuvor gespeicherten Pegel wiederhergestellt.

## Logs

Die Logs liegen unter `%LOCALAPPDATA%\ORhom\logs`. `app.log` wird bei 5 MB nach `app.previous.log` rotiert, und ein nicht beschreibbares Log darf die Diktierung nicht mehr beeinträchtigen:

```text
%LOCALAPPDATA%\ORhom\logs\app.log
```

Bei Problemen unter **Erweitert** zuerst **Chrome-Profil prüfen** und danach **ChatGPT-Diagnose speichern** ausführen. Im Log stehen nur technische Zustände wie Profilvalidierung, Kandidatentypen, Aufnahmeerkennung, Textlänge und Einfügeerfolg.

## Tests

Die Solution enthält deterministische Regressionstests für den gepinnten Modelldownload samt Fortschritt, SHA-256, Cache-Hit und Abbruch, lokale PCM/Float-Audiokonvertierung, Stereo-Downmix, 16-kHz-Resampling, Sprach-Gate, leise Randsilben, Randstille, Warm-up, Technik-Prompt, Provider-Migration und feste Sprache `de`. Hinzu kommen die bestehenden Tests für Zustandsbestätigung, Push-to-talk, Clipboard-Snapshots, Fokus-, Root- und Layout-Fingerprints, HWND/PID-Schutz, Sicherheitsregeln, atomare Persistenz, Chrome-Ownership, Escape-Recovery sowie Rendering und Multi-Monitor-Positionierung. Ein opt-in Hardwaretest lädt das echte Modell, verlangt ein bestätigtes Vulkan-Backend und führt eine Inferenz aus:

```powershell
dotnet test .\ORhom.sln -c Release

$env:ORHOM_RUN_WHISPER_VULKAN_INTEGRATION = '1'
dotnet test .\ORhom.sln -c Release --filter 'FullyQualifiedName~LocalWhisperVulkanIntegrationTests'
```

Der reale Regressionstest für zwei Chrome-Profile im selben User-Data-Verzeichnis ist bewusst opt-in, weil er eine interaktive Windows-Desktopsitzung mit installiertem Chrome benötigt. Er verwendet ausschließlich zwei temporäre Testprofile und prüft, dass das bereits offene Profil sichtbar, unminimiert, unverändert markiert und auf derselben URL bleibt:

```powershell
$env:ORHOM_RUN_CHROME_INTEGRATION = '1'
dotnet test .\ORhom.sln -c Release --filter 'Category=ChromeIntegration'
```

## Recherchequellen und Lizenzen

- [`whisper.cpp` Vulkan- und Modelldokumentation](https://github.com/ggml-org/whisper.cpp/blob/v1.9.1/README.md)
- [OpenAI-Modellkarte für Whisper large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo)
- [Gepinnte GGML-Modellablage](https://huggingface.co/ggerganov/whisper.cpp/tree/5359861c739e955e79d9a303bcbc70fb988958b1)
- [Whisper.net 1.9.1](https://www.nuget.org/packages/Whisper.net/1.9.1) und [Vulkan-Runtime](https://www.nuget.org/packages/Whisper.net.Runtime.Vulkan/1.9.1)
- [NAudio 2.3.0 Release Notes](https://github.com/naudio/NAudio/blob/master/RELEASE_NOTES.md)

Die Hinweise zu den MIT-lizenzierten Komponenten stehen zusätzlich in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
