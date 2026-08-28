namespace HubRadio.Core.Jobs;

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted,
}

public static class JobStateTransitions
{
    public static bool CanTransition(JobState fromState, JobState toState)
    {
        if (!Enum.IsDefined(fromState) ||
            !Enum.IsDefined(toState) ||
            fromState == toState)
        {
            return false;
        }

        return (fromState, toState) switch
        {
            (JobState.Queued, JobState.Running or JobState.Cancelled) => true,
            (JobState.Running,
                JobState.Succeeded or
                JobState.Failed or
                JobState.Cancelled or
                JobState.Interrupted) => true,
            (JobState.Failed or JobState.Interrupted, JobState.Queued) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(JobState fromState, JobState toState)
    {
        ValidateState(fromState, nameof(fromState));
        ValidateState(toState, nameof(toState));

        if (!CanTransition(fromState, toState))
        {
            throw new InvalidOperationException(
                $"A job cannot transition from {fromState} to {toState}.");
        }
    }

    private static void ValidateState(JobState state, string parameterName)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                state,
                "Unknown job state.");
        }
    }
}

public static class JobAttemptStateTransitions
{
    public static bool CanTransition(JobState fromState, JobState toState)
    {
        if (!Enum.IsDefined(fromState) ||
            !Enum.IsDefined(toState) ||
            fromState == toState)
        {
            return false;
        }

        return (fromState, toState) switch
        {
            (JobState.Queued, JobState.Running or JobState.Cancelled) => true,
            (JobState.Running,
                JobState.Succeeded or
                JobState.Failed or
                JobState.Cancelled or
                JobState.Interrupted) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(JobState fromState, JobState toState)
    {
        ValidateState(fromState, nameof(fromState));
        ValidateState(toState, nameof(toState));

        if (!CanTransition(fromState, toState))
        {
            throw new InvalidOperationException(
                $"A job attempt cannot transition from {fromState} to {toState}.");
        }
    }

    private static void ValidateState(JobState state, string parameterName)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                state,
                "Unknown job state.");
        }
    }
}