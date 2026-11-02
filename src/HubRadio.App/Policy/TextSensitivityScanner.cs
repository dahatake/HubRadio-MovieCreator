using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Terms;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Policy;

public enum TextSensitivityFindingKind
{
    EmailAddress,
    InternalNetworkLocation,
    CredentialPattern,
    DictionaryTerm,
    CustomerName,
    UnreleasedProduct,
    PersonalInformation,
    FinancialInformation,
    ConfidentialMaterial,
    FuturePlan,
}

public sealed class TextSensitivityFinding
{
    internal TextSensitivityFinding(
        EntityId<TranscriptRevisionIdTag> revisionId,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<SourceSpanIdTag> sourceSpanId,
        TextSensitivityFindingKind kind,
        int utf16Offset,
        int utf16Length)
    {
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(utteranceId);
        ArgumentNullException.ThrowIfNull(sourceSpanId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "The text-sensitivity finding kind must be defined.");
        }

        if (utf16Offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf16Offset),
                "A text-sensitivity offset must not be negative.");
        }

        if (utf16Length <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf16Length),
                "A text-sensitivity length must be positive.");
        }

        RevisionId = revisionId;
        UtteranceId = utteranceId;
        SourceSpanId = sourceSpanId;
        Kind = kind;
        Utf16Offset = utf16Offset;
        Utf16Length = utf16Length;
    }

    public EntityId<TranscriptRevisionIdTag> RevisionId { get; }

    public EntityId<TranscriptUtteranceIdTag> UtteranceId { get; }

    public EntityId<SourceSpanIdTag> SourceSpanId { get; }

    public TextSensitivityFindingKind Kind { get; }

    public string ReasonCode => Kind switch
    {
        TextSensitivityFindingKind.EmailAddress =>
            "sensitivity.email-address",
        TextSensitivityFindingKind.InternalNetworkLocation =>
            "sensitivity.internal-network-location",
        TextSensitivityFindingKind.CredentialPattern =>
            "sensitivity.credential-pattern",
        TextSensitivityFindingKind.DictionaryTerm =>
            "sensitivity.dictionary-term",
        TextSensitivityFindingKind.CustomerName =>
            "sensitivity.customer-name",
        TextSensitivityFindingKind.UnreleasedProduct =>
            "sensitivity.unreleased-product",
        TextSensitivityFindingKind.PersonalInformation =>
            "sensitivity.personal-information",
        TextSensitivityFindingKind.FinancialInformation =>
            "sensitivity.financial-information",
        TextSensitivityFindingKind.ConfidentialMaterial =>
            "sensitivity.confidential-material",
        TextSensitivityFindingKind.FuturePlan =>
            "sensitivity.future-plan",
        _ => "sensitivity.unknown",
    };

    public int Utf16Offset { get; }

    public int Utf16Length { get; }
}

public sealed class TextSensitivityScanResult
{
    internal TextSensitivityScanResult(
        EntityId<TranscriptRevisionIdTag> revisionId,
        bool isComplete,
        IReadOnlyList<TextSensitivityFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(revisionId);
        ArgumentNullException.ThrowIfNull(findings);

        var copiedFindings = new TextSensitivityFinding[findings.Count];
        for (int index = 0; index < copiedFindings.Length; index++)
        {
            TextSensitivityFinding finding = findings[index]
                ?? throw new ArgumentException(
                    "Text-sensitivity findings must not contain null elements.",
                    nameof(findings));
            if (finding.RevisionId != revisionId)
            {
                throw new ArgumentException(
                    "Text-sensitivity findings must identify the scanned revision.",
                    nameof(findings));
            }

            copiedFindings[index] = finding;
        }

        RevisionId = revisionId;
        IsComplete = isComplete;
        Findings = Array.AsReadOnly(copiedFindings);
    }

    public EntityId<TranscriptRevisionIdTag> RevisionId { get; }

    public string RuleSetVersion => TextSensitivityScanner.RuleSetVersion;

    public bool IsComplete { get; }

    public IReadOnlyList<TextSensitivityFinding> Findings { get; }

    public bool RequiresExternalOutputWarning => !IsComplete || Findings.Count != 0;
}

