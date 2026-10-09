import Foundation

struct DictationReplacement: Codable, Equatable, Identifiable, Sendable {
    var id: UUID
    var cue: String
    var replacement: String

    init(id: UUID = UUID(), cue: String, replacement: String) {
        self.id = id
        self.cue = cue
        self.replacement = replacement
    }
}

struct DictationPreferences: Codable, Equatable, Sendable {
    static let defaultsKey = "dictationPreferences.v1"
    static let defaultDictionary: [DictationReplacement] = [
        DictationReplacement(cue: "or hom", replacement: "ORhom"),
        DictationReplacement(cue: "orhom", replacement: "ORhom"),
        DictationReplacement(cue: "kodex", replacement: "Codex"),
        DictationReplacement(cue: "codex", replacement: "Codex"),
        DictationReplacement(cue: "whisper cpp", replacement: "whisper.cpp"),
        DictationReplacement(cue: "whisper punkt cpp", replacement: "whisper.cpp")
    ]

    var language: String
    var smartFormatting: Bool
    var removeFillers: Bool
    var spokenCommands: Bool
    var dictionary: [DictationReplacement]
    var snippets: [DictationReplacement]

    init(
        language: String = "de",
        smartFormatting: Bool = true,
        removeFillers: Bool = true,
        spokenCommands: Bool = true,
        dictionary: [DictationReplacement] = Self.defaultDictionary,
        snippets: [DictationReplacement] = []
    ) {
        self.language = language
        self.smartFormatting = smartFormatting
        self.removeFillers = removeFillers
        self.spokenCommands = spokenCommands
        self.dictionary = dictionary
        self.snippets = snippets
    }

    private enum CodingKeys: String, CodingKey {
        case language, smartFormatting, removeFillers, spokenCommands
        case dictionary, snippets
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        language = try values.decodeIfPresent(String.self, forKey: .language) ?? "de"
        smartFormatting = try values.decodeIfPresent(Bool.self, forKey: .smartFormatting) ?? true
        removeFillers = try values.decodeIfPresent(Bool.self, forKey: .removeFillers) ?? true
        spokenCommands = try values.decodeIfPresent(Bool.self, forKey: .spokenCommands) ?? true
        dictionary = try values.decodeIfPresent([DictationReplacement].self, forKey: .dictionary) ?? Self.defaultDictionary
        snippets = try values.decodeIfPresent([DictationReplacement].self, forKey: .snippets) ?? []
    }

    /// Only validated Whisper language codes reach the subprocess. Locale variants
    /// such as de-AT and en_US are reduced to their base language.
    var recognitionLanguage: String {
        let value = language.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased().replacingOccurrences(of: "_", with: "-")
            .split(separator: "-").first.map(String.init) ?? "auto"
        let supported: Set<String> = [
            "auto", "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr",
            "pl", "ca", "nl", "ar", "sv", "it", "id", "hi", "fi", "vi", "he",
            "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur",
            "hr", "bg", "lt", "la", "mi", "ml", "cy", "sk", "te", "fa", "lv",
            "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy",
            "ne", "mn", "bs", "kk", "sq", "sw", "gl", "mr", "pa", "si", "km",
            "sn", "yo", "so", "af", "oc", "ka", "be", "tg", "sd", "gu", "am",
            "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn", "mt", "sa", "lb",
            "my", "bo", "tl", "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw",
            "su", "yue"
        ]
        return supported.contains(value) ? value : "auto"
    }

