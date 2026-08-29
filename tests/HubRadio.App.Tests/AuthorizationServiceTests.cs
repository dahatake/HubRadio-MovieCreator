using System.Reflection;
using HubRadio.App.Security;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AuthorizationServiceTests
{
    private const string LastAdministratorRevocationMessage =
        "The last Administrator assignment cannot be revoked.";

    private const string AdministratorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string EditorSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string ReviewerSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1003";
    private const string UnknownSid =
        "S-1-5-21-1111111111-2222222222-3333333333-1004";

    [TestMethod]
    public void BootstrapAddsOnlyAdministratorAndEditorAndIsIdempotent()
    {
        using AuthorizationFixture fixture = new();

        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));
        Assert.IsFalse(fixture.Service.BootstrapInitialRoles(AdministratorSid));

        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Administrator, AuthorizationRole.Editor },
            fixture.Service.GetRoles(AdministratorSid).ToArray());
        Assert.IsFalse(fixture.Service.GetRoles(AdministratorSid).Any(
            static role => role is
                AuthorizationRole.PerformerReviewer or
                AuthorizationRole.ContentApprover or
                AuthorizationRole.BrandApprover or
                AuthorizationRole.RightsApprover));
    }

    [TestMethod]
    public void RevokeRoleRejectsSoleAdministratorThroughAuthorizationService()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));

        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                fixture.Service.RevokeRole(
                    AdministratorSid,
                    AuthorizationRole.Administrator,
                    AdministratorSid));

        Assert.AreEqual(LastAdministratorRevocationMessage, exception.Message);
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.Administrator, AuthorizationRole.Editor },
            fixture.Service.GetRoles(AdministratorSid).ToArray());
    }

    [TestMethod]
    public void CanPerformUsesTheUnionOfAllCurrentAssignments()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));

        Assert.IsTrue(fixture.Service.CanPerform(
            AdministratorSid,
            AuthorizationOperation.CreateProject));
        Assert.IsTrue(fixture.Service.CanPerform(
            AdministratorSid,
            AuthorizationOperation.ManageRoleAssignments));
        Assert.IsFalse(fixture.Service.CanPerform(
            AdministratorSid,
            AuthorizationOperation.RecordContentReview));

        fixture.Service.AssignRole(
            AdministratorSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid,
            "Allow explicit content approval.");

        Assert.IsTrue(fixture.Service.CanPerform(
            AdministratorSid,
            AuthorizationOperation.RecordContentReview));
        Assert.IsTrue(fixture.Service.CanPerform(
            AdministratorSid,
            AuthorizationOperation.RecordPublication));
        Assert.IsFalse(fixture.Service.CanPerform(
            AdministratorSid,
            AuthorizationOperation.RecordBrandReview));
        Assert.IsFalse(fixture.Service.CanPerform(
            UnknownSid,
            AuthorizationOperation.ViewProject));
        Assert.AreEqual(0, fixture.Service.GetRoles(UnknownSid).Count);
    }

    [TestMethod]
    public void DemandAsRequiresTheExactCurrentRoleInsteadOfUnionFallback()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));

        fixture.Service.DemandAs(
            AdministratorSid,
            AuthorizationRole.Editor,
            AuthorizationOperation.CreateProject);
        fixture.Service.DemandAs(
            AdministratorSid,
            AuthorizationRole.Administrator,
            AuthorizationOperation.ManageRoleAssignments);

        AuthorizationException wrongActingRole =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Service.DemandAs(
                    AdministratorSid,
                    AuthorizationRole.Administrator,
                    AuthorizationOperation.CreateProject));
        Assert.AreEqual(
            AuthorizationOperation.CreateProject,
            wrongActingRole.Operation);
        Assert.AreEqual(
            AuthorizationRole.Administrator,
            wrongActingRole.ActingRole);

        AuthorizationException unassignedRole =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Service.DemandAs(
                    AdministratorSid,
                    AuthorizationRole.ContentApprover,
                    AuthorizationOperation.RecordContentReview));
        Assert.AreEqual(
            AuthorizationOperation.RecordContentReview,
            unassignedRole.Operation);
        Assert.AreEqual(
            AuthorizationRole.ContentApprover,
            unassignedRole.ActingRole);

        fixture.Service.AssignRole(
            AdministratorSid,
            AuthorizationRole.ContentApprover,
            AdministratorSid,
            "Allow explicit content approval.");
        fixture.Service.DemandAs(
            AdministratorSid,
            AuthorizationRole.ContentApprover,
            AuthorizationOperation.RecordContentReview);
    }

    [TestMethod]
    public void DemandReturnsStableTypedDenialForUnknownOrUnauthorizedActor()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));

        fixture.Service.Demand(
            AdministratorSid,
            AuthorizationOperation.CreateProject);
        AuthorizationException unknown =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Service.Demand(
                    UnknownSid,
                    AuthorizationOperation.ViewProject));
        Assert.AreEqual(AuthorizationOperation.ViewProject, unknown.Operation);
        Assert.IsNull(unknown.ActingRole);
        Assert.AreEqual(
            "The actor is not authorized to perform the requested operation.",
            unknown.Message);

        AuthorizationException unauthorized =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Service.Demand(
                    AdministratorSid,
                    AuthorizationOperation.RecordRightsDecision));
        Assert.AreEqual(
            AuthorizationOperation.RecordRightsDecision,
            unauthorized.Operation);
        Assert.IsNull(unauthorized.ActingRole);
    }

    [TestMethod]
    public void AdministratorAssignmentAloneHasNoEditingApprovalRenderOrPublicationBypass()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));
        fixture.Service.RevokeRole(
            AdministratorSid,
            AuthorizationRole.Editor,
            AdministratorSid);

        Assert.IsTrue(fixture.Service.CanPerform(
            AdministratorSid,
            AuthorizationOperation.ManageRoleAssignments));
        AuthorizationOperation[] denied =
        [
            AuthorizationOperation.CreateProject,
            AuthorizationOperation.DecideAnalysisSuggestion,
            AuthorizationOperation.CreateCompositionVersion,
            AuthorizationOperation.SubmitForReview,
            AuthorizationOperation.RecordPerformerReview,
            AuthorizationOperation.RecordContentReview,
            AuthorizationOperation.ChangePublicationClassification,
            AuthorizationOperation.RecordBrandReview,
            AuthorizationOperation.RecordRightsDecision,
            AuthorizationOperation.StartApprovedRender,
            AuthorizationOperation.RecordQualityControl,
            AuthorizationOperation.ExportComposition,
            AuthorizationOperation.RecordPublication,
        ];
        foreach (AuthorizationOperation operation in denied)
        {
            Assert.IsFalse(
                fixture.Service.CanPerform(AdministratorSid, operation),
                operation.ToString());
            AuthorizationException exception =
                Assert.ThrowsExactly<AuthorizationException>(
                    () => fixture.Service.Demand(AdministratorSid, operation));
            Assert.AreEqual(operation, exception.Operation);
        }
    }

    [TestMethod]
    public void AssignAndRevokeRoleRequireExplicitCurrentAdministratorAssignment()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));
        fixture.Service.AssignRole(
            EditorSid,
            AuthorizationRole.Editor,
            AdministratorSid,
            "Allow editing.");

        AuthorizationException denied =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Service.AssignRole(
                    ReviewerSid,
                    AuthorizationRole.PerformerReviewer,
                    EditorSid,
                    "An Editor cannot grant roles."));
        Assert.AreEqual(
            AuthorizationOperation.ManageRoleAssignments,
            denied.Operation);
        Assert.AreEqual(0, fixture.Service.GetRoles(ReviewerSid).Count);

        fixture.Service.AssignRole(
            ReviewerSid,
            AuthorizationRole.PerformerReviewer,
            AdministratorSid,
            "Allow performer review.");
        CollectionAssert.AreEqual(
            new[] { AuthorizationRole.PerformerReviewer },
            fixture.Service.GetRoles(ReviewerSid).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.AssignRole(
                ReviewerSid,
                AuthorizationRole.PerformerReviewer,
                AdministratorSid,
                "Duplicate assignment must fail."));

        AuthorizationException deniedRevoke =
            Assert.ThrowsExactly<AuthorizationException>(() =>
                fixture.Service.RevokeRole(
                    ReviewerSid,
                    AuthorizationRole.PerformerReviewer,
                    EditorSid));
        Assert.AreEqual(
            AuthorizationOperation.ManageRoleAssignments,
            deniedRevoke.Operation);
        Assert.AreEqual(1, fixture.Service.GetRoles(ReviewerSid).Count);

        fixture.Service.RevokeRole(
            ReviewerSid,
            AuthorizationRole.PerformerReviewer,
            AdministratorSid);
        Assert.AreEqual(0, fixture.Service.GetRoles(ReviewerSid).Count);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.RevokeRole(
                ReviewerSid,
                AuthorizationRole.PerformerReviewer,
                AdministratorSid));
    }

    [TestMethod]
    public void MalformedActorsAndUndefinedEnumsThrowInsteadOfDenyingSilently()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));

        ArgumentException malformed = Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.Service.CanPerform(
                "not-a-sid",
                AuthorizationOperation.ViewProject));
        Assert.AreEqual("actorSid", malformed.ParamName);
        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.Service.Demand(
                " " + AdministratorSid,
                AuthorizationOperation.ViewProject));
        Assert.ThrowsExactly<ArgumentException>(() =>
            fixture.Service.DemandAs(
                "not-a-sid",
                AuthorizationRole.Editor,
                AuthorizationOperation.CreateProject));

        ArgumentOutOfRangeException canPerformOperation =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.CanPerform(
                    UnknownSid,
                    (AuthorizationOperation)int.MaxValue));
        Assert.AreEqual("operation", canPerformOperation.ParamName);
        ArgumentOutOfRangeException demandOperation =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.Demand(
                    AdministratorSid,
                    (AuthorizationOperation)int.MaxValue));
        Assert.AreEqual("operation", demandOperation.ParamName);
        ArgumentOutOfRangeException actingRole =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.DemandAs(
                    AdministratorSid,
                    (AuthorizationRole)int.MaxValue,
                    AuthorizationOperation.ViewProject));
        Assert.AreEqual("actingRole", actingRole.ParamName);
        ArgumentOutOfRangeException exactOperation =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.DemandAs(
                    AdministratorSid,
                    AuthorizationRole.Viewer,
                    (AuthorizationOperation)int.MaxValue));
        Assert.AreEqual("operation", exactOperation.ParamName);
        ArgumentOutOfRangeException assignedRole =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.AssignRole(
                    EditorSid,
                    (AuthorizationRole)int.MaxValue,
                    AdministratorSid,
                    "Invalid role."));
        Assert.AreEqual("role", assignedRole.ParamName);
        ArgumentOutOfRangeException revokedRole =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                fixture.Service.RevokeRole(
                    EditorSid,
                    (AuthorizationRole)int.MaxValue,
                    AdministratorSid));
        Assert.AreEqual("role", revokedRole.ParamName);
    }

    [TestMethod]
    public void CorruptCurrentAssignmentNeverBecomesAFalseOrAnAuthorizationGrant()
    {
        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));
        fixture.ExecuteNonQuery(
            """
                UPDATE role_assignments
                SET assigned_at = 'not-a-timestamp'
                WHERE actor_sid = $actor_sid AND role = 'Administrator';
                """,
            ("$actor_sid", AdministratorSid));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.CanPerform(
                AdministratorSid,
                AuthorizationOperation.ManageRoleAssignments));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.AssignRole(
                ReviewerSid,
                AuthorizationRole.PerformerReviewer,
                AdministratorSid,
                "Corrupt assignments must fail closed."));
        Assert.AreEqual(
            0L,
            fixture.ExecuteScalarInt64(
                "SELECT COUNT(*) FROM role_assignments WHERE actor_sid = $actor_sid;",
                ("$actor_sid", ReviewerSid)));
    }

    [TestMethod]
    public void PublicSurfaceIsMinimalTypedAndReadOnly()
    {
        Assert.IsTrue(typeof(AuthorizationService).IsPublic);
        Assert.IsTrue(typeof(AuthorizationService).IsSealed);
        Assert.AreEqual(0, typeof(AuthorizationService).GetInterfaces().Length);
        ConstructorInfo serviceConstructor =
            typeof(AuthorizationService).GetConstructors().Single();
        CollectionAssert.AreEqual(
            new[] { typeof(RoleAssignmentStore) },
            serviceConstructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "GetRoles",
                "CanPerform",
                "Demand",
                "DemandAs",
                "AssignRole",
                "RevokeRole",
                "BootstrapInitialRoles",
            },
            typeof(AuthorizationService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        MethodInfo revokeRole = typeof(AuthorizationService).GetMethod(
            nameof(AuthorizationService.RevokeRole))
            ?? throw new AssertFailedException("The RevokeRole API was not found.");
        CollectionAssert.AreEqual(
            new[] { typeof(string), typeof(AuthorizationRole), typeof(string) },
            revokeRole.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "actorSid", "role", "revokedBySid" },
            revokeRole.GetParameters()
                .Select(static parameter => parameter.Name)
                .ToArray());

        Assert.IsTrue(typeof(AuthorizationException).IsPublic);
        Assert.IsTrue(typeof(AuthorizationException).IsSealed);
        Assert.AreEqual(0, typeof(AuthorizationException).GetConstructors().Length);
        PropertyInfo[] properties = typeof(AuthorizationException).GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[] { "Operation", "ActingRole" },
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new AuthorizationService(null!));

        using AuthorizationFixture fixture = new();
        Assert.IsTrue(fixture.Service.BootstrapInitialRoles(AdministratorSid));
        IReadOnlyList<AuthorizationRole> roles =
            fixture.Service.GetRoles(AdministratorSid);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AuthorizationRole>)roles).Clear());
        Assert.AreEqual(2, fixture.Service.GetRoles(AdministratorSid).Count);
    }

    private sealed class AuthorizationFixture : IDisposable
    {
        internal AuthorizationFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.AuthorizationServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            DatabasePath = Path.Combine(RootPath, "store.db");
            _ = DatabaseMigrator.Migrate(
                DatabasePath,
                Path.Combine(RootPath, "backups"));
            Store = new RoleAssignmentStore(DatabasePath);
            Service = new AuthorizationService(Store);
        }

        private string RootPath { get; }

        private string DatabasePath { get; }

        internal RoleAssignmentStore Store { get; }

        internal AuthorizationService Service { get; }

        internal void ExecuteNonQuery(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        internal long ExecuteScalarInt64(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
                DefaultTimeout = 5,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (long)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The scalar query returned null."));
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