namespace HubRadio.Core.Security;

public enum AuthorizationRole
{
    Viewer,
    Editor,
    PerformerReviewer,
    ContentApprover,
    BrandApprover,
    RightsApprover,
    Administrator,
}

public enum AuthorizationOperation
{
    ViewProject,
    PreviewProject,
    CreateProject,
    DecideAnalysisSuggestion,
    CreateCompositionVersion,
    SubmitForReview,
    RecordPerformerReview,
    RecordContentReview,
    ChangePublicationClassification,
    RecordBrandReview,
    RecordRightsDecision,
    StartApprovedRender,
    RecordQualityControl,
    ExportComposition,
    RecordPublication,
    ManageRoleAssignments,
    ConfigureWorkspaceStorage,
    PublishTemplateVersion,
    ViewAuditLog,
}

public static class PermissionMatrix
{
    public static bool CanPerform(
        AuthorizationRole role,
        AuthorizationOperation operation)
    {
        ValidateDefined(role, nameof(role), "authorization role");
        ValidateDefined(operation, nameof(operation), "authorization operation");

        return (role, operation) switch
        {
            (AuthorizationRole.Viewer, AuthorizationOperation.ViewProject) => true,
            (AuthorizationRole.Viewer, AuthorizationOperation.PreviewProject) => true,

            (AuthorizationRole.Editor, AuthorizationOperation.ViewProject) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.PreviewProject) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.CreateProject) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.DecideAnalysisSuggestion) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.CreateCompositionVersion) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.SubmitForReview) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.StartApprovedRender) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.RecordQualityControl) => true,
            (AuthorizationRole.Editor, AuthorizationOperation.ExportComposition) => true,

            (AuthorizationRole.PerformerReviewer, AuthorizationOperation.ViewProject) => true,
            (AuthorizationRole.PerformerReviewer, AuthorizationOperation.PreviewProject) => true,
            (AuthorizationRole.PerformerReviewer, AuthorizationOperation.RecordPerformerReview) => true,

            (AuthorizationRole.ContentApprover, AuthorizationOperation.ViewProject) => true,
            (AuthorizationRole.ContentApprover, AuthorizationOperation.PreviewProject) => true,
            (AuthorizationRole.ContentApprover, AuthorizationOperation.RecordContentReview) => true,
            (AuthorizationRole.ContentApprover, AuthorizationOperation.ChangePublicationClassification) => true,
            (AuthorizationRole.ContentApprover, AuthorizationOperation.RecordPublication) => true,

            (AuthorizationRole.BrandApprover, AuthorizationOperation.ViewProject) => true,
            (AuthorizationRole.BrandApprover, AuthorizationOperation.PreviewProject) => true,
            (AuthorizationRole.BrandApprover, AuthorizationOperation.RecordBrandReview) => true,

            (AuthorizationRole.RightsApprover, AuthorizationOperation.ViewProject) => true,
            (AuthorizationRole.RightsApprover, AuthorizationOperation.PreviewProject) => true,
            (AuthorizationRole.RightsApprover, AuthorizationOperation.RecordRightsDecision) => true,

            (AuthorizationRole.Administrator, AuthorizationOperation.ViewProject) => true,
            (AuthorizationRole.Administrator, AuthorizationOperation.PreviewProject) => true,
            (AuthorizationRole.Administrator, AuthorizationOperation.ManageRoleAssignments) => true,
            (AuthorizationRole.Administrator, AuthorizationOperation.ConfigureWorkspaceStorage) => true,
            (AuthorizationRole.Administrator, AuthorizationOperation.PublishTemplateVersion) => true,
            (AuthorizationRole.Administrator, AuthorizationOperation.ViewAuditLog) => true,

            _ => false,
        };
    }

    private static void ValidateDefined<TEnum>(
        TEnum value,
        string parameterName,
        string description)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Unknown {description}.");
        }
    }
}