using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Recovery;

namespace HubRadio.Q007.JobRecoveryProbe;

internal static class Program
{
    private const int FileBufferSize = 64 * 1024;
    private const string RestartableJobKind = "q007.production-service-restart";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The job recovery probe requires Windows.");
            }

            if (args.Length == 0)
            {
                throw new ArgumentException("A job recovery probe mode is required.");
            }

            return args[0] switch
            {
                "run-until-killed" => await RunUntilKilledAsync(
                    RequireArgument(args, 1),
                    RequireArgument(args, 2)),
                "recover-and-resume" => await RecoverAndResumeAsync(
                    RequireArgument(args, 1)),
                _ => throw new ArgumentException("The job recovery probe mode is unknown."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"{exception.GetType().Name}: The job recovery probe failed.");
            return 1;
        }
    }

    private static async Task<int> RunUntilKilledAsync(
        string caseRoot,
        string readyPath)
    {
        string normalizedCaseRoot = RequireExistingDirectory(caseRoot);
        string normalizedReadyPath = RequireAbsolutePath(readyPath);
        EnsurePathAbsent(normalizedReadyPath);
        string workspacePath = Path.Combine(normalizedCaseRoot, "workspace");
        string databasePath = Path.Combine(workspacePath, "hubradio.db");
        Directory.CreateDirectory(workspacePath);
        _ = DatabaseMigrator.Migrate(
            databasePath,
            Path.Combine(normalizedCaseRoot, "schema-backups"));

        EntityId<ProjectIdTag> projectId = CreateProject(databasePath);
        StoredArtifact protectedArtifact = await PromoteProtectedArtifactAsync(
            databasePath,
            workspacePath,
            projectId).ConfigureAwait(false);
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        var job = new Job(
            EntityId<JobIdTag>.New(),
            projectId,
            RestartableJobKind,
            JobState.Queued,
            createdAt,
            createdAt,
            currentAttemptNumber: 0);
        var store = new JobStore(databasePath);
        store.CreateJob(job);
        var runner = new LocalJobRunner(store);

        _ = await runner.RunAsync(
            job.Id,
            async (context, _) =>
            {
                DateTimeOffset durableEventAt = DateTimeOffset.UtcNow;
                context.ReportProgress(
                    completed: 1,
                    total: 2,
                    phase: "q007-before-kill",
                    message: null,
                    durableEventAt);
                context.RecordHeartbeat(durableEventAt);
                StoredJobAggregate durable = store.GetJobAggregate(job.Id)
                    ?? throw new InvalidOperationException(
                        "The durable running-job snapshot was not found.");
                JobAttempt durableAttempt = durable.Attempts.Single();
                JobProgress durableProgress = durable.Progress.Single();
                JobHeartbeat durableHeartbeat = durable.Heartbeats.Single();
                var state = new JobHostState(
                    job.Id.ToString(),
                    projectId.ToString(),
                    durableEventAt,
                    new DurableJobSnapshot(
                        durable.Revision,
                        durable.Job.Kind,
                        durable.Job.State.ToString(),
                        durable.Job.CreatedAt,
                        durable.Job.UpdatedAt,
                        durable.Job.CurrentAttemptNumber),
                    new DurableAttemptSnapshot(
                        durableAttempt.Id.ToString(),
                        durableAttempt.JobId.ToString(),
                        durableAttempt.SequenceNumber,
                        durableAttempt.State.ToString(),
                        durableAttempt.QueuedAt,
                        durableAttempt.StartedAt,
                        durableAttempt.CompletedAt,
                        durableAttempt.ErrorCode),
                    new DurableProgressSnapshot(
                        durableProgress.Id.ToString(),
                        durableProgress.JobId.ToString(),
                        durableProgress.JobAttemptId.ToString(),
                        durableProgress.SequenceNumber,
                        durableProgress.RecordedAt,
                        durableProgress.CompletedUnits,
                        durableProgress.TotalUnits,
                        durableProgress.Phase,
                        durableProgress.Message),
                    new DurableHeartbeatSnapshot(
                        durableHeartbeat.Id.ToString(),
                        durableHeartbeat.JobId.ToString(),
                        durableHeartbeat.JobAttemptId.ToString(),
                        durableHeartbeat.SequenceNumber,
                        durableHeartbeat.RecordedAt),
                    new DurableArtifactSnapshot(
                        protectedArtifact.Id.ToString(),
                        protectedArtifact.ProjectId.ToString(),
                        protectedArtifact.Kind.ToString(),
                        protectedArtifact.StorageReference,
                        protectedArtifact.ContentDigest.Hex,
                        protectedArtifact.ByteSize,
                        protectedArtifact.CreatedAt));
                WriteJsonAtomically(
                    Path.Combine(normalizedCaseRoot, "job-state.json"),
                    state);
                WriteJsonAtomically(
                    normalizedReadyPath,
                    new
                    {
                        schemaVersion = "1.0.0",
                        processId = Environment.ProcessId,
                    });
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false);

        return 1;
    }

    private static async Task<int> RecoverAndResumeAsync(string caseRoot)
    {
        string normalizedCaseRoot = RequireExistingDirectory(caseRoot);
        string workspacePath = Path.Combine(normalizedCaseRoot, "workspace");
        string databasePath = Path.Combine(workspacePath, "hubradio.db");
        JobHostState state = ReadJson<JobHostState>(
            Path.Combine(normalizedCaseRoot, "job-state.json"));
        EntityId<ProjectIdTag> projectId = ParseEntityId<ProjectIdTag>(state.ProjectId);
        EntityId<JobIdTag> jobId = ParseEntityId<JobIdTag>(state.JobId);
        EntityId<JobAttemptIdTag> firstAttemptId =
            ParseEntityId<JobAttemptIdTag>(state.Attempt.Id);
        EntityId<ArtifactIdTag> protectedArtifactId =
            ParseEntityId<ArtifactIdTag>(state.ProtectedArtifact.Id);
        ContentDigest protectedDigest = ParseDigest(
            state.ProtectedArtifact.ContentDigest);

        var store = new JobStore(databasePath);
        StoredJobAggregate before = store.GetJobAggregate(jobId)
            ?? throw new InvalidOperationException("The durable job was not found.");
        var workspaceRecovery = new WorkspaceRecovery(databasePath, workspacePath);
        StoredArtifact protectedBefore = await new ArtifactStore(
            databasePath,
            workspacePath).ValidateRegisteredAsync(protectedArtifactId)
            .ConfigureAwait(false);

        var recovery = new JobRecovery(store, workspaceRecovery);
        Stopwatch recoveryElapsed = Stopwatch.StartNew();
        JobRecoveryResult recovered = await recovery.RecoverAsync(
            new HashSet<string>([RestartableJobKind], StringComparer.Ordinal),
            CancellationToken.None).ConfigureAwait(false);
        recoveryElapsed.Stop();

        Stopwatch resumeElapsed = Stopwatch.StartNew();
        LocalJobRunResult resumed = await new LocalJobRunner(store).RunAsync(
            jobId,
            (context, _) =>
            {
                DateTimeOffset durableEventAt = DateTimeOffset.UtcNow;
                context.ReportProgress(
                    completed: 2,
                    total: 2,
                    phase: "q007-after-restart",
                    message: null,
                    durableEventAt);
                context.RecordHeartbeat(durableEventAt);
                return Task.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        resumeElapsed.Stop();

        StoredJobAggregate after = store.GetJobAggregate(jobId)
            ?? throw new InvalidOperationException("The resumed job was not found.");
        WorkspaceRecoveryCheckResult finalCheck = await workspaceRecovery.CheckAsync()
            .ConfigureAwait(false);
        StoredArtifact protectedAfter = await new ArtifactStore(
            databasePath,
            workspacePath).ValidateRegisteredAsync(protectedArtifactId)
            .ConfigureAwait(false);
        RecoveredJob? recoveredJob = recovered.RecoveredJobs.SingleOrDefault();
        JobAttempt? firstAttempt = after.Attempts.Count > 0 ? after.Attempts[0] : null;
        JobAttempt? secondAttempt = after.Attempts.Count > 1 ? after.Attempts[1] : null;
        bool durableRunningStateObserved = DurableJobMatches(
                before.Job,
                before.Revision,
                state.Job) &&
            before.Job.ProjectId == projectId &&
            before.Attempts.Count == 1 &&
            before.Attempts[0].Id == firstAttemptId &&
            DurableAttemptMatches(before.Attempts[0], state.Attempt) &&
            before.Progress.Count == 1 &&
            DurableProgressMatches(before.Progress[0], state.Progress) &&
            before.Heartbeats.Count == 1 &&
            DurableHeartbeatMatches(before.Heartbeats[0], state.Heartbeat) &&
            state.Progress.RecordedAt == state.DurableEventAt &&
            state.Heartbeat.RecordedAt == state.DurableEventAt;
        bool exactRestartTransition = recovered.DatabaseUsable &&
            recovered.BeforeCheck.IsIntact &&
            recovered.BeforeCheck.Issues.Count == 0 &&
            recovered.BeforeCheck.StagingCandidates.Count == 0 &&
            recovered.AfterCheck.IsIntact &&
            recovered.AfterCheck.Issues.Count == 0 &&
            recovered.AfterCheck.StagingCandidates.Count == 0 &&
            recovered.SkippedJobIds.Count == 0 &&
            recoveredJob is not null &&
            recoveredJob.JobId == jobId &&
            recoveredJob.AttemptId == firstAttemptId &&
            recoveredJob.PreviousState == JobState.Running &&
            recoveredJob.CurrentState == JobState.Queued &&
            recoveredJob.AutomaticallyRequeued &&
            recoveredJob.FinalRevision == before.Revision + 2;
        bool priorDurableEvidencePreserved = before.Progress.Count == 1 &&
            before.Heartbeats.Count == 1 &&
            after.Progress.Count == 2 &&
            ProgressMatches(after.Progress[0], before.Progress[0]) &&
            after.Heartbeats.Count == 2 &&
            HeartbeatMatches(after.Heartbeats[0], before.Heartbeats[0]);
        bool exactResume = resumed.TerminalState == JobState.Succeeded &&
            resumed.SequenceNumber == 2 &&
            after.Job.State == JobState.Succeeded &&
            after.Job.Id == before.Job.Id &&
            after.Job.ProjectId == projectId &&
            string.Equals(after.Job.Kind, before.Job.Kind, StringComparison.Ordinal) &&
            after.Job.CreatedAt == before.Job.CreatedAt &&
            after.Job.UpdatedAt >= before.Job.UpdatedAt &&
            after.Job.CurrentAttemptNumber == 2 &&
            after.Revision == before.Revision + 4 &&
            after.Attempts.Count == 2 &&
            firstAttempt?.State == JobState.Interrupted &&
            firstAttempt.JobId == jobId &&
            firstAttempt.SequenceNumber == 1 &&
            firstAttempt.QueuedAt == before.Attempts[0].QueuedAt &&
            firstAttempt.StartedAt == before.Attempts[0].StartedAt &&
            firstAttempt.CompletedAt >= firstAttempt.StartedAt &&
            string.Equals(
                firstAttempt.ErrorCode,
                "job.app_restarted",
                StringComparison.Ordinal) &&
            secondAttempt?.State == JobState.Succeeded &&
            secondAttempt.Id == resumed.AttemptId &&
            secondAttempt.JobId == jobId &&
            secondAttempt.SequenceNumber == 2 &&
            secondAttempt.StartedAt >= secondAttempt.QueuedAt &&
            secondAttempt.CompletedAt >= secondAttempt.StartedAt &&
            secondAttempt.ErrorCode is null &&
            after.Progress.Count == 2 &&
            after.Progress[1].JobId == jobId &&
            after.Progress[1].JobAttemptId == resumed.AttemptId &&
            after.Progress[1].SequenceNumber == 2 &&
            after.Progress[1].CompletedUnits == 2 &&
            after.Progress[1].TotalUnits == 2 &&
            string.Equals(
                after.Progress[1].Phase,
                "q007-after-restart",
                StringComparison.Ordinal) &&
            after.Heartbeats.Count == 2 &&
            after.Heartbeats[1].JobId == jobId &&
            after.Heartbeats[1].JobAttemptId == resumed.AttemptId &&
            after.Heartbeats[1].SequenceNumber == 2 &&
            priorDurableEvidencePreserved;
        bool protectedArtifactUnchanged =
            DurableArtifactMatches(protectedBefore, state.ProtectedArtifact) &&
            ArtifactsMatch(protectedBefore, protectedAfter) &&
            protectedBefore.ProjectId == projectId &&
            protectedBefore.ContentDigest == protectedDigest;
        bool passed = string.Equals(
                typeof(JobRecovery).Assembly.GetName().Name,
                "HubRadio.App",
                StringComparison.Ordinal) &&
            durableRunningStateObserved &&
            exactRestartTransition &&
            exactResume &&
            protectedArtifactUnchanged &&
            finalCheck.DatabaseUsable &&
            finalCheck.IsIntact &&
            finalCheck.Issues.Count == 0 &&
            finalCheck.StagingCandidates.Count == 0;

        WriteResult(new
        {
            schemaVersion = "1.0.0",
            scenario = "production-job-service-kill-restart-resume",
            implementationAssembly = typeof(JobRecovery).Assembly.GetName().Name,
            latestDurableEventAtUtc = state.DurableEventAt.ToUniversalTime(),
            durableRunningStateObserved,
            stateBeforeRestart = before.Job.State.ToString(),
            recoveredState = recoveredJob?.CurrentState.ToString(),
            automaticallyRequeued = recoveredJob?.AutomaticallyRequeued,
            skippedJobCount = recovered.SkippedJobIds.Count,
            finalState = after.Job.State.ToString(),
            attemptCount = after.Attempts.Count,
            progressRecordCount = after.Progress.Count,
            heartbeatCount = after.Heartbeats.Count,
            priorDurableEvidencePreserved,
            firstAttemptInterrupted = firstAttempt?.State == JobState.Interrupted,
            secondAttemptSucceeded = secondAttempt?.State == JobState.Succeeded,
            protectedArtifactUnchanged,
            recoveryMilliseconds = Math.Round(
                recoveryElapsed.Elapsed.TotalMilliseconds,
                3),
            resumeMilliseconds = Math.Round(
                resumeElapsed.Elapsed.TotalMilliseconds,
                3),
            passed,
        });
        return passed ? 0 : 1;
    }

    private static EntityId<ProjectIdTag> CreateProject(string databasePath)
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        var project = new Project(
            projectId,
            "Q-007 job restart project",
            "Exercise durable running-job recovery across fresh processes.",
            "Q-007 test operator",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        new ProjectStore(databasePath).CreateProject(
            project,
            "{\"q007JobRestart\":true}",
            DateTimeOffset.UtcNow);
        return projectId;
    }

    private static bool DurableJobMatches(
        Job actual,
        int actualRevision,
        DurableJobSnapshot expected) =>
        actualRevision == expected.Revision &&
        string.Equals(actual.Kind, expected.Kind, StringComparison.Ordinal) &&
        string.Equals(actual.State.ToString(), expected.State, StringComparison.Ordinal) &&
        actual.CreatedAt == expected.CreatedAt &&
        actual.UpdatedAt == expected.UpdatedAt &&
        actual.CurrentAttemptNumber == expected.CurrentAttemptNumber;

    private static bool DurableAttemptMatches(
        JobAttempt actual,
        DurableAttemptSnapshot expected) =>
        string.Equals(actual.Id.ToString(), expected.Id, StringComparison.Ordinal) &&
        string.Equals(actual.JobId.ToString(), expected.JobId, StringComparison.Ordinal) &&
        actual.SequenceNumber == expected.SequenceNumber &&
        string.Equals(actual.State.ToString(), expected.State, StringComparison.Ordinal) &&
        actual.QueuedAt == expected.QueuedAt &&
        actual.StartedAt == expected.StartedAt &&
        actual.CompletedAt == expected.CompletedAt &&
        string.Equals(actual.ErrorCode, expected.ErrorCode, StringComparison.Ordinal);

    private static bool DurableProgressMatches(
        JobProgress actual,
        DurableProgressSnapshot expected) =>
        string.Equals(actual.Id.ToString(), expected.Id, StringComparison.Ordinal) &&
        string.Equals(actual.JobId.ToString(), expected.JobId, StringComparison.Ordinal) &&
        string.Equals(
            actual.JobAttemptId.ToString(),
            expected.JobAttemptId,
            StringComparison.Ordinal) &&
        actual.SequenceNumber == expected.SequenceNumber &&
        actual.RecordedAt == expected.RecordedAt &&
        actual.CompletedUnits == expected.CompletedUnits &&
        actual.TotalUnits == expected.TotalUnits &&
        string.Equals(actual.Phase, expected.Phase, StringComparison.Ordinal) &&
        string.Equals(actual.Message, expected.Message, StringComparison.Ordinal);

    private static bool DurableHeartbeatMatches(
        JobHeartbeat actual,
        DurableHeartbeatSnapshot expected) =>
        string.Equals(actual.Id.ToString(), expected.Id, StringComparison.Ordinal) &&
        string.Equals(actual.JobId.ToString(), expected.JobId, StringComparison.Ordinal) &&
        string.Equals(
            actual.JobAttemptId.ToString(),
            expected.JobAttemptId,
            StringComparison.Ordinal) &&
        actual.SequenceNumber == expected.SequenceNumber &&
        actual.RecordedAt == expected.RecordedAt;

    private static bool ProgressMatches(JobProgress expected, JobProgress actual) =>
        expected.Id == actual.Id &&
        expected.JobId == actual.JobId &&
        expected.JobAttemptId == actual.JobAttemptId &&
        expected.SequenceNumber == actual.SequenceNumber &&
        expected.RecordedAt == actual.RecordedAt &&
        expected.CompletedUnits == actual.CompletedUnits &&
        expected.TotalUnits == actual.TotalUnits &&
        string.Equals(expected.Phase, actual.Phase, StringComparison.Ordinal) &&
        string.Equals(expected.Message, actual.Message, StringComparison.Ordinal);

    private static bool HeartbeatMatches(JobHeartbeat expected, JobHeartbeat actual) =>
        expected.Id == actual.Id &&
        expected.JobId == actual.JobId &&
        expected.JobAttemptId == actual.JobAttemptId &&
        expected.SequenceNumber == actual.SequenceNumber &&
        expected.RecordedAt == actual.RecordedAt;

    private static bool DurableArtifactMatches(
        StoredArtifact actual,
        DurableArtifactSnapshot expected) =>
        string.Equals(actual.Id.ToString(), expected.Id, StringComparison.Ordinal) &&
        string.Equals(
            actual.ProjectId.ToString(),
            expected.ProjectId,
            StringComparison.Ordinal) &&
        string.Equals(actual.Kind.ToString(), expected.Kind, StringComparison.Ordinal) &&
        string.Equals(
            actual.StorageReference,
            expected.StorageReference,
            StringComparison.Ordinal) &&
        string.Equals(
            actual.ContentDigest.Hex,
            expected.ContentDigest,
            StringComparison.Ordinal) &&
        actual.ByteSize == expected.ByteSize &&
        actual.CreatedAt == expected.CreatedAt;

    private static bool ArtifactsMatch(StoredArtifact expected, StoredArtifact actual) =>
        expected.Id == actual.Id &&
        expected.ProjectId == actual.ProjectId &&
        expected.Kind == actual.Kind &&
        string.Equals(
            expected.StorageReference,
            actual.StorageReference,
            StringComparison.Ordinal) &&
        expected.ContentDigest == actual.ContentDigest &&
        expected.ByteSize == actual.ByteSize &&
        expected.CreatedAt == actual.CreatedAt;

    private static async Task<StoredArtifact> PromoteProtectedArtifactAsync(
        string databasePath,
        string workspacePath,
        EntityId<ProjectIdTag> projectId)
    {
        byte[] content = [0x51, 0x30, 0x30, 0x37, 0x2d, 0x4a, 0x4f, 0x42];
        string stagingPath = Path.Combine(
            workspacePath,
            "projects",
            projectId.ToString(),
            "staging",
            "protected-artifact.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        using (FileStream stream = new(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            FileOptions.WriteThrough | FileOptions.SequentialScan))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        return await new ArtifactStore(databasePath, workspacePath).PromoteAsync(
            new ArtifactPromotionRequest(
                EntityId<ArtifactIdTag>.New(),
                projectId,
                ArtifactKind.Render,
                stagingPath,
                "approved/protected.bin",
                ContentDigest.Compute(content),
                content.LongLength,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    private static T ReadJson<T>(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidDataException("Job recovery probe state is invalid.");
    }

    private static EntityId<TTag> ParseEntityId<TTag>(string value) =>
        EntityId<TTag>.TryParse(value, out EntityId<TTag>? parsed) &&
        string.Equals(value, parsed.ToString(), StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException("Job recovery identity is invalid.");

    private static ContentDigest ParseDigest(string value) =>
        ContentDigest.TryParse(value, out ContentDigest? parsed) &&
        string.Equals(value, parsed.Hex, StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException("Job recovery digest is invalid.");

    private static string RequireExistingDirectory(string path)
    {
        string normalized = RequireAbsolutePath(path);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(
                "A required job recovery directory is unavailable.");
        }

        return normalized;
    }

    private static string RequireAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A job recovery probe path is invalid.");
        }

        return Path.GetFullPath(path);
    }

    private static void EnsurePathAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("A job recovery probe output already exists.");
        }
    }

    private static string RequireArgument(string[] args, int index) =>
        args.Length > index
            ? args[index]
            : throw new ArgumentException("A required job recovery argument is missing.");

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException("A job recovery output directory is unavailable.");
        }

        Directory.CreateDirectory(directory);
        string stagingPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            using (FileStream stream = new(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileBufferSize,
                FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(stagingPath, path, overwrite: false);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    private static void WriteResult<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private sealed record JobHostState(
        string JobId,
        string ProjectId,
        DateTimeOffset DurableEventAt,
        DurableJobSnapshot Job,
        DurableAttemptSnapshot Attempt,
        DurableProgressSnapshot Progress,
        DurableHeartbeatSnapshot Heartbeat,
        DurableArtifactSnapshot ProtectedArtifact);

    private sealed record DurableJobSnapshot(
        int Revision,
        string Kind,
        string State,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int CurrentAttemptNumber);

    private sealed record DurableAttemptSnapshot(
        string Id,
        string JobId,
        int SequenceNumber,
        string State,
        DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        string? ErrorCode);

    private sealed record DurableProgressSnapshot(
        string Id,
        string JobId,
        string JobAttemptId,
        int SequenceNumber,
        DateTimeOffset RecordedAt,
        long CompletedUnits,
        long? TotalUnits,
        string? Phase,
        string? Message);

    private sealed record DurableHeartbeatSnapshot(
        string Id,
        string JobId,
        string JobAttemptId,
        int SequenceNumber,
        DateTimeOffset RecordedAt);

    private sealed record DurableArtifactSnapshot(
        string Id,
        string ProjectId,
        string Kind,
        string StorageReference,
        string ContentDigest,
        long ByteSize,
        DateTimeOffset CreatedAt);
}