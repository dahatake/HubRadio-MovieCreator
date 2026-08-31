using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Rendering;

public sealed class RenderGraphValidationContext
{
    public RenderGraphValidationContext(
        Project project,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        ContentDigest timelineMapDigest,
        RenderProfile profile,
        LayoutSpec layoutSpec,
        ContentDigest layoutSpecDigest,
        TemplateReference template,
        IReadOnlyList<SourceSpan> sourceSpans,
        IReadOnlyList<EntityId<EditorialRecordIdTag>> editorialRecordIds,
        IReadOnlyList<RenderArtifactEvidence> artifacts,
        IReadOnlyList<RenderFontEvidence> fonts)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(timelineMap);
        ArgumentNullException.ThrowIfNull(timelineMapDigest);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(layoutSpec);
        ArgumentNullException.ThrowIfNull(layoutSpecDigest);
        ArgumentNullException.ThrowIfNull(template);
        Project = project;
        CompositionVersion = compositionVersion;
        TimelineMap = timelineMap;
        TimelineMapDigest = timelineMapDigest;
        Profile = profile;
        LayoutSpec = layoutSpec;
        LayoutSpecDigest = layoutSpecDigest;
        Template = template;
        SourceSpans = Array.AsReadOnly(CopyUniqueSourceSpans(sourceSpans));
        EditorialRecordIds = Array.AsReadOnly(
            RenderGraphContract.CopyUniqueIds(
                editorialRecordIds,
                nameof(editorialRecordIds)));
        Artifacts = Array.AsReadOnly(CopyUniqueArtifacts(artifacts));
        Fonts = Array.AsReadOnly(CopyUniqueFonts(fonts));
    }

    public Project Project { get; }

    public CompositionVersion CompositionVersion { get; }

    public TimelineMap TimelineMap { get; }

    public ContentDigest TimelineMapDigest { get; }

    public RenderProfile Profile { get; }

    public LayoutSpec LayoutSpec { get; }

    public ContentDigest LayoutSpecDigest { get; }

    public TemplateReference Template { get; }

    public IReadOnlyList<SourceSpan> SourceSpans { get; }

    public IReadOnlyList<EntityId<EditorialRecordIdTag>> EditorialRecordIds { get; }

    public IReadOnlyList<RenderArtifactEvidence> Artifacts { get; }

    public IReadOnlyList<RenderFontEvidence> Fonts { get; }

    private static SourceSpan[] CopyUniqueSourceSpans(
        IReadOnlyList<SourceSpan> sourceSpans)
    {
        ArgumentNullException.ThrowIfNull(sourceSpans);
        var copied = new SourceSpan[sourceSpans.Count];
        var ids = new HashSet<EntityId<SourceSpanIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            SourceSpan span = sourceSpans[index]
                ?? throw new ArgumentException(
                    "Render source spans must not contain null values.",
                    nameof(sourceSpans));
            if (!ids.Add(span.Id))
            {
                throw new ArgumentException(
                    "Render source span identities must be unique.",
                    nameof(sourceSpans));
            }

            copied[index] = span;
        }

        return copied;
    }

    private static RenderArtifactEvidence[] CopyUniqueArtifacts(
        IReadOnlyList<RenderArtifactEvidence> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var copied = new RenderArtifactEvidence[artifacts.Count];
        var ids = new HashSet<EntityId<RenderInputIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            RenderArtifactEvidence artifact = artifacts[index]
                ?? throw new ArgumentException(
                    "Render artifacts must not contain null values.",
                    nameof(artifacts));
            if (!ids.Add(artifact.Id))
            {
                throw new ArgumentException(
                    "Render artifact identities must be unique.",
                    nameof(artifacts));
            }

            copied[index] = artifact;
        }

        return copied;
    }

    private static RenderFontEvidence[] CopyUniqueFonts(
        IReadOnlyList<RenderFontEvidence> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        var copied = new RenderFontEvidence[fonts.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copied.Length; index++)
        {
            RenderFontEvidence font = fonts[index]
                ?? throw new ArgumentException(
                    "Render font evidence must not contain null values.",
                    nameof(fonts));
            if (!names.Add(font.Name))
            {
                throw new ArgumentException(
                    "Render font evidence names must be unique.",
                    nameof(fonts));
            }

            copied[index] = font;
        }

        return copied;
    }
}

