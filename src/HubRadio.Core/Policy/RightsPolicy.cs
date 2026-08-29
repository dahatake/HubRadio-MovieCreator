using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;

namespace HubRadio.Core.Policy;

[Flags]
public enum RightsWarningCode
{
    None = 0,
    ExternalUseNotAuthorized = 1 << 0,
    RightsUnconfirmed = 1 << 1,
    RightsRejected = 1 << 2,
    RecordedExceptionOverride = 1 << 3,
}

public enum RightsPolicyDisposition
{
    AllowedByRightsRecord,
    WarnAndHold,
    AllowedByRecordedException,
}

[Flags]
public enum RightsResolutionOption
{
    None = 0,
    UpdateRightsRecordForExternalUse = 1 << 0,
    PersistAuthorizedException = 1 << 1,
}

public sealed class RightsExceptionIdTag
{
}

/// <summary>
/// Immutable, recorded evidence for one narrowly scoped external-output exception.
/// </summary>
/// <remarks>
/// There is intentionally no public constructor. Trusted application and persistence
/// code may create an instance only after it has verified the current RightsApprover
/// assignment, retained the corresponding audit event, and persisted this complete
/// snapshot. The type does not change the underlying <see cref="RightsRecord"/>.
/// </remarks>
public sealed class RecordedRightsException
{
    internal RecordedRightsException(
        EntityId<RightsExceptionIdTag> id,
        Asset asset,
        CompositionVersion compositionVersion,
        RenderProfile renderProfile,
        EntityId<AuditEventIdTag> auditEventId,
        string actorId,
        ReviewActorRole actorRole,
        DateTimeOffset decidedAt,
        string reason,
        string evidenceReference)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(renderProfile);
        ArgumentNullException.ThrowIfNull(auditEventId);

        if (asset.Rights.Status != RightsStatus.Unconfirmed
            || asset.Rights.ExternalPublicationAllowed)
        {
            throw new ArgumentException(
                "A recorded rights exception may be created only for unconfirmed rights that do not allow external publication.",
                nameof(asset));
        }

        if (!Enum.IsDefined(actorRole)
            || actorRole != ReviewActorRole.RightsApprover)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actorRole),
                actorRole,
                "A recorded rights exception actor must use the RightsApprover role.");
        }

        Id = id;
        AssetId = asset.Id;
        AssetContentDigest = asset.ContentDigest;
        RightsRecordFingerprint = RightsPolicy.ComputeRightsRecordFingerprint(asset.Rights);
        RightsStatusAtDecision = asset.Rights.Status;
        ExternalPublicationAllowedAtDecision =
            asset.Rights.ExternalPublicationAllowed;
        ProjectId = compositionVersion.ProjectId;
        CompositionVersionId = compositionVersion.Id;
        CompositionVersionDigest = compositionVersion.ContentDigest;
        RenderProfileId = renderProfile.Id;
        RenderProfileDigest = renderProfile.ProfileDigest;
        AuditEventId = auditEventId;
        ActorId = ContractText.RequireNonBlankStrictUtf8Substantive(
            actorId,
            nameof(actorId),
            "A recorded rights exception actor ID");
        ActorRole = actorRole;
        DecidedAt = decidedAt;
        Reason = ContractText.RequireNonBlankStrictUtf8Substantive(
            reason,
            nameof(reason),
            "A recorded rights exception reason");
        EvidenceReference = ContractText.RequireNonBlankStrictUtf8Substantive(
            evidenceReference,
            nameof(evidenceReference),
            "A recorded rights exception evidence reference");
    }

    public EntityId<RightsExceptionIdTag> Id { get; }

    public EntityId<AssetIdTag> AssetId { get; }

    public ContentDigest AssetContentDigest { get; }

    public ContentDigest RightsRecordFingerprint { get; }

    public RightsStatus RightsStatusAtDecision { get; }

    public bool ExternalPublicationAllowedAtDecision { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<RenderProfileIdTag> RenderProfileId { get; }

    public ContentDigest RenderProfileDigest { get; }

    public EntityId<AuditEventIdTag> AuditEventId { get; }

    public string ActorId { get; }

    public ReviewActorRole ActorRole { get; }

    public DateTimeOffset DecidedAt { get; }

    public string Reason { get; }

    public string EvidenceReference { get; }
}

