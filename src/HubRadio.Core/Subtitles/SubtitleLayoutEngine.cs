using System.Numerics;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Subtitles;

public sealed class SubtitlePlacementCandidateIdTag
{
}

public enum SubtitleHardConstraintKind
{
    NoPlacementCandidate,
    SourceTimingAdjustmentRequired,
    RequiredSafeZoneMissing,
    OutsideOutputFrame,
    UnknownLayoutRegion,
    LayoutRegionIsNotSubtitle,
    OutsideSubtitleRegion,
    OutsideRequiredSafeZone,
    ProhibitedRegionOverlap,
}

public enum SubtitleSoftConstraintKind
{
    FaceRegionOverlap,
    MaterialRegionOverlap,
    GraphicRegionOverlap,
    PlacementChanged,
}

public enum SubtitleLayoutResolutionStatus
{
    Resolved,
    ManualAdjustmentRequired,
}

/// <summary>
/// A positive subtitle rectangle in normalized output coordinates. Unlike
/// <see cref="NormalizedRect"/>, its offsets and far edges may be outside the
/// output frame so that the engine can report that hard-constraint failure.
/// </summary>
public sealed class SubtitleLayoutRect : IEquatable<SubtitleLayoutRect>
{
    public SubtitleLayoutRect(
        RationalNumber x,
        RationalNumber y,
        RationalNumber width,
        RationalNumber height)
    {
        if (width <= default(RationalNumber))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "A subtitle rectangle width must be positive.");
        }

        if (height <= default(RationalNumber))
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                "A subtitle rectangle height must be positive.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public RationalNumber X { get; }

    public RationalNumber Y { get; }

    public RationalNumber Width { get; }

    public RationalNumber Height { get; }

    public bool Equals(SubtitleLayoutRect? other)
    {
        return other is not null
            && X == other.X
            && Y == other.Y
            && Width == other.Width
            && Height == other.Height;
    }

    public override bool Equals(object? obj)
    {
        return obj is SubtitleLayoutRect other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Width, Height);
    }

    public static bool operator ==(SubtitleLayoutRect? left, SubtitleLayoutRect? right)
    {
        return left?.Equals(right) ?? right is null;
    }

    public static bool operator !=(SubtitleLayoutRect? left, SubtitleLayoutRect? right)
    {
        return !(left == right);
    }
}

public sealed class SubtitlePlacementCandidate : IEquatable<SubtitlePlacementCandidate>
{
    public SubtitlePlacementCandidate(
        EntityId<SubtitlePlacementCandidateIdTag> id,
        string regionName,
        SubtitleLayoutRect rect)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(rect);

        Id = id;
        RegionName = ContractText.RequireNonBlank(
            regionName,
            nameof(regionName),
            "A subtitle placement region name");
        Rect = rect;
    }

    public EntityId<SubtitlePlacementCandidateIdTag> Id { get; }

    public string RegionName { get; }

    public SubtitleLayoutRect Rect { get; }

    public bool Equals(SubtitlePlacementCandidate? other)
    {
        return other is not null
            && Id == other.Id
            && StringComparer.Ordinal.Equals(RegionName, other.RegionName)
            && Rect == other.Rect;
    }

    public override bool Equals(object? obj)
    {
        return obj is SubtitlePlacementCandidate other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Id,
            StringComparer.Ordinal.GetHashCode(RegionName),
            Rect);
    }
}

public sealed class SubtitleProhibitedRegion : IEquatable<SubtitleProhibitedRegion>
{
    public SubtitleProhibitedRegion(string name, NormalizedRect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);

        Name = ContractText.RequireNonBlank(
            name,
            nameof(name),
            "A subtitle prohibited-region name");
        Rect = rect;
    }

    public string Name { get; }

    public NormalizedRect Rect { get; }

    public bool Equals(SubtitleProhibitedRegion? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && Rect == other.Rect;
    }

    public override bool Equals(object? obj)
    {
        return obj is SubtitleProhibitedRegion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(StringComparer.Ordinal.GetHashCode(Name), Rect);
    }
}

public sealed class SubtitleSoftConstraintWeight : IEquatable<SubtitleSoftConstraintWeight>
{
    public SubtitleSoftConstraintWeight(
        SubtitleSoftConstraintKind kind,
        long weight)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown subtitle soft constraint.");
        }

        if (weight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight),
                weight,
                "A subtitle soft-constraint weight must not be negative.");
        }

        Kind = kind;
        Weight = weight;
    }

    public SubtitleSoftConstraintKind Kind { get; }

    public long Weight { get; }

    public bool Equals(SubtitleSoftConstraintWeight? other)
    {
        return other is not null && Kind == other.Kind && Weight == other.Weight;
    }

    public override bool Equals(object? obj)
    {
        return obj is SubtitleSoftConstraintWeight other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Kind, Weight);
    }
}