public enum RenderGraphValidationIssueKind
{
    CompositionBindingMismatch,
    CompositionDigestMismatch,
    TimelineBindingMismatch,
    TimelineDigestMismatch,
    TimelineMappingMismatch,
    ProfileBindingMismatch,
    LayoutBindingMismatch,
    TemplateBindingMismatch,
    InputArtifactMissing,
    InputDigestMismatch,
    InputSizeMismatch,
    InputKindMismatch,
    InputOriginMismatch,
    NodeDependencyKindMismatch,
    TransitionInputMismatch,
    OriginMissing,
    OriginDigestMismatch,
    SourceRangeMismatch,
    RightsRejected,
    ExternalRightsNotApproved,
    DanglingNodeInput,
    Cycle,
    UnreachableNode,
    OutputNodeInvalid,
    OutputStreamMismatch,
    OutputRangeMismatch,
    NodeRangeOutOfBounds,
    LayoutRegionMissing,
    LayoutRoleMismatch,
    FontEvidenceMismatch,
    DuplicateCue,
}

public sealed class RenderGraphValidationIssue
{
    internal RenderGraphValidationIssue(
        RenderGraphValidationIssueKind kind,
        EntityId<RenderNodeIdTag>? nodeId,
        EntityId<RenderInputIdTag>? inputId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        NodeId = nodeId;
        InputId = inputId;
    }

    public RenderGraphValidationIssueKind Kind { get; }

    public EntityId<RenderNodeIdTag>? NodeId { get; }

    public EntityId<RenderInputIdTag>? InputId { get; }
}

