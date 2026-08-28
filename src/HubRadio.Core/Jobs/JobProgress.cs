using HubRadio.Core.Identity;

namespace HubRadio.Core.Jobs;

public sealed class JobProgress
{
    public JobProgress(
        EntityId<JobProgressIdTag> id,
        EntityId<JobIdTag> jobId,
        EntityId<JobAttemptIdTag> jobAttemptId,
        int sequenceNumber,
        DateTimeOffset recordedAt,
        long completedUnits,
        long? totalUnits,
        string? phase,
        string? message)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(jobAttemptId);

        if (sequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "A job-progress sequence number must be positive.");
        }

        if (completedUnits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedUnits),
                completedUnits,
                "Completed units must not be negative.");
        }

        if (totalUnits is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalUnits),
                totalUnits,
                "Total units must be positive when supplied.");
        }

        if (totalUnits is long total && completedUnits > total)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedUnits),
                completedUnits,
                "Completed units must not exceed total units.");
        }

        JobValidation.ValidateOptionalText(phase, nameof(phase), "A progress phase");
        JobValidation.ValidateOptionalText(message, nameof(message), "A progress message");

        Id = id;
        JobId = jobId;
        JobAttemptId = jobAttemptId;
        SequenceNumber = sequenceNumber;
        RecordedAt = recordedAt;
        CompletedUnits = completedUnits;
        TotalUnits = totalUnits;
        Phase = phase;
        Message = message;
    }

    public EntityId<JobProgressIdTag> Id { get; }

    public EntityId<JobIdTag> JobId { get; }

    public EntityId<JobAttemptIdTag> JobAttemptId { get; }

    public int SequenceNumber { get; }

    public DateTimeOffset RecordedAt { get; }

    public long CompletedUnits { get; }

    public long? TotalUnits { get; }

    public string? Phase { get; }

    public string? Message { get; }
}