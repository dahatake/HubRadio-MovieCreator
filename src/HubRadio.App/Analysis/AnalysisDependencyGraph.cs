using System.Text;
using HubRadio.App.Jobs;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;

namespace HubRadio.App.Analysis;

public enum AnalysisDependencyNodeKind
{
    SourceAssetSnapshot,
    AudioExtraction,
    AutomaticSpeechRecognition,
    TranscriptRevision,
    SourceSpanSnapshot,
    DictionarySnapshot,
    TermScopeSnapshot,
    ProjectSnapshot,
    ProductTermSelectionSnapshot,
    SilenceCandidates,
    AudioReviewCandidates,
    TermCandidates,
    EditorialPhraseCandidates,
    RepetitionCandidates,
    TopicBoundaryCandidates,
    TopicRangeBinding,
    ContextDeviationCandidates,
    SpeakerAssignmentBinding,
    TopicMetadata,
    EmphasisCandidates,
}

public enum AnalysisDependencyGraphFailureReason
{
    InvalidNode,
    DuplicateNode,
    DuplicateEdge,
    UnknownNode,
    Cycle,
    UnsupportedArtifact,
    InvalidDependencySnapshot,
}

public sealed class AnalysisDependencyGraphException : Exception
{
    internal AnalysisDependencyGraphException(
        AnalysisDependencyGraphFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public AnalysisDependencyGraphFailureReason Reason { get; }

    private static string GetStableMessage(
        AnalysisDependencyGraphFailureReason reason) =>
        reason switch
        {
            AnalysisDependencyGraphFailureReason.InvalidNode =>
                "The analysis dependency graph contains an invalid node.",
            AnalysisDependencyGraphFailureReason.DuplicateNode =>
                "The analysis dependency graph contains a duplicate node.",
            AnalysisDependencyGraphFailureReason.DuplicateEdge =>
                "The analysis dependency graph contains a duplicate edge.",
            AnalysisDependencyGraphFailureReason.UnknownNode =>
                "An analysis dependency edge references an unknown node.",
            AnalysisDependencyGraphFailureReason.Cycle =>
                "The analysis dependency graph contains a cycle.",
            AnalysisDependencyGraphFailureReason.UnsupportedArtifact =>
                "The requested analysis node has no automatic cache plan.",
            AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot =>
                "The analysis artifact inputs do not match its dependency contract.",
            _ => "Analysis dependency planning failed.",
        };
}

public sealed class AnalysisDependencyEdge
{
    internal AnalysisDependencyEdge(
        AnalysisDependencyNodeKind from,
        AnalysisDependencyNodeKind to)
    {
        From = from;
        To = to;
    }

    public AnalysisDependencyNodeKind From { get; }

    public AnalysisDependencyNodeKind To { get; }
}

public sealed class AnalysisDependencySnapshot
{
    public AnalysisDependencySnapshot(
        AnalysisDependencyNodeKind node,
        ContentDigest digest)
    {
        AnalysisDependencyContract.RequireDefinedNode(node);
        ArgumentNullException.ThrowIfNull(digest);
        Node = node;
        Digest = digest;
    }

    public AnalysisDependencyNodeKind Node { get; }

