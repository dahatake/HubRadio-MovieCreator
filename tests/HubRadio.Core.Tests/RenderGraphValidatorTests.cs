using System.Reflection;
using System.Text;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class RenderGraphValidatorTests
{
    private const string Actor = "render-graph-test";

    private static readonly DateTimeOffset CreatedAt =
        new(2044, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [TestMethod]
    public void ValidClosedGraphBindsEveryInputOriginTimeLayoutRightAndVersion()
    {
        Fixture fixture = Fixture.Create();

        RenderGraphValidationResult result = RenderGraphValidator.Validate(
            fixture.Graph,
            fixture.Context);

        Assert.IsTrue(result.IsValid);
        Assert.HasCount(0, result.Issues);
        Assert.AreEqual(fixture.Graph.Id, result.GraphId);
        CollectionAssert.AreEqual(
            new[]
            {
                RenderNodeKind.Video,
                RenderNodeKind.Audio,
                RenderNodeKind.Subtitle,
                RenderNodeKind.Graphic,
                RenderNodeKind.Transition,
                RenderNodeKind.Output,
            },
            fixture.Graph.Nodes.Select(static node => node.Kind).ToArray());
        Assert.AreEqual(fixture.OutputNode.Id, fixture.Graph.OutputNodeId);
        Assert.AreEqual(fixture.Composition.Id, fixture.Graph.CompositionVersionId);
        Assert.AreEqual(fixture.Timeline.Id, fixture.Graph.TimelineMapId);
        Assert.AreEqual(fixture.Profile.Id, fixture.Graph.ProfileId);
        Assert.AreEqual(fixture.Layout.Id, fixture.Graph.LayoutSpecId);
        Assert.AreEqual(fixture.Template, fixture.Graph.Template);
    }

    [TestMethod]
    public void ArtifactEvidenceDetectsMissingDigestAndSizeWithoutPaths()
    {
        Fixture fixture = Fixture.Create();
        RenderGraphInput video = fixture.Graph.Inputs[0];

        RenderGraphValidationResult missing = RenderGraphValidator.Validate(
            fixture.Graph,
            fixture.ContextWith(
                artifacts: fixture.Context.Artifacts
                    .Where(item => item.Id != video.Id)
                    .ToArray()));
        RenderGraphValidationResult digest = RenderGraphValidator.Validate(
            fixture.Graph,
            fixture.ContextWith(
                artifacts: fixture.Context.Artifacts
                    .Select(item => item.Id == video.Id
                        ? new RenderArtifactEvidence(item.Id, Digest('f'), item.ByteSize)
                        : item)
                    .ToArray()));
        RenderGraphValidationResult size = RenderGraphValidator.Validate(
            fixture.Graph,
            fixture.ContextWith(
                artifacts: fixture.Context.Artifacts
                    .Select(item => item.Id == video.Id
                        ? new RenderArtifactEvidence(item.Id, item.ContentDigest, item.ByteSize + 1)
                        : item)
                    .ToArray()));

        AssertIssue(missing, RenderGraphValidationIssueKind.InputArtifactMissing, inputId: video.Id);
        AssertIssue(digest, RenderGraphValidationIssueKind.InputDigestMismatch, inputId: video.Id);
        AssertIssue(size, RenderGraphValidationIssueKind.InputSizeMismatch, inputId: video.Id);
        Assert.IsFalse(typeof(RenderGraphInput).GetProperties().Any(
            static property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void DanglingNodeCycleAndUnreachableNodeAreReportedDeterministically()
    {
        Fixture fixture = Fixture.Create();
        EntityId<RenderNodeIdTag> missingId = NodeId(99);
        RenderNode danglingOutput = RenderNode.Output(
            NodeId(6),
            [fixture.TransitionNode.Id, fixture.AudioNode.Id, missingId],
            Range(0, 4));
        RenderGraph danglingGraph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                danglingOutput,
            ],
            outputNodeId: danglingOutput.Id);
        AssertIssue(
            RenderGraphValidator.Validate(danglingGraph, fixture.Context),
            RenderGraphValidationIssueKind.DanglingNodeInput,
            nodeId: danglingOutput.Id);

        RenderNode cycleGraphic = RenderNode.Graphic(
            fixture.GraphicNode.Id,
            fixture.GraphicInput.Id,
            fixture.TransitionNode.Id,
            fixture.GraphicNode.Cue!,
            fixture.GraphicNode.Origin!);
        RenderGraph cycleGraph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                fixture.SubtitleNode,
                cycleGraphic,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(cycleGraph, fixture.Context),
            RenderGraphValidationIssueKind.Cycle);

        RenderNode unreachable = RenderNode.Video(
            NodeId(7),
            fixture.VideoInput.Id,
            Range(0, 4),
            fixture.AssetOrigin,
            sourceRange: null,
            "video");
        RenderGraph unreachableGraph = fixture.GraphWith(
            nodes: [.. fixture.Graph.Nodes, unreachable]);
        AssertIssue(
            RenderGraphValidator.Validate(unreachableGraph, fixture.Context),
            RenderGraphValidationIssueKind.UnreachableNode,
            nodeId: unreachable.Id);
    }

    [TestMethod]
    public void OutputNodeMustBeUniqueTerminalAndCoverTheTimeline()
    {
        Fixture fixture = Fixture.Create();
        RenderNode wrongRange = RenderNode.Output(
            fixture.OutputNode.Id,
            fixture.OutputNode.InputNodeIds,
            Range(0, 3));
        RenderGraph wrongRangeGraph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                wrongRange,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(wrongRangeGraph, fixture.Context),
            RenderGraphValidationIssueKind.OutputRangeMismatch,
            nodeId: wrongRange.Id);

        RenderGraph wrongOutputId = fixture.GraphWith(outputNodeId: fixture.VideoNode.Id);
        AssertIssue(
            RenderGraphValidator.Validate(wrongOutputId, fixture.Context),
            RenderGraphValidationIssueKind.OutputNodeInvalid,
            nodeId: fixture.VideoNode.Id);

        RenderNode secondOutput = RenderNode.Output(
            NodeId(8),
            [fixture.VideoNode.Id],
            Range(0, 4));
        RenderGraph twoOutputs = fixture.GraphWith(
            nodes: [.. fixture.Graph.Nodes, secondOutput]);
        AssertIssue(
            RenderGraphValidator.Validate(twoOutputs, fixture.Context),
            RenderGraphValidationIssueKind.OutputNodeInvalid,
            nodeId: secondOutput.Id);
    }

    [TestMethod]
    public void OutputNodeRejectsAmbiguousTerminalMediaStreams()
    {
        Fixture fixture = Fixture.Create();
        RenderNode ambiguousOutput = RenderNode.Output(
            fixture.OutputNode.Id,
            [
                fixture.TransitionNode.Id,
                fixture.GraphicNode.Id,
                fixture.AudioNode.Id,
            ],
            fixture.OutputNode.OutputRange);
        RenderGraph ambiguous = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                ambiguousOutput,
            ]);

        AssertIssue(
            RenderGraphValidator.Validate(ambiguous, fixture.Context),
            RenderGraphValidationIssueKind.OutputStreamMismatch,
            nodeId: ambiguousOutput.Id);
    }

    [TestMethod]
    public void CompositionAndTimelineBindingsRejectForgedDigestAndMapping()
    {
        Fixture fixture = Fixture.Create();
        var forgedComposition = new CompositionVersion(
            fixture.Composition.Id,
            fixture.Composition.ProjectId,
            fixture.Composition.ParentVersionId,
            fixture.Composition.CreatedByActorId,
            fixture.Composition.CreatedAt,
            fixture.Composition.ChangeReason,
            Digest('f'),
            fixture.Composition.Elements);
        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(composition: forgedComposition)),
            RenderGraphValidationIssueKind.CompositionDigestMismatch);

        var wrongTimeline = new TimelineMap(
            fixture.Timeline.Id,
            fixture.Composition.Id,
            [new TimelineMapEntry(
                Range(0, 3),
                fixture.AssetOrigin,
                sourceRange: null)]);
        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(
                    timeline: wrongTimeline,
                    timelineDigest: ComputeTimelineDigest(wrongTimeline))),
            RenderGraphValidationIssueKind.TimelineMappingMismatch);

        RenderNode sourceRangeMismatch = RenderNode.Video(
            fixture.VideoNode.Id,
            fixture.VideoInput.Id,
            Range(1, 3),
            fixture.AssetOrigin,
            sourceRange: null,
            "video");
        RenderGraph sourceRangeGraph = fixture.GraphWith(
            nodes:
            [
                sourceRangeMismatch,
                fixture.AudioNode,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(sourceRangeGraph, fixture.Context),
            RenderGraphValidationIssueKind.TimelineMappingMismatch,
            nodeId: sourceRangeMismatch.Id);
    }

    [TestMethod]
    public void UnknownAndMismatchedOriginsFailClosed()
    {
        Fixture fixture = Fixture.Create();
        OriginRef unknownAsset = OriginRef.FromAsset(Id<AssetIdTag>(0x77000020, 99));
        RenderGraphInput forgedInput = new(
            fixture.VideoInput.Id,
            fixture.VideoInput.Kind,
            fixture.VideoInput.ContentDigest,
            fixture.VideoInput.ByteSize,
            unknownAsset);
        RenderGraph unknownGraph = fixture.GraphWith(
            inputs:
            [
                forgedInput,
                fixture.AudioInput,
                fixture.SubtitleInput,
                fixture.GraphicInput,
            ],
            nodes:
            [
                RenderNode.Video(
                    fixture.VideoNode.Id,
                    forgedInput.Id,
                    Range(0, 4),
                    unknownAsset,
                    null,
                    "video"),
                fixture.AudioNode,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(unknownGraph, fixture.Context),
            RenderGraphValidationIssueKind.OriginMissing,
            inputId: forgedInput.Id);

        RenderNode mismatchedOrigin = RenderNode.Audio(
            fixture.AudioNode.Id,
            fixture.AudioInput.Id,
            Range(0, 4),
            fixture.EditorialOrigin,
            sourceRange: null);
        RenderGraph mismatchGraph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                mismatchedOrigin,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(mismatchGraph, fixture.Context),
            RenderGraphValidationIssueKind.InputOriginMismatch,
            nodeId: mismatchedOrigin.Id,
            inputId: fixture.AudioInput.Id);
    }

    [TestMethod]
    public void SourceSpanMustResolveToAnAssetWithMatchingDigest()
    {
        Fixture fixture = Fixture.Create(useSourceSpanVideo: true);
        Asset changedAsset = new(
            fixture.VideoAsset.Id,
            fixture.VideoAsset.FileName,
            fixture.VideoAsset.MediaType,
            Digest('f'),
            fixture.VideoAsset.ByteSize,
            fixture.VideoAsset.Rights);
        Project changedProject = new(
            fixture.Project.Id,
            fixture.Project.ContentName,
            fixture.Project.Purpose,
            fixture.Project.Audience,
            fixture.Project.PublicationScope,
            fixture.Project.Deadline,
            [changedAsset, fixture.GraphicAsset],
            fixture.Project.Performers);

        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(project: changedProject)),
            RenderGraphValidationIssueKind.OriginDigestMismatch);
    }

    [TestMethod]
    public void LayoutRegionRolesAndNodeInputKindsAreValidated()
    {
        Fixture fixture = Fixture.Create();
        RenderNode wrongRegion = RenderNode.Subtitle(
            fixture.SubtitleNode.Id,
            fixture.SubtitleInput.Id,
            fixture.VideoNode.Id,
            new Cue(
                fixture.SubtitleNode.Cue!.Id,
                CueKind.Subtitle,
                fixture.SubtitleNode.Cue.Range,
                "graphic"),
            fixture.EditorialOrigin);
        RenderGraph wrongRegionGraph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                wrongRegion,
                fixture.GraphicNode,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(wrongRegionGraph, fixture.Context),
            RenderGraphValidationIssueKind.LayoutRoleMismatch,
            nodeId: wrongRegion.Id);

        RenderNode wrongInput = RenderNode.Subtitle(
            fixture.SubtitleNode.Id,
            fixture.GraphicInput.Id,
            fixture.VideoNode.Id,
            fixture.SubtitleNode.Cue,
            fixture.GraphicOrigin);
        RenderGraph wrongInputGraph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                wrongInput,
                fixture.GraphicNode,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(wrongInputGraph, fixture.Context),
            RenderGraphValidationIssueKind.InputKindMismatch,
            nodeId: wrongInput.Id,
            inputId: fixture.GraphicInput.Id);
    }

    [TestMethod]
    public void OverlayTargetsAndTransitionArityMustMatchTypedMediaSemantics()
    {
        Fixture fixture = Fixture.Create();
        RenderNode subtitleOnAudio = RenderNode.Subtitle(
            fixture.SubtitleNode.Id,
            fixture.SubtitleInput.Id,
            fixture.AudioNode.Id,
            fixture.SubtitleNode.Cue!,
            fixture.EditorialOrigin);
        RenderGraph wrongTarget = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                subtitleOnAudio,
                fixture.GraphicNode,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(wrongTarget, fixture.Context),
            RenderGraphValidationIssueKind.NodeDependencyKindMismatch,
            nodeId: subtitleOnAudio.Id);

        RenderNode oneInputCrossfade = RenderNode.TransitionEffect(
            fixture.TransitionNode.Id,
            [fixture.VideoNode.Id],
            Range(2, 1),
            fixture.EditorialOrigin,
            Transition.Crossfade(new RationalTime(1, 1)));
        RenderGraph wrongTransition = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                fixture.SubtitleNode,
                oneInputCrossfade,
                fixture.OutputNode,
            ]);
        AssertIssue(
            RenderGraphValidator.Validate(wrongTransition, fixture.Context),
            RenderGraphValidationIssueKind.TransitionInputMismatch,
            nodeId: oneInputCrossfade.Id);
    }

    [TestMethod]
    public void BurnedInSubtitleNodesMayFormALinearCueChainOnly()
    {
        Fixture fixture = Fixture.Create();
        var secondInput = new RenderGraphInput(
            InputId(9),
            RenderInputKind.Subtitle,
            Digest('9'),
            81,
            fixture.EditorialOrigin);
        RenderNode secondSubtitle = RenderNode.Subtitle(
            NodeId(9),
            secondInput.Id,
            fixture.SubtitleNode.Id,
            new Cue(
                Id<CueIdTag>(0x7700001a, 9),
                CueKind.Subtitle,
                Range(3, 1),
                "subtitle"),
            fixture.EditorialOrigin);
        RenderNode output = RenderNode.Output(
            fixture.OutputNode.Id,
            [secondSubtitle.Id, fixture.AudioNode.Id],
            fixture.OutputNode.OutputRange);
        RenderGraph graph = fixture.GraphWith(
            inputs: [.. fixture.Graph.Inputs, secondInput],
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                secondSubtitle,
                output,
            ]);
        RenderGraphValidationContext context = fixture.ContextWith(
            artifacts:
            [
                .. fixture.Context.Artifacts,
                new RenderArtifactEvidence(
                    secondInput.Id,
                    secondInput.ContentDigest,
                    secondInput.ByteSize),
            ]);

        RenderGraphValidationResult burnedIn = RenderGraphValidator.Validate(
            graph,
            context);

        Assert.IsTrue(burnedIn.IsValid);
        Assert.HasCount(0, burnedIn.Issues);

        var subtitleStreamProfile = new RenderProfile(
            fixture.Profile.Id,
            fixture.Profile.Name,
            fixture.Profile.ProfileDigest,
            fixture.Profile.Width,
            fixture.Profile.Height,
            fixture.Profile.FrameRateNumerator,
            fixture.Profile.FrameRateDenominator,
            fixture.Profile.LayoutSpecId,
            fixture.Profile.TemplateId,
            fixture.Profile.TemplateVersionDigest,
            fixture.Profile.VideoCodec,
            fixture.Profile.AudioCodec,
            subtitleBurnIn: false);
        AssertIssue(
            RenderGraphValidator.Validate(
                graph,
                fixture.ContextWith(
                    profile: subtitleStreamProfile,
                    artifacts: context.Artifacts)),
            RenderGraphValidationIssueKind.NodeDependencyKindMismatch,
            nodeId: secondSubtitle.Id);
    }

    [TestMethod]
    public void SourceBackedAudioRangeMustRemainInsideItsSourceSpan()
    {
        Fixture fixture = Fixture.Create(useSourceSpanVideo: true);
        RenderNode outsideSource = RenderNode.Audio(
            fixture.AudioNode.Id,
            fixture.AudioInput.Id,
            Range(0, 4),
            fixture.AudioNode.Origin!,
            sourceRange: Range(10, 4));
        RenderGraph graph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                outsideSource,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                fixture.TransitionNode,
                fixture.OutputNode,
            ]);

        AssertIssue(
            RenderGraphValidator.Validate(graph, fixture.Context),
            RenderGraphValidationIssueKind.SourceRangeMismatch,
            nodeId: outsideSource.Id);
    }

    [TestMethod]
    public void InputlessNodeOriginsStillEnforceResolvedAssetRights()
    {
        Fixture fixture = Fixture.Create();
        var rejectedAsset = new Asset(
            Id<AssetIdTag>(0x77000023, 1),
            "rejected.png",
            "image/png",
            Digest('8'),
            10,
            new RightsRecord(
                RightsStatus.Rejected,
                evidenceReference: null,
                "Rejected asset",
                externalPublicationAllowed: false));
        Project project = new(
            fixture.Project.Id,
            fixture.Project.ContentName,
            fixture.Project.Purpose,
            fixture.Project.Audience,
            fixture.Project.PublicationScope,
            fixture.Project.Deadline,
            [fixture.VideoAsset, fixture.GraphicAsset, rejectedAsset],
            fixture.Project.Performers);
        RenderNode transition = RenderNode.TransitionEffect(
            fixture.TransitionNode.Id,
            fixture.TransitionNode.InputNodeIds,
            fixture.TransitionNode.OutputRange,
            OriginRef.FromAsset(rejectedAsset.Id),
            fixture.TransitionNode.Transition!);
        RenderGraph graph = fixture.GraphWith(
            nodes:
            [
                fixture.VideoNode,
                fixture.AudioNode,
                fixture.SubtitleNode,
                fixture.GraphicNode,
                transition,
                fixture.OutputNode,
            ]);

        AssertIssue(
            RenderGraphValidator.Validate(
                graph,
                fixture.ContextWith(project: project)),
            RenderGraphValidationIssueKind.RightsRejected,
            nodeId: transition.Id);
    }

    [TestMethod]
    public void SubtitleNodesRequireExactFontEvidence()
    {
        Fixture fixture = Fixture.Create();
        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(fonts: [])),
            RenderGraphValidationIssueKind.FontEvidenceMismatch);

        RenderFontEvidence changed = new(
            fixture.Font.Name,
            Digest('9'));
        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(fonts: [changed])),
            RenderGraphValidationIssueKind.FontEvidenceMismatch);
    }

    [TestMethod]
    public void ExternalRenderRequiresApprovedExternalRightsAndRejectedRightsAlwaysFail()
    {
        Fixture fixture = Fixture.Create();
        Project externalUnconfirmed = fixture.ProjectWithRights(
            PublicationScope.ExternalCandidate,
            new RightsRecord(
                RightsStatus.Unconfirmed,
                evidenceReference: null,
                "Internal review only",
                externalPublicationAllowed: false));
        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(project: externalUnconfirmed)),
            RenderGraphValidationIssueKind.ExternalRightsNotApproved);

        Project rejected = fixture.ProjectWithRights(
            PublicationScope.InternalOnly,
            new RightsRecord(
                RightsStatus.Rejected,
                evidenceReference: null,
                "Do not use",
                externalPublicationAllowed: false));
        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(project: rejected)),
            RenderGraphValidationIssueKind.RightsRejected);
    }

    [TestMethod]
    public void ProfileLayoutTemplateAndGraphBindingsAreValidated()
    {
        Fixture fixture = Fixture.Create();
        RenderGraph wrongLayout = fixture.GraphWith(
            layoutSpecId: Id<LayoutSpecIdTag>(0x77000021, 99));
        AssertIssue(
            RenderGraphValidator.Validate(wrongLayout, fixture.Context),
            RenderGraphValidationIssueKind.LayoutBindingMismatch);

        RenderProfile wrongTemplate = new(
            fixture.Profile.Id,
            fixture.Profile.Name,
            fixture.Profile.ProfileDigest,
            fixture.Profile.Width,
            fixture.Profile.Height,
            fixture.Profile.FrameRateNumerator,
            fixture.Profile.FrameRateDenominator,
            fixture.Profile.LayoutSpecId,
            Id<TemplateIdTag>(0x77000022, 99),
            fixture.Profile.TemplateVersionDigest,
            fixture.Profile.VideoCodec,
            fixture.Profile.AudioCodec,
            fixture.Profile.SubtitleBurnIn);
        AssertIssue(
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.ContextWith(profile: wrongTemplate)),
            RenderGraphValidationIssueKind.TemplateBindingMismatch);

        var renamedTemplate = new TemplateReference(
            fixture.Template.Id,
            "renamed-template",
            fixture.Template.VersionDigest);
        var renamedGraph = new RenderGraph(
            fixture.Graph.Id,
            fixture.Graph.CompositionVersionId,
            fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId,
            fixture.Graph.TimelineMapDigest,
            fixture.Graph.ProfileId,
            fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId,
            fixture.Graph.LayoutSpecDigest,
            renamedTemplate,
            fixture.Graph.Inputs,
            fixture.Graph.Fonts,
            fixture.Graph.Nodes,
            fixture.Graph.OutputNodeId);
        AssertIssue(
            RenderGraphValidator.Validate(renamedGraph, fixture.Context),
            RenderGraphValidationIssueKind.TemplateBindingMismatch);
    }

    [TestMethod]
    public void ConstructorsDefensivelyCopyAndRejectDuplicateIdentities()
    {
        Fixture fixture = Fixture.Create();
        var inputs = fixture.Graph.Inputs.ToList();
        var nodes = fixture.Graph.Nodes.ToList();
        var fonts = fixture.Graph.Fonts.ToList();
        var graph = new RenderGraph(
            GraphId(2),
            fixture.Graph.CompositionVersionId,
            fixture.Graph.CompositionVersionDigest,
            fixture.Graph.TimelineMapId,
            fixture.Graph.TimelineMapDigest,
            fixture.Graph.ProfileId,
            fixture.Graph.ProfileDigest,
            fixture.Graph.LayoutSpecId,
            fixture.Graph.LayoutSpecDigest,
            fixture.Graph.Template,
            inputs,
            fonts,
            nodes,
            fixture.Graph.OutputNodeId);
        inputs.Clear();
        nodes.Clear();
        fonts.Clear();

        Assert.HasCount(4, graph.Inputs);
        Assert.HasCount(6, graph.Nodes);
        Assert.HasCount(1, graph.Fonts);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<RenderNode>)graph.Nodes).Clear());
        Assert.ThrowsExactly<ArgumentException>(() => fixture.GraphWith(
            inputs: [fixture.VideoInput, fixture.VideoInput]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.GraphWith(
            nodes: [fixture.VideoNode, fixture.VideoNode]));
    }

    [TestMethod]
    public void PreCancellationAndPublicSurfaceHaveNoRawPathFilterOrCommand()
    {
        Fixture fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RenderGraphValidator.Validate(
                fixture.Graph,
                fixture.Context,
                cancellation.Token));

        CollectionAssert.AreEqual(
            new[] { "Video", "Audio", "Subtitle", "Graphic", "Transition", "Output" },
            Enum.GetNames<RenderNodeKind>());
        MemberInfo[] members = typeof(RenderGraph).Assembly.GetExportedTypes()
            .Where(static type => type.Namespace == "HubRadio.Core.Rendering")
            .Where(static type => type.Name.StartsWith("RenderGraph", StringComparison.Ordinal)
                || type.Name is "RenderNode" or "RenderArtifactEvidence")
            .SelectMany(static type => type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .ToArray();
        Assert.IsFalse(members.Any(static member =>
            member.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Command", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertIssue(
        RenderGraphValidationResult result,
        RenderGraphValidationIssueKind kind,
        EntityId<RenderNodeIdTag>? nodeId = null,
        EntityId<RenderInputIdTag>? inputId = null)
    {
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Kind == kind
            && (nodeId is null || issue.NodeId == nodeId)
            && (inputId is null || issue.InputId == inputId)),
            $"Expected issue {kind} was not found.");
    }

    private static EntityId<RenderGraphIdTag> GraphId(int value) =>
        Id<RenderGraphIdTag>(0x77000001, value);

    private static EntityId<RenderNodeIdTag> NodeId(int value) =>
        Id<RenderNodeIdTag>(0x77000002, value);

    private static EntityId<RenderInputIdTag> InputId(int value) =>
        Id<RenderInputIdTag>(0x77000003, value);

    private static EntityId<TTag> Id<TTag>(uint prefix, long value) =>
        EntityId<TTag>.Parse($"{prefix:x8}-0000-0000-0000-{value:x12}");

    private static ContentDigest Digest(char fill) => new(new string(fill, 64));

    private static TimeRange Range(long start, long duration) =>
        new(new RationalTime(start, 1), new RationalTime(duration, 1));

    private static ContentDigest EmptyOrSingleAssetCompositionDigest(
        EntityId<CompositionElementIdTag> elementId,
        EntityId<AssetIdTag> assetId)
    {
        string json = string.Concat(
            "{\"elements\":[{\"id\":\"", elementId,
            "\",\"origin\":{\"assetId\":\"", assetId,
            "\",\"kind\":\"asset\"},\"outputRange\":{\"duration\":{\"rate\":1,\"value\":4},\"start\":{\"rate\":1,\"value\":0}}}]}");
        return ContentDigest.Compute(Encoding.UTF8.GetBytes(json));
    }

    private static ContentDigest ComputeTimelineDigest(TimelineMap timeline)
    {
        string entry = string.Concat(
            "{\"origin\":{\"assetId\":\"",
            timeline.Entries.Single().Origin.AssetId,
            "\",\"kind\":\"asset\"},\"outputRange\":{\"duration\":{\"rate\":1,\"value\":",
            timeline.Entries.Single().OutputRange.Duration.Value,
            "},\"start\":{\"rate\":1,\"value\":0}}}");
        string json = string.Concat(
            "{\"compositionVersionId\":\"",
            timeline.CompositionVersionId,
            "\",\"entries\":[",
            entry,
            "],\"id\":\"",
            timeline.Id,
            "\"}");
        return ContentDigest.Compute(Encoding.UTF8.GetBytes(json));
    }

    private sealed class Fixture
    {
        private Fixture(
            Project project,
            Asset videoAsset,
            Asset graphicAsset,
            CompositionVersion composition,
            TimelineMap timeline,
            RenderProfile profile,
            LayoutSpec layout,
            TemplateReference template,
            RenderFontEvidence font,
            OriginRef assetOrigin,
            OriginRef graphicOrigin,
            OriginRef editorialOrigin,
            IReadOnlyList<SourceSpan> sourceSpans,
            RenderGraphInput videoInput,
            RenderGraphInput audioInput,
            RenderGraphInput subtitleInput,
            RenderGraphInput graphicInput,
            RenderNode videoNode,
            RenderNode audioNode,
            RenderNode subtitleNode,
            RenderNode graphicNode,
            RenderNode transitionNode,
            RenderNode outputNode,
            RenderGraph graph,
            RenderGraphValidationContext context)
        {
            Project = project;
            VideoAsset = videoAsset;
            GraphicAsset = graphicAsset;
            Composition = composition;
            Timeline = timeline;
            Profile = profile;
            Layout = layout;
            Template = template;
            Font = font;
            AssetOrigin = assetOrigin;
            GraphicOrigin = graphicOrigin;
            EditorialOrigin = editorialOrigin;
            SourceSpans = sourceSpans;
            VideoInput = videoInput;
            AudioInput = audioInput;
            SubtitleInput = subtitleInput;
            GraphicInput = graphicInput;
            VideoNode = videoNode;
            AudioNode = audioNode;
            SubtitleNode = subtitleNode;
            GraphicNode = graphicNode;
            TransitionNode = transitionNode;
            OutputNode = outputNode;
            Graph = graph;
            Context = context;
        }

        internal Project Project { get; }
        internal Asset VideoAsset { get; }
        internal Asset GraphicAsset { get; }
        internal CompositionVersion Composition { get; }
        internal TimelineMap Timeline { get; }
        internal RenderProfile Profile { get; }
        internal LayoutSpec Layout { get; }
        internal TemplateReference Template { get; }
        internal RenderFontEvidence Font { get; }
        internal OriginRef AssetOrigin { get; }
        internal OriginRef GraphicOrigin { get; }
        internal OriginRef EditorialOrigin { get; }
        internal IReadOnlyList<SourceSpan> SourceSpans { get; }
        internal RenderGraphInput VideoInput { get; }
        internal RenderGraphInput AudioInput { get; }
        internal RenderGraphInput SubtitleInput { get; }
        internal RenderGraphInput GraphicInput { get; }
        internal RenderNode VideoNode { get; }
        internal RenderNode AudioNode { get; }
        internal RenderNode SubtitleNode { get; }
        internal RenderNode GraphicNode { get; }
        internal RenderNode TransitionNode { get; }
        internal RenderNode OutputNode { get; }
        internal RenderGraph Graph { get; }
        internal RenderGraphValidationContext Context { get; }

        internal static Fixture Create(bool useSourceSpanVideo = false)
        {
            EntityId<ProjectIdTag> projectId = Id<ProjectIdTag>(0x77000010, 1);
            RightsRecord approved = new(
                RightsStatus.Approved,
                "rights:evidence",
                "Approved production use",
                externalPublicationAllowed: true);
            var videoAsset = new Asset(
                Id<AssetIdTag>(0x77000011, 1),
                "source.mp4",
                "video/mp4",
                Digest('a'),
                100,
                approved);
            var graphicAsset = new Asset(
                Id<AssetIdTag>(0x77000011, 2),
                "graphic.png",
                "image/png",
                Digest('b'),
                50,
                approved);
            var project = new Project(
                projectId,
                "Render graph",
                "Validate typed render IR.",
                "Reviewers",
                PublicationScope.InternalOnly,
                deadline: null,
                [videoAsset, graphicAsset],
                performers: []);
            OriginRef assetOrigin = OriginRef.FromAsset(videoAsset.Id);
            OriginRef graphicOrigin = OriginRef.FromAsset(graphicAsset.Id);
            EntityId<EditorialRecordIdTag> editorialId =
                Id<EditorialRecordIdTag>(0x77000012, 1);
            OriginRef editorialOrigin = OriginRef.FromEditorialRecord(editorialId);
            IReadOnlyList<SourceSpan> sourceSpans = [];
            OriginRef videoOrigin = assetOrigin;
            TimeRange? videoSourceRange = null;
            if (useSourceSpanVideo)
            {
                var span = new SourceSpan(
                    Id<SourceSpanIdTag>(0x77000013, 1),
                    videoAsset.Id,
                    "video:0",
                    Range(0, 4),
                    videoAsset.ContentDigest,
                    transcriptRevisionId: null,
                    transcriptTokenIds: []);
                sourceSpans = [span];
                videoOrigin = OriginRef.FromSourceSpan(span.Id);
                videoSourceRange = span.Range;
            }

            EntityId<CompositionElementIdTag> elementId =
                Id<CompositionElementIdTag>(0x77000014, 1);
            var element = new CompositionElement(
                elementId,
                Range(0, 4),
                videoOrigin,
                sourceRange: videoSourceRange);
            ContentDigest compositionDigest = useSourceSpanVideo
                ? RenderGraphTestDigest.ComputeComposition([element])
                : EmptyOrSingleAssetCompositionDigest(elementId, videoAsset.Id);
            var composition = new CompositionVersion(
                Id<CompositionVersionIdTag>(0x77000015, 1),
                project.Id,
                null,
                Actor,
                CreatedAt,
                "Create render composition.",
                compositionDigest,
                [element]);
            var timeline = new TimelineMap(
                Id<TimelineMapIdTag>(0x77000016, 1),
                composition.Id,
                [new TimelineMapEntry(
                    element.OutputRange,
                    element.Origin,
                    element.SourceRange)]);
            ContentDigest timelineDigest = useSourceSpanVideo
                ? RenderGraphTestDigest.ComputeTimeline(timeline)
                : ComputeTimelineDigest(timeline);
            var layout = new LayoutSpec(
                Id<LayoutSpecIdTag>(0x77000017, 1),
                16,
                9,
                [
                    new LayoutRegion("video", FullRect(), LayoutRegionRole.Video),
                    new LayoutRegion("subtitle", BottomHalf(), LayoutRegionRole.Subtitle),
                    new LayoutRegion("graphic", TopHalf(), LayoutRegionRole.Graphic),
                ],
                [new SafeZone("frame", FullRect())]);
            ContentDigest layoutDigest = Digest('c');
            var template = new TemplateReference(
                Id<TemplateIdTag>(0x77000018, 1),
                "render-template",
                Digest('d'));
            var profile = new RenderProfile(
                Id<RenderProfileIdTag>(0x77000019, 1),
                "render-profile",
                Digest('e'),
                1280,
                720,
                30000,
                1001,
                layout.Id,
                template.Id,
                template.VersionDigest,
                "h264",
                "aac",
                subtitleBurnIn: true);
            var font = new RenderFontEvidence("Test Sans", Digest('f'));
            var videoInput = new RenderGraphInput(
                InputId(1), RenderInputKind.Video, Digest('1'), 1000, videoOrigin);
            var audioInput = new RenderGraphInput(
                InputId(2), RenderInputKind.Audio, Digest('2'), 200, videoOrigin);
            var subtitleInput = new RenderGraphInput(
                InputId(3), RenderInputKind.Subtitle, Digest('3'), 80, editorialOrigin);
            var graphicInput = new RenderGraphInput(
                InputId(4), RenderInputKind.Graphic, Digest('4'), 50, graphicOrigin);
            var videoNode = RenderNode.Video(
                NodeId(1), videoInput.Id, Range(0, 4), videoOrigin, videoSourceRange, "video");
            var audioNode = RenderNode.Audio(
                NodeId(2), audioInput.Id, Range(0, 4), videoOrigin, videoSourceRange);
            var subtitleNode = RenderNode.Subtitle(
                NodeId(3),
                subtitleInput.Id,
                targetVisualNodeId: NodeId(5),
                new Cue(Id<CueIdTag>(0x7700001a, 1), CueKind.Subtitle, Range(1, 1), "subtitle"),
                editorialOrigin);
            var graphicNode = RenderNode.Graphic(
                NodeId(4),
                graphicInput.Id,
                videoNode.Id,
                new Cue(Id<CueIdTag>(0x7700001a, 2), CueKind.Graphic, Range(2, 1), "graphic"),
                graphicOrigin);
            var transitionNode = RenderNode.TransitionEffect(
                NodeId(5),
                [videoNode.Id, graphicNode.Id],
                Range(2, 1),
                editorialOrigin,
                Transition.Crossfade(new RationalTime(1, 1)));
            var outputNode = RenderNode.Output(
                NodeId(6),
                [subtitleNode.Id, audioNode.Id],
                Range(0, 4));
            RenderGraph graph = new(
                GraphId(1),
                composition.Id,
                composition.ContentDigest,
                timeline.Id,
                timelineDigest,
                profile.Id,
                profile.ProfileDigest,
                layout.Id,
                layoutDigest,
                template,
                [videoInput, audioInput, subtitleInput, graphicInput],
                [font],
                [videoNode, audioNode, subtitleNode, graphicNode, transitionNode, outputNode],
                outputNode.Id);
            RenderArtifactEvidence[] artifacts = graph.Inputs
                .Select(static input => new RenderArtifactEvidence(
                    input.Id,
                    input.ContentDigest,
                    input.ByteSize))
                .ToArray();
            var context = new RenderGraphValidationContext(
                project,
                composition,
                timeline,
                timelineDigest,
                profile,
                layout,
                layoutDigest,
                template,
                sourceSpans,
                [editorialId],
                artifacts,
                [font]);
            return new Fixture(
                project,
                videoAsset,
                graphicAsset,
                composition,
                timeline,
                profile,
                layout,
                template,
                font,
                assetOrigin,
                graphicOrigin,
                editorialOrigin,
                sourceSpans,
                videoInput,
                audioInput,
                subtitleInput,
                graphicInput,
                videoNode,
                audioNode,
                subtitleNode,
                graphicNode,
                transitionNode,
                outputNode,
                graph,
                context);
        }

        internal RenderGraph GraphWith(
            IReadOnlyList<RenderGraphInput>? inputs = null,
            IReadOnlyList<RenderFontEvidence>? fonts = null,
            IReadOnlyList<RenderNode>? nodes = null,
            EntityId<RenderNodeIdTag>? outputNodeId = null,
            EntityId<LayoutSpecIdTag>? layoutSpecId = null) =>
            new(
                Graph.Id,
                Graph.CompositionVersionId,
                Graph.CompositionVersionDigest,
                Graph.TimelineMapId,
                Graph.TimelineMapDigest,
                Graph.ProfileId,
                Graph.ProfileDigest,
                layoutSpecId ?? Graph.LayoutSpecId,
                Graph.LayoutSpecDigest,
                Graph.Template,
                inputs ?? Graph.Inputs,
                fonts ?? Graph.Fonts,
                nodes ?? Graph.Nodes,
                outputNodeId ?? Graph.OutputNodeId);

        internal RenderGraphValidationContext ContextWith(
            Project? project = null,
            CompositionVersion? composition = null,
            TimelineMap? timeline = null,
            ContentDigest? timelineDigest = null,
            RenderProfile? profile = null,
            IReadOnlyList<RenderArtifactEvidence>? artifacts = null,
            IReadOnlyList<RenderFontEvidence>? fonts = null) =>
            new(
                project ?? Context.Project,
                composition ?? Context.CompositionVersion,
                timeline ?? Context.TimelineMap,
                timelineDigest ?? Context.TimelineMapDigest,
                profile ?? Context.Profile,
                Context.LayoutSpec,
                Context.LayoutSpecDigest,
                Context.Template,
                Context.SourceSpans,
                Context.EditorialRecordIds,
                artifacts ?? Context.Artifacts,
                fonts ?? Context.Fonts);

        internal Project ProjectWithRights(
            PublicationScope scope,
            RightsRecord videoRights)
        {
            Asset changedVideo = new(
                VideoAsset.Id,
                VideoAsset.FileName,
                VideoAsset.MediaType,
                VideoAsset.ContentDigest,
                VideoAsset.ByteSize,
                videoRights);
            return new Project(
                Project.Id,
                Project.ContentName,
                Project.Purpose,
                Project.Audience,
                scope,
                Project.Deadline,
                [changedVideo, GraphicAsset],
                Project.Performers);
        }

        private static NormalizedRect FullRect() => Rect(0, 1, 0, 1, 1, 1, 1, 1);
        private static NormalizedRect TopHalf() => Rect(0, 1, 0, 1, 1, 1, 1, 2);
        private static NormalizedRect BottomHalf() => Rect(0, 1, 1, 2, 1, 1, 1, 2);
        private static NormalizedRect Rect(
            long xN, long xD, long yN, long yD,
            long wN, long wD, long hN, long hD) =>
            new(
                new RationalNumber(xN, xD),
                new RationalNumber(yN, yD),
                new RationalNumber(wN, wD),
                new RationalNumber(hN, hD));
    }
}

internal static class RenderGraphTestDigest
{
    internal static ContentDigest ComputeComposition(
        IReadOnlyList<CompositionElement> elements) =>
        RenderGraphCanonicalEvidence.ComputeCompositionDigest(elements);

    internal static ContentDigest ComputeTimeline(TimelineMap timeline) =>
        RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(timeline);
}