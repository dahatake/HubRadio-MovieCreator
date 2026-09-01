using System.Globalization;
using System.Text.Json;
using HubRadio.App.Reviews;
using HubRadio.App.Security;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Reviews;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Diagnostics;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Web;

internal sealed class ReviewBridgeHandler
{
    private readonly ReviewWorkspaceService service;

    internal ReviewBridgeHandler(ReviewWorkspaceService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        this.service = service;
    }

    internal Task<string> HandleAsync(
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string response;
        try
        {
            ReviewWorkspaceSnapshot snapshot = request.Method switch
            {
                BridgeMethod.ReviewGetSnapshot => service.GetSnapshot(
                    ReadVersionId(request)),
                BridgeMethod.ReviewAddComment => service.AddComment(
                    ReadVersionId(request),
                    ReadVersionDigest(request),
                    ReadReviewAuthorRole(request),
                    ReadOutputTime(request),
                    request.Parameters.GetProperty("text").GetString()!),
                BridgeMethod.ReviewRequestChanges => service.RequestChanges(
                    ReadVersionId(request),
                    ReadVersionDigest(request),
                    ReadReviewState(request),
                    ReadOutputTime(request),
                    request.Parameters.GetProperty("text").GetString()!),
                BridgeMethod.ReviewRecordApproval => service.RecordApproval(
                    ReadVersionId(request),
                    ReadVersionDigest(request),
                    ReadReviewAuthorRole(request),
                    request.Parameters.GetProperty("reason").GetString()!),
                _ => throw new ArgumentException(
                    "The bridge request is not a review method.",
                    nameof(request)),
            };
            response = WriteSnapshot(request, snapshot);
        }
        catch (ReviewWorkspaceException exception)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                ToErrorCode(exception.Reason));
        }
        catch (AuthorizationException)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ReviewNotAuthorized);
        }
        catch (UnauthorizedAccessException)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ReviewNotAuthorized);
        }
        catch (StoreConcurrencyException)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ReviewConflict);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ReviewConflict);
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                FormatException or
                JsonException or
                OverflowException)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ReviewInvalidRequest);
        }
        catch (Exception)
        {
            response = BridgeProtocol.CreateErrorResponse(
                request,
                StableErrorCodes.ReviewOperationFailed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(response);
    }

    private static EntityId<CompositionVersionIdTag> ReadVersionId(
        BridgeRequest request) =>
        EntityId<CompositionVersionIdTag>.Parse(
            request.Parameters.GetProperty("versionId").GetString()!);

    private static ContentDigest ReadVersionDigest(BridgeRequest request) =>
        ContentDigest.Parse(
            request.Parameters.GetProperty("versionDigest").GetString()!);

    private static RationalTime ReadOutputTime(BridgeRequest request)
    {
        JsonElement value = request.Parameters.GetProperty("outputTime");
        return new RationalTime(
            long.Parse(
                value.GetProperty("value").GetString()!,
                NumberStyles.None,
                CultureInfo.InvariantCulture),
            long.Parse(
                value.GetProperty("rate").GetString()!,
                NumberStyles.None,
                CultureInfo.InvariantCulture));
    }

    private static ReviewState ReadReviewState(BridgeRequest request) =>
        request.Parameters.GetProperty("expectedState").GetString() switch
        {
            "editing" => ReviewState.Editing,
            "editorReview" => ReviewState.EditorReview,
            "performerReview" => ReviewState.PerformerReview,
            "contentReview" => ReviewState.ContentReview,
            "brandReview" => ReviewState.BrandReview,
            "changesRequested" => ReviewState.ChangesRequested,
            "approved" => ReviewState.Approved,
            "publicationBlocked" => ReviewState.PublicationBlocked,
            "finalRendering" => ReviewState.FinalRendering,
            "qualityControl" => ReviewState.QualityControl,
            "exported" => ReviewState.Exported,
            "published" => ReviewState.Published,
            _ => throw new FormatException("The expected review state is invalid."),
        };

    private static ReviewActorRole ReadReviewAuthorRole(BridgeRequest request) =>
        request.Parameters.GetProperty("actorRole").GetString() switch
        {
            "editor" => ReviewActorRole.Editor,
            "performerReviewer" => ReviewActorRole.PerformerReviewer,
            "contentApprover" => ReviewActorRole.ContentApprover,
            "brandApprover" => ReviewActorRole.BrandApprover,
            "rightsApprover" => ReviewActorRole.RightsApprover,
            _ => throw new FormatException("The review actor role is invalid."),
        };

    private static string WriteSnapshot(
        BridgeRequest request,
        ReviewWorkspaceSnapshot snapshot) =>
        BridgeProtocol.CreateResultResponse(
            request,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("versionId", snapshot.Version.Id.ToString());
                writer.WriteString(
                    "versionDigest",
                    snapshot.Version.ContentDigest.Hex);
                writer.WriteString(
                    "createdAt",
                    FormatTimestamp(snapshot.Version.CreatedAt));
                WriteNullableString(
                    writer,
                    "parentVersionId",
                    snapshot.Version.ParentVersionId?.ToString());
                writer.WriteBoolean("isCurrentTarget", snapshot.IsCurrentTarget);
                WriteNullableString(
                    writer,
                    "reviewState",
                    snapshot.CurrentReviewRecord is null
                        ? null
                        : FormatReviewState(snapshot.CurrentReviewRecord.State));
                WriteNullableString(
                    writer,
                    "reviewStateReason",
                    snapshot.CurrentReviewRecord?.Reason);
                WriteNullableString(
                    writer,
                    "reviewStateRecordedAt",
                    snapshot.CurrentReviewRecord is null
                        ? null
                        : FormatTimestamp(snapshot.CurrentReviewRecord.RecordedAt));
                WriteNullableString(
                    writer,
                    "reviewStateActorRole",
                    snapshot.CurrentReviewRecord is null
                        ? null
                        : FormatReviewActorRole(
                            snapshot.CurrentReviewRecord.ActorRole));
                WriteComments(writer, snapshot.Comments);
                WriteApprovals(writer, snapshot.Approvals);
                WriteDirectParentDiff(writer, snapshot.DirectParentDiff);
                WriteActions(writer, snapshot.Actions);
                writer.WriteEndObject();
            });

    private static void WriteComments(
        Utf8JsonWriter writer,
        IReadOnlyList<StoredReviewComment> comments)
    {
        writer.WriteStartArray("comments");
        foreach (StoredReviewComment comment in comments)
        {
            writer.WriteStartObject();
            writer.WriteString("id", comment.Id.ToString());
            WriteRationalTime(writer, "outputTime", comment.OutputTime);
            writer.WriteString("sourceSpanId", comment.SourceSpanId.ToString());
            writer.WriteString(
                "actorRole",
                FormatReviewActorRole(comment.ActorRole));
            writer.WriteString("createdAt", FormatTimestamp(comment.CreatedAt));
            writer.WriteString("text", comment.Text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteApprovals(
        Utf8JsonWriter writer,
        IReadOnlyList<ReviewApprovalView> approvals)
    {
        writer.WriteStartArray("approvals");
        foreach (ReviewApprovalView view in approvals)
        {
            StoredApproval approval = view.Approval;
            writer.WriteStartObject();
            writer.WriteString("id", approval.Id.ToString());
            writer.WriteString(
                "versionId",
                approval.CompositionVersionId.ToString());
            writer.WriteString(
                "versionDigest",
                approval.CompositionVersionDigest.Hex);
            writer.WriteString(
                "actorRole",
                FormatReviewActorRole(approval.ActorRole));
            writer.WriteString("approvedAt", FormatTimestamp(approval.ApprovedAt));
            writer.WriteString("reason", approval.Reason);
            writer.WriteString("validity", view.IsValid ? "valid" : "stale");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDirectParentDiff(
        Utf8JsonWriter writer,
        HubRadio.App.Compositions.CompositionDiff? diff)
    {
        if (diff is null)
        {
            writer.WriteNull("directParentDiff");
            return;
        }

        writer.WriteStartObject("directParentDiff");
        writer.WriteString("fromVersionId", diff.FromVersionId.ToString());
        writer.WriteString("toVersionId", diff.ToVersionId.ToString());
        writer.WriteStartArray("changes");
        foreach (HubRadio.App.Compositions.CompositionChange change in diff.Changes)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", change.Kind switch
            {
                HubRadio.App.Compositions.CompositionChangeKind.Added => "added",
                HubRadio.App.Compositions.CompositionChangeKind.Removed => "removed",
                HubRadio.App.Compositions.CompositionChangeKind.Moved => "moved",
                HubRadio.App.Compositions.CompositionChangeKind.Modified => "modified",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(change),
                    change.Kind,
                    "Unknown composition change kind."),
            });
            writer.WriteString("elementId", change.ElementId.ToString());
            WriteNullableNumber(writer, "previousIndex", change.PreviousIndex);
            WriteNullableNumber(writer, "currentIndex", change.CurrentIndex);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteActions(
        Utf8JsonWriter writer,
        IReadOnlyList<ReviewWorkspaceAction> actions)
    {
        writer.WriteStartArray("actions");
        foreach (ReviewWorkspaceAction action in actions)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", action.Kind switch
            {
                ReviewWorkspaceActionKind.AddComment => "addComment",
                ReviewWorkspaceActionKind.RequestChanges => "requestChanges",
                ReviewWorkspaceActionKind.RecordApproval => "recordApproval",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(action),
                    action.Kind,
                    "Unknown review action kind."),
            });
            writer.WriteString("status", action.Status switch
            {
                ReviewWorkspaceActionStatus.Authorized => "authorized",
                ReviewWorkspaceActionStatus.ActorNotAuthorized =>
                    "actorNotAuthorized",
                ReviewWorkspaceActionStatus.AlreadyRecorded =>
                    "alreadyRecorded",
                ReviewWorkspaceActionStatus.StateNotEligible =>
                    "stateNotEligible",
                ReviewWorkspaceActionStatus.TargetNotCurrent =>
                    "targetNotCurrent",
                ReviewWorkspaceActionStatus.ReviewStateUnrecorded =>
                    "reviewStateUnrecorded",
                ReviewWorkspaceActionStatus.PublicationBlocked =>
                    "publicationBlocked",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(action),
                    action.Status,
                    "Unknown review action status."),
            });
            writer.WriteStartArray("actorRoles");
            foreach (ReviewActorRole role in action.ActorRoles)
            {
                writer.WriteStringValue(FormatReviewActorRole(role));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string FormatReviewState(ReviewState state) =>
        state switch
        {
            ReviewState.Editing => "editing",
            ReviewState.EditorReview => "editorReview",
            ReviewState.PerformerReview => "performerReview",
            ReviewState.ContentReview => "contentReview",
            ReviewState.BrandReview => "brandReview",
            ReviewState.ChangesRequested => "changesRequested",
            ReviewState.Approved => "approved",
            ReviewState.PublicationBlocked => "publicationBlocked",
            ReviewState.FinalRendering => "finalRendering",
            ReviewState.QualityControl => "qualityControl",
            ReviewState.Exported => "exported",
            ReviewState.Published => "published",
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Unknown review state."),
        };

    private static string FormatReviewActorRole(ReviewActorRole role) =>
        role switch
        {
            ReviewActorRole.Editor => "editor",
            ReviewActorRole.PerformerReviewer => "performerReviewer",
            ReviewActorRole.ContentApprover => "contentApprover",
            ReviewActorRole.BrandApprover => "brandApprover",
            ReviewActorRole.RightsApprover => "rightsApprover",
            ReviewActorRole.Administrator => "administrator",
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown review actor role."),
        };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void WriteRationalTime(
        Utf8JsonWriter writer,
        string propertyName,
        RationalTime value)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString(
            "value",
            value.Value.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(
            "rate",
            value.Rate.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static string ToErrorCode(ReviewWorkspaceFailureReason reason) =>
        reason switch
        {
            ReviewWorkspaceFailureReason.NotFound =>
                StableErrorCodes.ReviewNotFound,
            ReviewWorkspaceFailureReason.NotAuthorized =>
                StableErrorCodes.ReviewNotAuthorized,
            ReviewWorkspaceFailureReason.StaleVersion =>
                StableErrorCodes.ReviewStaleVersion,
            ReviewWorkspaceFailureReason.Conflict =>
                StableErrorCodes.ReviewConflict,
            ReviewWorkspaceFailureReason.InvalidRequest =>
                StableErrorCodes.ReviewInvalidRequest,
            ReviewWorkspaceFailureReason.OperationFailed =>
                StableErrorCodes.ReviewOperationFailed,
            _ => StableErrorCodes.ReviewOperationFailed,
        };
}
