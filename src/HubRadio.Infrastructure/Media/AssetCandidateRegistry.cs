using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Infrastructure.Media;

public sealed class AssetCandidateRegistry
{
    private readonly ConcurrentDictionary<EntityId<AssetIdTag>, CandidateEntry> pendingCandidates = [];

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
            if (pendingCandidates.TryAdd(candidateId, new CandidateEntry(selectedPath)))
            {
                return candidateId;
            }
        }
    }

    public bool IsAvailable(EntityId<AssetIdTag> candidateId)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        return pendingCandidates.TryGetValue(candidateId, out CandidateEntry? entry) &&
            entry.IsAvailable;
    }

    public bool IsClaimable(EntityId<AssetIdTag> candidateId)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        return pendingCandidates.TryGetValue(candidateId, out CandidateEntry? entry) &&
            entry.IsClaimable;
    }

    public bool TryReserve(
        EntityId<AssetIdTag> candidateId,
        [NotNullWhen(true)] out AssetCandidateReservation? reservation)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        reservation = null;
        if (!pendingCandidates.TryGetValue(candidateId, out CandidateEntry? entry))
        {
            return false;
        }

        Guid reservationId = Guid.NewGuid();
        if (!entry.TryReserve(reservationId, out string? selectedPath))
        {
            return false;
        }

        reservation = new AssetCandidateReservation(
            this,
            candidateId,
            reservationId,
            selectedPath);
        return true;
    }

    internal bool TryClaim(
        EntityId<AssetIdTag> candidateId,
        [NotNullWhen(true)] out string? selectedPath)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        selectedPath = null;
        if (!pendingCandidates.TryGetValue(candidateId, out CandidateEntry? entry) ||
            !entry.TryClaim(out selectedPath))
        {
            return false;
        }

        _ = pendingCandidates.TryRemove(
            new KeyValuePair<EntityId<AssetIdTag>, CandidateEntry>(candidateId, entry));
        return true;
    }

    internal int PendingCount => pendingCandidates.Count;

    private void CommitReservation(
        EntityId<AssetIdTag> candidateId,
        Guid reservationId)
    {
        if (!pendingCandidates.TryGetValue(candidateId, out CandidateEntry? entry) ||
            !entry.Commit(reservationId))
        {
            throw new InvalidOperationException(
                "The asset candidate reservation is no longer current.");
        }
    }

    private void DiscardReservation(
        EntityId<AssetIdTag> candidateId,
        Guid reservationId)
    {
        if (!pendingCandidates.TryGetValue(candidateId, out CandidateEntry? entry) ||
            !entry.Discard(reservationId))
        {
            throw new InvalidOperationException(
                "The asset candidate reservation is no longer current.");
        }

        _ = pendingCandidates.TryRemove(
            new KeyValuePair<EntityId<AssetIdTag>, CandidateEntry>(candidateId, entry));
    }

    private void ReleaseReservation(
        EntityId<AssetIdTag> candidateId,
        Guid reservationId)
    {
        if (pendingCandidates.TryGetValue(candidateId, out CandidateEntry? entry))
        {
            entry.Release(reservationId);
        }
    }

    public sealed class AssetCandidateReservation : IDisposable
    {
        private readonly AssetCandidateRegistry registry;
        private readonly EntityId<AssetIdTag> candidateId;
        private readonly Guid reservationId;
        private int state;

        internal AssetCandidateReservation(
            AssetCandidateRegistry registry,
            EntityId<AssetIdTag> candidateId,
            Guid reservationId,
            string selectedPath)
        {
            this.registry = registry;
            this.candidateId = candidateId;
            this.reservationId = reservationId;
            SelectedPath = selectedPath;
        }

        public string SelectedPath { get; }

        public void Commit()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "The asset candidate reservation is already completed.");
            }

            registry.CommitReservation(candidateId, reservationId);
        }

        public void Discard()
        {
            if (Interlocked.CompareExchange(ref state, 3, 0) != 0)
            {
                throw new InvalidOperationException(
                    "The asset candidate reservation is already completed.");
            }

            registry.DiscardReservation(candidateId, reservationId);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                registry.ReleaseReservation(candidateId, reservationId);
            }
        }
    }

    private sealed class CandidateEntry(string selectedPath)
    {
        private readonly object gate = new();
        private Guid? reservationId;
        private bool committedForClaim;
        private bool claimed;

        internal bool IsAvailable
        {
            get
            {
                lock (gate)
                {
                    return !claimed && !committedForClaim && reservationId is null;
                }
            }
        }

        internal bool IsClaimable
        {
            get
            {
                lock (gate)
                {
                    return !claimed && reservationId is null;
                }
            }
        }

        internal bool TryReserve(Guid candidateReservationId, out string path)
        {
            lock (gate)
            {
                path = selectedPath;
                if (claimed || committedForClaim || reservationId is not null)
                {
                    return false;
                }

                reservationId = candidateReservationId;
                return true;
            }
        }

        internal bool TryClaim([NotNullWhen(true)] out string? path)
        {
            lock (gate)
            {
                path = null;
                if (claimed || reservationId is not null)
                {
                    return false;
                }

                claimed = true;
                committedForClaim = false;
                path = selectedPath;
                return true;
            }
        }

        internal bool Commit(Guid candidateReservationId)
        {
            lock (gate)
            {
                if (claimed || reservationId != candidateReservationId)
                {
                    return false;
                }

                committedForClaim = true;
                reservationId = null;
                return true;
            }
        }

        internal bool Discard(Guid candidateReservationId)
        {
            lock (gate)
            {
                if (claimed || reservationId != candidateReservationId)
                {
                    return false;
                }

                claimed = true;
                reservationId = null;
                return true;
            }
        }

        internal void Release(Guid candidateReservationId)
        {
            lock (gate)
            {
                if (!claimed && reservationId == candidateReservationId)
                {
                    reservationId = null;
                }
            }
        }
    }
}