using System.Numerics;
using System.Reflection;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Serialization;
using HubRadio.Core.Subtitles;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class SubtitleLayoutEngineTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2043, 8, 31, 1, 2, 3, TimeSpan.Zero);

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [TestMethod]
    public void ResolveChoosesLowestVersionedSoftCostAndRetainsBindings()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout(
            [
                Region("subtitle", FullRect(), LayoutRegionRole.Subtitle),
                Region("material", RightHalf(), LayoutRegionRole.Material),
            ],
            [Zone("safe-frame", FullRect())]);
        SubtitlePlacementCandidate faceCandidate = Candidate(1, "subtitle", LeftHalfPlacement());
        SubtitlePlacementCandidate materialCandidate = Candidate(2, "subtitle", RightHalfPlacement());
        SubtitleLayoutProfile profile = CreateProfile(layout, faceWeight: 10, materialWeight: 2);

        SubtitleLayoutResolution result = SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(
            cue,
            layout,
            profile,
            [faceCandidate, materialCandidate],
            prohibitedRegions: [],
            faceRegions: [LeftHalf()],
            previousPlacement: null));

        Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, result.Status);
        Assert.AreEqual(cue.Id, result.SourceCandidateId);
        Assert.AreEqual(layout.Id, result.LayoutSpecId);
        Assert.AreEqual(profile.VersionDigest, result.ProfileVersionDigest);
        Assert.HasCount(0, result.GlobalHardConstraints);
        Assert.IsNotNull(result.Plan);
        Assert.AreEqual(materialCandidate, result.Plan.Placement);
        Assert.AreEqual(new BigInteger(2), result.Plan.SoftCost);
        CollectionAssert.AreEqual(
            new[] { SubtitleSoftConstraintKind.MaterialRegionOverlap },
            result.Plan.SoftConstraints.ToArray());
        Assert.HasCount(2, result.Evaluations);
        CollectionAssert.AreEqual(
            new[] { SubtitleSoftConstraintKind.FaceRegionOverlap },
            result.Evaluations[0].SoftConstraints.ToArray());
        Assert.AreEqual(new BigInteger(10), result.Evaluations[0].SoftCost);
        Assert.IsTrue(result.Evaluations[0].IsHardConstraintSatisfied);
        Assert.IsTrue(result.Evaluations[1].IsHardConstraintSatisfied);
    }

    [TestMethod]
    public void VersionedWeightsChangePreferenceWithoutChangingHardConstraints()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout(
            [
                Region("subtitle", FullRect(), LayoutRegionRole.Subtitle),
                Region("material", RightHalf(), LayoutRegionRole.Material),
            ],
            [Zone("safe-frame", FullRect())]);
        SubtitlePlacementCandidate faceCandidate = Candidate(1, "subtitle", LeftHalfPlacement());
        SubtitlePlacementCandidate materialCandidate = Candidate(2, "subtitle", RightHalfPlacement());

        SubtitleLayoutResolution preferFace = Resolve(
            cue,
            layout,
            CreateProfile(layout, faceWeight: 1, materialWeight: 10, digestFill: 'a'),
            [faceCandidate, materialCandidate],
            faceRegions: [LeftHalf()]);
        SubtitleLayoutResolution preferMaterial = Resolve(
            cue,
            layout,
            CreateProfile(layout, faceWeight: 10, materialWeight: 1, digestFill: 'b'),
            [faceCandidate, materialCandidate],
            faceRegions: [LeftHalf()]);

        Assert.AreEqual(faceCandidate, preferFace.Plan!.Placement);
        Assert.AreEqual(materialCandidate, preferMaterial.Plan!.Placement);
        Assert.AreNotEqual(preferFace.ProfileVersionDigest, preferMaterial.ProfileVersionDigest);
        Assert.IsTrue(preferFace.Evaluations.All(static evaluation =>
            evaluation.IsHardConstraintSatisfied));
        Assert.IsTrue(preferMaterial.Evaluations.All(static evaluation =>
            evaluation.IsHardConstraintSatisfied));
    }

    [TestMethod]
    public void EqualSoftCostUsesStableInputOrder()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        SubtitlePlacementCandidate first = Candidate(1, "subtitle", LeftHalfPlacement());
        SubtitlePlacementCandidate second = Candidate(2, "subtitle", RightHalfPlacement());
        SubtitleLayoutProfile profile = CreateProfile(layout);

        SubtitleLayoutResolution firstRun = Resolve(cue, layout, profile, [first, second]);
        SubtitleLayoutResolution secondRun = Resolve(cue, layout, profile, [first, second]);

        Assert.AreEqual(first, firstRun.Plan!.Placement);
        Assert.AreEqual(first, secondRun.Plan!.Placement);
        Assert.AreEqual(BigInteger.Zero, firstRun.Plan.SoftCost);
    }

    [TestMethod]
    public void OutsideOutputFrameIsHardRejectedWithoutDiscardingOtherCandidates()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        SubtitlePlacementCandidate outside = Candidate(
            1,
            "subtitle",
            Placement(-1, 10, 0, 1, 1, 2, 1, 2));
        SubtitlePlacementCandidate inside = Candidate(2, "subtitle", RightHalfPlacement());

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            CreateProfile(layout),
            [outside, inside]);

        Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, result.Status);
        Assert.AreEqual(inside, result.Plan!.Placement);
        Assert.IsFalse(result.Evaluations[0].IsHardConstraintSatisfied);
        CollectionAssert.Contains(
            result.Evaluations[0].HardConstraints.ToArray(),
            SubtitleHardConstraintKind.OutsideOutputFrame);
        Assert.IsTrue(result.Evaluations[1].IsHardConstraintSatisfied);
    }

    [TestMethod]
    public void SubtitleRegionBindingAndContainmentAreHardConstraints()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout(
            [
                Region("subtitle", LeftHalf(), LayoutRegionRole.Subtitle),
                Region("material", RightHalf(), LayoutRegionRole.Material),
            ],
            [Zone("safe-frame", FullRect())]);
        SubtitlePlacementCandidate outsideSubtitle = Candidate(
            1,
            "subtitle",
            RightHalfPlacement());
        SubtitlePlacementCandidate unknown = Candidate(
            2,
            "missing",
            LeftHalfPlacement());
        SubtitlePlacementCandidate wrongRole = Candidate(
            3,
            "material",
            RightHalfPlacement());

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            CreateProfile(layout),
            [outsideSubtitle, unknown, wrongRole]);

        Assert.AreEqual(SubtitleLayoutResolutionStatus.ManualAdjustmentRequired, result.Status);
        Assert.IsNull(result.Plan);
        CollectionAssert.Contains(
            result.Evaluations[0].HardConstraints.ToArray(),
            SubtitleHardConstraintKind.OutsideSubtitleRegion);
        CollectionAssert.Contains(
            result.Evaluations[1].HardConstraints.ToArray(),
            SubtitleHardConstraintKind.UnknownLayoutRegion);
        CollectionAssert.Contains(
            result.Evaluations[2].HardConstraints.ToArray(),
            SubtitleHardConstraintKind.LayoutRegionIsNotSubtitle);
    }

    [TestMethod]
    public void CandidateMustFitEveryRequiredSafeZone()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout(
            [Region("subtitle", FullRect(), LayoutRegionRole.Subtitle)],
            [
                Zone("frame", FullRect()),
                Zone("inner", CenterHalf()),
            ]);
        SubtitleLayoutProfile profile = CreateProfile(
            layout,
            requiredSafeZoneNames: ["frame", "inner"]);
        SubtitlePlacementCandidate outsideInner = Candidate(1, "subtitle", LeftHalfPlacement());
        SubtitlePlacementCandidate insideBoth = Candidate(2, "subtitle", CenterHalfPlacement());

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            profile,
            [outsideInner, insideBoth]);

        Assert.AreEqual(insideBoth, result.Plan!.Placement);
        CollectionAssert.Contains(
            result.Evaluations[0].HardConstraints.ToArray(),
            SubtitleHardConstraintKind.OutsideRequiredSafeZone);
        Assert.IsTrue(result.Evaluations[1].IsHardConstraintSatisfied);
    }

    [TestMethod]
    public void MissingRequiredSafeZoneIsGlobalNoSolutionInsteadOfAnImplicitFallback()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        SubtitleLayoutProfile profile = CreateProfile(
            layout,
            requiredSafeZoneNames: ["safe-frame", "unavailable-platform-zone"]);

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            profile,
            [Candidate(1, "subtitle", FullPlacement())]);

        Assert.AreEqual(SubtitleLayoutResolutionStatus.ManualAdjustmentRequired, result.Status);
        Assert.IsNull(result.Plan);
        CollectionAssert.AreEqual(
            new[] { SubtitleHardConstraintKind.RequiredSafeZoneMissing },
            result.GlobalHardConstraints.ToArray());
        Assert.IsTrue(result.Evaluations.Single().IsHardConstraintSatisfied);
    }

    [TestMethod]
    public void ProhibitedAreaOverlapIsHardButTouchingBoundaryIsAllowed()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        SubtitlePlacementCandidate overlapping = Candidate(1, "subtitle", FullPlacement());
        SubtitlePlacementCandidate touching = Candidate(2, "subtitle", RightHalfPlacement());
        SubtitleProhibitedRegion leftControls = new("left-controls", LeftHalf());

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            CreateProfile(layout),
            [overlapping, touching],
            prohibitedRegions: [leftControls]);

        Assert.AreEqual(touching, result.Plan!.Placement);
        CollectionAssert.Contains(
            result.Evaluations[0].HardConstraints.ToArray(),
            SubtitleHardConstraintKind.ProhibitedRegionOverlap);
        CollectionAssert.DoesNotContain(
            result.Evaluations[1].HardConstraints.ToArray(),
            SubtitleHardConstraintKind.ProhibitedRegionOverlap);
    }

    [TestMethod]
    public void AllHardFailuresReturnTypedManualResultWithoutRelaxation()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        SubtitlePlacementCandidate candidate = Candidate(1, "subtitle", FullPlacement());

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            CreateProfile(layout),
            [candidate],
            prohibitedRegions: [new SubtitleProhibitedRegion("full-mask", FullRect())]);

        Assert.AreEqual(SubtitleLayoutResolutionStatus.ManualAdjustmentRequired, result.Status);
        Assert.IsNull(result.Plan);
        Assert.HasCount(1, result.Evaluations);
        Assert.AreEqual(candidate, result.Evaluations[0].Placement);
        CollectionAssert.AreEqual(
            new[] { SubtitleHardConstraintKind.ProhibitedRegionOverlap },
            result.Evaluations[0].HardConstraints.ToArray());
    }

    [TestMethod]
    public void EmptyCandidateSetReturnsTypedManualResult()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            CreateProfile(layout),
            candidates: []);

        Assert.AreEqual(SubtitleLayoutResolutionStatus.ManualAdjustmentRequired, result.Status);
        Assert.IsNull(result.Plan);
        Assert.HasCount(0, result.Evaluations);
        CollectionAssert.AreEqual(
            new[] { SubtitleHardConstraintKind.NoPlacementCandidate },
            result.GlobalHardConstraints.ToArray());
    }

    [TestMethod]
    public void UntimedSourceCandidateRequiresManualAdjustmentEvenWhenPlacementFits()
    {
        SubtitleCueCandidate cue = CreateCue(untimed: true);
        LayoutSpec layout = CreateLayout();

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            CreateProfile(layout),
            [Candidate(1, "subtitle", FullPlacement())]);

        Assert.IsTrue(cue.ManualTimingAdjustmentRequired);
        Assert.AreEqual(SubtitleLayoutResolutionStatus.ManualAdjustmentRequired, result.Status);
        Assert.IsNull(result.Plan);
        CollectionAssert.AreEqual(
            new[] { SubtitleHardConstraintKind.SourceTimingAdjustmentRequired },
            result.GlobalHardConstraints.ToArray());
        Assert.IsTrue(result.Evaluations.Single().IsHardConstraintSatisfied);
    }

    [TestMethod]
    public void FaceRegionsAreOptionalSoftInputAndNeverBecomeAHardDependency()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        SubtitlePlacementCandidate candidate = Candidate(1, "subtitle", LeftHalfPlacement());
        SubtitleLayoutProfile profile = CreateProfile(layout, faceWeight: 7);

        SubtitleLayoutResolution withoutFaces = Resolve(
            cue,
            layout,
            profile,
            [candidate],
            faceRegions: []);
        SubtitleLayoutResolution withFaces = Resolve(
            cue,
            layout,
            profile,
            [candidate],
            faceRegions: [LeftHalf()]);

        Assert.AreEqual(BigInteger.Zero, withoutFaces.Plan!.SoftCost);
        Assert.HasCount(0, withoutFaces.Plan.SoftConstraints);
        Assert.AreEqual(new BigInteger(7), withFaces.Plan!.SoftCost);
        CollectionAssert.AreEqual(
            new[] { SubtitleSoftConstraintKind.FaceRegionOverlap },
            withFaces.Plan.SoftConstraints.ToArray());
        Assert.HasCount(0, withFaces.Evaluations.Single().HardConstraints);
    }

    [TestMethod]
    public void MaterialGraphicAndPlacementChangeRemainWeightedSoftConstraints()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout(
            [
                Region("subtitle", FullRect(), LayoutRegionRole.Subtitle),
                Region("material", LeftHalf(), LayoutRegionRole.Material),
                Region("graphic", RightHalf(), LayoutRegionRole.Graphic),
            ],
            [Zone("safe-frame", FullRect())]);
        SubtitleLayoutProfile profile = CreateProfile(
            layout,
            materialWeight: 3,
            graphicWeight: 5,
            placementChangeWeight: 11);

        SubtitleLayoutResolution result = Resolve(
            cue,
            layout,
            profile,
            [Candidate(1, "subtitle", FullPlacement())],
            previousPlacement: LeftHalfPlacement());

        Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, result.Status);
        CollectionAssert.AreEqual(
            new[]
            {
                SubtitleSoftConstraintKind.MaterialRegionOverlap,
                SubtitleSoftConstraintKind.GraphicRegionOverlap,
                SubtitleSoftConstraintKind.PlacementChanged,
            },
            result.Plan!.SoftConstraints.ToArray());
        Assert.AreEqual(new BigInteger(19), result.Plan.SoftCost);
    }

    [TestMethod]
    public void RequestsProfilesAndResultsDefensivelyCopyCollections()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        var safeZoneNames = new List<string> { "safe-frame" };
        var weights = CreateWeights();
        var candidates = new List<SubtitlePlacementCandidate>
        {
            Candidate(1, "subtitle", FullPlacement()),
        };
        var prohibited = new List<SubtitleProhibitedRegion>();
        var faces = new List<NormalizedRect>();
        var profile = new SubtitleLayoutProfile(
            layout.Id,
            Digest('a'),
            safeZoneNames,
            weights);
        var request = new SubtitleLayoutRequest(
            cue,
            layout,
            profile,
            candidates,
            prohibited,
            faces,
            previousPlacement: null);

        safeZoneNames.Clear();
        weights.Clear();
        candidates.Clear();
        prohibited.Add(new SubtitleProhibitedRegion("late", FullRect()));
        faces.Add(FullRect());
        SubtitleLayoutResolution result = SubtitleLayoutEngine.Resolve(request);

        Assert.HasCount(1, profile.RequiredSafeZoneNames);
        Assert.HasCount(Enum.GetValues<SubtitleSoftConstraintKind>().Length, profile.SoftConstraintWeights);
        Assert.HasCount(1, request.Candidates);
        Assert.HasCount(0, request.ProhibitedRegions);
        Assert.HasCount(0, request.FaceRegions);
        Assert.AreEqual(SubtitleLayoutResolutionStatus.Resolved, result.Status);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<string>)profile.RequiredSafeZoneNames).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleSoftConstraintWeight>)profile.SoftConstraintWeights).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitlePlacementCandidate>)request.Candidates).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitlePlacementEvaluation>)result.Evaluations).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SubtitleSoftConstraintKind>)result.Plan!.SoftConstraints).Clear());
    }

    [TestMethod]
    public void InvalidProfilesRequestsAndGeometryFailClosed()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        SubtitleLayoutProfile profile = CreateProfile(layout);
        SubtitlePlacementCandidate candidate = Candidate(1, "subtitle", FullPlacement());

        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => Placement(0, 1, 0, 1, 0, 1, 1, 1));
        AssertParamName<ArgumentOutOfRangeException>(
            "kind",
            () => new SubtitleSoftConstraintWeight(
                (SubtitleSoftConstraintKind)int.MaxValue,
                1));
        AssertParamName<ArgumentOutOfRangeException>(
            "weight",
            () => new SubtitleSoftConstraintWeight(
                SubtitleSoftConstraintKind.FaceRegionOverlap,
                -1));
        AssertParamName<ArgumentException>(
            "requiredSafeZoneNames",
            () => new SubtitleLayoutProfile(layout.Id, Digest('a'), [], CreateWeights()));
        AssertParamName<ArgumentException>(
            "softConstraintWeights",
            () => new SubtitleLayoutProfile(
                layout.Id,
                Digest('a'),
                ["safe-frame"],
                CreateWeights().Skip(1).ToArray()));

        EntityId<SubtitlePlacementCandidateIdTag> duplicateId = PlacementId(1);
        AssertParamName<ArgumentException>(
            "candidates",
            () => new SubtitleLayoutRequest(
                cue,
                layout,
                profile,
                [
                    new SubtitlePlacementCandidate(duplicateId, "subtitle", FullPlacement()),
                    new SubtitlePlacementCandidate(duplicateId, "subtitle", LeftHalfPlacement()),
                ],
                [],
                [],
                null));

        LayoutSpec otherLayout = CreateLayout();
        AssertParamName<ArgumentException>(
            "profile",
            () => new SubtitleLayoutRequest(
                cue,
                otherLayout,
                profile,
                [candidate],
                [],
                [],
                null));
        Assert.ThrowsExactly<ArgumentNullException>(() => SubtitleLayoutEngine.Resolve(null!));
    }

    [TestMethod]
    public void PreCancellationReturnsNoPartialResolution()
    {
        SubtitleCueCandidate cue = CreateCue();
        LayoutSpec layout = CreateLayout();
        var request = new SubtitleLayoutRequest(
            cue,
            layout,
            CreateProfile(layout),
            [Candidate(1, "subtitle", FullPlacement())],
            [],
            [],
            null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SubtitleLayoutEngine.Resolve(request, cancellation.Token));
    }

    [TestMethod]
    public void PublicSurfaceIsTypedImmutableAndContainsNoInventedThresholdApi()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "NoPlacementCandidate",
                "SourceTimingAdjustmentRequired",
                "RequiredSafeZoneMissing",
                "OutsideOutputFrame",
                "UnknownLayoutRegion",
                "LayoutRegionIsNotSubtitle",
                "OutsideSubtitleRegion",
                "OutsideRequiredSafeZone",
                "ProhibitedRegionOverlap",
            },
            Enum.GetNames<SubtitleHardConstraintKind>());
        CollectionAssert.AreEqual(
            new[]
            {
                "FaceRegionOverlap",
                "MaterialRegionOverlap",
                "GraphicRegionOverlap",
                "PlacementChanged",
            },
            Enum.GetNames<SubtitleSoftConstraintKind>());
        CollectionAssert.AreEqual(
            new[] { "Resolved", "ManualAdjustmentRequired" },
            Enum.GetNames<SubtitleLayoutResolutionStatus>());

        Type[] outputTypes =
        [
            typeof(SubtitlePlacementEvaluation),
            typeof(SubtitleLayoutPlan),
            typeof(SubtitleLayoutResolution),
        ];
        foreach (Type type in outputTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.IsTrue(typeof(SubtitlePlacementCandidateIdTag).IsPublic);
        Assert.IsTrue(typeof(SubtitlePlacementCandidateIdTag).IsSealed);
        Assert.IsTrue(typeof(SubtitleLayoutEngine).IsAbstract);
        Assert.IsTrue(typeof(SubtitleLayoutEngine).IsSealed);
        MethodInfo method = typeof(SubtitleLayoutEngine).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Single();
        Assert.AreEqual(nameof(SubtitleLayoutEngine.Resolve), method.Name);
        CollectionAssert.AreEqual(
            new[] { typeof(SubtitleLayoutRequest), typeof(CancellationToken) },
            method.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());

        MemberInfo[] publicMembers = typeof(SubtitleLayoutEngine).Assembly
            .GetExportedTypes()
            .Where(static type => type.Namespace == "HubRadio.Core.Subtitles")
            .SelectMany(static type => type.GetMembers(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .ToArray();
        Assert.IsFalse(publicMembers.Any(static member =>
            member.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Dpi", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("FramesPerSecond", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("CharactersPerSecond", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(publicMembers.OfType<MethodBase>()
            .SelectMany(static methodBase => methodBase.GetParameters())
            .Any(static parameter => parameter.ParameterType == typeof(double)
                || parameter.ParameterType == typeof(float)
                || parameter.ParameterType.Namespace == "HubRadio.Core.Analysis"));
    }

    private static SubtitleLayoutResolution Resolve(
        SubtitleCueCandidate cue,
        LayoutSpec layout,
        SubtitleLayoutProfile profile,
        IReadOnlyList<SubtitlePlacementCandidate> candidates,
        IReadOnlyList<SubtitleProhibitedRegion>? prohibitedRegions = null,
        IReadOnlyList<NormalizedRect>? faceRegions = null,
        SubtitleLayoutRect? previousPlacement = null)
    {
        return SubtitleLayoutEngine.Resolve(new SubtitleLayoutRequest(
            cue,
            layout,
            profile,
            candidates,
            prohibitedRegions ?? [],
            faceRegions ?? [],
            previousPlacement));
    }

    private static SubtitleLayoutProfile CreateProfile(
        LayoutSpec layout,
        long faceWeight = 0,
        long materialWeight = 0,
        long graphicWeight = 0,
        long placementChangeWeight = 0,
        char digestFill = 'a',
        IReadOnlyList<string>? requiredSafeZoneNames = null)
    {
        return new SubtitleLayoutProfile(
            layout.Id,
            Digest(digestFill),
            requiredSafeZoneNames ?? ["safe-frame"],
            CreateWeights(
                faceWeight,
                materialWeight,
                graphicWeight,
                placementChangeWeight));
    }

    private static List<SubtitleSoftConstraintWeight> CreateWeights(
        long faceWeight = 0,
        long materialWeight = 0,
        long graphicWeight = 0,
        long placementChangeWeight = 0)
    {
        return
        [
            new SubtitleSoftConstraintWeight(
                SubtitleSoftConstraintKind.FaceRegionOverlap,
                faceWeight),
            new SubtitleSoftConstraintWeight(
                SubtitleSoftConstraintKind.MaterialRegionOverlap,
                materialWeight),
            new SubtitleSoftConstraintWeight(
                SubtitleSoftConstraintKind.GraphicRegionOverlap,
                graphicWeight),
            new SubtitleSoftConstraintWeight(
                SubtitleSoftConstraintKind.PlacementChanged,
                placementChangeWeight),
        ];
    }

    private static LayoutSpec CreateLayout(
        IReadOnlyList<LayoutRegion>? regions = null,
        IReadOnlyList<SafeZone>? safeZones = null)
    {
        return new LayoutSpec(
            EntityId<LayoutSpecIdTag>.New(),
            16,
            9,
            regions ?? [Region("subtitle", FullRect(), LayoutRegionRole.Subtitle)],
            safeZones ?? [Zone("safe-frame", FullRect())]);
    }

    private static LayoutRegion Region(
        string name,
        NormalizedRect rect,
        LayoutRegionRole role)
    {
        return new LayoutRegion(name, rect, role);
    }

    private static SafeZone Zone(string name, NormalizedRect rect)
    {
        return new SafeZone(name, rect);
    }

    private static SubtitlePlacementCandidate Candidate(
        long id,
        string regionName,
        SubtitleLayoutRect rect)
    {
        return new SubtitlePlacementCandidate(PlacementId(id), regionName, rect);
    }

    private static EntityId<SubtitlePlacementCandidateIdTag> PlacementId(long value)
    {
        return EntityId<SubtitlePlacementCandidateIdTag>.Parse(
            $"72000001-0000-0000-0000-{value:x12}");
    }

    private static ContentDigest Digest(char fill)
    {
        return new ContentDigest(new string(fill, 64));
    }

    private static SubtitleLayoutRect FullPlacement()
    {
        return Placement(0, 1, 0, 1, 1, 1, 1, 1);
    }

    private static SubtitleLayoutRect LeftHalfPlacement()
    {
        return Placement(0, 1, 0, 1, 1, 2, 1, 1);
    }

    private static SubtitleLayoutRect RightHalfPlacement()
    {
        return Placement(1, 2, 0, 1, 1, 2, 1, 1);
    }

    private static SubtitleLayoutRect CenterHalfPlacement()
    {
        return Placement(1, 4, 1, 4, 1, 2, 1, 2);
    }

    private static SubtitleLayoutRect Placement(
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator)
    {
        return new SubtitleLayoutRect(
            new RationalNumber(xNumerator, xDenominator),
            new RationalNumber(yNumerator, yDenominator),
            new RationalNumber(widthNumerator, widthDenominator),
            new RationalNumber(heightNumerator, heightDenominator));
    }

    private static NormalizedRect FullRect()
    {
        return Rect(0, 1, 0, 1, 1, 1, 1, 1);
    }

    private static NormalizedRect LeftHalf()
    {
        return Rect(0, 1, 0, 1, 1, 2, 1, 1);
    }

    private static NormalizedRect RightHalf()
    {
        return Rect(1, 2, 0, 1, 1, 2, 1, 1);
    }

    private static NormalizedRect CenterHalf()
    {
        return Rect(1, 4, 1, 4, 1, 2, 1, 2);
    }

    private static NormalizedRect Rect(
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator)
    {
        return new NormalizedRect(
            new RationalNumber(xNumerator, xDenominator),
            new RationalNumber(yNumerator, yDenominator),
            new RationalNumber(widthNumerator, widthDenominator),
            new RationalNumber(heightNumerator, heightDenominator));
    }

    private static SubtitleCueCandidate CreateCue(bool untimed = false)
    {
        EntityId<TranscriptRevisionIdTag> revisionId = Id<TranscriptRevisionIdTag>(1);
        EntityId<TranscriptTokenIdTag> tokenId = Id<TranscriptTokenIdTag>(2);
        TimeRange utteranceRange = new(default, new RationalTime(2, 1));
        var token = new Token(
            tokenId,
            "字幕",
            untimed ? null : utteranceRange,
            recognitionConfidence: null);
        var sentence = new Sentence(
            Id<TranscriptSentenceIdTag>(3),
            token.Text,
            [token]);
        EntityId<SourceSpanIdTag> sourceSpanId = Id<SourceSpanIdTag>(4);
        var utterance = new Utterance(
            Id<TranscriptUtteranceIdTag>(5),
            sourceSpanId,
            Id<TranscriptSpeakerIdTag>(6),
            "Speaker",
            token.Text,
            utteranceRange,
            recognitionConfidence: null,
            [sentence]);
        var revision = new TranscriptRevision(
            revisionId,
            Id<TranscriptRevisionIdTag>(7),
            TranscriptChangeSource.Human,
            CreatedAt,
            [utterance]);
        var sourceSpan = new SourceSpan(
            sourceSpanId,
            Id<AssetIdTag>(8),
            "audio:1",
            utteranceRange,
            Digest('f'),
            revisionId,
            [tokenId]);

        return SubtitleSegmenter.Segment(
                revision,
                ComputeRevisionDigest(revision),
                [sourceSpan])
            .Candidates
            .Single();
    }

    private static ContentDigest ComputeRevisionDigest(TranscriptRevision revision)
    {
        var snapshot = new DigestRevision(
            revision.Id.ToString(),
            revision.ParentRevisionId?.ToString(),
            revision.ChangeSource switch
            {
                TranscriptChangeSource.Machine => "machine",
                TranscriptChangeSource.Human => "human",
                _ => throw new InvalidOperationException(),
            },
            revision.CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            revision.Utterances.Select(static utterance => new DigestUtterance(
                utterance.Id.ToString(),
                utterance.SourceSpanId.ToString(),
                utterance.SpeakerId.ToString(),
                utterance.SpeakerLabel,
                utterance.Text,
                DigestRange.From(utterance.Range),
                utterance.RecognitionConfidence,
                utterance.Sentences.Select(static sentence => new DigestSentence(
                    sentence.Id.ToString(),
                    sentence.Text,
                    sentence.Tokens.Select(static token => new DigestToken(
                        token.Id.ToString(),
                        token.Text,
                        token.Range is TimeRange range ? DigestRange.From(range) : null,
                        token.RecognitionConfidence,
                        token.ManualTimingAdjustmentRequired)).ToArray())).ToArray())).ToArray());
        return CanonicalJson.ComputeDigest(
            JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonOptions));
    }

    private static EntityId<TTag> Id<TTag>(long value)
    {
        return EntityId<TTag>.Parse($"72000002-0000-0000-0000-{value:x12}");
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }

    private sealed record DigestRevision(
        string Id,
        string? ParentRevisionId,
        string ChangeSource,
        string CreatedAt,
        DigestUtterance[] Utterances);

    private sealed record DigestUtterance(
        string Id,
        string SourceSpanId,
        string SpeakerId,
        string SpeakerLabel,
        string Text,
        DigestRange Range,
        double? RecognitionConfidence,
        DigestSentence[] Sentences);

    private sealed record DigestSentence(
        string Id,
        string Text,
        DigestToken[] Tokens);

    private sealed record DigestToken(
        string Id,
        string Text,
        DigestRange? Range,
        double? RecognitionConfidence,
        bool ManualTimingAdjustmentRequired);

    private sealed record DigestRange(DigestTime Start, DigestTime Duration)
    {
        internal static DigestRange From(TimeRange range)
        {
            return new DigestRange(
                new DigestTime(range.Start.Value, range.Start.Rate),
                new DigestTime(range.Duration.Value, range.Duration.Rate));
        }
    }

    private sealed record DigestTime(long Value, long Rate);
}