public sealed class RenderGraphValidationResult
{
    internal RenderGraphValidationResult(
        EntityId<RenderGraphIdTag> graphId,
        IReadOnlyList<RenderGraphValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(graphId);
        ArgumentNullException.ThrowIfNull(issues);
        GraphId = graphId;
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public EntityId<RenderGraphIdTag> GraphId { get; }

    public IReadOnlyList<RenderGraphValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}

public static class RenderGraphValidator
{
    public static RenderGraphValidationResult Validate(
        RenderGraph graph,
        RenderGraphValidationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new IssueCollector();
        ValidateBindings(graph, context, issues);
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<EntityId<RenderInputIdTag>, RenderGraphInput> inputs =
            graph.Inputs.ToDictionary(static input => input.Id);
        Dictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes =
            graph.Nodes.ToDictionary(static node => node.Id);
        ValidateArtifacts(graph, context, issues, cancellationToken);
        ValidateOrigins(graph, context, issues, cancellationToken);
        ValidateNodes(graph, context, inputs, nodes, issues, cancellationToken);
        ValidateGraphTopology(graph, nodes, issues, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderGraphValidationResult(graph.Id, issues.ToCanonicalArray());
    }

    private static void ValidateBindings(
        RenderGraph graph,
        RenderGraphValidationContext context,
        IssueCollector issues)
    {
        CompositionVersion composition = context.CompositionVersion;
        if (composition.ProjectId != context.Project.Id
            || graph.CompositionVersionId != composition.Id
            || graph.CompositionVersionDigest != composition.ContentDigest)
        {
            issues.Add(RenderGraphValidationIssueKind.CompositionBindingMismatch);
        }

        if (composition.ContentDigest !=
            RenderGraphCanonicalEvidence.ComputeCompositionDigest(
                composition.Elements))
        {
            issues.Add(RenderGraphValidationIssueKind.CompositionDigestMismatch);
        }

        if (context.TimelineMap.CompositionVersionId != composition.Id
            || graph.TimelineMapId != context.TimelineMap.Id
            || graph.TimelineMapDigest != context.TimelineMapDigest)
        {
            issues.Add(RenderGraphValidationIssueKind.TimelineBindingMismatch);
        }

        if (context.TimelineMapDigest !=
            RenderGraphCanonicalEvidence.ComputeTimelineMapDigest(
                context.TimelineMap))
        {
            issues.Add(RenderGraphValidationIssueKind.TimelineDigestMismatch);
        }

        if (!TimelineMatchesComposition(
                context.TimelineMap,
                composition.Elements))
        {
            issues.Add(RenderGraphValidationIssueKind.TimelineMappingMismatch);
        }

        if (graph.ProfileId != context.Profile.Id
            || graph.ProfileDigest != context.Profile.ProfileDigest)
        {
            issues.Add(RenderGraphValidationIssueKind.ProfileBindingMismatch);
        }

        if (graph.LayoutSpecId != context.LayoutSpec.Id
            || graph.LayoutSpecDigest != context.LayoutSpecDigest
            || context.Profile.LayoutSpecId != context.LayoutSpec.Id)
        {
            issues.Add(RenderGraphValidationIssueKind.LayoutBindingMismatch);
        }

        if (!graph.Template.Equals(context.Template)
            || context.Profile.TemplateId != context.Template.Id
            || context.Profile.TemplateVersionDigest !=
                context.Template.VersionDigest)
        {
            issues.Add(RenderGraphValidationIssueKind.TemplateBindingMismatch);
        }

        bool graphFontsMatch = graph.Fonts.Count == context.Fonts.Count
            && graph.Fonts.All(font => context.Fonts.Any(actual =>
                string.Equals(actual.Name, font.Name, StringComparison.Ordinal)
                && actual.FileDigest == font.FileDigest));
        bool subtitleNeedsFont = graph.Nodes.Any(static node =>
            node.Kind == RenderNodeKind.Subtitle);
        if (!graphFontsMatch || subtitleNeedsFont && graph.Fonts.Count == 0)
        {
            issues.Add(RenderGraphValidationIssueKind.FontEvidenceMismatch);
        }
    }

    private static void ValidateArtifacts(
        RenderGraph graph,
        RenderGraphValidationContext context,
        IssueCollector issues,
        CancellationToken cancellationToken)
    {
        Dictionary<EntityId<RenderInputIdTag>, RenderArtifactEvidence> actual =
            context.Artifacts.ToDictionary(static item => item.Id);
        foreach (RenderGraphInput input in graph.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(input.Id, out RenderArtifactEvidence? evidence))
            {
                issues.Add(
                    RenderGraphValidationIssueKind.InputArtifactMissing,
                    inputId: input.Id);
                continue;
            }

            if (evidence.ContentDigest != input.ContentDigest)
            {
                issues.Add(
                    RenderGraphValidationIssueKind.InputDigestMismatch,
                    inputId: input.Id);
            }

            if (evidence.ByteSize != input.ByteSize)
            {
                issues.Add(
                    RenderGraphValidationIssueKind.InputSizeMismatch,
                    inputId: input.Id);
            }
        }
    }

    private static void ValidateOrigins(
        RenderGraph graph,
        RenderGraphValidationContext context,
        IssueCollector issues,
        CancellationToken cancellationToken)
    {
        foreach (RenderGraphInput input in graph.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Asset? asset = ResolveAsset(input.Origin, context, out bool digestMismatch);
            if (asset is null && !OriginIsEditorial(input.Origin, context))
            {
                issues.Add(
                    RenderGraphValidationIssueKind.OriginMissing,
                    inputId: input.Id);
                continue;
            }

            if (digestMismatch)
            {
                issues.Add(
                    RenderGraphValidationIssueKind.OriginDigestMismatch,
                    inputId: input.Id);
            }

            if (asset is not null)
            {
                ValidateRights(asset, context.Project, issues, input.Id);
            }
        }
    }

