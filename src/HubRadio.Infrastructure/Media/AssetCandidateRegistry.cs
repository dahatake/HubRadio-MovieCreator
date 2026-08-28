using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Infrastructure.Media;

public sealed class AssetCandidateRegistry
{
    private readonly ConcurrentDictionary<EntityId<AssetIdTag>, string> pendingCandidates = [];

    public EntityId<AssetIdTag>? RegisterSelection(string? selectedPath)
    {
        if (selectedPath is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            throw new ArgumentException(
                "A selected media path must not be blank.",
                nameof(selectedPath));
        }

        while (true)
        {
            EntityId<AssetIdTag> candidateId = EntityId<AssetIdTag>.New();
            if (pendingCandidates.TryAdd(candidateId, selectedPath))
            {
                return candidateId;
            }
        }
    }

    internal bool TryClaim(
        EntityId<AssetIdTag> candidateId,
        [NotNullWhen(true)] out string? selectedPath)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        return pendingCandidates.TryRemove(candidateId, out selectedPath);
    }

    internal int PendingCount => pendingCandidates.Count;
}