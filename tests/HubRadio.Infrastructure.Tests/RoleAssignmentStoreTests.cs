using System.Reflection;
using System.Text;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class RoleAssignmentStoreTests
{
    private const string LastAdministratorRevocationMessage =
        "The last Administrator assignment cannot be revoked.";

    private const string AdministratorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string EditorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string ReviewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1003";
    private const string OtherSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1004";

    private static readonly DateTimeOffset FixedTime =
        new(2035, 4, 5, 6, 7, 8, TimeSpan.FromHours(9));

    [TestMethod]
    public void BootstrapInitialRolesAddsExactlyAdministratorAndEditorOnce()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));

        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Administrator, AuthorizationRole.Editor },
            store.GetRoles(AdministratorSid).ToArray());

        IReadOnlyList<StoredRoleAssignment> assignments = store.GetAssignments();
        Assert.AreEqual(2, assignments.Count);
        Assert.IsTrue(assignments.All(
            static assignment => assignment.ActorSid == AdministratorSid));
        Assert.IsTrue(assignments.All(
            static assignment => assignment.AssignedBySid == AdministratorSid));
        Assert.IsTrue(assignments.All(
            static assignment => assignment.Reason == "Initial local role bootstrap."));
        Assert.IsTrue(assignments.All(
            assignment => assignment.AssignedAt == FixedTime.ToUniversalTime()));
        Assert.IsFalse(assignments.Any(
            static assignment => assignment.Role is
                AuthorizationRole.PerformerReviewer or
                AuthorizationRole.ContentApprover or
                AuthorizationRole.BrandApprover or
                AuthorizationRole.RightsApprover));

        var noTimeStore = new RoleAssignmentStore(
            database.DatabasePath,
            new AlwaysThrowingTimeProvider());
        Assert.IsFalse(noTimeStore.BootstrapInitialRoles(AdministratorSid));
        Assert.AreEqual(2, noTimeStore.GetAssignments().Count);
    }

    [TestMethod]
    public void BootstrapInitialRolesDoesNothingWhenAnyAssignmentAlreadyExists()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        InsertRawAssignment(
            database,
            EditorSid,
            AuthorizationRole.Viewer,
            AdministratorSid,
            "Existing assignment");
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new AlwaysThrowingTimeProvider());

        Assert.IsFalse(store.BootstrapInitialRoles(AdministratorSid));

        IReadOnlyList<StoredRoleAssignment> assignments = store.GetAssignments();
        Assert.AreEqual(1, assignments.Count);
        Assert.AreEqual(EditorSid, assignments[0].ActorSid);
        Assert.AreEqual(AuthorizationRole.Viewer, assignments[0].Role);
    }

    [TestMethod]
    public async Task ParallelBootstrapCreatesOneCompletePairWithoutPartialAssignments()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        RoleAssignmentStore[] stores = Enumerable.Range(0, 8)
            .Select(_ => new RoleAssignmentStore(
                database.DatabasePath,
                new FixedTimeProvider(FixedTime)))
            .ToArray();
        using var start = new ManualResetEventSlim(initialState: false);
        Task<bool>[] attempts = stores
            .Select(store => Task.Run(() =>
            {
                start.Wait();
                return store.BootstrapInitialRoles(AdministratorSid);
            }))
            .ToArray();

        start.Set();
        bool[] results = await Task.WhenAll(attempts);

        Assert.AreEqual(1, results.Count(static applied => applied));
        IReadOnlyList<StoredRoleAssignment> assignments = stores[0].GetAssignments();
        Assert.AreEqual(2, assignments.Count);
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Administrator, AuthorizationRole.Editor },
            assignments.Select(static assignment => assignment.Role).ToArray());
    }

    [TestMethod]
    public void BootstrapFailureRollsBackBothAssignments()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        database.ExecuteNonQuery(
            """
                CREATE TRIGGER fail_editor_bootstrap
                BEFORE INSERT ON role_assignments
                WHEN NEW.role = 'Editor'
                BEGIN
                    SELECT RAISE(ABORT, 'injected bootstrap failure');
                END;
                """);

        _ = Assert.ThrowsExactly<SqliteException>(
            () => store.BootstrapInitialRoles(AdministratorSid));

        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM role_assignments;"));
    }

    [TestMethod]
    public void AssignAndRevokeRequireCurrentAdministratorAndFailOnDuplicateOrMissing()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Assign(
                EditorSid,
                AuthorizationRole.Viewer,
                OtherSid,
                "Unauthorized assignment"));

        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        store.Assign(
            EditorSid,
            AuthorizationRole.Viewer,
            AdministratorSid,
            "Viewer access");
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Viewer },
            store.GetRoles(EditorSid).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Assign(
                EditorSid,
                AuthorizationRole.Viewer,
                AdministratorSid,
                "Duplicate viewer access"));

        store.Revoke(
            EditorSid,
            AuthorizationRole.Viewer,
            AdministratorSid);
        Assert.AreEqual(0, store.GetRoles(EditorSid).Count);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Revoke(
                EditorSid,
                AuthorizationRole.Viewer,
                AdministratorSid));
    }

    [TestMethod]
    public void RevokeRejectsSoleAdministratorWithoutChangingEditorOrAdministrator()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        string expectedSnapshot = ReadRawAssignmentSnapshot(database);

        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                store.Revoke(
                    AdministratorSid,
                    AuthorizationRole.Administrator,
                    AdministratorSid));

        Assert.AreEqual(LastAdministratorRevocationMessage, exception.Message);
        Assert.AreEqual(expectedSnapshot, ReadRawAssignmentSnapshot(database));
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Administrator, AuthorizationRole.Editor },
            store.GetRoles(AdministratorSid).ToArray());
    }

    [TestMethod]
    public void RevokeAllowsOneOfMultipleAdministratorsAndRemainingAdministratorCanManageRoles()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        store.Assign(
            OtherSid,
            AuthorizationRole.Administrator,
            AdministratorSid,
            "Add a second administrator.");

        store.Revoke(
            AdministratorSid,
            AuthorizationRole.Administrator,
            OtherSid);

        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Editor },
            store.GetRoles(AdministratorSid).ToArray());
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Administrator },
            store.GetRoles(OtherSid).ToArray());
        store.Assign(
            ReviewerSid,
            AuthorizationRole.PerformerReviewer,
            OtherSid,
            "The remaining administrator can manage roles.");
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.PerformerReviewer },
            store.GetRoles(ReviewerSid).ToArray());
    }

    [TestMethod]
    public async Task ParallelSelfRevocationOfTwoAdministratorsLeavesExactlyOneAdministrator()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var firstStore = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        var secondStore = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(firstStore.BootstrapInitialRoles(AdministratorSid));
        firstStore.Assign(
            OtherSid,
            AuthorizationRole.Administrator,
            AdministratorSid,
            "Add a second administrator.");
        using var start = new ManualResetEventSlim(initialState: false);

        Task<InvalidOperationException?> firstAttempt = Task.Run(() =>
            CaptureRevokeFailure(
                firstStore,
                AdministratorSid,
                start));
        Task<InvalidOperationException?> secondAttempt = Task.Run(() =>
            CaptureRevokeFailure(
                secondStore,
                OtherSid,
                start));

        start.Set();
        InvalidOperationException?[] outcomes = await Task.WhenAll(
            firstAttempt,
            secondAttempt);

        Assert.AreEqual(1, outcomes.Count(static outcome => outcome is null));
        InvalidOperationException failure = outcomes.Single(
            static outcome => outcome is not null)!;
        Assert.AreEqual(LastAdministratorRevocationMessage, failure.Message);
        Assert.AreEqual(
            1,
            firstStore.GetAssignments().Count(
                static assignment =>
                    assignment.Role == AuthorizationRole.Administrator));
    }

    [TestMethod]
    public void BootstrapDoesNotReapplyAfterNormalPublicRoleMutation()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));

        store.Revoke(
            AdministratorSid,
            AuthorizationRole.Editor,
            AdministratorSid);

        Assert.IsFalse(store.BootstrapInitialRoles(AdministratorSid));
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Administrator },
            store.GetRoles(AdministratorSid).ToArray());
    }

    [TestMethod]
    public void AssignStoresCanonicalUtcTimestampReasonAndActorMetadata()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));

        store.Assign(
            ReviewerSid,
            AuthorizationRole.PerformerReviewer,
            AdministratorSid,
            "Confirm performer details.");

        StoredRoleAssignment assignment = store.GetAssignments().Single(
            static candidate => candidate.ActorSid == ReviewerSid);
        Assert.AreEqual(ReviewerSid, assignment.ActorSid);
        Assert.AreEqual(AuthorizationRole.PerformerReviewer, assignment.Role);
        Assert.AreEqual(FixedTime.ToUniversalTime(), assignment.AssignedAt);
        Assert.AreEqual(TimeSpan.Zero, assignment.AssignedAt.Offset);
        Assert.AreEqual(AdministratorSid, assignment.AssignedBySid);
        Assert.AreEqual("Confirm performer details.", assignment.Reason);

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT assigned_at, reason
            FROM role_assignments
            WHERE actor_sid = $actor_sid AND role = $role;
            """;
        command.Parameters.AddWithValue("$actor_sid", ReviewerSid);
        command.Parameters.AddWithValue(
            "$role",
            AuthorizationRole.PerformerReviewer.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(
            FixedTime.ToUniversalTime().ToString("O"),
            reader.GetString(0));
        Assert.AreEqual("Confirm performer details.", reader.GetString(1));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public void PublicSidArgumentsRejectNullMalformedNonCanonicalOrUntrimmedValues()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(database.DatabasePath);

        ArgumentNullException nullSid = Assert.ThrowsExactly<ArgumentNullException>(
            () => store.GetRoles(null!));
        Assert.AreEqual("actorSid", nullSid.ParamName);

        foreach (string invalidSid in new[]
        {
            string.Empty,
            "not-a-sid",
            " " + EditorSid,
            EditorSid + " ",
            "s" + EditorSid[1..],
        })
        {
            ArgumentException getException = Assert.ThrowsExactly<ArgumentException>(
                () => store.GetRoles(invalidSid));
            Assert.AreEqual("actorSid", getException.ParamName);
        }

        ArgumentException targetException = Assert.ThrowsExactly<ArgumentException>(
            () => store.Assign(
                "not-a-sid",
                AuthorizationRole.Viewer,
                AdministratorSid,
                "Reason"));
        Assert.AreEqual("actorSid", targetException.ParamName);
        ArgumentException assignerException = Assert.ThrowsExactly<ArgumentException>(
            () => store.Assign(
                EditorSid,
                AuthorizationRole.Viewer,
                "not-a-sid",
                "Reason"));
        Assert.AreEqual("assignedBySid", assignerException.ParamName);
        ArgumentException revokerException = Assert.ThrowsExactly<ArgumentException>(
            () => store.Revoke(
                EditorSid,
                AuthorizationRole.Viewer,
                "not-a-sid"));
        Assert.AreEqual("revokedBySid", revokerException.ParamName);
        ArgumentException bootstrapException = Assert.ThrowsExactly<ArgumentException>(
            () => store.BootstrapInitialRoles("not-a-sid"));
        Assert.AreEqual("currentActorSid", bootstrapException.ParamName);
    }

    [TestMethod]
    public void UndefinedRolesAndAssignReasonsFailBeforeMutation()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(database.DatabasePath);

        ArgumentOutOfRangeException assignRole =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => store.Assign(
                    EditorSid,
                    (AuthorizationRole)int.MaxValue,
                    AdministratorSid,
                    "Reason"));
        Assert.AreEqual("role", assignRole.ParamName);
        ArgumentOutOfRangeException revokeRole =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => store.Revoke(
                    EditorSid,
                    (AuthorizationRole)int.MaxValue,
                    AdministratorSid));
        Assert.AreEqual("role", revokeRole.ParamName);

        foreach (string blank in new[]
        {
            string.Empty,
            " ",
            "\t",
            "\r\n",
            " leading",
            "trailing ",
        })
        {
            ArgumentException assignReason = Assert.ThrowsExactly<ArgumentException>(
                () => store.Assign(
                    EditorSid,
                    AuthorizationRole.Viewer,
                    AdministratorSid,
                    blank));
            Assert.AreEqual("reason", assignReason.ParamName);
        }

        ArgumentNullException nullAssignReason =
            Assert.ThrowsExactly<ArgumentNullException>(
                () => store.Assign(
                    EditorSid,
                    AuthorizationRole.Viewer,
                    AdministratorSid,
                    null!));
        Assert.AreEqual("reason", nullAssignReason.ParamName);
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM role_assignments;"));
    }

    [TestMethod]
    public void InvalidUtf16AssignmentReasonsAreRejectedBeforeMutation()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(database.DatabasePath);

        foreach (string invalidReason in new[] { "invalid-\ud800", "invalid-\udc00" })
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
                store.Assign(
                    EditorSid,
                    AuthorizationRole.Viewer,
                    AdministratorSid,
                    invalidReason));
            Assert.AreEqual("reason", exception.ParamName);
        }

        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64("SELECT COUNT(*) FROM role_assignments;"));
    }

    [TestMethod]
    public void UnknownActorReturnsDefensiveEmptyRolesAndCollectionsAreDeterministic()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));

        IReadOnlyList<AuthorizationRole> unknownRoles = store.GetRoles(OtherSid);
        Assert.AreEqual(0, unknownRoles.Count);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AuthorizationRole>)unknownRoles).Add(AuthorizationRole.Viewer));

        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        store.Assign(
            ReviewerSid,
            AuthorizationRole.RightsApprover,
            AdministratorSid,
            "Rights review");
        store.Assign(
            ReviewerSid,
            AuthorizationRole.BrandApprover,
            AdministratorSid,
            "Brand review");
        IReadOnlyList<AuthorizationRole> reviewerRoles = store.GetRoles(ReviewerSid);
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.BrandApprover, AuthorizationRole.RightsApprover },
            reviewerRoles.ToArray());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<AuthorizationRole>)reviewerRoles).Clear());

        IReadOnlyList<StoredRoleAssignment> assignments = store.GetAssignments();
        string[] keys = assignments
            .Select(static assignment => $"{assignment.ActorSid}|{assignment.Role}")
            .ToArray();
        CollectionAssert.AreEqual(
            keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray(),
            keys);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredRoleAssignment>)assignments).Clear());
        Assert.AreEqual(4, store.GetAssignments().Count);
    }

    [TestMethod]
    public void TamperedStoredSidRoleTimestampOrReasonFailsClosed()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(database.DatabasePath);
        InsertRawAssignment(
            database,
            EditorSid,
            AuthorizationRole.Viewer,
            AdministratorSid,
            "Stored assignment");

        string[] mutations =
        [
            "UPDATE role_assignments SET actor_sid = 'not-a-sid';",
            "UPDATE role_assignments SET role = 'UnknownRole';",
            "UPDATE role_assignments SET assigned_at = '2035-04-05T06:07:08.0000000+09:00';",
            "UPDATE role_assignments SET assigned_by_sid = 'not-a-sid';",
            "UPDATE role_assignments SET reason = ' reason';",
        ];
        foreach (string mutation in mutations)
        {
            using (SqliteConnection connection = database.OpenConnection())
            {
                using SqliteCommand ignoreChecks = connection.CreateCommand();
                ignoreChecks.CommandText = "PRAGMA ignore_check_constraints = ON;";
                ignoreChecks.ExecuteNonQuery();
                using SqliteCommand corrupt = connection.CreateCommand();
                corrupt.CommandText = mutation;
                Assert.AreEqual(1, corrupt.ExecuteNonQuery());
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => store.GetAssignments());
            Assert.ThrowsExactly<InvalidOperationException>(
                () => store.GetRoles(EditorSid));

            Assert.ThrowsExactly<InvalidOperationException>(
                () => store.BootstrapInitialRoles(AdministratorSid));
            database.ExecuteNonQuery("DELETE FROM role_assignments;");
            InsertRawAssignment(
                database,
                EditorSid,
                AuthorizationRole.Viewer,
                AdministratorSid,
                "Stored assignment");
        }
    }

    [TestMethod]
    public void TamperedAdministratorMetadataCannotAuthorizeMutation()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        database.ExecuteNonQuery(
            """
                UPDATE role_assignments
                SET assigned_at = 'not-a-timestamp'
                WHERE actor_sid = $actor_sid AND role = 'Administrator';
                """,
            ("$actor_sid", AdministratorSid));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Assign(
                ReviewerSid,
                AuthorizationRole.PerformerReviewer,
                AdministratorSid,
                "A corrupt administrator row must not authorize assignment."));
        Assert.AreEqual(
            0L,
            database.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM role_assignments WHERE actor_sid = $actor_sid;",
                ("$actor_sid", ReviewerSid)));
    }

    [TestMethod]
    public void TamperedTargetMetadataCannotBeRevokedSilently()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        database.ExecuteNonQuery(
            """
                UPDATE role_assignments
                SET assigned_at = 'not-a-timestamp'
                WHERE actor_sid = $actor_sid AND role = 'Editor';
                """,
            ("$actor_sid", AdministratorSid));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Revoke(
                AdministratorSid,
                AuthorizationRole.Editor,
                AdministratorSid));
        Assert.AreEqual(
            1L,
            database.ExecuteScalarInt64(
                """
                    SELECT COUNT(*)
                    FROM role_assignments
                    WHERE actor_sid = $actor_sid AND role = 'Editor';
                    """,
                ("$actor_sid", AdministratorSid)));
    }

    [TestMethod]
    [DataRow("actor_sid")]
    [DataRow("role")]
    [DataRow("assigned_at")]
    [DataRow("assigned_by_sid")]
    [DataRow("reason")]
    public void InvalidUtf8InAnyAdministratorTextColumnFailsClosedWithoutMutation(
        string column)
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        ExecuteUncheckedMutation(
            database,
            FormattableString.Invariant(
                $"""
                    UPDATE role_assignments
                    SET {column} = CAST(X'80' AS TEXT)
                    WHERE actor_sid = $actor_sid AND role = 'Administrator';
                    """),
            ("$actor_sid", AdministratorSid));
        string expectedSnapshot = ReadRawAssignmentSnapshot(database);

        AssertInvalidStoredAssignmentWithoutMutation(
            database,
            expectedSnapshot,
            () => store.GetRoles(AdministratorSid));
        AssertInvalidStoredAssignmentWithoutMutation(
            database,
            expectedSnapshot,
            () => store.GetAssignments());
        AssertInvalidStoredAssignmentWithoutMutation(
            database,
            expectedSnapshot,
            () => store.Assign(
                ReviewerSid,
                AuthorizationRole.PerformerReviewer,
                AdministratorSid,
                "A malformed row must not authorize assignment."));
        AssertInvalidStoredAssignmentWithoutMutation(
            database,
            expectedSnapshot,
            () => store.Revoke(
                AdministratorSid,
                AuthorizationRole.Editor,
                AdministratorSid));
        AssertInvalidStoredAssignmentWithoutMutation(
            database,
            expectedSnapshot,
            () => store.BootstrapInitialRoles(AdministratorSid));
    }

    [TestMethod]
    public void ValidReplacementCharacterJapaneseAndEmojiReasonRoundTrips()
    {
        RequireWindows();
        using StoreTestDatabase database = new();
        var store = new RoleAssignmentStore(
            database.DatabasePath,
            new FixedTimeProvider(FixedTime));
        Assert.IsTrue(store.BootstrapInitialRoles(AdministratorSid));
        const string reason = "日本語の割り当て理由です。🎬 有効な置換文字：\uFFFD";

        store.Assign(
            ReviewerSid,
            AuthorizationRole.PerformerReviewer,
            AdministratorSid,
            reason);

        StoredRoleAssignment assignment = store.GetAssignments().Single(
            static candidate => candidate.ActorSid == ReviewerSid);
        Assert.AreEqual(reason, assignment.Reason);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(reason AS BLOB)
            FROM role_assignments
            WHERE actor_sid = $actor_sid AND role = $role;
            """;
        command.Parameters.AddWithValue("$actor_sid", ReviewerSid);
        command.Parameters.AddWithValue(
            "$role",
            AuthorizationRole.PerformerReviewer.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(reason),
            (byte[])reader.GetValue(0));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public void RoleAssignmentPublicApiIsMinimalAndStoredSnapshotsAreImmutable()
    {
        Assert.IsTrue(typeof(StoredRoleAssignment).IsPublic);
        Assert.IsTrue(typeof(StoredRoleAssignment).IsSealed);
        PropertyInfo[] properties = typeof(StoredRoleAssignment).GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[] { "ActorSid", "Role", "AssignedAt", "AssignedBySid", "Reason" },
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
        Assert.AreEqual(0, typeof(RoleAssignmentStore).GetProperties().Length);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "GetRoles",
                "GetAssignments",
                "Assign",
                "Revoke",
                "BootstrapInitialRoles",
            },
            typeof(RoleAssignmentStore)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        MethodInfo revoke = typeof(RoleAssignmentStore).GetMethod(
            nameof(RoleAssignmentStore.Revoke))
            ?? throw new AssertFailedException("The Revoke API was not found.");
        CollectionAssert.AreEqual(
            new[] { typeof(string), typeof(AuthorizationRole), typeof(string) },
            revoke.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "actorSid", "role", "revokedBySid" },
            revoke.GetParameters()
                .Select(static parameter => parameter.Name)
                .ToArray());
        ConstructorInfo[] publicConstructors = typeof(RoleAssignmentStore).GetConstructors();
        Assert.AreEqual(1, publicConstructors.Length);
        CollectionAssert.AreEqual(
            new[] { typeof(string) },
            publicConstructors[0]
                .GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
    }

    [TestMethod]
    public void RoleAssignmentStoreOnUnsupportedPlatformFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The unsupported-platform path requires a non-Windows runner.");
            return;
        }

        string databasePath = Path.Combine(Path.GetTempPath(), "unused-role-store.db");
        Assert.ThrowsExactly<PlatformNotSupportedException>(
            () => new RoleAssignmentStore(databasePath));
    }

    private static void InsertRawAssignment(
        StoreTestDatabase database,
        string actorSid,
        AuthorizationRole role,
        string assignedBySid,
        string reason)
    {
        database.ExecuteNonQuery(
            """
                INSERT INTO role_assignments (
                    actor_sid,
                    role,
                    assigned_at,
                    assigned_by_sid,
                    reason
                )
                VALUES (
                    $actor_sid,
                    $role,
                    $assigned_at,
                    $assigned_by_sid,
                    $reason
                );
                """,
            ("$actor_sid", actorSid),
            ("$role", role.ToString()),
            ("$assigned_at", FixedTime.ToUniversalTime().ToString("O")),
            ("$assigned_by_sid", assignedBySid),
            ("$reason", reason));
    }

    private static void ExecuteUncheckedMutation(
        StoreTestDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand ignoreChecks = connection.CreateCommand();
        ignoreChecks.CommandText = "PRAGMA ignore_check_constraints = ON;";
        ignoreChecks.ExecuteNonQuery();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.AreEqual(1, command.ExecuteNonQuery());
    }

    private static string ReadRawAssignmentSnapshot(StoreTestDatabase database)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CAST(actor_sid AS BLOB),
                CAST(role AS BLOB),
                CAST(assigned_at AS BLOB),
                CAST(assigned_by_sid AS BLOB),
                CAST(reason AS BLOB)
            FROM role_assignments
            ORDER BY actor_sid COLLATE BINARY, role COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                ':',
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Convert.ToHexString((byte[])reader.GetValue(index)))));
        }

        return string.Join('\n', rows);
    }

    private static void AssertInvalidStoredAssignmentWithoutMutation(
        StoreTestDatabase database,
        string expectedSnapshot,
        Action action)
    {
        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(action);
        Assert.AreEqual("Stored role assignment data is invalid.", exception.Message);
        Assert.AreEqual(expectedSnapshot, ReadRawAssignmentSnapshot(database));
    }

    private static InvalidOperationException? CaptureRevokeFailure(
        RoleAssignmentStore store,
        string actorSid,
        ManualResetEventSlim start)
    {
        start.Wait();
        try
        {
            store.Revoke(actorSid, AuthorizationRole.Administrator, actorSid);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AssertInconclusiveException(
                "Role assignment behavior requires Windows security identifiers.");
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _value;

        public FixedTimeProvider(DateTimeOffset value)
        {
            _value = value;
        }

        public override DateTimeOffset GetUtcNow() => _value;
    }

    private sealed class AlwaysThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("Time must not be read.");
    }
}