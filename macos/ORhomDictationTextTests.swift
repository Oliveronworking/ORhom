import Foundation

private enum DictationTextTestFailure: Error {
    case failed(String)
}

private func expectDictationText(_ actual: String, _ expected: String, _ message: String) throws {
    guard actual == expected else {
        throw DictationTextTestFailure.failed("\(message) Expected \(String(reflecting: expected)), received \(String(reflecting: actual)).")
    }
}

private func checkDictationText(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    if !condition() { throw DictationTextTestFailure.failed(message) }
}

private func processDictationTest(_ text: String, _ preferences: DictationPreferences = DictationPreferences()) -> String {
    DictationTextProcessor.process(text, preferences: preferences)
}

func runORhomDictationTextTests() throws {
    try testDictationVocabularyAndUnicode()
    try testDictationLiteralSnippets()
    try testDictationSpokenCommands()
    try testDictationCorrectionsAndDeletions()
    try testDictationFillersAndFormatting()
    try testDictationPreferencesAndPrompt()
}

private func testDictationVocabularyAndUnicode() throws {
    try expectDictationText(processDictationTest("or hom und kodex nutzen whisper cpp."), "ORhom und Codex nutzen whisper.cpp.", "Default product spellings must be reliable.")
    let preferences = DictationPreferences(
        smartFormatting: false,
        dictionary: [
            DictationReplacement(cue: "café", replacement: "Café"),
            DictationReplacement(cue: "my dog", replacement: "Fido"),
            DictationReplacement(cue: "my", replacement: "mine"),
            DictationReplacement(cue: "bob", replacement: "Alice"),
            DictationReplacement(cue: "alice", replacement: "Eve")
        ]
    )
    try expectDictationText(processDictationTest("cafe\u{301} CAFÉ caféine pré_café café2", preferences), "Café Café caféine pré_café café2", "Unicode normalization and letter, digit, underscore boundaries must prevent partial word replacements.")
    try expectDictationText(processDictationTest("my   dog and my dogs", preferences), "Fido and mine dogs", "The longest phrase at the same location must win, with flexible horizontal whitespace.")
    try expectDictationText(processDictationTest("bob alice", preferences), "Alice Eve", "Dictionary outputs must never cascade through another dictionary entry.")
    try expectDictationText(processDictationTest("my\ndog", preferences), "mine\ndog", "A phrase must not consume paragraph boundaries.")
    let regexCharacters = DictationPreferences(smartFormatting: false, dictionary: [DictationReplacement(cue: "c++", replacement: "C++")])
    try expectDictationText(processDictationTest("c++ c++driver ac++", regexCharacters), "C++ c++driver ac++", "Cues must be escaped as literal strings, including regular expression metacharacters.")
}

private func testDictationLiteralSnippets() throws {
    let literal = "  hallo ähm ORhom Komma\n\n  kodex\tende  "
    let preferences = DictationPreferences(
        dictionary: [DictationReplacement(cue: "kodex", replacement: "Codex")],
        snippets: [DictationReplacement(cue: "meine signatur", replacement: literal)]
    )
    try expectDictationText(processDictationTest("meine signatur", preferences), literal, "Multiline snippet output, leading/trailing spaces, fillers and command text must remain byte-for-byte intact.")
    try expectDictationText(processDictationTest("vorher meine signatur nachher", preferences), "Vorher " + literal + " nachher", "A literal snippet must remain exact when embedded in prose.")
    let overlap = DictationPreferences(
        dictionary: [DictationReplacement(cue: "signatur", replacement: "WRONG")],
        snippets: [DictationReplacement(cue: "signatur", replacement: "richtig")]
    )
    try expectDictationText(processDictationTest("signatur", overlap), "richtig", "Snippet cues must win over dictionary cues.")
    let commandCue = DictationPreferences(snippets: [DictationReplacement(cue: "ORhom Komma", replacement: "<literal>")])
    try expectDictationText(processDictationTest("ORhom Komma", commandCue), "<literal>", "Configured snippet cues must take priority over built-in commands.")
    let literalDictionary = DictationPreferences(dictionary: [DictationReplacement(cue: "produkt", replacement: "ORhom Komma ähm")])
    try expectDictationText(processDictationTest("produkt", literalDictionary), "ORhom Komma ähm", "Dictionary outputs must also be protected against later command and filler processing.")
    let privateScalar = "\u{F0000}"
    let unusual = DictationPreferences(smartFormatting: false, snippets: [DictationReplacement(cue: "test", replacement: privateScalar + "\nORhom Punkt")])
    try expectDictationText(processDictationTest(privateScalar + " test", unusual), privateScalar + " " + privateScalar + "\nORhom Punkt", "Internal marker characters must never collide with source or recursively expand literal replacement content.")
}

