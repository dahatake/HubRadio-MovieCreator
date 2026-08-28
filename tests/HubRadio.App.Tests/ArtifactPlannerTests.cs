using System.Reflection;
using HubRadio.App.Jobs;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ArtifactPlannerTests
{
    private const string Operation = "analysis.topic";

    private static readonly DateTimeOffset CreatedAt =
        new(2042, 11, 12, 13, 14, 15, TimeSpan.Zero);

    [TestMethod]
    public void NoCandidatesRequiresRecomputeAndPreservesExternalAdoption()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1', '2', '3');
        var currentAdoption = new ArtifactAdoptionSnapshot(
            ArtifactId("11111111-1111-4111-8111-111111111111"),
            Digest('9'));

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            [],
            currentAdoption);

        Assert.AreEqual(Operation, plan.Operation);
        Assert.AreSame(expectedCacheKey, plan.ExpectedCacheKey);
        Assert.AreEqual(ArtifactPlanDisposition.RecomputeRequired, plan.Disposition);
        Assert.IsNull(plan.CachedArtifact);
        Assert.AreEqual(0, plan.StaleArtifacts.Count);
        Assert.AreSame(currentAdoption, plan.CurrentAdoption);
        Assert.IsNull(plan.ProposedAdoptionDifference);
        Assert.IsFalse(plan.RequiresAdoptionReview);
    }

    [TestMethod]
    public void ExactCacheHitReturnsCandidateAndProposesFirstContentAdoption()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1', '2', '3');
        ArtifactCacheCandidate stale = Candidate(
            "22222222-2222-4222-8222-222222222222",
            Key('b', '1', '2', '3'),
            Digest('8'),
            CreatedAt.AddMinutes(-1));
        ArtifactCacheCandidate hit = Candidate(
            "33333333-3333-4333-8333-333333333333",
            expectedCacheKey,
            Digest('7'),
            CreatedAt);

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            [stale, hit],
            currentAdoption: null);

        Assert.AreEqual(ArtifactPlanDisposition.CacheHit, plan.Disposition);
        Assert.AreSame(hit, plan.CachedArtifact);
        Assert.AreEqual(1, plan.StaleArtifacts.Count);
        Assert.AreSame(stale, plan.StaleArtifacts[0]);
        Assert.IsNull(plan.CurrentAdoption);
        ArtifactAdoptionDifference difference = plan.ProposedAdoptionDifference
            ?? throw new AssertFailedException("The first adoption difference was not proposed.");
        Assert.IsNull(difference.PreviousArtifactId);
        Assert.IsNull(difference.PreviousDigest);
        Assert.AreEqual(hit.ArtifactId, difference.CandidateArtifactId);
        Assert.AreEqual(hit.OutputDigest, difference.CandidateDigest);
        Assert.IsTrue(difference.HasContentChange);
        Assert.IsTrue(plan.RequiresAdoptionReview);
    }

    [TestMethod]
    public void ChangedConfigurationSourceTranscriptOrDictionaryRequiresRecompute()
    {
        ArtifactCacheKey previousKey = Key('a', '1', '2', '3');
        ArtifactCacheCandidate previous = Candidate(
            "44444444-4444-4444-8444-444444444444",
            previousKey,
            Digest('6'),
            CreatedAt);
        var currentAdoption = new ArtifactAdoptionSnapshot(
            previous.ArtifactId,
            previous.OutputDigest);
        (string Name, ArtifactCacheKey Key)[] changes =
        [
            ("configuration", Key('b', '1', '2', '3')),
            ("source input", Key('a', '4', '2', '3')),
            ("transcript revision", Key('a', '1', '4', '3')),
            ("dictionary", Key('a', '1', '2', '4')),
        ];

        foreach ((string name, ArtifactCacheKey expectedCacheKey) in changes)
        {
            ArtifactPlan plan = ArtifactPlanner.Plan(
                Operation,
                expectedCacheKey,
                [previous],
                currentAdoption);

            Assert.AreEqual(
                ArtifactPlanDisposition.RecomputeRequired,
                plan.Disposition,
                name);
            Assert.IsNull(plan.CachedArtifact, name);
            Assert.AreEqual(1, plan.StaleArtifacts.Count, name);
            Assert.AreSame(previous, plan.StaleArtifacts[0], name);
            Assert.AreSame(currentAdoption, plan.CurrentAdoption, name);
            Assert.IsNull(plan.ProposedAdoptionDifference, name);
            Assert.IsFalse(plan.RequiresAdoptionReview, name);
        }
    }

    [TestMethod]
    public void InputDigestOrderAffectsPlanning()
    {
        ArtifactCacheKey orderedKey = Key('a', '1', '2', '3');
        ArtifactCacheKey reorderedKey = Key('a', '2', '1', '3');
        ArtifactCacheCandidate previous = Candidate(
            "55555555-5555-4555-8555-555555555555",
            orderedKey,
            Digest('5'),
            CreatedAt);

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            reorderedKey,
            [previous],
            currentAdoption: null);

        Assert.AreNotEqual(orderedKey, reorderedKey);
        Assert.AreEqual(ArtifactPlanDisposition.RecomputeRequired, plan.Disposition);
        Assert.IsNull(plan.CachedArtifact);
        Assert.AreSame(previous, plan.StaleArtifacts.Single());
    }

    [TestMethod]
    public void StaleArtifactsAreOrderedByCreatedAtThenArtifactId()
    {
        ArtifactCacheKey staleKey = Key('a', '1');
        ArtifactCacheKey expectedCacheKey = Key('b', '1');
        ArtifactCacheCandidate latest = Candidate(
            "44444444-4444-4444-8444-444444444444",
            staleKey,
            Digest('1'),
            CreatedAt.AddMinutes(1));
        ArtifactCacheCandidate sameTimeHigherId = Candidate(
            "33333333-3333-4333-8333-333333333333",
            staleKey,
            Digest('2'),
            CreatedAt);
        ArtifactCacheCandidate earliest = Candidate(
            "99999999-9999-4999-8999-999999999999",
            staleKey,
            Digest('3'),
            CreatedAt.AddMinutes(-1));
        ArtifactCacheCandidate sameTimeLowerId = Candidate(
            "11111111-1111-4111-8111-111111111111",
            staleKey,
            Digest('4'),
            CreatedAt);

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            [latest, sameTimeHigherId, earliest, sameTimeLowerId],
            currentAdoption: null);

        CollectionAssert.AreEqual(
            new[] { earliest, sameTimeLowerId, sameTimeHigherId, latest },
            plan.StaleArtifacts.ToArray());
    }

    [TestMethod]
    public void CandidateSnapshotAndPlanRejectInvalidNullOrTextInputs()
    {
        EntityId<ArtifactIdTag> artifactId =
            ArtifactId("66666666-6666-4666-8666-666666666666");
        ArtifactCacheKey key = Key('a', '1');
        ContentDigest digest = Digest('1');

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ArtifactCacheCandidate(null!, Operation, key, digest, CreatedAt));
        AssertParamName<ArgumentNullException>(
            "operation",
            () => new ArtifactCacheCandidate(
                artifactId,
                null!,
                key,
                digest,
                CreatedAt));
        AssertOperationRejected(string.Empty, key, artifactId, digest);
        AssertOperationRejected("   ", key, artifactId, digest);
        AssertOperationRejected(" leading", key, artifactId, digest);
        AssertOperationRejected("trailing ", key, artifactId, digest);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ArtifactCacheCandidate(artifactId, Operation, null!, digest, CreatedAt));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ArtifactCacheCandidate(artifactId, Operation, key, null!, CreatedAt));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ArtifactAdoptionSnapshot(null!, digest));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ArtifactAdoptionSnapshot(artifactId, null!));

        AssertParamName<ArgumentNullException>(
            "operation",
            () => ArtifactPlanner.Plan(
                null!,
                key,
                [],
                currentAdoption: null));
        AssertPlanOperationRejected(string.Empty, key);
        AssertPlanOperationRejected("   ", key);
        AssertPlanOperationRejected(" leading", key);
        AssertPlanOperationRejected("trailing ", key);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            ArtifactPlanner.Plan(Operation, null!, [], currentAdoption: null));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            ArtifactPlanner.Plan(Operation, key, null!, currentAdoption: null));
    }

    [TestMethod]
    public void NullDuplicateAndOperationMismatchedCandidatesAreRejected()
    {
        ArtifactCacheKey key = Key('a', '1');
        EntityId<ArtifactIdTag> duplicateId =
            ArtifactId("77777777-7777-4777-8777-777777777777");
        ArtifactCacheCandidate first = Candidate(
            duplicateId,
            key,
            Digest('1'),
            CreatedAt);
        ArtifactCacheCandidate duplicate = Candidate(
            duplicateId,
            Key('b', '1'),
            Digest('2'),
            CreatedAt.AddSeconds(1));
        var wrongCase = new ArtifactCacheCandidate(
            ArtifactId("88888888-8888-4888-8888-888888888888"),
            "Analysis.Topic",
            key,
            Digest('3'),
            CreatedAt);

        AssertParamName<ArgumentException>(
            "existingCandidates",
            () => ArtifactPlanner.Plan(
                Operation,
                key,
                [null!],
                currentAdoption: null));
        AssertParamName<ArgumentException>(
            "existingCandidates",
            () => ArtifactPlanner.Plan(
                Operation,
                key,
                [first, duplicate],
                currentAdoption: null));
        AssertParamName<ArgumentException>(
            "existingCandidates",
            () => ArtifactPlanner.Plan(
                Operation,
                key,
                [wrongCase],
                currentAdoption: null));
    }

    [TestMethod]
    public void MultipleCacheHitsAreAmbiguousEvenWhenContentMatches()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1');
        ContentDigest sharedOutput = Digest('1');
        ArtifactCacheCandidate first = Candidate(
            "99999999-9999-4999-8999-999999999999",
            expectedCacheKey,
            sharedOutput,
            CreatedAt);
        ArtifactCacheCandidate second = Candidate(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            expectedCacheKey,
            sharedOutput,
            CreatedAt.AddSeconds(1));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ArtifactPlanner.Plan(
                Operation,
                expectedCacheKey,
                [first, second],
                currentAdoption: null));
    }

    [TestMethod]
    public void CurrentAdoptionMatchingHitNeedsNoDifferenceOrReview()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1');
        ArtifactCacheCandidate hit = Candidate(
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            expectedCacheKey,
            Digest('1'),
            CreatedAt);
        var currentAdoption = new ArtifactAdoptionSnapshot(
            hit.ArtifactId,
            hit.OutputDigest);

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            [hit],
            currentAdoption);

        Assert.AreSame(hit, plan.CachedArtifact);
        Assert.AreSame(currentAdoption, plan.CurrentAdoption);
        Assert.IsNull(plan.ProposedAdoptionDifference);
        Assert.IsFalse(plan.RequiresAdoptionReview);
    }

    [TestMethod]
    public void DifferentAdoptedContentProducesReviewableDifference()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1');
        ArtifactCacheCandidate hit = Candidate(
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            expectedCacheKey,
            Digest('1'),
            CreatedAt);
        var currentAdoption = new ArtifactAdoptionSnapshot(
            ArtifactId("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            Digest('2'));

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            [hit],
            currentAdoption);

        ArtifactAdoptionDifference difference = plan.ProposedAdoptionDifference
            ?? throw new AssertFailedException("The content difference was not proposed.");
        Assert.AreEqual(currentAdoption.ArtifactId, difference.PreviousArtifactId);
        Assert.AreEqual(currentAdoption.OutputDigest, difference.PreviousDigest);
        Assert.AreEqual(hit.ArtifactId, difference.CandidateArtifactId);
        Assert.AreEqual(hit.OutputDigest, difference.CandidateDigest);
        Assert.IsTrue(difference.HasContentChange);
        Assert.IsTrue(plan.RequiresAdoptionReview);
    }

    [TestMethod]
    public void DifferentIdentityWithSameContentProducesNonReviewDifference()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1');
        ContentDigest outputDigest = Digest('1');
        ArtifactCacheCandidate hit = Candidate(
            "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
            expectedCacheKey,
            outputDigest,
            CreatedAt);
        var currentAdoption = new ArtifactAdoptionSnapshot(
            ArtifactId("ffffffff-ffff-4fff-8fff-ffffffffffff"),
            outputDigest);

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            [hit],
            currentAdoption);

        ArtifactAdoptionDifference difference = plan.ProposedAdoptionDifference
            ?? throw new AssertFailedException("The identity difference was not proposed.");
        Assert.AreEqual(currentAdoption.ArtifactId, difference.PreviousArtifactId);
        Assert.AreEqual(outputDigest, difference.PreviousDigest);
        Assert.AreEqual(hit.ArtifactId, difference.CandidateArtifactId);
        Assert.AreEqual(outputDigest, difference.CandidateDigest);
        Assert.IsFalse(difference.HasContentChange);
        Assert.IsFalse(plan.RequiresAdoptionReview);
    }

    [TestMethod]
    public void SameAdoptedIdentityWithDifferentDigestIsRejected()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1');
        ArtifactCacheCandidate hit = Candidate(
            "10101010-1010-4010-8010-101010101010",
            expectedCacheKey,
            Digest('1'),
            CreatedAt);
        var inconsistentAdoption = new ArtifactAdoptionSnapshot(
            hit.ArtifactId,
            Digest('2'));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ArtifactPlanner.Plan(
                Operation,
                expectedCacheKey,
                [hit],
                inconsistentAdoption));
    }

    [TestMethod]
    public void AdoptionDifferenceEnforcesPairedPreviousStateAndRequiredCandidate()
    {
        EntityId<ArtifactIdTag> previousId =
            ArtifactId("20202020-2020-4020-8020-202020202020");
        EntityId<ArtifactIdTag> candidateId =
            ArtifactId("30303030-3030-4030-8030-303030303030");
        ContentDigest previousDigest = Digest('1');
        ContentDigest candidateDigest = Digest('2');

        Assert.ThrowsExactly<ArgumentException>(() =>
            new ArtifactAdoptionDifference(
                previousId,
                previousDigest: null,
                candidateId,
                candidateDigest));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ArtifactAdoptionDifference(
                previousArtifactId: null,
                previousDigest,
                candidateId,
                candidateDigest));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ArtifactAdoptionDifference(
                previousArtifactId: null,
                previousDigest: null,
                candidateArtifactId: null!,
                candidateDigest));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ArtifactAdoptionDifference(
                previousArtifactId: null,
                previousDigest: null,
                candidateId,
                candidateDigest: null!));
    }

    [TestMethod]
    public void PlanDefensivelyCopiesInventoryAndReturnsReadOnlyStaleArtifacts()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1');
        ArtifactCacheCandidate stale = Candidate(
            "40404040-4040-4040-8040-404040404040",
            Key('b', '1'),
            Digest('1'),
            CreatedAt);
        var candidates = new List<ArtifactCacheCandidate> { stale };

        ArtifactPlan plan = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            candidates,
            currentAdoption: null);
        candidates.Clear();

        Assert.AreEqual(1, plan.StaleArtifacts.Count);
        Assert.AreSame(stale, plan.StaleArtifacts[0]);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ArtifactCacheCandidate>)plan.StaleArtifacts).Clear());
    }

    [TestMethod]
    public void RepeatedPlanningIsPureDeterministicAndDoesNotReorderInput()
    {
        ArtifactCacheKey expectedCacheKey = Key('a', '1');
        ArtifactCacheCandidate laterStale = Candidate(
            "50505050-5050-4050-8050-505050505050",
            Key('b', '1'),
            Digest('1'),
            CreatedAt.AddMinutes(1));
        ArtifactCacheCandidate hit = Candidate(
            "60606060-6060-4060-8060-606060606060",
            expectedCacheKey,
            Digest('2'),
            CreatedAt);
        ArtifactCacheCandidate earlierStale = Candidate(
            "70707070-7070-4070-8070-707070707070",
            Key('c', '1'),
            Digest('3'),
            CreatedAt.AddMinutes(-1));
        var candidates = new List<ArtifactCacheCandidate>
        {
            laterStale,
            hit,
            earlierStale,
        };
        ArtifactCacheCandidate[] originalOrder = candidates.ToArray();

        ArtifactPlan first = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            candidates,
            currentAdoption: null);
        ArtifactPlan second = ArtifactPlanner.Plan(
            Operation,
            expectedCacheKey,
            candidates,
            currentAdoption: null);

        CollectionAssert.AreEqual(originalOrder, candidates.ToArray());
        Assert.AreEqual(first.Disposition, second.Disposition);
        Assert.AreSame(first.CachedArtifact, second.CachedArtifact);
        CollectionAssert.AreEqual(
            first.StaleArtifacts.ToArray(),
            second.StaleArtifacts.ToArray());
        Assert.AreEqual(
            first.ProposedAdoptionDifference!.CandidateArtifactId,
            second.ProposedAdoptionDifference!.CandidateArtifactId);
        Assert.AreEqual(
            first.ProposedAdoptionDifference.CandidateDigest,
            second.ProposedAdoptionDifference.CandidateDigest);
        Assert.AreEqual(first.RequiresAdoptionReview, second.RequiresAdoptionReview);
    }

    [TestMethod]
    public void PublicSurfaceIsMinimalImmutableStoreFreeAndHasNoDeletionApi()
    {
        CollectionAssert.AreEqual(
            new[] { "CacheHit", "RecomputeRequired" },
            Enum.GetNames<ArtifactPlanDisposition>());

        Type plannerType = typeof(ArtifactPlanner);
        Assert.IsTrue(plannerType.IsPublic);
        Assert.IsTrue(plannerType.IsAbstract && plannerType.IsSealed);
        Assert.AreEqual(0, plannerType.GetInterfaces().Length);
        Assert.AreEqual(0, plannerType.GetConstructors().Length);
        MethodInfo[] plannerMethods = GetDeclaredPublicMethods(plannerType);
        Assert.AreEqual(1, plannerMethods.Length);
        MethodInfo planMethod = plannerMethods[0];
        Assert.AreEqual("Plan", planMethod.Name);
        Assert.IsTrue(planMethod.IsStatic);
        Assert.AreEqual(typeof(ArtifactPlan), planMethod.ReturnType);
        CollectionAssert.AreEqual(
            new[]
            {
                typeof(string),
                typeof(ArtifactCacheKey),
                typeof(IReadOnlyList<ArtifactCacheCandidate>),
                typeof(ArtifactAdoptionSnapshot),
            },
            planMethod.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());

        AssertInputType(
            typeof(ArtifactCacheCandidate),
            new[]
            {
                typeof(EntityId<ArtifactIdTag>),
                typeof(string),
                typeof(ArtifactCacheKey),
                typeof(ContentDigest),
                typeof(DateTimeOffset),
            },
            "ArtifactId",
            "Operation",
            "CacheKey",
            "OutputDigest",
            "CreatedAt");
        AssertInputType(
            typeof(ArtifactAdoptionSnapshot),
            new[]
            {
                typeof(EntityId<ArtifactIdTag>),
                typeof(ContentDigest),
            },
            "ArtifactId",
            "OutputDigest");
        AssertResultType(
            typeof(ArtifactAdoptionDifference),
            "PreviousArtifactId",
            "PreviousDigest",
            "CandidateArtifactId",
            "CandidateDigest",
            "HasContentChange");
        AssertResultType(
            typeof(ArtifactPlan),
            "Operation",
            "ExpectedCacheKey",
            "Disposition",
            "CachedArtifact",
            "StaleArtifacts",
            "CurrentAdoption",
            "ProposedAdoptionDifference",
            "RequiresAdoptionReview");
        Assert.AreEqual(
            typeof(IReadOnlyList<ArtifactCacheCandidate>),
            typeof(ArtifactPlan).GetProperty("StaleArtifacts")!.PropertyType);

        foreach (Type type in new[]
        {
            plannerType,
            typeof(ArtifactCacheCandidate),
            typeof(ArtifactAdoptionSnapshot),
            typeof(ArtifactAdoptionDifference),
            typeof(ArtifactPlan),
        })
        {
            Assert.AreEqual(0, type.GetInterfaces().Length, type.Name);
            Assert.IsFalse(GetDeclaredPublicMethods(type).Any(static method =>
                method.Name.Contains("Delete", StringComparison.Ordinal) ||
                method.Name.Contains("Remove", StringComparison.Ordinal)), type.Name);
            Assert.IsFalse(GetPublicSurfaceTypes(type).Any(static surfaceType =>
                surfaceType.Name.EndsWith("Store", StringComparison.Ordinal)), type.Name);
        }
    }

    private static ArtifactCacheCandidate Candidate(
        string artifactId,
        ArtifactCacheKey cacheKey,
        ContentDigest outputDigest,
        DateTimeOffset createdAt) =>
        Candidate(ArtifactId(artifactId), cacheKey, outputDigest, createdAt);

    private static ArtifactCacheCandidate Candidate(
        EntityId<ArtifactIdTag> artifactId,
        ArtifactCacheKey cacheKey,
        ContentDigest outputDigest,
        DateTimeOffset createdAt) =>
        new(artifactId, Operation, cacheKey, outputDigest, createdAt);

    private static EntityId<ArtifactIdTag> ArtifactId(string value) =>
        EntityId<ArtifactIdTag>.Parse(value);

    private static ArtifactCacheKey Key(char configuration, params char[] inputs) =>
        ArtifactCacheKey.Create(
            Operation,
            Digest(configuration),
            inputs.Select(Digest).ToArray());

    private static ContentDigest Digest(char value) => new(new string(value, 64));

    private static void AssertOperationRejected(
        string? operation,
        ArtifactCacheKey key,
        EntityId<ArtifactIdTag> artifactId,
        ContentDigest digest)
    {
        AssertParamName<ArgumentException>(
            "operation",
            () => new ArtifactCacheCandidate(
                artifactId,
                operation!,
                key,
                digest,
                CreatedAt));
    }

    private static void AssertPlanOperationRejected(
        string? operation,
        ArtifactCacheKey key)
    {
        AssertParamName<ArgumentException>(
            "operation",
            () => ArtifactPlanner.Plan(
                operation!,
                key,
                [],
                currentAdoption: null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }

    private static void AssertInputType(
        Type type,
        Type[] expectedConstructorParameters,
        params string[] expectedProperties)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        ConstructorInfo[] constructors = type.GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);
        Assert.AreEqual(1, constructors.Length, type.Name);
        CollectionAssert.AreEqual(
            expectedConstructorParameters,
            constructors[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray(),
            type.Name);
        AssertImmutableProperties(type, expectedProperties);
        Assert.AreEqual(0, GetDeclaredPublicMethods(type).Length, type.Name);
    }

    private static void AssertResultType(Type type, params string[] expectedProperties)
    {
        Assert.IsTrue(type.IsPublic, type.Name);
        Assert.IsTrue(type.IsSealed, type.Name);
        Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
        AssertImmutableProperties(type, expectedProperties);
        Assert.AreEqual(0, GetDeclaredPublicMethods(type).Length, type.Name);
    }

    private static void AssertImmutableProperties(Type type, string[] expectedProperties)
    {
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedProperties,
            properties.Select(static property => property.Name).ToArray(),
            type.Name);
        Assert.IsTrue(
            properties.All(static property => property.SetMethod is null),
            type.Name);
    }

    private static MethodInfo[] GetDeclaredPublicMethods(Type type) =>
        type.GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .ToArray();

    private static IEnumerable<Type> GetPublicSurfaceTypes(Type type)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors())
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly))
        {
            yield return method.ReturnType;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (PropertyInfo property in type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            yield return property.PropertyType;
        }
    }
}