using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Jobs;

public enum ArtifactPlanDisposition
{
    CacheHit,
    RecomputeRequired,
}

public sealed class ArtifactCacheCandidate
{
    public ArtifactCacheCandidate(
        EntityId<ArtifactIdTag> artifactId,
        string operation,
        ArtifactCacheKey cacheKey,
        ContentDigest outputDigest,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(outputDigest);

        ArtifactId = artifactId;
        Operation = ArtifactPlanningContract.RequireOperation(operation, nameof(operation));
        CacheKey = cacheKey;
        OutputDigest = outputDigest;
        CreatedAt = createdAt;
    }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public string Operation { get; }

    public ArtifactCacheKey CacheKey { get; }

    public ContentDigest OutputDigest { get; }

    public DateTimeOffset CreatedAt { get; }
}

public sealed class ArtifactAdoptionSnapshot
{
    public ArtifactAdoptionSnapshot(
        EntityId<ArtifactIdTag> artifactId,
        ContentDigest outputDigest)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(outputDigest);

        ArtifactId = artifactId;
        OutputDigest = outputDigest;
    }

    public EntityId<ArtifactIdTag> ArtifactId { get; }

    public ContentDigest OutputDigest { get; }
}

public sealed class ArtifactAdoptionDifference
{
    internal ArtifactAdoptionDifference(
        EntityId<ArtifactIdTag>? previousArtifactId,
        ContentDigest? previousDigest,
        EntityId<ArtifactIdTag> candidateArtifactId,
        ContentDigest candidateDigest)
    {
        ArgumentNullException.ThrowIfNull(candidateArtifactId);
        ArgumentNullException.ThrowIfNull(candidateDigest);

        if ((previousArtifactId is null) != (previousDigest is null))
        {
            throw new ArgumentException(
                "The previous artifact ID and digest must either both be null or both be provided.",
                nameof(previousArtifactId));
        }

        PreviousArtifactId = previousArtifactId;
        PreviousDigest = previousDigest;
        CandidateArtifactId = candidateArtifactId;
        CandidateDigest = candidateDigest;
    }

    public EntityId<ArtifactIdTag>? PreviousArtifactId { get; }

    public ContentDigest? PreviousDigest { get; }

    public EntityId<ArtifactIdTag> CandidateArtifactId { get; }

    public ContentDigest CandidateDigest { get; }

    public bool HasContentChange =>
        PreviousDigest is null || PreviousDigest != CandidateDigest;
}

public sealed class ArtifactPlan
{
    internal ArtifactPlan(
        string operation,
        ArtifactCacheKey expectedCacheKey,
        ArtifactPlanDisposition disposition,
        ArtifactCacheCandidate? cachedArtifact,
        IReadOnlyList<ArtifactCacheCandidate> staleArtifacts,
        ArtifactAdoptionSnapshot? currentAdoption,
        ArtifactAdoptionDifference? proposedAdoptionDifference)
    {
        ArgumentNullException.ThrowIfNull(expectedCacheKey);
        ArgumentNullException.ThrowIfNull(staleArtifacts);

        string validatedOperation = ArtifactPlanningContract.RequireOperation(
            operation,
            nameof(operation));
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "The artifact plan disposition must be defined.");
        }

        if ((disposition == ArtifactPlanDisposition.CacheHit) !=
            (cachedArtifact is not null))
        {
            throw new ArgumentException(
                "A cache-hit plan must contain exactly one cached artifact.",
                nameof(cachedArtifact));
        }

        if (cachedArtifact is not null &&
            (!string.Equals(
                cachedArtifact.Operation,
                validatedOperation,
                StringComparison.Ordinal) ||
             cachedArtifact.CacheKey != expectedCacheKey))
        {
            throw new ArgumentException(
                "The cached artifact must match the plan operation and cache key.",
                nameof(cachedArtifact));
        }

        if (disposition == ArtifactPlanDisposition.RecomputeRequired &&
            proposedAdoptionDifference is not null)
        {
            throw new ArgumentException(
                "A recompute plan cannot propose an artifact that does not yet exist.",
                nameof(proposedAdoptionDifference));
        }

        var copiedStaleArtifacts = new ArtifactCacheCandidate[staleArtifacts.Count];
        for (int index = 0; index < copiedStaleArtifacts.Length; index++)
        {
            copiedStaleArtifacts[index] = staleArtifacts[index]
                ?? throw new ArgumentException(
                    "Stale artifacts must not contain null values.",
                    nameof(staleArtifacts));
        }

        Operation = validatedOperation;
        ExpectedCacheKey = expectedCacheKey;
        Disposition = disposition;
        CachedArtifact = cachedArtifact;
        StaleArtifacts = Array.AsReadOnly(copiedStaleArtifacts);
        CurrentAdoption = currentAdoption;
        ProposedAdoptionDifference = proposedAdoptionDifference;
    }

    public string Operation { get; }

    public ArtifactCacheKey ExpectedCacheKey { get; }

    public ArtifactPlanDisposition Disposition { get; }

    public ArtifactCacheCandidate? CachedArtifact { get; }

    public IReadOnlyList<ArtifactCacheCandidate> StaleArtifacts { get; }

    public ArtifactAdoptionSnapshot? CurrentAdoption { get; }

    public ArtifactAdoptionDifference? ProposedAdoptionDifference { get; }

    public bool RequiresAdoptionReview =>
        ProposedAdoptionDifference is { HasContentChange: true };
}

