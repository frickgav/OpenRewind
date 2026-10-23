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
        re(#"(?<![A-Za-z0-9])sk-[A-Za-z0-9_\-]{20,}"#),                     // OpenAI, Anthropic
        re(#"(?<![A-Za-z0-9])(sk|rk)_(live|test)_[A-Za-z0-9]{16,}"#),         // Stripe secret keys
        re(#"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{30,}"#),
        re(#"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{40,}"#),
        re(#"(?<![A-Za-z0-9])xox[abposre]-[A-Za-z0-9\-]{10,}"#),
        re(#"(?<![A-Za-z0-9])(AKIA|ASIA)[0-9A-Z]{16}(?![A-Za-z0-9])"#),
        re(#"(?<![A-Za-z0-9])AIza[0-9A-Za-z_\-]{35}(?![A-Za-z0-9_\-])"#),
        re(#"(?<![A-Za-z0-9])eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}"#),
    ]

    /// Rules where only the `v` group is the secret: what follows a label.
    private static let labeled = re(
        #"(?i)(?<![A-Za-z])(password|passwd|passcode|pwd|api[ _\-]?key|secret[ _\-]?key|client[ _\-]?secret|access[ _\-]?token|auth[ _\-]?token|bearer)[ \t]*[:=][ \t]*["']?(?<v>[^\s"']{4,})"#)
    private static let bearer = re(#"(?<![A-Za-z])Bearer[ \t]+(?<v>[A-Za-z0-9._~+/\-]{20,}=*)"#)
    /// 13 to 19 digits, maybe in groups split by single spaces or dashes.
    private static let digits = re(#"(?<![\d\-.,/:])\d(?:[ \-]?\d){12,18}(?![\d\-]|[.,/:]\d)"#)
    /// Long runs of letters, digits and token punctuation, checked for randomness.
    private static let token = re(#"(?<![A-Za-z0-9+_=\-/.])[A-Za-z0-9+_=\-]{32,}(?![A-Za-z0-9+_=\-/])"#)

    /// Every secret in `text` (UTF-16 ranges), in order, without overlaps.
    static func find(_ text: String) -> [NSRange] {
        guard !text.isEmpty else { return [] }
        let ns = text as NSString
        let all = NSRange(location: 0, length: ns.length)
        var hits: [NSRange] = []
        for r in whole { hits += r.matches(in: text, range: all).map(\.range) }
        for r in [labeled, bearer] {
            for m in r.matches(in: text, range: all) {
                let v = m.range(withName: "v")
                guard v.location != NSNotFound, looksLikeValue(ns.substring(with: v)) else { continue }
                hits.append(v)
            }
        }
        for m in digits.matches(in: text, range: all) where isCardNumber(ns.substring(with: m.range)) { hits.append(m.range) }
        for m in token.matches(in: text, range: all) where isRandom(ns.substring(with: m.range)) { hits.append(m.range) }
        return merge(hits)
    }

    static func has(_ text: String) -> Bool { !find(text).isEmpty }

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
        guard upper >= 2, lower >= 2, digit >= 2 else { return false }
        // Words joined by dashes or underscores ("my-long-branch-name-2024-v2") are not random.
        let parts = s.split(whereSeparator: { $0 == "-" || $0 == "_" })
        if parts.count > 3, parts.allSatisfy({ $0.count < 12 }) { return false }
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
            "change", "show", "enter", "your", "must", "should", "cannot", "needs", "[redacted]",
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
