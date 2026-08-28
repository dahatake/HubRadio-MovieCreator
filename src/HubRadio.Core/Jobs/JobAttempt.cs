using HubRadio.Core.Identity;

namespace HubRadio.Core.Jobs;

public sealed class JobAttempt
{
    public JobAttempt(
        EntityId<JobAttemptIdTag> id,
        EntityId<JobIdTag> jobId,
        int sequenceNumber,
        JobState state,
        DateTimeOffset queuedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(jobId);
        JobValidation.ValidateState(state, nameof(state));

        if (sequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "A job-attempt sequence number must be positive.");
        }

        if (startedAt is DateTimeOffset started && started < queuedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startedAt),
                startedAt,
                "A job attempt cannot start before it is queued.");
        }

        if (completedAt is DateTimeOffset completed && completed < queuedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                completedAt,
                "A job attempt cannot complete before it is queued.");
        }

        if (startedAt is DateTimeOffset actualStart &&
            completedAt is DateTimeOffset actualCompletion &&
            actualCompletion < actualStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                completedAt,
                "A job attempt cannot complete before it starts.");
        }

        ValidateStateSnapshot(state, startedAt, completedAt, errorCode);

        Id = id;
        JobId = jobId;
        SequenceNumber = sequenceNumber;
        State = state;
        QueuedAt = queuedAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ErrorCode = errorCode;
    }

    public EntityId<JobAttemptIdTag> Id { get; }

    public EntityId<JobIdTag> JobId { get; }

    public int SequenceNumber { get; }

    public JobState State { get; }

    public DateTimeOffset QueuedAt { get; }

    public DateTimeOffset? StartedAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    public string? ErrorCode { get; }

    private static void ValidateStateSnapshot(
        JobState state,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? errorCode)
    {
        switch (state)
        {
            case JobState.Queued:
                RequireAbsent(startedAt, nameof(startedAt), "A queued attempt has not started.");
                RequireAbsent(completedAt, nameof(completedAt), "A queued attempt has not completed.");
                RequireAbsent(errorCode, nameof(errorCode), "A queued attempt has no error code.");
                break;

            case JobState.Running:
                RequirePresent(startedAt, nameof(startedAt));
                RequireAbsent(completedAt, nameof(completedAt), "A running attempt has not completed.");
                RequireAbsent(errorCode, nameof(errorCode), "A running attempt has no error code.");
                break;

            case JobState.Succeeded:
                RequirePresent(startedAt, nameof(startedAt));
                RequirePresent(completedAt, nameof(completedAt));
                RequireAbsent(errorCode, nameof(errorCode), "A successful attempt has no error code.");
                break;

            case JobState.Failed:
                RequirePresent(startedAt, nameof(startedAt));
                RequirePresent(completedAt, nameof(completedAt));
                ValidateRequiredErrorCode(errorCode);
                break;

            case JobState.Cancelled:
                RequirePresent(completedAt, nameof(completedAt));
                RequireAbsent(errorCode, nameof(errorCode), "A cancelled attempt has no error code.");
                break;

            case JobState.Interrupted:
                RequirePresent(startedAt, nameof(startedAt));
                RequirePresent(completedAt, nameof(completedAt));
                ValidateRequiredErrorCode(errorCode);
                break;
        }
    }

    private static void ValidateRequiredErrorCode(string? errorCode)
    {
        if (errorCode is null)
        {
            throw new ArgumentNullException(nameof(errorCode));
        }

        JobValidation.ValidateErrorCode(errorCode, nameof(errorCode));
    }

    private static void RequirePresent<T>(T? value, string parameterName)
        where T : struct
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }
    }

    private static void RequireAbsent<T>(
        T? value,
        string parameterName,
        string message)
        where T : struct
    {
        if (value is not null)
        {
            throw new ArgumentException(message, parameterName);
        }
    }

    private static void RequireAbsent(
        string? value,
        string parameterName,
        string message)
    {
        if (value is not null)
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}