    private static void ValidateNodes(
        RenderGraph graph,
        RenderGraphValidationContext context,
        IReadOnlyDictionary<EntityId<RenderInputIdTag>, RenderGraphInput> inputs,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes,
        IssueCollector issues,
        CancellationToken cancellationToken)
    {
        RationalTime timelineEnd = context.TimelineMap.Entries.Count == 0
            ? default
            : context.TimelineMap.Entries[^1].OutputRange.End;
        var cueIds = new HashSet<EntityId<CueIdTag>>();
        foreach (RenderNode node in graph.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (EntityId<RenderNodeIdTag> dependency in node.InputNodeIds)
            {
                if (!nodes.ContainsKey(dependency))
                {
                    issues.Add(
                        RenderGraphValidationIssueKind.DanglingNodeInput,
                        node.Id);
                }
            }

            if (node.OutputRange.Start < default(RationalTime)
                || node.OutputRange.End > timelineEnd
                || node.Kind != RenderNodeKind.Transition
                    && node.OutputRange.Duration <= default(RationalTime))
            {
                issues.Add(
                    RenderGraphValidationIssueKind.NodeRangeOutOfBounds,
                    node.Id);
            }

            if (node.Cue is not null && !cueIds.Add(node.Cue.Id))
            {
                issues.Add(
                    RenderGraphValidationIssueKind.DuplicateCue,
                    node.Id);
            }

            ValidateNodeInput(node, inputs, issues);
            ValidateNodeOrigin(node, inputs, context, issues);
            ValidateNodeDependencies(node, nodes, issues);
            ValidateNodeLayout(node, context.LayoutSpec, issues);
            if (node.Kind == RenderNodeKind.Video
                && !context.TimelineMap.Entries.Any(entry =>
                    entry.OutputRange == node.OutputRange
                    && entry.Origin == node.Origin
                    && entry.SourceRange == node.SourceRange))
            {
                issues.Add(
                    RenderGraphValidationIssueKind.TimelineMappingMismatch,
                    node.Id);
            }
        }

        RenderNode? output = nodes.GetValueOrDefault(graph.OutputNodeId);
        RenderNode[] outputNodes = graph.Nodes
            .Where(static node => node.Kind == RenderNodeKind.Output)
            .ToArray();
        if (output is null || output.Kind != RenderNodeKind.Output)
        {
            issues.Add(
                RenderGraphValidationIssueKind.OutputNodeInvalid,
                graph.OutputNodeId);
        }

        foreach (RenderNode extra in outputNodes.Where(node =>
            node.Id != graph.OutputNodeId))
        {
            issues.Add(
                RenderGraphValidationIssueKind.OutputNodeInvalid,
                extra.Id);
        }

        if (output is not null)
        {
            RenderNode[] terminalInputs = output.InputNodeIds
                .Where(nodes.ContainsKey)
                .Select(id => nodes[id])
                .ToArray();
            int audioCount = terminalInputs.Count(static node =>
                node.Kind == RenderNodeKind.Audio);
            int subtitleCount = terminalInputs.Count(static node =>
                node.Kind == RenderNodeKind.Subtitle);
            int primaryVisualCount = terminalInputs.Count(static node =>
                node.Kind is RenderNodeKind.Video
                    or RenderNodeKind.Graphic
                    or RenderNodeKind.Transition);
            bool streamsMatch = context.Profile.SubtitleBurnIn
                ? subtitleCount == 1
                    && primaryVisualCount == 0
                    && audioCount <= 1
                    && terminalInputs.Length == subtitleCount + audioCount
                : primaryVisualCount == 1
                    && subtitleCount <= 1
                    && audioCount <= 1
                    && terminalInputs.Length ==
                        primaryVisualCount + subtitleCount + audioCount;
            if (!streamsMatch)
            {
                issues.Add(
                    RenderGraphValidationIssueKind.OutputStreamMismatch,
                    output.Id);
            }
        }

        TimeRange expectedOutput = new(default, timelineEnd);
        if (output is not null && output.OutputRange != expectedOutput)
        {
            issues.Add(
                RenderGraphValidationIssueKind.OutputRangeMismatch,
                output.Id);
        }
    }

