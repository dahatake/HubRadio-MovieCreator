using System.Reflection;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class PermissionTests
{
    private static readonly IReadOnlyDictionary<AuthorizationRole, AuthorizationOperation[]>
        AllowedOperations = new Dictionary<AuthorizationRole, AuthorizationOperation[]>
        {
            [AuthorizationRole.Viewer] =
            [
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
            ],
            [AuthorizationRole.Editor] =
            [
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
                AuthorizationOperation.CreateProject,
                AuthorizationOperation.DecideAnalysisSuggestion,
                AuthorizationOperation.CreateCompositionVersion,
                AuthorizationOperation.SubmitForReview,
                AuthorizationOperation.StartApprovedRender,
                AuthorizationOperation.RecordQualityControl,
                AuthorizationOperation.ExportComposition,
            ],
            [AuthorizationRole.PerformerReviewer] =
            [
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
                AuthorizationOperation.RecordPerformerReview,
            ],
            [AuthorizationRole.ContentApprover] =
            [
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
                AuthorizationOperation.RecordContentReview,
                AuthorizationOperation.ChangePublicationClassification,
                AuthorizationOperation.RecordPublication,
            ],
            [AuthorizationRole.BrandApprover] =
            [
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
                AuthorizationOperation.RecordBrandReview,
            ],
            [AuthorizationRole.RightsApprover] =
            [
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
                AuthorizationOperation.RecordRightsDecision,
            ],
            [AuthorizationRole.Administrator] =
            [
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
                AuthorizationOperation.ManageRoleAssignments,
                AuthorizationOperation.ConfigureWorkspaceStorage,
                AuthorizationOperation.PublishTemplateVersion,
                AuthorizationOperation.ViewAuditLog,
            ],
        };

    [TestMethod]
    public void AuthorizationEnumsDefineExactlyTheClosedRequiredValues()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                AuthorizationRole.Viewer,
                AuthorizationRole.Editor,
                AuthorizationRole.PerformerReviewer,
                AuthorizationRole.ContentApprover,
                AuthorizationRole.BrandApprover,
                AuthorizationRole.RightsApprover,
                AuthorizationRole.Administrator,
            },
            Enum.GetValues<AuthorizationRole>());
        CollectionAssert.AreEqual(
            new[]
            {
                AuthorizationOperation.ViewProject,
                AuthorizationOperation.PreviewProject,
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
                AuthorizationOperation.ManageRoleAssignments,
                AuthorizationOperation.ConfigureWorkspaceStorage,
                AuthorizationOperation.PublishTemplateVersion,
                AuthorizationOperation.ViewAuditLog,
            },
            Enum.GetValues<AuthorizationOperation>());
    }

    [TestMethod]
    public void AuthorizationRoleIsSeparateFromReviewActorRole()
    {
        Assert.AreNotEqual(typeof(ReviewActorRole), typeof(AuthorizationRole));
        CollectionAssert.DoesNotContain(
            Enum.GetNames<ReviewActorRole>(),
            AuthorizationRole.Viewer.ToString());
    }

    [TestMethod]
    public void EveryRoleOperationCellMatchesTheExplicitMatrix()
    {
        int allowedCellCount = 0;
        foreach (AuthorizationRole role in Enum.GetValues<AuthorizationRole>())
        {
            HashSet<AuthorizationOperation> expected =
                AllowedOperations[role].ToHashSet();
            foreach (AuthorizationOperation operation in
                Enum.GetValues<AuthorizationOperation>())
            {
                bool actual = PermissionMatrix.CanPerform(role, operation);
                Assert.AreEqual(
                    expected.Contains(operation),
                    actual,
                    $"Unexpected permission cell for {role}/{operation}.");
                if (actual)
                {
                    allowedCellCount++;
                }
            }
        }

        Assert.AreEqual(31, allowedCellCount);
    }

    [TestMethod]
    public void AdministratorHasNoApprovalEditingRenderQcExportOrPublicationBypass()
    {
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

        Assert.IsTrue(PermissionMatrix.CanPerform(
            AuthorizationRole.Administrator,
            AuthorizationOperation.ManageRoleAssignments));
        foreach (AuthorizationOperation operation in denied)
        {
            Assert.IsFalse(PermissionMatrix.CanPerform(
                AuthorizationRole.Administrator,
                operation), operation.ToString());
        }
    }

    [TestMethod]
    public void ApprovalOperationsAreLimitedToTheirCorrespondingApprovers()
    {
        (AuthorizationOperation Operation, AuthorizationRole Role)[] approvals =
        [
            (AuthorizationOperation.RecordPerformerReview,
                AuthorizationRole.PerformerReviewer),
            (AuthorizationOperation.RecordContentReview,
                AuthorizationRole.ContentApprover),
            (AuthorizationOperation.ChangePublicationClassification,
                AuthorizationRole.ContentApprover),
            (AuthorizationOperation.RecordBrandReview,
                AuthorizationRole.BrandApprover),
            (AuthorizationOperation.RecordRightsDecision,
                AuthorizationRole.RightsApprover),
            (AuthorizationOperation.RecordPublication,
                AuthorizationRole.ContentApprover),
        ];

        foreach ((AuthorizationOperation operation, AuthorizationRole expectedRole) in approvals)
        {
            foreach (AuthorizationRole role in Enum.GetValues<AuthorizationRole>())
            {
                Assert.AreEqual(
                    role == expectedRole,
                    PermissionMatrix.CanPerform(role, operation),
                    $"Unexpected approval permission for {role}/{operation}.");
            }
        }
    }

    [TestMethod]
    public void UndefinedRoleAndOperationFailClosed()
    {
        ArgumentOutOfRangeException roleException =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PermissionMatrix.CanPerform(
                    (AuthorizationRole)int.MaxValue,
                    AuthorizationOperation.ViewProject));
        Assert.AreEqual("role", roleException.ParamName);

        ArgumentOutOfRangeException operationException =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => PermissionMatrix.CanPerform(
                    AuthorizationRole.Viewer,
                    (AuthorizationOperation)int.MaxValue));
        Assert.AreEqual("operation", operationException.ParamName);
    }

    [TestMethod]
    public void PermissionMatrixExposesOnlyThePureCanPerformOperation()
    {
        MethodInfo[] methods = typeof(PermissionMatrix).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(nameof(PermissionMatrix.CanPerform), methods[0].Name);
        Assert.AreEqual(typeof(bool), methods[0].ReturnType);
    }
}