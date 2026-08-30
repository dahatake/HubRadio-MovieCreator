using System.Reflection;
using System.Text.Json;
using HubRadio.App.Policy;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TextSensitivityScannerTests
{
    [TestMethod]
    public void Scan_FindsFourSupportedCandidateKindsWithSourceProvenance()
    {
        const string text =
            "連絡はuser@example.com。内部はhttps://10.0.0.5/admin。password=TopSecret ProjectFalcon";
        TranscriptRevision revision = CreateRevision(text);
        TermEntry[] dictionary = [CreateTerm("ProjectFalcon", "プロジェクトファルコン")];

        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            revision,
            dictionary);

        Assert.IsTrue(result.IsComplete);
        Assert.IsTrue(result.RequiresExternalOutputWarning);
        Assert.AreEqual(revision.Id, result.RevisionId);
        Assert.AreEqual(TextSensitivityScanner.RuleSetVersion, result.RuleSetVersion);
        CollectionAssert.AreEquivalent(
            Enum.GetValues<TextSensitivityFindingKind>(),
            result.Findings.Select(static finding => finding.Kind).ToArray());

        Utterance utterance = revision.Utterances.Single();
        foreach (TextSensitivityFinding finding in result.Findings)
        {
            Assert.AreEqual(revision.Id, finding.RevisionId);
            Assert.AreEqual(utterance.Id, finding.UtteranceId);
            Assert.AreEqual(utterance.SourceSpanId, finding.SourceSpanId);
            Assert.IsGreaterThanOrEqualTo(0, finding.Utf16Offset);
            Assert.IsGreaterThan(0, finding.Utf16Length);
            Assert.IsLessThanOrEqualTo(
                text.Length,
                finding.Utf16Offset + finding.Utf16Length);
            Assert.IsTrue(finding.ReasonCode.StartsWith(
                "sensitivity.",
                StringComparison.Ordinal));
        }

        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.EmailAddress),
            "user@example.com");
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.InternalNetworkLocation),
            "https://10.0.0.5/admin");
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.CredentialPattern),
            "TopSecret");
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.DictionaryTerm),
            "ProjectFalcon");
    }

    [TestMethod]
    public void Scan_NormalizesWidthCaseAndCombiningMarksButMapsOriginalUtf16()
    {
        const string decomposedTerm = "Cafe\u0301";
        const string text =
            "😀 ｕｓｅｒ＠ｅｘａｍｐｌｅ．ｃｏｍ ＡＰＩキー＝Ｓｅｃｒｅｔ Cafe\u0301";
        TranscriptRevision revision = CreateRevision(text);
        TermEntry[] dictionary = [CreateTerm("Café", "カフェ")];

        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            revision,
            dictionary);

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(3, result.Findings);
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.EmailAddress),
            "ｕｓｅｒ＠ｅｘａｍｐｌｅ．ｃｏｍ");
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.CredentialPattern),
            "Ｓｅｃｒｅｔ");
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.DictionaryTerm),
            decomposedTerm);
        Assert.AreEqual(
            decomposedTerm.Length,
            Find(result, TextSensitivityFindingKind.DictionaryTerm).Utf16Length);
    }

    [TestMethod]
    public void Scan_DeduplicatesContainedDictionaryAndInternalLocationMatches()
    {
        const string text = "HubRadio https://192.168.0.2/status";
        TranscriptRevision revision = CreateRevision(text);
        TermEntry[] dictionary =
        [
            CreateTerm("HubRadio", "ハブラジオ"),
            CreateTerm("Hub", "ハブ"),
            CreateTerm(
                "HubRadio",
                "ハブラジオ",
                TermScope.ForSeries(EntityId<SeriesIdTag>.New())),
        ];

        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            revision,
            dictionary);

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(2, result.Findings);
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.DictionaryTerm),
            "HubRadio");
        AssertRange(
            text,
            Find(result, TextSensitivityFindingKind.InternalNetworkLocation),
            "https://192.168.0.2/status");
    }

    [TestMethod]
    public void Scan_FindsLocalhostPrivateIpv4AndLoopbackIpv6ButNotPublicUrl()
    {
        const string text =
            "http://localhost:8080/x 192.168.1.7 https://[::1]/status " +
            "https://[fe80::1%25eth0]/zone。https://example.com";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            Array.Empty<TermEntry>());

        Assert.IsTrue(result.IsComplete);
        TextSensitivityFinding[] locations = result.Findings
            .Where(static finding =>
                finding.Kind == TextSensitivityFindingKind.InternalNetworkLocation)
            .ToArray();
        Assert.HasCount(4, locations);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "http://localhost:8080/x",
                "192.168.1.7",
                "https://[::1]/status",
                "https://[fe80::1%25eth0]/zone",
            },
            locations
                .Select(finding => text.Substring(
                    finding.Utf16Offset,
                    finding.Utf16Length))
                .ToArray());
        Assert.IsFalse(result.Findings.Any(finding =>
            text.Substring(finding.Utf16Offset, finding.Utf16Length)
                .Contains("example.com", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Scan_FindsBarePrivateIpv6ButNotPublicIpv6()
    {
        const string text = "fe80::2%12 fd00::7 2001:4860:4860::8888";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            Array.Empty<TermEntry>());

        Assert.IsTrue(result.IsComplete);
        TextSensitivityFinding[] locations = result.Findings
            .Where(static finding =>
                finding.Kind == TextSensitivityFindingKind.InternalNetworkLocation)
            .ToArray();
        Assert.HasCount(2, locations);
        CollectionAssert.AreEquivalent(
            new[] { "fe80::2%12", "fd00::7" },
            locations
                .Select(finding => text.Substring(
                    finding.Utf16Offset,
                    finding.Utf16Length))
                .ToArray());
    }

    [TestMethod]
    public void Scan_PreservesOverlappingReasonsWithoutDuplicateFindings()
    {
        const string text = "password=user@example.com";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            [CreateTerm("user@example.com", "連絡先")]);

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(3, result.Findings);
        CollectionAssert.AreEquivalent(
            new[]
            {
                TextSensitivityFindingKind.EmailAddress,
                TextSensitivityFindingKind.CredentialPattern,
                TextSensitivityFindingKind.DictionaryTerm,
            },
            result.Findings.Select(static finding => finding.Kind).ToArray());
        Assert.IsTrue(result.Findings.All(finding =>
            finding.Utf16Offset == text.IndexOf("user", StringComparison.Ordinal) &&
            finding.Utf16Length == "user@example.com".Length));
    }

    [TestMethod]
    public void Scan_PreservesEqualsInEmailLocalPartOutsideCredentialAssignment()
    {
        const string text = "連絡先はfoo=bar@example.comです";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            Array.Empty<TermEntry>());

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(1, result.Findings);
        AssertRange(text, result.Findings[0], "foo=bar@example.com");
    }

    [TestMethod]
    public void Scan_FindsSpacedAccountAndSharedAccessCredentialLabels()
    {
        const string text = "Account Key=first; Shared Access Signature: second";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            Array.Empty<TermEntry>());

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(2, result.Findings);
        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            result.Findings
                .Select(finding => text.Substring(
                    finding.Utf16Offset,
                    finding.Utf16Length))
                .ToArray());
    }

    [TestMethod]
    public void Scan_FindsSingleCharacterStandaloneBearerToken()
    {
        const string text = "bearer a";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            Array.Empty<TermEntry>());

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(1, result.Findings);
        Assert.AreEqual(
            TextSensitivityFindingKind.CredentialPattern,
            result.Findings[0].Kind);
        AssertRange(text, result.Findings[0], "a");
    }

    [TestMethod]
    public void Scan_UsesDictionaryValuesLiterallyAndFindsEachOccurrence()
    {
        const string text = "A+B と a+b";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            [CreateTerm("A+B", "エープラスビー")]);

        Assert.IsTrue(result.IsComplete);
        TextSensitivityFinding[] findings = result.Findings
            .Where(static finding =>
                finding.Kind == TextSensitivityFindingKind.DictionaryTerm)
            .ToArray();
        Assert.HasCount(2, findings);
        CollectionAssert.AreEqual(
            new[]
            {
                text.IndexOf("A+B", StringComparison.Ordinal),
                text.IndexOf("a+b", StringComparison.Ordinal),
            },
            findings.Select(static finding => finding.Utf16Offset).ToArray());
    }

    [TestMethod]
    public void Scan_FindsOverlappingDictionaryOccurrences()
    {
        const string text = "aaa";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(text),
            [CreateTerm("aa", "ダブルエー")]);

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(2, result.Findings);
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            result.Findings.Select(static finding => finding.Utf16Offset).ToArray());
        Assert.IsTrue(result.Findings.All(static finding =>
            finding.Utf16Length == 2));
    }

    [TestMethod]
    public void Scan_OrdersMultipleUtterancesAndKeepsEachSourceSpan()
    {
        TranscriptRevision revision = CreateRevision(
            ["password=first", "second@example.com"]);

        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            revision,
            Array.Empty<TermEntry>());

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(2, result.Findings);
        for (int index = 0; index < result.Findings.Count; index++)
        {
            TextSensitivityFinding finding = result.Findings[index];
            Utterance utterance = revision.Utterances[index];
            Assert.AreEqual(utterance.Id, finding.UtteranceId);
            Assert.AreEqual(utterance.SourceSpanId, finding.SourceSpanId);
            Assert.IsLessThanOrEqualTo(
                utterance.Text.Length,
                finding.Utf16Offset + finding.Utf16Length);
        }

        Assert.AreEqual(
            TextSensitivityFindingKind.CredentialPattern,
            result.Findings[0].Kind);
        Assert.AreEqual(
            TextSensitivityFindingKind.EmailAddress,
            result.Findings[1].Kind);
    }

    [TestMethod]
    public void Scan_EmptyOrOrdinaryTranscriptCompletesWithoutExternalWarning()
    {
        TranscriptRevision empty = new(
            EntityId<TranscriptRevisionIdTag>.New(),
            parentRevisionId: null,
            TranscriptChangeSource.Machine,
            DateTimeOffset.UnixEpoch,
            Array.Empty<Utterance>());
        TextSensitivityScanResult emptyResult = TextSensitivityScanner.Scan(
            empty,
            Array.Empty<TermEntry>());
        Assert.IsTrue(emptyResult.IsComplete);
        Assert.IsFalse(emptyResult.RequiresExternalOutputWarning);
        Assert.IsEmpty(emptyResult.Findings);

        TextSensitivityScanResult ordinary = TextSensitivityScanner.Scan(
            CreateRevision("公開済みの説明です https://example.com"),
            Array.Empty<TermEntry>());
        Assert.IsTrue(ordinary.IsComplete);
        Assert.IsFalse(ordinary.RequiresExternalOutputWarning);
        Assert.IsEmpty(ordinary.Findings);
    }

    [TestMethod]
    public void Scan_EditedRevisionRemainsWarnedUntilCandidateIsRemoved()
    {
        TranscriptRevision source = CreateRevision("連絡先はuser@example.comです");
        TextSensitivityScanResult sourceResult = TextSensitivityScanner.Scan(
            source,
            Array.Empty<TermEntry>());
        Assert.IsTrue(sourceResult.RequiresExternalOutputWarning);

        TranscriptRevision resolved = CreateRevision(
            "連絡先は確認済みの社内窓口です",
            source.Id,
            TranscriptChangeSource.Human);
        TextSensitivityScanResult resolvedResult = TextSensitivityScanner.Scan(
            resolved,
            Array.Empty<TermEntry>());

        Assert.IsTrue(resolvedResult.IsComplete);
        Assert.IsFalse(resolvedResult.RequiresExternalOutputWarning);
        Assert.IsEmpty(resolvedResult.Findings);
        Assert.AreEqual(resolved.Id, resolvedResult.RevisionId);
    }

    [TestMethod]
    public void Scan_InvalidUnicodeFailsClosedWithoutEchoingText()
    {
        const string invalid = "秘密\ud800";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision(invalid),
            Array.Empty<TermEntry>());

        Assert.IsFalse(result.IsComplete);
        Assert.IsTrue(result.RequiresExternalOutputWarning);
        Assert.IsEmpty(result.Findings);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains(
            "秘密",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Scan_LargeInputCompletesWithUntimedToken()
    {
        string text = string.Concat(new string('a', 200_000), " user@example.com");
        TranscriptRevision revision = CreateRevision(text);
        Assert.IsNull(revision.Utterances[0].Tokens[0].Range);

        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            revision,
            Array.Empty<TermEntry>());

        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(1, result.Findings);
        Assert.AreEqual(
            TextSensitivityFindingKind.EmailAddress,
            result.Findings[0].Kind);
        AssertRange(text, result.Findings[0], "user@example.com");
    }

    [TestMethod]
    public void PublicSurface_IsImmutableAndDoesNotExposeMatchedText()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                TextSensitivityFindingKind.EmailAddress,
                TextSensitivityFindingKind.InternalNetworkLocation,
                TextSensitivityFindingKind.CredentialPattern,
                TextSensitivityFindingKind.DictionaryTerm,
            },
            Enum.GetValues<TextSensitivityFindingKind>());
        AssertPublicGetOnlyProperties(
            typeof(TextSensitivityFinding),
            "RevisionId",
            "UtteranceId",
            "SourceSpanId",
            "Kind",
            "ReasonCode",
            "Utf16Offset",
            "Utf16Length");
        AssertPublicGetOnlyProperties(
            typeof(TextSensitivityScanResult),
            "RevisionId",
            "RuleSetVersion",
            "IsComplete",
            "Findings",
            "RequiresExternalOutputWarning");
        Assert.AreEqual(0, typeof(TextSensitivityFinding).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TextSensitivityScanResult).GetConstructors().Length);
        Assert.IsTrue(typeof(TextSensitivityScanner).IsAbstract);
        Assert.IsTrue(typeof(TextSensitivityScanner).IsSealed);

        const string secret = "Bearer-secret-value";
        TextSensitivityScanResult result = TextSensitivityScanner.Scan(
            CreateRevision($"Authorization: Bearer {secret}"),
            Array.Empty<TermEntry>());
        string serialized = JsonSerializer.Serialize(result);
        Assert.IsFalse(serialized.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(result.Findings[0].ToString()!.Contains(
            secret,
            StringComparison.Ordinal));
    }

    private static TextSensitivityFinding Find(
        TextSensitivityScanResult result,
        TextSensitivityFindingKind kind) =>
        result.Findings.Single(finding => finding.Kind == kind);

    private static void AssertRange(
        string source,
        TextSensitivityFinding finding,
        string expected)
    {
        Assert.AreEqual(
            expected,
            source.Substring(finding.Utf16Offset, finding.Utf16Length));
    }

    private static TranscriptRevision CreateRevision(
        string text,
        EntityId<TranscriptRevisionIdTag>? parentRevisionId = null,
        TranscriptChangeSource changeSource = TranscriptChangeSource.Machine) =>
        CreateRevision([text], parentRevisionId, changeSource);

    private static TranscriptRevision CreateRevision(
        IReadOnlyList<string> texts,
        EntityId<TranscriptRevisionIdTag>? parentRevisionId = null,
        TranscriptChangeSource changeSource = TranscriptChangeSource.Machine)
    {
        var utterances = new List<Utterance>(texts.Count);
        EntityId<TranscriptSpeakerIdTag> speakerId =
            EntityId<TranscriptSpeakerIdTag>.New();
        for (int index = 0; index < texts.Count; index++)
        {
            string text = texts[index];
            var token = new Token(
                EntityId<TranscriptTokenIdTag>.New(),
                text,
                range: null,
                recognitionConfidence: 0.9);
            var sentence = new Sentence(
                EntityId<TranscriptSentenceIdTag>.New(),
                text,
                [token]);
            utterances.Add(new Utterance(
                EntityId<TranscriptUtteranceIdTag>.New(),
                EntityId<SourceSpanIdTag>.New(),
                speakerId,
                "Speaker 1",
                text,
                new TimeRange(
                    new RationalTime(index, 1),
                    new RationalTime(1, 1)),
                recognitionConfidence: 0.9,
                [sentence]));
        }

        return new TranscriptRevision(
            EntityId<TranscriptRevisionIdTag>.New(),
            parentRevisionId,
            changeSource,
            DateTimeOffset.UnixEpoch,
            utterances);
    }

    private static TermEntry CreateTerm(
        string writtenForm,
        string reading,
        TermScope? scope = null) =>
        new(
            EntityId<TermEntryIdTag>.New(),
            scope ?? TermScope.ForProject(EntityId<HubRadio.Core.Projects.ProjectIdTag>.New()),
            writtenForm,
            reading,
            version: 0);

    private static void AssertPublicGetOnlyProperties(
        Type type,
        params string[] expectedNames)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }
}