using System.Text.Json;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class WebBridgeSchemaTests
{
    [TestMethod]
    public void SchemaIsClosedVersionedAndAllowsOnlyCanonicalMethods()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement root = document.RootElement;
        JsonElement definitions = root.GetProperty("$defs");

        Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.AreEqual("WebBridgeMessage", root.GetProperty("title").GetString());
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "96 KiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "1 MiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "16 KiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "4 MiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "256 UTF-8 bytes");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "4096-byte responses");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "24 KiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "comparison requests use 4 KiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "16 KiB review text");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "512 UTF-8 bytes");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "analysis requests use 64 KiB");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "Analysis adoption targets are native-owned");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "persisted=false");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "never matched values");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "full digest");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "mark unsupported semantic features unavailable");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "reserved intercepted HTTPS media origin");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "current Windows actor");
        StringAssert.Contains(root.GetProperty("$comment").GetString()!, "never accepted from Web messages");
        Assert.AreEqual(1, definitions.GetProperty("protocolVersion").GetProperty("const").GetInt32());
        Assert.IsTrue(
            definitions.GetProperty("timestamp")
                .GetProperty("pattern")
                .GetString()!
                .EndsWith("(?![\\s\\S])", StringComparison.Ordinal));
        CollectionAssert.AreEqual(
            new[]
            {
                "bridge.ping",
                "project.list",
                "project.createDraft",
                "project.loadDraft",
                "project.saveDraft",
                "project.pickSource",
                "project.finalizeDraft",
                "jobs.getStatus",
                "jobs.cancel",
                "jobs.retry",
                "player.read",
                "player.seek",
                "thumbnail.read",
                "thumbnail.generate",
                "thumbnail.cancel",
                "transcript.read",
                "transcript.editToken",
                "transcript.setSpeaker",
                "transcript.removeSpeaker",
                "transcript.adoptTerm",
                "transcript.rejectTerm",
                "analysis.read",
                "analysis.reject",
                "analysis.adopt",
                "analysis.updateTopic",
                "timeline.read",
                "timeline.apply",
                "review.getSnapshot",
                "review.addComment",
                "review.requestChanges",
                "review.recordApproval",
                "compare.read",
                "delivery.read",
                "layout.read",
                "layout.apply",
                "settings.read",
                "settings.update",
                "settings.reset",
                "settings.resetAll",
            },
            definitions.GetProperty("method").GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());

        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/pingRequest",
                "#/$defs/listRequest",
                "#/$defs/createDraftRequest",
                "#/$defs/loadDraftRequest",
                "#/$defs/saveDraftRequest",
                "#/$defs/pickSourceRequest",
                "#/$defs/finalizeDraftRequest",
                "#/$defs/jobStatusRequest",
                "#/$defs/jobCancelRequest",
                "#/$defs/jobRetryRequest",
                "#/$defs/reviewGetSnapshotRequest",
                "#/$defs/reviewAddCommentRequest",
                "#/$defs/reviewRequestChangesRequest",
                "#/$defs/reviewRecordApprovalRequest",
                "#/$defs/comparisonReadRequest",
                "#/$defs/pingResponse",
                "#/$defs/listResponse",
                "#/$defs/draftResponse",
                "#/$defs/pickSourceResponse",
                "#/$defs/jobStatusResponse",
                "#/$defs/jobCancelResponse",
                "#/$defs/jobRetryResponse",
                "#/$defs/projectErrorResponse",
                "#/$defs/errorResponse",
                "#/$defs/reviewResponse",
                "#/$defs/reviewErrorResponse",
                "#/$defs/comparisonReadResponse",
                "#/$defs/playerReadRequest",
                "#/$defs/playerReadResponse",
                "#/$defs/playerSeekRequest",
                "#/$defs/playerSeekResponse",
                "#/$defs/transcriptReadRequest",
                "#/$defs/transcriptEditTokenRequest",
                "#/$defs/transcriptSetSpeakerRequest",
                "#/$defs/transcriptRemoveSpeakerRequest",
                "#/$defs/transcriptAdoptTermRequest",
                "#/$defs/transcriptRejectTermRequest",
                "#/$defs/transcriptReadResponse",
                "#/$defs/transcriptEditTokenResponse",
                "#/$defs/transcriptSetSpeakerResponse",
                "#/$defs/transcriptRemoveSpeakerResponse",
                "#/$defs/transcriptAdoptTermResponse",
                "#/$defs/transcriptRejectTermResponse",
                "#/$defs/analysisReadRequest",
                "#/$defs/analysisRejectRequest",
                "#/$defs/analysisAdoptRequest",
                "#/$defs/analysisUpdateTopicRequest",
                "#/$defs/analysisReadResponse",
                "#/$defs/analysisRejectResponse",
                "#/$defs/analysisAdoptResponse",
                "#/$defs/analysisUpdateTopicResponse",
                "#/$defs/timelineReadRequest",
                "#/$defs/timelineReadResponse",
                "#/$defs/timelineApplyRequest",
                "#/$defs/timelineApplyResponse",
                "#/$defs/thumbnailReadRequest",
                "#/$defs/thumbnailReadResponse",
                "#/$defs/thumbnailGenerateRequest",
                "#/$defs/thumbnailGenerateResponse",
                "#/$defs/thumbnailCancelRequest",
                "#/$defs/thumbnailCancelResponse",
                "#/$defs/deliveryReadRequest",
                "#/$defs/deliveryReadResponse",
                "#/$defs/layoutReadRequest",
                "#/$defs/layoutApplyRequest",
                "#/$defs/layoutReadResponse",
                "#/$defs/layoutApplyResponse",
                "#/$defs/settingsReadRequest",
                "#/$defs/settingsUpdateRequest",
                "#/$defs/settingsResetRequest",
                "#/$defs/settingsResetAllRequest",
                "#/$defs/settingsReadResponse",
                "#/$defs/settingsUpdateResponse",
                "#/$defs/settingsResetResponse",
                "#/$defs/settingsResetAllResponse",
            },
            root.GetProperty("oneOf")
                .EnumerateArray()
                .Select(static item => item.GetProperty("$ref").GetString())
                .ToArray());

        foreach (string definitionName in new[]
        {
            "pingRequest",
            "listRequest",
            "createDraftRequest",
            "loadDraftRequest",
            "saveDraftRequest",
            "pickSourceRequest",
            "finalizeDraftRequest",
            "mutateDraftParams",
            "draftInput",
            "projectListItem",
            "draft",
            "sourceOnlyProjectSummary",
            "pingResponse",
            "listResponse",
            "draftResponse",
            "pickSourceResponse",
            "projectError",
            "projectErrorResponse",
            "pingResult",
            "jobStatusParams",
            "jobCommandParams",
            "jobStatusRequest",
            "jobStatusResponse",
            "jobCancelRequest",
            "jobCancelResult",
            "jobCancelResponse",
            "jobRetryRequest",
            "jobRetryResult",
            "jobRetryResponse",
            "jobSnapshot",
            "bridgeError",
            "errorResponse",
            "playerReadParams",
            "playerSeekParams",
            "playerReadRequest",
            "playerReadResponse",
            "playerSeekRequest",
            "playerSeekResponse",
            "playerSnapshot",
            "playerProxy",
            "playerWaveform",
            "playerSprite",
            "playerFrame",
            "playerPosition",
            "rationalTime",
            "transcriptTimeRange",
            "transcriptPerformer",
            "transcriptSpeaker",
            "transcriptToken",
            "transcriptSentence",
            "transcriptUtterance",
            "transcriptTermCandidate",
            "transcriptTermDecision",
            "transcriptTextChange",
            "transcriptStaleRange",
            "transcriptInvalidation",
            "transcriptSnapshot",
            "transcriptReadParams",
            "transcriptEditTokenParams",
            "transcriptSetSpeakerParams",
            "transcriptRemoveSpeakerParams",
            "transcriptTermDecisionParams",
            "transcriptReadRequest",
            "transcriptEditTokenRequest",
            "transcriptSetSpeakerRequest",
            "transcriptRemoveSpeakerRequest",
            "transcriptAdoptTermRequest",
            "transcriptRejectTermRequest",
            "transcriptReadResponse",
            "transcriptEditTokenResponse",
            "transcriptSetSpeakerResponse",
            "transcriptRemoveSpeakerResponse",
            "transcriptAdoptTermResponse",
            "transcriptRejectTermResponse",
            "analysisReadParams",
            "analysisDecisionParams",
            "analysisUpdateTopicParams",
            "analysisReadRequest",
            "analysisRejectRequest",
            "analysisAdoptRequest",
            "analysisUpdateTopicRequest",
            "analysisTimeRange",
            "analysisSource",
            "analysisSpeaker",
            "analysisTopicMainSpeaker",
            "analysisTopic",
            "analysisDetection",
            "analysisTermPayload",
            "analysisSilencePayload",
            "analysisEditorialPayload",
            "analysisRepetitionPayload",
            "analysisTopicBoundaryPayload",
            "analysisAudioPayload",
            "analysisContextPayload",
            "analysisEmphasisPayload",
            "analysisDecision",
            "analysisCandidate",
            "analysisSensitivityFinding",
            "analysisSensitivity",
            "analysisCue",
            "analysisSnapshot",
            "analysisReadResponse",
            "analysisRejectResponse",
            "analysisAdoptResponse",
            "analysisUpdateTopicResponse",
            "timelineTimeRange",
            "timelineElement",
            "timelineSnapshot",
            "timelineKeepOperation",
            "timelineDeleteOperation",
            "timelineSplitOperation",
            "timelineMoveOperation",
            "timelineReadParams",
            "timelineApplyParams",
            "timelineReadRequest",
            "timelineReadResponse",
            "timelineApplyRequest",
            "timelineApplyResponse",
            "reviewGetSnapshotParams",
            "reviewAddCommentParams",
            "reviewRequestChangesParams",
            "reviewRecordApprovalParams",
            "reviewGetSnapshotRequest",
            "reviewAddCommentRequest",
            "reviewRequestChangesRequest",
            "reviewRecordApprovalRequest",
            "reviewOutputTime",
            "reviewComment",
            "reviewApproval",
            "reviewCompositionChange",
            "reviewDirectParentDiff",
            "reviewAction",
            "reviewSnapshot",
            "reviewResponse",
            "reviewError",
            "reviewErrorResponse",
            "comparisonReadParams",
            "comparisonReadRequest",
            "comparisonFeatureAvailability",
            "comparisonTimeRange",
            "comparisonUsedSourceRange",
            "comparisonVersion",
            "comparisonAdoptionChange",
            "comparison",
            "comparisonReadResponse",
            "thumbnailRights",
            "thumbnailTemplate",
            "thumbnailSnapshot",
            "thumbnailReadParams",
            "thumbnailGenerateParams",
            "thumbnailCancelParams",
            "thumbnailOutputSource",
            "thumbnailOutputSelection",
            "thumbnailOutputTemplate",
            "thumbnailOutput",
            "thumbnailReadRequest",
            "thumbnailReadResponse",
            "thumbnailGenerateRequest",
            "thumbnailGenerateResponse",
            "thumbnailCancelRequest",
            "thumbnailCancelResponse",
            "deliveryReadParams",
            "deliveryArtifact",
            "deliveryReviewRecord",
            "deliveryApproval",
            "deliveryReview",
            "deliveryRights",
            "deliveryQcStream",
            "deliveryQcDuration",
            "deliveryQcLuma",
            "deliveryQcAudio",
            "deliveryQcTool",
            "deliveryQualityControl",
            "deliverySourceSpan",
            "deliverySnapshot",
            "deliveryReadRequest",
            "deliveryReadResponse",
            "layoutRational",
            "layoutPositiveRational",
            "layoutZeroRational",
            "layoutOneRational",
            "layoutRect",
            "layoutNormalizedRect",
            "layoutFullFrameRect",
            "layoutTimeRange",
            "layoutManualSourceInput",
            "layoutManualOperation",
            "layoutKeyframeInput",
            "layoutKeyframeSegmentInput",
            "layoutKeyframeOperation",
            "layoutTextEdit",
            "layoutTimingEdit",
            "layoutStyleEdit",
            "layoutSubtitlePlacementEdit",
            "layoutTeropRegionEdit",
            "layoutSubtitleOperation",
            "layoutTeropOperation",
            "layoutReadParams",
            "layoutApplyParams",
            "layoutReadRequest",
            "layoutApplyRequest",
            "layoutPixelRect",
            "layoutResolvedGeometry",
            "layoutOutput",
            "layoutRegion",
            "layoutOutputSafeZone",
            "layoutProjection",
            "layoutSource",
            "layoutTemplate",
            "layoutManualPlacement",
            "layoutKeyframeEvaluation",
            "layoutKeyframeTrack",
            "layoutSubtitleEvaluation",
            "layoutSubtitleResolution",
            "layoutSubtitleCue",
            "layoutSubtitleDisplayedCue",
            "layoutSubtitleDisplay",
            "layoutTeropCue",
            "layoutBurnedInVariant",
            "layoutSubtitleFreeVariant",
            "layoutSnapshot",
            "layoutReadResponse",
            "layoutApplyResponse",
            "settingsEvidence",
            "settingsDefinition",
            "settingsSelection",
            "settingsSnapshot",
            "settingsReadParams",
            "settingsUpdateParams",
            "settingsResetParams",
            "settingsResetAllParams",
            "settingsReadRequest",
            "settingsUpdateRequest",
            "settingsResetRequest",
            "settingsResetAllRequest",
            "settingsReadResponse",
            "settingsUpdateResponse",
            "settingsResetResponse",
            "settingsResetAllResponse",
        })
        {
            Assert.AreEqual(
                JsonValueKind.False,
                definitions.GetProperty(definitionName).GetProperty("additionalProperties").ValueKind);
        }

        string proxyMediaPattern = definitions
            .GetProperty("proxyMediaReference")
            .GetProperty("pattern")
            .GetString()!;
        string spriteMediaPattern = definitions
            .GetProperty("spriteMediaReference")
            .GetProperty("pattern")
            .GetString()!;
        StringAssert.Contains(proxyMediaPattern, "player-media\\.appassets\\.example/media/v1/");
        StringAssert.Contains(proxyMediaPattern, "/proxy/");
        StringAssert.Contains(proxyMediaPattern, "[0-9a-f]{64}");
        StringAssert.Contains(spriteMediaPattern, "/sprite/");
        Assert.AreEqual(
            "#/$defs/proxyMediaReference",
            definitions.GetProperty("playerProxy")
                .GetProperty("properties")
                .GetProperty("mediaReference")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            "#/$defs/spriteMediaReference",
            definitions.GetProperty("playerSprite")
                .GetProperty("properties")
                .GetProperty("imageReference")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            2,
            definitions.GetProperty("playerWaveform")
                .GetProperty("oneOf")
                .GetArrayLength());
        CollectionAssert.Contains(
            GetStrings(definitions
                .GetProperty("playerReadResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")),
            "forbidden");
        Assert.AreEqual(
            4,
            definitions.GetProperty("timelineOperation")
                .GetProperty("oneOf")
                .GetArrayLength());
        Assert.AreEqual(
            1,
            definitions.GetProperty("timelineApplyParams")
                .GetProperty("properties")
                .GetProperty("operations")
                .GetProperty("minItems")
                .GetInt32());
        CollectionAssert.Contains(
            GetStrings(definitions
                .GetProperty("timelineApplyResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")),
            "conflict");

        CollectionAssert.AreEqual(
            new[]
            {
                "review.getSnapshot",
                "review.addComment",
                "review.requestChanges",
                "review.recordApproval",
            },
            GetStrings(definitions.GetProperty("reviewMethod").GetProperty("enum")));
        Assert.AreEqual(
            "^[0-9a-f]{64}(?![\\s\\S])",
            definitions.GetProperty("contentDigest").GetProperty("pattern").GetString());
        Assert.IsFalse(definitions.TryGetProperty("reviewWorkflowGate", out _));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "leftVersionId",
                "leftVersionDigest",
                "rightVersionId",
                "rightVersionDigest",
            },
            GetStrings(definitions.GetProperty("comparisonReadParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "forbidden",
                "notFound",
                "staleVersion",
                "invalidComparison",
                "unavailable",
            },
            GetStrings(definitions.GetProperty("comparisonReadResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")));
        JsonElement comparisonFeatures = definitions
            .GetProperty("comparisonFeatureAvailability");
        string[] expectedComparisonFeatures =
        [
            "openingHook",
            "subtitleStyle",
            "telopCount",
            "transitions",
            "effects",
            "callToAction",
            "destination",
            "warnings",
        ];
        CollectionAssert.AreEquivalent(
            expectedComparisonFeatures,
            GetStrings(comparisonFeatures.GetProperty("required")));
        CollectionAssert.AreEquivalent(
            expectedComparisonFeatures,
            comparisonFeatures.GetProperty("properties")
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        foreach (JsonProperty feature in comparisonFeatures
            .GetProperty("properties")
            .EnumerateObject())
        {
            Assert.AreEqual(
                "unavailable",
                feature.Value.GetProperty("const").GetString());
        }

        string thumbnailMediaPattern = definitions
            .GetProperty("thumbnailMediaReference")
            .GetProperty("pattern")
            .GetString()!;
        StringAssert.Contains(
            thumbnailMediaPattern,
            "player-media\\.appassets\\.example/media/v1/");
        StringAssert.Contains(thumbnailMediaPattern, "/thumbnail/");
        StringAssert.Contains(thumbnailMediaPattern, "[0-9a-f]{64}");
        Assert.AreEqual(
            "approved",
            definitions.GetProperty("thumbnailOutputSource")
                .GetProperty("properties")
                .GetProperty("rightsStatus")
                .GetProperty("const")
                .GetString());
        Assert.IsTrue(definitions.GetProperty("thumbnailOutputSource")
            .GetProperty("properties")
            .GetProperty("externalPublicationAllowed")
            .GetProperty("const")
            .GetBoolean());
        Assert.AreEqual(
            "brandApprover",
            definitions.GetProperty("thumbnailTemplate")
                .GetProperty("properties")
                .GetProperty("approvalRole")
                .GetProperty("const")
                .GetString());
        Assert.AreEqual(
            "brandApprover",
            definitions.GetProperty("thumbnailOutputTemplate")
                .GetProperty("properties")
                .GetProperty("approvalRole")
                .GetProperty("const")
                .GetString());
        Assert.IsFalse(definitions.GetProperty("thumbnailTemplate")
            .GetProperty("properties")
            .GetProperty("name")
            .TryGetProperty("maxLength", out _));
        Assert.IsFalse(definitions.GetProperty("thumbnailSnapshot")
            .GetProperty("properties")
            .GetProperty("templates")
            .TryGetProperty("maxItems", out _));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "forbidden",
                "unavailable",
                "stale",
                "mismatched",
                "invalidTime",
                "rightsNotApproved",
                "templateUnavailable",
                "cancelled",
                "conflict",
                "failed",
            },
            GetStrings(definitions
                .GetProperty("thumbnailGenerateResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")));
        CollectionAssert.Contains(
            GetStrings(definitions
                .GetProperty("playerSeekResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")),
            "forbidden");
        JsonElement deliveryQc = definitions
            .GetProperty("deliveryQualityControl")
            .GetProperty("properties");
        Assert.IsTrue(deliveryQc
            .GetProperty("machineChecksPassed")
            .GetProperty("const")
            .GetBoolean());
        Assert.IsTrue(deliveryQc
            .GetProperty("humanReviewRequired")
            .GetProperty("const")
            .GetBoolean());
        Assert.IsFalse(deliveryQc
            .GetProperty("approvedNumericThresholdsApplied")
            .GetProperty("const")
            .GetBoolean());
        Assert.IsFalse(deliveryQc
            .GetProperty("canProceedToDelivery")
            .GetProperty("const")
            .GetBoolean());
        Assert.AreEqual(
            "#/$defs/reviewApprovalActorRole",
            definitions.GetProperty("deliveryApproval")
                .GetProperty("properties")
                .GetProperty("actorRole")
                .GetProperty("$ref")
                .GetString());
        JsonElement deliveryArtifactProperties = definitions
            .GetProperty("deliveryArtifact")
            .GetProperty("properties");
        Assert.IsFalse(deliveryArtifactProperties.TryGetProperty(
            "storageReference",
            out _));
        Assert.IsFalse(definitions.GetProperty("deliverySnapshot")
            .GetProperty("properties")
            .TryGetProperty("download", out _));
    }

    [TestMethod]
    public void SettingsSchemaIsClosedAndPinsFullMutationIdentity()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        CollectionAssert.AreEqual(
            new[] { "projectAndOutput", "editing", "subtitlesAndGraphics", "audioAndAssets", "privacyAndStorage" },
            GetStrings(definitions.GetProperty("settingsDefinition").GetProperty("properties").GetProperty("group").GetProperty("enum")));
        Assert.AreEqual(5, definitions.GetProperty("settingsValue").GetProperty("oneOf").GetArrayLength());
        Assert.AreEqual(14, definitions.GetProperty("settingsDefinition").GetProperty("properties").GetProperty("unit").GetProperty("enum").GetArrayLength());
        Assert.AreEqual(7, definitions.GetProperty("settingsEvidence").GetProperty("properties").GetProperty("kind").GetProperty("enum").GetArrayLength());
        Assert.AreEqual(4, definitions.GetProperty("settingsEvidence").GetProperty("oneOf").GetArrayLength());
        CollectionAssert.AreEquivalent(
            new[] { "projectId", "expectedRevision", "expectedProfileId", "expectedProfileVersion", "expectedProfileDigest", "expectedSnapshotDigest", "key", "value" },
            GetStrings(definitions.GetProperty("settingsUpdateParams").GetProperty("required")));
        CollectionAssert.Contains(
            GetStrings(definitions.GetProperty("settingsResult").GetProperty("oneOf")[1].GetProperty("properties").GetProperty("status").GetProperty("enum")),
            "conflict");
        Assert.IsFalse(definitions.GetProperty("settingsSnapshot").GetProperty("properties").GetProperty("definitionCount").TryGetProperty("const", out _));

        string schema = document.RootElement.GetRawText();
        Assert.IsFalse(schema.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("downloadUrl", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RequestAndResponseHaveDistinctExactEnvelopesAndTenFieldInput()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("pingRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("pingResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            GetStrings(definitions.GetProperty("errorResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            GetStrings(definitions.GetProperty("projectErrorResponse").GetProperty("required")));
        Assert.AreEqual(
            "#/$defs/jobMethod",
            definitions.GetProperty("errorResponse")
                .GetProperty("properties")
                .GetProperty("method")
                .GetProperty("$ref")
                .GetString());
        Assert.AreEqual(
            "#/$defs/projectMethod",
            definitions.GetProperty("projectErrorResponse")
                .GetProperty("properties")
                .GetProperty("method")
                .GetProperty("$ref")
                .GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "sourceCandidateId",
                "contentName",
                "purpose",
                "audience",
                "publicationScope",
                "outputProfileIds",
                "language",
                "performerNames",
                "deadlineSelection",
                "brandTemplateId",
            },
            GetStrings(definitions.GetProperty("draftInput").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("playerReadRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("playerReadResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("playerSeekRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("playerSeekResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "jobId" },
            GetStrings(definitions.GetProperty("playerReadParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "jobId", "digest", "proxyFrameSequence" },
            GetStrings(definitions.GetProperty("playerSeekParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "projectId" },
            GetStrings(definitions.GetProperty("timelineReadParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "projectId", "expectedRevision", "operations" },
            GetStrings(definitions.GetProperty("timelineApplyParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "workingCopyId", "projectId", "baseVersionId", "revision", "createdAt", "updatedAt", "elements" },
            GetStrings(definitions.GetProperty("timelineSnapshot").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "jobId" },
            GetStrings(definitions.GetProperty("thumbnailReadParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "jobId",
                "generationId",
                "digest",
                "playerDigest",
                "templateId",
                "proxyFrameSequence",
            },
            GetStrings(definitions.GetProperty("thumbnailGenerateParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "generationId" },
            GetStrings(definitions.GetProperty("thumbnailCancelParams").GetProperty("required")));
        StringAssert.Contains(
            definitions.GetProperty("playerSeekParams")
                .GetProperty("properties")
                .GetProperty("digest")
                .GetProperty("$comment")
                .GetString()!,
            "256-byte request limit");
        Assert.AreEqual(
            0,
            definitions.GetProperty("emptyObject").GetProperty("maxProperties").GetInt32());
        Assert.AreEqual(
            36,
            definitions.GetProperty("canonicalId").GetProperty("maxLength").GetInt32());
        CollectionAssert.AreEquivalent(
            new[] { "jobId" },
            GetStrings(definitions.GetProperty("jobStatusParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "jobId", "expectedRevision" },
            GetStrings(definitions.GetProperty("jobCommandParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "versionId" },
            GetStrings(definitions.GetProperty("reviewGetSnapshotParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "versionId", "versionDigest", "actorRole", "outputTime", "text" },
            GetStrings(definitions.GetProperty("reviewAddCommentParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "versionId", "versionDigest", "expectedState", "outputTime", "text" },
            GetStrings(definitions.GetProperty("reviewRequestChangesParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "versionId", "versionDigest", "actorRole", "reason" },
            GetStrings(definitions.GetProperty("reviewRecordApprovalParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("reviewResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "error" },
            GetStrings(definitions.GetProperty("reviewErrorResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("comparisonReadRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("comparisonReadResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "versionId", "outputTime" },
            GetStrings(definitions.GetProperty("deliveryReadParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("deliveryReadRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("deliveryReadResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "projectId", "evaluationTime" },
            GetStrings(definitions.GetProperty("layoutReadParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "projectId", "expectedRevision", "operation" },
            GetStrings(definitions.GetProperty("layoutApplyParams").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("layoutReadRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "params" },
            GetStrings(definitions.GetProperty("layoutApplyRequest").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("layoutReadResponse").GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[] { "version", "requestId", "method", "result" },
            GetStrings(definitions.GetProperty("layoutApplyResponse").GetProperty("required")));
        Assert.AreEqual(
            3,
            definitions.GetProperty("reviewSnapshot")
                .GetProperty("properties")
                .GetProperty("actions")
                .GetProperty("minItems")
                .GetInt32());
        Assert.AreEqual(
            5,
            definitions.GetProperty("reviewAction")
                .GetProperty("properties")
                .GetProperty("actorRoles")
                .GetProperty("maxItems")
                .GetInt32());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "jobId",
                "kind",
                "state",
                "currentAttemptId",
                "currentAttemptNumber",
                "revision",
                "detailsRevision",
                "updatedAt",
                "progressRatio",
                "lastProgressAt",
                "lastHeartbeatAt",
                "failure",
                "retryability",
                "cancellationRequested",
                "warnings",
                "reconciliation",
            },
            GetStrings(definitions.GetProperty("jobSnapshot").GetProperty("required")));
    }

    [TestMethod]
    public void TranscriptSchemaMatchesClosedRuntimeContract()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        string[] methods =
        [
            "transcript.read",
            "transcript.editToken",
            "transcript.setSpeaker",
            "transcript.removeSpeaker",
            "transcript.adoptTerm",
            "transcript.rejectTerm",
        ];
        CollectionAssert.AreEqual(
            methods,
            GetStrings(definitions.GetProperty("transcriptMethod").GetProperty("enum")));

        (string Definition, string Method)[] requests =
        [
            ("transcriptReadRequest", methods[0]),
            ("transcriptEditTokenRequest", methods[1]),
            ("transcriptSetSpeakerRequest", methods[2]),
            ("transcriptRemoveSpeakerRequest", methods[3]),
            ("transcriptAdoptTermRequest", methods[4]),
            ("transcriptRejectTermRequest", methods[5]),
        ];
        foreach ((string definitionName, string method) in requests)
        {
            JsonElement definition = definitions.GetProperty(definitionName);
            CollectionAssert.AreEquivalent(
                new[] { "version", "requestId", "method", "params" },
                GetStrings(definition.GetProperty("required")));
            Assert.AreEqual(
                method,
                definition.GetProperty("properties")
                    .GetProperty("method")
                    .GetProperty("const")
                    .GetString());
        }

        (string Definition, string Method)[] responses =
        [
            ("transcriptReadResponse", methods[0]),
            ("transcriptEditTokenResponse", methods[1]),
            ("transcriptSetSpeakerResponse", methods[2]),
            ("transcriptRemoveSpeakerResponse", methods[3]),
            ("transcriptAdoptTermResponse", methods[4]),
            ("transcriptRejectTermResponse", methods[5]),
        ];
        foreach ((string definitionName, string method) in responses)
        {
            JsonElement definition = definitions.GetProperty(definitionName);
            CollectionAssert.AreEquivalent(
                new[] { "version", "requestId", "method", "result" },
                GetStrings(definition.GetProperty("required")));
            Assert.AreEqual(
                method,
                definition.GetProperty("properties")
                    .GetProperty("method")
                    .GetProperty("const")
                    .GetString());
            Assert.AreEqual(
                "#/$defs/transcriptResult",
                definition.GetProperty("properties")
                    .GetProperty("result")
                    .GetProperty("$ref")
                    .GetString());
        }

        CollectionAssert.AreEquivalent(
            new[] { "projectId" },
            GetStrings(definitions.GetProperty("transcriptReadParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "tokenId",
                "expectedText",
                "revisedText",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptEditTokenParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "expectedSpeakerRevisionId",
                "speakerId",
                "performerId",
                "surname",
                "displayFormat",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptSetSpeakerParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "expectedSpeakerRevisionId",
                "speakerId",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptRemoveSpeakerParams")
                .GetProperty("required")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "candidateId",
                "termEntryId",
                "termEntryVersion",
                "reason",
            },
            GetStrings(definitions.GetProperty("transcriptTermDecisionParams")
                .GetProperty("required")));

        JsonElement resultAlternatives = definitions.GetProperty("transcriptResult")
            .GetProperty("oneOf");
        Assert.AreEqual(
            "available",
            resultAlternatives[0].GetProperty("properties")
                .GetProperty("status")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "forbidden",
                "unavailable",
                "staleBase",
                "conflict",
                "noChange",
                "invalid",
            },
            GetStrings(definitions.GetProperty("transcriptFailureStatus")
                .GetProperty("enum")));
        Assert.AreEqual(
            "null",
            resultAlternatives[1].GetProperty("properties")
                .GetProperty("snapshot")
                .GetProperty("type")
                .GetString());

        CollectionAssert.AreEqual(
            new[]
            {
                "projectId",
                "revisionId",
                "revisionDigest",
                "parentRevisionId",
                "changeSource",
                "createdAt",
                "speakerRevisionId",
                "performers",
                "utterances",
                "termCandidates",
                "termDecisions",
                "diff",
                "staleRange",
                "invalidation",
            },
            GetStrings(definitions.GetProperty("transcriptSnapshot")
                .GetProperty("required")));
        CollectionAssert.AreEqual(
            new[]
            {
                "sourceRevisionId",
                "sourceRevisionDigest",
                "revisionId",
                "revisionDigest",
            },
            GetStrings(definitions.GetProperty("transcriptStaleRange")
                .GetProperty("required")));

        string schema = document.RootElement.GetRawText();
        Assert.IsFalse(schema.Contains("confidenceClass", StringComparison.Ordinal));
        Assert.IsFalse(schema.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(
            definitions.GetProperty("transcriptTermCandidate")
                .GetProperty("$comment")
                .GetString()!,
            "no invented confidence classification");
    }

    [TestMethod]
    public void AnalysisSchemaMatchesClosedRuntimeContract()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        string[] methods =
        [
            "analysis.read",
            "analysis.reject",
            "analysis.adopt",
            "analysis.updateTopic",
        ];
        CollectionAssert.AreEqual(
            methods,
            GetStrings(definitions.GetProperty("analysisMethod").GetProperty("enum")));

        (string Definition, string Method)[] requests =
        [
            ("analysisReadRequest", methods[0]),
            ("analysisRejectRequest", methods[1]),
            ("analysisAdoptRequest", methods[2]),
            ("analysisUpdateTopicRequest", methods[3]),
        ];
        foreach ((string definitionName, string method) in requests)
        {
            JsonElement definition = definitions.GetProperty(definitionName);
            CollectionAssert.AreEquivalent(
                new[] { "version", "requestId", "method", "params" },
                GetStrings(definition.GetProperty("required")));
            Assert.AreEqual(
                method,
                definition.GetProperty("properties")
                    .GetProperty("method")
                    .GetProperty("const")
                    .GetString());
        }

        (string Definition, string Method)[] responses =
        [
            ("analysisReadResponse", methods[0]),
            ("analysisRejectResponse", methods[1]),
            ("analysisAdoptResponse", methods[2]),
            ("analysisUpdateTopicResponse", methods[3]),
        ];
        foreach ((string definitionName, string method) in responses)
        {
            JsonElement definition = definitions.GetProperty(definitionName);
            CollectionAssert.AreEquivalent(
                new[] { "version", "requestId", "method", "result" },
                GetStrings(definition.GetProperty("required")));
            Assert.AreEqual(
                method,
                definition.GetProperty("properties")
                    .GetProperty("method")
                    .GetProperty("const")
                    .GetString());
            Assert.AreEqual(
                "#/$defs/analysisResult",
                definition.GetProperty("properties")
                    .GetProperty("result")
                    .GetProperty("$ref")
                    .GetString());
        }

        CollectionAssert.AreEquivalent(
            new[] { "projectId" },
            GetStrings(definitions.GetProperty("analysisReadParams").GetProperty("required")));
        CollectionAssert.AreEqual(
            new[] { "projectId", "revisionId", "digest", "candidateId", "reason" },
            GetStrings(definitions.GetProperty("analysisDecisionParams").GetProperty("required")));
        CollectionAssert.AreEqual(
            new[]
            {
                "projectId",
                "revisionId",
                "digest",
                "topicId",
                "expectedTopicRevisionId",
                "update",
                "reason",
            },
            GetStrings(definitions.GetProperty("analysisUpdateTopicParams").GetProperty("required")));
        Assert.AreEqual(
            7,
            definitions.GetProperty("analysisTopicUpdate").GetProperty("oneOf").GetArrayLength());
        foreach (JsonElement update in definitions.GetProperty("analysisTopicUpdate")
                     .GetProperty("oneOf")
                     .EnumerateArray())
        {
            Assert.AreEqual(JsonValueKind.False, update.GetProperty("additionalProperties").ValueKind);
            Assert.AreEqual(2, update.GetProperty("required").GetArrayLength());
        }

        JsonElement decisionProperties = definitions.GetProperty("analysisDecisionParams")
            .GetProperty("properties");
        foreach (string nativeOwnedTarget in new[]
        {
            "elementId",
            "cueId",
            "splitOffset",
            "rightElementId",
            "tokenId",
            "termEntryId",
        })
        {
            Assert.IsFalse(decisionProperties.TryGetProperty(nativeOwnedTarget, out _));
        }
        StringAssert.Contains(
            definitions.GetProperty("analysisDecisionParams").GetProperty("$comment").GetString()!,
            "native-owned");

        CollectionAssert.AreEqual(
            new[]
            {
                "projectId",
                "revisionId",
                "revisionDigest",
                "createdAt",
                "sourceRevisionId",
                "sourceRevisionDigest",
                "speakers",
                "topics",
                "candidates",
                "sensitivity",
                "stagedChanges",
                "adoptionPersistence",
            },
            GetStrings(definitions.GetProperty("analysisSnapshot").GetProperty("required")));
        Assert.AreEqual(
            "notPersisted",
            definitions.GetProperty("analysisSnapshot")
                .GetProperty("properties")
                .GetProperty("adoptionPersistence")
                .GetProperty("const")
                .GetString());

        JsonElement stagedChanges = definitions.GetProperty("analysisStagedChange")
            .GetProperty("oneOf");
        Assert.AreEqual(4, stagedChanges.GetArrayLength());
        foreach (JsonElement stagedChange in stagedChanges.EnumerateArray())
        {
            Assert.AreEqual(JsonValueKind.False, stagedChange.GetProperty("additionalProperties").ValueKind);
            Assert.IsFalse(
                stagedChange.GetProperty("properties")
                    .GetProperty("persisted")
                    .GetProperty("const")
                    .GetBoolean());
        }

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
            definitions.GetProperty("analysisCandidatePayload")
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(item => definitions
                    .GetProperty(item.GetProperty("$ref").GetString()!["#/$defs/".Length..])
                    .GetProperty("properties")
                    .GetProperty("producer")
                    .GetProperty("const")
                    .GetString())
                .ToArray());
        Assert.AreEqual(
            8,
            definitions.GetProperty("analysisCandidatePayload").GetProperty("oneOf").GetArrayLength());
        Assert.AreEqual(
            JsonValueKind.False,
            definitions.GetProperty("analysisSensitivityFinding")
                .GetProperty("additionalProperties")
                .ValueKind);
        JsonElement sensitivityProperties = definitions.GetProperty("analysisSensitivityFinding")
            .GetProperty("properties");
        Assert.IsFalse(sensitivityProperties.TryGetProperty("matchedValue", out _));
        Assert.IsFalse(sensitivityProperties.TryGetProperty("startOffset", out _));
        Assert.IsFalse(sensitivityProperties.TryGetProperty("length", out _));
        CollectionAssert.Contains(
            GetStrings(sensitivityProperties.GetProperty("kind").GetProperty("enum")),
            "credentialPattern");

        JsonElement resultAlternatives = definitions.GetProperty("analysisResult")
            .GetProperty("oneOf");
        Assert.AreEqual(
            "available",
            resultAlternatives[0].GetProperty("properties")
                .GetProperty("status")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[] { "forbidden", "unavailable", "staleBase", "conflict", "noChange", "invalid" },
            GetStrings(definitions.GetProperty("analysisFailureStatus").GetProperty("enum")));
        Assert.AreEqual(
            "null",
            resultAlternatives[1].GetProperty("properties")
                .GetProperty("snapshot")
                .GetProperty("type")
                .GetString());
    }

    [TestMethod]
    public void LayoutSchemaMatchesClosedNativeAggregateContract()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");

        string[] methods = GetStrings(
            definitions.GetProperty("method").GetProperty("enum"));
        CollectionAssert.Contains(methods, "layout.read");
        CollectionAssert.Contains(methods, "layout.apply");
        StringAssert.Contains(
            definitions.GetProperty("layoutProtocolLimits")
                .GetProperty("$comment").GetString()!,
            "64 KiB");
        StringAssert.Contains(
            definitions.GetProperty("layoutProtocolLimits")
                .GetProperty("$comment").GetString()!,
            "4 MiB");
        StringAssert.Contains(
            definitions.GetProperty("layoutProtocolLimits")
                .GetProperty("$comment").GetString()!,
            "Actor identity");
        StringAssert.Contains(
            definitions.GetProperty("layoutApplyParams")
                .GetProperty("$comment").GetString()!,
            "optimistic-concurrency");

        CollectionAssert.AreEqual(
            new[]
            {
                "singleSubjectCrop",
                "twoSpeakerStack",
                "speakerMaterialStack",
                "materialWithSpeakerInset",
                "importantRegionZoom",
                "blurredSourceBackground",
                "fixedTemplatePlacement",
            },
            GetStrings(definitions.GetProperty("layoutManualMode").GetProperty("enum")));
        CollectionAssert.AreEqual(
            new[]
            {
                "#/$defs/layoutManualOperation",
                "#/$defs/layoutKeyframeOperation",
                "#/$defs/layoutSubtitleOperation",
                "#/$defs/layoutTeropOperation",
            },
            definitions.GetProperty("layoutOperation")
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(static item => item.GetProperty("$ref").GetString())
                .ToArray());
        Assert.AreEqual(
            2,
            definitions.GetProperty("layoutManualOperation")
                .GetProperty("properties")
                .GetProperty("sources")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.AreEqual(
            256,
            definitions.GetProperty("layoutKeyframeOperation")
                .GetProperty("properties")
                .GetProperty("segments")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.AreEqual(
            256,
            definitions.GetProperty("layoutKeyframeSegmentInput")
                .GetProperty("properties")
                .GetProperty("keyframes")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.AreEqual(
            "output-frame-bounds",
            definitions.GetProperty("layoutOutputSafeZone")
                .GetProperty("properties")
                .GetProperty("name")
                .GetProperty("const")
                .GetString());
        Assert.AreEqual(
            1,
            definitions.GetProperty("layoutProjection")
                .GetProperty("properties")
                .GetProperty("safeZones")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.AreEqual(
            "1",
            definitions.GetProperty("layoutOneRational")
                .GetProperty("properties")
                .GetProperty("numerator")
                .GetProperty("const")
                .GetString());
        Assert.AreEqual(
            "notPersisted",
            definitions.GetProperty("layoutSnapshot")
                .GetProperty("properties")
                .GetProperty("persistence")
                .GetProperty("const")
                .GetString());
        CollectionAssert.AreEqual(
            new[] { "full", "selected" },
            GetStrings(definitions.GetProperty("layoutSubtitleDisplay")
                .GetProperty("properties")
                .GetProperty("cueMode")
                .GetProperty("enum")));
        CollectionAssert.AreEqual(
            new[] { "preserve", "hideJapaneseCommaAndPeriod" },
            GetStrings(definitions.GetProperty("layoutSubtitleDisplay")
                .GetProperty("properties")
                .GetProperty("punctuationDisplay")
                .GetProperty("enum")));
        CollectionAssert.Contains(
            GetStrings(definitions.GetProperty("layoutSnapshot")
                .GetProperty("required")),
            "subtitleDisplays");
        Assert.AreEqual(
            4,
            definitions.GetProperty("layoutSnapshot")
                .GetProperty("properties")
                .GetProperty("subtitleDisplays")
                .GetProperty("maxItems")
                .GetInt32());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "forbidden",
                "notFound",
                "conflict",
                "noChange",
                "invalid",
                "unavailable",
            },
            GetStrings(definitions.GetProperty("layoutResult")
                .GetProperty("oneOf")[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")));
        StringAssert.Contains(
            definitions.GetProperty("layoutVariantPair")
                .GetProperty("oneOf")[1]
                .GetProperty("$comment")
                .GetString()!,
            "does not trigger rendering");

        JsonElement snapshotProperties = definitions
            .GetProperty("layoutSnapshot")
            .GetProperty("properties");
        foreach (string forbidden in new[]
        {
            "actorSid",
            "sourcePath",
            "provider",
            "confidence",
            "faceCandidates",
            "persist",
            "render",
        })
        {
            Assert.IsFalse(snapshotProperties.TryGetProperty(forbidden, out _));
        }
    }

    [TestMethod]
    public void ListSchemaRepresentsUnknownColumnsAsNullAndNeverDefinesPrivateValues()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath("schemas/web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement properties = definitions
            .GetProperty("projectListItem")
            .GetProperty("properties");

        foreach (string unavailable in new[]
        {
            "episodeNumber",
            "owner",
            "editState",
            "reviewState",
        })
        {
            Assert.AreEqual(
                "null",
                properties.GetProperty(unavailable).GetProperty("type").GetString());
        }

        string schema = document.RootElement.GetRawText();
        Assert.IsFalse(schema.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("windowsSid", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("filePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("\"credential\":", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("credentialValue", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("matchedValue", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("contentBody", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("processOutput", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("selectedByActor", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("\"approvalActor\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("approvalReason", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("evidenceReference", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("templatePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("commandLine", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("faceDetection", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("qualityScore", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("frameRecommendation", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PersistencePayloadSchemaIsClosedVersionedAndOpaque()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ResolveRepositoryPath(
                "schemas/project-creation-draft.schema.json")));
        JsonElement root = document.RootElement;

        Assert.AreEqual(JsonValueKind.False, root.GetProperty("additionalProperties").ValueKind);
        Assert.AreEqual(12, root.GetProperty("required").GetArrayLength());
        Assert.AreEqual(
            "1.0.0",
            root.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const")
                .GetString());
        string schema = root.GetRawText();
        StringAssert.Contains(schema, "sourceCandidateId");
        StringAssert.Contains(schema, "sourceOnly");
        StringAssert.Contains(schema, "settingsSnapshotDigest");
        Assert.IsFalse(root.GetProperty("required")
            .EnumerateArray()
            .Any(static item => string.Equals(
                item.GetString(),
                "sourceOnly",
                StringComparison.Ordinal)));
        Assert.IsFalse(schema.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(schema.Contains("actorSid", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetStrings(JsonElement array)
    {
        return array.EnumerateArray().Select(static item => item.GetString()!).ToArray();
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}
