using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Rendering;

public sealed class RenderGraphIdTag
{
}

public sealed class RenderNodeIdTag
{
}

public sealed class RenderInputIdTag
{
}

public enum RenderInputKind
{
    Video,
    Audio,
    Subtitle,
    Graphic,
}

public enum RenderNodeKind
{
    Video,
    Audio,
    Subtitle,
    Graphic,
    Transition,
    Output,
}

public sealed class RenderGraphInput
{
    public RenderGraphInput(
        EntityId<RenderInputIdTag> id,
        RenderInputKind kind,
        ContentDigest contentDigest,
        long byteSize,
        OriginRef origin)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(origin);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteSize));
        }

        Id = id;
        Kind = kind;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        Origin = origin;
    }

    public EntityId<RenderInputIdTag> Id { get; }

    public RenderInputKind Kind { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public OriginRef Origin { get; }
}

public sealed class RenderArtifactEvidence
{
    public RenderArtifactEvidence(
        EntityId<RenderInputIdTag> id,
        ContentDigest contentDigest,
        long byteSize)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(contentDigest);
        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteSize));
        }

        Id = id;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
    }

    public EntityId<RenderInputIdTag> Id { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }
}

public sealed class RenderNode
{
    private RenderNode(
        EntityId<RenderNodeIdTag> id,
        RenderNodeKind kind,
        IReadOnlyList<EntityId<RenderNodeIdTag>> inputNodeIds,
        EntityId<RenderInputIdTag>? artifactInputId,
        TimeRange outputRange,
        TimeRange? sourceRange,
        OriginRef? origin,
        string? regionName,
        Cue? cue,
        Transition? transition)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        EntityId<RenderNodeIdTag>[] copiedInputs =
            RenderGraphContract.CopyUniqueIds(inputNodeIds, nameof(inputNodeIds));
        ValidateShape(
            kind,
            copiedInputs,
            artifactInputId,
            outputRange,
            sourceRange,
            origin,
            regionName,
            cue,
            transition);
        Id = id;
        Kind = kind;
        InputNodeIds = Array.AsReadOnly(copiedInputs);
        ArtifactInputId = artifactInputId;
        OutputRange = outputRange;
        SourceRange = sourceRange;
        Origin = origin;
        RegionName = regionName;
        Cue = cue;
        Transition = transition;
    }

    public EntityId<RenderNodeIdTag> Id { get; }

    public RenderNodeKind Kind { get; }

    public IReadOnlyList<EntityId<RenderNodeIdTag>> InputNodeIds { get; }

    public EntityId<RenderInputIdTag>? ArtifactInputId { get; }

    public TimeRange OutputRange { get; }

    public TimeRange? SourceRange { get; }

    public OriginRef? Origin { get; }

    public string? RegionName { get; }

    public Cue? Cue { get; }

    public Transition? Transition { get; }

    public static RenderNode Video(
        EntityId<RenderNodeIdTag> id,
        EntityId<RenderInputIdTag> artifactInputId,
        TimeRange outputRange,
        OriginRef origin,
        TimeRange? sourceRange,
        string regionName) =>
        new(
            id,
            RenderNodeKind.Video,
            [],
            artifactInputId,
            outputRange,
            sourceRange,
            origin,
            RenderGraphContract.RequireText(regionName, nameof(regionName)),
            cue: null,
            transition: null);

    public static RenderNode Audio(
        EntityId<RenderNodeIdTag> id,
        EntityId<RenderInputIdTag> artifactInputId,
        TimeRange outputRange,
        OriginRef origin,
        TimeRange? sourceRange) =>
        new(
            id,
            RenderNodeKind.Audio,
            [],
            artifactInputId,
            outputRange,
            sourceRange,
            origin,
            regionName: null,
            cue: null,
            transition: null);

    public static RenderNode Subtitle(
        EntityId<RenderNodeIdTag> id,
        EntityId<RenderInputIdTag> artifactInputId,
        EntityId<RenderNodeIdTag> targetVisualNodeId,
        Cue cue,
        OriginRef origin)
    {
        ArgumentNullException.ThrowIfNull(targetVisualNodeId);
        ArgumentNullException.ThrowIfNull(cue);
        return new RenderNode(
            id,
            RenderNodeKind.Subtitle,
            [targetVisualNodeId],
            artifactInputId,
            cue.Range,
            sourceRange: null,
            origin,
            cue.RegionName,
            cue,
            transition: null);
    }

    public static RenderNode Graphic(
        EntityId<RenderNodeIdTag> id,
        EntityId<RenderInputIdTag> artifactInputId,
        EntityId<RenderNodeIdTag> targetVisualNodeId,
        Cue cue,
        OriginRef origin)
    {
        ArgumentNullException.ThrowIfNull(targetVisualNodeId);
        ArgumentNullException.ThrowIfNull(cue);
        return new RenderNode(
            id,
            RenderNodeKind.Graphic,
            [targetVisualNodeId],
            artifactInputId,
            cue.Range,
            sourceRange: null,
            origin,
            cue.RegionName,
            cue,
            transition: null);
    }

    public static RenderNode TransitionEffect(
        EntityId<RenderNodeIdTag> id,
        IReadOnlyList<EntityId<RenderNodeIdTag>> inputNodeIds,
        TimeRange outputRange,
        OriginRef origin,
        Transition transition) =>
        new(
            id,
            RenderNodeKind.Transition,
            inputNodeIds,
            artifactInputId: null,
            outputRange,
            sourceRange: null,
            origin,
            regionName: null,
            cue: null,
            transition);

    public static RenderNode Output(
        EntityId<RenderNodeIdTag> id,
        IReadOnlyList<EntityId<RenderNodeIdTag>> inputNodeIds,
        TimeRange outputRange) =>
        new(
            id,
            RenderNodeKind.Output,
            inputNodeIds,
            artifactInputId: null,
            outputRange,
            sourceRange: null,
            origin: null,
            regionName: null,
            cue: null,
            transition: null);

    private static void ValidateShape(
        RenderNodeKind kind,
        IReadOnlyList<EntityId<RenderNodeIdTag>> inputs,
        EntityId<RenderInputIdTag>? artifactInputId,
        TimeRange outputRange,
        TimeRange? sourceRange,
        OriginRef? origin,
        string? regionName,
        Cue? cue,
        Transition? transition)
    {
        bool rangeIsPositive = outputRange.Start >= default(RationalTime)
            && outputRange.Duration > default(RationalTime);
        bool sourceShape = origin?.Kind == OriginKind.SourceSpan
            ? sourceRange is TimeRange source
                && source.Duration == outputRange.Duration
            : sourceRange is null;
        bool valid = kind switch
        {
            RenderNodeKind.Video =>
                rangeIsPositive
                && inputs.Count == 0
                && artifactInputId is not null
                && origin is not null
                && sourceShape
                && regionName is not null
                && cue is null
                && transition is null,
            RenderNodeKind.Audio =>
                rangeIsPositive
                && inputs.Count == 0
                && artifactInputId is not null
                && origin is not null
                && sourceShape
                && regionName is null
                && cue is null
                && transition is null,
            RenderNodeKind.Subtitle =>
                rangeIsPositive
                && inputs.Count == 1
                && artifactInputId is not null
                && origin is not null
                && sourceRange is null
                && regionName is not null
                && cue?.Kind == CueKind.Subtitle
                && cue.Range == outputRange
                && string.Equals(cue.RegionName, regionName, StringComparison.Ordinal)
                && transition is null,
            RenderNodeKind.Graphic =>
                rangeIsPositive
                && inputs.Count == 1
                && artifactInputId is not null
                && origin is not null
                && sourceRange is null
                && regionName is not null
                && cue?.Kind == CueKind.Graphic
                && cue.Range == outputRange
                && string.Equals(cue.RegionName, regionName, StringComparison.Ordinal)
                && transition is null,
            RenderNodeKind.Transition =>
                outputRange.Start >= default(RationalTime)
                && inputs.Count is 1 or 2
                && artifactInputId is null
                && origin is not null
                && sourceRange is null
                && regionName is null
                && cue is null
                && transition is not null
                && (transition.Kind == TransitionKind.Cut
                    ? outputRange.Duration == default(RationalTime)
                    : outputRange.Duration == transition.Duration),
            RenderNodeKind.Output =>
                rangeIsPositive
                && inputs.Count > 0
                && artifactInputId is null
                && sourceRange is null
                && origin is null
                && regionName is null
                && cue is null
                && transition is null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The render node does not match its closed typed shape.",
                nameof(kind));
        }
    }
}

