using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ApprovalServiceTests
{
    private const string AdministratorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-4001";
    private const string ApproverSid =
        "S-1-5-21-1111111111-2222222222-3333333333-4002";
    private const string ViewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-4003";

    private static readonly DateTimeOffset VersionTime =
        new(2033, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [TestMethod]
    public void RecordApprovalPinsExactVersionDigestAndRoundTripsSqlUnicodeForEveryRole()
    {
        using ApprovalFixture fixture = new();
        ReviewActorRole[] roles =
        [
            ReviewActorRole.ContentApprover,
            ReviewActorRole.BrandApprover,
            ReviewActorRole.RightsApprover,
        ];
        foreach (ReviewActorRole role in roles)
        {
            fixture.Assign(ApproverSid, ToAuthorizationRole(role));
        }

        const string sqlUnicodeReason =
            "承認: Robert'); DROP TABLE approvals;-- 🎬";
        var recorded = new List<Approval>();
        for (int index = 0; index < roles.Length; index++)
        {
            recorded.Add(fixture.Service.RecordApproval(
                EntityId<ApprovalIdTag>.New(),
                fixture.Version,
                ApproverSid,
                roles[index],
                VersionTime.AddMinutes(index + 1),
                index == 0 ? sqlUnicodeReason : $"Record {roles[index]} approval."));
        }

        IReadOnlyList<StoredApproval> stored = fixture.GetApprovals(fixture.Version);
        Assert.AreEqual(roles.Length, stored.Count);
        foreach (Approval approval in recorded)
        {
            StoredApproval roundTripped = stored.Single(item => item.Id == approval.Id);
            Assert.AreEqual(fixture.Version.ProjectId, approval.ProjectId);
            Assert.AreEqual(fixture.Version.Id, approval.CompositionVersionId);
            Assert.AreEqual(fixture.Version.ContentDigest, approval.CompositionVersionDigest);
            Assert.AreEqual(approval.ProjectId, roundTripped.ProjectId);
            Assert.AreEqual(approval.CompositionVersionId, roundTripped.CompositionVersionId);
            Assert.AreEqual(
                approval.CompositionVersionDigest,
                roundTripped.CompositionVersionDigest);
            Assert.AreEqual(approval.ActorRole, roundTripped.ActorRole);
            Assert.AreEqual(approval.Reason, roundTripped.Reason);
        }

        Assert.AreEqual(sqlUnicodeReason, stored.Single(
            item => item.ActorRole == ReviewActorRole.ContentApprover).Reason);
        Assert.AreEqual(
            1L,
            fixture.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'approvals';"));
    }

    [TestMethod]
    public void RecordApprovalRejectsViewerAdministratorAndNonApproverRolesWithoutWriting()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ViewerSid, AuthorizationRole.Viewer);

        AuthorizationException viewer = Assert.ThrowsExactly<AuthorizationException>(() =>
            fixture.Service.RecordApproval(
                EntityId<ApprovalIdTag>.New(),
                fixture.Version,
                ViewerSid,
                ReviewActorRole.ContentApprover,
                VersionTime.AddMinutes(1),
                "A Viewer cannot approve content."));
        Assert.AreEqual(AuthorizationRole.ContentApprover, viewer.ActingRole);
        Assert.AreEqual(AuthorizationOperation.RecordContentReview, viewer.Operation);

        AuthorizationException administratorBypass =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Service.RecordApproval(
                    EntityId<ApprovalIdTag>.New(),
                    fixture.Version,
                    AdministratorSid,
                    ReviewActorRole.ContentApprover,
                    VersionTime.AddMinutes(2),
                    "Administrator alone cannot approve content."));
        Assert.AreEqual(
            AuthorizationRole.ContentApprover,
            administratorBypass.ActingRole);

        foreach (ReviewActorRole role in new[]
        {
            ReviewActorRole.Editor,
            ReviewActorRole.PerformerReviewer,
            ReviewActorRole.Administrator,
            (ReviewActorRole)int.MaxValue,
        })
        {
            ArgumentOutOfRangeException exception =
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    fixture.Service.RecordApproval(
                        EntityId<ApprovalIdTag>.New(),
                        fixture.Version,
                        AdministratorSid,
                        role,
                        VersionTime.AddMinutes(3),
                        "Only an explicit approver role can approve."));
            Assert.AreEqual("actorRole", exception.ParamName);
        }

        Assert.AreEqual(0L, fixture.ExecuteScalarInt64("SELECT COUNT(*) FROM approvals;"));
    }

    [TestMethod]
    public void DuplicateApprovalIdRollsBackAndPreservesOriginalEvidence()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ApproverSid, AuthorizationRole.ContentApprover);
        EntityId<ApprovalIdTag> approvalId = EntityId<ApprovalIdTag>.New();
        Approval original = fixture.Service.RecordApproval(
            approvalId,
            fixture.Version,
            ApproverSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(1),
            "Original approval reason.");

        SqliteException duplicate = Assert.ThrowsExactly<SqliteException>(() =>
            fixture.Service.RecordApproval(
                approvalId,
                fixture.Version,
                ApproverSid,
                ReviewActorRole.ContentApprover,
                VersionTime.AddMinutes(2),
                "Replacement must not be written."));

        Assert.AreEqual(19, duplicate.SqliteErrorCode);
        StoredApproval stored = fixture.GetApprovals(fixture.Version).Single();
        Assert.AreEqual(original.Id, stored.Id);
        Assert.AreEqual(original.ApprovedAt, stored.ApprovedAt);
        Assert.AreEqual(original.Reason, stored.Reason);
        Assert.AreEqual(1L, fixture.ExecuteScalarInt64("SELECT COUNT(*) FROM approvals;"));
    }

    [TestMethod]
    public void AuthorizedStoreWriteRejectsRoleRevokedAfterServicePrecheck()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ApproverSid, AuthorizationRole.ContentApprover);
        fixture.Authorization.DemandAs(
            ApproverSid,
            AuthorizationRole.ContentApprover,
            AuthorizationOperation.RecordContentReview);
        fixture.Authorization.RevokeRole(
            ApproverSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid);
        var approval = new Approval(
            EntityId<ApprovalIdTag>.New(),
            fixture.Version,
            ApproverSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(1),
            "The role was revoked after the precheck.");

        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            fixture.ReviewStore.AppendAuthorizedApproval(
                approval,
                AuthorizationRole.ContentApprover,
                AuthorizationOperation.RecordContentReview));

        Assert.AreEqual(0L, fixture.ExecuteScalarInt64("SELECT COUNT(*) FROM approvals;"));
    }

    [TestMethod]
    public void ChangedChildAndRollbackAsChildMakeOldApprovalStaleWithoutDeletingIt()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ApproverSid, AuthorizationRole.ContentApprover);
        Approval approval = fixture.Service.RecordApproval(
            EntityId<ApprovalIdTag>.New(),
            fixture.Version,
            ApproverSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(1),
            "Approve the original content digest.");
        IReadOnlyList<StoredApproval> candidates = fixture.GetApprovals(fixture.Version);
        CompositionVersion changedChild = fixture.AppendVersion(
            fixture.Version,
            new ContentDigest(new string('b', 64)),
            "Change subtitle content.");

        ApprovalValidityResult changedResult = fixture.Service.EvaluateValidity(
            changedChild,
            candidates);

        Assert.AreEqual(0, changedResult.ValidApprovals.Count);
        Assert.AreEqual(approval.Id, changedResult.StaleApprovals.Single().Id);

        CompositionVersion rollbackChild = fixture.AppendVersion(
            changedChild,
            fixture.Version.ContentDigest,
            "Rollback as a new child version.");
        ApprovalValidityResult rollbackResult = fixture.Service.EvaluateValidity(
            rollbackChild,
            candidates);

        Assert.AreEqual(0, rollbackResult.ValidApprovals.Count);
        Assert.AreEqual(approval.Id, rollbackResult.StaleApprovals.Single().Id);
        Assert.AreEqual(1, fixture.GetApprovals(fixture.Version).Count);
        Assert.AreEqual(0, fixture.GetApprovals(changedChild).Count);
        Assert.AreEqual(0, fixture.GetApprovals(rollbackChild).Count);
        Assert.AreEqual(1L, fixture.ExecuteScalarInt64("SELECT COUNT(*) FROM approvals;"));
    }

    [TestMethod]
    public void SameVersionTechnicalRerenderKeepsApprovalValid()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ApproverSid, AuthorizationRole.RightsApprover);
        Approval approval = fixture.Service.RecordApproval(
            EntityId<ApprovalIdTag>.New(),
            fixture.Version,
            ApproverSid,
            ReviewActorRole.RightsApprover,
            VersionTime.AddMinutes(1),
            "Rights evidence remains bound to this version.");
        IReadOnlyList<StoredApproval> candidates = fixture.GetApprovals(fixture.Version);

        ApprovalValidityResult result = fixture.Service.EvaluateValidity(
            fixture.Version,
            candidates);

        Assert.AreEqual(approval.Id, result.ValidApprovals.Single().Id);
        Assert.AreEqual(0, result.StaleApprovals.Count);
    }

    [TestMethod]
    public void ValidityResultUsesDefensiveReadOnlyValidAndStaleSnapshots()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ApproverSid, AuthorizationRole.ContentApprover);
        _ = fixture.Service.RecordApproval(
            EntityId<ApprovalIdTag>.New(),
            fixture.Version,
            ApproverSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(1),
            "Approve the parent.");
        CompositionVersion child = fixture.AppendVersion(
            fixture.Version,
            new ContentDigest(new string('b', 64)),
            "Create changed child content.");
        _ = fixture.Service.RecordApproval(
            EntityId<ApprovalIdTag>.New(),
            child,
            ApproverSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(2),
            "Approve the child.");
        var candidates = new List<StoredApproval>();
        candidates.AddRange(fixture.GetApprovals(fixture.Version));
        candidates.AddRange(fixture.GetApprovals(child));

        ApprovalValidityResult result = fixture.Service.EvaluateValidity(
            child,
            candidates);
        candidates.Clear();

        Assert.AreEqual(1, result.ValidApprovals.Count);
        Assert.AreEqual(1, result.StaleApprovals.Count);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredApproval>)result.ValidApprovals).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<StoredApproval>)result.StaleApprovals).Clear());
    }

    [TestMethod]
    public void EvaluateValidityFailsClosedForMissingForgedAndCrossProjectVersions()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ApproverSid, AuthorizationRole.ContentApprover);
        CompositionVersion missing = new(
            EntityId<CompositionVersionIdTag>.New(),
            fixture.Version.ProjectId,
            fixture.Version.Id,
            AdministratorSid,
            VersionTime.AddMinutes(1),
            "Unstored version.",
            new ContentDigest(new string('b', 64)),
            elements: []);
        CompositionVersion forgedDigest = new(
            fixture.Version.Id,
            fixture.Version.ProjectId,
            fixture.Version.ParentVersionId,
            fixture.Version.CreatedByActorId,
            fixture.Version.CreatedAt,
            fixture.Version.ChangeReason,
            new ContentDigest(new string('f', 64)),
            fixture.Version.Elements);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.EvaluateValidity(missing, []));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.EvaluateValidity(forgedDigest, []));

        (_, CompositionVersion otherVersion) = fixture.CreateProjectVersion(
            new ContentDigest(new string('c', 64)),
            "Other approval project");
        _ = fixture.Service.RecordApproval(
            EntityId<ApprovalIdTag>.New(),
            otherVersion,
            ApproverSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(2),
            "Approve another project.");
        IReadOnlyList<StoredApproval> crossProjectCandidates =
            fixture.GetApprovals(otherVersion);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.EvaluateValidity(
                fixture.Version,
                crossProjectCandidates));
    }

    [TestMethod]
    public void EvaluateValidityFailsClosedWhenStoredCandidateMovesAcrossProjectAndVersion()
    {
        using ApprovalFixture fixture = new();
        fixture.Assign(ApproverSid, AuthorizationRole.ContentApprover);
        Approval approval = fixture.Service.RecordApproval(
            EntityId<ApprovalIdTag>.New(),
            fixture.Version,
            ApproverSid,
            ReviewActorRole.ContentApprover,
            VersionTime.AddMinutes(1),
            "Capture candidate evidence before corruption.");
        IReadOnlyList<StoredApproval> candidates = fixture.GetApprovals(fixture.Version);
        CompositionVersion currentChild = fixture.AppendVersion(
            fixture.Version,
            new ContentDigest(new string('b', 64)),
            "Current changed child.");
        (_, CompositionVersion otherVersion) = fixture.CreateProjectVersion(
            new ContentDigest(new string('c', 64)),
            "Corruption target project");
        fixture.MoveApprovalToVersion(approval.Id, otherVersion);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.EvaluateValidity(currentChild, candidates));
    }

    private static AuthorizationRole ToAuthorizationRole(ReviewActorRole actorRole) =>
        actorRole switch
        {
            ReviewActorRole.ContentApprover => AuthorizationRole.ContentApprover,
            ReviewActorRole.BrandApprover => AuthorizationRole.BrandApprover,
            ReviewActorRole.RightsApprover => AuthorizationRole.RightsApprover,
            _ => throw new ArgumentOutOfRangeException(nameof(actorRole)),
        };

    private sealed class ApprovalFixture : IDisposable
    {
        private int _versionSequence;

        internal ApprovalFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.ApprovalServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));

            ProjectStore = new ProjectStore(DatabasePath);
            (Project, Version) = CreateProjectVersion(
                new ContentDigest(new string('a', 64)),
                "Approval service project");
            ReviewStore = new ReviewStore(DatabasePath);
            Authorization = new AuthorizationService(
                new RoleAssignmentStore(DatabasePath));
            Assert.IsTrue(Authorization.BootstrapInitialRoles(AdministratorSid));
            Service = new ApprovalService(ReviewStore, Authorization);
        }

        private string RootPath { get; }

        private string DatabasePath { get; }

        private ProjectStore ProjectStore { get; }

        internal Project Project { get; }

        internal CompositionVersion Version { get; }

        internal ReviewStore ReviewStore { get; }

        internal AuthorizationService Authorization { get; }

        internal ApprovalService Service { get; }

        internal void Assign(string actorSid, AuthorizationRole role)
        {
            Authorization.AssignRole(
                actorSid,
                role,
                AdministratorSid,
                $"Assign {role} for the approval-service test.");
        }

        internal CompositionVersion AppendVersion(
            CompositionVersion parent,
            ContentDigest contentDigest,
            string reason)
        {
            var version = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                parent.ProjectId,
                parent.Id,
                AdministratorSid,
                VersionTime.AddHours(++_versionSequence),
                reason,
                contentDigest,
                elements: []);
            ProjectStore.AppendCompositionVersion(
                version,
                $"{{\"version\":{_versionSequence + 1}}}");
            return version;
        }

        internal (Project Project, CompositionVersion Version) CreateProjectVersion(
            ContentDigest contentDigest,
            string projectName)
        {
            var project = new Project(
                EntityId<ProjectIdTag>.New(),
                projectName,
                "Verify S-010 approval validity.",
                "Approvers",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            ProjectStore.CreateProject(
                project,
                $"{{\"project\":\"{project.Id}\"}}",
                VersionTime);
            var version = new CompositionVersion(
                EntityId<CompositionVersionIdTag>.New(),
                project.Id,
                parentVersionId: null,
                AdministratorSid,
                VersionTime,
                "Create approval candidate.",
                contentDigest,
                elements: []);
            ProjectStore.AppendCompositionVersion(
                version,
                $"{{\"version\":\"{version.Id}\"}}");
            return (project, version);
        }

        internal IReadOnlyList<StoredApproval> GetApprovals(
            CompositionVersion version) =>
            ReviewStore.GetVersionEvidence(version.Id)?.Approvals
            ?? throw new AssertFailedException("The version evidence was not loaded.");

        internal long ExecuteScalarInt64(string sql)
        {
            using SqliteConnection connection = OpenConnection(foreignKeys: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The scalar query returned null."));
        }

        internal void MoveApprovalToVersion(
            EntityId<ApprovalIdTag> approvalId,
            CompositionVersion targetVersion)
        {
            using SqliteConnection connection = OpenConnection(foreignKeys: false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE approvals
                SET
                    project_id = $project_id,
                    composition_version_id = $composition_version_id,
                    composition_version_digest = $composition_version_digest
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$project_id", targetVersion.ProjectId.ToString());
            command.Parameters.AddWithValue(
                "$composition_version_id",
                targetVersion.Id.ToString());
            command.Parameters.AddWithValue(
                "$composition_version_digest",
                targetVersion.ContentDigest.Hex);
            command.Parameters.AddWithValue("$id", approvalId.ToString());
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        private SqliteConnection OpenConnection(bool foreignKeys)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = foreignKeys,
                DefaultTimeout = 5,
            };
            var connection = new SqliteConnection(builder.ToString())
            {
                DefaultTimeout = 5,
            };
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}