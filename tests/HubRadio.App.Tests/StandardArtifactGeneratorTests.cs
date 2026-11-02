using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Rendering;
using HubRadio.App.Subtitles;
using HubRadio.App.Transcripts;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;
using HubRadio.Infrastructure.Rendering;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StandardArtifactGeneratorTests
{
    // The null-muxer loudness pass writes no media file; only the final pass has an output to fake.
    private static bool IsLoudnessMeasurement(ProcessRunRequest process) =>
        process.ArgumentList.Count > 3 && process.ArgumentList[^3] == "null";

    [TestMethod]
    public async Task ActualBytesAreMeasuredAfterExecutionAndEveryVideoHasBoundQcRequest()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
        int calls = 0;
        var generatedBytes = new Dictionary<string, byte[]>();
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root, async (process, token) =>
        {
            if (IsLoudnessMeasurement(process))
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            // Explicit synthetic output: this test is not an encoder/quality test.
            byte[] bytes = Encoding.UTF8.GetBytes($"encoded fixture output {++calls}");
            generatedBytes.Add(process.ArgumentList[^1], bytes);
            await File.WriteAllBytesAsync(process.ArgumentList[^1], bytes, token);
            return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
        }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);

        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());

        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition,
            $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.IsTrue(result.IsComplete);
        Assert.IsFalse(result.Omissions.Any(item => item.Role == "EpisodeTitle"));
        Assert.IsTrue(result.Warnings.Any(item => item.Code == "LibraryAbsent"));
        Assert.IsTrue(result.Warnings.Any(item => item.Code == "NoEligibleNonDialogueCues"));
        Assert.HasCount(6, result.Artifacts); // clean + landscape burn-in + real short (burn-in + clean) + SRT + VTT
        Assert.AreEqual(4, calls);
        Assert.IsFalse(result.StagingCleanupRequired);
        Assert.IsEmpty(result.ReconciliationArtifactIds);
        foreach (StandardGeneratedArtifact output in result.Artifacts)
        {
            StoredArtifact actual = await fixture.Store.ValidateRegisteredAsync(output.Artifact.Id);
            byte[] bytes = File.ReadAllBytes(fixture.Store.GetValidatedRegisteredArtifactPath(actual.Id));
            Assert.AreEqual(ContentDigest.Compute(bytes), actual.ContentDigest);
            Assert.AreEqual(bytes.LongLength, actual.ByteSize);
            Assert.AreEqual(actual.Id, output.BindingEvidence.ArtifactId);
            Assert.AreEqual(actual.ContentDigest, output.BindingEvidence.OutputDigest);
            Assert.AreEqual(fixture.Settings.ContentDigest, output.BindingEvidence.SettingsSnapshotDigest);
            Assert.AreEqual(fixture.Preparation.Plan.PlanDigest, output.BindingEvidence.PlanDigest);
            Assert.AreEqual(StandardRenderGraphFactory.Digest(output.BindingEvidence), output.Manifest.ConfigurationDigest);
            StringAssert.Contains(output.Artifact.StorageReference, output.BindingEvidence.Role);
            if (output.QualityControlRequest is OutputQcRequest qc)
            {
                Assert.IsTrue(output.TypedArtifact.IsVideo);
                Assert.IsNotNull(output.BindingEvidence.Execution);
                Assert.IsNotNull(output.BindingEvidence.Execution.Loudness);
                Assert.AreEqual("render-filter-before-aac", output.BindingEvidence.Execution.Loudness.MeasurementStage);
                Assert.IsTrue(output.BindingEvidence.Execution.TextOverlays.Any(item => item.Role == "EpisodeTitle"));
                Assert.IsTrue(output.Manifest.Inputs.Any(item => item.Name.Contains(
                    "/text-role:EpisodeTitle/provenance:SourceAssetMetadata/", StringComparison.Ordinal)));
                OutputQc.ValidateEvidence(qc, CancellationToken.None);
                Assert.AreSame(output.Graph, qc.Graph);
                Assert.AreSame(output.Manifest, qc.RenderManifest);
                Assert.AreEqual(actual.Id, qc.OutputArtifactId);
                Assert.IsTrue(generatedBytes.Values.Any(candidate => ContentDigest.Compute(candidate) == actual.ContentDigest));
            }
            else
            {
                Assert.IsFalse(output.TypedArtifact.IsVideo);
                Assert.IsNull(output.BindingEvidence.Execution);
            }
        }

        Assert.HasCount(4, result.Artifacts.Where(static item => item.QualityControlRequest is not null).ToArray());
        StandardGeneratedArtifact srt = result.Artifacts.Single(item => item.BindingEvidence.Role == "landscape-srt");
        StandardGeneratedArtifact vtt = result.Artifacts.Single(item => item.BindingEvidence.Role == "landscape-vtt");
        Assert.AreEqual(srt.BindingEvidence.DisplayCueDigest, vtt.BindingEvidence.DisplayCueDigest);
        Assert.AreEqual("元の字幕", srt.DisplayCues.Single().Text);
        Assert.AreEqual(StandardArtifactRole.LandscapeSubRip, srt.TypedArtifact.Role);
        Assert.AreEqual(StandardArtifactRole.LandscapeWebVtt, vtt.TypedArtifact.Role);
        StandardGeneratedArtifact shortOutput = result.Artifacts.Single(item => item.TypedArtifact.Role == StandardArtifactRole.VerticalBurnInVideo);
        Assert.AreEqual(fixture.Preparation.Child.Id, shortOutput.Context.CompositionVersion.ParentVersionId);
        Assert.IsTrue(result.CompositionVersions.Any(version => version.Id == shortOutput.Context.CompositionVersion.Id));
        // FR-132 AC-136: the short is also produced without burned-in subtitles (clean), 1080x1920 like the burned-in short (FR-010 AC-002).
        StandardGeneratedArtifact shortClean = result.Artifacts.Single(item => item.TypedArtifact.Role == StandardArtifactRole.VerticalCleanVideo);
        StringAssert.StartsWith(shortClean.BindingEvidence.Role, "vertical-clean-");
        Assert.AreEqual(1080, shortClean.Context.Profile.Width);
        Assert.AreEqual(1920, shortClean.Context.Profile.Height);
        Assert.IsFalse(shortClean.Context.Profile.SubtitleBurnIn);
        Assert.IsNull(shortClean.SubtitleEvidence);
        Assert.AreEqual(1080, shortOutput.Context.Profile.Width);
        Assert.AreEqual(1920, shortOutput.Context.Profile.Height);
        Assert.AreEqual(fixture.Preparation.Child.Id, fixture.Versions.GetCurrentState(fixture.Project.Id)!.WorkingCopy.BaseVersionId);
    }

    [TestMethod]
    public async Task RepeatGenerationNeverOverwritesOriginalOrPriorArtifacts()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        var generator = CreateGenerator(fixture);
        StandardArtifactGenerationResult first = await generator.GenerateAsync(fixture.Request());
        StandardArtifactGenerationResult second = await generator.GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, first.Disposition);
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, second.Disposition);
        Assert.IsFalse(first.Artifacts.Select(static item => item.Artifact.Id)
            .Intersect(second.Artifacts.Select(static item => item.Artifact.Id)).Any());
        Assert.IsFalse(first.Artifacts.Select(static item => item.Artifact.StorageReference)
            .Intersect(second.Artifacts.Select(static item => item.Artifact.StorageReference)).Any());
        Assert.AreEqual(fixture.Asset.ContentDigest, (await fixture.Store.ValidateRegisteredAsync(fixture.Original.Artifact.Id)).ContentDigest);
        foreach (StandardGeneratedArtifact output in first.Artifacts)
        {
            Assert.AreEqual(output.Artifact.ContentDigest, (await fixture.Store.ValidateRegisteredAsync(output.Artifact.Id)).ContentDigest);
        }
    }

    [TestMethod]
    public async Task FailureAfterFirstVideoCleansAllStagingWithoutPublishingPartialSuccess()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        int calls = 0;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root, async (process, token) =>
        {
            await File.WriteAllBytesAsync(process.ArgumentList[^1], [1, 2, 3], token);
            return new ProcessRunResult(++calls == 4 ? 1 : 0, "", StandardGenerationFixture.LoudnessReport + "\nprivate diagnostic", false, TimeSpan.Zero); // call 4 = second video's final pass
        }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Failed, result.Disposition);
        Assert.AreEqual(StandardGenerationFailureReason.ProcessFailed, result.FailureReason);
        Assert.IsEmpty(result.Artifacts);
        Assert.IsEmpty(result.ReconciliationArtifactIds);
        Assert.IsFalse(result.StagingCleanupRequired);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(fixture.Root, "projects", fixture.Project.Id.ToString(), "staging"), "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task CancelledExecutionDoesNotPromoteEvenIfProcessWroteBytes()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root, async (process, token) =>
        {
            await File.WriteAllBytesAsync(process.ArgumentList[^1], [4, 5, 6], token);
            cancellation.Cancel();
            return new ProcessRunResult(0, "", "", false, TimeSpan.Zero);
        }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request(), cancellation.Token);
        Assert.AreEqual(StandardArtifactGenerationDisposition.Cancelled, result.Disposition);
        Assert.IsEmpty(result.Artifacts);
        Assert.IsFalse(result.StagingCleanupRequired);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task EmptyOutputIsNotPromotedAndAbsentSubtitlesAreExplicitOmissions()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        var emptyGenerator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root, async (process, token) =>
        {
            await File.WriteAllBytesAsync(process.ArgumentList[^1], [], token);
            return new ProcessRunResult(0, "", "", false, TimeSpan.Zero);
        }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        StandardArtifactGenerationResult failed = await emptyGenerator.GenerateAsync(fixture.Request());
        Assert.IsFalse(failed.IsComplete);
        Assert.IsEmpty(failed.Artifacts);

        StandardSubtitleInput noSubtitles = new([], fixture.Subtitles.DisplayProfile, fixture.Subtitles.AssProfile);
        StandardArtifactGenerationResult result = await CreateGenerator(fixture).GenerateAsync(fixture.Request(subtitles: noSubtitles));
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition);
        Assert.IsTrue(result.IsComplete);
        Assert.HasCount(1, result.Artifacts);
        Assert.AreEqual("landscape-clean", result.Artifacts[0].BindingEvidence.Role);
        Assert.IsTrue(result.Omissions.Any(item => item.Reason == "NoDisplayCues"));
        Assert.IsTrue(result.Omissions.Any(item => item.Reason == "NoGroundedCandidate"));
    }

    [TestMethod]
    public async Task RequestRejectsSettingsAndModelDriftBeforeExecution()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        DefaultSettingsSnapshot changed = fixture.SettingsProfile.CreateSnapshot(fixture.Settings.Values.Select(item =>
            item.Key == "project.verticalShortCandidateCount" ? new DefaultSettingSelection(item.Key, DefaultSettingValue.FromInteger(2)) : item).ToArray());
        Assert.AreEqual(StandardGenerationFailureReason.BindingMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request(settings: changed)).Reason);
        var model = new RenderModelEvidence("test", "fixture", "1", StandardGenerationFixture.Hash("different-model"));
        Assert.AreEqual(StandardGenerationFailureReason.BindingMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request(model: model)).Reason);
        Assert.IsFalse(typeof(StandardArtifactGenerationRequest).GetProperties().Any(static item =>
            item.Name.Contains("ExpectedOutput", StringComparison.Ordinal) || item.Name.Contains("Approval", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MeasurementUsesRawBytesNotNormalizedText()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("first\r\nsecond\r\n");
        string path = Path.Combine(fixture.Root, "measure.bin");
        await File.WriteAllBytesAsync(path, bytes);
        (ContentDigest digest, long size) = await StandardArtifactGenerator.MeasureAsync(path, CancellationToken.None);
        Assert.AreEqual(ContentDigest.Compute(bytes), digest);
        Assert.AreEqual(bytes.LongLength, size);
        Assert.AreNotEqual(StandardGenerationFixture.Hash("first\nsecond\n"), digest);
    }

    [TestMethod]
    public async Task PromotionFailureReturnsOnlyReconciliationIdsAndCleansUnpromotedFiles()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_second_render BEFORE INSERT ON artifacts
                WHEN NEW.kind = 'Render' AND (SELECT COUNT(*) FROM artifacts WHERE kind = 'Render') >= 1
                BEGIN SELECT RAISE(ABORT, 'synthetic promotion failure'); END;
                """;
            command.ExecuteNonQuery();
        }

        StandardArtifactGenerationResult result = await CreateGenerator(fixture).GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.ReconciliationRequired, result.Disposition);
        Assert.IsEmpty(result.Artifacts);
        Assert.HasCount(2, result.ReconciliationArtifactIds); // registered first + attempted second, even if rollback succeeded
        Assert.IsNotNull(fixture.Store.GetArtifact(result.ReconciliationArtifactIds[0]));
        Assert.IsNull(fixture.Store.GetArtifact(result.ReconciliationArtifactIds[1]));
        Assert.IsFalse(result.StagingCleanupRequired);
        Assert.HasCount(2, fixture.Store.ListProjectArtifacts(fixture.Project.Id)); // untouched original + orphan to reconcile
    }

    [TestMethod]
    public async Task UncheckpointedPreparationCannotStartFileOrProcessWork()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(persistPreparation: false);
        string[] before = Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories);
        Assert.IsNull(fixture.Versions.GetStandardComposition(fixture.Preparation.Child.Id));
        StandardGenerationException error = await Assert.ThrowsExactlyAsync<StandardGenerationException>(() =>
            CreateGenerator(fixture).GenerateAsync(fixture.Request()));
        Assert.AreEqual(StandardGenerationFailureReason.PreparationNotCheckpointed, error.Reason);
        CollectionAssert.AreEquivalent(before, Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories));
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task SameChildIdAndContentButDifferentCanonicalVersionCannotGenerate()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardCompositionPreparation preparation = fixture.Preparation;
        CompositionVersion child = preparation.Child;
        JsonObject root = JsonNode.Parse(CompositionSnapshotJson.WriteVersion(child))!.AsObject();
        root["changeReason"] = "Unstored change reason";
        CompositionVersion changed = CompositionSnapshotJson.ReadVersion(CanonicalJson.Canonicalize(root.ToJsonString()));
        var forged = new StandardCompositionPreparation(preparation.ApplicationDigest, preparation.Plan, changed,
            preparation.PreparedWorkingCopy, preparation.ExpectedWorkingCopyRevision, preparation.ParentTimelineMap, preparation.ChildTimelineMap);
        StandardGenerationException error = await Assert.ThrowsExactlyAsync<StandardGenerationException>(() =>
            CreateGenerator(fixture).GenerateAsync(fixture.Request(preparation: forged)));
        Assert.AreEqual(StandardGenerationFailureReason.PreparationNotCheckpointed, error.Reason);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NonFrameAlignedSourceStartOrDurationIsRejectedWithoutChangingTheTimeline(bool invalidStart)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            firstCutStart: invalidStart ? new RationalTime(101, 100) : null,
            firstCutDuration: invalidStart ? null : new RationalTime(101, 100));
        string before = CompositionSnapshotJson.WriteVersion(fixture.Preparation.Child);
        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSourceTiming,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSourceTiming,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Graph(false)).Reason);
        Assert.AreEqual(before, CompositionSnapshotJson.WriteVersion(fixture.Versions.GetVersion(fixture.Preparation.Child.Id)!));
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequestAndFactoryAcceptActualCanonical1920By1080Geometry(bool includeShort)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            includeShort: includeShort, persistPreparation: false);
        using JsonDocument document = JsonDocument.Parse(fixture.Preparation.Child.Evidence!.CanonicalJson);
        JsonElement captured = document.RootElement.GetProperty("input").GetProperty("sourceDisplayGeometry");
        Assert.AreEqual("192e1", captured.GetProperty("width").GetRawText());
        Assert.AreEqual("108e1", captured.GetProperty("height").GetRawText());

        StandardArtifactGenerationRequest request = fixture.Request();
        Assert.AreEqual(new SourceDisplayGeometry(1920, 1080), request.SourceDisplayGeometry);
        Assert.AreEqual(includeShort ? 1 : 0, request.ShortForms.Count);
        Assert.AreEqual(fixture.Preparation.Child.Id, fixture.Graph(false).Graph.CompositionVersionId);
    }

    [TestMethod]
    [DataRow("width", "1920.5")]
    [DataRow("height", "1080.5")]
    [DataRow("width", "1920.0000000000000000000000000000001")]
    [DataRow("height", "1080.0000000000000000000000000000001")]
    [DataRow("width", "2147483648")]
    [DataRow("height", "2147483648")]
    [DataRow("width", "-2147483649")]
    [DataRow("height", "-2147483649")]
    [DataRow("width", "1e1000")]
    [DataRow("height", "1e-1000")]
    [DataRow("width", "\"1920\"")]
    [DataRow("height", "null")]
    public async Task RequestAndFactoryRejectInvalidCapturedGeometry(string field, string valueJson)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(persistPreparation: false);
        StandardCompositionPreparation preparation = fixture.Preparation;
        CompositionVersion child = preparation.Child;
        JsonNode evidence = JsonNode.Parse(child.Evidence!.CanonicalJson)!;
        evidence["input"]!["sourceDisplayGeometry"]![field] = JsonNode.Parse(valueJson);
        CompositionEvidenceEnvelope changedEvidence = CompositionEvidenceEnvelope.Create(
            child.Evidence.Kind, CanonicalJson.Canonicalize(evidence.ToJsonString()));
        JsonObject root = JsonNode.Parse(CompositionSnapshotJson.WriteVersion(child))!.AsObject();
        root["evidence"]!["canonicalJson"] = changedEvidence.CanonicalJson;
        root["evidence"]!["contentDigest"] = changedEvidence.ContentDigest.Hex;
        CompositionVersion changedChild = CompositionSnapshotJson.ReadVersion(CanonicalJson.Canonicalize(root.ToJsonString()));
        var changed = new StandardCompositionPreparation(preparation.ApplicationDigest, preparation.Plan, changedChild,
            preparation.PreparedWorkingCopy, preparation.ExpectedWorkingCopyRevision,
            preparation.ParentTimelineMap, preparation.ChildTimelineMap);

        Assert.AreEqual(StandardGenerationFailureReason.BindingMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request(preparation: changed)).Reason);
        Assert.AreEqual(StandardGenerationFailureReason.BindingMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => StandardRenderGraphFactory.Create(
                changed, fixture.Asset, fixture.Geometry, false, fixture.Subtitles, fixture.Font.Evidence)).Reason);
    }

    [TestMethod]
    public async Task NoShortFormsStillRequireSameCapturedAndMeasuredSourceGeometry()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        Assert.IsEmpty(fixture.Preparation.Plan.ShortForms);
        Assert.AreEqual(StandardGenerationFailureReason.SourceMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request(geometry: new SourceDisplayGeometry(1280, 720))).Reason);
        StandardArtifactGenerationResult nominal = await CreateGenerator(fixture).GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, nominal.Disposition);
        Assert.HasCount(4, nominal.Artifacts);
        var foreignProbe = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            (_, _) => throw new AssertFailedException("Encoder must not start for foreign geometry."),
            StandardGenerationFixture.CompileAsync,
            (_, _, _) => Task.FromResult(new SourceDisplayGeometry(1280, 720)));
        StandardArtifactGenerationResult failed = await foreignProbe.GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Failed, failed.Disposition);
        Assert.AreEqual(StandardGenerationFailureReason.SourceMismatch, failed.FailureReason);
        Assert.IsEmpty(failed.Artifacts);
    }

    // FR-132: a transient clean refusal (nothing moved) of the first promotion is repeated once; the set completes.
    [TestMethod]
    public async Task TransientCleanPromotionRefusalIsRepeatedAndGenerationCompletes()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        int calls = 0;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [7, 8, 9], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync,
            (promotion, token) => ++calls == 1
                ? throw new ArtifactStoreException(ArtifactStoreFailureReason.UnsafePath, reconciliationRequired: false)
                : fixture.Store.PromoteAsync(promotion, token));
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition, $"{result.FailureReason}");
        Assert.IsGreaterThan(1, calls);
        Assert.IsFalse(result.StagingCleanupRequired);
    }

    [TestMethod]
    public async Task RollbackFailedKeepsUnregisteredMovedArtifactDescriptor()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        ArtifactPromotionRequest? attempted = null;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [7, 8, 9], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync,
            (promotion, _) =>
            {
                attempted = promotion;
                string destination = Path.Combine(fixture.Root, "projects", promotion.ProjectId.ToString(), "renders",
                    promotion.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(promotion.StagingPath, destination); // model the move before a failed INSERT and rollback
                throw new ArtifactStoreException(ArtifactStoreFailureReason.RollbackFailed, reconciliationRequired: true);
            });
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.ReconciliationRequired, result.Disposition);
        Assert.IsNotNull(attempted);
        Assert.IsEmpty(result.Artifacts);
        Assert.HasCount(1, result.ReconciliationArtifacts);
        StandardPromotionAttempt descriptor = result.ReconciliationArtifacts[0];
        Assert.AreEqual(attempted.Id, descriptor.ArtifactId);
        Assert.AreEqual(attempted.ProjectId, descriptor.ProjectId);
        Assert.AreEqual(attempted.StagingPath, descriptor.StagingPath);
        Assert.AreEqual(attempted.ExpectedDigest, descriptor.ContentDigest);
        Assert.AreEqual(attempted.ExpectedByteSize, descriptor.ByteSize);
        Assert.AreEqual($"projects/{fixture.Project.Id}/renders/{attempted.RelativePath}", descriptor.StorageReference);
        Assert.IsNull(fixture.Store.GetArtifact(descriptor.ArtifactId));
        string moved = Path.Combine(fixture.Root, descriptor.StorageReference.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual(descriptor.ContentDigest, ContentDigest.Compute(File.ReadAllBytes(moved)));
        Assert.IsFalse(result.StagingCleanupRequired);
    }

    // FR-132 AC-136 / AC-137 (E2E-006): a transient SQLITE_BUSY-class refusal on a later promotion (nothing moved, rolled back)
    // must be repeated for that item instead of demoting the whole run to ReconciliationRequired.
    [TestMethod]
    public async Task TransientDatabaseRefusalOnLaterPromotionIsRetriedAndRunCompletes()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        int promotions = 0;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                if (IsLoudnessMeasurement(process))
                    return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [7, 8, 9], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync,
            (promotion, token) =>
            {
                if (++promotions == 2)
                {
                    throw new ArtifactStoreException(ArtifactStoreFailureReason.DatabaseRejected, reconciliationRequired: false);
                }
                return fixture.Store.PromoteAsync(promotion, token);
            });
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition,
            $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.IsEmpty(result.ReconciliationArtifactIds);
    }

    // FR-047.1 AC-022.1: hubradio-sample.mp4 ends its AAC audio 23/750 s (30.67 ms) before
    // the last video frame. Up to 250 ms is padded with silence; anything longer is rejected.
    [TestMethod]
    [DataRow(23L, 750L, true)]
    [DataRow(1L, 4L, true)]
    [DataRow(251L, 1000L, false)]
    [DataRow(1L, 1L, false)]
    public async Task ShortSourceAudioTailIsAcceptedOnlyWithinTheBoundedShortfall(long shortfallValue, long shortfallRate, bool accepted)
    {
        // The fixture's last kept cut ends at 70 s.
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            sourceAudioDuration: new RationalTime(70, 1) - new RationalTime(shortfallValue, shortfallRate));
        if (accepted)
        {
            Assert.IsNotNull(fixture.Request());
        }
        else
        {
            Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSourceTiming,
                Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        }
    }

    [TestMethod]
    public async Task AssignedMusicAndNonzeroSourceClockAreExplicitlyUnsupported()
    {
        MusicPlanningResult music = new MusicPlanningService().Plan(MusicCatalog.CreateAvailable(
            [new MusicTrack(StandardGenerationFixture.Hash("music"), 100, 10m, "mp3", 48000, 2)]),
            [new MusicCue(StandardGenerationFixture.Hash("music-cue"), MusicCueKind.TitleCard, 5m, false)],
            MusicPlanningOptions.Default);
        using (StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(musicPlanning: music))
        {
            Assert.IsNotNull(fixture.Preparation.Plan.MusicPlanning.Assignments.Single().TrackDigest);
            Assert.AreEqual(StandardGenerationFailureReason.UnsupportedMusic,
                Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        }
        using (StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(sourceClockStart: new RationalTime(1, 1)))
        {
            Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSourceTiming,
                Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        }
    }

    [TestMethod]
    public async Task ExplicitNonSpeechCardMusicIsVerifiedAndMixedBeforeNormalization()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeCardMusic: true);
        Assert.IsNotNull(fixture.Music);
        string musicPath = fixture.Music.Tracks.Single().FilePath;
        int catalogChecks = 0;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                string[] args = process.ArgumentList.ToArray();
                CollectionAssert.Contains(args, musicPath);
                CollectionAssert.DoesNotContain(args, "-stream_loop");
                string filter = await File.ReadAllTextAsync(args[Array.IndexOf(args, "-/filter_complex") + 1], token);
                StringAssert.Contains(filter, "adelay=960000S:all=1");
                StringAssert.Contains(filter, "amix=inputs=2:duration=first:dropout_transition=0:normalize=0");
                Assert.IsTrue(filter.IndexOf("amix=", StringComparison.Ordinal) < filter.IndexOf("loudnorm=", StringComparison.Ordinal));
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [7, 8, 9], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync,
            verifyMusic: (_, _) => { catalogChecks++; return Task.FromResult(true); });
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.IsTrue(result.IsComplete, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.AreEqual(1, catalogChecks);
        foreach (StandardGeneratedArtifact video in result.Artifacts.Where(static item => item.TypedArtifact.IsVideo))
        {
            StandardMusicRenderEvidence music = video.BindingEvidence.Execution!.Music!.Single();
            Assert.AreEqual(fixture.Music.Catalog.CatalogDigest, music.CatalogDigest);
            Assert.AreEqual(fixture.Music.Tracks.Single().ExpectedDigest, music.TrackDigest);
            Assert.AreEqual(fixture.TranscriptDigest, music.TranscriptDigest);
            Assert.AreEqual(StandardGenerationFixture.Range(20, 5), music.OutputRange);
            Assert.AreEqual(MusicCueKind.TopicCard, music.Kind);
            Assert.IsFalse(music.Loop);
            Assert.IsFalse(music.Stretch);
            OutputQc.ValidateEvidence(video.QualityControlRequest!, CancellationToken.None);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MusicCannotOverlapSpeechUseForeignCatalogOrModifiedBytes(bool preserveSize)
    {
        using (StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeCardMusic: true, musicSpeechOverlap: true))
        {
            Assert.AreEqual(StandardGenerationFailureReason.UnsupportedMusic,
                Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        }
        using (StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeCardMusic: true))
        {
            StandardMusicRenderInput original = fixture.Music!;
            var wrong = new StandardMusicRenderInput(MusicCatalog.CreateUnavailable(MusicLibraryState.Absent, MusicCatalogWarning.LibraryAbsent),
                original.Transcript, original.Cards, original.Tracks);
            Assert.AreEqual(StandardGenerationFailureReason.UnsupportedMusic,
                Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request(music: wrong)).Reason);
            byte[] changed = preserveSize ? await File.ReadAllBytesAsync(original.Tracks.Single().FilePath) : [0, 0, 0];
            changed[0] ^= 1;
            await File.WriteAllBytesAsync(original.Tracks.Single().FilePath, changed);
            int processCalls = 0;
            var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
                (_, _) => { processCalls++; throw new AssertFailedException("Changed music must not reach the encoder."); },
                StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
            StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
            Assert.AreEqual(StandardArtifactGenerationDisposition.Failed, result.Disposition);
            Assert.AreEqual(StandardGenerationFailureReason.UnsupportedMusic, result.FailureReason);
            Assert.AreEqual(0, processCalls);
            Assert.IsEmpty(result.Artifacts);
            Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
        }
    }

    [TestMethod]
    public async Task MusicRespectsEligibleSectionSettingsRatherThanForcingAnAssignment()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeCardMusic: true, settingsOverrides:
            [new DefaultSettingSelection("assets.musicEligibleSections", DefaultSettingValue.FromTextList(["titleCard", "endCard"]))]);
        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedMusic,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
    }

    [TestMethod]
    public async Task UnavailableLibraryAtRenderIsExplicitWarningNotPhantomIntroOrFailure()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeCardMusic: true);
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                string[] args = process.ArgumentList.ToArray();
                CollectionAssert.DoesNotContain(args, fixture.Music!.Tracks.Single().FilePath);
                string filter = await File.ReadAllTextAsync(args[Array.IndexOf(args, "-/filter_complex") + 1], token);
                Assert.IsFalse(filter.Contains("amix=", StringComparison.Ordinal));
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [7, 8, 9], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync, verifyMusic: (_, _) => Task.FromResult(false));
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.IsTrue(result.IsComplete, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.IsTrue(result.Warnings.Any(item => item.Code == "MusicLibraryUnavailableAtRenderCompletedWithoutMusic"));
        Assert.IsTrue(result.Artifacts.Where(static item => item.TypedArtifact.IsVideo)
            .All(item => item.BindingEvidence.Execution!.Music!.Count == 0));
        Assert.AreEqual(new RationalTime(60, 1), result.Artifacts[0].Graph.Nodes.Single(node => node.Id == result.Artifacts[0].Graph.OutputNodeId).OutputRange.Duration);
    }

    [TestMethod]
    public async Task TimedAdditionalEndingFailsInsteadOfDisappearingFromTheOutput()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeTimedEnding: true);
        LongFormElementPlan ending = fixture.Preparation.Plan.LongForm.Elements.Single(item => item.Kind == LongFormElementKind.Ending);
        Assert.AreEqual(StandardGenerationFixture.Range(60, 5), ending.OutputRange);
        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedPlanElement,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task ThumbnailCannotUseAnAlteredPtsIndexEvenWhenChosenTimeStillMatches()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeThumbnail: true);
        PtsIndex original = fixture.Index;
        var altered = new PtsIndex(original.CandidateId, original.ProjectId, original.JobId, original.SourceFileName,
            original.ContentDigest, original.ByteSize, original.StreamIndex, original.TimeBase,
            original.Entries.Select(entry => entry.Sequence == 100 ? new PtsIndexEntry(entry.Sequence,
                entry.PresentationTime, entry.Duration, false) : entry).ToArray());
        Assert.AreEqual(original.Entries[450].PresentationTime, altered.Entries[450].PresentationTime);
        Assert.AreEqual(StandardGenerationFailureReason.SourceMismatch,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request(index: altered)).Reason);
    }

    [TestMethod]
    public async Task Source44100IsResampledToFixed48000()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            sourceSampleRate: 44100);
        Assert.AreEqual(44100, fixture.Original.MediaInfo.Streams.Single(item => item.Kind == MediaStreamType.Audio).SampleRate);
        StandardArtifactGenerationResult result = await CreateGenerator(fixture).GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition);
        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(new RationalTime(60, 1), result.Artifacts[0].Graph.Nodes
            .Single(node => node.Id == result.Artifacts[0].Graph.OutputNodeId).OutputRange.Duration);
        FfmpegCompilation compilation = await fixture.CompileGraphAsync(fixture.Graph(false));
        string[] args = compilation.ProcessRequest.ArgumentList.ToArray();
        Assert.AreEqual("48000", args[Array.IndexOf(args, "-ar") + 1]);
        Assert.AreEqual("aac_low", args[Array.IndexOf(args, "-profile:a") + 1]);
    }

    [TestMethod]
    public async Task PlannedThumbnailsAreRealExtractionCommandsAndTypedNonVideoArtifacts()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeThumbnail: true);
        var observed = new List<ProcessRunRequest>();
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                observed.Add(process);
                // Deliberately not PNG/MP4 bytes: this verifies command selection,
                // artifact binding and persistence, not actual image/video decoding.
                await File.WriteAllBytesAsync(process.ArgumentList[^1], Encoding.UTF8.GetBytes($"synthetic-output-{observed.Count}"), token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.IsTrue(result.IsComplete, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.HasCount(6, result.Artifacts); // Two videos, two sidecars, one source-frame candidate on two canvases.
        StandardGeneratedArtifact[] thumbnails = result.Artifacts.Where(item => item.TypedArtifact.Role is
            StandardArtifactRole.LandscapeThumbnail or StandardArtifactRole.VerticalThumbnail).ToArray();
        Assert.HasCount(2, thumbnails);
        foreach (StandardGeneratedArtifact thumbnail in thumbnails)
        {
            Assert.IsNull(thumbnail.QualityControlRequest);
            Assert.IsFalse(thumbnail.TypedArtifact.IsVideo);
            Assert.IsNotNull(thumbnail.BindingEvidence.Execution!.Thumbnail);
            Assert.IsNull(thumbnail.BindingEvidence.Execution.Loudness);
            Assert.IsNull(thumbnail.BindingEvidence.Execution.EncoderSettings);
            Assert.AreEqual(450, thumbnail.BindingEvidence.Execution.Thumbnail.SourceFrameSequence);
            Assert.AreEqual(new RationalTime(15, 1), thumbnail.BindingEvidence.Execution.Thumbnail.SourcePresentationTime);
            Assert.AreEqual(thumbnail.Artifact.ContentDigest, (await fixture.Store.ValidateRegisteredAsync(thumbnail.Artifact.Id)).ContentDigest);
        }
        Assert.AreEqual(2, observed.Count(process => process.ArgumentList.Contains("png")));
        Assert.IsFalse(result.Omissions.Any(item => item.Reason.Contains("RendererNotConnected", StringComparison.Ordinal)));
        Assert.IsFalse(result.StagingCleanupRequired);
    }

    [TestMethod]
    public async Task StandardGenerationAlwaysUsesSnapshotEncoderAndCompilerOwnedScript()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(settingsOverrides:
            [new DefaultSettingSelection("output.standardFrameRateBitrate", DefaultSettingValue.FromInteger(9_000_000)),
             new DefaultSettingSelection("output.stereoBitrate", DefaultSettingValue.FromInteger(192_000))]);
        int calls = 0;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                string[] args = process.ArgumentList.ToArray();
                if (IsLoudnessMeasurement(process))
                    return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
                Assert.AreEqual("100", args[Array.IndexOf(args, "-profile:v") + 1]);
                Assert.AreEqual("9000000", args[Array.IndexOf(args, "-b:v") + 1]);
                Assert.AreEqual("192000", args[Array.IndexOf(args, "-b:a") + 1]);
                Assert.AreEqual("30/1", args[Array.IndexOf(args, "-r") + 1]);
                string script = args[Array.IndexOf(args, "-/filter_complex") + 1];
                string text = await File.ReadAllTextAsync(script, token);
                StringAssert.Contains(text, "loudnorm=I=-16:TP=-1.1:LRA=11:measured_I=-22.00:");
                StringAssert.Contains(text, "ass=filename=");
                CollectionAssert.DoesNotContain(args, "-filter_complex");
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [1, 2, (byte)++calls], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.IsTrue(result.IsComplete, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.AreEqual(2, calls);
        Assert.IsTrue(result.Artifacts.Where(static item => item.TypedArtifact.IsVideo).All(item =>
            item.BindingEvidence.Execution!.EncoderSettings!.VideoBitrateBitsPerSecond == 9_000_000));
    }

    [TestMethod]
    public async Task LoudnessIsMeasuredFirstThenAppliedLinearlyWithTruePeakHeadroom()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        var scripts = new List<string>();
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                string[] args = process.ArgumentList.ToArray();
                scripts.Add(await File.ReadAllTextAsync(args[Array.IndexOf(args, "-/filter_complex") + 1], token));
                await File.WriteAllBytesAsync(args[^1], [1, 2, (byte)scripts.Count], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.IsTrue(result.IsComplete, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.HasCount(4, scripts); // measurement + final pass for each of the two videos
        for (int i = 0; i < scripts.Count; i += 2)
        {
            StringAssert.Contains(scripts[i], "loudnorm=I=-16:TP=-1.1:LRA=11:linear=false:print_format=json");
            Assert.IsFalse(scripts[i].Contains("yadif", StringComparison.Ordinal));
            StringAssert.Contains(scripts[i + 1], "loudnorm=I=-16:TP=-1.1:LRA=11:measured_I=-22.00:measured_TP=-5.00"
                + ":measured_LRA=4.00:measured_thresh=-32.00:offset=0.00:linear=true:print_format=json");
        }
        foreach (StandardGeneratedArtifact video in result.Artifacts.Where(static item => item.TypedArtifact.IsVideo))
        {
            FfmpegLoudnessTarget target = video.BindingEvidence.Execution!.Loudness!.Target;
            Assert.AreEqual(-1.1m, target.TruePeakDbtp);
            Assert.AreEqual(-22m, target.MeasuredIntegratedLufs);
            Assert.AreSame(target, video.BindingEvidence.Execution.EncoderSettings!.Loudness);
        }
        StandardArtifactGenerationResult repeat = await generator.GenerateAsync(fixture.Request());
        Assert.IsTrue(repeat.IsComplete, $"{repeat.FailureReason}/{repeat.CompilerFailureReason}");
        // Each run stages its ASS file under a fresh run directory, so compare scripts without those paths.
        static string WithoutRunPaths(string script) =>
            System.Text.RegularExpressions.Regex.Replace(script, "standard-[0-9a-f]{32}(/[0-9a-f-]{36}\\.ass)?", "run");
        CollectionAssert.AreEqual(scripts.Take(4).Select(WithoutRunPaths).ToArray(), scripts.Skip(4).Select(WithoutRunPaths).ToArray());
    }

    [TestMethod]
    public async Task EncoderAdapterSelectsHighRateAndPreservesMonoWithoutChangingLegacyDefaults()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardRenderConfiguration normal = StandardRenderConfiguration.FromSettings(fixture.SettingsProfile,
            fixture.Settings, new RationalNumber(30, 1), 2);
        StandardRenderConfiguration fast = StandardRenderConfiguration.FromSettings(fixture.SettingsProfile,
            fixture.Settings, new RationalNumber(60000, 1001), 1);
        Assert.AreEqual(8_000_000, normal.Encoder.VideoBitrateBitsPerSecond);
        Assert.AreEqual(12_000_000, fast.Encoder.VideoBitrateBitsPerSecond);
        Assert.AreEqual(384_000, normal.Encoder.AudioBitrateBitsPerSecond);
        Assert.AreEqual(fixture.Settings.Values.Single(item => item.Key == "output.monoBitrate").Value.IntegerValue,
            (long)fast.Encoder.AudioBitrateBitsPerSecond);
        Assert.AreEqual(1, fast.Encoder.AudioChannels);
        Assert.AreEqual(-16m, fast.Encoder.Loudness!.IntegratedLufs);
    }

    [TestMethod]
    // FR-015: the openers are no longer unsupported; their opt-in is covered by ShortFormOpenerAndProposalTests.
    [DataRow("editing.crossfadeEnabled")]
    [DataRow("audio.noiseReductionEnabled")]
    [DataRow("audio.humReductionEnabled")]
    public async Task UnsupportedActivatedSettingsCannotSilentlyComplete(string key)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(settingsOverrides:
            [new DefaultSettingSelection(key, DefaultSettingValue.FromBoolean(true))]);
        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSetting,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task UntimedEmptyEndingFailsExplicitlyInsteadOfOmittingPlannedContent()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeUntimedEnding: true);
        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedPlanElement,
            Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request()).Reason);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public async Task MissingLoudnessReportCannotPublishSuccessfulArtifacts()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [1, 2, 3], token);
                return new ProcessRunResult(0, "", "private path without measurements", false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.AreEqual(StandardArtifactGenerationDisposition.Failed, result.Disposition);
        Assert.AreEqual(StandardGenerationFailureReason.LoudnessReportInvalid, result.FailureReason);
        Assert.IsEmpty(result.Artifacts);
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    [TestMethod]
    public void LoudnessMeasurementsAreStrictBoundedAndNeverAQualityPass()
    {
        var target = new FfmpegLoudnessTarget(-16m, -1m);
        StandardLoudnessEvidence measured = StandardLoudnessReport.Read(
            new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero), target);
        Assert.AreEqual(-22m, measured.InputIntegratedLufs);
        Assert.AreEqual(-16m, measured.OutputIntegratedLufs);
        Assert.IsFalse(measured.Silent);
        Assert.IsFalse(typeof(StandardLoudnessEvidence).GetProperties().Any(static property =>
            property.Name.Contains("Pass", StringComparison.Ordinal) || property.Name.Contains("Approved", StringComparison.Ordinal)));
        foreach (string invalid in new[]
        {
            StandardGenerationFixture.LoudnessReport.Replace("-22.00", "nan", StringComparison.Ordinal),
            StandardGenerationFixture.LoudnessReport.Replace("-22.00", "-inf", StringComparison.Ordinal),
            StandardGenerationFixture.LoudnessReport.Replace("\"input_i\":", "\"input_tp\":", StringComparison.Ordinal),
            StandardGenerationFixture.LoudnessReport.Replace("dynamic", "unknown", StringComparison.Ordinal),
            new string('x', ProcessRunRequest.DefaultMaxCapturedBytes + 1),
        })
        {
            Assert.AreEqual(StandardGenerationFailureReason.LoudnessReportInvalid,
                Assert.ThrowsExactly<StandardGenerationException>(() => StandardLoudnessReport.Read(
                    new ProcessRunResult(0, "", invalid, false, TimeSpan.Zero), target)).Reason);
        }
        Assert.ThrowsExactly<StandardGenerationException>(() => StandardLoudnessReport.Read(
            new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, true, TimeSpan.Zero), target));
    }

    [TestMethod]
    public async Task MeasuredSilentAudioCompletesWithReasonWithoutInventingLoudness()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        const string silence = """
            [Parsed_loudnorm_99 @ synthetic]
            {
              "input_i": "-inf", "input_tp": "-inf", "input_lra": "0.00", "input_thresh": "-70.00",
              "output_i": "-inf", "output_tp": "-inf", "output_lra": "0.00", "output_thresh": "-70.00",
              "normalization_type": "linear", "target_offset": "inf"
            }
            """;
        int silentCalls = 0;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                silentCalls++;
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [1, 2, 3], token);
                return new ProcessRunResult(0, "", silence, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
        var empty = new StandardSubtitleInput([], fixture.Subtitles.DisplayProfile, fixture.Subtitles.AssProfile);
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request(subtitles: empty));
        Assert.IsTrue(result.IsComplete, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.HasCount(1, result.Artifacts);
        Assert.AreEqual(2, silentCalls);
        StandardLoudnessEvidence evidence = result.Artifacts[0].BindingEvidence.Execution!.Loudness!;
        Assert.IsFalse(evidence.Target.IsMeasured); // unmeasurable silence keeps the single-pass target
        Assert.IsTrue(evidence.Silent);
        Assert.IsNull(evidence.OutputIntegratedLufs);
        Assert.IsNull(evidence.OutputTruePeakDbtp);
        Assert.IsNull(evidence.TargetOffsetDb);
        Assert.IsTrue(result.Warnings.Any(item => item.Code == "SilentAudioLoudnessNotMeasurable"));
        Assert.IsNotNull(result.Artifacts[0].QualityControlRequest);
    }

    [TestMethod]
    [DataRow("smpte2084", FfmpegSourceColorModel.Bt2020Pq)]
    [DataRow("arib-std-b67", FfmpegSourceColorModel.Bt2020Hlg)]
    public async Task SourceColorProbeBindsActualStreamAndExplicitHdrConversion(string transfer, FfmpegSourceColorModel expected)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        string json = JsonSerializer.Serialize(new
        {
            streams = new[] { new { index = 0, width = 1920, height = 1080,
            color_primaries = "bt2020", color_transfer = transfer, color_space = "bt2020nc", color_range = "tv" } }
        });
        StandardSourceColorEvidence color = StandardSourceColor.Parse(fixture.Original.MediaInfo, json);
        Assert.AreEqual(fixture.Asset.ContentDigest, color.SourceDigest);
        Assert.AreEqual(expected, color.Conversion.Model);
        Assert.IsTrue(color.Conversion.ToneMapping);
        Assert.IsFalse(color.UsedUntaggedFallback);
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            async (process, token) =>
            {
                string[] args = process.ArgumentList.ToArray();
                if (IsLoudnessMeasurement(process))
                    return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
                string filter = await File.ReadAllTextAsync(args[Array.IndexOf(args, "-/filter_complex") + 1], token);
                StringAssert.Contains(filter, $"tin={transfer}");
                StringAssert.Contains(filter, "tonemap=tonemap=hable:desat=0");
                Assert.IsTrue(filter.IndexOf("tonemap=", StringComparison.Ordinal) < filter.IndexOf("format=yuv420p", StringComparison.Ordinal));
                await File.WriteAllBytesAsync(process.ArgumentList[^1], [1, 2, 3], token);
                return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
            }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync,
            probeColor: (_, _, _) => Task.FromResult<StandardSourceColorEvidence?>(color));
        StandardArtifactGenerationResult result = await generator.GenerateAsync(fixture.Request());
        Assert.IsTrue(result.IsComplete, $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.IsTrue(result.Warnings.Any(item => item.Code == "HdrToneMappedToSdrBt709NotQualityApproval"));
        Assert.IsTrue(result.Artifacts.Where(static item => item.TypedArtifact.IsVideo)
            .All(item => item.BindingEvidence.Execution!.SourceColor == color));
    }

    [TestMethod]
    public async Task UntaggedColorIsAnExplicitFallbackAndUnknownColorCannotBecomeBt709ByRetagging()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync();
        StandardSourceColorEvidence color = StandardSourceColor.Parse(fixture.Original.MediaInfo,
            """{"streams":[{"index":0,"width":1920,"height":1080}]}""");
        Assert.IsTrue(color.UsedUntaggedFallback);
        Assert.IsNull(color.Primaries);
        Assert.AreEqual(FfmpegSourceColorModel.Bt709Sdr, color.Conversion.Model);
        foreach (string invalid in new[]
        {
            """{"streams":[{"index":1,"width":1920,"height":1080}]}""",
            """{"streams":[{"index":0,"width":1280,"height":720}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"color_transfer":"smpte2084"}]}""",
            """{"streams":[{"index":0,"width":1920,"height":1080,"color_range":"tv;[0:v]null"}]}""",
        })
            Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSourceColor,
                Assert.ThrowsExactly<StandardGenerationException>(() => StandardSourceColor.Parse(fixture.Original.MediaInfo, invalid)).Reason);
    }

    [TestMethod]
    public async Task SubtitleWarningsKeepAllDefaultOutputsAndStableSourceAndSettingReferences()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true, includeThumbnail: true);
        var landscapeWarning = new StandardSubtitleBuildWarning("subtitles.sourceTiming", StandardSubtitleBuilder.RuleVersion + ".source-token",
            "missing-token-timing", true, fixture.Spans[1].Range, fixture.Spans[1].Id);
        var shortWarning = new StandardSubtitleBuildWarning("subtitles.platformUiMaskStatus", StandardSubtitleBuilder.RuleVersion + ".generic-safe-region",
            "platform-ui-mask-not-measured", false);
        StandardShortArtifactInput originalShort = fixture.Shorts.Single();
        StandardSubtitleInput landscape = StandardGenerationFixture.WithWarnings(fixture.Subtitles, [landscapeWarning]);
        var shortInput = new StandardShortArtifactInput(originalShort.ShortFormId,
            StandardGenerationFixture.WithWarnings(originalShort.Subtitles, [shortWarning]));

        StandardArtifactGenerationResult result = await CreateGenerator(fixture).GenerateAsync(
            fixture.Request(subtitles: landscape, shorts: [shortInput]));

        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition,
            $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.IsTrue(result.IsComplete);
        CollectionAssert.AreEquivalent(new[]
        {
            StandardArtifactRole.LandscapeCleanVideo, StandardArtifactRole.LandscapeBurnInVideo,
            StandardArtifactRole.VerticalBurnInVideo, StandardArtifactRole.VerticalCleanVideo, StandardArtifactRole.LandscapeSubRip,
            StandardArtifactRole.LandscapeWebVtt, StandardArtifactRole.LandscapeThumbnail,
            StandardArtifactRole.VerticalThumbnail,
        }, result.Artifacts.Select(static item => item.TypedArtifact.Role).ToArray());
        Assert.IsFalse(result.Omissions.Any(static item => item.Reason == "NoDisplayCues"));
        Assert.IsTrue(result.Artifacts.Where(static item => item.TypedArtifact.IsVideo).All(item =>
            item.BindingEvidence.Execution!.TextOverlays.Any(static text => text.Role == "EpisodeTitle")));
        AssertSubtitleWarning(result.Warnings.Single(static item => item.Role == "landscape-subtitles"),
            landscapeWarning, "landscape-subtitles", fixture);
        AssertSubtitleWarning(result.Warnings.Single(item => item.Role == $"vertical-subtitles-{shortInput.ShortFormId}"),
            shortWarning, $"vertical-subtitles-{shortInput.ShortFormId}", fixture);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StandardGenerationWarning>)result.Warnings).Clear());
    }

    [TestMethod]
    [DataRow(2, 1, 6)]
    [DataRow(8, 0, 3)]
    public async Task BuilderWarningsSurviveGenerationWithPartialOrEntirelyExcludedSubtitles(
        int secondTokenSeconds, int expectedCues, int expectedArtifacts)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            includeThumbnail: true, fontName: "Noto Sans JP", secondTokenDuration: new RationalTime(secondTokenSeconds, 1));
        string originalTranscript = TranscriptSnapshotJson.WriteRevision(fixture.Transcript);
        StandardSubtitleBuildResult built = StandardSubtitleBuilder.BuildWithEvidence(fixture.Preparation.Child,
            fixture.Preparation.ChildTimelineMap, fixture.Preparation.Plan.LongForm.Profile.Layout,
            fixture.Preparation.Plan.LongForm.Profile.Profile, fixture.Transcript, fixture.TranscriptDigest,
            fixture.Spans, fixture.SettingsProfile, fixture.Settings, fixture.Font.Evidence,
            StandardGenerationFixture.Actor, fixture.Transcript.CreatedAt);
        Assert.IsTrue(built.HasUnresolvedCues);
        Assert.HasCount(expectedCues, built.Subtitles.Revisions);
        CollectionAssert.AreEqual(built.Warnings.ToArray(), built.Subtitles.Warnings.ToArray());
        Assert.IsTrue(built.Warnings.Any(static warning => warning.Reason == "token-too-long-without-inner-timing" && warning.IsUnresolved));

        StandardArtifactGenerationResult result = await CreateGenerator(fixture).GenerateAsync(fixture.Request(subtitles: built.Subtitles));

        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition,
            $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.HasCount(expectedArtifacts, result.Artifacts);
        StandardGenerationWarning[] warnings = result.Warnings.Where(static item => item.Role == "landscape-subtitles").ToArray();
        Assert.HasCount(built.Warnings.Count, warnings);
        for (int i = 0; i < warnings.Length; i++)
            AssertSubtitleWarning(warnings[i], built.Warnings[i], "landscape-subtitles", fixture);
        Assert.IsTrue(warnings.Any(static item => item.Key == "subtitles.semanticCompleteness" && item.Code == "NOT EVALUATED"));
        StandardGeneratedArtifact clean = result.Artifacts.Single(static item => item.TypedArtifact.Role == StandardArtifactRole.LandscapeCleanVideo);
        Assert.HasCount(expectedCues, clean.DisplayCues);
        if (expectedCues == 1)
        {
            Assert.AreEqual(StandardGenerationFixture.Range(42, 2), built.Subtitles.Candidates.Single().SourceRange);
            Assert.AreEqual(StandardGenerationFixture.Range(32, 2), clean.DisplayCues.Single().Cue.Range);
            Assert.AreEqual("次の字幕", clean.DisplayCues.Single().Text);
            Assert.IsFalse(result.Omissions.Any(static item => item.Reason == "NoDisplayCues"));
        }
        else
        {
            Assert.IsTrue(result.Omissions.Any(static item => item.Role == "landscape-burn-in/srt/vtt" && item.Reason == "NoDisplayCues"));
        }
        Assert.AreEqual(originalTranscript, TranscriptSnapshotJson.WriteRevision(fixture.Transcript));
    }

    [TestMethod]
    [DataRow(300)]  // Hole inside a selected long-form cut.
    [DataRow(899)]  // The selected end no longer has a measured frame edge.
    [DataRow(1050)] // A hole outside the cuts still makes this source non-CFR.
    [DataRow(1200)] // The second selected start is on the nominal grid but absent.
    public async Task NominalThirtyCannotHideMissingMeasuredFrames(int missingFrame)
    {
        PtsIndexEntry[] frames = Enumerable.Range(0, 2400).Where(frame => frame != missingFrame)
            .Select((frame, sequence) => new PtsIndexEntry(sequence, new RationalTime(frame, 30), new RationalTime(1, 30), true)).ToArray();
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true,
            sourceFrames: frames, sourceTimeBase: new RationalTime(1, 90000));
        Assert.AreEqual(new RationalNumber(30, 1), fixture.Original.MediaInfo.PrimaryVideoStream.NominalFrameRate);
        Assert.AreEqual(new RationalTime(80, 1), fixture.Index.PresentationEnd);
        foreach (TimeRange range in fixture.Preparation.ChildTimelineMap.Entries.Select(static item => item.SourceRange!.Value)
            .Concat(fixture.Preparation.Plan.ShortForms.SelectMany(static item => item.SourceSpans).Select(static item => item.Range)))
        {
            Assert.IsTrue(FfmpegCompiler.IsOnOutputGrid(range.Start, fixture.Preparation.Plan.LongForm.Profile.Profile));
            Assert.IsTrue(FfmpegCompiler.IsOnOutputGrid(range.Duration, fixture.Preparation.Plan.LongForm.Profile.Profile));
        }
        await AssertUnsupportedTimingBeforeEncodingAsync(fixture);
    }

    [TestMethod]
    [DataRow("shifted-pts", 600)]
    [DataRow("shifted-pts", 1200)]
    [DataRow("short-duration", 600)]
    [DataRow("long-duration", 600)]
    [DataRow("short-duration", 899)]
    [DataRow("long-duration", 899)]
    [DataRow("short-duration", 1050)]
    [DataRow("short-duration", 2399)]
    public async Task EveryMeasuredFrameMustHaveExactCfrPtsAndDuration(string variation, int changedFrame)
    {
        PtsIndexEntry[] frames = Enumerable.Range(0, 2400).Select(frame => new PtsIndexEntry(frame,
            frame == changedFrame && variation == "shifted-pts" ? new RationalTime(2 * frame + 1, 60) : new RationalTime(frame, 30),
            frame == changedFrame && variation == "short-duration" ? new RationalTime(1, 60)
                : frame == changedFrame && variation == "long-duration" ? new RationalTime(1, 15) : new RationalTime(1, 30), true)).ToArray();
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            sourceFrames: frames, sourceTimeBase: new RationalTime(1, 60));
        await AssertUnsupportedTimingBeforeEncodingAsync(fixture);
    }

    [TestMethod]
    public async Task ContinuousVfrWithNominalThirtyIsRejectedWithoutRetimeOrFabricatedFrames()
    {
        PtsIndexEntry[] frames = Enumerable.Range(0, 2400).Select(frame => new PtsIndexEntry(frame,
            frame % 2 == 0 ? new RationalTime(frame, 30) : new RationalTime(2 * frame - 1, 60),
            new RationalTime(frame % 2 == 0 ? 1 : 3, 60), true)).ToArray();
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            sourceFrames: frames, sourceTimeBase: new RationalTime(1, 60));
        for (int i = 1; i < frames.Length; i++)
            Assert.AreEqual(frames[i - 1].PresentationTime + frames[i - 1].Duration, frames[i].PresentationTime);
        Assert.AreEqual(new RationalTime(80, 1), fixture.Index.PresentationEnd);
        Assert.AreEqual(new RationalNumber(30, 1), fixture.Original.MediaInfo.PrimaryVideoStream.NominalFrameRate);
        await AssertUnsupportedTimingBeforeEncodingAsync(fixture);
    }

    [TestMethod]
    public async Task SubFrameNonzeroSourceClockCannotBeRoundedToZero()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(
            sourceClockStart: new RationalTime(1, 90000), sourceTimeBase: new RationalTime(1, 90000));
        await AssertUnsupportedTimingBeforeEncodingAsync(fixture);
        Assert.AreEqual(new RationalTime(1, 90000), fixture.Index.PresentationStart);
    }

    [TestMethod]
    [DataRow(2100)] // The final selected edge is exactly the source's last frame end.
    [DataRow(2400)]
    public async Task ExactCfrEdgesIncludingLastFrameEndKeepAllDefaultOutputs(int frameCount)
    {
        PtsIndexEntry[] frames = Enumerable.Range(0, frameCount).Select(frame => new PtsIndexEntry(frame,
            new RationalTime(frame * 3000L, 90000), new RationalTime(3000, 90000), true)).ToArray();
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true, includeThumbnail: true,
            sourceFrames: frames, sourceTimeBase: new RationalTime(1, 90000));
        string originalIndex = fixture.Index.CanonicalJson;
        string originalVersion = CompositionSnapshotJson.WriteVersion(fixture.Preparation.Child);

        StandardArtifactGenerationResult result = await CreateGenerator(fixture).GenerateAsync(fixture.Request());

        Assert.AreEqual(StandardArtifactGenerationDisposition.Completed, result.Disposition,
            $"{result.FailureReason}/{result.CompilerFailureReason}");
        Assert.HasCount(8, result.Artifacts);
        Assert.AreEqual(originalIndex, fixture.Index.CanonicalJson);
        Assert.AreEqual(originalVersion, CompositionSnapshotJson.WriteVersion(fixture.Versions.GetVersion(fixture.Preparation.Child.Id)!));
    }

    private static async Task AssertUnsupportedTimingBeforeEncodingAsync(StandardGenerationFixture fixture)
    {
        string originalIndex = fixture.Index.CanonicalJson;
        string originalVersion = CompositionSnapshotJson.WriteVersion(fixture.Preparation.Child);
        string[] before = Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories);
        Assert.AreEqual(fixture.Index.Entries.Count, fixture.Original.DecodeValidation.IndexedFrameCount);
        Assert.AreEqual(fixture.Index.PresentationStart, fixture.Original.DecodeValidation.PresentationStart);
        Assert.AreEqual(fixture.Index.PresentationEnd, fixture.Original.DecodeValidation.PresentationEnd);
        int processCalls = 0;
        int compileCalls = 0;
        var generator = new StandardArtifactGenerator(fixture.Store, fixture.Versions, fixture.Root,
            (_, _) => { processCalls++; throw new AssertFailedException("No encode is allowed for unsupported source timing."); },
            (_, _) => { compileCalls++; throw new AssertFailedException("No compilation is allowed for unsupported source timing."); },
            fixture.ProbeGeometryAsync);

        StandardGenerationException failure = await Assert.ThrowsExactlyAsync<StandardGenerationException>(async () =>
            await generator.GenerateAsync(fixture.Request()));

        Assert.AreEqual(StandardGenerationFailureReason.UnsupportedSourceTiming, failure.Reason);
        Assert.AreEqual(0, compileCalls);
        Assert.AreEqual(0, processCalls);
        Assert.AreEqual(originalIndex, fixture.Index.CanonicalJson);
        Assert.AreEqual(originalVersion, CompositionSnapshotJson.WriteVersion(fixture.Versions.GetVersion(fixture.Preparation.Child.Id)!));
        CollectionAssert.AreEquivalent(before, Directory.GetFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories));
        Assert.HasCount(1, fixture.Store.ListProjectArtifacts(fixture.Project.Id));
    }

    private static void AssertSubtitleWarning(StandardGenerationWarning actual, StandardSubtitleBuildWarning expected,
        string role, StandardGenerationFixture fixture)
    {
        Assert.AreEqual(role, actual.Role);
        Assert.AreEqual(expected.Reason, actual.Code);
        Assert.AreEqual(expected.Key, actual.Key);
        Assert.AreEqual(expected.Rule, actual.Rule);
        Assert.AreEqual(expected.IsUnresolved, actual.IsUnresolved);
        Assert.AreEqual(expected.SourceRange, actual.SourceRange);
        Assert.AreEqual(expected.SourceSpanId, actual.SourceSpanId);
        Assert.AreEqual(fixture.Original.Artifact.Id, actual.SourceArtifactId);
        Assert.AreEqual(fixture.Settings.ContentDigest, actual.SettingsSnapshotDigest);
    }

    private static StandardArtifactGenerator CreateGenerator(StandardGenerationFixture fixture) => new(fixture.Store, fixture.Versions, fixture.Root,
        async (process, token) =>
        {
            await File.WriteAllBytesAsync(process.ArgumentList[^1], [7, 8, 9], token);
            return new ProcessRunResult(0, "", StandardGenerationFixture.LoudnessReport, false, TimeSpan.Zero);
        }, StandardGenerationFixture.CompileAsync, fixture.ProbeGeometryAsync);
}