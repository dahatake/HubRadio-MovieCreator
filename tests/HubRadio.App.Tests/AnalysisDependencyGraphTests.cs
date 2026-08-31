using System.Reflection;
using System.Text;
using HubRadio.App.Analysis;
using HubRadio.App.Jobs;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AnalysisDependencyGraphTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";

    private static readonly DateTimeOffset CreatedAt =
        new(2044, 7, 8, 9, 10, 11, TimeSpan.Zero);

    [TestMethod]
    public void DefaultGraphUsesExplicitEdgesAndRejectsUnknownOrCyclicDefinitions()
    {
        AnalysisDependencyGraph graph = AnalysisDependencyGraph.Default;

        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.From == AnalysisDependencyNodeKind.TranscriptRevision
            && edge.To == AnalysisDependencyNodeKind.TermCandidates));
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.From == AnalysisDependencyNodeKind.TopicRangeBinding
            && edge.To == AnalysisDependencyNodeKind.ContextDeviationCandidates));
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.From == AnalysisDependencyNodeKind.SpeakerAssignmentBinding
            && edge.To == AnalysisDependencyNodeKind.TopicMetadata));
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.From == AnalysisDependencyNodeKind.TopicMetadata
            && edge.To == AnalysisDependencyNodeKind.EmphasisCandidates));
        Assert.IsFalse(graph.Edges.Any(edge =>
            edge.From == AnalysisDependencyNodeKind.TranscriptRevision
            && edge.To is AnalysisDependencyNodeKind.AudioExtraction
                or AnalysisDependencyNodeKind.AutomaticSpeechRecognition
                or AnalysisDependencyNodeKind.SilenceCandidates
                or AnalysisDependencyNodeKind.AudioReviewCandidates));

        AnalysisDependencyGraphException unknown =
            Assert.ThrowsExactly<AnalysisDependencyGraphException>(() =>
                new AnalysisDependencyGraph(
                    [AnalysisDependencyNodeKind.TranscriptRevision],
                    [new AnalysisDependencyEdge(
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.TermCandidates)]));
        Assert.AreEqual(
            AnalysisDependencyGraphFailureReason.UnknownNode,
            unknown.Reason);

        AnalysisDependencyGraphException cycle =
            Assert.ThrowsExactly<AnalysisDependencyGraphException>(() =>
                new AnalysisDependencyGraph(
                    [
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.TermCandidates,
                    ],
                    [
                        new AnalysisDependencyEdge(
                            AnalysisDependencyNodeKind.TranscriptRevision,
                            AnalysisDependencyNodeKind.TermCandidates),
                        new AnalysisDependencyEdge(
                            AnalysisDependencyNodeKind.TermCandidates,
                            AnalysisDependencyNodeKind.TranscriptRevision),
                    ]));
        Assert.AreEqual(AnalysisDependencyGraphFailureReason.Cycle, cycle.Reason);
    }

    [TestMethod]
    public void SameTranscriptDigestProducesNoInvalidation()
    {
        ContentDigest digest = Digest('a');

        AnalysisInvalidationPlan plan = AnalysisDependencyGraph.Default
            .AnalyzeTranscriptChange(digest, digest);

        Assert.IsFalse(plan.HasTranscriptChange);
        Assert.AreEqual(digest, plan.PreviousTranscriptDigest);
        Assert.AreEqual(digest, plan.CurrentTranscriptDigest);
        Assert.HasCount(0, plan.StaleNodes);
        Assert.HasCount(0, plan.RecomputationNodes);
        Assert.HasCount(0, plan.ManualRebindingNodes);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AnalysisDependencyNodeKind>)plan.StaleNodes).Clear());
    }

    [TestMethod]
    public void PartialTokenEditInvalidatesOnlyTranscriptBoundArtifacts()
    {
        TranscriptFixture fixture = new();
        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.TokenId, "旧表記", "新表記")],
            Id<TranscriptRevisionIdTag>(
                "51000000-0000-4000-8000-000000000002"),
            ActorSid,
            CreatedAt.AddMinutes(1),
            "一つのtokenだけを修正");

        AnalysisInvalidationPlan plan = AnalysisDependencyGraph.Default
            .AnalyzeTranscriptEdit(edit.DownstreamStale);

        Assert.IsTrue(plan.HasTranscriptChange);
        Assert.AreEqual(
            edit.DownstreamStale.SourceRevisionDigest,
            plan.PreviousTranscriptDigest);
        Assert.AreEqual(edit.RevisionDigest, plan.CurrentTranscriptDigest);
        CollectionAssert.AreEqual(
            new[]
            {
                AnalysisDependencyNodeKind.TermCandidates,
                AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                AnalysisDependencyNodeKind.RepetitionCandidates,
                AnalysisDependencyNodeKind.TopicBoundaryCandidates,
                AnalysisDependencyNodeKind.TopicRangeBinding,
                AnalysisDependencyNodeKind.ContextDeviationCandidates,
                AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
                AnalysisDependencyNodeKind.TopicMetadata,
                AnalysisDependencyNodeKind.EmphasisCandidates,
            },
            plan.StaleNodes.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                AnalysisDependencyNodeKind.TermCandidates,
                AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                AnalysisDependencyNodeKind.RepetitionCandidates,
                AnalysisDependencyNodeKind.TopicBoundaryCandidates,
                AnalysisDependencyNodeKind.ContextDeviationCandidates,
                AnalysisDependencyNodeKind.TopicMetadata,
                AnalysisDependencyNodeKind.EmphasisCandidates,
            },
            plan.RecomputationNodes.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                AnalysisDependencyNodeKind.TopicRangeBinding,
                AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
            },
            plan.ManualRebindingNodes.ToArray());
        foreach (AnalysisDependencyNodeKind unaffected in new[]
        {
            AnalysisDependencyNodeKind.AudioExtraction,
            AnalysisDependencyNodeKind.AutomaticSpeechRecognition,
            AnalysisDependencyNodeKind.SilenceCandidates,
            AnalysisDependencyNodeKind.AudioReviewCandidates,
        })
        {
            Assert.IsFalse(plan.StaleNodes.Contains(unaffected), unaffected.ToString());
        }

        Assert.AreEqual("旧表記", fixture.Revision.Utterances.Single().Text);
        Assert.AreEqual("新表記", edit.Revision.Utterances.Single().Text);
        Assert.AreEqual(
            fixture.SourceSpans.Single().SourceDigest,
            edit.RevisedSourceSpans.Single().SourceDigest);
    }

    [TestMethod]
    public void ArtifactContractsUseExactProducerInputsInCanonicalOrder()
    {
        AnalysisDependencyGraph graph = AnalysisDependencyGraph.Default;
        (AnalysisDependencyNodeKind Artifact, AnalysisDependencyNodeKind[] Inputs)[]
            contracts =
            [
                (
                    AnalysisDependencyNodeKind.TermCandidates,
                    [
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                        AnalysisDependencyNodeKind.DictionarySnapshot,
                        AnalysisDependencyNodeKind.TermScopeSnapshot,
                    ]),
                (
                    AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                    [
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                    ]),
                (
                    AnalysisDependencyNodeKind.RepetitionCandidates,
                    [
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                    ]),
                (
                    AnalysisDependencyNodeKind.TopicBoundaryCandidates,
                    [
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                    ]),
                (
                    AnalysisDependencyNodeKind.ContextDeviationCandidates,
                    [
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                        AnalysisDependencyNodeKind.TopicRangeBinding,
                    ]),
                (
                    AnalysisDependencyNodeKind.TopicMetadata,
                    [
                        AnalysisDependencyNodeKind.ProjectSnapshot,
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                        AnalysisDependencyNodeKind.TopicRangeBinding,
                        AnalysisDependencyNodeKind.SpeakerAssignmentBinding,
                    ]),
                (
                    AnalysisDependencyNodeKind.EmphasisCandidates,
                    [
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                        AnalysisDependencyNodeKind.TopicMetadata,
                        AnalysisDependencyNodeKind.DictionarySnapshot,
                        AnalysisDependencyNodeKind.TermScopeSnapshot,
                        AnalysisDependencyNodeKind.ProductTermSelectionSnapshot,
                    ]),
            ];
        var operations = new HashSet<string>(StringComparer.Ordinal);

        foreach ((AnalysisDependencyNodeKind artifact,
            AnalysisDependencyNodeKind[] expectedInputs) in contracts)
        {
            AnalysisDependencySnapshot[] supplied = expectedInputs
                .Reverse()
                .Select(node => new AnalysisDependencySnapshot(
                    node,
                    DigestFor(node)))
                .ToArray();

            AnalysisArtifactPlan plan = graph.PlanArtifact(
                artifact,
                supplied,
                existingCandidates: [],
                currentAdoption: null);

            Assert.AreEqual(artifact, plan.Artifact);
            Assert.IsTrue(operations.Add(plan.Operation), plan.Operation);
            CollectionAssert.AreEqual(
                expectedInputs,
                plan.Inputs.Select(static input => input.Node).ToArray(),
                artifact.ToString());
            CollectionAssert.AreEqual(
                expectedInputs.Select(DigestFor).ToArray(),
                plan.Inputs.Select(static input => input.Digest).ToArray(),
                artifact.ToString());
            ArtifactCacheKey expectedKey = ArtifactCacheKey.Create(
                plan.Operation,
                plan.ConfigurationDigest,
                expectedInputs.Select(DigestFor).ToArray());
            Assert.AreEqual(expectedKey, plan.CachePlan.ExpectedCacheKey);
            Assert.AreEqual(
                ArtifactPlanDisposition.RecomputeRequired,
                plan.CachePlan.Disposition);
            Assert.HasCount(0, plan.CachePlan.StaleArtifacts);
        }

        AnalysisArtifactPlan term = graph.PlanArtifact(
            AnalysisDependencyNodeKind.TermCandidates,
            CreateInputs(contracts[0].Inputs),
            existingCandidates: [],
            currentAdoption: null);
        ArtifactCacheKey reversed = ArtifactCacheKey.Create(
            term.Operation,
            term.ConfigurationDigest,
            term.Inputs.Reverse().Select(static input => input.Digest).ToArray());
        Assert.AreNotEqual(term.CachePlan.ExpectedCacheKey, reversed);
    }

    [TestMethod]
    public void ChangedTranscriptKeepsAdoptedArtifactAsStaleWithoutDeletingOrReplacingIt()
    {
        AnalysisDependencyGraph graph = AnalysisDependencyGraph.Default;
        AnalysisDependencyNodeKind[] dependencyOrder =
        [
            AnalysisDependencyNodeKind.TranscriptRevision,
            AnalysisDependencyNodeKind.SourceSpanSnapshot,
            AnalysisDependencyNodeKind.DictionarySnapshot,
            AnalysisDependencyNodeKind.TermScopeSnapshot,
        ];
        AnalysisDependencySnapshot[] oldInputs = CreateInputs(dependencyOrder);
        AnalysisArtifactPlan oldPlan = graph.PlanArtifact(
            AnalysisDependencyNodeKind.TermCandidates,
            oldInputs,
            existingCandidates: [],
            currentAdoption: null);
        var previous = new ArtifactCacheCandidate(
            Id<ArtifactIdTag>("52000000-0000-4000-8000-000000000001"),
            oldPlan.Operation,
            oldPlan.CachePlan.ExpectedCacheKey,
            Digest('e'),
            CreatedAt);
        var adoption = new ArtifactAdoptionSnapshot(
            previous.ArtifactId,
            previous.OutputDigest);
        AnalysisDependencySnapshot[] currentInputs =
        [
            new(AnalysisDependencyNodeKind.TermScopeSnapshot, DigestFor(
                AnalysisDependencyNodeKind.TermScopeSnapshot)),
            new(AnalysisDependencyNodeKind.DictionarySnapshot, DigestFor(
                AnalysisDependencyNodeKind.DictionarySnapshot)),
            new(AnalysisDependencyNodeKind.SourceSpanSnapshot, Digest('f')),
            new(AnalysisDependencyNodeKind.TranscriptRevision, Digest('d')),
        ];

        AnalysisArtifactPlan currentPlan = graph.PlanArtifact(
            AnalysisDependencyNodeKind.TermCandidates,
            currentInputs,
            [previous],
            adoption);

        Assert.AreNotEqual(
            oldPlan.CachePlan.ExpectedCacheKey,
            currentPlan.CachePlan.ExpectedCacheKey);
        Assert.AreEqual(
            ArtifactPlanDisposition.RecomputeRequired,
            currentPlan.CachePlan.Disposition);
        Assert.AreSame(previous, currentPlan.CachePlan.StaleArtifacts.Single());
        Assert.AreSame(adoption, currentPlan.CachePlan.CurrentAdoption);
        Assert.IsNull(currentPlan.CachePlan.CachedArtifact);
        Assert.IsNull(currentPlan.CachePlan.ProposedAdoptionDifference);
        Assert.IsFalse(currentPlan.CachePlan.RequiresAdoptionReview);
        Assert.AreEqual(oldPlan.Operation, previous.Operation);
        Assert.AreEqual(oldPlan.CachePlan.ExpectedCacheKey, previous.CacheKey);
        Assert.AreEqual(Digest('e'), previous.OutputDigest);
    }

    [TestMethod]
    public void PlanningIsDeterministicDefensiveAndDoesNotReorderCallerInputs()
    {
        AnalysisDependencyGraph graph = AnalysisDependencyGraph.Default;
        var inputs = new List<AnalysisDependencySnapshot>
        {
            new(AnalysisDependencyNodeKind.TermScopeSnapshot, Digest('4')),
            new(AnalysisDependencyNodeKind.TranscriptRevision, Digest('1')),
            new(AnalysisDependencyNodeKind.DictionarySnapshot, Digest('3')),
            new(AnalysisDependencyNodeKind.SourceSpanSnapshot, Digest('2')),
        };
        AnalysisDependencySnapshot[] callerOrder = inputs.ToArray();

        AnalysisArtifactPlan first = graph.PlanArtifact(
            AnalysisDependencyNodeKind.TermCandidates,
            inputs,
            existingCandidates: [],
            currentAdoption: null);
        AnalysisArtifactPlan second = graph.PlanArtifact(
            AnalysisDependencyNodeKind.TermCandidates,
            inputs,
            existingCandidates: [],
            currentAdoption: null);
        CollectionAssert.AreEqual(callerOrder, inputs.ToArray());
        inputs.Clear();

        Assert.AreEqual(
            first.CachePlan.ExpectedCacheKey,
            second.CachePlan.ExpectedCacheKey);
        CollectionAssert.AreEqual(
            first.Inputs.Select(static input => input.Node).ToArray(),
            second.Inputs.Select(static input => input.Node).ToArray());
        Assert.HasCount(4, first.Inputs);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AnalysisDependencySnapshot>)first.Inputs).Clear());

        var nodes = new List<AnalysisDependencyNodeKind>
        {
            AnalysisDependencyNodeKind.TranscriptRevision,
            AnalysisDependencyNodeKind.TermCandidates,
        };
        var edges = new List<AnalysisDependencyEdge>
        {
            new(
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.TermCandidates),
        };
        var custom = new AnalysisDependencyGraph(nodes, edges);
        nodes.Clear();
        edges.Clear();
        Assert.HasCount(2, custom.Nodes);
        Assert.HasCount(1, custom.Edges);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AnalysisDependencyNodeKind>)custom.Nodes).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AnalysisDependencyEdge>)custom.Edges).Clear());
    }

    [TestMethod]
    public void InvalidArtifactInputsAndUnsupportedNodesFailClosed()
    {
        AnalysisDependencyGraph graph = AnalysisDependencyGraph.Default;
        AnalysisDependencyNodeKind[] required =
        [
            AnalysisDependencyNodeKind.TranscriptRevision,
            AnalysisDependencyNodeKind.SourceSpanSnapshot,
        ];

        AssertFailure(
            () => graph.PlanArtifact(
                (AnalysisDependencyNodeKind)999,
                [],
                [],
                currentAdoption: null),
            AnalysisDependencyGraphFailureReason.InvalidNode);
        AssertFailure(
            () => graph.PlanArtifact(
                AnalysisDependencyNodeKind.AudioExtraction,
                [],
                [],
                currentAdoption: null),
            AnalysisDependencyGraphFailureReason.UnsupportedArtifact);
        AssertFailure(
            () => graph.PlanArtifact(
                AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                CreateInputs([AnalysisDependencyNodeKind.TranscriptRevision]),
                [],
                currentAdoption: null),
            AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot);
        AssertFailure(
            () => graph.PlanArtifact(
                AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                [
                    .. CreateInputs(required),
                    new AnalysisDependencySnapshot(
                        AnalysisDependencyNodeKind.DictionarySnapshot,
                        Digest('f')),
                ],
                [],
                currentAdoption: null),
            AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot);
        AssertFailure(
            () => graph.PlanArtifact(
                AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                [
                    new AnalysisDependencySnapshot(
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        Digest('1')),
                    new AnalysisDependencySnapshot(
                        AnalysisDependencyNodeKind.TranscriptRevision,
                        Digest('2')),
                    new AnalysisDependencySnapshot(
                        AnalysisDependencyNodeKind.SourceSpanSnapshot,
                        Digest('3')),
                ],
                [],
                currentAdoption: null),
            AnalysisDependencyGraphFailureReason.InvalidDependencySnapshot);
        AssertFailure(
            () => _ = new AnalysisDependencySnapshot(
                (AnalysisDependencyNodeKind)999,
                Digest('1')),
            AnalysisDependencyGraphFailureReason.InvalidNode);
    }

    [TestMethod]
    public void PreCancelledInvalidationAndPlanningHaveNoSideEffects()
    {
        AnalysisDependencyGraph graph = AnalysisDependencyGraph.Default;
        var inputs = CreateInputs(
            [
                AnalysisDependencyNodeKind.TranscriptRevision,
                AnalysisDependencyNodeKind.SourceSpanSnapshot,
            ]).ToList();
        AnalysisDependencySnapshot[] before = inputs.ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            graph.AnalyzeTranscriptChange(
                Digest('1'),
                Digest('2'),
                cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            graph.PlanArtifact(
                AnalysisDependencyNodeKind.EditorialPhraseCandidates,
                inputs,
                [],
                currentAdoption: null,
                cancellation.Token));
        CollectionAssert.AreEqual(before, inputs.ToArray());
    }

    [TestMethod]
    public void PublicSurfaceIsImmutableStoreFreeAndHasNoDeletionApi()
    {
        foreach (Type type in new[]
        {
            typeof(AnalysisDependencyEdge),
            typeof(AnalysisDependencySnapshot),
            typeof(AnalysisInvalidationPlan),
            typeof(AnalysisArtifactPlan),
            typeof(AnalysisDependencyGraph),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
            Assert.IsFalse(type.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Any(static method =>
                    method.Name.Contains("Delete", StringComparison.Ordinal)
                    || method.Name.Contains("Remove", StringComparison.Ordinal)),
                type.Name);
        }

        Assert.AreEqual(0, typeof(AnalysisDependencyGraph).GetConstructors().Length);
        Assert.AreEqual(0, typeof(AnalysisInvalidationPlan).GetConstructors().Length);
        Assert.AreEqual(0, typeof(AnalysisArtifactPlan).GetConstructors().Length);
        Assert.IsFalse(typeof(AnalysisDependencyGraph).GetMethods(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly)
            .SelectMany(static method => method.GetParameters())
            .Any(static parameter =>
                parameter.ParameterType.Name.Contains("Store", StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains(
                    "SuggestionDecision",
                    StringComparison.Ordinal)));
    }

    private static AnalysisDependencySnapshot[] CreateInputs(
        IReadOnlyList<AnalysisDependencyNodeKind> nodes) =>
        nodes.Select(node => new AnalysisDependencySnapshot(
                node,
                DigestFor(node)))
            .ToArray();

    private static ContentDigest DigestFor(AnalysisDependencyNodeKind node) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(node.ToString()));

    private static ContentDigest Digest(char value) =>
        new(new string(value, 64));

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private static AnalysisDependencyGraphException AssertFailure(
        Action action,
        AnalysisDependencyGraphFailureReason reason)
    {
        AnalysisDependencyGraphException exception =
            Assert.ThrowsExactly<AnalysisDependencyGraphException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private sealed class TranscriptFixture
    {
        internal TranscriptFixture()
        {
            TokenId = Id<TranscriptTokenIdTag>(
                "51000000-0000-4000-8000-000000000003");
            EntityId<SourceSpanIdTag> sourceSpanId = Id<SourceSpanIdTag>(
                "51000000-0000-4000-8000-000000000004");
            EntityId<TranscriptRevisionIdTag> revisionId =
                Id<TranscriptRevisionIdTag>(
                    "51000000-0000-4000-8000-000000000001");
            var range = new TimeRange(default, new RationalTime(1, 1));
            var token = new Token(TokenId, "旧表記", range, 0.9);
            var sentence = new Sentence(
                Id<TranscriptSentenceIdTag>(
                    "51000000-0000-4000-8000-000000000005"),
                "旧表記",
                [token]);
            var utterance = new Utterance(
                Id<TranscriptUtteranceIdTag>(
                    "51000000-0000-4000-8000-000000000006"),
                sourceSpanId,
                Id<TranscriptSpeakerIdTag>(
                    "51000000-0000-4000-8000-000000000007"),
                "Speaker 1",
                "旧表記",
                range,
                0.9,
                [sentence]);
            Revision = new TranscriptRevision(
                revisionId,
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                CreatedAt,
                [utterance]);
            SourceSpans =
            [
                new SourceSpan(
                    sourceSpanId,
                    Id<AssetIdTag>(
                        "51000000-0000-4000-8000-000000000008"),
                    "audio:1",
                    range,
                    Digest('9'),
                    revisionId,
                    [TokenId]),
            ];
        }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal EntityId<TranscriptTokenIdTag> TokenId { get; }
    }
}