/// <summary>
/// Immutable result of evaluating one asset for one composition and render profile.
/// </summary>
/// <remarks>
/// Authorization values are handoff requirements only. This pure result does not verify
/// current role assignments, persist a decision, or determine whether internal use is lawful.
/// </remarks>
public sealed class RightsPolicyDecision
{
    internal RightsPolicyDecision(
        Asset asset,
        CompositionVersion compositionVersion,
        RenderProfile renderProfile,
        RightsWarningCode warningCodes,
        RightsPolicyDisposition disposition,
        bool holdExternalOutput,
        RightsResolutionOption requiredResolutionOptions,
        AuthorizationOperation? requiredAuthorizationOperation,
        AuthorizationRole? requiredAuthorizationRole,
        ReviewActorRole? requiredReviewActorRole,
        bool auditRequired,
        RecordedRightsException? appliedException)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(renderProfile);

        ContentDigest rightsRecordFingerprint =
            RightsPolicy.ComputeRightsRecordFingerprint(asset.Rights);
        ValidateInvariant(
            asset,
            compositionVersion,
            renderProfile,
            rightsRecordFingerprint,
            warningCodes,
            disposition,
            holdExternalOutput,
            requiredResolutionOptions,
            requiredAuthorizationOperation,
            requiredAuthorizationRole,
            requiredReviewActorRole,
            auditRequired,
            appliedException);

