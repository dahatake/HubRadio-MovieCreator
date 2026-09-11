using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Compositions;
using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.App.Web;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Rendering;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.App.Tests;

// Real journal replay, pure graphs/writers and real artifact registrations. Video
// bytes/QC are explicitly synthetic. No saved generated-runtime success is invented.
[TestClass]
public sealed class SourceOnlyProjectedResultTests
{
    [TestMethod]
    public async Task NullProjectionKeepsExactlyThePreviousV2CanonicalContract()
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true, includeThumbnail: true);
        foreach (StandardGeneratedArtifact item in fixture.Generated.Artifacts)
        {
            SourceOnlyRenderingInputsPayload payload = SourceOnlyResultInputValidation.Capture(item, fixture.Generation.Preparation);
            string canonical = Write(payload);
            Assert.AreEqual("source-only.rendering-inputs.v2", payload.SchemaVersion);
            Assert.IsNull(payload.SubtitleEditProjection);
            Assert.IsNull(payload.SubtitleEditProjectionDigest);
            // Independent old DTO shape, including the existing checkpoint null.
            Assert.AreEqual(Write(new
            {
                payload.SchemaVersion,
                payload.PreparedVersionCanonicalDigest,
                payload.PlanDigest,
                payload.Version,
                payload.Timeline,
                payload.Profile,
                payload.Layout,
                payload.SourceSpans,
                payload.EditorialRecordIds,
                payload.Fonts,
                payload.BindingEvidence,
                payload.DisplayCues,
                payload.SubtitleEvidence,
                payload.SubtitleFiles,
                payload.GraphicFiles,
                payload.SourceCueMappingDigest,
                payload.GenerationCheckpoint,
            }), canonical);
            JsonElement json = SourceOnlyResultCodec.Element(canonical);
            Assert.IsFalse(json.TryGetProperty("subtitleEditProjection", out _));
            Assert.IsFalse(json.TryGetProperty("subtitleEditProjectionDigest", out _));
            Assert.AreEqual(canonical, Write(SourceOnlyResultInputValidation.Read(json)));
        }
    }

    [TestMethod]
    [DataRow("v2-with-proof")]
    [DataRow("v3-without-proof")]
    [DataRow("canonical-only")]
    [DataRow("digest-only")]
    [DataRow("explicit-null")]
    [DataRow("unknown-field")]
    [DataRow("noncanonical-proof")]
    [DataRow("wrong-digest")]
    public async Task ReaderRejectsDowngradesHalfProofsNullMembersAndUnknownFields(string change)
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync();
        JsonObject json = JsonNode.Parse(Write(SourceOnlyResultInputValidation.Capture(
            fixture.Generated.Artifacts[0], fixture.Generation.Preparation)))!.AsObject();
        if (change is not ("explicit-null" or "unknown-field"))
        {
            json["schemaVersion"] = "source-only.rendering-inputs.v3";
            json["subtitleEditProjection"] = "{}";
            json["subtitleEditProjectionDigest"] = Hash("{}").Hex;
        }
        switch (change)
        {
            case "v2-with-proof": json["schemaVersion"] = "source-only.rendering-inputs.v2"; break;
            case "v3-without-proof": json.Remove("subtitleEditProjection"); json.Remove("subtitleEditProjectionDigest"); break;
            case "canonical-only": json.Remove("subtitleEditProjectionDigest"); break;
            case "digest-only": json.Remove("subtitleEditProjection"); break;
            case "explicit-null": json["subtitleEditProjection"] = null; json["subtitleEditProjectionDigest"] = null; break;
            case "unknown-field": json["trustedProjection"] = true; break;
            case "noncanonical-proof": json["subtitleEditProjection"] = "{ }"; json["subtitleEditProjectionDigest"] = Hash("{ }").Hex; break;
            case "wrong-digest": json["subtitleEditProjectionDigest"] = Hash("wrong").Hex; break;
        }
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResultInputValidation.Read(Element(json)));
    }

    [TestMethod]
    public async Task SelfAssertedJsonAndTrustedIdsNeverCreateNativeAuthority()
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync();
        StandardGeneratedArtifact item = fixture.Generated.Artifacts[0];
        string self = CanonicalJson.Canonicalize(JsonSerializer.Serialize(new
        {
            schema = SubtitleEditProjection.RuleVersion,
            trustedEditorialRecordIds = new[] { EntityId<EditorialRecordIdTag>.New() },
        }));
        SourceOnlyRenderingInputsPayload forged = SourceOnlyResultInputValidation.Capture(item, fixture.Generation.Preparation) with
        {
            SchemaVersion = "source-only.rendering-inputs.v3",
            SubtitleEditProjection = self,
            SubtitleEditProjectionDigest = Hash(self).Hex,
        };
        SourceOnlyRenderingInputsPayload parsed = SourceOnlyResultInputValidation.Read(SourceOnlyResultCodec.Element(Write(forged)));
        Assert.ThrowsExactly<JsonException>(() => Validate(fixture, item, parsed));
    }

    [TestMethod]
    [DataRow(SourceOnlyResultRole.LandscapeClean)]
    [DataRow(SourceOnlyResultRole.LandscapeBurnIn)]
    [DataRow(SourceOnlyResultRole.SubRip)]
    [DataRow(SourceOnlyResultRole.WebVtt)]
    [DataRow(SourceOnlyResultRole.VerticalBurnIn)]
    public async Task ManualProjectionRequiresTheExactNativeOutputAndKeepsOriginalTranscript(SourceOnlyResultRole role)
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: role == SourceOnlyResultRole.VerticalBurnIn);
        string transcript = TranscriptSnapshotJson.WriteRevision(fixture.Generation.Transcript);
        SourceOnlyGeneratedSubtitleRuntimeEvidence runtime = Project(fixture, role == SourceOnlyResultRole.VerticalBurnIn);
        StandardGeneratedArtifact item = await ArtifactAsync(fixture, runtime, role);
        SourceOnlyRenderingInputsPayload payload = SourceOnlyResultInputValidation.Capture(item, fixture.Generation.Preparation);
        Assert.AreEqual("source-only.rendering-inputs.v3", payload.SchemaVersion);
        Assert.AreEqual(runtime.SubtitleProjection!.CanonicalJson, payload.SubtitleEditProjection);
        Assert.AreEqual(runtime.SubtitleProjection.ContentDigest.Hex, payload.SubtitleEditProjectionDigest);
        SourceOnlyRenderingInputsPayload restored = SourceOnlyResultInputValidation.Read(SourceOnlyResultCodec.Element(Write(payload)));
        IReadOnlyList<SourceOnlyResultSubtitleMeasurement> measurements = Validate(fixture, item, restored, runtime);
        Assert.HasCount(runtime.Histories.Count, measurements);
        Assert.AreEqual("確認済みの訂正字幕", item.DisplayCues.Single().Text);
        Assert.AreEqual(SubtitleFileCueStyle.Quote, item.DisplayCues.Single().Style);
        Assert.AreEqual(transcript, TranscriptSnapshotJson.WriteRevision(fixture.Generation.Transcript));
        Assert.AreEqual(fixture.Generation.TranscriptDigest, runtime.Histories.Single().SourceCandidate.SourceRevisionDigest);
        Assert.AreNotEqual(runtime.Histories.Single().SourceCandidate.Text, runtime.Histories.Single().CurrentRevision.Snapshot.Text);
        Assert.IsTrue(runtime.SubtitleProjection.TrustedEditorialRecordIds.All(item.Context.EditorialRecordIds.Contains));
        Assert.ThrowsExactly<JsonException>(() => Validate(fixture, item, restored));
        Assert.ThrowsExactly<JsonException>(() => Validate(fixture, item, restored, runtime with { SubtitleProjection = null }));
        Assert.ThrowsExactly<SubtitleEditProjectionException>(() => Validate(fixture, item, restored, runtime with { Histories = [] }));
        SourceOnlyRenderingInputsPayload stripped = restored with
        {
            SchemaVersion = "source-only.rendering-inputs.v2",
            SubtitleEditProjection = null,
            SubtitleEditProjectionDigest = null,
        };
        Assert.ThrowsExactly<JsonException>(() => Validate(fixture, item, stripped, runtime));
    }

    [TestMethod]
    [DataRow("proof")]
    [DataRow("editorial-id")]
    [DataRow("display-text")]
    [DataRow("source-text-digest")]
    [DataRow("layout")]
    [DataRow("ass-profile")]
    public async Task RehashedPayloadCannotChangeAnyProjectedNativeSubtitleField(string change)
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyGeneratedSubtitleRuntimeEvidence runtime = Project(fixture);
        StandardGeneratedArtifact item = await ArtifactAsync(fixture, runtime, SourceOnlyResultRole.LandscapeBurnIn);
        SourceOnlyRenderingInputsPayload payload = SourceOnlyResultInputValidation.Capture(item, fixture.Generation.Preparation);
        _ = Validate(fixture, item, payload, runtime);
        switch (change)
        {
            case "proof":
                JsonNode canonical = JsonNode.Parse(payload.SubtitleEditProjection!)!;
                canonical["materialization"]!["reason"] = "Caller claim";
                string proof = CanonicalJson.Canonicalize(canonical.ToJsonString());
                payload = payload with { SubtitleEditProjection = proof, SubtitleEditProjectionDigest = Hash(proof).Hex };
                break;
            case "editorial-id":
                payload = payload with
                {
                    EditorialRecordIds = SourceOnlyResultEvidenceStore.Snapshot(
                    item.Context.EditorialRecordIds.Append(EntityId<EditorialRecordIdTag>.New()).ToArray())
                };
                break;
            case "display-text":
                JsonNode cues = JsonNode.Parse(payload.DisplayCues.GetRawText())!;
                cues[0]!["Text"] = "自己申告の字幕";
                payload = payload with { DisplayCues = Element(cues) };
                break;
            case "source-text-digest":
                JsonNode evidence = JsonNode.Parse(payload.SubtitleEvidence!.Value.GetRawText())!;
                evidence["DisplayResult"]!["DisplayedCues"]![0]!["SourceTextDigest"]!["Hex"] = Hash("other original text").Hex;
                payload = payload with { SubtitleEvidence = Element(evidence) };
                break;
            case "layout":
                JsonNode layout = JsonNode.Parse(payload.SubtitleEvidence!.Value.GetRawText())!;
                layout["Layouts"]![0]!["Resolution"]!["ProfileVersionDigest"]!["Hex"] = Hash("other layout profile").Hex;
                payload = payload with { SubtitleEvidence = Element(layout) };
                break;
            case "ass-profile":
                payload = payload with { SubtitleFiles = [payload.SubtitleFiles.Single() with { AssProfileDigest = Hash("other ASS profile").Hex }] };
                break;
        }
        payload = payload with
        {
            SourceCueMappingDigest = Hash(Write(new
            {
                domain = "source-only.subtitle-mapping.v2",
                displayCues = payload.DisplayCues,
                subtitleEvidence = payload.SubtitleEvidence,
                sourceSpans = payload.SourceSpans,
            })).Hex
        };
        SourceOnlyRenderingInputsPayload restored = SourceOnlyResultInputValidation.Read(SourceOnlyResultCodec.Element(Write(payload)));
        Assert.ThrowsExactly<JsonException>(() => Validate(fixture, item, restored, runtime));
    }

    [TestMethod]
    public async Task ConfigurationDigestMustUseTheRendererDomainAndNativeDigestObject()
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyGeneratedSubtitleRuntimeEvidence runtime = Project(fixture);
        StandardGeneratedArtifact item = await ArtifactAsync(fixture, runtime, SourceOnlyResultRole.LandscapeBurnIn);
        StandardSubtitleInput input = runtime.Build.Subtitles;
        var revisions = input.Revisions.Select(r => new { r.Id, r.ContentDigest, r.SourceCandidateId, r.SourceRevisionId, r.SourceRevisionDigest }).ToArray();
        ContentDigest expected = StandardRenderGraphFactory.Digest(new
        {
            domain = "standard-subtitle-input-v3",
            input.DisplayProfile,
            input.AssProfile,
            input.Warnings,
            revisions,
            SubtitleEditProjection = runtime.SubtitleProjection!.CanonicalJson,
            SubtitleEditProjectionDigest = runtime.SubtitleProjection.ContentDigest,
        });
        Assert.AreEqual(expected, item.BindingEvidence.SubtitleConfigurationDigest);
        ContentDigest wrongShape = StandardRenderGraphFactory.Digest(new
        {
            domain = "standard-subtitle-input-v3",
            input.DisplayProfile,
            input.AssProfile,
            input.Warnings,
            revisions,
            SubtitleEditProjection = runtime.SubtitleProjection.CanonicalJson,
            SubtitleEditProjectionDigest = runtime.SubtitleProjection.ContentDigest.Hex,
        });
        Assert.AreNotEqual(expected, wrongShape);
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResultInputValidation.Capture(item with
        {
            BindingEvidence = item.BindingEvidence with { SubtitleConfigurationDigest = wrongShape },
        }, fixture.Generation.Preparation));
    }

    [TestMethod]
    public async Task SourceFrameImagesCannotCarryAValidSubtitleCapability()
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync(includeThumbnail: true);
        SourceOnlyGeneratedSubtitleRuntimeEvidence runtime = Project(fixture);
        StandardGeneratedArtifact image = fixture.Generated.Artifacts.First(a => a.BindingEvidence.Role.StartsWith("thumbnail-", StringComparison.Ordinal));
        Assert.ThrowsExactly<JsonException>(() => SourceOnlyResultInputValidation.Capture(
            image with { SubtitleProjection = runtime.SubtitleProjection }, fixture.Generation.Preparation));
    }

    [TestMethod]
    public async Task LiveEvaluationDoesNotTurnAnUnsavedGeneratedRootIntoReloadAuthority()
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync();
        SourceOnlyGeneratedSubtitleRuntimeEvidence runtime = Project(fixture);
        var items = new List<StandardGeneratedArtifact>();
        foreach (SourceOnlyResultRole role in new[] { SourceOnlyResultRole.LandscapeClean, SourceOnlyResultRole.LandscapeBurnIn,
            SourceOnlyResultRole.SubRip, SourceOnlyResultRole.WebVtt })
            items.Add(await ArtifactAsync(fixture, runtime, role));
        var generated = new StandardArtifactGenerationResult(StandardArtifactGenerationDisposition.Completed, items,
            fixture.Generated.Omissions, [], null, null, false, fixture.Generated.Warnings);
        SourceOnlyResult result = await fixture.EvaluateAsync(generated: generated);
        Assert.IsTrue(result.Artifacts.All(a => a.FailureCode is null && a.Evaluation is not null));
        Assert.IsTrue(result.Artifacts.All(a => a.Evaluation!.Rules.Single(r => r.RuleId == AutomatedEvaluationRuleId.Provenance).Status == AutomatedEvaluationStatus.Pass));
        StoredSourceOnlyExecution saved = fixture.PersistResult(result); // Real UpdateState/Get; no invented Completed row.
        var reader = new SourceOnlySubtitleEvidenceReader(fixture.Generation.Store, fixture.Projects,
            new DefaultSettingsStore(fixture.Generation.DatabasePath), fixture.Executions);
        Assert.IsNull(await reader.ReadAsync(saved, CancellationToken.None, 0));
        SourceOnlyResultLoadResult reloaded = await fixture.Service.LoadAsync(saved);
        Assert.IsFalse(reloaded.Validation.IsValid, "Live capabilities do not replace an execution-owned persisted generated-runtime checkpoint.");
        Assert.IsTrue(result.Artifacts.All(a => reloaded.Validation.Failures.Any(f =>
            f.ArtifactId == a.ArtifactId && f.Code == SourceOnlyResultFailureCode.StoredEvidenceInvalid)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AllV2NullProofsCannotSkipAcceptedManualHistory(bool inherited)
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true);
        SourceOnlyRenderingInputsPayload[] downgraded = fixture.Generated.Artifacts.Select(item =>
            SourceOnlyResultInputValidation.Read(SourceOnlyResultCodec.Element(Write(
                SourceOnlyResultInputValidation.Capture(item, fixture.Generation.Preparation) with
                {
                    SchemaVersion = "source-only.rendering-inputs.v2",
                    SubtitleEditProjection = null,
                    SubtitleEditProjectionDigest = null,
                })))).ToArray();
        Assert.IsTrue(downgraded.All(payload => payload.SubtitleEditProjection is null && payload.SubtitleEditProjectionDigest is null));
        bool observed = downgraded.Any(payload => payload.SchemaVersion == "source-only.rendering-inputs.v3");
        Assert.IsFalse(observed);
        StoredSourceOnlyExecution manual = AcceptanceSuccessor(fixture.Execution, capture: true);
        StoredSourceOnlyExecution current = inherited ? AcceptanceSuccessor(manual, capture: false) : manual;
        IReadOnlyList<StoredSourceOnlyExecution> history = inherited
            ? [fixture.Execution, manual, current] : [fixture.Execution, current];
        Assert.AreEqual(!inherited, current.Request.SubtitleEdits is { Count: > 0 });
        Assert.IsTrue(SourceOnlyResultService.ShouldReadSubtitleEvidence(current, history, observed));
    }

    [TestMethod]
    public async Task LegacyOwnerlessV2SkipsButObservedV3AlwaysReads()
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync();
        StoredSourceOnlyExecution legacy = AcceptanceSuccessor(fixture.Execution, capture: false);
        Assert.IsFalse(SourceOnlyResultService.ShouldReadSubtitleEvidence(legacy, [legacy], false));
        Assert.IsFalse(SourceOnlyResultService.ShouldReadSubtitleEvidence(fixture.Execution, [fixture.Execution], false));
        Assert.IsTrue(SourceOnlyResultService.ShouldReadSubtitleEvidence(legacy, [legacy], true));
        Assert.IsTrue(SourceOnlyResultService.ShouldReadSubtitleEvidence(fixture.Execution, [fixture.Execution], true));
        SourceOnlyResult result = await fixture.EvaluateAsync();
        Assert.IsTrue((await fixture.Service.LoadAsync(fixture.PersistResult(result))).Validation.IsValid);
    }

    [TestMethod]
    public async Task ReplayGuardDoesNotHideAmbiguousOrTruncatedHistory()
    {
        using SourceOnlyEvaluationFixture fixture = await SourceOnlyEvaluationFixture.CreateAsync();
        StoredSourceOnlyExecution current = AcceptanceSuccessor(fixture.Execution, capture: false);
        foreach (bool observed in new[] { false, true })
        {
            Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() => SourceOnlyResultService.ShouldReadSubtitleEvidence(
                current, [fixture.Execution, fixture.Execution, current], observed));
            Assert.ThrowsExactly<SourceOnlyPipeline.PipelineFailure>(() => SourceOnlyResultService.ShouldReadSubtitleEvidence(
                current, Enumerable.Repeat(fixture.Execution, SourceOnlyExecutionStore.MaximumListResults).ToArray(), observed));
        }
    }

    // Selection-only records: not persisted acceptance or successful runtime/media evidence.
    private static StoredSourceOnlyExecution AcceptanceSuccessor(StoredSourceOnlyExecution parent, bool capture)
    {
        SourceOnlyExecutionRequest request = parent.Request with
        {
            ExecutionId = EntityId<SourceOnlyExecutionIdTag>.New(),
            RootJobId = EntityId<JobIdTag>.New(),
            ParentVersionId = parent.VersionId,
            ParentDigest = parent.VersionDigest,
            CreatedAt = parent.Request.CreatedAt.AddMinutes(1),
            SubtitleEdits = capture ? [new SubtitleEditHeadReference(parent.VersionId!, 1, Hash("accepted guard fixture prefix"))] : [],
        };
        return new(request, "{\"selectionOnly\":true}", Hash("guard request"), 0, null, null, 1,
            SourceOnlyExecutionPhase.Failed, null, null, EntityId<CompositionVersionIdTag>.New(), Hash("guard version"),
            "source_only.guard_fixture", Hash("guard state"));
    }

    private static SourceOnlyGeneratedSubtitleRuntimeEvidence Project(SourceOnlyEvaluationFixture fixture, bool vertical = false)
    {
        StandardGenerationFixture generation = fixture.Generation;
        EntityId<StandardShortFormPlanIdTag>? shortId = vertical ? generation.Shorts.Single().ShortFormId : null;
        CompositionVersion targetVersion = vertical ? StandardRenderGraphFactory.CreateShortVersion(generation.Preparation, shortId!) : generation.Preparation.Child;
        LayoutSpec layout = StandardRenderGraphFactory.GetLayout(generation.Preparation.Plan, shortId);
        RenderProfile profile = StandardRenderGraphFactory.GetProfile(generation.Preparation.Plan, vertical, shortId);
        StandardSubtitleInput template = vertical ? generation.Shorts.Single().Subtitles : generation.Subtitles;
        ContentDigest layoutDigest = template.Revisions[0].Snapshot.LayoutResolution.ProfileVersionDigest;
        var layoutProfile = new SubtitleLayoutProfile(layout.Id, layoutDigest, layout.SafeZones.Select(z => z.Name).ToArray(),
            Enum.GetValues<SubtitleSoftConstraintKind>().Select(k => new SubtitleSoftConstraintWeight(k, 0)).ToArray());
        SourceOnlyGeneratedSubtitleRuntimeEvidence Initial(CompositionVersion version, TimelineMap timeline)
        {
            SubtitleCueHistory[] histories = template.Revisions.Select(revision =>
            {
                SubtitleCueCandidate candidate = template.Candidates.Single(c => c.Id == revision.SourceCandidateId);
                SubtitleLayoutResolution resolution = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(candidate, layout,
                    layoutProfile, [revision.Snapshot.ResolvedPlacement!], [], [], null));
                return SubtitleService.CreateInitial(version, candidate, resolution, EntityId<CueIdTag>.New(),
                    EntityId<SubtitleCueRevisionIdTag>.New(), revision.Snapshot.Cue.Range, StandardGenerationFixture.Actor,
                    version.CreatedAt, "Synthetic source-derived initial; not a saved pipeline checkpoint");
            }).ToArray();
            var input = new StandardSubtitleInput(histories, new SubtitleDisplayProfile(layoutDigest, SubtitleCueDisplayMode.Full,
                SubtitlePunctuationDisplay.HideJapaneseCommaAndPeriod), template.AssProfile);
            return new(version, timeline, layout, profile,
                new StandardSubtitleBuildResult(input, [], StandardSubtitleBuilder.RuleVersion, generation.Settings.ContentDigest), Array.AsReadOnly(histories));
        }
        var rootVersion = new CompositionVersion(EntityId<CompositionVersionIdTag>.New(), targetVersion.ProjectId, null,
            StandardGenerationFixture.Actor, targetVersion.CreatedAt, "Independent synthetic subtitle journal root",
            targetVersion.ContentDigest, targetVersion.Elements);
        fixture.Projects.AppendCompositionVersion(rootVersion, CompositionSnapshotJson.WriteVersion(rootVersion));
        SourceOnlyGeneratedSubtitleRuntimeEvidence root = Initial(rootVersion, TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), rootVersion));
        var snapshot = new LayoutSubtitleBridgeSnapshot(rootVersion.ProjectId, 0, rootVersion, layout, profile.Width, profile.Height,
            [new LayoutSubtitleSourceBinding(generation.Asset.Id, OriginRef.FromAsset(generation.Asset.Id), generation.Geometry)], [],
            root.Histories.Select(h => new LayoutSubtitleCueBinding(h, layout, layoutProfile, [], [])).ToArray(), [],
            [new LayoutSubtitleDisplayBinding(root.Build.Subtitles.DisplayProfile, [])], subtitleOnly: vertical);
        var journal = new SubtitleEditStore(generation.DatabasePath);
        var service = new DurableSubtitleEditService(journal);
        service.RegisterGenerated(snapshot, SubtitleEditProjection.GeneratedDigest(root));
        foreach (object edit in new object[] { new { kind = "text", text = "確認済みの訂正字幕。" }, new { kind = "style", style = "quote" } })
        {
            LayoutSubtitleBridgeSnapshot head = service.GetLatest(rootVersion.ProjectId, rootVersion.Id);
            SubtitleCueRevision cue = head.Subtitles.Single().History.CurrentRevision;
            var request = BridgeRequest.Create(Guid.NewGuid(), BridgeMethod.LayoutApply, JsonSerializer.SerializeToElement(new
            {
                projectId = rootVersion.ProjectId.ToString(),
                expectedRevision = head.Revision,
                expectedVersionId = head.CompositionVersion.Id.ToString(),
                expectedVersionDigest = head.CompositionVersion.ContentDigest.Hex,
                operation = new
                {
                    kind = "editSubtitle",
                    cueId = cue.Snapshot.Cue.Id.ToString(),
                    expectedRevisionId = cue.Id.ToString(),
                    expectedRevisionDigest = cue.ContentDigest.Hex,
                    reason = "Reviewed synthetic subtitle correction",
                    edit
                },
            }));
            Assert.AreEqual(LayoutSubtitleBridgeStatus.Available, service.Execute(request, StandardGenerationFixture.Actor, CancellationToken.None).Status);
        }
        StoredSubtitleEdit last = journal.ReadBranch(rootVersion.ProjectId, rootVersion.Id)[^1];
        SubtitleEditCapture capture = service.CapturePrefix(rootVersion.ProjectId, rootVersion.Id, last.Sequence, last.RecordDigest);
        TimelineMap targetTimeline = vertical ? TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), targetVersion) : generation.Preparation.ChildTimelineMap;
        SourceOnlyGeneratedSubtitleRuntimeEvidence target = Initial(targetVersion, targetTimeline);
        SubtitleEditProjectionResult projected = SubtitleEditProjection.Apply(capture, target, StandardGenerationFixture.Actor, last.OccurredAt.AddSeconds(1));
        StandardSubtitleBuildResult build = projected.Output.Build;
        return projected.Output with
        {
            SubtitleProjection = projected.Evidence,
            Build = new StandardSubtitleBuildResult(build.Subtitles.WithProjection(projected), build.Warnings, build.SegmentationRuleVersion, build.SettingsDigest),
        };
    }

    private static async Task<StandardGeneratedArtifact> ArtifactAsync(SourceOnlyEvaluationFixture fixture,
        SourceOnlyGeneratedSubtitleRuntimeEvidence runtime, SourceOnlyResultRole role)
    {
        StandardGenerationFixture generation = fixture.Generation;
        EntityId<StandardShortFormPlanIdTag>? shortId = role == SourceOnlyResultRole.VerticalBurnIn ? generation.Shorts.Single().ShortFormId : null;
        bool burn = role is SourceOnlyResultRole.LandscapeBurnIn or SourceOnlyResultRole.VerticalBurnIn;
        StandardRenderGraphOutput output = StandardRenderGraphFactory.Create(generation.Preparation, generation.Asset, generation.Geometry,
            burn, runtime.Build.Subtitles, generation.Font.Evidence, shortId);
        string wire = role switch
        {
            SourceOnlyResultRole.SubRip => "landscape-srt",
            SourceOnlyResultRole.WebVtt => "landscape-vtt",
            _ => output.Role,
        };
        bool video = role is not (SourceOnlyResultRole.SubRip or SourceOnlyResultRole.WebVtt);
        string mediaType = video ? "video/mp4" : role == SourceOnlyResultRole.SubRip ? "application/x-subrip" : "text/vtt";
        byte[] bytes = role switch
        {
            SourceOnlyResultRole.SubRip => SubRipSubtitleWriter.Write(output.DisplayCues).GetUtf8Bytes(),
            SourceOnlyResultRole.WebVtt => WebVttSubtitleWriter.Write(output.DisplayCues).GetUtf8Bytes(),
            _ => Encoding.UTF8.GetBytes("Synthetic video bytes; NOT an encoder/decode acceptance result: " + wire),
        };
        var id = EntityId<ArtifactIdTag>.New();
        string path = Path.Combine(generation.Root, "projects", generation.Project.Id.ToString(), "staging", id + ".bin");
        await File.WriteAllBytesAsync(path, bytes);
        StoredArtifact artifact = await generation.Store.PromoteAsync(new ArtifactPromotionRequest(id, generation.Project.Id, ArtifactKind.Render,
            path, $"projected-test/{id}.bin", ContentDigest.Compute(bytes), bytes.LongLength, SourceOnlyEvaluationFixture.Now));
        RenderGraphValidationContext context = output.ValidationContext;
        var binding = new StandardArtifactBindingEvidence(wire, id, generation.Preparation.Plan.Id, generation.Preparation.Plan.PlanDigest,
            runtime.Version.Id, runtime.Version.ContentDigest, generation.Settings.ContentDigest, generation.Original.Artifact.Id,
            Hash(generation.Index.CanonicalJson), output.Graph.Id, output.GraphDigest, StandardRenderGraphFactory.Digest(output.VideoGeometry),
            StandardRenderGraphFactory.Digest(output.DisplayCues), output.SubtitleConfigurationDigest, artifact.ContentDigest, artifact.ByteSize);
        var manifest = new RenderManifest(EntityId<RenderManifestIdTag>.New(), context.CompositionVersion, context.TimelineMap,
            context.TimelineMapDigest, context.Profile, StandardRenderGraphFactory.Digest(binding), "synthetic-test", Hash("synthetic application"),
            output.Graph.Inputs.Select(i => new RenderInputEvidence("synthetic/input:" + i.Id, i.ContentDigest)).ToArray(),
            [generation.ToolEvidence], [generation.Model], output.Graph.Fonts,
            [new ManifestArtifact(video ? "video" : "subtitle", mediaType, null, artifact.ByteSize, artifact.ContentDigest, artifact.StorageReference)]);
        return new StandardGeneratedArtifact(artifact, output.Graph, manifest, binding, context, output.DisplayCues,
            output.SubtitleFiles, output.SubtitleEvidence,
            video ? new OutputQcRequest(id, manifest, output.Graph, context, output.SubtitleEvidence, TimeSpan.FromMinutes(1)) : null,
            output.GraphicFiles)
        { SubtitleProjection = output.SubtitleProjection };
    }

    private static IReadOnlyList<SourceOnlyResultSubtitleMeasurement> Validate(SourceOnlyEvaluationFixture fixture,
        StandardGeneratedArtifact item, SourceOnlyRenderingInputsPayload payload, SourceOnlyGeneratedSubtitleRuntimeEvidence? runtime = null)
    {
        SourceOnlyResultRoleIdentity role = SourceOnlyOutputEvaluation.ParseRole(item.BindingEvidence.Role);
        var artifact = new SourceOnlyResultArtifact(role.Role, role.RenditionId, SourceOnlyResultEvidenceStore.Reference(item.Artifact),
            item.Graph.CompositionVersionId, item.Graph.CompositionVersionDigest, SourceOnlyResultEvidenceStore.Reference(fixture.Generation.Original.Artifact),
            item.BindingEvidence.GraphDigest, StandardRenderGraphFactory.Digest(item.Manifest), [], null, null, null,
            profileId: item.Graph.ProfileId, profileDigest: item.Graph.ProfileDigest);
        return SourceOnlyResultInputValidation.Validate(payload, artifact, fixture.Generation.Preparation, fixture.Generation.Settings,
            SourceOnlyResultEvidenceStore.Snapshot(item.Graph), SourceOnlyResultEvidenceStore.Snapshot(item.Manifest), runtime);
    }

    private static string Write<T>(T value) => SourceOnlyResultCodec.Write(value, SourceOnlyResultEvidenceStore.MaximumEvidenceBytes);
    private static JsonElement Element(JsonNode value) => SourceOnlyResultCodec.Element(CanonicalJson.Canonicalize(value.ToJsonString()));
    private static ContentDigest Hash(string value) => ContentDigest.Compute(Encoding.UTF8.GetBytes(value));
}