private func testDictationSpokenCommands() throws {
    try expectDictationText(processDictationTest("hallo ORhom Komma welt ORhom Punkt"), "Hallo, welt.", "Explicit punctuation commands must replace command phrases and preserve prose.")
    try expectDictationText(processDictationTest("hallo OR hom Punkt. weiter ORhom Fragezeichen!"), "Hallo. Weiter?", "Spaced ORhom prefixes and ASR punctuation around commands must work without duplicate punctuation.")
    try expectDictationText(processDictationTest("hallo Sprachbefehl, Komma welt Diktat, Punkt."), "Hallo, welt.", "Clearly pronounceable command prefixes and transcribed commas must be accepted.")
    try expectDictationText(processDictationTest("Hallo Team Sprachbefehl Komma, morgen testen wir die Aufnahme Sprachbefehl neue Absatz viele Grüße Sprachbefehl Punkt"), "Hallo Team, morgen testen wir die Aufnahme\n\nViele Grüße.", "The real local Whisper fixture must process recognized command phrases and ASR punctuation exactly.")
    try expectDictationText(processDictationTest("Hallo Team, Sprachbefehl Komma, morgen testen wir die Aufnahme, Sprachbefehl neuer Absatz, viele Grüße, Sprachbefehl Punkt"), "Hallo Team, morgen testen wir die Aufnahme\n\nViele Grüße.", "Real Whisper punctuation before and after command phrases must not leave doubled commas or comma-period combinations.")
    try expectDictationText(processDictationTest("Hallo!, Sprachbefehl Punkt."), "Hallo.", "An explicit punctuation command must supersede immediately preceding ASR punctuation.")
    try expectDictationText(processDictationTest("Fertig. Sprachbefehl neuer Absatz Weiter! Sprachbefehl neue Zeile Liste: Sprachbefehl Listenpunkt Milch"), "Fertig.\n\nWeiter!\nListe:\n- Milch", "Structural commands must retain actual sentence endings and colons.")
    try expectDictationText(processDictationTest("Ein Diktat und ein Sprachbefehl helfen."), "Ein Diktat und ein Sprachbefehl helfen.", "Command prefix words without a known command must remain ordinary prose.")
    try expectDictationText(processDictationTest("Der Punkt ist ein Komma. Ein neuer Absatz fehlt."), "Der Punkt ist ein Komma. Ein\n\nFehlt.", "Ordinary punctuation words must remain; explicit newline commands are reserved phrases.")
    try expectDictationText(processDictationTest("A period is a point, and the comma is useful."), "A period is a point, and the comma is useful.", "Ambiguous English punctuation words must never be global replacements.")
    try expectDictationText(processDictationTest("erste neue Zeile zweite neuer Absatz dritte"), "Erste\nZweite\n\nDritte", "German line and paragraph commands must produce actual newlines.")
    try expectDictationText(processDictationTest("first ORhom new line second ORhom new paragraph third"), "First\nSecond\n\nThird", "Prefixed English structural commands must work.")
    try expectDictationText(processDictationTest("ORhom Listenpunkt milch ORhom bullet point brot"), "- Milch\n- Brot", "Bullet list commands must retain list markers and capitalize each item.")
    try expectDictationText(processDictationTest("ORhom Nummer eins erster ORhom number two zweiter ORhom Nummer 3 dritter"), "1. Erster\n2. Zweiter\n3. Dritter", "Numbered list commands must work for spoken numbers and explicit digits.")
    try expectDictationText(processDictationTest("Nummer eins und number two bleiben Wörter."), "Nummer eins und number two bleiben Wörter.", "Unprefixed numbers must remain prose.")
    let disabled = DictationPreferences(smartFormatting: false, removeFillers: false, spokenCommands: false, dictionary: [])
    try expectDictationText(processDictationTest("hallo ORhom Komma neue Zeile", disabled), "hallo ORhom Komma neue Zeile", "Disabling spoken commands must preserve their literal text.")
    try expectDictationText(processDictationTest("testORhom Komma ORhom Kommando"), "testORhom Komma ORhom Kommando", "A command prefix inside a word and unknown commands must not be consumed.")
}