public sealed class SubtitleLayoutProfile
{
    private readonly IReadOnlyDictionary<SubtitleSoftConstraintKind, long> _weights;

    public SubtitleLayoutProfile(
        EntityId<LayoutSpecIdTag> layoutSpecId,
        ContentDigest versionDigest,
        IReadOnlyList<string> requiredSafeZoneNames,
        IReadOnlyList<SubtitleSoftConstraintWeight> softConstraintWeights)
    {
        ArgumentNullException.ThrowIfNull(layoutSpecId);
        ArgumentNullException.ThrowIfNull(versionDigest);
        ArgumentNullException.ThrowIfNull(requiredSafeZoneNames);
        ArgumentNullException.ThrowIfNull(softConstraintWeights);

        if (requiredSafeZoneNames.Count == 0)
        {
            throw new ArgumentException(
                "A subtitle layout profile must require at least one versioned safe zone.",
                nameof(requiredSafeZoneNames));
        }

        string[] copiedNames = new string[requiredSafeZoneNames.Count];
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copiedNames.Length; index++)
        {
            string? name = requiredSafeZoneNames[index];
            if (name is null)
            {
                throw new ArgumentException(
                    "Required safe-zone names must not contain null values.",
                    nameof(requiredSafeZoneNames));
            }

            copiedNames[index] = ContractText.RequireNonBlank(
                name,
                nameof(requiredSafeZoneNames),
                "A required safe-zone name");
            if (!uniqueNames.Add(copiedNames[index]))
            {
                throw new ArgumentException(
                    "Required safe-zone names must be unique.",
                    nameof(requiredSafeZoneNames));
            }
        }

        SubtitleSoftConstraintWeight[] copiedWeights =
            new SubtitleSoftConstraintWeight[softConstraintWeights.Count];
        var weights = new Dictionary<SubtitleSoftConstraintKind, long>();
        for (int index = 0; index < copiedWeights.Length; index++)
        {
            SubtitleSoftConstraintWeight? weight = softConstraintWeights[index];
            if (weight is null)
            {
                throw new ArgumentException(
                    "Soft-constraint weights must not contain null values.",
                    nameof(softConstraintWeights));
            }

            if (!weights.TryAdd(weight.Kind, weight.Weight))
            {
                throw new ArgumentException(
                    "A subtitle soft constraint must have exactly one weight.",
                    nameof(softConstraintWeights));
            }

            copiedWeights[index] = weight;
        }

        SubtitleSoftConstraintKind[] knownKinds =
            Enum.GetValues<SubtitleSoftConstraintKind>();
        if (weights.Count != knownKinds.Length
            || knownKinds.Any(kind => !weights.ContainsKey(kind)))
        {
            throw new ArgumentException(
                "Every subtitle soft constraint must have exactly one versioned weight.",
                nameof(softConstraintWeights));
        }

        LayoutSpecId = layoutSpecId;
        VersionDigest = versionDigest;
        RequiredSafeZoneNames = Array.AsReadOnly(copiedNames);
        SoftConstraintWeights = Array.AsReadOnly(copiedWeights);
        _weights = weights;
    }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public ContentDigest VersionDigest { get; }

    public IReadOnlyList<string> RequiredSafeZoneNames { get; }

    public IReadOnlyList<SubtitleSoftConstraintWeight> SoftConstraintWeights { get; }

    internal long WeightFor(SubtitleSoftConstraintKind kind)
    {
        return _weights[kind];
    }
}