    public ContentDigest Digest { get; }
}

public sealed class AnalysisInvalidationPlan
{
    internal AnalysisInvalidationPlan(
        ContentDigest previousTranscriptDigest,
        ContentDigest currentTranscriptDigest,
        IReadOnlyList<AnalysisDependencyNodeKind> staleNodes,
        IReadOnlyList<AnalysisDependencyNodeKind> recomputationNodes,
        IReadOnlyList<AnalysisDependencyNodeKind> manualRebindingNodes)
    {
        ArgumentNullException.ThrowIfNull(previousTranscriptDigest);
        ArgumentNullException.ThrowIfNull(currentTranscriptDigest);
        ArgumentNullException.ThrowIfNull(staleNodes);
        ArgumentNullException.ThrowIfNull(recomputationNodes);
        ArgumentNullException.ThrowIfNull(manualRebindingNodes);

        AnalysisDependencyNodeKind[] copiedStale = staleNodes.ToArray();
        AnalysisDependencyNodeKind[] copiedRecomputation =
            recomputationNodes.ToArray();
        AnalysisDependencyNodeKind[] copiedManual = manualRebindingNodes.ToArray();
        bool changed = previousTranscriptDigest != currentTranscriptDigest;
        if (!changed
            && (copiedStale.Length != 0
                || copiedRecomputation.Length != 0
                || copiedManual.Length != 0)
            || copiedRecomputation.Except(copiedStale).Any()
            || copiedManual.Except(copiedStale).Any()
            || copiedRecomputation.Intersect(copiedManual).Any())
        {
            throw new ArgumentException(
                "The analysis invalidation plan is inconsistent.",
                nameof(staleNodes));
        }

        PreviousTranscriptDigest = previousTranscriptDigest;
        CurrentTranscriptDigest = currentTranscriptDigest;
        StaleNodes = Array.AsReadOnly(copiedStale);
        RecomputationNodes = Array.AsReadOnly(copiedRecomputation);
        ManualRebindingNodes = Array.AsReadOnly(copiedManual);
    }

    public ContentDigest PreviousTranscriptDigest { get; }

    public ContentDigest CurrentTranscriptDigest { get; }

    public bool HasTranscriptChange =>
        PreviousTranscriptDigest != CurrentTranscriptDigest;

    public IReadOnlyList<AnalysisDependencyNodeKind> StaleNodes { get; }

    public IReadOnlyList<AnalysisDependencyNodeKind> RecomputationNodes { get; }

    public IReadOnlyList<AnalysisDependencyNodeKind> ManualRebindingNodes { get; }
}

public sealed class AnalysisArtifactPlan
{
    internal AnalysisArtifactPlan(
        AnalysisDependencyNodeKind artifact,
        ContentDigest configurationDigest,
        IReadOnlyList<AnalysisDependencySnapshot> inputs,
        ArtifactPlan cachePlan)
    {
        AnalysisDependencyContract.RequireDefinedNode(artifact);
        ArgumentNullException.ThrowIfNull(configurationDigest);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(cachePlan);
        AnalysisDependencySnapshot[] copiedInputs = inputs.ToArray();
        if (copiedInputs.Any(static input => input is null)
            || cachePlan.ExpectedCacheKey != ArtifactCacheKey.Create(
                cachePlan.Operation,
                configurationDigest,
                copiedInputs.Select(static input => input.Digest).ToArray()))
        {
            throw new ArgumentException(
                "The analysis artifact plan is inconsistent.",
                nameof(cachePlan));
        }

        Artifact = artifact;
        Operation = cachePlan.Operation;
        ConfigurationDigest = configurationDigest;
        Inputs = Array.AsReadOnly(copiedInputs);
        CachePlan = cachePlan;
    }

    public AnalysisDependencyNodeKind Artifact { get; }

    public string Operation { get; }

    public ContentDigest ConfigurationDigest { get; }

    public IReadOnlyList<AnalysisDependencySnapshot> Inputs { get; }

    public ArtifactPlan CachePlan { get; }
}

public sealed class AnalysisDependencyGraph
{
    public const string ConfigurationDomain =
        "HubRadio.MovieCreator.AnalysisDependencyConfiguration/v1";

    private const string TermCandidateConfigurationVersion =
        "a006-2026-08-31.exact-dictionary-snapshot.v1";
    private const string TopicMetadataConfigurationVersion =
        "a020-2026-08-31.manual-first-metadata.v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly IReadOnlyDictionary<
        AnalysisDependencyNodeKind,
        AnalysisArtifactContract> ArtifactContracts = CreateArtifactContracts();

    private static readonly HashSet<AnalysisDependencyNodeKind> StaleTrackedNodes =
    [
        AnalysisDependencyNodeKind.TermCandidates,
        AnalysisDependencyNodeKind.EditorialPhraseCandidates,
        AnalysisDependencyNodeKind.RepetitionCandidates,
        AnalysisDependencyNodeKind.TopicBoundaryCandidates,
        AnalysisDependencyNodeKind.TopicRangeBinding,
        AnalysisDependencyNodeKind.ContextDeviationCandidates,
        AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
        AnalysisDependencyNodeKind.TopicMetadata,
        AnalysisDependencyNodeKind.EmphasisCandidates,
    ];

