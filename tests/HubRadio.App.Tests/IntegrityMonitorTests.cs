using System.Reflection;
using System.Text;
using System.Text.Json;
using HubRadio.App.Integrity;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class IntegrityMonitorTests
{
    [TestMethod]
    public async Task UnchangedEvidenceAllowsTechnicalRerenderWithNewOutput()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.OutputDigest = IntegrityFixture.Digest('e');
            builder.OutputStorageReference = "objects/current/rerender.mp4";
        });
        Assert.AreNotEqual(approved.Manifest.Id, current.Manifest.Id);

        IntegrityMonitorResult first = await fixture.EvaluateAsync(
            approved,
            current);
        IntegrityMonitorResult second = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(IntegrityDisposition.Unchanged, first.Disposition);
        Assert.IsTrue(first.IsUnchanged);
        Assert.IsNull(first.RequiredReviewState);
        Assert.HasCount(0, first.Findings);
        Assert.AreEqual(first.EvidenceDigest, second.EvidenceDigest);
    }

    [TestMethod]
    public async Task CoherentContentCorrectionRoutesToReReview()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.CompositionVersionId =
                EntityId<CompositionVersionIdTag>.New();
            builder.CompositionElementId =
                EntityId<CompositionElementIdTag>.New();
            builder.TimelineMapId = EntityId<TimelineMapIdTag>.New();
            builder.GraphId = EntityId<RenderGraphIdTag>.New();
        });

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(IntegrityDisposition.ReReview, result.Disposition);
        Assert.AreEqual(ReviewState.ChangesRequested, result.RequiredReviewState);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Composition
            && finding.Reason == IntegrityFindingReason.IdentityChanged));
        Assert.IsFalse(result.Findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.PublicationBlocked));
    }

    [TestMethod]
    public async Task SameCompositionIdentityMutationBlocksPublication()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.CompositionElementId =
                EntityId<CompositionElementIdTag>.New();
        });

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.AreEqual(
            ReviewState.PublicationBlocked,
            result.RequiredReviewState);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Composition
            && finding.Reason
                == IntegrityFindingReason.SameIdentityMutation));
    }

    [TestMethod]
    public async Task SameCompositionIdentityMetadataMutationBlocksPublication()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
            builder.CompositionChangeReason = "Rewritten audit metadata.");

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Composition
            && finding.Reason
                == IntegrityFindingReason.SameIdentityMutation));
    }

    [TestMethod]
    public async Task CoherentToolVersionBuildAndDigestChangeRoutesToReReview()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        ContentDigest currentDigest = fixture.WriteTool("tool-v2");
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.ToolVersion = "2.0.0";
            builder.ToolBuildConfiguration = "release+codec-b";
            builder.ToolDigest = currentDigest;
        });

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(IntegrityDisposition.ReReview, result.Disposition);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Tool
            && finding.Reason == IntegrityFindingReason.VersionChanged));
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Tool
            && finding.Reason
                == IntegrityFindingReason.ConfigurationChanged));
        Assert.IsFalse(result.Findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.PublicationBlocked));
    }

    [TestMethod]
    public async Task UndeclaredToolBinaryDigestDriftBlocksPublication()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot();
        IReadOnlyList<IntegrityFileBinding> files =
            fixture.CreateCurrentFiles(current);
        _ = fixture.WriteTool("tool-v2");

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current,
            files);

        AssertBlockedFileFinding(
            result,
            IntegrityEvidenceKind.Tool,
            IntegrityFindingReason.DigestMismatch);
    }

    [TestMethod]
    public async Task SameToolRevisionContainerDigestMutationBlocksPublication()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot(builder =>
            builder.ToolContainerDigest = IntegrityFixture.Digest('a'));
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
            builder.ToolContainerDigest = IntegrityFixture.Digest('b'));

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        IntegrityFinding finding = result.Findings.Single(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Tool
            && finding.Reason
                == IntegrityFindingReason.SameIdentityMutation);
        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.IsTrue(
            finding.ExpectedDigest == IntegrityFixture.Digest('a'));
        Assert.AreEqual(IntegrityFixture.Digest('b'), finding.ObservedDigest);
    }

    [TestMethod]
    public async Task ToolSizeDriftIsReportedWithoutPublishingAPath()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
        IReadOnlyList<IntegrityFileBinding> files =
            fixture.CreateCurrentFiles(snapshot);
        _ = fixture.WriteTool("tool-v1-with-extra-bytes");

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            snapshot,
            snapshot,
            files);

        AssertBlockedFileFinding(
            result,
            IntegrityEvidenceKind.Tool,
            IntegrityFindingReason.SizeMismatch);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains(
            fixture.RootPath,
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ModelVersionChangeRequiresReviewAndSameVersionMutationBlocks()
    {
        using (IntegrityFixture fixture = new())
        {
            IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
            ContentDigest currentDigest = fixture.WriteModel("model-v2");
            IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
            {
                builder.ModelVersion = "2.0.0";
                builder.ModelDigest = currentDigest;
            });

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                approved,
                current);

            Assert.AreEqual(IntegrityDisposition.ReReview, result.Disposition);
            Assert.IsTrue(result.Findings.Any(static finding =>
                finding.EvidenceKind == IntegrityEvidenceKind.Model
                && finding.Reason == IntegrityFindingReason.VersionChanged));
        }

        using (IntegrityFixture fixture = new())
        {
            IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
            ContentDigest currentDigest = fixture.WriteModel("model-v2");
            IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
                builder.ModelDigest = currentDigest);

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                approved,
                current);

            Assert.AreEqual(
                IntegrityDisposition.PublicationBlocked,
                result.Disposition);
            Assert.IsTrue(result.Findings.Any(static finding =>
                finding.EvidenceKind == IntegrityEvidenceKind.Model
                && finding.Reason
                    == IntegrityFindingReason.SameIdentityMutation));
        }
    }

    [TestMethod]
    public async Task PolicyChangeAndCoherentProfileTemplateChangeRequireReview()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.ConfigurationDigest = IntegrityFixture.Digest('b');
            builder.PublicationScope = PublicationScope.InternalOnly;
            builder.ProfileId = EntityId<RenderProfileIdTag>.New();
            builder.ProfileDigest = IntegrityFixture.Digest('c');
            builder.TemplateId = EntityId<TemplateIdTag>.New();
            builder.TemplateName = "Current approved template";
            builder.TemplateDigest = IntegrityFixture.Digest('d');
            builder.GraphId = EntityId<RenderGraphIdTag>.New();
        });

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(IntegrityDisposition.ReReview, result.Disposition);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                IntegrityEvidenceKind.Policy,
                IntegrityEvidenceKind.Profile,
                IntegrityEvidenceKind.Template,
                IntegrityEvidenceKind.Project,
            },
            result.Findings.Select(static finding => finding.EvidenceKind)
                .Distinct()
                .ToArray());
        Assert.IsFalse(result.Findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.PublicationBlocked));
    }

    [TestMethod]
    public async Task SameProfileOrTemplateIdentityMetadataMutationBlocks()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.ProfileWidth = 1280;
            builder.TemplateName = "Mutated template metadata";
            builder.LayoutAspectWidth = 4;
        });

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Profile
            && finding.Reason
                == IntegrityFindingReason.SameIdentityMutation));
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Template
            && finding.Reason
                == IntegrityFindingReason.SameIdentityMutation));
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Policy
            && finding.Reason
                == IntegrityFindingReason.SameIdentityMutation));
    }

    [TestMethod]
    public async Task ApplicationVersionChangeRequiresReviewButSameVersionMutationBlocks()
    {
        using (IntegrityFixture fixture = new())
        {
            IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
            ContentDigest appDigest = fixture.WriteApplication("app-v2");
            IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
            {
                builder.ApplicationVersion = "2.0.0";
                builder.ApplicationDigest = appDigest;
            });

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                approved,
                current);

            Assert.AreEqual(IntegrityDisposition.ReReview, result.Disposition);
            Assert.IsTrue(result.Findings.Any(static finding =>
                finding.EvidenceKind == IntegrityEvidenceKind.Application
                && finding.Reason == IntegrityFindingReason.VersionChanged));
        }

        using (IntegrityFixture fixture = new())
        {
            IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
            ContentDigest appDigest = fixture.WriteApplication("app-v2");
            IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
                builder.ApplicationDigest = appDigest);

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                approved,
                current);

            Assert.AreEqual(
                IntegrityDisposition.PublicationBlocked,
                result.Disposition);
            Assert.IsTrue(result.Findings.Any(static finding =>
                finding.EvidenceKind == IntegrityEvidenceKind.Application
                && finding.Reason
                    == IntegrityFindingReason.SameIdentityMutation));
        }
    }

    [TestMethod]
    public async Task CoherentFontReplacementRequiresReviewAndUndeclaredDriftBlocks()
    {
        using (IntegrityFixture fixture = new())
        {
            IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
            ContentDigest fontDigest = fixture.WriteFont("font-v2");
            IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
            {
                builder.FontName = "Replacement Font";
                builder.FontDigest = fontDigest;
                builder.GraphId = EntityId<RenderGraphIdTag>.New();
            });

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                approved,
                current);

            Assert.AreEqual(IntegrityDisposition.ReReview, result.Disposition);
            Assert.IsTrue(result.Findings.Any(static finding =>
                finding.EvidenceKind == IntegrityEvidenceKind.Font
                && finding.Reason == IntegrityFindingReason.IdentityChanged));
        }

        using (IntegrityFixture fixture = new())
        {
            IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
            IReadOnlyList<IntegrityFileBinding> files =
                fixture.CreateCurrentFiles(snapshot);
            _ = fixture.WriteFont("font-v2");

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                snapshot,
                snapshot,
                files);

            AssertBlockedFileFinding(
                result,
                IntegrityEvidenceKind.Font,
                IntegrityFindingReason.DigestMismatch);
        }
    }

    [TestMethod]
    public async Task SameInputIdentityDigestAndSizeMutationBlocksEvenWhenFileMatches()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        ContentDigest inputDigest = fixture.WriteInput("input-current");
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.InputDigest = inputDigest;
            builder.InputByteSize = Encoding.UTF8.GetByteCount("input-current");
            builder.ManifestInputDigest = inputDigest;
        });

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Input
            && finding.Reason
                == IntegrityFindingReason.SameIdentityMutation));
    }

    [TestMethod]
    public async Task NewInputIdentityInNewCompositionRoutesToReReview()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        ContentDigest inputDigest = fixture.WriteInput("input-current");
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
        {
            builder.CompositionVersionId =
                EntityId<CompositionVersionIdTag>.New();
            builder.CompositionElementId =
                EntityId<CompositionElementIdTag>.New();
            builder.TimelineMapId = EntityId<TimelineMapIdTag>.New();
            builder.GraphId = EntityId<RenderGraphIdTag>.New();
            builder.InputId = EntityId<RenderInputIdTag>.New();
            builder.AssetId = EntityId<AssetIdTag>.New();
            builder.InputDigest = inputDigest;
            builder.InputByteSize = Encoding.UTF8.GetByteCount("input-current");
            builder.ManifestInputDigest = inputDigest;
        });

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(IntegrityDisposition.ReReview, result.Disposition);
        Assert.AreEqual(ReviewState.ChangesRequested, result.RequiredReviewState);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Input
            && finding.Reason == IntegrityFindingReason.EvidenceChanged));
        Assert.IsFalse(result.Findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.PublicationBlocked));
    }

    [TestMethod]
    public async Task MissingInputFileBlocksWithTypedFinding()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
        List<IntegrityFileBinding> files =
            fixture.CreateCurrentFiles(snapshot).ToList();
        files.RemoveAll(static binding =>
            binding.Kind == IntegrityFileKind.Input);
        RenderGraphInput input = snapshot.Graph.Inputs.Single();
        files.Add(IntegrityFileBinding.ForInput(
            input.Id,
            Path.Combine(fixture.RootPath, "missing-input.bin"),
            input.ContentDigest,
            input.ByteSize));

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            snapshot,
            snapshot,
            files);

        AssertBlockedFileFinding(
            result,
            IntegrityEvidenceKind.Input,
            IntegrityFindingReason.FileMissing);
    }

    [TestMethod]
    public async Task MissingOriginalEvidenceBlocksWhenRenderInputIsAvailable()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
        List<IntegrityFileBinding> files =
            fixture.CreateCurrentFiles(snapshot).ToList();
        files.RemoveAll(static binding =>
            binding.Kind == IntegrityFileKind.Original);

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            snapshot,
            snapshot,
            files);

        AssertBlockedFileFinding(
            result,
            IntegrityEvidenceKind.Input,
            IntegrityFindingReason.EvidenceMissing);
        Assert.IsFalse(result.Findings.Any(static finding =>
            finding.ComponentIdentity.StartsWith(
                "input.",
                StringComparison.Ordinal)
            && finding.Reason == IntegrityFindingReason.EvidenceMissing));
    }

    [TestMethod]
    public async Task NonRegularAndReparseFilesFailClosed()
    {
        using (IntegrityFixture fixture = new())
        {
            IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
            List<IntegrityFileBinding> files =
                fixture.CreateCurrentFiles(snapshot).ToList();
            files.RemoveAll(static binding =>
                binding.Kind == IntegrityFileKind.ModelWeights);
            RenderModelEvidence model = snapshot.Manifest.Models.Single();
            files.Add(IntegrityFileBinding.ForModel(
                model.Provider,
                model.ModelIdentifier,
                fixture.RootPath,
                model.WeightsDigest,
                expectedByteSize: 0));

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                snapshot,
                snapshot,
                files);

            AssertBlockedFileFinding(
                result,
                IntegrityEvidenceKind.Model,
                IntegrityFindingReason.NotRegularFile);
        }

        using (IntegrityFixture fixture = new())
        {
            string linkPath = Path.Combine(fixture.RootPath, "font-link.bin");
            try
            {
                File.CreateSymbolicLink(linkPath, fixture.FontPath);
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
            {
                Assert.Inconclusive(
                    "Symbolic links are not available in this test environment.");
                return;
            }

            if ((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) == 0)
            {
                Assert.Inconclusive(
                    "The test file system does not expose links as reparse points.");
                return;
            }

            IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
            List<IntegrityFileBinding> files =
                fixture.CreateCurrentFiles(snapshot).ToList();
            files.RemoveAll(static binding =>
                binding.Kind == IntegrityFileKind.Font);
            RenderFontEvidence font = snapshot.Manifest.Fonts.Single();
            files.Add(IntegrityFileBinding.ForFont(
                font.Name,
                linkPath,
                font.FileDigest,
                new FileInfo(fixture.FontPath).Length));

            IntegrityMonitorResult result = await fixture.EvaluateAsync(
                snapshot,
                snapshot,
                files);

            AssertBlockedFileFinding(
                result,
                IntegrityEvidenceKind.Font,
                IntegrityFindingReason.ReparsePoint);
        }
    }

    [TestMethod]
    public async Task MissingUnexpectedAndInconsistentBindingsBlockPublication()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
        List<IntegrityFileBinding> files =
            fixture.CreateCurrentFiles(snapshot).ToList();
        files.RemoveAll(static binding =>
            binding.Kind == IntegrityFileKind.ModelWeights);
        RenderGraphInput input = snapshot.Graph.Inputs.Single();
        files.RemoveAll(static binding =>
            binding.Kind == IntegrityFileKind.Input);
        files.Add(IntegrityFileBinding.ForInput(
            input.Id,
            fixture.InputPath,
            IntegrityFixture.Digest('f'),
            input.ByteSize));
        files.Add(IntegrityFileBinding.ForFont(
            "Unexpected Font",
            fixture.FontPath,
            snapshot.Manifest.Fonts.Single().FileDigest,
            new FileInfo(fixture.FontPath).Length));

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            snapshot,
            snapshot,
            files);

        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Model
            && finding.Reason == IntegrityFindingReason.EvidenceMissing));
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Font
            && finding.Reason == IntegrityFindingReason.UnexpectedEvidence));
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Input
            && finding.Reason == IntegrityFindingReason.EvidenceInconsistent));
    }

    [TestMethod]
    public async Task InconsistentCurrentSnapshotBlocksInsteadOfTrustingManifest()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
            builder.ManifestInputDigest = IntegrityFixture.Digest('f'));

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            approved,
            current);

        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.IsTrue(result.Findings.Any(static finding =>
            finding.EvidenceKind == IntegrityEvidenceKind.Snapshot
            && finding.Reason
                == IntegrityFindingReason.EvidenceInconsistent));
    }

    [TestMethod]
    public async Task BlockingFindingsTakePrecedenceAndOrderingIsDeterministic()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot approved = fixture.CreateSnapshot();
        IntegrityEvidenceSnapshot current = fixture.CreateSnapshot(builder =>
            builder.ConfigurationDigest = IntegrityFixture.Digest('a'));
        IReadOnlyList<IntegrityFileBinding> files =
            fixture.CreateCurrentFiles(current);
        File.Delete(fixture.InputPath);

        IntegrityMonitorResult first = await fixture.EvaluateAsync(
            approved,
            current,
            files);
        IntegrityMonitorResult second = await fixture.EvaluateAsync(
            approved,
            current,
            files.Reverse().ToArray());

        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            first.Disposition);
        Assert.IsTrue(first.Findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.ReReview));
        Assert.IsTrue(first.Findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.PublicationBlocked));
        CollectionAssert.AreEqual(
            first.Findings.ToArray(),
            second.Findings.ToArray());
        Assert.AreEqual(first.EvidenceDigest, second.EvidenceDigest);
        CollectionAssert.AreEqual(
            first.Findings
                .OrderBy(static finding => finding.EvidenceKind)
                .ThenBy(
                    static finding => finding.ComponentIdentity,
                    StringComparer.Ordinal)
                .ThenBy(static finding => finding.Reason)
                .ToArray(),
            first.Findings.ToArray());
    }

    [TestMethod]
    public async Task CancellationIsPropagatedBeforeFileAccess()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                new IntegrityMonitor().EvaluateAsync(
                    new IntegrityMonitorRequest(
                        snapshot,
                        snapshot,
                        fixture.CreateCurrentFiles(snapshot)),
                    cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public async Task PublicResultsAreImmutableAndDoNotExposePathsOrActors()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
        IReadOnlyList<IntegrityFileBinding> files =
            fixture.CreateCurrentFiles(snapshot);
        File.Delete(fixture.ToolPath);

        IntegrityMonitorResult result = await fixture.EvaluateAsync(
            snapshot,
            snapshot,
            files);
        string serialized = JsonSerializer.Serialize(result);

        Assert.IsFalse(serialized.Contains(
            fixture.RootPath,
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains(
            "S-1-5-21",
            StringComparison.Ordinal));
        foreach (Type type in new[]
        {
            typeof(IntegrityFinding),
            typeof(IntegrityMonitorResult),
            typeof(IntegrityFileBinding),
        })
        {
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
            Assert.IsFalse(type.GetProperties(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly)
                .Any(static property => property.Name.Contains(
                    "Path",
                    StringComparison.OrdinalIgnoreCase)), type.Name);
        }

        Assert.AreEqual(0, typeof(IntegrityFinding).GetConstructors().Length);
        Assert.AreEqual(0, typeof(IntegrityMonitorResult).GetConstructors().Length);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<IntegrityFinding>)result.Findings).Clear());
    }

    [TestMethod]
    public void RequestRejectsDuplicateFileEvidenceIdentity()
    {
        using IntegrityFixture fixture = new();
        IntegrityEvidenceSnapshot snapshot = fixture.CreateSnapshot();
        IntegrityFileBinding input = fixture.CreateCurrentFiles(snapshot)
            .Single(static binding => binding.Kind == IntegrityFileKind.Input);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
            new IntegrityMonitorRequest(snapshot, snapshot, [input, input]));

        Assert.AreEqual("currentFiles", exception.ParamName);
    }

    private static void AssertBlockedFileFinding(
        IntegrityMonitorResult result,
        IntegrityEvidenceKind evidenceKind,
        IntegrityFindingReason reason)
    {
        Assert.AreEqual(
            IntegrityDisposition.PublicationBlocked,
            result.Disposition);
        Assert.AreEqual(
            ReviewState.PublicationBlocked,
            result.RequiredReviewState);
        Assert.IsTrue(result.Findings.Any(finding =>
            finding.EvidenceKind == evidenceKind
            && finding.Reason == reason
            && finding.Disposition
                == IntegrityDisposition.PublicationBlocked));
    }

    private sealed class IntegrityFixture : IDisposable
    {
        private static readonly DateTimeOffset CreatedAt =
            new(2047, 8, 9, 10, 11, 12, TimeSpan.Zero);

        private readonly EntityId<ProjectIdTag> _projectId =
            EntityId<ProjectIdTag>.New();
        private readonly EntityId<AssetIdTag> _assetId =
            EntityId<AssetIdTag>.New();
        private readonly EntityId<CompositionVersionIdTag> _compositionVersionId =
            EntityId<CompositionVersionIdTag>.New();
        private readonly EntityId<CompositionElementIdTag> _compositionElementId =
            EntityId<CompositionElementIdTag>.New();
        private readonly EntityId<TimelineMapIdTag> _timelineMapId =
            EntityId<TimelineMapIdTag>.New();
        private readonly EntityId<RenderProfileIdTag> _profileId =
            EntityId<RenderProfileIdTag>.New();
        private readonly EntityId<LayoutSpecIdTag> _layoutSpecId =
            EntityId<LayoutSpecIdTag>.New();
        private readonly EntityId<TemplateIdTag> _templateId =
            EntityId<TemplateIdTag>.New();
        private readonly EntityId<RenderGraphIdTag> _graphId =
            EntityId<RenderGraphIdTag>.New();
        private readonly EntityId<RenderInputIdTag> _inputId =
            EntityId<RenderInputIdTag>.New();
        private readonly EntityId<RenderNodeIdTag> _videoNodeId =
            EntityId<RenderNodeIdTag>.New();
        private readonly EntityId<RenderNodeIdTag> _outputNodeId =
            EntityId<RenderNodeIdTag>.New();

        internal IntegrityFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.IntegrityMonitorTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            InputPath = CreateFile("input.bin", "input-v1");
            ToolPath = CreateFile("tool.bin", "tool-v1");
            ModelPath = CreateFile("model.bin", "model-v1");
            FontPath = CreateFile("font.bin", "font-v1");
            ApplicationPath = CreateFile("application.bin", "app-v1");
        }

        internal string RootPath { get; }

        internal string InputPath { get; }

        internal string ToolPath { get; }

        internal string ModelPath { get; }

        internal string FontPath { get; }

        internal string ApplicationPath { get; }

        internal IntegrityEvidenceSnapshot CreateSnapshot(
            Action<SnapshotBuilder>? configure = null)
        {
            var builder = new SnapshotBuilder(this);
            configure?.Invoke(builder);
            return builder.Build();
        }

        internal IReadOnlyList<IntegrityFileBinding> CreateCurrentFiles(
            IntegrityEvidenceSnapshot snapshot)
        {
            RenderGraphInput input = snapshot.Graph.Inputs.Single();
            RenderToolEvidence tool = snapshot.Manifest.Tools.Single();
            RenderModelEvidence model = snapshot.Manifest.Models.Single();
            RenderFontEvidence font = snapshot.Manifest.Fonts.Single();
            return
            [
                IntegrityFileBinding.ForOriginal(
                    snapshot.ValidationContext.Project.Assets.Single().Id,
                    InputPath,
                    snapshot.ValidationContext.Project.Assets.Single()
                        .ContentDigest,
                    snapshot.ValidationContext.Project.Assets.Single()
                        .ByteSize),
                IntegrityFileBinding.ForInput(
                    input.Id,
                    InputPath,
                    input.ContentDigest,
                    input.ByteSize),
                IntegrityFileBinding.ForTool(
                    tool.Name,
                    ToolPath,
                    tool.ExecutableDigest,
                    new FileInfo(ToolPath).Length),
                IntegrityFileBinding.ForModel(
                    model.Provider,
                    model.ModelIdentifier,
                    ModelPath,
                    model.WeightsDigest,
                    new FileInfo(ModelPath).Length),
                IntegrityFileBinding.ForFont(
                    font.Name,
                    FontPath,
                    font.FileDigest,
                    new FileInfo(FontPath).Length),
                IntegrityFileBinding.ForApplication(
                    ApplicationPath,
                    snapshot.Manifest.ApplicationDigest,
                    new FileInfo(ApplicationPath).Length),
            ];
        }

        internal async Task<IntegrityMonitorResult> EvaluateAsync(
            IntegrityEvidenceSnapshot approved,
            IntegrityEvidenceSnapshot current,
            IReadOnlyList<IntegrityFileBinding>? files = null) =>
            await new IntegrityMonitor().EvaluateAsync(
                new IntegrityMonitorRequest(
                    approved,
                    current,
                    files ?? CreateCurrentFiles(current)));

        internal ContentDigest WriteInput(string value) =>
            WriteFile(InputPath, value);

        internal ContentDigest WriteTool(string value) =>
            WriteFile(ToolPath, value);

        internal ContentDigest WriteModel(string value) =>
            WriteFile(ModelPath, value);

        internal ContentDigest WriteFont(string value) =>
            WriteFile(FontPath, value);

        internal ContentDigest WriteApplication(string value) =>
            WriteFile(ApplicationPath, value);

        internal static ContentDigest Digest(char value) =>
            new(new string(value, 64));

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private string CreateFile(string name, string value)
        {
            string path = Path.Combine(RootPath, name);
            _ = WriteFile(path, value);
            return path;
        }

        private static ContentDigest WriteFile(string path, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            File.WriteAllBytes(path, bytes);
            return ContentDigest.Compute(bytes);
        }

        internal sealed class SnapshotBuilder
        {
            private readonly IntegrityFixture _fixture;

            internal SnapshotBuilder(IntegrityFixture fixture)
            {
                _fixture = fixture;
                ProjectId = fixture._projectId;
                AssetId = fixture._assetId;
                CompositionVersionId = fixture._compositionVersionId;
                CompositionElementId = fixture._compositionElementId;
                TimelineMapId = fixture._timelineMapId;
                ProfileId = fixture._profileId;
                LayoutSpecId = fixture._layoutSpecId;
                TemplateId = fixture._templateId;
                GraphId = fixture._graphId;
                InputId = fixture._inputId;
                VideoNodeId = fixture._videoNodeId;
                OutputNodeId = fixture._outputNodeId;
                InputDigest = HashFile(fixture.InputPath);
                InputByteSize = new FileInfo(fixture.InputPath).Length;
                ManifestInputDigest = InputDigest;
                ToolDigest = HashFile(fixture.ToolPath);
                ModelDigest = HashFile(fixture.ModelPath);
                FontDigest = HashFile(fixture.FontPath);
                ApplicationDigest = HashFile(fixture.ApplicationPath);
            }

            internal EntityId<ProjectIdTag> ProjectId { get; set; }

            internal EntityId<AssetIdTag> AssetId { get; set; }

            internal EntityId<CompositionVersionIdTag> CompositionVersionId
            {
                get;
                set;
            }

            internal EntityId<CompositionElementIdTag> CompositionElementId
            {
                get;
                set;
            }

            internal EntityId<TimelineMapIdTag> TimelineMapId { get; set; }

            internal EntityId<RenderProfileIdTag> ProfileId { get; set; }

            internal EntityId<LayoutSpecIdTag> LayoutSpecId { get; set; }

            internal EntityId<TemplateIdTag> TemplateId { get; set; }

            internal EntityId<RenderGraphIdTag> GraphId { get; set; }

            internal EntityId<RenderInputIdTag> InputId { get; set; }

            internal EntityId<RenderNodeIdTag> VideoNodeId { get; set; }

            internal EntityId<RenderNodeIdTag> OutputNodeId { get; set; }

            internal ContentDigest InputDigest { get; set; }

            internal long InputByteSize { get; set; }

            internal string CompositionChangeReason { get; set; } =
                "Build integrity evidence.";

            internal string ManifestInputName { get; set; } = "source-video";

            internal ContentDigest ManifestInputDigest { get; set; }

            internal string ToolName { get; set; } = "ffmpeg";

            internal string ToolVersion { get; set; } = "1.0.0";

            internal string ToolBuildConfiguration { get; set; } =
                "release+codec-a";

            internal ContentDigest ToolDigest { get; set; }

            internal ContentDigest? ToolContainerDigest { get; set; }

            internal string ModelProvider { get; set; } = "local";

            internal string ModelIdentifier { get; set; } = "layout-model";

            internal string ModelVersion { get; set; } = "1.0.0";

            internal ContentDigest ModelDigest { get; set; }

            internal string FontName { get; set; } = "Fixture Font";

            internal ContentDigest FontDigest { get; set; }

            internal ContentDigest ProfileDigest { get; set; } = Digest('5');

            internal int ProfileWidth { get; set; } = 1920;

            internal string TemplateName { get; set; } = "Fixture template";

            internal ContentDigest TemplateDigest { get; set; } = Digest('6');

            internal ContentDigest LayoutSpecDigest { get; set; } = Digest('7');

            internal int LayoutAspectWidth { get; set; } = 16;

            internal ContentDigest ConfigurationDigest { get; set; } = Digest('8');

            internal PublicationScope PublicationScope { get; set; } =
                PublicationScope.ExternalPublicationApproved;

            internal string ApplicationVersion { get; set; } = "1.0.0";

            internal ContentDigest ApplicationDigest { get; set; }

            internal ContentDigest OutputDigest { get; set; } = Digest('9');

            internal string OutputStorageReference { get; set; } =
                "objects/approved/render.mp4";

            internal IntegrityEvidenceSnapshot Build()
            {
                var rights = new RightsRecord(
                    RightsStatus.Approved,
                    "fixture-rights",
                    "Integrity monitor test use.",
                    externalPublicationAllowed: true);
                var asset = new Asset(
                    AssetId,
                    "source-video.mp4",
                    "video/mp4",
                    InputDigest,
                    InputByteSize,
                    rights);
                var project = new Project(
                    ProjectId,
                    "Integrity monitor fixture",
                    "Verify post-approval drift.",
                    "Test operators",
                    PublicationScope,
                    deadline: null,
                    [asset],
                    performers: []);
                var range = new TimeRange(
                    default,
                    new RationalTime(1, 1));
                OriginRef origin = OriginRef.FromAsset(AssetId);
                var element = new CompositionElement(
                    CompositionElementId,
                    range,
                    origin,
                    sourceRange: null);
                var composition = new CompositionVersion(
                    CompositionVersionId,
                    ProjectId,
                    parentVersionId: null,
                    "S-1-5-21-100-200-300-1001",
                    CreatedAt,
                    CompositionChangeReason,
                    RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                        [element]),
                    [element]);
                var timeline = new TimelineMap(
                    TimelineMapId,
                    CompositionVersionId,
                    [new TimelineMapEntry(range, origin, sourceRange: null)]);
                ContentDigest timelineDigest =
                    RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                        timeline);
                var template = new TemplateReference(
                    TemplateId,
                    TemplateName,
                    TemplateDigest);
                var layout = new LayoutSpec(
                    LayoutSpecId,
                    LayoutAspectWidth,
                    9,
                    [
                        new LayoutRegion(
                            "main",
                            new NormalizedRect(
                                default,
                                default,
                                new RationalNumber(1, 1),
                                new RationalNumber(1, 1)),
                            LayoutRegionRole.Video),
                    ],
                    safeZones: []);
                var profile = new RenderProfile(
                    ProfileId,
                    "Integrity profile",
                    ProfileDigest,
                    ProfileWidth,
                    1080,
                    30,
                    1,
                    LayoutSpecId,
                    TemplateId,
                    TemplateDigest,
                    "h264",
                    "aac",
                    subtitleBurnIn: false);
                var graphInput = new RenderGraphInput(
                    InputId,
                    RenderInputKind.Video,
                    InputDigest,
                    InputByteSize,
                    origin);
                RenderNode videoNode = RenderNode.Video(
                    VideoNodeId,
                    InputId,
                    range,
                    origin,
                    sourceRange: null,
                    "main");
                RenderNode outputNode = RenderNode.Output(
                    OutputNodeId,
                    [VideoNodeId],
                    range);
                RenderFontEvidence font = new(FontName, FontDigest);
                var graph = new RenderGraph(
                    GraphId,
                    CompositionVersionId,
                    composition.ContentDigest,
                    TimelineMapId,
                    timelineDigest,
                    ProfileId,
                    ProfileDigest,
                    LayoutSpecId,
                    LayoutSpecDigest,
                    template,
                    [graphInput],
                    [font],
                    [videoNode, outputNode],
                    OutputNodeId);
                var context = new RenderGraphValidationContext(
                    project,
                    composition,
                    timeline,
                    timelineDigest,
                    profile,
                    layout,
                    LayoutSpecDigest,
                    template,
                    sourceSpans: [],
                    editorialRecordIds: [],
                    [new RenderArtifactEvidence(
                        InputId,
                        InputDigest,
                        InputByteSize)],
                    [font]);
                var manifest = new RenderManifest(
                    EntityId<RenderManifestIdTag>.New(),
                    composition,
                    timeline,
                    timelineDigest,
                    profile,
                    ConfigurationDigest,
                    ApplicationVersion,
                    ApplicationDigest,
                    [new RenderInputEvidence(
                        ManifestInputName,
                        ManifestInputDigest)],
                    [new RenderToolEvidence(
                        ToolName,
                        ToolVersion,
                        ToolBuildConfiguration,
                        ToolDigest,
                        ToolContainerDigest)],
                    [new RenderModelEvidence(
                        ModelProvider,
                        ModelIdentifier,
                        ModelVersion,
                        ModelDigest)],
                    [font],
                    [new ManifestArtifact(
                        "video",
                        "video/mp4",
                        "ja",
                        128,
                        OutputDigest,
                        OutputStorageReference)]);
                return new IntegrityEvidenceSnapshot(
                    manifest,
                    graph,
                    context);
            }

            private static ContentDigest HashFile(string path) =>
                ContentDigest.Compute(File.ReadAllBytes(path));
        }
    }
}