public sealed class SubtitleLayoutRequest
{
    public SubtitleLayoutRequest(
        SubtitleCueCandidate sourceCandidate,
        LayoutSpec layoutSpec,
        SubtitleLayoutProfile profile,
        IReadOnlyList<SubtitlePlacementCandidate> candidates,
        IReadOnlyList<SubtitleProhibitedRegion> prohibitedRegions,
        IReadOnlyList<NormalizedRect> faceRegions,
        SubtitleLayoutRect? previousPlacement)
    {
        ArgumentNullException.ThrowIfNull(sourceCandidate);
        ArgumentNullException.ThrowIfNull(layoutSpec);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(prohibitedRegions);
        ArgumentNullException.ThrowIfNull(faceRegions);

        if (profile.LayoutSpecId != layoutSpec.Id)
        {
            throw new ArgumentException(
                "The subtitle layout profile must reference the supplied LayoutSpec version.",
                nameof(profile));
        }

        SubtitlePlacementCandidate[] copiedCandidates =
            new SubtitlePlacementCandidate[candidates.Count];
        var candidateIds = new HashSet<EntityId<SubtitlePlacementCandidateIdTag>>();
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            SubtitlePlacementCandidate? candidate = candidates[index];
            if (candidate is null)
            {
                throw new ArgumentException(
                    "Subtitle placement candidates must not contain null values.",
                    nameof(candidates));
            }

            if (!candidateIds.Add(candidate.Id))
            {
                throw new ArgumentException(
                    "Subtitle placement candidate identities must be unique.",
                    nameof(candidates));
            }

            copiedCandidates[index] = candidate;
        }

        SubtitleProhibitedRegion[] copiedProhibitedRegions =
            new SubtitleProhibitedRegion[prohibitedRegions.Count];
        var prohibitedRegionNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < copiedProhibitedRegions.Length; index++)
        {
            SubtitleProhibitedRegion? region = prohibitedRegions[index];
            if (region is null)
            {
                throw new ArgumentException(
                    "Subtitle prohibited regions must not contain null values.",
                    nameof(prohibitedRegions));
            }

            if (!prohibitedRegionNames.Add(region.Name))
            {
                throw new ArgumentException(
                    "Subtitle prohibited-region names must be unique.",
                    nameof(prohibitedRegions));
            }

            copiedProhibitedRegions[index] = region;
        }

        NormalizedRect[] copiedFaceRegions = new NormalizedRect[faceRegions.Count];
        var distinctFaceRegions = new HashSet<NormalizedRect>();
        for (int index = 0; index < copiedFaceRegions.Length; index++)
        {
            NormalizedRect? faceRegion = faceRegions[index];
            if (faceRegion is null)
            {
                throw new ArgumentException(
                    "Optional face regions must not contain null values.",
                    nameof(faceRegions));
            }

            if (!distinctFaceRegions.Add(faceRegion))
            {
                throw new ArgumentException(
                    "Optional face regions must be unique.",
                    nameof(faceRegions));
            }

            copiedFaceRegions[index] = faceRegion;
        }

        SourceCandidate = sourceCandidate;
        LayoutSpec = layoutSpec;
        Profile = profile;
        Candidates = Array.AsReadOnly(copiedCandidates);
        ProhibitedRegions = Array.AsReadOnly(copiedProhibitedRegions);
        FaceRegions = Array.AsReadOnly(copiedFaceRegions);
        PreviousPlacement = previousPlacement;
    }

    public SubtitleCueCandidate SourceCandidate { get; }

    public LayoutSpec LayoutSpec { get; }

    public SubtitleLayoutProfile Profile { get; }

    public IReadOnlyList<SubtitlePlacementCandidate> Candidates { get; }

    public IReadOnlyList<SubtitleProhibitedRegion> ProhibitedRegions { get; }

    public IReadOnlyList<NormalizedRect> FaceRegions { get; }

    public SubtitleLayoutRect? PreviousPlacement { get; }
}

public sealed class SubtitlePlacementEvaluation
{
    internal SubtitlePlacementEvaluation(
        SubtitlePlacementCandidate placement,
        IReadOnlyList<SubtitleHardConstraintKind> hardConstraints,
        IReadOnlyList<SubtitleSoftConstraintKind> softConstraints,
        BigInteger softCost)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (softCost < BigInteger.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(softCost));
        }

        Placement = placement;
        HardConstraints = SubtitleLayoutCollections.CopyCanonicalEnums(
            hardConstraints,
            nameof(hardConstraints));
        SoftConstraints = SubtitleLayoutCollections.CopyCanonicalEnums(
            softConstraints,
            nameof(softConstraints));
        SoftCost = softCost;
    }

    public SubtitlePlacementCandidate Placement { get; }

    public IReadOnlyList<SubtitleHardConstraintKind> HardConstraints { get; }

    public IReadOnlyList<SubtitleSoftConstraintKind> SoftConstraints { get; }

    public BigInteger SoftCost { get; }

    public bool IsHardConstraintSatisfied => HardConstraints.Count == 0;
}