public static class TextSensitivityScanner
{
    public const string RuleSetVersion = "1.0.0";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Regex EmailPattern = CreateRegex(
        """[A-Z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?(?:\.[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?)*""");
    private static readonly Regex InternalUrlPattern = CreateRegex(
        """(?<value>(?:(?:https?|wss?)://(?:\[[A-Z0-9:.%_-]+\]|[A-Z0-9._~-]+)(?::[0-9]{1,5})?(?:[/?#][^\s<>"'。、，；！？「」『』（）【】]*)?|file://[^\s<>"'。、，；！？「」『』（）【】]+))""");
    private static readonly Regex InternalHostPattern = CreateRegex(
        """(?<value>(?:localhost|(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\.)+(?:local|internal|corp|lan|home|intranet))(?::[0-9]{1,5})?)""");
    private static readonly Regex Ipv4Pattern = CreateRegex(
        """(?<value>(?:[0-9]{1,3}\.){3}[0-9]{1,3})""");
    private static readonly Regex Ipv6Pattern = CreateRegex(
        """(?<value>[A-Z0-9%_.-]*:[A-Z0-9:%_.-]*:[A-Z0-9:%_.-]*)""");
    private static readonly Regex CredentialAssignmentPattern = CreateRegex(
        """(?:authorization|proxy-authorization|x-api-key|api[ \t_-]?(?:key|キー)|subscription[ \t_-]?key|account[ \t_-]?key|shared[ \t_-]?access[ \t_-]?(?:key|signature)|client[ \t_-]?(?:secret|シークレット)|password|pwd|access[ \t_-]?(?:token|トークン)|connection[ \t_-]?string|パスワード|アクセストークン|接続文字列)[ \t]*[:=][ \t]*(?:(?:bearer|basic)[ \t]+)?(?<value>"[^"\r\n]+"|'[^'\r\n]+'|[^\s,;。、，；！？「」『』（）【】]+)""");
    private static readonly Regex BearerPattern = CreateRegex(
        """bearer[ \t]+(?<value>"[^"\r\n]+"|'[^'\r\n]+'|[A-Z0-9._~+/=-]+)""");

    // NFR-BR-003 AC-124: 10 種のうち語句・形式で候補にできる種類。いずれも「可能性」の候補で、外部公開可の判定には使わない。
    private static readonly (Regex Pattern, TextSensitivityFindingKind Kind)[] PhraseRules =
    [
        (CreateRegex("""(?:株式会社|有限会社|合同会社)[\p{L}\p{N}ー・]{1,20}|[ァ-ヴA-Za-z0-9ー・]{1,20}(?:株式会社|有限会社|合同会社)|顧客名|お客様名|取引先名|クライアント名|[A-Z][A-Z0-9&.-]{1,30}[ ]?(?:Inc\.?|Corp\.?|Co\.,?[ ]?Ltd\.?|LLC|GmbH)"""),
            TextSensitivityFindingKind.CustomerName),
        (CreateRegex("""未公開|未発表|未発売|新製品|新商品|新サービス|コードネーム|unreleased|pre-?release|not[ ]yet[ ]announced"""),
            TextSensitivityFindingKind.UnreleasedProduct),
        (CreateRegex("""\b0[0-9]{1,4}-[0-9]{1,4}-[0-9]{3,4}\b|\+81[ -]?[0-9]{1,4}[ -]?[0-9]{1,4}[ -]?[0-9]{3,4}\b|〒[ ]?[0-9]{3}-[0-9]{4}|\b[0-9]{3}-[0-9]{4}\b|住所|生年月日|本籍|マイナンバー|個人番号|電話番号|パスポート番号|運転免許証番号"""),
            TextSensitivityFindingKind.PersonalInformation),
        (CreateRegex("""[0-9][0-9,]*(?:\.[0-9]+)?[ ]?(?:万円|億円|千円|円|ドル|USD|JPY)|契約書|契約金額|契約内容|見積書|見積金額|売上高|売上|売り上げ|粗利|原価|年商|受注額|請求額|価格表"""),
            TextSensitivityFindingKind.FinancialInformation),
        (CreateRegex("""社外秘|極秘|機密|秘密保持|confidential|NDA|取扱注意|関係者外秘|内部資料|社内資料|社内限定|internal[ ]use[ ]only|do[ ]not[ ]distribute"""),
            TextSensitivityFindingKind.ConfidentialMaterial),
        (CreateRegex("""今後の(?:計画|予定|戦略)|将来(?:計画|構想)|中期(?:経営)?計画|来期|来年度|ロードマップ|roadmap|発売予定|リリース予定|提供開始予定"""),
            TextSensitivityFindingKind.FuturePlan),
    ];