        AssetId = asset.Id;
        AssetContentDigest = asset.ContentDigest;
        RightsRecordFingerprint = rightsRecordFingerprint;
        ObservedRightsStatus = asset.Rights.Status;
        ObservedExternalPublicationAllowed =
            asset.Rights.ExternalPublicationAllowed;
        ProjectId = compositionVersion.ProjectId;
        CompositionVersionId = compositionVersion.Id;
        CompositionVersionDigest = compositionVersion.ContentDigest;
        RenderProfileId = renderProfile.Id;
        RenderProfileDigest = renderProfile.ProfileDigest;
        WarningCodes = warningCodes;
        Disposition = disposition;
        HoldExternalOutput = holdExternalOutput;
        RequiredResolutionOptions = requiredResolutionOptions;
        RequiredAuthorizationOperation = requiredAuthorizationOperation;
        RequiredAuthorizationRole = requiredAuthorizationRole;
        RequiredReviewActorRole = requiredReviewActorRole;
        AuditRequired = auditRequired;
        AppliedException = appliedException;
    }

    public EntityId<AssetIdTag> AssetId { get; }

    public ContentDigest AssetContentDigest { get; }

    public ContentDigest RightsRecordFingerprint { get; }

    public RightsStatus ObservedRightsStatus { get; }

    public bool ObservedExternalPublicationAllowed { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<RenderProfileIdTag> RenderProfileId { get; }

    public ContentDigest RenderProfileDigest { get; }

    public RightsWarningCode WarningCodes { get; }

    public RightsPolicyDisposition Disposition { get; }

    public bool HoldExternalOutput { get; }

    /// <summary>
    /// Gets alternative resolution paths. A caller must complete one listed option and
    /// evaluate again; the flags do not mean the resolution has occurred.
    /// </summary>
    public RightsResolutionOption RequiredResolutionOptions { get; }

    public AuthorizationOperation? RequiredAuthorizationOperation { get; }

    public AuthorizationRole? RequiredAuthorizationRole { get; }

    public ReviewActorRole? RequiredReviewActorRole { get; }

    public bool AuditRequired { get; }

    public RecordedRightsException? AppliedException { get; }

    private static void ValidateInvariant(
        Asset asset,
        CompositionVersion compositionVersion,
        RenderProfile renderProfile,
        ContentDigest rightsRecordFingerprint,
        RightsWarningCode warningCodes,
        RightsPolicyDisposition disposition,
        bool holdExternalOutput,
        RightsResolutionOption requiredResolutionOptions,
        AuthorizationOperation? requiredAuthorizationOperation,
        AuthorizationRole? requiredAuthorizationRole,
        ReviewActorRole? requiredReviewActorRole,
        bool auditRequired,
        RecordedRightsException? appliedException)
    {
        const RightsWarningCode allWarningCodes =
            RightsWarningCode.ExternalUseNotAuthorized
            | RightsWarningCode.RightsUnconfirmed
            | RightsWarningCode.RightsRejected
            | RightsWarningCode.RecordedExceptionOverride;
        const RightsResolutionOption allResolutionOptions =
            RightsResolutionOption.UpdateRightsRecordForExternalUse
            | RightsResolutionOption.PersistAuthorizedException;

        bool enumsAndFlagsAreDefined = Enum.IsDefined(asset.Rights.Status)
            && Enum.IsDefined(disposition)
            && (warningCodes & ~allWarningCodes) == RightsWarningCode.None
            && (requiredResolutionOptions & ~allResolutionOptions)
                == RightsResolutionOption.None
            && (requiredAuthorizationOperation is null
                || Enum.IsDefined(requiredAuthorizationOperation.Value))
            && (requiredAuthorizationRole is null
                || Enum.IsDefined(requiredAuthorizationRole.Value))
            && (requiredReviewActorRole is null
                || Enum.IsDefined(requiredReviewActorRole.Value));
        bool rightsStateIsValid = asset.Rights.Status == RightsStatus.Approved
            || !asset.Rights.ExternalPublicationAllowed;
        RightsWarningCode baseWarning = enumsAndFlagsAreDefined && rightsStateIsValid
            ? RightsPolicy.GetBaseWarningCode(
                asset.Rights.Status,
                asset.Rights.ExternalPublicationAllowed)
            : RightsWarningCode.None;
        RightsResolutionOption expectedResolutionOptions =
            RightsPolicy.GetResolutionOptions(asset.Rights.Status, baseWarning);
        bool hasNoAuthorizationHandoff = requiredAuthorizationOperation is null
            && requiredAuthorizationRole is null
            && requiredReviewActorRole is null;
        bool hasExactRightsAuthorizationHandoff =
            requiredAuthorizationOperation
                == AuthorizationOperation.RecordRightsDecision
            && requiredAuthorizationRole == AuthorizationRole.RightsApprover
            && requiredReviewActorRole == ReviewActorRole.RightsApprover;
        bool exceptionMatches = appliedException is not null
            && RightsPolicy.ExceptionMatches(
                asset,
                compositionVersion,
                renderProfile,
                rightsRecordFingerprint,
                appliedException);

        bool isConsistent = enumsAndFlagsAreDefined
            && rightsStateIsValid
            && disposition switch
            {
                RightsPolicyDisposition.AllowedByRightsRecord =>
                    baseWarning == RightsWarningCode.None
                    && warningCodes == RightsWarningCode.None
                    && !holdExternalOutput
                    && requiredResolutionOptions == RightsResolutionOption.None
                    && hasNoAuthorizationHandoff
                    && !auditRequired
                    && appliedException is null,
                RightsPolicyDisposition.WarnAndHold =>
                    baseWarning != RightsWarningCode.None
                    && warningCodes == baseWarning
                    && holdExternalOutput
                    && requiredResolutionOptions == expectedResolutionOptions
                    && hasExactRightsAuthorizationHandoff
                    && auditRequired
                    && appliedException is null,
                RightsPolicyDisposition.AllowedByRecordedException =>
                    baseWarning == RightsWarningCode.RightsUnconfirmed
                    && warningCodes
                        == (baseWarning | RightsWarningCode.RecordedExceptionOverride)
                    && !holdExternalOutput
                    && requiredResolutionOptions == RightsResolutionOption.None
                    && hasNoAuthorizationHandoff
                    && auditRequired
                    && exceptionMatches,
                _ => false,
            };

        if (!isConsistent)
        {
            throw new InvalidOperationException(
                "Rights policy decision arguments are inconsistent.");
        }
    }
}

