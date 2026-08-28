using HubRadio.Core.Identity;

namespace HubRadio.Core.Jobs;

public sealed class JobHeartbeat
{
    public JobHeartbeat(
        EntityId<JobHeartbeatIdTag> id,
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> jobAttemptId,
        int sequenceNumber,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(jobAttemptId);

        if (sequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "A job-heartbeat sequence number must be positive.");
        }

        Id = id;
        JobId = jobId;
        JobAttemptId = jobAttemptId;
        SequenceNumber = sequenceNumber;
        RecordedAt = recordedAt;
    }

    public EntityId<JobHeartbeatIdTag> Id { get; }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<JobAttemptIdTag> JobAttemptId { get; }

    public int SequenceNumber { get; }

    public DateTimeOffset RecordedAt { get; }
}