    /// A short vocabulary hint, never the expanded contents of personal snippets.
    /// Control characters, duplicate terms and oversized entries are excluded.
    func recognitionPrompt(maximumCharacters: Int = 800) -> String {
        let limit = min(2_000, max(0, maximumCharacters))
        guard limit > 0 else { return "" }
        var terms: [String] = []
        var seen: Set<String> = []
        func append(_ raw: String, budget: Int) {
            guard !raw.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { return }
            let term = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !term.isEmpty, term.count <= 80 else { return }
            let key = term.precomposedStringWithCanonicalMapping.lowercased()
            guard !seen.contains(key) else { return }
            let candidate = (terms + [term]).joined(separator: ", ")
            if candidate.count <= budget {
                terms.append(term)
                seen.insert(key)
            }
        }
        for term in ["ORhom", "Codex", "whisper.cpp"] { append(term, budget: limit) }
        if spokenCommands {
            // Exact phrases materially help whisper.cpp distinguish a spoken
            // command from ordinary words. These three are the primary UI cues.
            for term in ["Sprachbefehl Komma", "Sprachbefehl neuer Absatz", "Sprachbefehl Punkt"] {
                append(term, budget: limit)
            }
        }
        let personalTerms = dictionary.map(\.replacement) + snippets.map(\.cue)
        // Give personal vocabulary priority without filling the entire prompt
        // before the remaining command grammar can be hinted.
        let personalBudget = spokenCommands ? min(limit, terms.joined(separator: ", ").count + min(300, limit / 2)) : limit
        for term in personalTerms { append(term, budget: personalBudget) }
        if spokenCommands {
            for term in [
                "Sprachbefehl neue Zeile", "Sprachbefehl letzten Satz löschen",
                "Sprachbefehl ersetze", "Sprachbefehl Korrektur Ende",
                "ORhom comma", "ORhom period", "ORhom new paragraph", "ORhom new line",
                "ORhom delete last sentence", "ORhom replace", "ORhom end correction",
                "Sprachbefehl letztes Wort löschen", "Sprachbefehl Listenpunkt",
                "Sprachbefehl Nummer eins", "Sprachbefehl Fragezeichen",
                "Sprachbefehl Ausrufezeichen", "Sprachbefehl Doppelpunkt", "Sprachbefehl Semikolon"
            ] {
                append(term, budget: limit)
            }
        }
        for term in personalTerms { append(term, budget: limit) }
        return terms.joined(separator: ", ")
    }

    static func load(from defaults: UserDefaults = .standard) -> DictationPreferences {
        guard let data = defaults.data(forKey: defaultsKey), data.count <= 1_048_576,
              let value = try? JSONDecoder().decode(Self.self, from: data) else { return Self() }
        return value.validated()
    }

    @discardableResult
    func save(to defaults: UserDefaults = .standard) -> Bool {
        guard let data = try? JSONEncoder().encode(validated()), data.count <= 1_048_576 else { return false }
        defaults.set(data, forKey: Self.defaultsKey)
        return true
    }

    func validated() -> Self {
        var result = self
        result.language = recognitionLanguage
        result.dictionary = Self.validEntries(dictionary, replacementLimit: 200)
        result.snippets = Self.validEntries(snippets, replacementLimit: 12_000)
        return result
    }

    private static func validEntries(_ entries: [DictationReplacement], replacementLimit: Int) -> [DictationReplacement] {
        var seen: Set<String> = []
        return entries.prefix(500).compactMap { entry in
            let cue = entry.cue.trimmingCharacters(in: .whitespacesAndNewlines)
                .precomposedStringWithCanonicalMapping
            guard !cue.isEmpty, cue.count <= 120, !entry.replacement.isEmpty,
                  entry.replacement.count <= replacementLimit,
                  !cue.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { return nil }
            guard seen.insert(cue.lowercased()).inserted else { return nil }
            return DictationReplacement(id: entry.id, cue: cue, replacement: entry.replacement)
        }
    }
}

enum DictationTextProcessor {
    private static let wordCharacters = #"\p{L}\p{M}\p{N}_"#
    private static let prefix = #"(?:ORhom|OR[ \t]+hom|Sprachbefehl|Diktat)[ \t]*(?:[,.:;][ \t]*)?[ \t]+"#

    private struct ProtectedText {
        var values: [String: String] = [:]
        private var used: Set<UInt32>
        private var next: UInt32 = 0xF0000

        init(source: String) { used = Set(source.unicodeScalars.map(\.value)) }

        mutating func marker(for value: String) -> String {
            while used.contains(next) { next += 1 }
            // Inputs contain at most 500 validated cues per stage, far fewer than
            // the private-use plane. Markers cannot collide with dictated text.
            let marker = String(UnicodeScalar(next)!)
            used.insert(next)
            next += 1
            values[marker] = value
            return marker
        }

        func restored(_ text: String) -> String {
            // One pass: a literal containing another marker is never expanded.
            text.unicodeScalars.map { values[String($0)] ?? String($0) }.joined()
        }
    }

    private enum Command {
        case punctuation(String)
        case lineBreak(String)
        case deleteSentence
        case deleteWord
        case correction(String, String)
    }