    private static void ValidateNodeInput(
        RenderNode node,
        IReadOnlyDictionary<EntityId<RenderInputIdTag>, RenderGraphInput> inputs,
        IssueCollector issues)
    {
        if (node.ArtifactInputId is not EntityId<RenderInputIdTag> inputId)
        {
            return;
        }

        if (!inputs.TryGetValue(inputId, out RenderGraphInput? input))
        {
            issues.Add(
                RenderGraphValidationIssueKind.InputArtifactMissing,
                node.Id,
                inputId);
            return;
        }

        RenderInputKind expected = node.Kind switch
        {
            RenderNodeKind.Video => RenderInputKind.Video,
            RenderNodeKind.Audio => RenderInputKind.Audio,
            RenderNodeKind.Subtitle => RenderInputKind.Subtitle,
            RenderNodeKind.Graphic => RenderInputKind.Graphic,
            _ => throw new InvalidOperationException(
                "Only input-backed render nodes may reference an artifact input."),
        };
        if (input.Kind != expected)
        {
            issues.Add(
                RenderGraphValidationIssueKind.InputKindMismatch,
                node.Id,
                inputId);
        }
    }

    private static void ValidateNodeOrigin(
        RenderNode node,
        IReadOnlyDictionary<EntityId<RenderInputIdTag>, RenderGraphInput> inputs,
        RenderGraphValidationContext context,
        IssueCollector issues)
    {
        if (node.Origin is null)
        {
            return;
        }

        if (node.ArtifactInputId is EntityId<RenderInputIdTag> inputId
            && inputs.TryGetValue(inputId, out RenderGraphInput? input)
            && input.Origin != node.Origin)
        {
            issues.Add(
                RenderGraphValidationIssueKind.InputOriginMismatch,
                node.Id,
                inputId);
        }

        Asset? asset = ResolveAsset(node.Origin, context, out bool digestMismatch);
        if (asset is null && !OriginIsEditorial(node.Origin, context))
        {
            issues.Add(RenderGraphValidationIssueKind.OriginMissing, node.Id);
        }

        if (digestMismatch)
        {
            issues.Add(RenderGraphValidationIssueKind.OriginDigestMismatch, node.Id);
        }

        if (asset is not null)
        {
            ValidateRights(asset, context.Project, issues, nodeId: node.Id);
        }

        if (node.Kind is RenderNodeKind.Video or RenderNodeKind.Audio
            && node.Origin.Kind == OriginKind.SourceSpan)
        {
            SourceSpan? sourceSpan = context.SourceSpans.SingleOrDefault(span =>
                span.Id == node.Origin.SourceSpanId);
            if (sourceSpan is null
                || node.SourceRange is not TimeRange sourceRange
                || sourceRange.Start < sourceSpan.Range.Start
                || sourceRange.End > sourceSpan.Range.End)
            {
                issues.Add(
                    RenderGraphValidationIssueKind.SourceRangeMismatch,
                    node.Id);
            }
        }
    }

    private static void ValidateNodeDependencies(
        RenderNode node,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes,
        IssueCollector issues)
    {
        RenderNode[] resolvedInputs = node.InputNodeIds
            .Where(nodes.ContainsKey)
            .Select(id => nodes[id])
            .ToArray();
        if (node.Kind is RenderNodeKind.Subtitle or RenderNodeKind.Graphic)
        {
            if (resolvedInputs.Length != 1
                || !IsVisualProducer(resolvedInputs[0].Kind))
            {
                issues.Add(
                    RenderGraphValidationIssueKind.NodeDependencyKindMismatch,
                    node.Id);
            }

            return;
        }

        if (node.Kind != RenderNodeKind.Transition || node.Transition is null)
        {
            return;
        }

        int expectedCount = node.Transition.Kind is
            TransitionKind.Cut
            or TransitionKind.Crossfade
            or TransitionKind.Wipe
            or TransitionKind.Slide
            ? 2
            : 1;
        if (resolvedInputs.Length != expectedCount
            || resolvedInputs.Any(input => !IsVisualProducer(input.Kind)))
        {
            issues.Add(
                RenderGraphValidationIssueKind.TransitionInputMismatch,
                node.Id);
        }
    }

