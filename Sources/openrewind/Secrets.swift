import Foundation

/// Finds secrets in screen text before it is saved: API keys and tokens, private keys, card numbers and
/// whatever follows a "password:" label. Matching text becomes [redacted] and the words are blurred in the
/// screenshot (see Recorder). The Windows app has the same rules in Secrets.cs, with unit tests; keep the
/// two in step. Kept strict on purpose: ordinary words, dates, phone numbers and short numbers never match.
enum Secrets {
    static let mask = "[redacted]"

    /// On unless turned off in Settings → Privacy.
    static var enabled: Bool {
        get { UserDefaults.standard.object(forKey: "redactSecrets") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "redactSecrets") }
    }

    private static func re(_ pattern: String) -> NSRegularExpression {
        // The patterns are fixed, so a bad one is a bug caught on the first run.
        try! NSRegularExpression(pattern: pattern)
    }

    /// Rules whose whole match is the secret.
    private static let whole: [NSRegularExpression] = [
        re(#"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----[\s\S]*?(-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----|\z)"#),
        re(#"(?<![A-Za-z0-9])eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}"#),
    ]
    /// Prefixed keys, which OCR may split in two at a space (see extend).
    private static let prefixed: [NSRegularExpression] = [
        re(#"(?<![A-Za-z0-9])sk-[A-Za-z0-9_\-]{20,}"#),
        re(#"(?<![A-Za-z0-9])(sk|rk)_(live|test)_[A-Za-z0-9]{16,}"#),
        re(#"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{30,}"#),
        re(#"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{40,}"#),
        re(#"(?<![A-Za-z0-9])xox[abposre]-[A-Za-z0-9\-]{10,}"#),
        re(#"(?<![A-Za-z0-9])(AKIA|ASIA)[0-9A-Z]{16}(?![A-Za-z0-9])"#),
        re(#"(?<![A-Za-z0-9])AIza[0-9A-Za-z_\-]{35}(?![A-Za-z0-9_\-])"#),
        re(#"(?<![A-Za-z0-9])whsec_[A-Za-z0-9+/=]{20,}"#),            // Stripe, Svix
        re(#"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_\-]{20,}"#),
        re(#"(?<![A-Za-z0-9])npm_[A-Za-z0-9]{36,}"#),
        re(#"(?<![A-Za-z0-9])hf_[A-Za-z0-9]{30,}"#),
        re(#"(?<![A-Za-z0-9])pypi-AgE[A-Za-z0-9_\-]{20,}"#),
        re(#"(?<![A-Za-z0-9])SG\.[A-Za-z0-9_\-]{16,32}\.[A-Za-z0-9_\-]{16,64}"#),
        re(#"(?<![A-Za-z0-9])shp(at|ss|ca|pa)_[a-fA-F0-9]{32}"#),
        re(#"(?<![A-Za-z0-9])xapp-[A-Za-z0-9\-]{10,}"#),
        re(#"(?<![A-Za-z0-9])do[opr]_v1_[a-fA-F0-9]{32,}"#),
        re(#"(?<![A-Za-z0-9])ya29\.[A-Za-z0-9_\-]{20,}"#),
        re(#"(?<![A-Za-z0-9])EAA[A-Za-z0-9]{40,}"#),
    ]

    /// Names of secrets, for a value with no ':' or '=' after them ("Signing secret  whsec_…", or the value
    /// on the next line) and for long hex values near them. Longer names first.
    private static let names = #"signing[ _\-]?secret|webhook[ _\-]?secret|signing[ _\-]?key|shared[ _\-]?secret|app[ _\-]?secret"#
        + #"|consumer[ _\-]?secret|encryption[ _\-]?key|client[ _\-]?secret|secret[ _\-]?access[ _\-]?key|secret[ _\-]?key"#
        + #"|auth[ _\-]?token|api[ _\-]?key|access[ _\-]?token|refresh[ _\-]?token|private[ _\-]?key|password|token|secret"#
    /// A name, then the value after spaces or on the next line (one line at most), maybe past a Reveal or
    /// Copy button. The value counts only when it looks like a key (looksLikeKey).
    private static let near = re(#"(?i)(?<![A-Za-z])(?:"# + names
        + #")(?![A-Za-z])(?<sep>[ \t]*[:=]?(?:[ \t]+(?:reveal|show|copy|hide|view)(?![A-Za-z]))?(?:[ \t]*\r?\n)?[ \t]*)["']?(?<v>[A-Za-z0-9_+/=\-]{6,})"#)
    /// Hex values (32 to 128 digits, maybe split in two by OCR) are secrets only near a name, so commit
    /// hashes and checksums on their own stay. Twilio keys: SK + 32 hex digits.
    private static let hexRun = re(#"(?<![A-Za-z0-9_\-])(?:SK)?[0-9a-fA-F]{8,130}(?: [0-9a-fA-F]{8,130})?(?![A-Za-z0-9_\-])"#)
    private static let hexName = re(#"(?i)(?<![A-Za-z])(?:"# + names + #"|account[ _\-]?sid|twilio)(?![A-Za-z])"#)
    private static let keyChars = re(#"^[A-Za-z0-9_+/=\-]+$"#)

    /// Rules where only the `v` group is the secret: what follows a label.
    private static let labeled = re(
        #"(?i)(?<![A-Za-z])(password|passwd|passcode|pwd|api[ _\-]?key|signing[ _\-]?secret|webhook[ _\-]?secret|signing[ _\-]?key|shared[ _\-]?secret|app[ _\-]?secret|consumer[ _\-]?secret|encryption[ _\-]?key|secret[ _\-]?key|(?:aws[ _\-]?)?secret[ _\-]?access[ _\-]?key|access[ _\-]?key|account[ _\-]?key|private[ _\-]?key|client[ _\-]?secret|access[ _\-]?token|auth[ _\-]?token|refresh[ _\-]?token|bearer)[ \t]*[:=][ \t]*["']?(?<v>[^\s"';]{4,})"#)
    /// Environment variables and settings named like a secret: FOO_SECRET=…, GITHUB_TOKEN=…, STRIPE_KEY=….
    private static let envVar = re(
        #"(?<![A-Za-z0-9_])[A-Z][A-Z0-9_]*_(SECRET|TOKEN|KEY|PASSWORD|PASS|PWD|APIKEY|CREDENTIALS?)[ \t]*[:=][ \t]*["']?(?<v>[^\s"';]{8,})"#)
    /// A password inside an address in text: postgres://user:pass@host, https://me:pw@example.com.
    private static let textUserinfo = re(#"(?<![A-Za-z0-9+.\-])[a-zA-Z][a-zA-Z0-9+.\-]*://[^/@:\s]+:(?<v>[^/@\s]{3,})@"#)
    private static let plainWord = re(#"^[a-z_.]+$"#)
    private static let pathWord = re(#"^[A-Z]?[a-z]{3,}$"#)
    private static let bearer = re(#"(?<![A-Za-z])Bearer[ \t]+(?<v>[A-Za-z0-9._~+/\-]{20,}=*)"#)
    /// 13 to 19 digits, maybe in groups split by single spaces or dashes.
    private static let digits = re(#"(?<![\d\-.,/:])\d(?:[ \-]?\d){12,18}(?![\d\-]|[.,/:]\d)"#)
    /// Long runs of letters, digits and token punctuation, checked for randomness.
    /// '/' may be inside (base64: AWS and Azure keys) but not first; isRandom keeps paths out.
    private static let token = re(#"(?<![A-Za-z0-9+_\-/.])[A-Za-z0-9+_\-][A-Za-z0-9+/_\-]{31,}={0,2}(?![A-Za-z0-9+/_=\-])"#)

    private static func matches(_ r: NSRegularExpression, _ s: String) -> Bool {
        r.firstMatch(in: s, range: NSRange(location: 0, length: (s as NSString).length)) != nil
    }

    /// Every secret in `text` (UTF-16 ranges), in order, without overlaps.
    static func find(_ text: String) -> [NSRange] {
        guard !text.isEmpty else { return [] }
        let ns = text as NSString
        let all = NSRange(location: 0, length: ns.length)
        var hits: [NSRange] = []
        for r in whole { hits += r.matches(in: text, range: all).map(\.range) }
        // OCR may read one key as two words: take the second half too.
        for r in prefixed {
            for m in r.matches(in: text, range: all) {
                let end = extend(ns, m.range.location + m.range.length)
                hits.append(NSRange(location: m.range.location, length: end - m.range.location))
            }
        }
        for m in near.matches(in: text, range: all) {
            let v = m.range(withName: "v")
            // "token1234…" is one word.
            guard v.location != NSNotFound, m.range(withName: "sep").length > 0 else { continue }
            let end = extend(ns, v.location + v.length)
            let r = NSRange(location: v.location, length: end - v.location)
            if looksLikeKey(ns.substring(with: r)) { hits.append(r) }
        }
        for m in hexRun.matches(in: text, range: all) {
            var hex = ns.substring(with: m.range).replacingOccurrences(of: " ", with: "")
            if hex.hasPrefix("SK") { hex.removeFirst(2) }
            let digits = hex.filter { $0.isASCII && $0.isNumber }.count
            guard (32...130).contains(hex.count), digits >= 2, hex.count - digits >= 2 else { continue }
            if nearName(ns, m.range.location) { hits.append(m.range) }
        }
        for r in [labeled, bearer, envVar, textUserinfo] {
            for m in r.matches(in: text, range: all) {
                let v = m.range(withName: "v")
                guard v.location != NSNotFound, looksLikeValue(ns.substring(with: v)) else { continue }
                // SORT_KEY=timestamp: a plain word is a setting, not a secret.
                if r === envVar && matches(plainWord, ns.substring(with: v)) { continue }
                hits.append(v)
            }
        }
        for m in digits.matches(in: text, range: all) where isCardNumber(ns.substring(with: m.range)) { hits.append(m.range) }
        for m in token.matches(in: text, range: all) where isRandom(ns.substring(with: m.range)) { hits.append(m.range) }
        return merge(hits)
    }

    /// A secret's name on the same line before `at` (UTF-16), or on the line above.
    private static func nearName(_ ns: NSString, _ at: Int) -> Bool {
        let nl = UInt16(UInt8(ascii: "\n"))
        var line = at - 1
        while line >= 0, ns.character(at: line) != nl { line -= 1 }
        var from = 0
        if line > 0 {
            from = line - 1
            while from >= 0, ns.character(at: from) != nl { from -= 1 }
            from += 1
        }
        return hexName.firstMatch(in: ns as String, range: NSRange(location: from, length: at - from)) != nil
    }

    /// Where a key ends when OCR split it in two at a space: the next word joins it when it is made of key
    /// characters and has letters and digits (so "Copy", a date or a sentence do not).
    private static func extend(_ ns: NSString, _ end: Int) -> Int {
        guard end + 1 < ns.length, ns.character(at: end) == 32 else { return end }
        var e = end + 1
        let ws = CharacterSet.whitespacesAndNewlines
        while e < ns.length, let u = Unicode.Scalar(ns.character(at: e)), !ws.contains(u) { e += 1 }
        let w = ns.substring(with: NSRange(location: end + 1, length: e - end - 1))
        guard w.count >= 4, matches(keyChars, w), w.contains(where: { $0.isASCII && $0.isNumber }),
              w.contains(where: { $0.isASCII && $0.isLetter }) else { return end }
        return e
    }

    /// A value after a secret's name that is a key, not a word, a button or a masked placeholder: 16 or more
    /// key characters (one space allowed, from OCR), mostly letters and digits with at least two of each,
    /// and not a path or words joined by dashes.
    static func looksLikeKey(_ v: String) -> Bool {
        let s = v.replacingOccurrences(of: " ", with: "")
        guard (16...512).contains(s.count), matches(keyChars, s) else { return false }
        let letters = s.filter { $0.isASCII && $0.isLetter }.count
        let digits = s.filter { $0.isASCII && $0.isNumber }.count
        guard letters >= 2, digits >= 2, Double(letters + digits) >= Double(s.count) * 0.8 else { return false }
        let parts = s.split(whereSeparator: { $0 == "-" || $0 == "_" })
        if parts.count > 3, parts.allSatisfy({ $0.count < 12 }) { return false }
        if s.hasPrefix("/") || s.contains("//") || (s.contains("/") && s.split(separator: "/").contains(where: { matches(pathWord, String($0)) })) { return false }
        return true
    }

    static func has(_ text: String) -> Bool { !find(text).isEmpty }

    /// Known key prefixes as OCR may read them, with the "_" or "-" after them lost or turned into a space.
    private static let stem = re(#"^(?:whsec|sk|rk|gh[pousr]|github|xox[abposre]|glpat|npm|hf|pypi|shp(?:at|ss|ca|pa)|xapp|do[opr]|ya29|AKIA|ASIA|AIza)$"#)
    private static let nameOnly = re(#"(?i)(?<![A-Za-z])(?:"# + names + #")(?![A-Za-z])"#)
    private static let runWord = re(#"[A-Za-z0-9_+/=.\-]+"#)
    /// A known prefix or a secret's name somewhere in rough OCR text: worth a careful read before saving.
    private static let hint = re(#"(?<![A-Za-z0-9])(?:whsec|sk[-_ ]|rk[-_ ]|gh[pousr][-_ ]|github[-_ ]pat|xox[abposre]|glpat|npm[-_ ]|hf[-_ ]|pypi|shp(?:at|ss|ca|pa)|xapp|do[opr][-_ ]v1|ya29|AKIA|ASIA|AIza|eyJ)"#)

    /// True when `text` names a secret or has a key prefix, even one OCR broke up: such a screen is read
    /// carefully, not quickly, before looking for what to blur.
    static func mightHave(_ text: String) -> Bool {
        !text.isEmpty && (matches(hint, text) || matches(nameOnly, text) || has(text))
    }

    /// Secrets in OCR lines that find misses because OCR put spaces inside a key ("whsec K70w 0aQn…"): a run
    /// of words with one space between them that starts with a known prefix, or that follows a secret's name
    /// (on the same line or the line above) from its first word with letters and digits, and that looks like
    /// a key once the spaces are taken out. Ranges are within each line (UTF-16). Same as Secrets.cs.
    static func findInLines(_ lines: [String]) -> [(line: Int, range: NSRange)] {
        var out: [(line: Int, range: NSRange)] = []
        for (i, line) in lines.enumerated() {
            let ns = line as NSString
            let words = runWord.matches(in: line, range: NSRange(location: 0, length: ns.length)).map(\.range)
            guard words.count >= 2 else { continue }
            let named = matches(nameOnly, line) || (i > 0 && matches(nameOnly, lines[i - 1]))
            var w = 0
            while w < words.count {
                var end = w
                while end + 1 < words.count, words[end + 1].location == words[end].location + words[end].length + 1 { end += 1 }
                if let r = loose(ns, Array(words[w...end]), named: named) { out.append((i, r)) }
                w = end + 1
            }
        }
        return out
    }

    /// Where a key is inside one run of words, or nil.
    private static func loose(_ ns: NSString, _ words: [NSRange], named: Bool) -> NSRange? {
        let text = words.map { ns.substring(with: $0) }
        var from = text.firstIndex(where: { matches(stem, head($0)) })
        if from == nil, named { from = text.firstIndex(where: isMixed) }
        guard let f = from, words.count - f >= 2 else { return nil }
        let part = Array(text[f...])
        let h = head(part[0])
        let keyish: Bool
        if matches(stem, h) {
            let rest = (part[0].count > h.count ? String(part[0].dropFirst(h.count + 1)) : "") + part.dropFirst().joined()
            keyish = rest.count >= 16 && looksLikeKey(rest)
        } else {
            keyish = looksLikeKey(part.joined())
        }
        guard keyish, part.contains(where: { $0.count >= 4 && isMixed($0) }) else { return nil }
        let a = words[f].location, last = words[words.count - 1]
        return NSRange(location: a, length: last.location + last.length - a)
    }

    /// A word up to its first "_", "-" or ".".
    private static func head(_ w: String) -> String {
        String(w.prefix(while: { $0 != "_" && $0 != "-" && $0 != "." }))
    }

    private static func isMixed(_ s: String) -> Bool {
        s.contains(where: { $0.isASCII && $0.isNumber }) && s.contains(where: { $0.isASCII && $0.isLetter })
    }

    /// Where each of `secrets` (found in text read another way, such as Accessibility) shows in the OCR
    /// `lines`, even when OCR misread it a little: a stretch of 10 characters of the secret in the line,
    /// ignoring spaces, case-sensitive but with O/0 and l/1/I taken as the same. Ranges are within each line (UTF-16).
    static func locate(_ secrets: [String], in lines: [String]) -> [(line: Int, range: NSRange)] {
        let chunk = 10
        let keys = Array(Set(secrets.map(fold))).filter { $0.count >= chunk }.map { Array($0.utf16) }
        var out: [(line: Int, range: NSRange)] = []
        guard !keys.isEmpty else { return out }
        for (i, line) in lines.enumerated() {
            var folded: [UInt16] = []
            var at: [Int] = []
            for (c, u) in line.utf16.enumerated() where !isSpace(u) {
                folded.append(foldUnit(u)); at.append(c)
            }
            var lo = Int.max, hi = -1
            for k in keys {
                var s = 0
                while s + chunk <= k.count {
                    let piece = Array(k[s..<(s + chunk)])
                    var f = 0
                    while f + chunk <= folded.count {
                        if Array(folded[f..<(f + chunk)]) == piece {
                            lo = min(lo, at[f]); hi = max(hi, at[f + chunk - 1] + 1)
                        }
                        f += 1
                    }
                    s += chunk / 2
                }
            }
            if hi > lo { out.append((i, NSRange(location: lo, length: hi - lo))) }
        }
        return out
    }

    /// The secrets themselves (not where they are) in `text`.
    static func values(_ text: String) -> [String] {
        let ns = text as NSString
        return find(text).map { ns.substring(with: $0) }
    }

    private static func isSpace(_ u: UInt16) -> Bool { u == 32 || u == 9 || u == 10 || u == 13 || u == 0xA0 }

    private static func fold(_ s: String) -> String {
        String(decoding: s.utf16.filter { !isSpace($0) }.map(foldUnit), as: UTF16.self)
    }

    /// O and o as 0, l, I and | as 1.
    private static func foldUnit(_ u: UInt16) -> UInt16 {
        switch u {
        case 79, 111: return 48
        case 108, 73, 124: return 49
        default: return u
        }
    }

    /// `text` with every secret replaced by [redacted].
    static func redact(_ text: String) -> String {
        let hits = find(text)
        guard !hits.isEmpty else { return text }
        let ns = text as NSString
        var out = ""
        var at = 0
        for h in hits {
            out += ns.substring(with: NSRange(location: at, length: h.location - at)) + mask
            at = h.location + h.length
        }
        return out + ns.substring(from: at)
    }

    /// Query parameters that carry secrets; their values are always hidden.
    private static let secretParams: Set<String> = [
        "token", "access_token", "refresh_token", "id_token", "auth", "auth_token", "api_key", "apikey", "key",
        "secret", "client_secret", "password", "pass", "pwd", "sig", "signature", "code", "session", "sessionid",
        "sid", "x-amz-signature", "x-amz-credential", "x-amz-security-token", "otp",
    ]
    private static let userinfo = re(#"^([a-zA-Z][a-zA-Z0-9+.\-]*://[^/@:\s]+):[^/@\s]+@"#)
    private static let param = re(#"([?&#])([^=&#\s]+)=([^&#\s]*)"#)

    /// A web address with secrets taken out: a password before the host, the values of secret query
    /// parameters (token=, key=, sig=…) and anything the text rules find.
    static func redactURL(_ url: String) -> String {
        guard !url.isEmpty else { return url }
        var s = userinfo.stringByReplacingMatches(in: url, range: NSRange(location: 0, length: (url as NSString).length),
                                                  withTemplate: "$1:" + NSRegularExpression.escapedTemplate(for: mask) + "@")
        let ns = s as NSString
        var out = ""
        var at = 0
        for m in param.matches(in: s, range: NSRange(location: 0, length: ns.length)) {
            let name = ns.substring(with: m.range(at: 2))
            guard m.range(at: 3).length > 0, secretParams.contains((name.removingPercentEncoding ?? name).lowercased()) else { continue }
            out += ns.substring(with: NSRange(location: at, length: m.range(at: 3).location - at)) + mask
            at = m.range(at: 3).location + m.range(at: 3).length
        }
        s = out + ns.substring(from: at)
        return redact(s)
    }

    /// Card numbers: 13–19 digits that pass the Luhn check, start like a real card (Visa, Mastercard,
    /// Amex, Discover, JCB, Diners, UnionPay) and are either one run or in groups of four (Amex 4-6-5).
    static func isCardNumber(_ s: String) -> Bool {
        let groups = s.split(whereSeparator: { $0 == " " || $0 == "-" }).map(String.init)
        let d = groups.joined()
        guard (13...19).contains(d.count), d.allSatisfy({ $0.isASCII && $0.isNumber }) else { return false }
        if groups.count > 1 {
            let sizes = groups.map { String($0.count) }.joined(separator: ",")
            let fours = groups.dropLast().allSatisfy { $0.count == 4 } && (1...4).contains(groups.last!.count)
            guard fours || sizes == "4,6,5" || sizes == "4,6,4" else { return false }
        }
        guard Set(d).count >= 2 else { return false }  // 0000…
        let two = String(d.prefix(2))
        guard d.first.map({ "3456".contains($0) }) == true || ["22", "23", "24", "25", "26", "27"].contains(two) else { return false }
        if d.hasPrefix("3"), !["34", "37", "35", "36", "30", "38", "39"].contains(two) { return false }
        return luhn(d)
    }

    static func luhn(_ digits: String) -> Bool {
        var sum = 0
        var double = false
        for c in digits.reversed() {
            guard var n = c.wholeNumberValue else { return false }
            if double { n *= 2; if n > 9 { n -= 9 } }
            sum += n
            double.toggle()
        }
        return sum % 10 == 0
    }

    /// A long string that looks machine-made: at least 32 characters with upper and lower case letters and
    /// digits mixed, and close to random (Shannon entropy). Hex hashes and ids (one case only), file names
    /// and CamelCase words (no digits) never match.
    static func isRandom(_ s: String) -> Bool {
        guard s.count >= 32 else { return false }
        var upper = 0, lower = 0, digit = 0
        for u in s.unicodeScalars {
            switch u.value {
            case 65...90: upper += 1
            case 97...122: lower += 1
            case 48...57: digit += 1
            default: break
            }
        }
        // Base64 with '/' or '+' (AWS and Azure keys) may happen to have fewer digits.
        let base64 = s.count >= 40 && (s.contains("/") || s.contains("+"))
        guard upper >= 2, lower >= 2, digit >= 2 || base64 else { return false }
        // Words joined by dashes or underscores ("my-long-branch-name-2024-v2") are not random.
        let parts = s.split(whereSeparator: { $0 == "-" || $0 == "_" })
        if parts.count > 3, parts.allSatisfy({ $0.count < 12 }) { return false }
        // Paths ("Users/sam/Projects/App2024/Sources"): a word between slashes, or "//".
        if s.contains("/"), s.contains("//") || s.split(separator: "/").contains(where: { matches(pathWord, String($0)) }) { return false }
        return entropy(s) >= 4.2
    }

    /// Bits per character.
    static func entropy(_ s: String) -> Double {
        guard !s.isEmpty else { return 0 }
        var counts: [Character: Int] = [:]
        for c in s { counts[c, default: 0] += 1 }
        let n = Double(s.count)
        return counts.values.reduce(0) { e, k in
            let p = Double(k) / n
            return e - p * log2(p)
        }
    }

    /// What follows "password:" is a value, not more of the sentence ("Password: required", "pwd = ****").
    private static func looksLikeValue(_ v: String) -> Bool {
        if v.allSatisfy({ "*•●·.".contains($0) }) { return false }
        var lower = v.lowercased()
        while let last = lower.last, ".,;)".contains(last) { lower.removeLast() }
        return ![
            "required", "optional", "hidden", "none", "null", "undefined", "true", "false", "forgot", "reset",
            "change", "show", "enter", "your", "must", "should", "cannot", "needs", "reveal", "copy", "view", "hide",
            "regenerate", "roll", "generate", "create", "revoke", "delete", "manage", "edit", "expired", "invalid",
            "missing", "[redacted]",
        ].contains(lower)
    }

    /// Sorted, with overlapping ranges joined.
    private static func merge(_ hits: [NSRange]) -> [NSRange] {
        let sorted = hits.sorted { $0.location != $1.location ? $0.location < $1.location : $0.length > $1.length }
        var out: [NSRange] = []
        for h in sorted {
            if let last = out.last, h.location < last.location + last.length {
                let end = max(last.location + last.length, h.location + h.length)
                out[out.count - 1] = NSRange(location: last.location, length: end - last.location)
                continue
            }
            out.append(h)
        }
        return out
    }
}
