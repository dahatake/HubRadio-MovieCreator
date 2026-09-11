using System.Text;
using HubRadio.Core.Compositions;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class AutomatedEvaluationStoreTests
{
    private static readonly EntityId<ProjectIdTag> ProjectId =
        EntityId<ProjectIdTag>.Parse("10000000-0000-4000-8000-000000000001");

    private static readonly EntityId<CompositionVersionIdTag> VersionId =
        EntityId<CompositionVersionIdTag>.Parse(
            "20000000-0000-4000-8000-000000000002");

    private static readonly EntityId<CompositionVersionIdTag> OtherVersionId =
        EntityId<CompositionVersionIdTag>.Parse(
            "20000000-0000-4000-8000-000000000003");

    private static readonly EntityId<AutomatedEvaluationArtifactIdTag> ArtifactId =
        EntityId<AutomatedEvaluationArtifactIdTag>.Parse(
            "30000000-0000-4000-8000-000000000003");

    private static readonly EntityId<AutomatedEvaluationArtifactIdTag> UnknownArtifactId =
        EntityId<AutomatedEvaluationArtifactIdTag>.Parse(
            "30000000-0000-4000-8000-000000000099");

    private static readonly EntityId<DefaultSettingsProfileIdTag> SettingsProfileId =
        EntityId<DefaultSettingsProfileIdTag>.Parse(
            "35000000-0000-4000-8000-000000000003");

    private static readonly SettingsProfileVersion SettingsProfileVersion = new(1, 2, 3);
    private static readonly ContentDigest VersionDigest = Digest('a');
    private static readonly ContentDigest OtherVersionDigest = Digest('b');
    private static readonly ContentDigest ArtifactDigest = Digest('c');
    private static readonly ContentDigest SettingsProfileDigest = Digest('d');
    private static readonly ContentDigest SettingsSnapshotDigest = Digest('e');
    private static readonly ContentDigest InputDigest = Digest('f');
    private static readonly ContentDigest EnvironmentDigest = Digest('0');
    private static readonly ContentDigest ToolVersionsDigest = Digest('1');
    private static readonly ContentDigest ModelVersionsDigest = Digest('2');
    private static readonly ContentDigest TemplateDigest = Digest('3');
    private static readonly ContentDigest MusicDigest = Digest('4');
    private static readonly ContentDigest EvidenceDigest = Digest('5');

    private static readonly DateTimeOffset StartedAt =
        new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public void AppendGetAndRestartRoundTripCanonicalRunAndAllNormalizedRows()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        AutomatedEvaluationTarget target = AutomatedEvaluationTarget.Preview(
            ProjectId,
            VersionId,
            VersionDigest,
            AutomatedEvaluationTimeBasis.Output,
            new RationalTime(25, 24),
            ArtifactId,
            ArtifactDigest);
        AutomatedEvaluationRun run = CreateRun(
            EvaluationId("40000000-0000-4000-8000-000000000004"),
            failRule: AutomatedEvaluationRuleId.SubtitleTiming,
            target: target);

        AutomatedEvaluationRun appended = store.Append(run);
        AutomatedEvaluationRun loaded = store.Get(run.EvaluationId)
            ?? throw new AssertFailedException("The automated evaluation was not loaded.");
        AutomatedEvaluationRun restarted = new AutomatedEvaluationStore(database.DatabasePath)
            .Get(run.EvaluationId)
            ?? throw new AssertFailedException(
                "The automated evaluation was not loaded after restart.");

        AssertRunEqual(run, appended);
        AssertRunEqual(run, loaded);
        AssertRunEqual(run, restarted);
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(27L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluation_rules;"));
        Assert.AreEqual(9L, database.ExecuteScalarInt64(
            """
            SELECT COUNT(*)
            FROM automated_evaluation_rules
            WHERE category = 'humanLimitation'
              AND scope = 'humanReview'
              AND status = 'NOT EVALUATED';
            """));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            """
            SELECT COUNT(*)
            FROM automated_evaluation_rules
            WHERE status = 'FAIL'
              AND target_kind = 'preview'
              AND time_basis = 'output'
              AND time_value = 25
              AND time_rate = 24;
            """));
        Assert.AreEqual(SettingsProfileId.ToString(), ReadScalarString(database,
            "SELECT settings_profile_id FROM automated_evaluations;"));
        Assert.AreEqual(EnvironmentDigest.Hex, ReadScalarString(database,
            "SELECT environment_digest FROM automated_evaluations;"));
        Assert.AreEqual(18L, database.ExecuteScalarInt64(
            "SELECT pass_count + fail_count FROM automated_evaluations;"));
        Assert.IsNull(store.Get(EvaluationId(
            "40000000-0000-4000-8000-000000000099")));
    }

    [TestMethod]
    public void AppendSameCanonicalBytesIsIdempotentButDifferentContentConflicts()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        EntityId<AutomatedEvaluationIdTag> id = EvaluationId(
            "41000000-0000-4000-8000-000000000004");
        AutomatedEvaluationRun original = CreateRun(id);
        AutomatedEvaluationRun conflict = CreateRun(
            id,
            environmentVersion: "ffmpeg-8.0.2_windows-x64");

        AssertRunEqual(original, store.Append(original));
        AssertRunEqual(original, store.Append(original));
        AutomatedEvaluationConflictException exception =
            Assert.ThrowsExactly<AutomatedEvaluationConflictException>(
                () => store.Append(conflict));

        Assert.AreEqual(id, exception.EvaluationId);
        Assert.IsFalse(exception.Message.Contains(
            conflict.CanonicalJson,
            StringComparison.Ordinal));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(27L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluation_rules;"));
    }

    [TestMethod]
    public void ArtifactlessPlanStoresUiPassesButAggregateRemainsNotEvaluated()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        const string QuotedEnvironment = "ffmpeg'); DROP TABLE automated_evaluations;--";
        AutomatedEvaluationRun run = CreateRun(
            EvaluationId("42000000-0000-4000-8000-000000000004"),
            includeArtifact: false,
            environmentVersion: QuotedEnvironment);

        AutomatedEvaluationRun stored = store.Append(run);

        Assert.AreEqual(QuotedEnvironment, stored.EnvironmentVersion);
        Assert.IsNull(stored.ArtifactId);
        Assert.IsNull(stored.ArtifactDigest);
        Assert.IsNull(stored.OutputDigest);
        Assert.AreEqual(AutomatedEvaluationStatus.NotEvaluated, stored.AggregateStatus);
        Assert.AreEqual(4, stored.StatusCounts.Pass);
        Assert.AreEqual(23, stored.StatusCounts.NotEvaluated);
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table' AND name = 'automated_evaluations';
            """));
    }

    [TestMethod]
    [DataRow("unknown-id")]
    [DataRow("mismatched-digest")]
    public void UnknownOrMismatchedArtifactBindingRollsBackAtomically(string mutation)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        AutomatedEvaluationRun run = CreateRun(
            EvaluationId("43000000-0000-4000-8000-000000000004"),
            artifactId: mutation == "unknown-id" ? UnknownArtifactId : ArtifactId,
            artifactDigest: mutation == "mismatched-digest" ? Digest('9') : ArtifactDigest);

        Assert.Throws<SqliteException>(() => store.Append(run));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluation_rules;"));
    }

    [TestMethod]
    public void NonRenderExistingArtifactBindingRollsBackAtomically()
    {
        using StoreTestDatabase database = CreateDatabase(artifactKind: "Proxy");
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        AutomatedEvaluationRun run = CreateRun(
            EvaluationId("43000000-0000-4000-8000-000000000005"));

        Assert.Throws<SqliteException>(() => store.Append(run));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluation_rules;"));
    }

    [TestMethod]
    public void VersionForeignKeyAndRuleInsertionFailuresRollBackAtomically()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        AutomatedEvaluationRun unknownVersion = CreateRun(
            EvaluationId("43100000-0000-4000-8000-000000000004"),
            versionId: EntityId<CompositionVersionIdTag>.Parse(
                "99999999-9999-4999-8999-999999999999"),
            versionDigest: Digest('9'));
        Assert.Throws<SqliteException>(() => store.Append(unknownVersion));

        database.ExecuteNonQuery(
            """
            CREATE TRIGGER abort_automated_evaluation_rule
            BEFORE INSERT ON automated_evaluation_rules
            WHEN NEW.rule_id = 'fullDecode'
            BEGIN
                SELECT RAISE(ABORT, 'injected automated evaluation rule failure');
            END;
            """);
        AutomatedEvaluationRun ruleFailure = CreateRun(
            EvaluationId("43200000-0000-4000-8000-000000000004"));
        Assert.Throws<SqliteException>(() => store.Append(ruleFailure));

        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(0L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluation_rules;"));
    }

    [TestMethod]
    public void ListNewestForVersionFiltersExactBindingUsesStableOrderAndBoundsResults()
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        AutomatedEvaluationRun oldest = CreateRun(
            EvaluationId("44000000-0000-4000-8000-000000000004"),
            evaluatedAt: StartedAt.AddSeconds(20));
        AutomatedEvaluationRun sameTimeFirst = CreateRun(
            EvaluationId("44000000-0000-4000-8000-000000000005"),
            evaluatedAt: StartedAt.AddSeconds(30));
        AutomatedEvaluationRun sameTimeSecond = CreateRun(
            EvaluationId("44000000-0000-4000-8000-000000000006"),
            evaluatedAt: StartedAt.AddSeconds(30));
        AutomatedEvaluationRun otherVersion = CreateRun(
            EvaluationId("44000000-0000-4000-8000-000000000007"),
            versionId: OtherVersionId,
            versionDigest: OtherVersionDigest,
            evaluatedAt: StartedAt.AddSeconds(40));
        _ = store.Append(oldest);
        _ = store.Append(sameTimeSecond);
        _ = store.Append(otherVersion);
        _ = store.Append(sameTimeFirst);

        IReadOnlyList<AutomatedEvaluationRun> listed = store.ListNewestForVersion(
            ProjectId,
            VersionId,
            VersionDigest);
        CollectionAssert.AreEqual(
            new[]
            {
                sameTimeFirst.EvaluationId,
                sameTimeSecond.EvaluationId,
                oldest.EvaluationId,
            },
            listed.Select(static run => run.EvaluationId).ToArray());
        CollectionAssert.AreEqual(
            new[] { sameTimeFirst.EvaluationId, sameTimeSecond.EvaluationId },
            store.ListNewestForVersion(
                    ProjectId,
                    VersionId,
                    VersionDigest,
                    maximumCount: 2)
                .Select(static run => run.EvaluationId)
                .ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AutomatedEvaluationRun>)listed).Clear());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            store.ListNewestForVersion(ProjectId, VersionId, VersionDigest, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            store.ListNewestForVersion(
                ProjectId,
                VersionId,
                VersionDigest,
                AutomatedEvaluationStore.MaximumListResults + 1));
    }

    [TestMethod]
    [DataRow("aggregate")]
    [DataRow("counts")]
    [DataRow("audit-digest")]
    [DataRow("canonical-digest")]
    [DataRow("canonical-payload")]
    [DataRow("rule-category")]
    [DataRow("missing-limitation")]
    [DataRow("malformed-utf8")]
    public void GetFailsClosedWhenNormalizedOrCanonicalDataIsCorrupted(string mutation)
    {
        using StoreTestDatabase database = CreateDatabase();
        var store = new AutomatedEvaluationStore(database.DatabasePath);
        AutomatedEvaluationRun run = CreateRun(
            EvaluationId("45000000-0000-4000-8000-000000000004"));
        _ = store.Append(run);

        switch (mutation)
        {
            case "aggregate":
                CorruptIgnoringChecks(
                    database,
                    "UPDATE automated_evaluations SET aggregate_status = 'FAIL';");
                break;
            case "counts":
                CorruptIgnoringChecks(
                    database,
                    """
                    UPDATE automated_evaluations
                    SET pass_count = pass_count - 1,
                        warning_count = warning_count + 1;
                    """);
                break;
            case "audit-digest":
                database.ExecuteNonQuery(
                    "UPDATE automated_evaluations SET environment_digest = $digest;",
                    ("$digest", Digest('9').Hex));
                break;
            case "canonical-digest":
                database.ExecuteNonQuery(
                    "UPDATE automated_evaluations SET canonical_digest = $digest;",
                    ("$digest", Digest('9').Hex));
                break;
            case "canonical-payload":
                database.ExecuteNonQuery(
                    "UPDATE automated_evaluations SET canonical_payload = $payload;",
                    ("$payload", Encoding.UTF8.GetBytes("{}")));
                break;
            case "rule-category":
                CorruptIgnoringChecks(
                    database,
                    "UPDATE automated_evaluation_rules SET category = 'humanLimitation' WHERE rule_id = 'codec';");
                break;
            case "missing-limitation":
                database.ExecuteNonQuery(
                    """
                    DELETE FROM automated_evaluation_rules
                    WHERE rule_id = 'legalCompliance';
                    """);
                break;
            case "malformed-utf8":
                database.ExecuteNonQuery(
                    """
                    UPDATE automated_evaluations
                    SET environment_version = CAST(X'80' AS TEXT);
                    """);
                break;
            default:
                throw new AssertFailedException("Unknown corruption mutation.");
        }

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Get(run.EvaluationId));
        Assert.AreEqual("Stored automated evaluation data is invalid.", exception.Message);
    }

    [TestMethod]
    public async Task ConcurrentIdenticalAppendsAreIdempotent()
    {
        using StoreTestDatabase database = CreateDatabase();
        var firstStore = new AutomatedEvaluationStore(database.DatabasePath);
        var secondStore = new AutomatedEvaluationStore(database.DatabasePath);
        AutomatedEvaluationRun run = CreateRun(
            EvaluationId("46000000-0000-4000-8000-000000000004"));
        using var release = new ManualResetEventSlim(initialState: false);

        Task<AutomatedEvaluationRun> first = Task.Run(() =>
        {
            release.Wait();
            return firstStore.Append(run);
        });
        Task<AutomatedEvaluationRun> second = Task.Run(() =>
        {
            release.Wait();
            return secondStore.Append(run);
        });
        release.Set();
        AutomatedEvaluationRun[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(2, results.Length);
        Assert.IsTrue(results.All(result =>
            string.Equals(result.CanonicalJson, run.CanonicalJson, StringComparison.Ordinal)));
        Assert.AreEqual(1L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluations;"));
        Assert.AreEqual(27L, database.ExecuteScalarInt64(
            "SELECT COUNT(*) FROM automated_evaluation_rules;"));
    }

    [TestMethod]
    public void PublicStoreSurfaceIsAppendOnly()
    {
        string[] publicMethods = typeof(AutomatedEvaluationStore)
            .GetMethods()
            .Where(static method => method.DeclaringType == typeof(AutomatedEvaluationStore))
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(AutomatedEvaluationStore.Append),
                nameof(AutomatedEvaluationStore.Get),
                nameof(AutomatedEvaluationStore.ListNewestForVersion),
            },
            publicMethods);
    }

    private static StoreTestDatabase CreateDatabase(string artifactKind = "Render")
    {
        var database = new StoreTestDatabase();
        try
        {
            database.ExecuteNonQuery(
                """
                INSERT INTO projects (
                    id, revision, publication_scope, deadline, payload, updated_at
                ) VALUES (
                    $id, 0, 'InternalOnly', NULL, X'01', $updated_at
                );
                """,
                ("$id", ProjectId.ToString()),
                ("$updated_at", StartedAt.ToString("O")));
            InsertVersion(database, VersionId, VersionDigest);
            InsertVersion(database, OtherVersionId, OtherVersionDigest);
            database.ExecuteNonQuery(
                """
                INSERT INTO default_settings_profiles (
                    profile_id, profile_version, profile_digest, profile_payload
                ) VALUES (
                    $profile_id, $profile_version, $profile_digest, X'01'
                );
                """,
                ("$profile_id", SettingsProfileId.ToString()),
                ("$profile_version", SettingsProfileVersion.ToString()),
                ("$profile_digest", SettingsProfileDigest.Hex));
            database.ExecuteNonQuery(
                """
                INSERT INTO artifacts (
                    id, project_id, kind, storage_reference,
                    content_digest, byte_size, created_at
                ) VALUES (
                    $id, $project_id, $kind, 'renders/final.mp4',
                    $digest, 1, $created_at
                );
                """,
                ("$id", ArtifactId.ToString()),
                ("$project_id", ProjectId.ToString()),
                ("$kind", artifactKind),
                ("$digest", ArtifactDigest.Hex),
                ("$created_at", StartedAt.ToString("O")));
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static string ReadScalarString(
        StoreTestDatabase database,
        string sql)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string
            ?? throw new AssertFailedException("The scalar value was not text.");
    }

    private static void CorruptIgnoringChecks(
        StoreTestDatabase database,
        string sql)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA ignore_check_constraints = ON;";
        command.ExecuteNonQuery();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InsertVersion(
        StoreTestDatabase database,
        EntityId<CompositionVersionIdTag> versionId,
        ContentDigest digest)
    {
        database.ExecuteNonQuery(
            """
            INSERT INTO composition_versions (
                id, project_id, parent_version_id, content_digest, created_at, payload
            ) VALUES (
                $id, $project_id, NULL, $digest, $created_at, X'01'
            );
            """,
            ("$id", versionId.ToString()),
            ("$project_id", ProjectId.ToString()),
            ("$digest", digest.Hex),
            ("$created_at", StartedAt.ToString("O")));
    }

    private static AutomatedEvaluationRun CreateRun(
        EntityId<AutomatedEvaluationIdTag> evaluationId,
        EntityId<CompositionVersionIdTag>? versionId = null,
        ContentDigest? versionDigest = null,
        bool includeArtifact = true,
        EntityId<AutomatedEvaluationArtifactIdTag>? artifactId = null,
        ContentDigest? artifactDigest = null,
        string environmentVersion = "ffmpeg-8.0.1_windows-x64",
        DateTimeOffset? evaluatedAt = null,
        AutomatedEvaluationRuleId? failRule = null,
        AutomatedEvaluationTarget? target = null)
    {
        AutomatedEvaluationRuleObservation[] observations =
            AutomatedEvaluationRules.Definitions
                .Where(definition =>
                    definition.Category == AutomatedEvaluationRuleCategory.Machine &&
                    (includeArtifact ||
                     definition.Scope == AutomatedEvaluationRuleScope.UserInterface))
                .Select(definition => definition.RuleId == failRule
                    ? AutomatedEvaluationRuleObservation.ObjectiveFail(
                        definition.RuleId,
                        "evaluation.rule_failed",
                        EvidenceDigest,
                        target)
                    : AutomatedEvaluationRuleObservation.ObjectivePass(
                        definition.RuleId,
                        "evaluation.rule_passed",
                        EvidenceDigest))
                .ToArray();
        ContentDigest? selectedArtifactDigest = includeArtifact
            ? artifactDigest ?? ArtifactDigest
            : null;
        var request = new AutomatedEvaluationRequest(
            evaluationId,
            jobId: null,
            ProjectId,
            versionId ?? VersionId,
            versionDigest ?? VersionDigest,
            includeArtifact ? artifactId ?? ArtifactId : null,
            selectedArtifactDigest,
            SettingsProfileId,
            SettingsProfileVersion,
            SettingsProfileDigest,
            SettingsSnapshotDigest,
            new SettingsProfileVersion(2, 0, 0),
            environmentVersion,
            EnvironmentDigest,
            ToolVersionsDigest,
            ModelVersionsDigest,
            TemplateDigest,
            MusicDigest,
            StartedAt,
            StartedAt.AddSeconds(10),
            evaluatedAt ?? StartedAt.AddSeconds(11),
            InputDigest,
            selectedArtifactDigest,
            observations);
        return new AutomatedEvaluationEngine().Evaluate(request);
    }

    private static void AssertRunEqual(
        AutomatedEvaluationRun expected,
        AutomatedEvaluationRun actual)
    {
        Assert.AreEqual(expected.EvaluationId, actual.EvaluationId);
        Assert.AreEqual(expected.CanonicalDigest, actual.CanonicalDigest);
        Assert.AreEqual(expected.CanonicalJson, actual.CanonicalJson);
        Assert.AreEqual(expected.AggregateStatus, actual.AggregateStatus);
        Assert.AreEqual(expected.Rules.Count, actual.Rules.Count);
        Assert.AreEqual(expected.Limitations.Count, actual.Limitations.Count);
        Assert.AreEqual(expected.StatusCounts.Pass, actual.StatusCounts.Pass);
        Assert.AreEqual(
            expected.StatusCounts.NotEvaluated,
            actual.StatusCounts.NotEvaluated);
    }

    private static EntityId<AutomatedEvaluationIdTag> EvaluationId(string value) =>
        EntityId<AutomatedEvaluationIdTag>.Parse(value);

    private static ContentDigest Digest(char character) =>
        new(new string(character, 64));
}
