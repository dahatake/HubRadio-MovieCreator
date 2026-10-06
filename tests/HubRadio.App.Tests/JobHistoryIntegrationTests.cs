using System.Text.Json;
using HubRadio.App.Jobs;
using HubRadio.App.Projects;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Diagnostics;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Recovery;

namespace HubRadio.App.Tests;

// FR-049 AC-024: job history written by the real runner, recovery and production composition.
[TestClass]
public sealed class JobHistoryIntegrationTests
{
    private static readonly DateTimeOffset CreatedAt = new(2042, 3, 4, 5, 6, 7, TimeSpan.Zero);

    // FR-049.5 AC-024.3
    [TestMethod]
    public async Task LocalJobRunnerRecordsOnlyTheSanitizedExceptionType()
    {
        using HistoryFixture fixture = new();
        var store = new JobStore(fixture.DatabasePath, new JobHistoryLog(fixture.HistoryPath, "1.0.0.0"));
        var runner = new LocalJobRunner(store, new FixedTimeProvider(CreatedAt.AddSeconds(1)));
        Job failing = fixture.CreateQueuedJob(store, CreatedAt);
        Job succeeding = fixture.CreateQueuedJob(store, CreatedAt.AddSeconds(-1));

        LocalJobRunResult failed = await runner.RunAsync(
            failing.Id,
            (_, _) => throw new IOException(@"C:\Users\someone\video.mp4 is locked"),
            CancellationToken.None);
        LocalJobRunResult succeeded = await runner.RunAsync(
            succeeding.Id,
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.AreEqual(JobState.Failed, failed.TerminalState);
        Assert.AreEqual(JobState.Succeeded, succeeded.TerminalState);
        string failedText = File.ReadAllText(Path.Combine(fixture.HistoryPath, JobHistoryLog.GetFileName(failing)));
        Assert.IsFalse(failedText.Contains("someone", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(failedText.Contains("locked", StringComparison.OrdinalIgnoreCase));
        using (JsonDocument document = JsonDocument.Parse(failedText))
        {
            JsonElement attempt = document.RootElement.GetProperty("attempts")[0];
            Assert.AreEqual("Failed", attempt.GetProperty("state").GetString());
            Assert.AreEqual("job.execution_failed", attempt.GetProperty("errorCode").GetString());
            Assert.AreEqual("IOException", attempt.GetProperty("exceptionType").GetString());
        }

        using JsonDocument success = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixture.HistoryPath, JobHistoryLog.GetFileName(succeeding))));
        JsonElement successAttempt = success.RootElement.GetProperty("attempts")[0];
        Assert.AreEqual("Succeeded", successAttempt.GetProperty("state").GetString());
        Assert.AreEqual(JsonValueKind.Null, successAttempt.GetProperty("exceptionType").ValueKind);
    }

    // FR-049.2 AC-024.8
    [TestMethod]
    public async Task RecoveryInterruptionIsRecorded()
    {
        using HistoryFixture fixture = new();
        var store = new JobStore(fixture.DatabasePath, new JobHistoryLog(fixture.HistoryPath, null));
        Job job = fixture.CreateQueuedJob(store, CreatedAt);
        DateTimeOffset startedAt = CreatedAt.AddSeconds(1);
        _ = store.StartAttempt(
            job.Id,
            0,
            new JobAttempt(EntityId<JobAttemptIdTag>.New(), job.Id, 1, JobState.Running, CreatedAt, startedAt, null, null),
            startedAt);
        string path = Path.Combine(fixture.HistoryPath, JobHistoryLog.GetFileName(job));
        Assert.AreEqual("Running", ReadState(path));

        var recovery = new JobRecovery(
            store,
            new WorkspaceRecovery(fixture.DatabasePath, fixture.WorkspacePath),
            new FixedTimeProvider(CreatedAt.AddMinutes(5)));
        JobRecoveryResult result = await recovery.RecoverAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);

        Assert.AreEqual(1, result.RecoveredJobs.Count);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("Interrupted", document.RootElement.GetProperty("job").GetProperty("state").GetString());
        JsonElement attempt = document.RootElement.GetProperty("attempts")[0];
        Assert.AreEqual("Interrupted", attempt.GetProperty("state").GetString());
        Assert.AreEqual("job.app_restarted", attempt.GetProperty("errorCode").GetString());
    }

    // FR-049.1 FR-049.9 AC-024.7
    [TestMethod]
    public void ProductionServicesWriteUnderTheWorkspaceJobFolder()
    {
        using HistoryFixture fixture = new();
        var services = new SourceOnlyApplicationServices(
            fixture.DatabasePath,
            fixture.WorkspacePath,
            new AssetCandidateRegistry(),
            new DefaultSettingsStore(fixture.DatabasePath));
        JobHistoryLog history = services.Jobs.History
            ?? throw new AssertFailedException("The production job store has no history.");
        Assert.AreEqual(Path.Combine(fixture.WorkspacePath, "job"), history.DirectoryPath);
        Assert.AreEqual(fixture.HistoryPath, history.DirectoryPath);

        Job job = fixture.CreateQueuedJob(services.Jobs, CreatedAt);

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixture.HistoryPath, JobHistoryLog.GetFileName(job))));
        Assert.AreEqual(
            typeof(SourceOnlyApplicationServices).Assembly.GetName().Version!.ToString(),
            document.RootElement.GetProperty("applicationVersion").GetString());
        Assert.AreEqual("Queued", document.RootElement.GetProperty("job").GetProperty("state").GetString());
    }

    private static string? ReadState(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("job").GetProperty("state").GetString();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class HistoryFixture : IDisposable
    {
        internal HistoryFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "HubRadio.JobHistoryTests", Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            DatabasePath = Path.Combine(WorkspacePath, "hubradio.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(WorkspacePath, "backups"));
            ProjectId = EntityId<ProjectIdTag>.New();
            new ProjectStore(DatabasePath).CreateProject(
                new Project(
                    ProjectId,
                    "Job history test project",
                    "Exercise job history.",
                    "Test operators",
                    PublicationScope.InternalOnly,
                    deadline: null,
                    assets: [],
                    performers: []),
                "{\"jobHistory\":true}",
                CreatedAt.AddDays(-1));
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        internal string DatabasePath { get; }

        internal string HistoryPath => Path.Combine(WorkspacePath, "job");

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal Job CreateQueuedJob(JobStore store, DateTimeOffset createdAt)
        {
            var job = new Job(EntityId<JobIdTag>.New(), ProjectId, "history.test", JobState.Queued, createdAt, createdAt, 0);
            store.CreateJob(job);
            return job;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