    private static readonly HashSet<AnalysisDependencyNodeKind> ManualBindingNodes =
    [
        AnalysisDependencyNodeKind.TopicRangeBinding,
        AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
    ];

    public static AnalysisDependencyGraph Default { get; } = CreateDefault();

    private readonly IReadOnlyDictionary<
        AnalysisDependencyNodeKind,
        AnalysisDependencyNodeKind[]> _outgoing;
    private readonly HashSet<AnalysisDependencyNodeKind> _nodeSet;
    private readonly AnalysisDependencyNodeKind[] _topologicalOrder;

    internal AnalysisDependencyGraph(
        IReadOnlyList<AnalysisDependencyNodeKind> nodes,
        IReadOnlyList<AnalysisDependencyEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        AnalysisDependencyNodeKind[] copiedNodes = nodes.ToArray();
        _nodeSet = new HashSet<AnalysisDependencyNodeKind>();
        var nodeOrder = new Dictionary<AnalysisDependencyNodeKind, int>();
        for (int index = 0; index < copiedNodes.Length; index++)
        {
            AnalysisDependencyNodeKind node = copiedNodes[index];
            AnalysisDependencyContract.RequireDefinedNode(node);
            if (!_nodeSet.Add(node))
            {
                throw Failure(
                    AnalysisDependencyGraphFailureReason.DuplicateNode);
            }

            nodeOrder.Add(node, index);
        }

        AnalysisDependencyEdge[] copiedEdges = new AnalysisDependencyEdge[edges.Count];
        var uniqueEdges = new HashSet<(
            AnalysisDependencyNodeKind From,
            AnalysisDependencyNodeKind To)>();
        var outgoing = copiedNodes.ToDictionary(
            static node => node,
            static _ => new List<AnalysisDependencyNodeKind>());
        var indegree = copiedNodes.ToDictionary(
            static node => node,
            static _ => 0);
        for (int index = 0; index < copiedEdges.Length; index++)
        {
            AnalysisDependencyEdge edge = edges[index]
                ?? throw new ArgumentException(
                    "Analysis dependency edges must not contain null elements.",
                    nameof(edges));
            AnalysisDependencyContract.RequireDefinedNode(edge.From);
            AnalysisDependencyContract.RequireDefinedNode(edge.To);
            if (!_nodeSet.Contains(edge.From) || !_nodeSet.Contains(edge.To))
            {
                throw Failure(AnalysisDependencyGraphFailureReason.UnknownNode);
            }

            if (!uniqueEdges.Add((edge.From, edge.To)))
            {
                throw Failure(AnalysisDependencyGraphFailureReason.DuplicateEdge);
            }

            copiedEdges[index] = edge;
            outgoing[edge.From].Add(edge.To);
            indegree[edge.To] = checked(indegree[edge.To] + 1);
        }

        foreach (List<AnalysisDependencyNodeKind> targets in outgoing.Values)
        {
            targets.Sort((left, right) =>
                nodeOrder[left].CompareTo(nodeOrder[right]));
        }

        _topologicalOrder = BuildTopologicalOrder(
            copiedNodes,
            outgoing,
            indegree,
            nodeOrder);
        _outgoing = outgoing.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray());
        Nodes = Array.AsReadOnly(copiedNodes);
        Edges = Array.AsReadOnly(copiedEdges);
    }

    public IReadOnlyList<AnalysisDependencyNodeKind> Nodes { get; }

    public IReadOnlyList<AnalysisDependencyEdge> Edges { get; }

    public AnalysisInvalidationPlan AnalyzeTranscriptChange(
        ContentDigest previousTranscriptDigest,
        ContentDigest currentTranscriptDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previousTranscriptDigest);
        ArgumentNullException.ThrowIfNull(currentTranscriptDigest);
        cancellationToken.ThrowIfCancellationRequested();
        if (previousTranscriptDigest == currentTranscriptDigest)
        {
            return new AnalysisInvalidationPlan(
                previousTranscriptDigest,
                currentTranscriptDigest,
                staleNodes: [],
                recomputationNodes: [],
                manualRebindingNodes: []);
        }