public sealed class RenderGraph
{
    public RenderGraph(
        EntityId<RenderGraphIdTag> id,
        EntityId<CompositionVersionIdTag> compositionVersionId,
        ContentDigest compositionVersionDigest,
        EntityId<TimelineMapIdTag> timelineMapId,
        ContentDigest timelineMapDigest,
        EntityId<RenderProfileIdTag> profileId,
        ContentDigest profileDigest,
        EntityId<LayoutSpecIdTag> layoutSpecId,
        ContentDigest layoutSpecDigest,
        TemplateReference template,
        IReadOnlyList<RenderGraphInput> inputs,
        IReadOnlyList<RenderFontEvidence> fonts,
        IReadOnlyList<RenderNode> nodes,
        EntityId<RenderNodeIdTag> outputNodeId)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(compositionVersionId);
        ArgumentNullException.ThrowIfNull(compositionVersionDigest);
        ArgumentNullException.ThrowIfNull(timelineMapId);
        ArgumentNullException.ThrowIfNull(timelineMapDigest);
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(profileDigest);
        ArgumentNullException.ThrowIfNull(layoutSpecId);
        ArgumentNullException.ThrowIfNull(layoutSpecDigest);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(outputNodeId);

        RenderGraphInput[] copiedInputs = CopyUniqueInputs(inputs);
        RenderFontEvidence[] copiedFonts = CopyUniqueFonts(fonts);
        RenderNode[] copiedNodes = CopyUniqueNodes(nodes);
        Id = id;
        CompositionVersionId = compositionVersionId;
        CompositionVersionDigest = compositionVersionDigest;
        TimelineMapId = timelineMapId;
        TimelineMapDigest = timelineMapDigest;
        ProfileId = profileId;
        ProfileDigest = profileDigest;
        LayoutSpecId = layoutSpecId;
        LayoutSpecDigest = layoutSpecDigest;
        Template = template;
        Inputs = Array.AsReadOnly(copiedInputs);
        Fonts = Array.AsReadOnly(copiedFonts);
        Nodes = Array.AsReadOnly(copiedNodes);
        OutputNodeId = outputNodeId;
    }

    public EntityId<RenderGraphIdTag> Id { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<TimelineMapIdTag> TimelineMapId { get; }

    public ContentDigest TimelineMapDigest { get; }

    public EntityId<RenderProfileIdTag> ProfileId { get; }

    public ContentDigest ProfileDigest { get; }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public ContentDigest LayoutSpecDigest { get; }

    public TemplateReference Template { get; }

    public IReadOnlyList<RenderGraphInput> Inputs { get; }

    public IReadOnlyList<RenderFontEvidence> Fonts { get; }

    public IReadOnlyList<RenderNode> Nodes { get; }

    public EntityId<RenderNodeIdTag> OutputNodeId { get; }

    private static RenderGraphInput[] CopyUniqueInputs(
        IReadOnlyList<RenderGraphInput> inputs)
    {
        var copied = new RenderGraphInput[inputs.Count];
        var ids = new HashSet<EntityId<RenderInputIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            RenderGraphInput input = inputs[index]
                ?? throw new ArgumentException(
                    "Render graph inputs must not contain null values.",
                    nameof(inputs));
            if (!ids.Add(input.Id))
            {
                throw new ArgumentException(
                    "Render graph input identities must be unique.",
                    nameof(inputs));
            }

            copied[index] = input;
        }

        return copied;
    }

    private static RenderFontEvidence[] CopyUniqueFonts(
        IReadOnlyList<RenderFontEvidence> fonts)
    {
        var copied = new RenderFontEvidence[fonts.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copied.Length; index++)
        {
            RenderFontEvidence font = fonts[index]
                ?? throw new ArgumentException(
                    "Render graph fonts must not contain null values.",
                    nameof(fonts));
            if (!names.Add(font.Name))
            {
                throw new ArgumentException(
                    "Render graph font names must be unique.",
                    nameof(fonts));
            }

            copied[index] = font;
        }

        return copied;
    }

    private static RenderNode[] CopyUniqueNodes(IReadOnlyList<RenderNode> nodes)
    {
        var copied = new RenderNode[nodes.Count];
        var ids = new HashSet<EntityId<RenderNodeIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            RenderNode node = nodes[index]
                ?? throw new ArgumentException(
                    "Render graph nodes must not contain null values.",
                    nameof(nodes));
            if (!ids.Add(node.Id))
            {
                throw new ArgumentException(
                    "Render graph node identities must be unique.",
                    nameof(nodes));
            }

            copied[index] = node;
        }

        return copied;
    }
}

internal static class RenderGraphContract
{
    internal static EntityId<TTag>[] CopyUniqueIds<TTag>(
        IReadOnlyList<EntityId<TTag>> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copied = new EntityId<TTag>[values.Count];
        var ids = new HashSet<EntityId<TTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            EntityId<TTag> value = values[index]
                ?? throw new ArgumentException(
                    "Render graph identity lists must not contain null values.",
                    parameterName);
            if (!ids.Add(value))
            {
                throw new ArgumentException(
                    "Render graph identity lists must be unique.",
                    parameterName);
            }

            copied[index] = value;
        }

        return copied;
    }

    internal static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Render graph text must be nonblank and trimmed.",
                parameterName);
        }

        return value;
    }
}