    private struct CommandMatch {
        var range: NSRange
        var command: Command
    }

    static func process(_ raw: String, preferences: DictationPreferences) -> String {
        let settings = preferences.validated()
        var protected = ProtectedText(source: raw)
        var text = raw.precomposedStringWithCanonicalMapping
        // Snippet cues have priority, including cues that contain command words.
        text = replaceCues(in: text, entries: settings.snippets, protected: &protected)
        if settings.spokenCommands { text = applyCommands(text) }
        text = replaceCues(in: text, entries: settings.dictionary, protected: &protected)
        let code = looksLikeCode(text)
        if !code && (settings.removeFillers || settings.smartFormatting) {
            text = protectingStructuredTokens(text, protected: &protected)
        }
        if settings.removeFillers && !code { text = removingFillers(text, language: settings.recognitionLanguage) }
        if settings.smartFormatting && !code {
            text = formatted(text, protected: protected)
        }
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return protected.restored(text)
    }

    private static func expression(_ pattern: String) -> NSRegularExpression? {
        try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
    }

    private static func looksLikeCode(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.first == "{" || trimmed.first == "[" || text.contains("`") { return true }
        guard let regex = expression(#"(?:^|\n)[ \t]*(?:let|var|const|func|class|import|def|return)[ \t]+|(?:=>|==|:=)|[\p{L}_][\p{L}\p{N}_]*[ \t]*=[ \t]*\S"#) else { return false }
        return regex.firstMatch(in: text, range: NSRange(location: 0, length: (text as NSString).length)) != nil
    }

    private static func protectingStructuredTokens(_ text: String, protected: inout ProtectedText) -> String {
        // Explicit technical tokens retain their case and embedded punctuation.
        // This is a lexical safeguard, not an attempt to rewrite source code.
        let pattern = #"(?i:(?:https?://|www\.)[^\s]+|[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}|v[0-9]+(?:\.[0-9]+)+)|[\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*|\[[0-9]+\])+|[\p{Ll}][\p{L}\p{N}_]*[\p{Lu}][\p{L}\p{N}_]*|[\p{L}][\p{L}\p{N}]*_[\p{L}\p{N}_]+|(?:/|~/)[\p{L}\p{N}_.\-/]+"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return text }
        let source = text as NSString
        var result = text
        for match in regex.matches(in: text, range: NSRange(location: 0, length: source.length)).reversed() {
            result = (result as NSString).replacingCharacters(in: match.range, with: protected.marker(for: source.substring(with: match.range)))
        }
        return result
    }

    private static func wordPattern(_ cue: String) -> String {
        let phrase = cue.components(separatedBy: .whitespacesAndNewlines)
            .filter { !$0.isEmpty }.map(NSRegularExpression.escapedPattern(for:))
            .joined(separator: #"[\p{Z}\t]+"#)
        return "(?<![\(wordCharacters)])(?:\(phrase))(?![\(wordCharacters)])"
    }

    private static func replaceCues(in text: String, entries: [DictationReplacement], protected: inout ProtectedText) -> String {
        struct Match { let range: NSRange; let replacement: String; let order: Int }
        let source = text as NSString
        var matches: [Match] = []
        for (index, entry) in entries.enumerated() {
            guard let regex = expression(wordPattern(entry.cue)) else { continue }
            matches += regex.matches(in: text, range: NSRange(location: 0, length: source.length)).map {
                Match(range: $0.range, replacement: entry.replacement, order: index)
            }
        }
        matches.sort {
            if $0.range.location != $1.range.location { return $0.range.location < $1.range.location }
            if $0.range.length != $1.range.length { return $0.range.length > $1.range.length }
            return $0.order < $1.order
        }
        var result = ""
        var position = 0
        for match in matches where match.range.location >= position {
            result += source.substring(with: NSRange(location: position, length: match.range.location - position))
            result += protected.marker(for: match.replacement)
            position = NSMaxRange(match.range)
        }
        result += source.substring(from: position)
        return result
    }

    private static func applyCommands(_ text: String) -> String {
        let source = text as NSString
        let range = NSRange(location: 0, length: source.length)
        var commands: [CommandMatch] = []
        var patterns: [(String, Command)] = [
            (#"(?:komma|comma)"#, .punctuation(",")),
            (#"(?:punkt|period|full stop)"#, .punctuation(".")),
            (#"(?:fragezeichen|question mark)"#, .punctuation("?")),
            (#"(?:ausrufezeichen|exclamation mark|exclamation point)"#, .punctuation("!")),
            (#"(?:doppelpunkt|colon)"#, .punctuation(":")),
            (#"(?:semikolon|semicolon)"#, .punctuation(";")),
            (#"(?:neue zeile|neuer zeile|new line|newline)"#, .lineBreak("\n")),
            (#"(?:neuer absatz|neuen absatz|neue absatz|new paragraph)"#, .lineBreak("\n\n")),
            (#"(?:listenpunkt|bullet point)"#, .lineBreak("\n- ")),
            (#"(?:letzten satz löschen|lösche (?:den )?letzten satz|delete (?:the )?last sentence)"#, .deleteSentence),
            (#"(?:letztes wort löschen|lösche (?:das )?letzte wort|delete (?:the )?last word)"#, .deleteWord)
        ]
        for (index, words) in [
            "eins|one", "zwei|two", "drei|three", "vier|four", "fünf|five",
            "sechs|six", "sieben|seven", "acht|eight", "neun|nine", "zehn|ten"
        ].enumerated() {
            patterns.append(("(?:nummer|number)[ \\t]+(?:\(words)|\(index + 1))", .lineBreak("\n\(index + 1). ")))
        }
        for (phrase, command) in patterns {
            let pattern = "(?<![\(wordCharacters)])\(prefix)\(phrase)(?![\(wordCharacters)])[.,!?;:]?[ \\t]*"
            guard let regex = expression(pattern) else { continue }
            commands += regex.matches(in: text, range: range).map { CommandMatch(range: $0.range, command: command) }
        }
        for (phrase, value) in [
            (#"(?:neue zeile|new line|newline)"#, "\n"),
            (#"(?:neuer absatz|new paragraph)"#, "\n\n")
        ] {
            guard let regex = expression("(?<![\(wordCharacters)])\(phrase)(?![\(wordCharacters)])[.,!?;:]?[ \\t]*") else { continue }
            commands += regex.matches(in: text, range: range).map { CommandMatch(range: $0.range, command: .lineBreak(value)) }
        }
        // A required spoken terminator keeps subsequent dictation outside the
        // correction. Missing targets leave the complete command readable.
        let correctionPattern = "(?<![\(wordCharacters)])\(prefix)(?:ersetze|replace)[ \\t]+(.{1,120}?)[ \\t]+(?:durch|with)[ \\t]+(.{1,200}?)[ \\t]+\(prefix)(?:korrektur ende|end correction)(?![\(wordCharacters)])[.,!?;:]?"
        if let regex = expression(correctionPattern) {
            commands += regex.matches(in: text, range: range).map {
                CommandMatch(range: $0.range, command: .correction(source.substring(with: $0.range(at: 1)), source.substring(with: $0.range(at: 2))))
            }
        }
        // Unprefixed German self-correction is accepted only as a complete,
        // separately punctuated sentence, never inside ordinary prose.
        let germanPattern = #"(?:^|(?<=[.!?\n]))[ \t]*korrigiere[ \t]+([^.!?\n]{1,120}?)[ \t]+zu[ \t]+([^.!?\n]{1,200}?)[.!?](?=[ \t\n]|$)"#
        if let regex = expression(germanPattern) {
            commands += regex.matches(in: text, range: range).map {
                CommandMatch(range: $0.range, command: .correction(source.substring(with: $0.range(at: 1)), source.substring(with: $0.range(at: 2))))
            }
        }
        commands.sort {
            if $0.range.location != $1.range.location { return $0.range.location < $1.range.location }
            return $0.range.length > $1.range.length
        }
        var result = ""
        var position = 0
        for match in commands where match.range.location >= position {
            result += source.substring(with: NSRange(location: position, length: match.range.location - position))
            switch match.command {
            case .punctuation(let mark):
                // Whisper can supply punctuation before the spoken prefix as
                // well as after the command. The explicit mark supersedes it.
                result = removingTrailingPunctuation(result, marks: ",.!?;:") + mark + " "
            case .lineBreak(let newline):
                // A comma before a structural command is commonly ASR's pause
                // punctuation. Keep real sentence endings and list colons.
                result = removingTrailingPunctuation(result, marks: ",") + newline
            case .deleteSentence:
                result = deletingLastSentence(result)
            case .deleteWord:
                result = deletingLastWord(result)
            case .correction(let old, let new):
                let previous = result as NSString
                if let regex = expression(wordPattern(old.trimmingCharacters(in: .whitespacesAndNewlines))),
                   let last = regex.matches(in: result, range: NSRange(location: 0, length: previous.length)).last {
                    result = previous.replacingCharacters(in: last.range, with: new.trimmingCharacters(in: .whitespacesAndNewlines))
                    if let lastCharacter = result.last, !lastCharacter.isWhitespace { result += " " }
                } else {
                    result += source.substring(with: match.range)
                }
            }
            position = NSMaxRange(match.range)
        }
        result += source.substring(from: position)
        return result
    }

    private static func trimmingHorizontalEnd(_ text: String) -> String {
        var result = text
        while let last = result.last, last == " " || last == "\t" { result.removeLast() }
        return result
    }

    private static func removingTrailingPunctuation(_ text: String, marks: String) -> String {
        var result = trimmingHorizontalEnd(text)
        while let last = result.last, marks.contains(last) {
            result.removeLast()
            result = trimmingHorizontalEnd(result)
        }
        return result
    }

    private static func deletingLastSentence(_ text: String) -> String {
        var content = text.trimmingCharacters(in: .whitespacesAndNewlines)
        while let last = content.last, ".!?".contains(last) { content.removeLast() }
        // Only real sentence endings are separators: dots inside decimals,
        // e-mail addresses and code identifiers do not split a sentence.
        if let regex = expression(#"[.!?](?=[\s]|$)|\n"#),
           let last = regex.matches(in: content, range: NSRange(location: 0, length: (content as NSString).length)).last {
            return (content as NSString).substring(to: NSMaxRange(last.range)).trimmingCharacters(in: .whitespacesAndNewlines) + " "
        }
        return ""
    }

    private static func deletingLastWord(_ text: String) -> String {
        guard let regex = expression(#"(?:[\p{L}\p{M}\p{N}_]+(?:['’\-][\p{L}\p{M}\p{N}_]+)*|[\x{F0000}-\x{FFFFD}])[.,!?;:]*[\s]*$"#) else { return text }
        let source = text as NSString
        guard let last = regex.firstMatch(in: text, range: NSRange(location: 0, length: source.length)) else { return text }
        return source.substring(to: last.range.location)
    }

    private static func removingFillers(_ text: String, language: String) -> String {
        let fillers: String
        switch language {
        case "de": fillers = "ähm|äh"
        case "en": fillers = "uh|um"
        case "auto": fillers = "ähm|äh|uh"
        default: return text
        }
        guard let regex = expression("(?<![\(wordCharacters)])(\(fillers))(?![\(wordCharacters)])(?:,)?[ \\t]*") else { return text }
        var result = text as NSString
        for match in regex.matches(in: text, range: NSRange(location: 0, length: result.length)).reversed() {
            let word = result.substring(with: match.range(at: 1))
            if word.count > 1 && word == word.uppercased() { continue }
            result = result.replacingCharacters(in: match.range, with: "") as NSString
        }
        return result as String
    }

    private static func formatted(_ text: String, protected: ProtectedText) -> String {
        var value = text
        for (pattern, replacement) in [
            (#"[\p{Z}\t ]+"#, " "),
            (#" +([,.!?;:])"#, "$1"),
            (#" *\n *"#, "\n")
        ] {
            if let regex = expression(pattern) {
                value = regex.stringByReplacingMatches(in: value, range: NSRange(location: 0, length: (value as NSString).length), withTemplate: replacement)
            }
        }
        var result = ""
        var sentenceStart = true
        let characters = Array(value)
        for (index, character) in characters.enumerated() {
            if let literal = protected.values[String(character)] {
                result.append(character)
                if let ending = literal.last(where: { !$0.isWhitespace }) { sentenceStart = ".!?".contains(ending) }
            } else if character == "\n" || (
                ".!?".contains(character) &&
                (index + 1 == characters.count || characters[index + 1].isWhitespace)
            ) {
                result.append(character)
                sentenceStart = true
            } else if character.isLetter {
                result += sentenceStart ? String(character).uppercased() : String(character)
                sentenceStart = false
            } else {
                result.append(character)
                if character.isNumber { sentenceStart = false }
            }
        }
        return result
    }
}
