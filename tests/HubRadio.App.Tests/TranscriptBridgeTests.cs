using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Speakers;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TranscriptBridgeTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private const string RequestId =
        "00112233-4455-6677-8899-aabbccddeeff";

    [TestMethod]
    public async Task ReadAuthorizesBeforeAccessAndReturnsExactReviewEvidence()
    {
        var inaccessibleRuntime = new CountingRuntime();
        var denied = new TranscriptBridgeRequestHandler(
            inaccessibleRuntime,
            new StubAuthorization(allow: false));

        using JsonDocument forbidden = JsonDocument.Parse(
            await denied.HandleAsync(ParseRequest(
                BridgeProtocol.TranscriptReadMethod,
                $$"""{"projectId":"{{Fixture.ProjectId}}"}""")));

        Assert.AreEqual(0, inaccessibleRuntime.CallCount);
        AssertResult(forbidden, "forbidden", hasSnapshot: false);

        Fixture fixture = new();
        using JsonDocument response = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.TranscriptReadMethod,
                $$"""{"projectId":"{{Fixture.ProjectId}}"}"""));

        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        Assert.AreEqual(Fixture.ProjectId, snapshot.GetProperty("projectId").GetString());
        Assert.AreEqual(
            fixture.Snapshot.RevisionDigest.Hex,
            snapshot.GetProperty("revisionDigest").GetString());
        JsonElement tokens = snapshot.GetProperty("utterances")[0]
            .GetProperty("sentences")[0]
            .GetProperty("tokens");
        Assert.AreEqual(0.42, tokens[0].GetProperty("recognitionConfidence").GetDouble());
        Assert.AreEqual(JsonValueKind.Null, tokens[1]
            .GetProperty("recognitionConfidence").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, tokens[1].GetProperty("range").ValueKind);
        Assert.IsTrue(tokens[1]
            .GetProperty("manualTimingAdjustmentRequired").GetBoolean());
        CollectionAssert.AreEqual(
            new[] { "workerReported", "missingRawTiming" },
            tokens[1].GetProperty("timingReviewReasons")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        JsonElement candidate = snapshot.GetProperty("termCandidates")[0];
        Assert.AreEqual("ハブ", candidate.GetProperty("observedText").GetString());
        Assert.AreEqual("Hub", candidate.GetProperty("proposedText").GetString());
        Assert.AreEqual(JsonValueKind.Null, candidate.GetProperty("confidence").ValueKind);
        Assert.IsFalse(response.RootElement.GetRawText().Contains(
            ActorSid,
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TokenEditUsesRevisionAndTextOccAndReportsExactInvalidation()
    {
        Fixture fixture = new();
        string editParameters = fixture.EditParameters(
            fixture.Snapshot.Revision.Id.ToString(),
            fixture.Snapshot.RevisionDigest.Hex,
            "ハブ",
            "Hub");

        using JsonDocument editedResponse = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.TranscriptEditTokenMethod,
                editParameters));
        JsonElement edited = AssertResult(
            editedResponse,
            "available",
            hasSnapshot: true);
        Assert.AreEqual(
            "Hub",
            edited.GetProperty("utterances")[0]
                .GetProperty("sentences")[0]
                .GetProperty("tokens")[0]
                .GetProperty("text")
                .GetString());
        CollectionAssert.AreEqual(
            new[] { "utterance", "sentence", "token" },
            edited.GetProperty("diff")
                .EnumerateArray()
                .Select(static change => change.GetProperty("kind").GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "termCandidates",
                "editorialPhraseCandidates",
                "repetitionCandidates",
                "topicBoundaryCandidates",
                "topicRangeBinding",
                "contextDeviationCandidates",
                "speakerAssignmentBinding",
                "topicMetadata",
                "emphasisCandidates",
            },
            edited.GetProperty("invalidation")
                .GetProperty("staleNodes")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "topicRangeBinding", "speakerAssignmentBinding" },
            edited.GetProperty("invalidation")
                .GetProperty("manualRebindingNodes")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        JsonElement staleRange = edited.GetProperty("staleRange");
        Assert.AreEqual(
            fixture.Snapshot.Revision.Id.ToString(),
            staleRange.GetProperty("sourceRevisionId").GetString());
        Assert.AreEqual(
            fixture.Snapshot.RevisionDigest.Hex,
            staleRange.GetProperty("sourceRevisionDigest").GetString());
        Assert.AreEqual(
            edited.GetProperty("revisionId").GetString(),
            staleRange.GetProperty("revisionId").GetString());
        Assert.AreEqual(
            edited.GetProperty("revisionDigest").GetString(),
            staleRange.GetProperty("revisionDigest").GetString());

        using JsonDocument stale = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.TranscriptEditTokenMethod,
                editParameters));
        AssertResult(stale, "staleBase", hasSnapshot: false);

        TranscriptBridgeSnapshot current = fixture.Runtime.GetRequired(Fixture.ProjectEntityId);
        using JsonDocument conflict = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.TranscriptEditTokenMethod,
                fixture.EditParameters(
                    current.Revision.Id.ToString(),
                    current.RevisionDigest.Hex,
                    "ハブ",
                    "別表記")));
        AssertResult(conflict, "conflict", hasSnapshot: false);

        using JsonDocument noChange = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.TranscriptEditTokenMethod,
                fixture.EditParameters(
                    current.Revision.Id.ToString(),
                    current.RevisionDigest.Hex,
                    "Hub",
                    "Hub")));
        AssertResult(noChange, "noChange", hasSnapshot: false);
    }

    [TestMethod]
    public async Task SpeakerAndTermMutationsUseTheirExactOccSnapshots()
    {
        Fixture speakerFixture = new();
        string common = speakerFixture.BaseParameters();
        string setParameters = $$"""
            { {{common}},"expectedSpeakerRevisionId":null,"speakerId":"{{Fixture.SpeakerId}}","performerId":"{{Fixture.PerformerId}}","surname":"佐藤","displayFormat":"surname","reason":"出演者を確認"}
            """;

        using JsonDocument assignedResponse = JsonDocument.Parse(
            await speakerFixture.HandleAsync(
                BridgeProtocol.TranscriptSetSpeakerMethod,
                setParameters));
        JsonElement assigned = AssertResult(
            assignedResponse,
            "available",
            hasSnapshot: true);
        Assert.AreEqual(
            "佐藤",
            assigned.GetProperty("utterances")[0]
                .GetProperty("speaker")
                .GetProperty("text")
                .GetString());
        Assert.IsNotNull(assigned.GetProperty("speakerRevisionId").GetString());

        using JsonDocument speakerConflict = JsonDocument.Parse(
            await speakerFixture.HandleAsync(
                BridgeProtocol.TranscriptSetSpeakerMethod,
                setParameters));
        AssertResult(speakerConflict, "conflict", hasSnapshot: false);

        Fixture rejectionFixture = new();
        TermCandidate rejectedCandidate = rejectionFixture.Snapshot.TermCandidates.Single();
        using JsonDocument rejectedResponse = JsonDocument.Parse(
            await rejectionFixture.HandleAsync(
                BridgeProtocol.TranscriptRejectTermMethod,
                rejectionFixture.TermDecisionParameters(rejectedCandidate)));
        JsonElement rejected = AssertResult(
            rejectedResponse,
            "available",
            hasSnapshot: true);
        Assert.AreEqual(0, rejected.GetProperty("termCandidates").GetArrayLength());
        Assert.AreEqual(
            "rejected",
            rejected.GetProperty("termDecisions")[0]
                .GetProperty("decision")
                .GetString());
        Assert.AreEqual(
            rejectionFixture.Snapshot.Revision.Id.ToString(),
            rejected.GetProperty("revisionId").GetString());

        Fixture adoptionFixture = new();
        TermCandidate adoptedCandidate = adoptionFixture.Snapshot.TermCandidates.Single();
        using JsonDocument adoptedResponse = JsonDocument.Parse(
            await adoptionFixture.HandleAsync(
                BridgeProtocol.TranscriptAdoptTermMethod,
                adoptionFixture.TermDecisionParameters(adoptedCandidate)));
        JsonElement adopted = AssertResult(
            adoptedResponse,
            "available",
            hasSnapshot: true);
        Assert.AreEqual(
            "adopted",
            adopted.GetProperty("termDecisions")[0]
                .GetProperty("decision")
                .GetString());
        Assert.AreEqual(
            "Hub",
            adopted.GetProperty("utterances")[0]
                .GetProperty("sentences")[0]
                .GetProperty("tokens")[0]
                .GetProperty("text")
                .GetString());
        Assert.AreNotEqual(
            adoptionFixture.Snapshot.Revision.Id.ToString(),
            adopted.GetProperty("revisionId").GetString());
    }

    [TestMethod]
    public void ProtocolAcceptsIntegralExponentAndRejectsExtendedTranscriptParams()
    {
        Fixture fixture = new();
        TermCandidate candidate = fixture.Snapshot.TermCandidates.Single();
        string valid = CreateRequest(
            BridgeProtocol.TranscriptRejectTermMethod,
            fixture.TermDecisionParameters(candidate).Replace(
                "\"termEntryVersion\":3",
                "\"termEntryVersion\":3e0",
                StringComparison.Ordinal));

        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            valid,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNotNull(request);
        Assert.AreEqual(BridgeRequestRejection.None, rejection);

        string extended = valid.Replace(
            "\"reason\":\"候補を判断\"",
            "\"reason\":\"候補を判断\",\"actorSid\":\"S-1-5-18\"",
            StringComparison.Ordinal);
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            extended,
            out _,
            out BridgeRequestRejection extendedRejection));
        Assert.AreEqual(
            BridgeRequestRejection.SchemaViolation,
            extendedRejection);
    }

    [TestMethod]
    public async Task OversizedSuccessDoesNotCommitAHiddenMutation()
    {
        string largePerformerName = new(
            'a',
            (BridgeProtocol.MaxTranscriptResponseUtf8Bytes / 2) + 1024);
        Fixture fixture = new(largePerformerName);
        string parameters = $$"""
            { {{fixture.BaseParameters()}},"expectedSpeakerRevisionId":null,"speakerId":"{{Fixture.SpeakerId}}","performerId":"{{Fixture.PerformerId}}","surname":"佐藤","displayFormat":"fullName","reason":"出演者を確認"}
            """;

        string response = await fixture.HandleAsync(
            BridgeProtocol.TranscriptSetSpeakerMethod,
            parameters);

        using JsonDocument unavailable = JsonDocument.Parse(response);
        AssertResult(unavailable, "unavailable", hasSnapshot: false);
        Assert.IsNull(fixture.Runtime.GetRequired(Fixture.ProjectEntityId).SpeakerHistory);
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxTranscriptResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(response));
    }

    private static BridgeRequest ParseRequest(string method, string parameters)
    {
        Assert.IsTrue(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            CreateRequest(method, parameters),
            out BridgeRequest? request,
            out BridgeRequestRejection rejection),
            rejection.ToString());
        return request!;
    }

    private static string CreateRequest(string method, string parameters) =>
        $$"""{"version":1,"requestId":"{{RequestId}}","method":"{{method}}","params":{{parameters}}}""";

    private static JsonElement AssertResult(
        JsonDocument response,
        string status,
        bool hasSnapshot)
    {
        JsonElement result = response.RootElement.GetProperty("result");
        CollectionAssert.AreEquivalent(
            new[] { "status", "snapshot" },
            result.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(status, result.GetProperty("status").GetString());
        Assert.AreEqual(
            hasSnapshot ? JsonValueKind.Object : JsonValueKind.Null,
            result.GetProperty("snapshot").ValueKind);
        return result.GetProperty("snapshot");
    }

    private sealed class Fixture
    {
        internal const string ProjectId =
            "10000000-0000-4000-8000-000000000001";
        internal const string SpeakerId =
            "10000000-0000-4000-8000-000000000002";
        internal const string PerformerId =
            "10000000-0000-4000-8000-000000000003";
        internal const string TokenId =
            "10000000-0000-4000-8000-000000000004";

        internal static readonly EntityId<ProjectIdTag> ProjectEntityId =
            Id<ProjectIdTag>(ProjectId);

        private static readonly DateTimeOffset CreatedAt =
            new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        private readonly TranscriptBridgeRequestHandler handler;

        internal Fixture(string performerDisplayName = "佐藤 花子")
        {
            var performer = new Performer(
                Id<PerformerIdTag>(PerformerId),
            performerDisplayName,
                "司会",
                CreatedAt.AddDays(-1));
            var project = new Project(
                ProjectEntityId,
                "U-008 test",
                "Verify transcript editing.",
                "Test runner",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: [performer]);
            var revisionId = Id<TranscriptRevisionIdTag>(
                "10000000-0000-4000-8000-000000000005");
            var spanId = Id<SourceSpanIdTag>(
                "10000000-0000-4000-8000-000000000006");
            var speakerId = Id<TranscriptSpeakerIdTag>(SpeakerId);
            var timed = new Token(
                Id<TranscriptTokenIdTag>(TokenId),
                "ハブ",
                Range(0, 1),
                recognitionConfidence: 0.42);
            var untimed = new Token(
                Id<TranscriptTokenIdTag>(
                    "10000000-0000-4000-8000-000000000007"),
                "未時刻",
                range: null,
                recognitionConfidence: null);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(
                    "10000000-0000-4000-8000-000000000008"),
                "ハブ 未時刻",
                [timed, untimed]);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    "10000000-0000-4000-8000-000000000009"),
                spanId,
                speakerId,
                "Speaker 1",
                "ハブ 未時刻",
                Range(0, 2),
                recognitionConfidence: null,
                [sentence]);
            var revision = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [utterance]);
            var span = new SourceSpan(
                spanId,
                Id<AssetIdTag>(
                    "10000000-0000-4000-8000-00000000000a"),
                "audio:1",
                utterance.Range,
                new ContentDigest(new string('a', 64)),
                revision.Id,
                [timed.Id, untimed.Id]);
            TranscriptTimingEvidence[] timing =
            [
                new(
                    utterance.Id,
                    span.Id,
                    tokenId: null,
                    rawStartSeconds: 0,
                    rawEndSeconds: 2,
                    roundedStartSample: 0,
                    roundedEndSample: 32000,
                    startWasRounded: false,
                    endWasRounded: false,
                    TranscriptTimingReviewReason.None),
                new(
                    utterance.Id,
                    span.Id,
                    timed.Id,
                    rawStartSeconds: 0,
                    rawEndSeconds: 1,
                    roundedStartSample: 0,
                    roundedEndSample: 16000,
                    startWasRounded: false,
                    endWasRounded: false,
                    TranscriptTimingReviewReason.None),
                new(
                    utterance.Id,
                    span.Id,
                    untimed.Id,
                    rawStartSeconds: null,
                    rawEndSeconds: null,
                    roundedStartSample: null,
                    roundedEndSample: null,
                    startWasRounded: false,
                    endWasRounded: false,
                    TranscriptTimingReviewReason.WorkerReported |
                        TranscriptTimingReviewReason.MissingRawTiming),
            ];
            TermScope scope = TermScope.ForProject(project.Id);
            var term = new TermEntry(
                Id<TermEntryIdTag>(
                    "10000000-0000-4000-8000-00000000000b"),
                scope,
                "Hub",
                "ハブ",
                version: 3);
            Snapshot = TranscriptBridgeSnapshot.Create(
                project,
                revision,
                [span],
                timing,
                [term],
                scope,
                Id<ModelRunIdTag>(
                    "10000000-0000-4000-8000-00000000000c"),
                speakerHistory: null);
            var ids = new Queue<Guid>(
            [
                Guid.Parse("20000000-0000-4000-8000-000000000001"),
                Guid.Parse("20000000-0000-4000-8000-000000000002"),
                Guid.Parse("20000000-0000-4000-8000-000000000003"),
            ]);
            Runtime = new TranscriptBridgeRuntime(
                () => CreatedAt.AddMinutes(1),
                () => ids.Dequeue());
            Runtime.Register(Snapshot);
            handler = new TranscriptBridgeRequestHandler(
                Runtime,
                new StubAuthorization(allow: true));
        }

        internal TranscriptBridgeSnapshot Snapshot { get; }

        internal TranscriptBridgeRuntime Runtime { get; }

        internal Task<string> HandleAsync(string method, string parameters) =>
            handler.HandleAsync(ParseRequest(method, parameters));

        internal string BaseParameters() =>
            $"\"projectId\":\"{ProjectId}\",\"revisionId\":\"{Snapshot.Revision.Id}\",\"digest\":\"{Snapshot.RevisionDigest.Hex}\"";

        internal string EditParameters(
            string revisionId,
            string digest,
            string expectedText,
            string revisedText) =>
            $$"""{"projectId":"{{ProjectId}}","revisionId":"{{revisionId}}","digest":"{{digest}}","tokenId":"{{TokenId}}","expectedText":"{{expectedText}}","revisedText":"{{revisedText}}","reason":"認識結果を修正"}""";

        internal string TermDecisionParameters(TermCandidate candidate) =>
            $$"""{ {{BaseParameters()}},"candidateId":"{{candidate.Suggestion.Id}}","termEntryId":"{{candidate.TermEntryId}}","termEntryVersion":{{candidate.TermEntryVersion}},"reason":"候補を判断"}""";

        private static EntityId<TTag> Id<TTag>(string value) =>
            EntityId<TTag>.Parse(value);

        private static TimeRange Range(long start, long duration) =>
            new(new RationalTime(start, 1), new RationalTime(duration, 1));
    }

    private sealed class StubAuthorization(bool allow) :
        ITranscriptBridgeAuthorization
    {
        public string DemandRead()
        {
            Demand();
            return ActorSid;
        }

        public string DemandMutation()
        {
            Demand();
            return ActorSid;
        }

        private void Demand()
        {
            if (!allow)
            {
                throw new HubRadio.App.Security.AuthorizationException(
                    HubRadio.Core.Security.AuthorizationOperation.ViewProject,
                    actingRole: null);
            }
        }
    }

    private sealed class CountingRuntime : ITranscriptBridgeRuntime
    {
        internal int CallCount { get; private set; }

        public TranscriptBridgeOperationResult Execute(
            BridgeRequest request,
            string actorSid,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return TranscriptBridgeOperationResult.Unavailable();
        }
    }
}