public static class ArtifactPlanner
{
    public static ArtifactPlan Plan(
        string operation,
        ArtifactCacheKey expectedCacheKey,
        IReadOnlyList<ArtifactCacheCandidate> existingCandidates,
        ArtifactAdoptionSnapshot? currentAdoption)
    {
        string validatedOperation = ArtifactPlanningContract.RequireOperation(
            operation,
            nameof(operation));
        ArgumentNullException.ThrowIfNull(expectedCacheKey);
        ArgumentNullException.ThrowIfNull(existingCandidates);

        ArtifactCacheCandidate[] candidates = CopyAndValidateCandidates(
            validatedOperation,
            existingCandidates);
        ArtifactCacheCandidate? cachedArtifact = FindUniqueCacheHit(
            expectedCacheKey,
            candidates);
        ArtifactCacheCandidate[] staleArtifacts = candidates
            .Where(candidate =>
                cachedArtifact is null ||
                candidate.ArtifactId != cachedArtifact.ArtifactId)
            .OrderBy(static candidate => candidate.CreatedAt)
            .ThenBy(
                static candidate => candidate.ArtifactId.ToString(),
                StringComparer.Ordinal)
            .ToArray();

        if (cachedArtifact is null)
        {
            return new ArtifactPlan(
                validatedOperation,
                expectedCacheKey,
                ArtifactPlanDisposition.RecomputeRequired,
                cachedArtifact: null,
                staleArtifacts,
                currentAdoption,
                proposedAdoptionDifference: null);
        }

        ArtifactAdoptionDifference? adoptionDifference = BuildAdoptionDifference(
            cachedArtifact,
            currentAdoption);
        return new ArtifactPlan(
            validatedOperation,
            expectedCacheKey,
            ArtifactPlanDisposition.CacheHit,
            cachedArtifact,
            staleArtifacts,
            currentAdoption,
            adoptionDifference);
    }

    private static ArtifactCacheCandidate[] CopyAndValidateCandidates(
        string operation,
        IReadOnlyList<ArtifactCacheCandidate> existingCandidates)
    {
        var copiedCandidates = new ArtifactCacheCandidate[existingCandidates.Count];
        var artifactIds = new HashSet<EntityId<ArtifactIdTag>>();
        for (int index = 0; index < copiedCandidates.Length; index++)
        {
            ArtifactCacheCandidate candidate = existingCandidates[index]
                ?? throw new ArgumentException(
                    "Artifact candidates must not contain null values.",
                    nameof(existingCandidates));
            if (!string.Equals(candidate.Operation, operation, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Every artifact candidate must match the requested operation exactly.",
                    nameof(existingCandidates));
            }

            if (!artifactIds.Add(candidate.ArtifactId))
            {
                throw new ArgumentException(
                    "Artifact candidates must have unique artifact IDs.",
                    nameof(existingCandidates));
            }

            copiedCandidates[index] = candidate;
        }

        return copiedCandidates;
    }

    private static ArtifactCacheCandidate? FindUniqueCacheHit(
        ArtifactCacheKey expectedCacheKey,
        IReadOnlyList<ArtifactCacheCandidate> candidates)
    {
        ArtifactCacheCandidate? cachedArtifact = null;
        foreach (ArtifactCacheCandidate candidate in candidates)
        {
            if (candidate.CacheKey != expectedCacheKey)
            {
                continue;
            }

            if (cachedArtifact is not null)
            {
                throw new InvalidOperationException(
                    "More than one artifact candidate matches the expected cache key.");
            }

            cachedArtifact = candidate;
        }

        return cachedArtifact;
    }

    private static ArtifactAdoptionDifference? BuildAdoptionDifference(
        ArtifactCacheCandidate cachedArtifact,
        ArtifactAdoptionSnapshot? currentAdoption)
    {
        if (currentAdoption is null)
        {
            return new ArtifactAdoptionDifference(
                previousArtifactId: null,
                previousDigest: null,
                cachedArtifact.ArtifactId,
                cachedArtifact.OutputDigest);
        }

        if (currentAdoption.ArtifactId == cachedArtifact.ArtifactId)
        {
            if (currentAdoption.OutputDigest != cachedArtifact.OutputDigest)
            {
                throw new InvalidOperationException(
                    "The currently adopted artifact identity has inconsistent content digests.");
            }

            return null;
        }

        return new ArtifactAdoptionDifference(
            currentAdoption.ArtifactId,
            currentAdoption.OutputDigest,
            cachedArtifact.ArtifactId,
            cachedArtifact.OutputDigest);
    }
}

internal static class ArtifactPlanningContract
{
    internal static string RequireOperation(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An artifact operation must be nonblank without surrounding whitespace.",
                parameterName);
        }

        return value;
    }
}