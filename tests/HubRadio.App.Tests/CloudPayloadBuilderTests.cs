using System.Reflection;
using System.Text;
using System.Text.Json;
using HubRadio.App.Azure;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class CloudPayloadBuilderTests
{
    private static readonly DateTimeOffset ConfirmedAt =
        new(2034, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [TestMethod]
    public void BuildPreview_UsesOnlyExplicitAdjacentContextInSourceOrder()
    {
        TranscriptRevision revision = CreateRevision(["前文。", "対象本文", "後文。"]);
        CloudTextSelection selection = Selection(
            revision.Utterances[1],
            0,
            revision.Utterances[1].Text.Length,
            before: Range(revision.Utterances[0]),
            after: Range(revision.Utterances[2]));

        CloudPayloadPreview preview = Build(revision, [selection]);

        Assert.AreEqual(revision.Id, preview.RevisionId);
        Assert.AreEqual(CloudPayloadPurpose.TextSuggestionEvaluation, preview.Purpose);
        Assert.IsTrue(preview.RequiresExplicitConfirmation);
        CollectionAssert.AreEqual(
            new[]
            {
                CloudPayloadSegmentRole.ContextBefore,
                CloudPayloadSegmentRole.Selected,
                CloudPayloadSegmentRole.ContextAfter,
            },
            preview.Segments.Select(static segment => segment.Role).ToArray());
        CollectionAssert.AreEqual(
            new[] { "e0", "e1", "e2" },
            preview.Segments.Select(static segment => segment.EvidenceId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "前文。", "対象本文", "後文。" },
            preview.Segments.Select(static segment => segment.OutboundText).ToArray());

        JsonElement payload = ConfirmAndParse(preview);
        JsonElement[] segments = payload.GetProperty("segments").EnumerateArray().ToArray();
        Assert.HasCount(3, segments);
        CollectionAssert.AreEqual(
            new[] { "context-before", "selected", "context-after" },
            segments.Select(static segment =>
                segment.GetProperty("role").GetString()).ToArray());
    }

    [TestMethod]
    public void BuildPreview_MasksEverySensitivityKindWithoutRetainingMatchedValues()
    {
        const string email = "user@example.com";
        const string internalUrl = "https://10.0.0.5/admin";
        const string secret = "TopSecret";
        const string dictionaryValue = "ProjectFalcon";
        string text =
            $"連絡は{email}。内部は{internalUrl}。password={secret} {dictionaryValue}";
        TranscriptRevision revision = CreateRevision(text);

        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])],
            [CreateTerm(dictionaryValue, "プロジェクトファルコン")]);

        CloudPayloadPreviewSegment segment = preview.Segments.Single();
        Assert.IsFalse(segment.OutboundText.Contains(email, StringComparison.Ordinal));
        Assert.IsFalse(segment.OutboundText.Contains(internalUrl, StringComparison.Ordinal));
        Assert.IsFalse(segment.OutboundText.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(segment.OutboundText.Contains(
            dictionaryValue,
            StringComparison.Ordinal));
        Assert.AreEqual(4, CountOccurrences(
            segment.OutboundText,
            CloudPayloadMaskChange.MaskReplacement));
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "sensitivity.email-address",
                "sensitivity.internal-network-location",
                "sensitivity.credential-pattern",
                "sensitivity.dictionary-term",
            },
            segment.MaskChanges
                .SelectMany(static change => change.ReasonCodes)
                .ToArray());

        string payload = ConfirmAndGetJson(preview);
        foreach (string forbidden in new[]
        {
            email,
            internalUrl,
            secret,
            dictionaryValue,
        })
        {
            Assert.IsFalse(payload.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void BuildPreview_MasksUrlsPathsFileNamesSidsAndInternalIdsAfterNfkc()
    {
        const string path = @"C:\Users\Alice\secret.mov";
        const string fileName = "report.pdf";
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        const string internalId = "11111111-2222-3333-4444-555555555555";
        const string quotedPath = @"C:\Secret Folder\plan.bin";
        const string relativePath = "folder/private.dat";
        string text = string.Join(
            ' ',
            "ｈｔｔｐｓ：／／ｅｘａｍｐｌｅ．ｃｏｍ／ａ",
            path,
            fileName,
            sid,
            internalId,
            $"\"{quotedPath}\"",
            relativePath);
        TranscriptRevision revision = CreateRevision(text);

        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])]);

        CloudPayloadPreviewSegment segment = preview.Segments.Single();
        Assert.AreEqual(7, CountOccurrences(
            segment.OutboundText,
            CloudPayloadMaskChange.MaskReplacement));
        foreach (string forbidden in new[]
        {
            path,
            fileName,
            sid,
            internalId,
            quotedPath,
            relativePath,
        })
        {
            Assert.IsFalse(segment.OutboundText.Contains(
                forbidden,
                StringComparison.Ordinal));
        }

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "cloud-payload.url",
                "cloud-payload.path",
                "cloud-payload.file-name",
                "cloud-payload.windows-sid",
                "cloud-payload.internal-id",
            },
            segment.MaskChanges
                .SelectMany(static change => change.ReasonCodes)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    [TestMethod]
    public void BuildPreview_MasksIntersectionWhenSelectionCutsThroughSensitiveValue()
    {
        const string text = "prefix user@example.com suffix";
        TranscriptRevision revision = CreateRevision(text);
        int offset = text.IndexOf("user", StringComparison.Ordinal) + 2;
        const int length = 6;

        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0], offset, length)]);

        CloudPayloadPreviewSegment segment = preview.Segments.Single();
        Assert.AreEqual(CloudPayloadMaskChange.MaskReplacement, segment.OutboundText);
        CloudPayloadMaskChange change = segment.MaskChanges.Single();
        Assert.AreEqual(offset, change.SourceUtf16Offset);
        Assert.AreEqual(length, change.SourceUtf16Length);
    }

    [TestMethod]
    public void BuildPreview_MergesOverlappingReasonsIntoOneGenericMask()
    {
        const string sensitive = "user@example.com";
        string text = $"password={sensitive}";
        TranscriptRevision revision = CreateRevision(text);

        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])],
            [CreateTerm(sensitive, "連絡先")]);

        CloudPayloadPreviewSegment segment = preview.Segments.Single();
        Assert.AreEqual($"password={CloudPayloadMaskChange.MaskReplacement}",
            segment.OutboundText);
        CloudPayloadMaskChange change = segment.MaskChanges.Single();
        CollectionAssert.AreEqual(
            new[]
            {
                "sensitivity.credential-pattern",
                "sensitivity.dictionary-term",
                "sensitivity.email-address",
            }.Order(StringComparer.Ordinal).ToArray(),
            change.ReasonCodes.ToArray());
    }

    [TestMethod]
    public void BuildPreview_FailsClosedWhenAnyPartOfRevisionHasInvalidUnicode()
    {
        const string privateText = "DoNotEcho\ud800";
        TranscriptRevision revision = CreateRevision([privateText, "safe selection"]);

        CloudPayloadException exception =
            Assert.ThrowsExactly<CloudPayloadException>(() => Build(
                revision,
                [Selection(revision.Utterances[1])]));

        Assert.AreEqual(
            CloudPayloadFailureReason.SensitivityScanIncomplete,
            exception.Reason);
        Assert.IsFalse(exception.ToString().Contains(
            "DoNotEcho",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildPreview_RejectsForeignOutOfBoundsAndSplitSurrogateRanges()
    {
        TranscriptRevision revision = CreateRevision("A😀B");
        Utterance utterance = revision.Utterances[0];
        var foreign = new CloudTranscriptTextRange(
            EntityId<TranscriptUtteranceIdTag>.New(),
            0,
            1);
        AssertFailure(
            CloudPayloadFailureReason.InvalidSelection,
            () => Build(revision, [new CloudTextSelection(foreign)]));

        AssertFailure(
            CloudPayloadFailureReason.InvalidSelection,
            () => Build(
                revision,
                [Selection(utterance, utterance.Text.Length, 1)]));
        AssertFailure(
            CloudPayloadFailureReason.InvalidSelection,
            () => Build(revision, [Selection(utterance, 1, 1)]));
        AssertFailure(
            CloudPayloadFailureReason.InvalidSelection,
            () => Build(revision, [Selection(utterance, 2, 1)]));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CloudTranscriptTextRange(utterance.Id, -1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CloudTranscriptTextRange(utterance.Id, 0, 0));
    }

    [TestMethod]
    public void BuildPreview_AcceptsDirectContextWithinOneUtterance()
    {
        const string text = "before-selected-after";
        TranscriptRevision revision = CreateRevision(text);
        Utterance utterance = revision.Utterances[0];
        var selection = new CloudTextSelection(
            new CloudTranscriptTextRange(utterance.Id, 7, 8),
            new CloudTranscriptTextRange(utterance.Id, 0, 7),
            new CloudTranscriptTextRange(utterance.Id, 15, 6));

        CloudPayloadPreview preview = Build(revision, [selection]);

        CollectionAssert.AreEqual(
            new[] { "before-", "selected", "-after" },
            preview.Segments.Select(static segment => segment.OutboundText).ToArray());
    }

    [TestMethod]
    public void BuildPreview_RejectsSkippedOrGappedContext()
    {
        TranscriptRevision revision = CreateRevision(["first", "middle", "target"]);
        Utterance target = revision.Utterances[2];
        AssertFailure(
            CloudPayloadFailureReason.ContextNotAdjacent,
            () => Build(
                revision,
                [Selection(
                    target,
                    before: Range(revision.Utterances[0]))]));

        TranscriptRevision sameUtterance = CreateRevision("before gap target");
        AssertFailure(
            CloudPayloadFailureReason.ContextNotAdjacent,
            () => Build(
                sameUtterance,
                [Selection(
                    sameUtterance.Utterances[0],
                    11,
                    6,
                    before: new CloudTranscriptTextRange(
                        sameUtterance.Utterances[0].Id,
                        0,
                        6))]));
    }

    [TestMethod]
    public void BuildPreview_RejectsOverlappingSelectionsAndDuplicatedContext()
    {
        TranscriptRevision revision = CreateRevision("0123456789");
        Utterance utterance = revision.Utterances[0];
        AssertFailure(
            CloudPayloadFailureReason.OverlappingSelection,
            () => Build(
                revision,
                [
                    Selection(utterance, 1, 4),
                    Selection(utterance, 3, 4),
                ]));

        CloudTranscriptTextRange shared = new(utterance.Id, 0, 2);
        AssertFailure(
            CloudPayloadFailureReason.OverlappingSelection,
            () => Build(
                revision,
                [
                    Selection(utterance, 2, 2, before: shared),
                    Selection(utterance, 0, 2),
                ]));
    }

    [TestMethod]
    public void BuildPreview_IsDeterministicForEquivalentSelectionOrder()
    {
        TranscriptRevision revision = CreateRevision(["first", "second"]);
        CloudTextSelection first = Selection(revision.Utterances[0]);
        CloudTextSelection second = Selection(revision.Utterances[1]);

        CloudPayloadPreview forward = Build(revision, [first, second]);
        CloudPayloadPreview reverse = Build(revision, [second, first]);

        Assert.AreEqual(forward.PayloadDigest, reverse.PayloadDigest);
        Assert.AreEqual(forward.ConfirmationDigest, reverse.ConfirmationDigest);
        CollectionAssert.AreEqual(
            forward.Segments.Select(static segment => segment.OutboundText).ToArray(),
            reverse.Segments.Select(static segment => segment.OutboundText).ToArray());
        CollectionAssert.AreEqual(
            Confirm(forward).GetPayloadUtf8(),
            Confirm(reverse).GetPayloadUtf8());
    }

    [TestMethod]
    public void Confirm_RejectsDigestFromAnotherPreview()
    {
        TranscriptRevision firstRevision = CreateRevision("first");
        TranscriptRevision secondRevision = CreateRevision("second");
        CloudPayloadPreview first = Build(
            firstRevision,
            [Selection(firstRevision.Utterances[0])]);
        CloudPayloadPreview second = Build(
            secondRevision,
            [Selection(secondRevision.Utterances[0])]);
        var mismatched = new CloudPayloadConfirmation(
            second.ConfirmationDigest,
            ConfirmedAt);

        AssertFailure(
            CloudPayloadFailureReason.ConfirmationDigestMismatch,
            () => CloudPayloadBuilder.Confirm(first, mismatched));
    }

    [TestMethod]
    public void Confirm_IssuesPayloadOnlyOnceForOnePreview()
    {
        TranscriptRevision revision = CreateRevision("one confirmation");
        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])]);
        var confirmation = new CloudPayloadConfirmation(
            preview.ConfirmationDigest,
            ConfirmedAt);

        _ = CloudPayloadBuilder.Confirm(preview, confirmation);

        AssertFailure(
            CloudPayloadFailureReason.ConfirmationUnavailable,
            () => CloudPayloadBuilder.Confirm(preview, confirmation));
    }

    [TestMethod]
    public async Task Confirm_ConcurrentCallsIssueExactlyOnePayload()
    {
        TranscriptRevision revision = CreateRevision("concurrent confirmation");
        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])]);
        var confirmation = new CloudPayloadConfirmation(
            preview.ConfirmationDigest,
            ConfirmedAt);
        using var start = new ManualResetEventSlim(initialState: false);
        Task<Exception?>[] attempts = Enumerable.Range(0, 16)
            .Select(attempt => Task.Run(() =>
            {
                _ = attempt;
                start.Wait();
                try
                {
                    _ = CloudPayloadBuilder.Confirm(preview, confirmation);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }))
            .ToArray();

        start.Set();
        Exception?[] outcomes = await Task.WhenAll(attempts);

        Assert.AreEqual(1, outcomes.Count(static outcome => outcome is null));
        CloudPayloadException[] rejected = outcomes
            .OfType<CloudPayloadException>()
            .ToArray();
        Assert.HasCount(15, rejected);
        Assert.IsTrue(rejected.All(static exception =>
            exception.Reason == CloudPayloadFailureReason.ConfirmationUnavailable));
    }

    [TestMethod]
    public void Confirm_BindsCanonicalPayloadAndReturnsDefensiveByteCopies()
    {
        TranscriptRevision revision = CreateRevision("確認済み本文");
        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])]);

        ConfirmedCloudPayload confirmed = Confirm(preview);
        byte[] first = confirmed.GetPayloadUtf8();
        byte[] expected = first.ToArray();
        first[0] ^= 0xff;
        byte[] second = confirmed.GetPayloadUtf8();

        CollectionAssert.AreEqual(expected, second);
        Assert.AreEqual(preview.PayloadDigest, confirmed.PayloadDigest);
        Assert.AreEqual(preview.ConfirmationDigest, confirmed.ConfirmationDigest);
        Assert.AreEqual(ConfirmedAt, confirmed.ConfirmedAt);
        Assert.AreEqual(
            confirmed.PayloadDigest,
            CanonicalJson.ComputeDigest(second));
        CollectionAssert.AreEqual(second, CanonicalJson.CanonicalizeToUtf8(second));
    }

    [TestMethod]
    public void ConfirmedPayload_ContainsClosedSchemaAndNoLocalIdentifiersOrTargetMetadata()
    {
        EntityId<TranscriptRevisionIdTag> revisionId =
            EntityId<TranscriptRevisionIdTag>.Parse(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        EntityId<TranscriptUtteranceIdTag> utteranceId =
            EntityId<TranscriptUtteranceIdTag>.Parse(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        EntityId<SourceSpanIdTag> sourceSpanId = EntityId<SourceSpanIdTag>.Parse(
            "cccccccc-cccc-cccc-cccc-cccccccccccc");
        TranscriptRevision revision = CreateRevision(
            "safe text",
            revisionId,
            utteranceId,
            sourceSpanId);
        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])]);

        string json = ConfirmAndGetJson(preview);

        foreach (string forbidden in new[]
        {
            revisionId.ToString(),
            utteranceId.ToString(),
            sourceSpanId.ToString(),
            AzureCloudConfiguration.OpenAiEndpoint.AbsoluteUri,
            AzureCloudConfiguration.DeploymentName,
            AzureCloudConfiguration.Region,
            AzureCloudConfiguration.ModelName,
        })
        {
            Assert.IsFalse(json.Contains(forbidden, StringComparison.Ordinal));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        CollectionAssert.AreEquivalent(
            new[] { "purpose", "schemaVersion", "segments" },
            document.RootElement
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        JsonElement segment = document.RootElement
            .GetProperty("segments")
            .EnumerateArray()
            .Single();
        CollectionAssert.AreEquivalent(
            new[] { "evidenceId", "role", "text" },
            segment.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        Assert.AreEqual("e0", segment.GetProperty("evidenceId").GetString());
    }

    [TestMethod]
    public void Preview_DisclosesExactDestinationModelRegionProcessingAndRetention()
    {
        TranscriptRevision revision = CreateRevision("safe text");
        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])]);

        Assert.AreEqual(
            AzureCloudConfiguration.OpenAiEndpoint.AbsoluteUri,
            preview.Destination);
        Assert.AreEqual(AzureCloudConfiguration.Region, preview.Region);
        Assert.AreEqual(
            AzureCloudConfiguration.DeploymentName,
            preview.DeploymentName);
        Assert.AreEqual(
            AzureCloudConfiguration.DeploymentType,
            preview.DeploymentType);
        Assert.AreEqual(AzureCloudConfiguration.ModelName, preview.ModelName);
        Assert.AreEqual(AzureCloudConfiguration.ModelVersion, preview.ModelVersion);
        Assert.AreEqual(
            AzureCloudConfiguration.ProcessingScopeDisclosure,
            preview.ProcessingScopeDisclosure);
        Assert.AreEqual(
            AzureCloudConfiguration.RetentionDisclosure,
            preview.RetentionDisclosure);
        Assert.AreEqual(
            HubRadio.App.Policy.TextSensitivityScanner.RuleSetVersion,
            preview.SensitivityRuleSetVersion);
    }

    [TestMethod]
    public void PreviewCollections_AreDefensiveAndMaskMetadataNeverContainsSourceText()
    {
        const string secret = "secret@example.com";
        TranscriptRevision revision = CreateRevision(secret);
        var mutableSelections = new List<CloudTextSelection>
        {
            Selection(revision.Utterances[0]),
        };

        CloudPayloadPreview preview = Build(revision, mutableSelections);
        mutableSelections.Clear();

        Assert.HasCount(1, preview.Segments);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<CloudPayloadPreviewSegment>)preview.Segments).Clear());
        CloudPayloadMaskChange change = preview.Segments[0].MaskChanges.Single();
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)change.ReasonCodes).Clear());
        Assert.IsFalse(JsonSerializer.Serialize(change).Contains(
            secret,
            StringComparison.Ordinal));
        Assert.IsFalse(change.ToString()!.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildPreview_DoesNotInventATextLengthLimit()
    {
        string text = new('あ', 200_000);
        TranscriptRevision revision = CreateRevision(text);

        CloudPayloadPreview preview = Build(
            revision,
            [Selection(revision.Utterances[0])]);

        Assert.AreEqual(text, preview.Segments.Single().OutboundText);
        Assert.AreEqual(text, ConfirmAndParse(preview)
            .GetProperty("segments")[0]
            .GetProperty("text")
            .GetString());
    }

    [TestMethod]
    public void BuildPreview_RejectsUnknownPurposeAndNullSelectionElements()
    {
        TranscriptRevision revision = CreateRevision("safe text");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CloudPayloadBuilder.BuildPreview(
                revision,
                Array.Empty<TermEntry>(),
                (CloudPayloadPurpose)int.MaxValue,
                [Selection(revision.Utterances[0])]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CloudPayloadBuilder.BuildPreview(
                revision,
                Array.Empty<TermEntry>(),
                CloudPayloadPurpose.TextSuggestionEvaluation,
                [null!]));
        AssertFailure(
            CloudPayloadFailureReason.InvalidSelection,
            () => Build(revision, []));
    }

    [TestMethod]
    public void InternalSurface_HasNoSenderCredentialStoreOrMutablePublicApi()
    {
        Assert.IsFalse(typeof(CloudPayloadBuilder).IsPublic);
        Assert.IsTrue(typeof(CloudPayloadBuilder).IsAbstract);
        Assert.IsTrue(typeof(CloudPayloadBuilder).IsSealed);
        CollectionAssert.AreEquivalent(
            new[] { "BuildPreview", "Confirm" },
            typeof(CloudPayloadBuilder)
                .GetMethods(
                    BindingFlags.Static |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(static method => method.IsAssembly && !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());

        Type[] contractTypes =
        [
            typeof(CloudTranscriptTextRange),
            typeof(CloudTextSelection),
            typeof(CloudPayloadMaskChange),
            typeof(CloudPayloadPreviewSegment),
            typeof(CloudPayloadPreview),
            typeof(CloudPayloadConfirmation),
            typeof(ConfirmedCloudPayload),
        ];
        foreach (Type type in contractTypes)
        {
            Assert.IsFalse(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsFalse(type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly).Any(static property =>
                    property.SetMethod is not null), type.Name);
        }

        string surface = string.Join(
            '|',
            contractTypes
                .SelectMany(static type => type
                    .GetMembers(
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly))
                .Select(static member => member.Name));
        foreach (string forbidden in new[]
        {
            "Send",
            "Token",
            "Credential",
            "Client",
            "Store",
            "Persist",
            "ModelRun",
        })
        {
            Assert.IsFalse(surface.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static CloudPayloadPreview Build(
        TranscriptRevision revision,
        IReadOnlyList<CloudTextSelection> selections,
        IReadOnlyList<TermEntry>? dictionary = null) =>
        CloudPayloadBuilder.BuildPreview(
            revision,
            dictionary ?? Array.Empty<TermEntry>(),
            CloudPayloadPurpose.TextSuggestionEvaluation,
            selections);

    private static ConfirmedCloudPayload Confirm(CloudPayloadPreview preview) =>
        CloudPayloadBuilder.Confirm(
            preview,
            new CloudPayloadConfirmation(preview.ConfirmationDigest, ConfirmedAt));

    private static string ConfirmAndGetJson(CloudPayloadPreview preview) =>
        Encoding.UTF8.GetString(Confirm(preview).GetPayloadUtf8());

    private static JsonElement ConfirmAndParse(CloudPayloadPreview preview)
    {
        using JsonDocument document = JsonDocument.Parse(
            Confirm(preview).GetPayloadUtf8());
        return document.RootElement.Clone();
    }

    private static CloudTextSelection Selection(
        Utterance utterance,
        int offset = 0,
        int? length = null,
        CloudTranscriptTextRange? before = null,
        CloudTranscriptTextRange? after = null) =>
        new(
            new CloudTranscriptTextRange(
                utterance.Id,
                offset,
                length ?? utterance.Text.Length - offset),
            before,
            after);

    private static CloudTranscriptTextRange Range(Utterance utterance) =>
        new(utterance.Id, 0, utterance.Text.Length);

    private static TranscriptRevision CreateRevision(string text) =>
        CreateRevision([text]);

    private static TranscriptRevision CreateRevision(
        string text,
        EntityId<TranscriptRevisionIdTag> revisionId,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<SourceSpanIdTag> sourceSpanId)
    {
        EntityId<TranscriptSpeakerIdTag> speakerId =
            EntityId<TranscriptSpeakerIdTag>.New();
        return new TranscriptRevision(
            revisionId,
            parentRevisionId: null,
            TranscriptChangeSource.Human,
            DateTimeOffset.UnixEpoch,
            [CreateUtterance(text, 0, utteranceId, sourceSpanId, speakerId)]);
    }

    private static TranscriptRevision CreateRevision(IReadOnlyList<string> texts)
    {
        EntityId<TranscriptSpeakerIdTag> speakerId =
            EntityId<TranscriptSpeakerIdTag>.New();
        var utterances = new List<Utterance>(texts.Count);
        for (int index = 0; index < texts.Count; index++)
        {
            utterances.Add(CreateUtterance(
                texts[index],
                index,
                EntityId<TranscriptUtteranceIdTag>.New(),
                EntityId<SourceSpanIdTag>.New(),
                speakerId));
        }

        return new TranscriptRevision(
            EntityId<TranscriptRevisionIdTag>.New(),
            parentRevisionId: null,
            TranscriptChangeSource.Human,
            DateTimeOffset.UnixEpoch,
            utterances);
    }

    private static Utterance CreateUtterance(
        string text,
        int index,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<SourceSpanIdTag> sourceSpanId,
        EntityId<TranscriptSpeakerIdTag> speakerId)
    {
        var token = new Token(
            EntityId<TranscriptTokenIdTag>.New(),
            text,
            range: null,
            recognitionConfidence: 0.9);
        var sentence = new Sentence(
            EntityId<TranscriptSentenceIdTag>.New(),
            text,
            [token]);
        return new Utterance(
            utteranceId,
            sourceSpanId,
            speakerId,
            "Speaker 1",
            text,
            new TimeRange(
                new RationalTime(index, 1),
                new RationalTime(1, 1)),
            recognitionConfidence: 0.9,
            [sentence]);
    }

    private static TermEntry CreateTerm(string writtenForm, string reading) =>
        new(
            EntityId<TermEntryIdTag>.New(),
            TermScope.ForProject(
                EntityId<HubRadio.Core.Projects.ProjectIdTag>.New()),
            writtenForm,
            reading,
            version: 0);

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static void AssertFailure(
        CloudPayloadFailureReason expected,
        Action action)
    {
        CloudPayloadException exception =
            Assert.ThrowsExactly<CloudPayloadException>(action);
        Assert.AreEqual(expected, exception.Reason);
    }
}