private func testDictationCorrectionsAndDeletions() throws {
    try expectDictationText(processDictationTest("Erster Satz. Zweiter Satz. ORhom letzten Satz löschen Weiter."), "Erster Satz. Weiter.", "Deletion must remove only the latest sentence in the current recording.")
    try expectDictationText(processDictationTest("Erster ORhom Punkt Zweiter ORhom Punkt ORhom delete last sentence Ende."), "Erster. Ende.", "Deletion must respect spoken punctuation already emitted in this recording.")
    try expectDictationText(processDictationTest("Erster Satz. noch unvollständig ORhom delete last sentence Ende."), "Erster Satz. Ende.", "Deletion must also remove an unfinished last sentence.")
    try expectDictationText(processDictationTest("Erster Satz. Wert 3.14 Grad. ORhom delete last sentence Ende."), "Erster Satz. Ende.", "Decimal and identifier dots must not split a sentence for deletion.")
    try expectDictationText(processDictationTest("Hallo falsches ORhom letztes Wort löschen richtig"), "Hallo richtig", "Delete last word must preserve the earlier dictation.")
    try expectDictationText(processDictationTest("ORhom delete last sentence"), "", "Deleting from an empty recording must be harmless.")
    try expectDictationText(processDictationTest("Treffen Montag. ORhom ersetze Montag durch Dienstag ORhom Korrektur Ende Danach."), "Treffen Dienstag. Danach.", "German corrections must replace the last preceding target and retain following prose.")
    try expectDictationText(processDictationTest("Bob met Bob. ORhom replace Bob with Alice ORhom end correction Next."), "Bob met Alice. Next.", "English corrections must replace only the latest whole-word occurrence.")
    try expectDictationText(processDictationTest("Treffen Montag. Sprachbefehl, ersetze Montag durch Dienstag Diktat, Korrektur Ende. Danach."), "Treffen Dienstag. Danach.", "Correction commands and terminators must accept pronounceable prefixes with ASR separators.")
    try expectDictationText(processDictationTest("Bobcat wartet. ORhom replace Bob with Alice ORhom end correction"), "Bobcat wartet. ORhom replace Bob with Alice ORhom end correction", "A missing whole-word correction target must preserve the complete command.")
    try expectDictationText(processDictationTest("Montag bleibt. ORhom ersetze Montag durch Dienstag Danach kommt mehr."), "Montag bleibt. ORhom ersetze Montag durch Dienstag Danach kommt mehr.", "An unterminated prefixed correction must remain readable and must never swallow subsequent dictation.")
    try expectDictationText(processDictationTest("Treffen Montag. Korrigiere Montag zu Dienstag. Danach."), "Treffen Dienstag. Danach.", "A separately punctuated German self-correction must be supported.")
    try expectDictationText(processDictationTest("Ich sage korrigiere Montag zu Dienstag."), "Ich sage korrigiere Montag zu Dienstag.", "Ordinary prose containing the correction verb must not be rewritten.")
    let preferences = DictationPreferences(snippets: [DictationReplacement(cue: "signatur", replacement: "Mein Satz. Noch ein Satz.")])
    try expectDictationText(processDictationTest("Vorher. signatur ORhom delete last sentence Nachher.", preferences), "Vorher. Nachher.", "An explicit destructive command may remove a snippet as an atomic insertion, never modify its interior.")
}

