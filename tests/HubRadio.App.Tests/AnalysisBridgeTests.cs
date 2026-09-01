using System.Text;
using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Policy;
using HubRadio.App.Security;
using HubRadio.App.Speakers;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Terms;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AnalysisBridgeTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private const string RequestId =
        "00112233-4455-6677-8899-aabbccddeeff";

    [TestMethod]
    public async Task AuthorizationPrecedesRuntimeLookupAndMissingProductionDataIsUnavailable()
    {
        string projectId = Fixture.ProjectId;
        string revisionId = "10000000-0000-4000-8000-000000000099";
        string digest = new('a', 64);
        string candidateId = "10000000-0000-4000-8000-000000000098";
        var runtime = new CountingRuntime();
        var authorization = new StubAuthorization(allow: false);
        var denied = new AnalysisBridgeRequestHandler(runtime, authorization);

        using JsonDocument forbiddenRead = JsonDocument.Parse(
            await denied.HandleAsync(ParseRequest(
                BridgeProtocol.AnalysisReadMethod,
                $$"""{"projectId":"{{projectId}}"}""")));
        using JsonDocument forbiddenMutation = JsonDocument.Parse(
            await denied.HandleAsync(ParseRequest(
                BridgeProtocol.AnalysisAdoptMethod,
                $$"""{"projectId":"{{projectId}}","revisionId":"{{revisionId}}","digest":"{{digest}}","candidateId":"{{candidateId}}","reason":"候補を採用"}""")));

        AssertResult(forbiddenRead, "forbidden", hasSnapshot: false);
        AssertResult(forbiddenMutation, "forbidden", hasSnapshot: false);
        Assert.AreEqual(0, runtime.CallCount);
        Assert.AreEqual(1, authorization.ReadDemandCount);
        Assert.AreEqual(1, authorization.MutationDemandCount);

        var productionRuntime = new AnalysisBridgeRuntime();
        var production = new AnalysisBridgeRequestHandler(
            productionRuntime,
            new StubAuthorization(allow: true));
        using JsonDocument unavailable = JsonDocument.Parse(
            await production.HandleAsync(ParseRequest(
                BridgeProtocol.AnalysisReadMethod,
                $$"""{"projectId":"{{projectId}}"}""")));

        AssertResult(unavailable, "unavailable", hasSnapshot: false);
        AssertPrivateDetailAbsent(unavailable.RootElement.GetRawText());
    }

    [TestMethod]
    public void ProtocolAcceptsOnlyClosedAnalysisRequestsAndRejectsNativeTargets()
    {
        string projectId = Fixture.ProjectId;
        string revisionId = "10000000-0000-4000-8000-000000000099";
        string candidateId = "10000000-0000-4000-8000-000000000098";
        string topicId = "10000000-0000-4000-8000-000000000097";
        string topicRevisionId = "10000000-0000-4000-8000-000000000096";
        string targetId = "10000000-0000-4000-8000-000000000095";
        string digest = new('a', 64);
        string adopt =
            $$"""{"projectId":"{{projectId}}","revisionId":"{{revisionId}}","digest":"{{digest}}","candidateId":"{{candidateId}}","reason":"候補を採用"}""";
        string update =
            $$"""{"projectId":"{{projectId}}","revisionId":"{{revisionId}}","digest":"{{digest}}","topicId":"{{topicId}}","expectedTopicRevisionId":"{{topicRevisionId}}","update":{"field":"title","text":"更新後"},"reason":"題名を更新"}""";

        Assert.AreEqual(
            BridgeMethod.AnalysisRead,
            ParseRequest(
                BridgeProtocol.AnalysisReadMethod,
                $$"""{"projectId":"{{projectId}}"}""").Method);
        Assert.AreEqual(
            BridgeMethod.AnalysisAdopt,
            ParseRequest(BridgeProtocol.AnalysisAdoptMethod, adopt).Method);
        Assert.AreEqual(
            BridgeMethod.AnalysisUpdateTopic,
            ParseRequest(BridgeProtocol.AnalysisUpdateTopicMethod, update).Method);
        Assert.AreEqual(
            BridgeProtocol.MaxAnalysisResponseUtf8Bytes,
            BridgeProtocol.GetMaximumResponseUtf8Bytes(
                BridgeMethod.AnalysisRead));

        string[] injectedAdoptionTargets =
        [
            adopt.Replace(
                "\"reason\":\"候補を採用\"",
                $"\"reason\":\"候補を採用\",\"elementId\":\"{targetId}\"",
                StringComparison.Ordinal),
            adopt.Replace(
                "\"reason\":\"候補を採用\"",
                "\"reason\":\"候補を採用\",\"target\":{\"kind\":\"delete\"}",
                StringComparison.Ordinal),
        ];
        foreach (string parameters in injectedAdoptionTargets)
        {
            AssertRejected(
                BridgeProtocol.AnalysisAdoptMethod,
                parameters,
                BridgeRequestRejection.SchemaViolation);
        }

        AssertRejected(
            BridgeProtocol.AnalysisUpdateTopicMethod,
            update.Replace(
                "\"text\":\"更新後\"",
                $"\"text\":\"更新後\",\"elementId\":\"{targetId}\"",
                StringComparison.Ordinal),
            BridgeRequestRejection.SchemaViolation);
        AssertRejected(
            BridgeProtocol.AnalysisReadMethod,
            $$"""{"projectId":"{{projectId}}","actorSid":"S-1-5-18"}""",
            BridgeRequestRejection.SchemaViolation);
    }

    [TestMethod]
    public async Task ReadSerializesAllProducerEvidenceNavigationAndSafeSensitivity()
    {
        Fixture fixture = new();

        string responseText = await fixture.HandleAsync(
            BridgeProtocol.AnalysisReadMethod,
            $$"""{"projectId":"{{Fixture.ProjectId}}"}""");
        using JsonDocument response = JsonDocument.Parse(responseText);
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);

        Assert.AreEqual(Fixture.ProjectId, snapshot.GetProperty("projectId").GetString());
        Assert.AreEqual(
            fixture.Snapshot.RevisionDigest.Hex,
            snapshot.GetProperty("revisionDigest").GetString());
        Assert.AreEqual("notPersisted", snapshot
            .GetProperty("adoptionPersistence")
            .GetString());
        Assert.AreEqual(0, snapshot.GetProperty("stagedChanges").GetArrayLength());
        Assert.HasCount(2, snapshot.GetProperty("speakers").EnumerateArray());
        Assert.HasCount(1, snapshot.GetProperty("topics").EnumerateArray());
        Assert.IsTrue(snapshot.GetProperty("speakers")
            .EnumerateArray()
            .All(static speaker =>
                speaker.GetProperty("source").GetProperty("range").ValueKind ==
                    JsonValueKind.Object));
        Assert.AreEqual(
            JsonValueKind.Object,
            snapshot.GetProperty("topics")[0].GetProperty("range").ValueKind);

        JsonElement[] candidates = snapshot.GetProperty("candidates")
            .EnumerateArray()
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "termCorrection",
                "silence",
                "editorialPhrase",
                "repetition",
                "topicBoundary",
                "audioReview",
                "contextDeviation",
                "emphasis",
            },
            candidates.Select(static candidate =>
                candidate.GetProperty("producer").GetString()).ToArray());
        Assert.IsTrue(candidates.All(static candidate =>
            candidate.GetProperty("producer").GetString() ==
                candidate.GetProperty("payload").GetProperty("producer").GetString()));
        Assert.IsTrue(candidates.All(static candidate =>
            candidate.GetProperty("confidence").ValueKind == JsonValueKind.Null));
        Assert.IsTrue(candidates.All(static candidate =>
            candidate.GetProperty("freshness").GetString() == "current"));
        Assert.IsTrue(candidates.All(static candidate =>
            candidate.GetProperty("source").GetProperty("range").ValueKind ==
                JsonValueKind.Object));

        Assert.AreEqual(
            "ハブ",
            FindCandidate(candidates, "termCorrection")
                .GetProperty("payload")
                .GetProperty("observedText")
                .GetString());
        Assert.IsFalse(FindCandidate(candidates, "silence")
            .GetProperty("payload")
            .GetProperty("productionApproved")
            .GetBoolean());
        Assert.AreEqual(
            "ここはカット",
            FindCandidate(candidates, "editorialPhrase")
                .GetProperty("payload")
                .GetProperty("matchedPhrase")
                .GetString());
        Assert.IsTrue(FindCandidate(candidates, "repetition")
            .GetProperty("payload")
            .GetProperty("features")
            .GetProperty("retentionChoiceRequired")
            .GetBoolean());
        Assert.AreEqual(
            "3",
            FindCandidate(candidates, "topicBoundary")
                .GetProperty("payload")
                .GetProperty("suggestedBoundary")
                .GetProperty("value")
                .GetString());
        JsonElement technical = FindCandidate(candidates, "audioReview");
        Assert.AreEqual("technicalWarning", technical
            .GetProperty("displayKind")
            .GetString());
        Assert.IsFalse(technical.GetProperty("canAdopt").GetBoolean());
        Assert.IsTrue(technical.GetProperty("canReject").GetBoolean());
        Assert.AreEqual("recordingQuality", technical
            .GetProperty("payload")
            .GetProperty("eventKind")
            .GetString());
        Assert.AreEqual("leadingBeforeFirstTopic", FindCandidate(
                candidates,
                "contextDeviation")
            .GetProperty("payload")
            .GetProperty("features")
            .GetProperty("position")
            .GetString());
        CollectionAssert.Contains(
            FindCandidate(candidates, "emphasis")
                .GetProperty("payload")
                .GetProperty("reasons")
                .EnumerateArray()
                .Select(static reason => reason.GetString())
                .ToArray(),
            "importantStatement");

        JsonElement sensitivity = snapshot.GetProperty("sensitivity");
        Assert.IsTrue(sensitivity.GetProperty("isComplete").GetBoolean());
        Assert.IsTrue(sensitivity
            .GetProperty("requiresExternalOutputWarning")
            .GetBoolean());
        JsonElement credential = sensitivity.GetProperty("findings")
            .EnumerateArray()
            .Single(static finding =>
                finding.GetProperty("kind").GetString() == "credentialPattern");
        CollectionAssert.AreEquivalent(
            new[]
            {
                "utteranceId",
                "sourceSpanId",
                "kind",
                "reasonCode",
                "sourceRange",
            },
            credential.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        Assert.AreEqual(JsonValueKind.Object, credential
            .GetProperty("sourceRange")
            .ValueKind);
        Assert.IsFalse(responseText.Contains(
            Fixture.SensitiveMatchedValue,
            StringComparison.Ordinal));
        Assert.IsFalse(responseText.Contains(ActorSid, StringComparison.Ordinal));
        AssertPrivateDetailAbsent(responseText);
    }

    [TestMethod]
    public async Task CandidateDecisionsEnforceSnapshotOccAndCandidateIdentity()
    {
        Fixture fixture = new();
        EntityId<SuggestionIdTag> termId = fixture.CandidateId(
            SuggestionProducerKind.TermCorrection);
        string staleRevision = "10000000-0000-4000-8000-000000000099";
        string staleParameters = fixture.DecisionParameters(
            fixture.Snapshot,
            termId,
            revisionId: staleRevision);

        using JsonDocument stale = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisRejectMethod,
                staleParameters));
        AssertResult(stale, "staleBase", hasSnapshot: false);
        Assert.AreEqual(0, fixture.Current.DecisionHistory.Count);

        using JsonDocument conflict = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisRejectMethod,
                fixture.DecisionParameters(
                    fixture.Current,
                    Id<SuggestionIdTag>(
                        "10000000-0000-4000-8000-000000000097"))));
        AssertResult(conflict, "conflict", hasSnapshot: false);
        Assert.AreEqual(0, fixture.Current.DecisionHistory.Count);

        string acceptedParameters = fixture.DecisionParameters(
            fixture.Current,
            termId);
        using JsonDocument accepted = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisRejectMethod,
                acceptedParameters));
        JsonElement updated = AssertResult(
            accepted,
            "available",
            hasSnapshot: true);
        Assert.AreEqual("rejected", FindCandidate(
                updated.GetProperty("candidates").EnumerateArray().ToArray(),
                "termCorrection")
            .GetProperty("status")
            .GetString());

        using JsonDocument replay = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisRejectMethod,
                acceptedParameters));
        AssertResult(replay, "staleBase", hasSnapshot: false);
        Assert.HasCount(1, fixture.Current.DecisionHistory);
    }

    [TestMethod]
    public async Task TechnicalWarningCannotBeAdoptedButCanBeExplicitlyRejected()
    {
        Fixture fixture = new();
        EntityId<SuggestionIdTag> technicalId = fixture.CandidateId(
            SuggestionProducerKind.AudioReview);

        using JsonDocument adoption = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisAdoptMethod,
                fixture.DecisionParameters(fixture.Current, technicalId)));
        AssertResult(adoption, "conflict", hasSnapshot: false);
        Assert.AreEqual(0, fixture.Current.DecisionHistory.Count);
        Assert.AreEqual(0, fixture.Current.StagedChanges.Count);

        using JsonDocument rejection = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisRejectMethod,
                fixture.DecisionParameters(fixture.Current, technicalId)));
        JsonElement snapshot = AssertResult(
            rejection,
            "available",
            hasSnapshot: true);
        JsonElement technical = FindCandidate(
            snapshot.GetProperty("candidates").EnumerateArray().ToArray(),
            "audioReview");
        Assert.AreEqual("rejected", technical.GetProperty("status").GetString());
        Assert.AreEqual("rejected", technical
            .GetProperty("decision")
            .GetProperty("decision")
            .GetString());
        Assert.IsFalse(technical.GetProperty("canAdopt").GetBoolean());
        Assert.IsFalse(technical.GetProperty("canReject").GetBoolean());
        Assert.AreEqual(0, snapshot.GetProperty("stagedChanges").GetArrayLength());
        Assert.AreEqual(0, fixture.Current.StagedChanges.Count);
    }

    [TestMethod]
    public async Task AdoptionsUseNativeTypedTargetsAndRemainExplicitlyUnpersisted()
    {
        Fixture fixture = new();
        (SuggestionProducerKind Producer, string StagedKind)[] decisions =
        [
            (SuggestionProducerKind.TermCorrection, "transcriptRevision"),
            (SuggestionProducerKind.Silence, "editOperation"),
            (SuggestionProducerKind.TopicBoundary, "editOperation"),
            (SuggestionProducerKind.Emphasis, "cue"),
        ];

        for (int index = 0; index < decisions.Length; index++)
        {
            AnalysisBridgeSnapshot current = fixture.Current;
            EntityId<SuggestionIdTag> candidateId = fixture.CandidateId(
                decisions[index].Producer);
            using JsonDocument response = JsonDocument.Parse(
                await fixture.HandleAsync(
                    BridgeProtocol.AnalysisAdoptMethod,
                    fixture.DecisionParameters(current, candidateId)));
            JsonElement snapshot = AssertResult(
                response,
                "available",
                hasSnapshot: true);
            JsonElement[] staged = snapshot.GetProperty("stagedChanges")
                .EnumerateArray()
                .ToArray();
            Assert.HasCount(index + 1, staged);
            JsonElement change = staged[^1];
            Assert.AreEqual(
                decisions[index].StagedKind,
                change.GetProperty("kind").GetString());
            Assert.IsFalse(change.GetProperty("persisted").GetBoolean());
            Assert.AreEqual(
                "notPersisted",
                snapshot.GetProperty("adoptionPersistence").GetString());

            JsonElement candidate = snapshot.GetProperty("candidates")
                .EnumerateArray()
                .Single(item =>
                    item.GetProperty("candidateId").GetString() ==
                        candidateId.ToString());
            Assert.AreEqual("adopted", candidate.GetProperty("status").GetString());
        }

        JsonElement[] finalChanges = ReadSnapshot(
                await fixture.HandleAsync(
                    BridgeProtocol.AnalysisReadMethod,
                    $$"""{"projectId":"{{Fixture.ProjectId}}"}"""))
            .GetProperty("stagedChanges")
            .EnumerateArray()
            .ToArray();
        Assert.AreEqual(
            fixture.DeleteElementId.ToString(),
            finalChanges[1]
                .GetProperty("operation")
                .GetProperty("elementId")
                .GetString());
        Assert.AreEqual(
            fixture.BoundaryElementId.ToString(),
            finalChanges[2]
                .GetProperty("operation")
                .GetProperty("elementId")
                .GetString());
        Assert.AreEqual("split", finalChanges[2]
            .GetProperty("operation")
            .GetProperty("kind")
            .GetString());
        Assert.AreEqual(
            fixture.RightElementId.ToString(),
            finalChanges[2]
                .GetProperty("operation")
                .GetProperty("rightElementId")
                .GetString());
        Assert.AreEqual(
            fixture.CueId.ToString(),
            finalChanges[3]
                .GetProperty("cue")
                .GetProperty("cueId")
                .GetString());
        Assert.AreEqual("ハブ", fixture.Revision.Utterances[0].Tokens[0].Text);
        Assert.AreEqual(0, fixture.Snapshot.StagedChanges.Count);
        Assert.HasCount(4, fixture.Current.StagedChanges);
        Assert.IsTrue(fixture.Current.StagedChanges.All(static change =>
            !change.Persisted));
    }

    [TestMethod]
    public async Task TopicUpdateChangesOneFieldWithMetadataOccAndStagesNoPersistence()
    {
        Fixture fixture = new();
        string originalAudience = fixture.Topic.Metadata.Metadata.Audience;
        EntityId<TopicMetadataRevisionIdTag> wrongRevision =
            Id<TopicMetadataRevisionIdTag>(
                "10000000-0000-4000-8000-000000000094");

        using JsonDocument conflict = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisUpdateTopicMethod,
                fixture.TopicUpdateParameters(
                    fixture.Current,
                    wrongRevision,
                    "{\"field\":\"title\",\"text\":\"更新後の題名\"}")));
        AssertResult(conflict, "conflict", hasSnapshot: false);
        Assert.AreEqual(fixture.Topic.Metadata.Id, fixture.Current.Topics[0].Metadata.Id);

        string acceptedParameters = fixture.TopicUpdateParameters(
            fixture.Current,
            fixture.Topic.Metadata.Id,
            "{\"field\":\"title\",\"text\":\"更新後の題名\"}");
        using JsonDocument accepted = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisUpdateTopicMethod,
                acceptedParameters));
        JsonElement snapshot = AssertResult(
            accepted,
            "available",
            hasSnapshot: true);
        JsonElement topic = snapshot.GetProperty("topics")[0];
        Assert.AreEqual("更新後の題名", topic.GetProperty("title").GetString());
        Assert.AreEqual(originalAudience, topic.GetProperty("audience").GetString());
        Assert.AreEqual("title", topic.GetProperty("changedField").GetString());
        Assert.AreEqual(
            fixture.Topic.Metadata.Id.ToString(),
            topic.GetProperty("parentMetadataRevisionId").GetString());
        JsonElement staged = snapshot.GetProperty("stagedChanges")[0];
        Assert.AreEqual(
            "topicMetadataRevision",
            staged.GetProperty("kind").GetString());
        Assert.IsFalse(staged.GetProperty("persisted").GetBoolean());
        Assert.AreEqual("notPersisted", snapshot
            .GetProperty("adoptionPersistence")
            .GetString());
        Assert.AreEqual("stale", FindCandidate(
                snapshot.GetProperty("candidates").EnumerateArray().ToArray(),
                "emphasis")
            .GetProperty("freshness")
            .GetString());

        using JsonDocument replay = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisUpdateTopicMethod,
                acceptedParameters));
        AssertResult(replay, "staleBase", hasSnapshot: false);

        AnalysisBridgeSnapshot current = fixture.Current;
        using JsonDocument noChange = JsonDocument.Parse(
            await fixture.HandleAsync(
                BridgeProtocol.AnalysisUpdateTopicMethod,
                fixture.TopicUpdateParameters(
                    current,
                    current.Topics[0].Metadata.Id,
                    "{\"field\":\"title\",\"text\":\"更新後の題名\"}")));
        AssertResult(noChange, "noChange", hasSnapshot: false);
        Assert.HasCount(1, fixture.Current.StagedChanges);
    }

    [TestMethod]
    public async Task OversizedSuccessDoesNotCommitAHiddenAnalysisMutation()
    {
        string largePerformerName = new(
            'x',
            BridgeProtocol.MaxAnalysisResponseUtf8Bytes + 1024);
        Fixture fixture = new(largePerformerName);
        EntityId<SuggestionIdTag> candidateId = fixture.CandidateId(
            SuggestionProducerKind.TermCorrection);
        EntityId<AnalysisBridgeRevisionIdTag> originalRevision =
            fixture.Snapshot.RevisionId;
        ContentDigest originalDigest = fixture.Snapshot.RevisionDigest;

        string responseText = await fixture.HandleAsync(
            BridgeProtocol.AnalysisRejectMethod,
            fixture.DecisionParameters(fixture.Current, candidateId));

        using JsonDocument response = JsonDocument.Parse(responseText);
        AssertResult(response, "unavailable", hasSnapshot: false);
        Assert.AreEqual(originalRevision, fixture.Current.RevisionId);
        Assert.AreEqual(originalDigest, fixture.Current.RevisionDigest);
        Assert.AreEqual(0, fixture.Current.DecisionHistory.Count);
        Assert.AreEqual(0, fixture.Current.StagedChanges.Count);
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxAnalysisResponseUtf8Bytes,
            Encoding.UTF8.GetByteCount(responseText));
        Assert.IsFalse(responseText.Contains(largePerformerName, StringComparison.Ordinal));
        AssertPrivateDetailAbsent(responseText);
    }

    private static JsonElement FindCandidate(
        IReadOnlyList<JsonElement> candidates,
        string producer) =>
        candidates.Single(candidate =>
            candidate.GetProperty("producer").GetString() == producer);

    private static JsonElement ReadSnapshot(string responseText)
    {
        using JsonDocument response = JsonDocument.Parse(responseText);
        JsonElement snapshot = AssertResult(
            response,
            "available",
            hasSnapshot: true);
        return snapshot.Clone();
    }

    private static BridgeRequest ParseRequest(string method, string parameters)
    {
        bool accepted = BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            CreateRequest(method, parameters),
            out BridgeRequest? request,
            out BridgeRequestRejection rejection);
        Assert.IsTrue(accepted, rejection.ToString());
        Assert.IsNotNull(request);
        return request;
    }

    private static void AssertRejected(
        string method,
        string parameters,
        BridgeRequestRejection expected)
    {
        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            CreateRequest(method, parameters),
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(expected, rejection);
    }

    private static string CreateRequest(string method, string parameters) =>
        $$"""{"version":1,"requestId":"{{RequestId}}","method":"{{method}}","params":{{parameters}}}""";

    private static JsonElement AssertResult(
        JsonDocument response,
        string status,
        bool hasSnapshot)
    {
        JsonElement root = response.RootElement;
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        Assert.AreEqual(RequestId, root.GetProperty("requestId").GetString());
        JsonElement result = root.GetProperty("result");
        CollectionAssert.AreEquivalent(
            new[] { "status", "snapshot" },
            result.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        Assert.AreEqual(status, result.GetProperty("status").GetString());
        Assert.AreEqual(
            hasSnapshot ? JsonValueKind.Object : JsonValueKind.Null,
            result.GetProperty("snapshot").ValueKind);
        return result.GetProperty("snapshot");
    }

    private static void AssertPrivateDetailAbsent(string response)
    {
        Assert.IsFalse(response.Contains("C:\\", StringComparison.Ordinal));
        Assert.IsFalse(response.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(response.Contains("Exception", StringComparison.OrdinalIgnoreCase));
    }

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private sealed class Fixture
    {
        internal const string ProjectId =
            "a1000000-0000-4000-8000-000000000001";
        internal const string SensitiveMatchedValue = "TopSecret";

        private static readonly DateTimeOffset CreatedAt =
            new(2044, 7, 8, 9, 10, 11, TimeSpan.Zero);

        private readonly AnalysisBridgeRequestHandler handler;

        internal Fixture(string secondPerformerDisplayName = "Guest Beta")
        {
            EntityId<ProjectIdTag> projectId = Id<ProjectIdTag>(ProjectId);
            EntityId<AssetIdTag> sourceAssetId = Id<AssetIdTag>(
                "a1000000-0000-4000-8000-000000000002");
            var sourceContentDigest = new ContentDigest(new string('a', 64));
            EntityId<TranscriptRevisionIdTag> revisionId =
                Id<TranscriptRevisionIdTag>(
                    "a1000000-0000-4000-8000-000000000003");
            EntityId<TranscriptSpeakerIdTag> firstSpeakerId =
                Id<TranscriptSpeakerIdTag>(
                    "a1000000-0000-4000-8000-000000000004");
            EntityId<TranscriptSpeakerIdTag> secondSpeakerId =
                Id<TranscriptSpeakerIdTag>(
                    "a1000000-0000-4000-8000-000000000005");
            var firstPerformer = new Performer(
                Id<PerformerIdTag>(
                    "a1000000-0000-4000-8000-000000000006"),
                "Host Alpha",
                "Host",
                CreatedAt.AddDays(-1));
            var secondPerformer = new Performer(
                Id<PerformerIdTag>(
                    "a1000000-0000-4000-8000-000000000007"),
                secondPerformerDisplayName,
                "Guest",
                titleConfirmedAt: null);
            Project = new Project(
                projectId,
                "U-016 native analysis test",
                "Verify the integrated analysis bridge.",
                "Internal editors",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: [firstPerformer, secondPerformer]);

            var firstToken = new Token(
                Id<TranscriptTokenIdTag>(
                    "a1000000-0000-4000-8000-000000000008"),
                "ハブ",
                Range(0, 1),
                recognitionConfidence: 0.8);
            var sensitiveToken = new Token(
                Id<TranscriptTokenIdTag>(
                    "a1000000-0000-4000-8000-000000000009"),
                $"password={SensitiveMatchedValue}",
                Range(1, 1),
                recognitionConfidence: null);
            var editorialToken = new Token(
                Id<TranscriptTokenIdTag>(
                    "a1000000-0000-4000-8000-00000000000a"),
                "ここはカット",
                Range(3, 1),
                recognitionConfidence: 0.9);
            EntityId<SourceSpanIdTag> firstSpanId = Id<SourceSpanIdTag>(
                "a1000000-0000-4000-8000-00000000000b");
            EntityId<SourceSpanIdTag> secondSpanId = Id<SourceSpanIdTag>(
                "a1000000-0000-4000-8000-00000000000c");
            var firstUtterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    "a1000000-0000-4000-8000-00000000000d"),
                firstSpanId,
                firstSpeakerId,
                "Speaker 1",
                $"ハブ password={SensitiveMatchedValue}",
                Range(0, 2),
                recognitionConfidence: null,
                [new Sentence(
                    Id<TranscriptSentenceIdTag>(
                        "a1000000-0000-4000-8000-00000000000e"),
                    $"ハブ password={SensitiveMatchedValue}",
                    [firstToken, sensitiveToken])]);
            var secondUtterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    "a1000000-0000-4000-8000-00000000000f"),
                secondSpanId,
                secondSpeakerId,
                "Speaker 2",
                "ここはカット",
                Range(3, 1),
                recognitionConfidence: 0.9,
                [new Sentence(
                    Id<TranscriptSentenceIdTag>(
                        "a1000000-0000-4000-8000-000000000010"),
                    "ここはカット",
                    [editorialToken])]);
            Revision = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [firstUtterance, secondUtterance]);
            SourceSpans =
            [
                new SourceSpan(
                    firstSpanId,
                    sourceAssetId,
                    "audio:1",
                    firstUtterance.Range,
                    sourceContentDigest,
                    revisionId,
                    [firstToken.Id, sensitiveToken.Id]),
                new SourceSpan(
                    secondSpanId,
                    sourceAssetId,
                    "audio:1",
                    secondUtterance.Range,
                    sourceContentDigest,
                    revisionId,
                    [editorialToken.Id]),
            ];
            ContentDigest revisionDigest =
                AnalysisBridgeSnapshot.ComputeTranscriptDigest(Revision);
            TermScope termScope = TermScope.ForProject(projectId);
            TermEntry = new TermEntry(
                Id<TermEntryIdTag>(
                    "a1000000-0000-4000-8000-000000000011"),
                termScope,
                "Hub",
                "ハブ",
                version: 3);
            EntityId<ModelRunIdTag> modelRunId = Id<ModelRunIdTag>(
                "a1000000-0000-4000-8000-000000000012");

            SpeakerAssignmentHistory assignments =
                SpeakerAssignmentService.SetAssignment(
                    Project,
                    Revision,
                    history: null,
                    Id<SpeakerAssignmentRevisionIdTag>(
                        "a1000000-0000-4000-8000-000000000013"),
                    firstSpeakerId,
                    firstPerformer.Id,
                    "Alpha",
                    SpeakerDisplayFormat.FullName,
                    ActorSid,
                    CreatedAt,
                    "Assign the host.");
            assignments = SpeakerAssignmentService.SetAssignment(
                Project,
                Revision,
                assignments,
                Id<SpeakerAssignmentRevisionIdTag>(
                    "a1000000-0000-4000-8000-000000000014"),
                secondSpeakerId,
                secondPerformer.Id,
                "Beta",
                SpeakerDisplayFormat.FullName,
                ActorSid,
                CreatedAt.AddMinutes(1),
                "Assign the guest.");

            TopicBoundaryScanResult boundaryScan =
                TopicBoundaryService.FindCandidates(
                    Revision,
                    SourceSpans,
                    modelRunId);
            TopicBoundaryCandidate boundaryPayload =
                boundaryScan.Candidates.Single();
            TopicRangeRevision topicRange = TopicBoundaryService.CreateTopicRange(
                boundaryScan,
                Id<TopicRangeIdTag>(
                    "a1000000-0000-4000-8000-000000000015"),
                Id<TopicRangeRevisionIdTag>(
                    "a1000000-0000-4000-8000-000000000016"),
                boundaryPayload.Suggestion.Id,
                endBoundarySuggestionId: null,
                Range(3, 1),
                ActorSid,
                CreatedAt.AddMinutes(2),
                "Define the reviewed topic range.");
            TopicMetadataRevision metadata = TopicMetadataService.CreateInitial(
                Project,
                Revision,
                SourceSpans,
                topicRange,
                assignments,
                topicOrdinal: 1,
                Id<TopicMetadataIdTag>(
                    "a1000000-0000-4000-8000-000000000017"),
                Id<TopicMetadataRevisionIdTag>(
                    "a1000000-0000-4000-8000-000000000018"),
                ActorSid,
                CreatedAt.AddMinutes(3),
                "Create the initial topic metadata.");
            Topic = AnalysisTopicBinding.Create(
                Project,
                Revision,
                SourceSpans,
                assignments,
                topicRange,
                metadata);

            TermCandidate termPayload = TermCandidateService.FindCandidates(
                    Revision,
                    SourceSpans,
                    [TermEntry],
                    termScope,
                    modelRunId)
                .Candidates
                .Single();
            EditorialPhraseCandidate editorialPayload =
                EditorialPhraseDetector.FindCandidates(
                        Revision,
                        SourceSpans,
                        modelRunId)
                    .Candidates
                    .Single();
            ContextDeviationCandidate contextPayload =
                ContextDeviationDetector.FindCandidates(
                        Revision,
                        SourceSpans,
                        [topicRange],
                        modelRunId)
                    .Candidates
                    .Single();
            (RepetitionSuggestionPayload repetitionPayload,
                Suggestion repetitionSuggestion) = CreateRepetition(
                    Revision,
                    revisionDigest,
                    SourceSpans,
                    modelRunId);
            AnalysisAudioTimeline audioTimeline = CreateAudioTimeline(
                projectId,
                sourceAssetId,
                sourceContentDigest);
            SilenceCandidate silencePayload = SilenceDetector.ParseProcessResult(
                    audioTimeline,
                    modelRunId,
                    new ProcessRunResult(
                        0,
                        string.Empty,
                        "[silencedetect @ a] silence_start: 1\n" +
                            "[silencedetect @ a] silence_end: 3 | silence_duration: 2\n",
                        Truncated: false,
                        TimeSpan.FromMilliseconds(1)))
                .Candidates
                .Single();
            AudioReviewCandidate audioPayload =
                AudioReviewCandidateDetector.CreateCandidates(
                        audioTimeline,
                        modelRunId,
                        [new AudioReviewObservation(
                            AudioReviewEventKind.RecordingQuality,
                            AudioReviewEvidenceMethod.WaveformClipping,
                            analysisStartSample: 100,
                            analysisEndSample: 200,
                            observedMetric: 3)])
                    .Candidates
                    .Single();
            var emphasisPayload = new EmphasisCandidate(
                new Suggestion(
                    Id<SuggestionIdTag>(
                        "a1000000-0000-4000-8000-000000000019"),
                    modelRunId,
                    SuggestionKind.Highlight,
                    SuggestionStatus.Proposed,
                    OriginRef.FromSourceSpan(SourceSpans[1].Id),
                    "Explicit important-statement evidence.",
                    confidence: null,
                    "The rule does not establish display priority.",
                    "Review wording and layout before adoption."),
                SourceSpans[1],
                Revision.Id,
                revisionDigest,
                metadata.Id,
                EmphasisCandidateService.RuleVersion,
                "ここはカット",
                EmphasisTextSourceKind.OriginalTranscript,
                [EmphasisReasonKind.ImportantStatement],
                termEvidence: []);

            var registered = new List<SuggestionCandidate>();
            TermSuggestionCandidate term = SuggestionService.Register(
                termPayload,
                registered);
            registered.Add(term);
            SilenceSuggestionCandidate silence = SuggestionService.Register(
                silencePayload,
                registered);
            registered.Add(silence);
            EditorialPhraseSuggestionCandidate editorial =
                SuggestionService.Register(editorialPayload, registered);
            registered.Add(editorial);
            RepetitionSuggestionCandidate repetition =
                SuggestionService.Register(
                    repetitionPayload,
                    repetitionSuggestion,
                    registered);
            registered.Add(repetition);
            TopicBoundarySuggestionCandidate boundary =
                SuggestionService.Register(boundaryPayload, registered);
            registered.Add(boundary);
            AudioReviewSuggestionCandidate audio = SuggestionService.Register(
                audioPayload,
                registered);
            registered.Add(audio);
            ContextDeviationSuggestionCandidate context =
                SuggestionService.Register(contextPayload, registered);
            registered.Add(context);
            EmphasisSuggestionCandidate emphasis = SuggestionService.Register(
                emphasisPayload,
                registered);
            registered.Add(emphasis);

            DeleteElementId = Id<CompositionElementIdTag>(
                "a1000000-0000-4000-8000-00000000001a");
            BoundaryElementId = Id<CompositionElementIdTag>(
                "a1000000-0000-4000-8000-00000000001b");
            RightElementId = Id<CompositionElementIdTag>(
                "a1000000-0000-4000-8000-00000000001c");
            CueId = Id<CueIdTag>(
                "a1000000-0000-4000-8000-00000000001d");
            AnalysisCandidateBinding[] bindings =
            [
                AnalysisCandidateBinding.ForTerm(
                    term,
                    SourceSpans[0],
                    Revision,
                    SourceSpans,
                    TermEntry),
                AnalysisCandidateBinding.ForDelete(
                    silence,
                    silencePayload.SourceSpan,
                    silence.SourceDigest,
                    DeleteElementId),
                AnalysisCandidateBinding.ForDelete(
                    editorial,
                    editorialPayload.SourceSpan,
                    revisionDigest,
                    Id<CompositionElementIdTag>(
                        "a1000000-0000-4000-8000-00000000001e")),
                AnalysisCandidateBinding.ForDelete(
                    repetition,
                    SourceSpans[1],
                    revisionDigest,
                    Id<CompositionElementIdTag>(
                        "a1000000-0000-4000-8000-00000000001f")),
                AnalysisCandidateBinding.ForTopicBoundary(
                    boundary,
                    boundaryPayload.SourceSpan,
                    revisionDigest,
                    BoundaryElementId,
                    new RationalTime(1, 2),
                    RightElementId),
                AnalysisCandidateBinding.ForTechnicalWarning(
                    audio,
                    audioPayload.SourceSpan,
                    audio.SourceDigest),
                AnalysisCandidateBinding.ForDelete(
                    context,
                    contextPayload.SourceSpan,
                    revisionDigest,
                    Id<CompositionElementIdTag>(
                        "a1000000-0000-4000-8000-000000000020")),
                AnalysisCandidateBinding.ForHighlight(
                    emphasis,
                    emphasisPayload.SourceSpan,
                    revisionDigest,
                    CueId,
                    Range(20, 2),
                    "graphic-main"),
            ];
            TextSensitivityScanResult sensitivity = TextSensitivityScanner.Scan(
                Revision,
                [TermEntry]);
            Snapshot = AnalysisBridgeSnapshot.Create(
                Id<AnalysisBridgeRevisionIdTag>(
                    "a1000000-0000-4000-8000-000000000021"),
                CreatedAt.AddMinutes(10),
                Project,
                Revision,
                SourceSpans,
                assignments,
                [Topic],
                bindings,
                decisionHistory: [],
                sensitivity);

            long timeOffset = 0;
            long idCounter = 1;
            Runtime = new AnalysisBridgeRuntime(
                () => Snapshot.CreatedAt.AddMinutes(++timeOffset),
                () => Guid.Parse(
                    $"f1000000-0000-4000-8000-{idCounter++:000000000000}"));
            Runtime.Register(Snapshot);
            handler = new AnalysisBridgeRequestHandler(
                Runtime,
                new StubAuthorization(allow: true));
        }

        internal Project Project { get; }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal TermEntry TermEntry { get; }

        internal AnalysisTopicBinding Topic { get; }

        internal AnalysisBridgeSnapshot Snapshot { get; }

        internal AnalysisBridgeRuntime Runtime { get; }

        internal EntityId<CompositionElementIdTag> DeleteElementId { get; }

        internal EntityId<CompositionElementIdTag> BoundaryElementId { get; }

        internal EntityId<CompositionElementIdTag> RightElementId { get; }

        internal EntityId<CueIdTag> CueId { get; }

        internal AnalysisBridgeSnapshot Current =>
            Runtime.GetRequired(Project.Id);

        internal EntityId<SuggestionIdTag> CandidateId(
            SuggestionProducerKind producer) =>
            Current.Candidates.Single(candidate =>
                candidate.Candidate.Producer == producer).Candidate.Id;

        internal Task<string> HandleAsync(string method, string parameters) =>
            handler.HandleAsync(ParseRequest(method, parameters));

        internal string DecisionParameters(
            AnalysisBridgeSnapshot snapshot,
            EntityId<SuggestionIdTag> candidateId,
            string? revisionId = null) =>
            $$"""{"projectId":"{{Project.Id}}","revisionId":"{{revisionId ?? snapshot.RevisionId.ToString()}}","digest":"{{snapshot.RevisionDigest.Hex}}","candidateId":"{{candidateId}}","reason":"候補を判断"}""";

        internal string TopicUpdateParameters(
            AnalysisBridgeSnapshot snapshot,
            EntityId<TopicMetadataRevisionIdTag> expectedRevisionId,
            string update) =>
            $$"""{"projectId":"{{Project.Id}}","revisionId":"{{snapshot.RevisionId}}","digest":"{{snapshot.RevisionDigest.Hex}}","topicId":"{{Topic.TopicRange.TopicRangeId}}","expectedTopicRevisionId":"{{expectedRevisionId}}","update":{{update}},"reason":"トピックを更新"}""";

        private static (
            RepetitionSuggestionPayload Payload,
            Suggestion Suggestion) CreateRepetition(
                TranscriptRevision revision,
                ContentDigest revisionDigest,
                IReadOnlyList<SourceSpan> sourceSpans,
                EntityId<ModelRunIdTag> modelRunId)
        {
            SourceSpan evidence = sourceSpans[1];
            SourceSpan comparison = sourceSpans[0];
            EntityId<TranscriptTokenIdTag>[] comparisonTokenIds =
                [comparison.TranscriptTokenIds[0]];
            var features = new RepetitionReasonFeatures(
                RepetitionReasonFeatures.AdjacentExactUtteranceRule,
                comparison.Id,
                comparisonTokenIds,
                matchingTokenCount: evidence.TranscriptTokenIds.Count,
                exactNfkcMatch: true,
                structurallyAdjacent: true,
                sameSpeakerRequired: true,
                candidateIsLaterOccurrence: true,
                retentionChoiceRequired: true);
            ContentDigest key = RepetitionSuggestionPayload.ComputeCandidateKey(
                revision.Id,
                revisionDigest,
                features.Rule,
                evidence.Id,
                evidence.TranscriptTokenIds,
                comparison.Id,
                comparisonTokenIds);
            const string reason =
                "Adjacent evidence requires an explicit retention choice.";
            var payload = new RepetitionSuggestionPayload(
                key,
                modelRunId,
                revision.Id,
                revisionDigest,
                evidence.Id,
                evidence.TranscriptTokenIds,
                RepetitionSuggestionPayload.CanonicalRuleVersion,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                reason,
                confidence: null,
                automaticDeletionAllowed: false,
                humanReviewRequired: true,
                features);
            var suggestion = new Suggestion(
                Id<SuggestionIdTag>(
                    "a1000000-0000-4000-8000-000000000022"),
                modelRunId,
                SuggestionKind.DeleteRange,
                SuggestionStatus.Proposed,
                OriginRef.FromSourceSpan(evidence.Id),
                reason,
                confidence: null,
                missingContext: null,
                warning: null);
            return (payload, suggestion);
        }

        private static AnalysisAudioTimeline CreateAudioTimeline(
            EntityId<ProjectIdTag> projectId,
            EntityId<AssetIdTag> sourceAssetId,
            ContentDigest sourceDigest) =>
            new(
                projectId,
                Id<JobIdTag>(
                    "a1000000-0000-4000-8000-000000000023"),
                sourceAssetId,
                sourceDigest,
                sourceByteSize: 1000,
                Id<ArtifactIdTag>(
                    "a1000000-0000-4000-8000-000000000024"),
                new ContentDigest(new string('b', 64)),
                sourceVideoStreamIndex: 0,
                sourceAudioStreamIndex: 1,
                new RationalTime(1, 48000),
                probeReportedAudioStart: null,
                firstDecodedAudioFrameTimestamp: 0,
                firstDecodedAudioFrameSampleCount: 1024,
                Id<ArtifactIdTag>(
                    "a1000000-0000-4000-8000-000000000025"),
                new ContentDigest(new string('c', 64)),
                analysisAudioByteSize: 128044,
                analysisSampleCount: 64000,
                Id<ArtifactIdTag>(
                    "a1000000-0000-4000-8000-000000000026"));
    }

    private sealed class StubAuthorization(bool allow) :
        IAnalysisBridgeAuthorization
    {
        internal int ReadDemandCount { get; private set; }

        internal int MutationDemandCount { get; private set; }

        public string DemandRead()
        {
            ReadDemandCount++;
            Demand(AuthorizationOperation.ViewProject);
            return ActorSid;
        }

        public string DemandMutation()
        {
            MutationDemandCount++;
            Demand(AuthorizationOperation.DecideAnalysisSuggestion);
            return ActorSid;
        }

        private void Demand(AuthorizationOperation operation)
        {
            if (!allow)
            {
                throw new AuthorizationException(operation, actingRole: null);
            }
        }
    }

    private sealed class CountingRuntime : IAnalysisBridgeRuntime
    {
        internal int CallCount { get; private set; }

        public AnalysisBridgeOperationResult Execute(
            BridgeRequest request,
            string actorSid,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return AnalysisBridgeOperationResult.Unavailable();
        }
    }
}
