using HubRadio.Core.Identity;
using HubRadio.Core.Projects;

namespace HubRadio.Core.Jobs;

public sealed class Job
{
    public Job(
        EntityId<JobIdTag> id,
        EntityId<ProjectIdTag> projectId,
        string kind,
        JobState state,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        int currentAttemptNumber)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(kind);

        JobValidation.ValidateRequiredText(kind, nameof(kind), "A job kind");
        JobValidation.ValidateState(state, nameof(state));

        if (updatedAt < createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                updatedAt,
                "A job cannot be updated before it is created.");
        }

        if (currentAttemptNumber < 0 ||
            (state != JobState.Queued && currentAttemptNumber == 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentAttemptNumber),
                currentAttemptNumber,
                "A queued job may have attempt number zero; every other job state requires a positive attempt number.");
        }

        Id = id;
        ProjectId = projectId;
        Kind = kind;
        State = state;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        CurrentAttemptNumber = currentAttemptNumber;
    }

    public EntityId<JobIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public string Kind { get; }

    public JobState State { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public int CurrentAttemptNumber { get; }
}