private func testDictationFillersAndFormatting() throws {
    try expectDictationText(processDictationTest("Ähm, um acht gehen wir äh zum Café."), "Um acht gehen wir zum Café.", "German fillers must be removed without deleting the German preposition um.")
    let english = DictationPreferences(language: "en", dictionary: [])
    try expectDictationText(processDictationTest("Um, I uh think this is useful, like a tool you know.", english), "I think this is useful, like a tool you know.", "English filler removal must remain conservative about meaningful phrases.")
    let auto = DictationPreferences(language: "auto", dictionary: [])
    try expectDictationText(processDictationTest("um acht uh starten", auto), "Um acht starten", "Automatic language mode must retain ambiguous um.")
    try expectDictationText(processDictationTest("UM stands for a university.", english), "UM stands for a university.", "Uppercase acronyms must not be treated as filler words.")
    let preserved = DictationPreferences(removeFillers: false, dictionary: [])
    try expectDictationText(processDictationTest("ähm, hallo", preserved), "Ähm, hallo", "Filler removal must be independently optional.")
    try expectDictationText(processDictationTest("hallo   welt .\n  nächster Satz", DictationPreferences(dictionary: [])), "Hallo welt.\nNächster Satz", "Formatting must tidy prose spacing and sentence starts.")
    for technicalText in [
        "api.client", "helloWorld", "foo_bar", "https://example.com/api.client?q=um",
        "me@example.com", "/Users/me/helloWorld.swift", "v1.2.3",
        "{\"name\": \"hello  world\", \"um\": true}", "let um = api.client", "`api.client`"
    ] {
        try expectDictationText(processDictationTest(technicalText, DictationPreferences(dictionary: [])), technicalText, "Technical tokens and clear code must retain case, whitespace and semantic words.")
    }
    try expectDictationText(processDictationTest("version 3.14 ist bereit. nächster Satz."), "Version 3.14 ist bereit. Nächster Satz.", "Formatting must distinguish decimals from sentence boundaries.")
}