public sealed class SubtitleLayoutPlan
{
    internal SubtitleLayoutPlan(
        SubtitlePlacementCandidate placement,
        IReadOnlyList<SubtitleSoftConstraintKind> softConstraints,
        BigInteger softCost)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (softCost < BigInteger.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(softCost));
        }

        Placement = placement;
        SoftConstraints = SubtitleLayoutCollections.CopyCanonicalEnums(
            softConstraints,
            nameof(softConstraints));
        SoftCost = softCost;
    }

    public SubtitlePlacementCandidate Placement { get; }

    public IReadOnlyList<SubtitleSoftConstraintKind> SoftConstraints { get; }

    public BigInteger SoftCost { get; }
}

public sealed class SubtitleLayoutResolution
{
    internal SubtitleLayoutResolution(
        SubtitleLayoutResolutionStatus status,
        EntityId<SubtitleCueCandidateIdTag> sourceCandidateId,
        EntityId<LayoutSpecIdTag> layoutSpecId,
        ContentDigest profileVersionDigest,
        IReadOnlyList<SubtitleHardConstraintKind> globalHardConstraints,
        IReadOnlyList<SubtitlePlacementEvaluation> evaluations,
        SubtitleLayoutPlan? plan)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown subtitle layout status.");
        }

        ArgumentNullException.ThrowIfNull(sourceCandidateId);
        ArgumentNullException.ThrowIfNull(layoutSpecId);
        ArgumentNullException.ThrowIfNull(profileVersionDigest);
        ArgumentNullException.ThrowIfNull(evaluations);

        bool resolved = status == SubtitleLayoutResolutionStatus.Resolved;
        if (resolved != (plan is not null))
        {
            throw new ArgumentException(
                "Only a resolved subtitle layout may contain a plan.",
                nameof(plan));
        }

        IReadOnlyList<SubtitleHardConstraintKind> copiedGlobalConstraints =
            SubtitleLayoutCollections.CopyCanonicalEnums(
                globalHardConstraints,
                nameof(globalHardConstraints));
        if (resolved && copiedGlobalConstraints.Count != 0)
        {
            throw new ArgumentException(
                "A resolved subtitle layout cannot contain global hard failures.",
                nameof(globalHardConstraints));
        }

        SubtitlePlacementEvaluation[] copiedEvaluations =
            new SubtitlePlacementEvaluation[evaluations.Count];
        var candidateIds = new HashSet<EntityId<SubtitlePlacementCandidateIdTag>>();
        for (int index = 0; index < copiedEvaluations.Length; index++)
        {
            SubtitlePlacementEvaluation? evaluation = evaluations[index];
            if (evaluation is null)
            {
                throw new ArgumentException(
                    "Subtitle placement evaluations must not contain null values.",
                    nameof(evaluations));
            }

            if (!candidateIds.Add(evaluation.Placement.Id))
            {
                throw new ArgumentException(
                    "Subtitle placement evaluations must have unique candidate identities.",
                    nameof(evaluations));
            }

            copiedEvaluations[index] = evaluation;
        }

        if (plan is not null && !copiedEvaluations.Any(evaluation =>
            evaluation.Placement.Equals(plan.Placement)
            && evaluation.IsHardConstraintSatisfied
            && evaluation.SoftCost == plan.SoftCost
            && evaluation.SoftConstraints.SequenceEqual(plan.SoftConstraints)))
        {
            throw new ArgumentException(
                "A subtitle layout plan must match a hard-valid candidate evaluation.",
                nameof(plan));
        }

        Status = status;
        SourceCandidateId = sourceCandidateId;
        LayoutSpecId = layoutSpecId;
        ProfileVersionDigest = profileVersionDigest;
        GlobalHardConstraints = copiedGlobalConstraints;
        Evaluations = Array.AsReadOnly(copiedEvaluations);
        Plan = plan;
    }

    public SubtitleLayoutResolutionStatus Status { get; }

    public EntityId<SubtitleCueCandidateIdTag> SourceCandidateId { get; }

    public EntityId<LayoutSpecIdTag> LayoutSpecId { get; }

    public ContentDigest ProfileVersionDigest { get; }

    public IReadOnlyList<SubtitleHardConstraintKind> GlobalHardConstraints { get; }

    public IReadOnlyList<SubtitlePlacementEvaluation> Evaluations { get; }

    public SubtitleLayoutPlan? Plan { get; }
}