    public static TextSensitivityScanResult Scan(
        TranscriptRevision revision,
        IReadOnlyList<TermEntry> dictionaryEntries)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);

        List<string> dictionaryTerms = PrepareDictionaryTerms(
            dictionaryEntries,
            out bool dictionaryIsComplete);
        bool isComplete = dictionaryIsComplete;
        var candidates = new List<Candidate>();

        for (int utteranceIndex = 0;
            utteranceIndex < revision.Utterances.Count;
            utteranceIndex++)
        {
            Utterance utterance = revision.Utterances[utteranceIndex];
            if (!NormalizedText.TryCreate(utterance.Text, out NormalizedText? text))
            {
                isComplete = false;
                continue;
            }

            AddRegexFindings(
                EmailPattern,
                text,
                revision,
                utterance,
                utteranceIndex,
                TextSensitivityFindingKind.EmailAddress,
                SelectEmail,
                candidates,
                ref isComplete);
            AddRegexFindings(
                InternalUrlPattern,
                text,
                revision,
                utterance,
                utteranceIndex,
                TextSensitivityFindingKind.InternalNetworkLocation,
                SelectInternalUrl,
                candidates,
                ref isComplete);
            AddRegexFindings(
                InternalHostPattern,
                text,
                revision,
                utterance,
                utteranceIndex,
                TextSensitivityFindingKind.InternalNetworkLocation,
                SelectInternalHost,
                candidates,
                ref isComplete);
            AddRegexFindings(
                Ipv4Pattern,
                text,
                revision,
                utterance,
                utteranceIndex,
                TextSensitivityFindingKind.InternalNetworkLocation,
                SelectPrivateIpv4,
                candidates,
                ref isComplete);
            AddRegexFindings(
                Ipv6Pattern,
                text,
                revision,
                utterance,
                utteranceIndex,
                TextSensitivityFindingKind.InternalNetworkLocation,
                SelectPrivateIpv6,
                candidates,
                ref isComplete);
            AddRegexFindings(
                CredentialAssignmentPattern,
                text,
                revision,
                utterance,
                utteranceIndex,
                TextSensitivityFindingKind.CredentialPattern,
                SelectCredentialValue,
                candidates,
                ref isComplete);
            AddRegexFindings(
                BearerPattern,
                text,
                revision,
                utterance,
                utteranceIndex,
                TextSensitivityFindingKind.CredentialPattern,
                SelectCredentialValue,
                candidates,
                ref isComplete);
            foreach ((Regex phrase, TextSensitivityFindingKind phraseKind) in PhraseRules)
            {
                AddRegexFindings(
                    phrase,
                    text,
                    revision,
                    utterance,
                    utteranceIndex,
                    phraseKind,
                    SelectWhole,
                    candidates,
                    ref isComplete);
            }

            AddDictionaryFindings(
                text,
                dictionaryTerms,
                revision,
                utterance,
                utteranceIndex,
                candidates,
                ref isComplete);
        }

        TextSensitivityFinding[] findings = CanonicalizeCandidates(candidates)
            .OrderBy(static candidate => candidate.UtteranceIndex)
            .ThenBy(static candidate => candidate.Finding.Utf16Offset)
            .ThenByDescending(static candidate => candidate.Finding.Utf16Length)
            .ThenBy(static candidate => candidate.Finding.Kind)
            .Select(static candidate => candidate.Finding)
            .ToArray();
        return new TextSensitivityScanResult(revision.Id, isComplete, findings);
    }

    private static List<string> PrepareDictionaryTerms(
        IReadOnlyList<TermEntry> entries,
        out bool isComplete)
    {
        isComplete = true;
        var uniqueTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entries.Count; index++)
        {
            TermEntry entry = entries[index]
                ?? throw new ArgumentException(
                    "Dictionary entries must not contain null elements.",
                    nameof(entries));
            AddNormalizedDictionaryTerm(entry.WrittenForm, uniqueTerms, ref isComplete);
            AddNormalizedDictionaryTerm(entry.Reading, uniqueTerms, ref isComplete);
        }

        return uniqueTerms
            .OrderByDescending(static term => term.Length)
            .ThenBy(static term => term, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static term => term, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddNormalizedDictionaryTerm(
        string value,
        ISet<string> terms,
        ref bool isComplete)
    {
        try
        {
            string normalized = value.Normalize(NormalizationForm.FormKC);
            if (normalized.Length == 0)
            {
                isComplete = false;
                return;
            }

            _ = terms.Add(normalized);
        }
        catch (ArgumentException)
        {
            isComplete = false;
        }
    }

    private static void AddRegexFindings(
        Regex pattern,
        NormalizedText text,
        TranscriptRevision revision,
        Utterance utterance,
        int utteranceIndex,
        TextSensitivityFindingKind kind,
        MatchSelector selector,
        ICollection<Candidate> candidates,
        ref bool isComplete)
    {
        try
        {
            for (Match match = pattern.Match(text.Value);
                match.Success;
                match = match.NextMatch())
            {
                NormalizedRange? selected = selector(text.Value, match);
                if (selected is NormalizedRange range)
                {
                    AddFinding(
                        text,
                        range,
                        revision,
                        utterance,
                        utteranceIndex,
                        kind,
                        candidates,
                        ref isComplete);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            isComplete = false;
        }
    }

    private static void AddDictionaryFindings(
        NormalizedText text,
        IReadOnlyList<string> dictionaryTerms,
        TranscriptRevision revision,
        Utterance utterance,
        int utteranceIndex,
        ICollection<Candidate> candidates,
        ref bool isComplete)
    {
        foreach (string term in dictionaryTerms)
        {
            int searchOffset = 0;
            while (searchOffset <= text.Value.Length - term.Length)
            {
                int matchOffset = text.Value.IndexOf(
                    term,
                    searchOffset,
                    StringComparison.OrdinalIgnoreCase);
                if (matchOffset < 0)
                {
                    break;
                }

                AddFinding(
                    text,
                    new NormalizedRange(matchOffset, term.Length),
                    revision,
                    utterance,
                    utteranceIndex,
                    TextSensitivityFindingKind.DictionaryTerm,
                    candidates,
                    ref isComplete);
                searchOffset = matchOffset + 1;
            }
        }
    }

    private static void AddFinding(
        NormalizedText text,
        NormalizedRange normalizedRange,
        TranscriptRevision revision,
        Utterance utterance,
        int utteranceIndex,
        TextSensitivityFindingKind kind,
        ICollection<Candidate> candidates,
        ref bool isComplete)
    {
        if (!text.TryMapToOriginal(
                normalizedRange.Offset,
                normalizedRange.Length,
                out int utf16Offset,
                out int utf16Length))
        {
            isComplete = false;
            return;
        }

        var finding = new TextSensitivityFinding(
            revision.Id,
            utterance.Id,
            utterance.SourceSpanId,
            kind,
            utf16Offset,
            utf16Length);
        candidates.Add(new Candidate(utteranceIndex, finding));
    }

    private static IEnumerable<Candidate> CanonicalizeCandidates(
        IEnumerable<Candidate> candidates)
    {
        foreach (IGrouping<(int UtteranceIndex, TextSensitivityFindingKind Kind), Candidate>
            group in candidates.GroupBy(static candidate =>
                (candidate.UtteranceIndex, candidate.Finding.Kind)))
        {
            long furthestRetainedEnd = -1;
            foreach (Candidate candidate in group
                .OrderBy(static item => item.Finding.Utf16Offset)
                .ThenByDescending(static item => item.Finding.Utf16Length))
            {
                long candidateEnd =
                    (long)candidate.Finding.Utf16Offset +
                    candidate.Finding.Utf16Length;
                if (candidateEnd <= furthestRetainedEnd)
                {
                    continue;
                }

                yield return candidate;
                furthestRetainedEnd = candidateEnd;
            }
        }
    }

    private static NormalizedRange? SelectWhole(string text, Match match) =>
        match.Length == 0 ? null : new NormalizedRange(match.Index, match.Length);

    private static NormalizedRange? SelectEmail(string text, Match match)
    {
        if (!HasTokenBoundary(
                text,
                match.Index,
                match.Length,
                IsEmailContinuation))
        {
            return null;
        }

        int atOffset = match.Value.IndexOf('@', StringComparison.Ordinal);
        int assignmentOffset = atOffset > 0
            ? match.Value.LastIndexOf('=', atOffset - 1)
            : -1;
        if (assignmentOffset >= 0 &&
            IsCredentialAssignmentLabel(match.Value[..assignmentOffset]))
        {
            int valueOffset = assignmentOffset + 1;
            return new NormalizedRange(
                match.Index + valueOffset,
                match.Length - valueOffset);
        }

        return new NormalizedRange(match.Index, match.Length);
    }

    private static bool IsCredentialAssignmentLabel(string value)
    {
        string compact = value
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return compact.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("proxyauthorization", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("xapikey", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("subscriptionkey", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("accountkey", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("sharedaccesskey", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("sharedaccesssignature", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("clientsecret", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("password", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("pwd", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("accesstoken", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("connectionstring", StringComparison.OrdinalIgnoreCase);
    }

    private static NormalizedRange? SelectInternalUrl(string text, Match match)
    {
        Group value = match.Groups["value"];
        int length = value.Length;
        while (length > 0 && IsTrailingUrlPunctuation(text[value.Index + length - 1]))
        {
            length--;
        }

        if (length == 0)
        {
            return null;
        }

        string candidate = text.Substring(value.Index, length);
        return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
            IsInternalUri(uri)
            ? new NormalizedRange(value.Index, length)
            : null;
    }

    private static NormalizedRange? SelectInternalHost(string text, Match match)
    {
        Group value = match.Groups["value"];
        return HasTokenBoundary(
            text,
            value.Index,
            value.Length,
            IsHostContinuation)
            ? new NormalizedRange(value.Index, value.Length)
            : null;
    }

    private static NormalizedRange? SelectPrivateIpv4(string text, Match match)
    {
        Group value = match.Groups["value"];
        if (!HasTokenBoundary(
                text,
                value.Index,
                value.Length,
                IsIpv4Continuation))
        {
            return null;
        }

        string candidate = text.Substring(value.Index, value.Length);
        return IPAddress.TryParse(candidate, out IPAddress? address) &&
            IsInternalAddress(address)
            ? new NormalizedRange(value.Index, value.Length)
            : null;
    }

    private static NormalizedRange? SelectPrivateIpv6(string text, Match match)
    {
        Group value = match.Groups["value"];
        int length = value.Length;
        while (length > 0 && IsTrailingUrlPunctuation(text[value.Index + length - 1]))
        {
            length--;
        }

        if (length == 0 ||
            !HasTokenBoundary(
                text,
                value.Index,
                length,
                IsIpv6Continuation))
        {
            return null;
        }

        string candidate = text.Substring(value.Index, length);
        int zoneIndex = candidate.IndexOf('%', StringComparison.Ordinal);
        string addressText = zoneIndex < 0 ? candidate : candidate[..zoneIndex];
        return IPAddress.TryParse(addressText, out IPAddress? address) &&
            address.AddressFamily == AddressFamily.InterNetworkV6 &&
            IsInternalAddress(address)
            ? new NormalizedRange(value.Index, length)
            : null;
    }

    private static NormalizedRange? SelectCredentialValue(string text, Match match)
    {
        if (match.Index > 0 && IsIdentifierContinuation(text[match.Index - 1]))
        {
            return null;
        }

        Group value = match.Groups["value"];
        return value.Success && value.Length > 0
            ? new NormalizedRange(value.Index, value.Length)
            : null;
    }

    private static bool IsInternalUri(Uri uri)
    {
        if (uri.IsFile)
        {
            return true;
        }

        string host = uri.Host;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return IsInternalAddress(address);
        }

        if (host.Length == 0 ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        return InternalHostSuffixes.Any(suffix =>
            host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInternalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsInternalAddress(address.MapToIPv4());
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0 ||
                bytes[0] == 10 ||
                bytes[0] == 127 ||
                bytes[0] == 169 && bytes[1] == 254 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        return address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            (bytes[0] & 0xfe) == 0xfc;
    }

    private static bool HasTokenBoundary(
        string text,
        int offset,
        int length,
        Func<char, bool> isContinuation)
    {
        return (offset == 0 || !isContinuation(text[offset - 1])) &&
            (offset + length == text.Length ||
                !isContinuation(text[offset + length]));
    }

    private static bool IsEmailContinuation(char value) =>
        IsAsciiLetterOrDigit(value) ||
        value is '.' or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+'
            or '/' or '=' or '?' or '^' or '_' or '`' or '{' or '|' or '}' or '~'
            or '-' or '@';

    private static bool IsHostContinuation(char value) =>
        IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_';

    private static bool IsIpv4Continuation(char value) =>
        value is >= '0' and <= '9' || value == '.';

    private static bool IsIpv6Continuation(char value) =>
        IsAsciiLetterOrDigit(value) || value is ':' or '%' or '_' or '.' or '-';

    private static bool IsIdentifierContinuation(char value) =>
        IsAsciiLetterOrDigit(value) || value is '_' or '-';

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' ||
        value is >= 'a' and <= 'z' ||
        value is >= '0' and <= '9';

    private static bool IsTrailingUrlPunctuation(char value) =>
        value is '.' or ',' or ';' or '!' or '?' or ')' or '}'
            or '。' or '、' or '，' or '；' or '！' or '？'
            or '」' or '』' or '）' or '】';

    private static Regex CreateRegex(string pattern) =>
        new(
            pattern,
            RegexOptions.CultureInvariant |
                RegexOptions.IgnoreCase |
                RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1));

    private static readonly string[] InternalHostSuffixes =
    [
        ".local",
        ".internal",
        ".corp",
        ".lan",
        ".home",
        ".intranet",
    ];

    private delegate NormalizedRange? MatchSelector(string text, Match match);

    private readonly record struct NormalizedRange(int Offset, int Length);

    private sealed record Candidate(
        int UtteranceIndex,
        TextSensitivityFinding Finding);

    private sealed class NormalizedText
    {
        private NormalizedText(
            string value,
            int[] originalStarts,
            int[] originalEnds)
        {
            Value = value;
            _originalStarts = originalStarts;
            _originalEnds = originalEnds;
        }

        private readonly int[] _originalStarts;
        private readonly int[] _originalEnds;

        internal string Value { get; }

        internal static bool TryCreate(
            string source,
            [NotNullWhen(true)] out NormalizedText? normalizedText)
        {
            try
            {
                _ = StrictUtf8.GetByteCount(source);
                int[] elementStarts = StringInfo.ParseCombiningCharacters(source);
                var value = new StringBuilder(source.Length);
                var originalStarts = new List<int>(source.Length);
                var originalEnds = new List<int>(source.Length);

                for (int index = 0; index < elementStarts.Length; index++)
                {
                    int originalStart = elementStarts[index];
                    int originalEnd = index + 1 < elementStarts.Length
                        ? elementStarts[index + 1]
                        : source.Length;
                    string element = source[originalStart..originalEnd]
                        .Normalize(NormalizationForm.FormKC);
                    value.Append(element);
                    for (int character = 0; character < element.Length; character++)
                    {
                        originalStarts.Add(originalStart);
                        originalEnds.Add(originalEnd);
                    }
                }

                normalizedText = new NormalizedText(
                    value.ToString(),
                    originalStarts.ToArray(),
                    originalEnds.ToArray());
                return true;
            }
            catch (ArgumentException)
            {
                normalizedText = null;
                return false;
            }
        }

        internal bool TryMapToOriginal(
            int normalizedOffset,
            int normalizedLength,
            out int originalOffset,
            out int originalLength)
        {
            long normalizedEnd = (long)normalizedOffset + normalizedLength;
            if (normalizedOffset < 0 ||
                normalizedLength <= 0 ||
                normalizedEnd > _originalStarts.Length)
            {
                originalOffset = 0;
                originalLength = 0;
                return false;
            }

            originalOffset = _originalStarts[normalizedOffset];
            int originalEnd = _originalEnds[(int)normalizedEnd - 1];
            originalLength = originalEnd - originalOffset;
            return originalLength > 0;
        }
    }
}