    private static bool IsVisualProducer(RenderNodeKind kind) =>
        kind is RenderNodeKind.Video
            or RenderNodeKind.Graphic
            or RenderNodeKind.Transition;

    private static void ValidateNodeLayout(
        RenderNode node,
        LayoutSpec layout,
        IssueCollector issues)
    {
        if (node.RegionName is null)
        {
            return;
        }

        LayoutRegion? region = layout.Regions.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, node.RegionName, StringComparison.Ordinal));
        if (region is null)
        {
            issues.Add(RenderGraphValidationIssueKind.LayoutRegionMissing, node.Id);
            return;
        }

        bool roleMatches = node.Kind switch
        {
            RenderNodeKind.Video =>
                region.Role is LayoutRegionRole.Video or LayoutRegionRole.Material,
            RenderNodeKind.Subtitle => region.Role == LayoutRegionRole.Subtitle,
            RenderNodeKind.Graphic => region.Role == LayoutRegionRole.Graphic,
            _ => true,
        };
        if (!roleMatches)
        {
            issues.Add(RenderGraphValidationIssueKind.LayoutRoleMismatch, node.Id);
        }
    }

    private static void ValidateGraphTopology(
        RenderGraph graph,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes,
        IssueCollector issues,
        CancellationToken cancellationToken)
    {
        var visiting = new HashSet<EntityId<RenderNodeIdTag>>();
        var visited = new HashSet<EntityId<RenderNodeIdTag>>();
        foreach (RenderNode node in graph.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasCycle(node.Id, nodes, visiting, visited, cancellationToken))
            {
                issues.Add(RenderGraphValidationIssueKind.Cycle, node.Id);
                break;
            }
        }

        if (!nodes.ContainsKey(graph.OutputNodeId))
        {
            return;
        }

        var reachable = new HashSet<EntityId<RenderNodeIdTag>>();
        MarkReachable(
            graph.OutputNodeId,
            nodes,
            reachable,
            cancellationToken);
        foreach (RenderNode node in graph.Nodes)
        {
            if (!reachable.Contains(node.Id))
            {
                issues.Add(
                    RenderGraphValidationIssueKind.UnreachableNode,
                    node.Id);
            }
        }
    }

    private static bool HasCycle(
        EntityId<RenderNodeIdTag> id,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes,
        HashSet<EntityId<RenderNodeIdTag>> visiting,
        HashSet<EntityId<RenderNodeIdTag>> visited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (visited.Contains(id))
        {
            return false;
        }

        if (!visiting.Add(id))
        {
            return true;
        }

        if (nodes.TryGetValue(id, out RenderNode? node))
        {
            foreach (EntityId<RenderNodeIdTag> dependency in node.InputNodeIds)
            {
                if (nodes.ContainsKey(dependency)
                    && HasCycle(
                        dependency,
                        nodes,
                        visiting,
                        visited,
                        cancellationToken))
                {
                    return true;
                }
            }
        }

        _ = visiting.Remove(id);
        _ = visited.Add(id);
        return false;
    }

    private static void MarkReachable(
        EntityId<RenderNodeIdTag> id,
        IReadOnlyDictionary<EntityId<RenderNodeIdTag>, RenderNode> nodes,
        HashSet<EntityId<RenderNodeIdTag>> reachable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!reachable.Add(id) || !nodes.TryGetValue(id, out RenderNode? node))
        {
            return;
        }

        foreach (EntityId<RenderNodeIdTag> dependency in node.InputNodeIds)
        {
            MarkReachable(dependency, nodes, reachable, cancellationToken);
        }
    }

    private static Asset? ResolveAsset(
        OriginRef origin,
        RenderGraphValidationContext context,
        out bool digestMismatch)
    {
        digestMismatch = false;
        if (origin.Kind == OriginKind.Asset)
        {
            return context.Project.Assets.SingleOrDefault(asset =>
                asset.Id == origin.AssetId);
        }

        if (origin.Kind != OriginKind.SourceSpan)
        {
            return null;
        }

        SourceSpan? span = context.SourceSpans.SingleOrDefault(candidate =>
            candidate.Id == origin.SourceSpanId);
        if (span is null)
        {
            return null;
        }

        Asset? sourceAsset = context.Project.Assets.SingleOrDefault(asset =>
            asset.Id == span.AssetId);
        digestMismatch = sourceAsset is not null
            && sourceAsset.ContentDigest != span.SourceDigest;
        return sourceAsset;
    }

    private static bool OriginIsEditorial(
        OriginRef origin,
        RenderGraphValidationContext context) =>
        origin.Kind == OriginKind.EditorialRecord
        && context.EditorialRecordIds.Contains(origin.EditorialRecordId!);

    private static void ValidateRights(
        Asset asset,
        Project project,
        IssueCollector issues,
        EntityId<RenderInputIdTag>? inputId = null,
        EntityId<RenderNodeIdTag>? nodeId = null)
    {
        if (asset.Rights.Status == RightsStatus.Rejected)
        {
            issues.Add(
                RenderGraphValidationIssueKind.RightsRejected,
                nodeId,
                inputId: inputId);
            return;
        }

        bool external = project.PublicationScope is
            PublicationScope.ExternalCandidate
            or PublicationScope.ExternalPublicationApproved;
        if (external
            && (asset.Rights.Status != RightsStatus.Approved
                || !asset.Rights.ExternalPublicationAllowed))
        {
            issues.Add(
                RenderGraphValidationIssueKind.ExternalRightsNotApproved,
                nodeId,
                inputId: inputId);
        }
    }

    private static bool TimelineMatchesComposition(
        TimelineMap timeline,
        IReadOnlyList<CompositionElement> elements)
    {
        if (timeline.Entries.Count != elements.Count)
        {
            return false;
        }

        for (int index = 0; index < elements.Count; index++)
        {
            CompositionElement element = elements[index];
            TimelineMapEntry entry = timeline.Entries[index];
            if (entry.OutputRange != element.OutputRange
                || entry.Origin != element.Origin
                || entry.SourceRange != element.SourceRange)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class IssueCollector
    {
        private readonly List<RenderGraphValidationIssue> _issues = [];
        private readonly HashSet<IssueKey> _keys = [];

        internal void Add(
            RenderGraphValidationIssueKind kind,
            EntityId<RenderNodeIdTag>? nodeId = null,
            EntityId<RenderInputIdTag>? inputId = null)
        {
            var key = new IssueKey(kind, nodeId, inputId);
            if (_keys.Add(key))
            {
                _issues.Add(new RenderGraphValidationIssue(kind, nodeId, inputId));
            }
        }

        internal RenderGraphValidationIssue[] ToCanonicalArray() =>
            _issues
                .OrderBy(static issue => issue.Kind)
                .ThenBy(
                    static issue => issue.NodeId?.ToString(),
                    StringComparer.Ordinal)
                .ThenBy(
                    static issue => issue.InputId?.ToString(),
                    StringComparer.Ordinal)
                .ToArray();

        private sealed record IssueKey(
            RenderGraphValidationIssueKind Kind,
            EntityId<RenderNodeIdTag>? NodeId,
            EntityId<RenderInputIdTag>? InputId);
    }
}