/// <summary>
/// Evaluates whether one immutable asset snapshot may be used for external output.
/// </summary>
public static class RightsPolicy
{
    private const string FingerprintDomain =
        "HubRadio.MovieCreator.RightsRecordFingerprint/v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Evaluates external-output rights without mutating or persisting any input.
    /// </summary>
    /// <remarks>
    /// Only a <see cref="RecordedRightsException"/> loaded through trusted application
    /// persistence may release an unconfirmed-rights hold. Rejected rights and an approved
    /// record that explicitly disallows external publication require a RightsRecord update.
    /// </remarks>
    public static RightsPolicyDecision EvaluateExternalUse(
        Asset asset,
        CompositionVersion compositionVersion,
        RenderProfile renderProfile,
        RecordedRightsException? recordedException)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(renderProfile);

        RightsWarningCode baseWarning = GetBaseWarningCode(
            asset.Rights.Status,
            asset.Rights.ExternalPublicationAllowed);

        if (baseWarning == RightsWarningCode.None)
        {
            RejectContradictoryException(recordedException);
            return CreateDecision(
                asset,
                compositionVersion,
                renderProfile,
                RightsWarningCode.None,
                RightsPolicyDisposition.AllowedByRightsRecord,
                holdExternalOutput: false,
                RightsResolutionOption.None,
                auditRequired: false,
                appliedException: null);
        }

        if (recordedException is null)
        {
            return CreateDecision(
                asset,
                compositionVersion,
                renderProfile,
                baseWarning,
                RightsPolicyDisposition.WarnAndHold,
                holdExternalOutput: true,
                GetResolutionOptions(asset.Rights.Status, baseWarning),
                auditRequired: true,
                appliedException: null);
        }

        if (asset.Rights.Status != RightsStatus.Unconfirmed)
        {
            throw new ArgumentException(
                "A recorded exception cannot override rejected rights or a rights record that explicitly disallows external publication.",
                nameof(recordedException));
        }

        ContentDigest fingerprint = ComputeRightsRecordFingerprint(asset.Rights);
        if (!ExceptionMatches(
                asset,
                compositionVersion,
                renderProfile,
                fingerprint,
                recordedException))
        {
            throw new ArgumentException(
                "A recorded rights exception must match the current asset, rights record, composition version, and render profile snapshots.",
                nameof(recordedException));
        }