public static class SubtitleLayoutEngine
{
    public static SubtitleLayoutResolution Resolve(
        SubtitleLayoutRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var globalHardConstraints = new List<SubtitleHardConstraintKind>(3);
        if (request.Candidates.Count == 0)
        {
            globalHardConstraints.Add(SubtitleHardConstraintKind.NoPlacementCandidate);
        }

        if (request.SourceCandidate.ManualTimingAdjustmentRequired)
        {
            globalHardConstraints.Add(
                SubtitleHardConstraintKind.SourceTimingAdjustmentRequired);
        }

        SafeZone[] requiredSafeZones = ResolveRequiredSafeZones(
            request,
            out bool requiredSafeZoneMissing,
            cancellationToken);
        if (requiredSafeZoneMissing)
        {
            globalHardConstraints.Add(
                SubtitleHardConstraintKind.RequiredSafeZoneMissing);
        }

        var evaluations = new List<SubtitlePlacementEvaluation>(request.Candidates.Count);
        SubtitlePlacementEvaluation? best = null;
        foreach (SubtitlePlacementCandidate candidate in request.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubtitlePlacementEvaluation evaluation = Evaluate(
                request,
                candidate,
                requiredSafeZones,
                cancellationToken);
            evaluations.Add(evaluation);

            if (globalHardConstraints.Count == 0
                && evaluation.IsHardConstraintSatisfied
                && (best is null || evaluation.SoftCost < best.SoftCost))
            {
                best = evaluation;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        SubtitleLayoutPlan? plan = best is null
            ? null
            : new SubtitleLayoutPlan(
                best.Placement,
                best.SoftConstraints,
                best.SoftCost);
        SubtitleLayoutResolutionStatus status = plan is null
            ? SubtitleLayoutResolutionStatus.ManualAdjustmentRequired
            : SubtitleLayoutResolutionStatus.Resolved;
        return new SubtitleLayoutResolution(
            status,
            request.SourceCandidate.Id,
            request.LayoutSpec.Id,
            request.Profile.VersionDigest,
            globalHardConstraints,
            evaluations,
            plan);
    }

    private static SafeZone[] ResolveRequiredSafeZones(
        SubtitleLayoutRequest request,
        out bool missing,
        CancellationToken cancellationToken)
    {
        var byName = new Dictionary<string, SafeZone>(StringComparer.Ordinal);
        foreach (SafeZone safeZone in request.LayoutSpec.SafeZones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byName.Add(safeZone.Name, safeZone);
        }

        var resolved = new List<SafeZone>(request.Profile.RequiredSafeZoneNames.Count);
        missing = false;
        foreach (string name in request.Profile.RequiredSafeZoneNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (byName.TryGetValue(name, out SafeZone? safeZone))
            {
                resolved.Add(safeZone);
            }
            else
            {
                missing = true;
            }
        }

        return resolved.ToArray();
    }

    private static SubtitlePlacementEvaluation Evaluate(
        SubtitleLayoutRequest request,
        SubtitlePlacementCandidate candidate,
        IReadOnlyList<SafeZone> requiredSafeZones,
        CancellationToken cancellationToken)
    {
        var hardConstraints = new List<SubtitleHardConstraintKind>(5);
        if (!SubtitleLayoutGeometry.IsWithinOutputFrame(candidate.Rect))
        {
            hardConstraints.Add(SubtitleHardConstraintKind.OutsideOutputFrame);
        }

        LayoutRegion? layoutRegion = request.LayoutSpec.Regions.FirstOrDefault(region =>
            string.Equals(region.Name, candidate.RegionName, StringComparison.Ordinal));
        if (layoutRegion is null)
        {
            hardConstraints.Add(SubtitleHardConstraintKind.UnknownLayoutRegion);
        }
        else if (layoutRegion.Role != LayoutRegionRole.Subtitle)
        {
            hardConstraints.Add(SubtitleHardConstraintKind.LayoutRegionIsNotSubtitle);
        }
        else if (!SubtitleLayoutGeometry.Contains(layoutRegion.Rect, candidate.Rect))
        {
            hardConstraints.Add(SubtitleHardConstraintKind.OutsideSubtitleRegion);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (requiredSafeZones.Any(safeZone =>
            !SubtitleLayoutGeometry.Contains(safeZone.Rect, candidate.Rect)))
        {
            hardConstraints.Add(SubtitleHardConstraintKind.OutsideRequiredSafeZone);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProhibitedRegions.Any(region =>
            SubtitleLayoutGeometry.Overlaps(candidate.Rect, region.Rect)))
        {
            hardConstraints.Add(SubtitleHardConstraintKind.ProhibitedRegionOverlap);
        }

        var softConstraints = new List<SubtitleSoftConstraintKind>(4);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.FaceRegions.Any(faceRegion =>
            SubtitleLayoutGeometry.Overlaps(candidate.Rect, faceRegion)))
        {
            softConstraints.Add(SubtitleSoftConstraintKind.FaceRegionOverlap);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.LayoutSpec.Regions.Any(region =>
            region.Role == LayoutRegionRole.Material
            && SubtitleLayoutGeometry.Overlaps(candidate.Rect, region.Rect)))
        {
            softConstraints.Add(SubtitleSoftConstraintKind.MaterialRegionOverlap);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.LayoutSpec.Regions.Any(region =>
            region.Role == LayoutRegionRole.Graphic
            && SubtitleLayoutGeometry.Overlaps(candidate.Rect, region.Rect)))
        {
            softConstraints.Add(SubtitleSoftConstraintKind.GraphicRegionOverlap);
        }

        if (request.PreviousPlacement is not null
            && request.PreviousPlacement != candidate.Rect)
        {
            softConstraints.Add(SubtitleSoftConstraintKind.PlacementChanged);
        }

        BigInteger softCost = BigInteger.Zero;
        foreach (SubtitleSoftConstraintKind constraint in softConstraints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            softCost += request.Profile.WeightFor(constraint);
        }

        return new SubtitlePlacementEvaluation(
            candidate,
            hardConstraints,
            softConstraints,
            softCost);
    }
}

internal static class SubtitleLayoutGeometry
{
    private static readonly RationalNumber One = new(1, 1);

    internal static bool IsWithinOutputFrame(SubtitleLayoutRect rect)
    {
        return rect.X >= default(RationalNumber)
            && rect.Y >= default(RationalNumber)
            && CompareSums(default, One, rect.X, rect.Width) >= 0
            && CompareSums(default, One, rect.Y, rect.Height) >= 0;
    }

    internal static bool Contains(NormalizedRect outer, SubtitleLayoutRect inner)
    {
        return outer.X <= inner.X
            && outer.Y <= inner.Y
            && CompareSums(outer.X, outer.Width, inner.X, inner.Width) >= 0
            && CompareSums(outer.Y, outer.Height, inner.Y, inner.Height) >= 0;
    }

    internal static bool Overlaps(SubtitleLayoutRect left, NormalizedRect right)
    {
        return CompareSums(left.X, left.Width, right.X, default) > 0
            && CompareSums(right.X, right.Width, left.X, default) > 0
            && CompareSums(left.Y, left.Height, right.Y, default) > 0
            && CompareSums(right.Y, right.Height, left.Y, default) > 0;
    }

    private static int CompareSums(
        RationalNumber firstLeft,
        RationalNumber secondLeft,
        RationalNumber firstRight,
        RationalNumber secondRight)
    {
        BigInteger leftNumerator =
            ((BigInteger)firstLeft.Numerator * secondLeft.Denominator)
            + ((BigInteger)secondLeft.Numerator * firstLeft.Denominator);
        BigInteger leftDenominator =
            (BigInteger)firstLeft.Denominator * secondLeft.Denominator;
        BigInteger rightNumerator =
            ((BigInteger)firstRight.Numerator * secondRight.Denominator)
            + ((BigInteger)secondRight.Numerator * firstRight.Denominator);
        BigInteger rightDenominator =
            (BigInteger)firstRight.Denominator * secondRight.Denominator;

        return (leftNumerator * rightDenominator)
            .CompareTo(rightNumerator * leftDenominator);
    }
}

internal static class SubtitleLayoutCollections
{
    internal static IReadOnlyList<TEnum> CopyCanonicalEnums<TEnum>(
        IReadOnlyList<TEnum> values,
        string parameterName)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        TEnum[] copied = new TEnum[values.Count];
        var unique = new HashSet<TEnum>();
        int previousValue = int.MinValue;
        for (int index = 0; index < copied.Length; index++)
        {
            TEnum value = values[index];
            int numericValue = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            if (!Enum.IsDefined(value)
                || !unique.Add(value)
                || numericValue <= previousValue)
            {
                throw new ArgumentException(
                    "Subtitle constraint collections must contain unique known values in canonical order.",
                    parameterName);
            }

            previousValue = numericValue;
            copied[index] = value;
        }

        return Array.AsReadOnly(copied);
    }
}