        HashSet<AnalysisDependencyNodeKind> affected = GetReachableNodes(
            [
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
            ],
            cancellationToken);
        AnalysisDependencyNodeKind[] stale = _topologicalOrder
            .Where(node => affected.Contains(node) && StaleTrackedNodes.Contains(node))
            .ToArray();
        AnalysisDependencyNodeKind[] recomputation = stale
            .Where(ArtifactContracts.ContainsKey)
            .ToArray();
        AnalysisDependencyNodeKind[] manual = stale
            .Where(ManualBindingNodes.Contains)
            .ToArray();
        return new AnalysisInvalidationPlan(
            previousTranscriptDigest,
            currentTranscriptDigest,
            stale,
            recomputation,
            manual);
    }

    public AnalysisInvalidationPlan AnalyzeTranscriptEdit(
        TranscriptDownstreamStaleRecord transcriptChange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcriptChange);
        return AnalyzeTranscriptChange(
            transcriptChange.SourceRevisionDigest,
            transcriptChange.RevisionDigest,
            cancellationToken);
    }

    public AnalysisArtifactPlan PlanArtifact(
        AnalysisDependencyNodeKind artifact,
        IReadOnlyList<AnalysisDependencySnapshot> currentInputs,
        IReadOnlyList<ArtifactCacheCandidate> existingCandidates,
        ArtifactAdoptionSnapshot? currentAdoption,
        CancellationToken cancellationToken = default)
    {
        AnalysisDependencyContract.RequireDefinedNode(artifact);
        ArgumentNullException.ThrowIfNull(currentInputs);
        ArgumentNullException.ThrowIfNull(existingCandidates);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_nodeSet.Contains(artifact))
        {
            throw Failure(AnalysisDependencyGraphFailureReason.UnknownNode);
        }

        if (!ArtifactContracts.TryGetValue(
                artifact,
                out AnalysisArtifactContract? contract))
        {
            throw Failure(
                AnalysisDependencyGraphFailureReason.UnsupportedArtifact);
        }

        var supplied = new Dictionary<
            AnalysisDependencyNodeKind,
            AnalysisDependencySnapshot>();
        for (int index = 0; index < currentInputs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalysisDependencySnapshot input = currentInputs[index]
                ?? throw Failure(
                    AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot);
            if (!_nodeSet.Contains(input.Node)
                || !supplied.TryAdd(input.Node, input))
            {
                throw Failure(
                    AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot);
            }
        }

        if (supplied.Count != contract.Inputs.Count
            || supplied.Keys.Except(contract.Inputs).Any())
        {
            throw Failure(
                AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot);
        }

        var orderedInputs = new AnalysisDependencySnapshot[contract.Inputs.Count];
        for (int index = 0; index < orderedInputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!supplied.TryGetValue(
                    contract.Inputs[index],
                    out AnalysisDependencySnapshot? input))
            {
                throw Failure(
                    AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot);
            }

            orderedInputs[index] = input;
        }

        ArtifactCacheKey expectedCacheKey = ArtifactCacheKey.Create(
            contract.Operation,
            contract.ConfigurationDigest,
            orderedInputs.Select(static input => input.Digest).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        ArtifactPlan cachePlan = ArtifactPlanner.Plan(
            contract.Operation,
            expectedCacheKey,
            existingCandidates,
            currentAdoption);
        return new AnalysisArtifactPlan(
            artifact,
            contract.ConfigurationDigest,
            orderedInputs,
            cachePlan);
    }

    private static AnalysisDependencyGraph CreateDefault()
    {
        AnalysisDependencyNodeKind[] nodes =
        [
            AnalysisDependencyNodeKind.SourceAssetSnapshot,
            AnalysisDependencyNodeKind.AudioExtraction,
            AnalysisDependencyNodeKind.AutomaticSpeechRecognition,
            AnalysisDependencyNodeKind.TranscriptRevision,
            AnalysisDependencyNodeKind.SourceSpanSnapshot,
            AnalysisDependencyNodeKind.DictionarySnapshot,
            AnalysisDependencyNodeKind.TermScopeSnapshot,
            AnalysisDependencyNodeKind.ProjectSnapshot,
            AnalysisDependencyNodeKind.ProductTermSelectionSnapshot,
            AnalysisDependencyNodeKind.SilenceCandidates,
            AnalysisDependencyNodeKind.AudioReviewCandidates,
            AnalysisDependencyNodeKind.TermCandidates,
            AnalysisDependencyNodeKind.EditorialPhraseCandidates,
            AnalysisDependencyNodeKind.RepetitionCandidates,
            AnalysisDependencyNodeKind.TopicBoundaryCandidates,
            AnalysisDependencyNodeKind.TopicRangeBinding,
            AnalysisDependencyNodeKind.ContextDeviationCandidates,
            AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
            AnalysisDependencyNodeKind.TopicMetadata,
            AnalysisDependencyNodeKind.EmphasisCandidates,
        ];
        AnalysisDependencyEdge[] edges =
        [
            Edge(
                AnalysisDependencyNodeKind.SourceAssetSnapshot,
                AnalysisDependencyNodeKind.AudioExtraction),
            Edge(
                AnalysisDependencyNodeKind.AudioExtraction,
                AnalysisDependencyNodeKind.AutomaticSpeechRecognition),
            Edge(
                AnalysisDependencyNodeKind.AudioExtraction,
                AnalysisDependencyNodeKind.SilenceCandidates),
            Edge(
                AnalysisDependencyNodeKind.AudioExtraction,
                AnalysisDependencyNodeKind.AudioReviewCandidates),
            Edge(
                AnalysisDependencyNodeKind.AutomaticSpeechRecognition,
                AnalysisDependencyNodeKind.TranscriptRevision),
            Edge(
                AnalysisDependencyNodeKind.AutomaticSpeechRecognition,
                AnalysisDependencyNodeKind.SourceSpanSnapshot),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.SourceSpanSnapshot),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.TermCandidates),
            Edge(
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
                AnalysisDependencyNodeKind.TermCandidates),
            Edge(
                AnalysisDependencyNodeKind.DictionarySnapshot,
                AnalysisDependencyNodeKind.TermCandidates),
            Edge(
                AnalysisDependencyNodeKind.TermScopeSnapshot,
                AnalysisDependencyNodeKind.TermCandidates),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.EditorialPhraseCandidates),
            Edge(
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
                AnalysisDependencyNodeKind.EditorialPhraseCandidates),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.RepetitionCandidates),
            Edge(
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
                AnalysisDependencyNodeKind.RepetitionCandidates),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.TopicBoundaryCandidates),
            Edge(
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
                AnalysisDependencyNodeKind.TopicBoundaryCandidates),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.TopicRangeBinding),
            Edge(
                AnalysisDependencyNodeKind.TopicBoundaryCandidates,
                AnalysisDependencyNodeKind.TopicRangeBinding),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.ContextDeviationCandidates),
            Edge(
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
                AnalysisDependencyNodeKind.ContextDeviationCandidates),
            Edge(
                AnalysisDependencyNodeKind.TopicRangeBinding,
                AnalysisDependencyNodeKind.ContextDeviationCandidates),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.SpeakerAssignmentBinding),
            Edge(
                AnalysisDependencyNodeKind.ProjectSnapshot,
                AnalysisDependencyNodeKind.SpeakerAssignmentBinding),
            Edge(
                AnalysisDependencyNodeKind.ProjectSnapshot,
                AnalysisDependencyNodeKind.TopicMetadata),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.TopicMetadata),
            Edge(
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
                AnalysisDependencyNodeKind.TopicMetadata),
            Edge(
                AnalysisDependencyNodeKind.TopicRangeBinding,
                AnalysisDependencyNodeKind.TopicMetadata),
            Edge(
                AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
                AnalysisDependencyNodeKind.TopicMetadata),
            Edge(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.EmphasisCandidates),
            Edge(
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
                AnalysisDependencyNodeKind.EmphasisCandidates),
            Edge(
                AnalysisDependencyNodeKind.TopicMetadata,
                AnalysisDependencyNodeKind.EmphasisCandidates),
            Edge(
                AnalysisDependencyNodeKind.DictionarySnapshot,
                AnalysisDependencyNodeKind.EmphasisCandidates),
            Edge(
                AnalysisDependencyNodeKind.TermScopeSnapshot,
                AnalysisDependencyNodeKind.EmphasisCandidates),
            Edge(
                AnalysisDependencyNodeKind.ProductTermSelectionSnapshot,
                AnalysisDependencyNodeKind.EmphasisCandidates),
        ];
        var graph = new AnalysisDependencyGraph(nodes, edges);
        graph.ValidateArtifactContracts();
        return graph;
    }

    private void ValidateArtifactContracts()
    {
        foreach (AnalysisArtifactContract contract in ArtifactContracts.Values)
        {
            if (!_nodeSet.Contains(contract.Artifact)
                || contract.Inputs.Any(input => !_nodeSet.Contains(input))
                || contract.Inputs.Any(input => !Edges.Any(edge =>
                    edge.From == input && edge.To == contract.Artifact)))
            {
                throw new InvalidOperationException(
                    "The built-in analysis cache contract does not match its graph edges.");
            }
        }
    }

    private HashSet<AnalysisDependencyNodeKind> GetReachableNodes(
        IReadOnlyList<AnalysisDependencyNodeKind> roots,
        CancellationToken cancellationToken)
    {
        var reachable = new HashSet<AnalysisDependencyNodeKind>();
        var pending = new Queue<AnalysisDependencyNodeKind>();
        foreach (AnalysisDependencyNodeKind root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_nodeSet.Contains(root))
            {
                throw Failure(AnalysisDependencyGraphFailureReason.UnknownNode);
            }

            if (reachable.Add(root))
            {
                pending.Enqueue(root);
            }
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalysisDependencyNodeKind current = pending.Dequeue();
            foreach (AnalysisDependencyNodeKind target in _outgoing[current])
            {
                if (reachable.Add(target))
                {
                    pending.Enqueue(target);
                }
            }
        }

        return reachable;
    }

    private static AnalysisDependencyNodeKind[] BuildTopologicalOrder(
        IReadOnlyList<AnalysisDependencyNodeKind> nodes,
        IReadOnlyDictionary<
            AnalysisDependencyNodeKind,
            List<AnalysisDependencyNodeKind>> outgoing,
        Dictionary<AnalysisDependencyNodeKind, int> indegree,
        IReadOnlyDictionary<AnalysisDependencyNodeKind, int> nodeOrder)
    {
        var available = new SortedSet<int>();
        foreach (AnalysisDependencyNodeKind node in nodes)
        {
            if (indegree[node] == 0)
            {
                _ = available.Add(nodeOrder[node]);
            }
        }

        var ordered = new List<AnalysisDependencyNodeKind>(nodes.Count);
        while (available.Count > 0)
        {
            int nextIndex = available.Min;
            _ = available.Remove(nextIndex);
            AnalysisDependencyNodeKind node = nodes[nextIndex];
            ordered.Add(node);
            foreach (AnalysisDependencyNodeKind target in outgoing[node])
            {
                int remaining = checked(indegree[target] - 1);
                indegree[target] = remaining;
                if (remaining == 0)
                {
                    _ = available.Add(nodeOrder[target]);
                }
            }
        }

        if (ordered.Count != nodes.Count)
        {
            throw Failure(AnalysisDependencyGraphFailureReason.Cycle);
        }

        return ordered.ToArray();
    }

    private static IReadOnlyDictionary<
        AnalysisDependencyNodeKind,
        AnalysisArtifactContract> CreateArtifactContracts()
    {
        AnalysisArtifactContract[] contracts =
        [
            Contract(
                AnalysisDependencyNodeKind.TermCandidates,
                "analysis.term-candidates",
                TermCandidateConfigurationVersion,
                [
                    AnalysisDependencyNodeKind.TranscriptRevision,
                    AnalysisDependencyNodeKind.SourceSpanSnapshot,
                    AnalysisDependencyNodeKind.DictionarySnapshot,
                    AnalysisDependencyNodeKind.TermScopeSnapshot,
                ]),
            Contract(
                AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                "analysis.editorial-phrase-candidates",
                EditorialPhraseDetector.DictionaryVersion,
                [
                    AnalysisDependencyNodeKind.TranscriptRevision,
                    AnalysisDependencyNodeKind.SourceSpanSnapshot,
                ]),
            Contract(
                AnalysisDependencyNodeKind.RepetitionCandidates,
                "analysis.repetition-candidates",
                RepetitionSuggestionPayload.CanonicalRuleVersion,
                [
                    AnalysisDependencyNodeKind.TranscriptRevision,
                    AnalysisDependencyNodeKind.SourceSpanSnapshot,
                ]),
            Contract(
                AnalysisDependencyNodeKind.TopicBoundaryCandidates,
                "analysis.topic-boundary-candidates",
                TopicBoundaryService.RuleVersion,
                [
                    AnalysisDependencyNodeKind.TranscriptRevision,
                    AnalysisDependencyNodeKind.SourceSpanSnapshot,
                ]),
            Contract(
                AnalysisDependencyNodeKind.ContextDeviationCandidates,
                "analysis.context-deviation-candidates",
                ContextDeviationDetector.RuleVersion,
                [
                    AnalysisDependencyNodeKind.TranscriptRevision,
                    AnalysisDependencyNodeKind.SourceSpanSnapshot,
                    AnalysisDependencyNodeKind.TopicRangeBinding,
                ]),
            Contract(
                AnalysisDependencyNodeKind.TopicMetadata,
                "analysis.topic-metadata",
                TopicMetadataConfigurationVersion,
                [
                    AnalysisDependencyNodeKind.ProjectSnapshot,
                    AnalysisDependencyNodeKind.TranscriptRevision,
                    AnalysisDependencyNodeKind.SourceSpanSnapshot,
                    AnalysisDependencyNodeKind.TopicRangeBinding,
                    AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
                ]),
            Contract(
                AnalysisDependencyNodeKind.EmphasisCandidates,
                "analysis.emphasis-candidates",
                EmphasisCandidateService.RuleVersion,
                [
                    AnalysisDependencyNodeKind.TranscriptRevision,
                    AnalysisDependencyNodeKind.SourceSpanSnapshot,
                    AnalysisDependencyNodeKind.TopicMetadata,
                    AnalysisDependencyNodeKind.DictionarySnapshot,
                    AnalysisDependencyNodeKind.TermScopeSnapshot,
                    AnalysisDependencyNodeKind.ProductTermSelectionSnapshot,
                ]),
        ];
        return contracts.ToDictionary(static contract => contract.Artifact);
    }

    private static AnalysisArtifactContract Contract(
        AnalysisDependencyNodeKind artifact,
        string operation,
        string configurationVersion,
        IReadOnlyList<AnalysisDependencyNodeKind> inputs) =>
        new(
            artifact,
            operation,
            ComputeConfigurationDigest(operation, configurationVersion),
            Array.AsReadOnly(inputs.ToArray()));

    private static ContentDigest ComputeConfigurationDigest(
        string operation,
        string configurationVersion)
    {
        string canonical = string.Concat(
            ConfigurationDomain,
            "\n",
            operation,
            "\n",
            configurationVersion,
            "\n");
        return ContentDigest.Compute(StrictUtf8.GetBytes(canonical));
    }

    private static AnalysisDependencyEdge Edge(
        AnalysisDependencyNodeKind from,
        AnalysisDependencyNodeKind to) =>
        new(from, to);

    private static AnalysisDependencyGraphException Failure(
        AnalysisDependencyGraphFailureReason reason) =>
        new(reason);

    private sealed record AnalysisArtifactContract(
        AnalysisDependencyNodeKind Artifact,
        string Operation,
        ContentDigest ConfigurationDigest,
        IReadOnlyList<AnalysisDependencyNodeKind> Inputs);
}

internal static class AnalysisDependencyContract
{
    internal static void RequireDefinedNode(AnalysisDependencyNodeKind node)
    {
        if (!Enum.IsDefined(node))
        {
            throw new AnalysisDependencyGraphException(
                AnalysisDependencyGraphFailureReason.InvalidNode);
        }
    }
}