        return CreateDecision(
            asset,
            compositionVersion,
            renderProfile,
            baseWarning | RightsWarningCode.RecordedExceptionOverride,
            RightsPolicyDisposition.AllowedByRecordedException,
            holdExternalOutput: false,
            RightsResolutionOption.None,
            auditRequired: true,
            appliedException: recordedException);
    }

    internal static ContentDigest ComputeRightsRecordFingerprint(RightsRecord rights)
    {
        ArgumentNullException.ThrowIfNull(rights);
        if (!Enum.IsDefined(rights.Status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rights),
                rights.Status,
                "Unknown rights status.");
        }

        string allowedUse = ContractText.RequireNonBlankStrictUtf8Substantive(
            rights.AllowedUse,
            nameof(rights),
            "A rights-record allowed use");
        string? evidenceReference = rights.EvidenceReference is null
            ? null
            : ContractText.RequireNonBlankStrictUtf8Substantive(
                rights.EvidenceReference,
                nameof(rights),
                "A rights-record evidence reference");

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendText(hash, 1, FingerprintDomain);
        AppendText(hash, 2, rights.Status.ToString());
        AppendNullableText(hash, 3, evidenceReference);
        AppendText(hash, 4, allowedUse);
        AppendBytes(hash, 5, [rights.ExternalPublicationAllowed ? (byte)1 : (byte)0]);
        return ContentDigest.FromBytes(hash.GetHashAndReset());
    }

    internal static RightsWarningCode GetBaseWarningCode(
        RightsStatus status,
        bool externalPublicationAllowed)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown rights status.");
        }

        if (status != RightsStatus.Approved && externalPublicationAllowed)
        {
            throw new ArgumentException(
                "External publication cannot be allowed unless rights are approved.",
                nameof(externalPublicationAllowed));
        }

        return (status, externalPublicationAllowed) switch
        {
            (RightsStatus.Approved, true) => RightsWarningCode.None,
            (RightsStatus.Approved, false) =>
                RightsWarningCode.ExternalUseNotAuthorized,
            (RightsStatus.Unconfirmed, false) => RightsWarningCode.RightsUnconfirmed,
            (RightsStatus.Rejected, false) => RightsWarningCode.RightsRejected,
            _ => throw new InvalidOperationException(
                "The rights state is not a canonical external-use state."),
        };
    }

    internal static RightsResolutionOption GetResolutionOptions(
        RightsStatus status,
        RightsWarningCode baseWarning)
    {
        return (status, baseWarning) switch
        {
            (RightsStatus.Unconfirmed, RightsWarningCode.RightsUnconfirmed) =>
                RightsResolutionOption.UpdateRightsRecordForExternalUse
                | RightsResolutionOption.PersistAuthorizedException,
            (RightsStatus.Approved, RightsWarningCode.ExternalUseNotAuthorized)
                or (RightsStatus.Rejected, RightsWarningCode.RightsRejected) =>
                RightsResolutionOption.UpdateRightsRecordForExternalUse,
            _ => RightsResolutionOption.None,
        };
    }

    internal static bool ExceptionMatches(
        Asset asset,
        CompositionVersion compositionVersion,
        RenderProfile renderProfile,
        ContentDigest rightsRecordFingerprint,
        RecordedRightsException exception)
    {
        return exception.AssetId == asset.Id
            && exception.AssetContentDigest == asset.ContentDigest
            && exception.RightsRecordFingerprint == rightsRecordFingerprint
            && exception.RightsStatusAtDecision == asset.Rights.Status
            && exception.ExternalPublicationAllowedAtDecision
                == asset.Rights.ExternalPublicationAllowed
            && exception.ProjectId == compositionVersion.ProjectId
            && exception.CompositionVersionId == compositionVersion.Id
            && exception.CompositionVersionDigest == compositionVersion.ContentDigest
            && exception.RenderProfileId == renderProfile.Id
            && exception.RenderProfileDigest == renderProfile.ProfileDigest
            && exception.ActorRole == ReviewActorRole.RightsApprover;
    }

    private static RightsPolicyDecision CreateDecision(
        Asset asset,
        CompositionVersion compositionVersion,
        RenderProfile renderProfile,
        RightsWarningCode warningCodes,
        RightsPolicyDisposition disposition,
        bool holdExternalOutput,
        RightsResolutionOption resolutionOptions,
        bool auditRequired,
        RecordedRightsException? appliedException)
    {
        bool requiresResolution = resolutionOptions != RightsResolutionOption.None;
        return new RightsPolicyDecision(
            asset,
            compositionVersion,
            renderProfile,
            warningCodes,
            disposition,
            holdExternalOutput,
            resolutionOptions,
            requiresResolution
                ? AuthorizationOperation.RecordRightsDecision
                : null,
            requiresResolution ? AuthorizationRole.RightsApprover : null,
            requiresResolution ? ReviewActorRole.RightsApprover : null,
            auditRequired,
            appliedException);
    }

    private static void RejectContradictoryException(
        RecordedRightsException? recordedException)
    {
        if (recordedException is not null)
        {
            throw new ArgumentException(
                "A recorded exception is contradictory when the rights record already allows external publication.",
                nameof(recordedException));
        }
    }

    private static void AppendNullableText(
        IncrementalHash hash,
        byte tag,
        string? value)
    {
        AppendBytes(hash, tag, [value is null ? (byte)0 : (byte)1]);
        if (value is not null)
        {
            AppendText(hash, tag, value);
        }
    }

    private static void AppendText(
        IncrementalHash hash,
        byte tag,
        string value)
    {
        AppendBytes(hash, tag, StrictUtf8.GetBytes(value));
    }

    private static void AppendBytes(
        IncrementalHash hash,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], value.Length);
        hash.AppendData(header);
        hash.AppendData(value);
    }
}