private func testDictationPreferencesAndPrompt() throws {
    let name = "at.orhom.dictation-text-tests.\(UUID().uuidString)"
    guard let defaults = UserDefaults(suiteName: name) else { throw DictationTextTestFailure.failed("Cannot create isolated defaults.") }
    defer { defaults.removePersistentDomain(forName: name) }
    let preferences = DictationPreferences(
        language: "en-US", smartFormatting: false, removeFillers: false, spokenCommands: false,
        dictionary: [DictationReplacement(cue: " my name ", replacement: "Alice")],
        snippets: [DictationReplacement(cue: "signatur", replacement: "  Gruß\nAlice  ")]
    )
    try checkDictationText(preferences.save(to: defaults), "Preferences must save as Codable data.")
    try checkDictationText(DictationPreferences.load(from: defaults) == preferences.validated(), "All preference fields and stable replacement IDs must survive persistence.")
    try expectDictationText(DictationPreferences(language: "de_AT").recognitionLanguage, "de", "Locale variants must reduce to a validated Whisper language code.")
    try expectDictationText(DictationPreferences(language: "de --prompt bad").recognitionLanguage, "auto", "Invalid subprocess language codes must safely fall back to automatic detection.")
    defaults.set(Data("invalid".utf8), forKey: DictationPreferences.defaultsKey)
    try checkDictationText(DictationPreferences.load(from: defaults).smartFormatting, "Corrupt defaults must fall back safely.")
    defaults.set(Data("{\"language\":\"en\"}".utf8), forKey: DictationPreferences.defaultsKey)
    let migrated = DictationPreferences.load(from: defaults)
    try checkDictationText(migrated.language == "en" && migrated.spokenCommands && !migrated.dictionary.isEmpty, "Missing preference fields must receive compatible defaults.")
    let promptPreferences = DictationPreferences(
        dictionary: [
            DictationReplacement(cue: "name", replacement: "Alice"),
            DictationReplacement(cue: "duplicate", replacement: "alice"),
            DictationReplacement(cue: "bad", replacement: "line\nnew instruction"),
            DictationReplacement(cue: "long", replacement: String(repeating: "x", count: 81))
        ],
        snippets: [DictationReplacement(cue: "my signature", replacement: "PRIVATE ADDRESS\nSECRET")]
    )
    let prompt = promptPreferences.recognitionPrompt()
    try checkDictationText(prompt.contains("ORhom") && prompt.contains("Codex") && prompt.contains("whisper.cpp") && prompt.contains("Alice"), "Prompt must include canonical product and personal vocabulary.")
    for command in [
        "Sprachbefehl Komma", "Sprachbefehl neuer Absatz", "Sprachbefehl Punkt",
        "Sprachbefehl letzten Satz löschen", "Sprachbefehl ersetze", "Sprachbefehl Korrektur Ende",
        "ORhom new paragraph", "ORhom delete last sentence", "ORhom replace", "ORhom end correction"
    ] {
        try checkDictationText(prompt.contains(command), "Exact command phrases must be present in the default recognition hint: \(command).")
    }
    try checkDictationText(prompt.hasPrefix("ORhom, Codex, whisper.cpp, Sprachbefehl Komma, Sprachbefehl neuer Absatz, Sprachbefehl Punkt"), "The three proven command phrases must have priority before optional vocabulary.")
    var commandsDisabled = promptPreferences
    commandsDisabled.spokenCommands = false
    try checkDictationText(!commandsDisabled.recognitionPrompt().contains("Sprachbefehl"), "Disabled spoken commands must not influence ASR command vocabulary.")
    var largeVocabulary = promptPreferences
    largeVocabulary.dictionary = (0..<100).map { DictationReplacement(cue: "alias\($0)", replacement: "Name\($0)") }
    let largePrompt = largeVocabulary.recognitionPrompt()
    try checkDictationText(largePrompt.contains("Name0") && largePrompt.contains("ORhom end correction") && largePrompt.count <= 800, "Large personal dictionaries must retain both priority vocabulary and core DE/EN command hints within the bound.")
    try checkDictationText(!prompt.contains("PRIVATE") && !prompt.contains("SECRET") && !prompt.contains("instruction") && !prompt.contains("alice"), "Prompt must exclude private snippet expansion, control characters and duplicate spellings.")
    for limit in [0, 1, 7, 20, 800, 8_000] {
        try checkDictationText(promptPreferences.recognitionPrompt(maximumCharacters: limit).count <= min(limit, 2_000), "Recognition prompt must always respect its maximum character bound.")
    }
    try expectDictationText(promptPreferences.recognitionPrompt(maximumCharacters: -1), "", "Negative prompt lengths must be harmless.")
    let invalid = DictationPreferences(dictionary: [
        DictationReplacement(cue: "", replacement: "empty cue"),
        DictationReplacement(cue: "name", replacement: ""),
        DictationReplacement(cue: "valid", replacement: "ok"),
        DictationReplacement(cue: "VALID", replacement: "duplicate")
    ])
    try checkDictationText(invalid.validated().dictionary.count == 1, "Invalid and duplicate cues must be filtered deterministically.")
}
