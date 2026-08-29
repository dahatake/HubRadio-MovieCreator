using System.Reflection;
using System.Text;
using HubRadio.Core.Audit;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Policy;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class RightsPolicyTests
{
    private const string ActorId = "S-1-5-21-1000";
    private const string DigestA =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DigestB =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string DigestC =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ExceptionReason =
        "Permit this exact unconfirmed-rights snapshot for the reviewed output.";
    private const string ExceptionEvidenceReference =
        "evidence/rights-exceptions/decision-001.json";

    private static readonly RightsResolutionOption UnconfirmedOptions =
        RightsResolutionOption.UpdateRightsRecordForExternalUse
        | RightsResolutionOption.PersistAuthorizedException;

    [TestMethod]
    public void ClosedEnumsDefineExactlyTheRequiredTypedContract()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                RightsWarningCode.None,
                RightsWarningCode.ExternalUseNotAuthorized,
                RightsWarningCode.RightsUnconfirmed,
                RightsWarningCode.RightsRejected,
                RightsWarningCode.RecordedExceptionOverride,
            },
            Enum.GetValues<RightsWarningCode>());
        CollectionAssert.AreEqual(
            new[]
            {
                RightsPolicyDisposition.AllowedByRightsRecord,
                RightsPolicyDisposition.WarnAndHold,
                RightsPolicyDisposition.AllowedByRecordedException,
            },
            Enum.GetValues<RightsPolicyDisposition>());
        CollectionAssert.AreEqual(
            new[]
            {
                RightsResolutionOption.None,
                RightsResolutionOption.UpdateRightsRecordForExternalUse,
                RightsResolutionOption.PersistAuthorizedException,
            },
            Enum.GetValues<RightsResolutionOption>());
        Assert.IsNotNull(typeof(RightsWarningCode).GetCustomAttribute<FlagsAttribute>());
        Assert.IsNotNull(typeof(RightsResolutionOption).GetCustomAttribute<FlagsAttribute>());
        Assert.IsTrue(typeof(RightsExceptionIdTag).IsPublic);
        Assert.IsTrue(typeof(RightsExceptionIdTag).IsSealed);
    }

    [TestMethod]
    public void EveryCanonicalRightsStateProducesTheExpectedFailClosedDecision()
    {
        var cases = new[]
        {
            new RightsStateCase(
                RightsStatus.Unconfirmed,
                ExternalPublicationAllowed: false,
                RightsWarningCode.RightsUnconfirmed,
                UnconfirmedOptions),
            new RightsStateCase(
                RightsStatus.Approved,
                ExternalPublicationAllowed: false,
                RightsWarningCode.ExternalUseNotAuthorized,
                RightsResolutionOption.UpdateRightsRecordForExternalUse),
            new RightsStateCase(
                RightsStatus.Rejected,
                ExternalPublicationAllowed: false,
                RightsWarningCode.RightsRejected,
                RightsResolutionOption.UpdateRightsRecordForExternalUse),
            new RightsStateCase(
                RightsStatus.Approved,
                ExternalPublicationAllowed: true,
                RightsWarningCode.None,
                RightsResolutionOption.None),
        };

        foreach (RightsStateCase stateCase in cases)
        {
            Asset asset = CreateAsset(
                stateCase.Status,
                stateCase.ExternalPublicationAllowed);
            CompositionVersion version = CreateVersion();
            RenderProfile profile = CreateProfile();

            RightsPolicyDecision decision = RightsPolicy.EvaluateExternalUse(
                asset,
                version,
                profile,
                recordedException: null);

            AssertDecisionSnapshot(decision, asset, version, profile);
            if (stateCase.ExpectedWarning == RightsWarningCode.None)
            {
                AssertAllowedByRightsRecord(decision);
            }
            else
            {
                AssertHeld(
                    decision,
                    stateCase.ExpectedWarning,
                    stateCase.ExpectedResolutionOptions);
            }
        }
    }

    [TestMethod]
    public void InvalidStatusExternalAllowedCombinationsFailBeforeEvaluation()
    {
        AssertParamName<ArgumentException>(
            "externalPublicationAllowed",
            () => CreateRightsRecord(
                RightsStatus.Unconfirmed,
                externalPublicationAllowed: true));
        AssertParamName<ArgumentException>(
            "externalPublicationAllowed",
            () => CreateRightsRecord(
                RightsStatus.Rejected,
                externalPublicationAllowed: true));
    }

    [TestMethod]
    public void MatchingRecordedExceptionReleasesOnlyAnUnconfirmedRightsHold()
    {
        Asset asset = CreateAsset(RightsStatus.Unconfirmed);
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();
        RecordedRightsException exception = CreateRecordedException(
            asset,
            version,
            profile);

        RightsPolicyDecision decision = RightsPolicy.EvaluateExternalUse(
            asset,
            version,
            profile,
            exception);

        AssertDecisionSnapshot(decision, asset, version, profile);
        Assert.AreEqual(
            RightsWarningCode.RightsUnconfirmed
                | RightsWarningCode.RecordedExceptionOverride,
            decision.WarningCodes);
        Assert.AreEqual(
            RightsPolicyDisposition.AllowedByRecordedException,
            decision.Disposition);
        Assert.IsFalse(decision.HoldExternalOutput);
        Assert.AreEqual(
            RightsResolutionOption.None,
            decision.RequiredResolutionOptions);
        AssertNoAuthorizationHandoff(decision);
        Assert.IsTrue(decision.AuditRequired);
        Assert.AreSame(exception, decision.AppliedException);
    }

    [TestMethod]
    public void RecordedExceptionBindsEveryCurrentOutputContextSnapshot()
    {
        Asset asset = CreateAsset(RightsStatus.Unconfirmed);
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();
        EntityId<RightsExceptionIdTag> exceptionId =
            EntityId<RightsExceptionIdTag>.New();
        EntityId<AuditEventIdTag> auditEventId =
            EntityId<AuditEventIdTag>.New();
        DateTimeOffset decidedAt = new(
            2026,
            8,
            29,
            18,
            45,
            12,
            TimeSpan.FromHours(9));

        RecordedRightsException exception = CreateRecordedException(
            asset,
            version,
            profile,
            exceptionId,
            auditEventId,
            decidedAt: decidedAt);

        Assert.AreSame(exceptionId, exception.Id);
        Assert.AreSame(asset.Id, exception.AssetId);
        Assert.AreSame(asset.ContentDigest, exception.AssetContentDigest);
        Assert.AreEqual(RightsStatus.Unconfirmed, exception.RightsStatusAtDecision);
        Assert.IsFalse(exception.ExternalPublicationAllowedAtDecision);
        Assert.AreSame(version.ProjectId, exception.ProjectId);
        Assert.AreSame(version.Id, exception.CompositionVersionId);
        Assert.AreSame(version.ContentDigest, exception.CompositionVersionDigest);
        Assert.AreSame(profile.Id, exception.RenderProfileId);
        Assert.AreSame(profile.ProfileDigest, exception.RenderProfileDigest);
        Assert.AreSame(auditEventId, exception.AuditEventId);
        Assert.AreEqual(ActorId, exception.ActorId);
        Assert.AreEqual(ReviewActorRole.RightsApprover, exception.ActorRole);
        Assert.AreEqual(decidedAt, exception.DecidedAt);
        Assert.AreEqual(TimeSpan.FromHours(9), exception.DecidedAt.Offset);
        Assert.AreEqual(ExceptionReason, exception.Reason);
        Assert.AreEqual(
            ExceptionEvidenceReference,
            exception.EvidenceReference);
        Assert.AreEqual(
            RightsPolicy.EvaluateExternalUse(asset, version, profile, null)
                .RightsRecordFingerprint,
            exception.RightsRecordFingerprint);
    }

    [TestMethod]
    public void AnyAssetRightsVersionOrProfileSnapshotChangeInvalidatesException()
    {
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
        ContentDigest assetDigest = Digest(DigestA);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<CompositionVersionIdTag> versionId =
            EntityId<CompositionVersionIdTag>.New();
        ContentDigest versionDigest = Digest(DigestB);
        EntityId<RenderProfileIdTag> profileId =
            EntityId<RenderProfileIdTag>.New();
        ContentDigest profileDigest = Digest(DigestC);
        Asset asset = CreateAsset(
            RightsStatus.Unconfirmed,
            assetId: assetId,
            contentDigest: assetDigest);
        CompositionVersion version = CreateVersion(
            projectId,
            versionId,
            versionDigest);
        RenderProfile profile = CreateProfile(profileId, profileDigest);
        RecordedRightsException exception = CreateRecordedException(
            asset,
            version,
            profile);

        var mismatches = new (Asset Asset, CompositionVersion Version, RenderProfile Profile)[]
        {
            (CreateAsset(
                RightsStatus.Unconfirmed,
                assetId: EntityId<AssetIdTag>.New(),
                contentDigest: assetDigest), version, profile),
            (CreateAsset(
                RightsStatus.Unconfirmed,
                assetId: assetId,
                contentDigest: Digest(DigestB)), version, profile),
            (CreateAsset(
                RightsStatus.Unconfirmed,
                assetId: assetId,
                contentDigest: assetDigest,
                rights: CreateRightsRecord(
                    RightsStatus.Unconfirmed,
                    false,
                    allowedUse: "different local use")), version, profile),
            (asset, CreateVersion(
                EntityId<ProjectIdTag>.New(),
                versionId,
                versionDigest), profile),
            (asset, CreateVersion(
                projectId,
                EntityId<CompositionVersionIdTag>.New(),
                versionDigest), profile),
            (asset, CreateVersion(
                projectId,
                versionId,
                Digest(DigestC)), profile),
            (asset, version, CreateProfile(
                EntityId<RenderProfileIdTag>.New(),
                profileDigest)),
            (asset, version, CreateProfile(profileId, Digest(DigestA))),
        };

        foreach ((Asset changedAsset, CompositionVersion changedVersion,
                     RenderProfile changedProfile) in mismatches)
        {
            AssertParamName<ArgumentException>(
                "recordedException",
                () => RightsPolicy.EvaluateExternalUse(
                    changedAsset,
                    changedVersion,
                    changedProfile,
                    exception));
        }
    }

    [TestMethod]
    public void RejectedAndExplicitlyInternalRightsCannotUseExceptionPath()
    {
        foreach (Asset blocked in new[]
        {
            CreateAsset(RightsStatus.Rejected),
            CreateAsset(RightsStatus.Approved),
        })
        {
            ConstructorInfo constructor = GetExceptionConstructor();
            TargetInvocationException creationFailure =
                Assert.ThrowsExactly<TargetInvocationException>(
                    () => constructor.Invoke(CreateExceptionArguments(
                        blocked,
                        CreateVersion(),
                        CreateProfile())));
            Assert.IsInstanceOfType<ArgumentException>(
                creationFailure.InnerException);
            Assert.AreEqual(
                "asset",
                ((ArgumentException)creationFailure.InnerException!).ParamName);
        }

        Asset unconfirmed = CreateAsset(RightsStatus.Unconfirmed);
        EntityId<AssetIdTag> sharedId = unconfirmed.Id;
        ContentDigest sharedDigest = unconfirmed.ContentDigest;
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();
        RecordedRightsException exception = CreateRecordedException(
            unconfirmed,
            version,
            profile);

        foreach (RightsStatus status in new[]
        {
            RightsStatus.Rejected,
            RightsStatus.Approved,
        })
        {
            Asset changed = CreateAsset(
                status,
                assetId: sharedId,
                contentDigest: sharedDigest);
            AssertParamName<ArgumentException>(
                "recordedException",
                () => RightsPolicy.EvaluateExternalUse(
                    changed,
                    version,
                    profile,
                    exception));
        }
    }

    [TestMethod]
    public void AlreadyAllowedRightsRejectContradictoryExceptionEvidence()
    {
        EntityId<AssetIdTag> sharedId = EntityId<AssetIdTag>.New();
        ContentDigest sharedDigest = Digest(DigestA);
        Asset unconfirmed = CreateAsset(
            RightsStatus.Unconfirmed,
            assetId: sharedId,
            contentDigest: sharedDigest);
        Asset allowed = CreateAsset(
            RightsStatus.Approved,
            externalPublicationAllowed: true,
            assetId: sharedId,
            contentDigest: sharedDigest);
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();
        RecordedRightsException exception = CreateRecordedException(
            unconfirmed,
            version,
            profile);

        AssertParamName<ArgumentException>(
            "recordedException",
            () => RightsPolicy.EvaluateExternalUse(
                allowed,
                version,
                profile,
                exception));
    }

    [TestMethod]
    public void RightsRecordFingerprintIsDeterministicAndDetectsEveryFieldDifference()
    {
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();
        EntityId<AssetIdTag> assetId = EntityId<AssetIdTag>.New();
        ContentDigest assetDigest = Digest(DigestA);
        RightsRecord[] rightsRecords =
        [
            CreateRightsRecord(RightsStatus.Unconfirmed, false),
            CreateRightsRecord(
                RightsStatus.Unconfirmed,
                false,
                evidenceReference: "evidence/pending-review.json"),
            CreateRightsRecord(
                RightsStatus.Unconfirmed,
                false,
                allowedUse: "another local use"),
            CreateRightsRecord(RightsStatus.Rejected, false),
            CreateRightsRecord(RightsStatus.Approved, false),
            CreateRightsRecord(RightsStatus.Approved, true),
        ];

        string[] fingerprints = rightsRecords
            .Select(rights => RightsPolicy.EvaluateExternalUse(
                CreateAsset(
                    rights.Status,
                    rights.ExternalPublicationAllowed,
                    assetId,
                    assetDigest,
                    rights),
                version,
                profile,
                null).RightsRecordFingerprint.Hex)
            .ToArray();

        Assert.AreEqual(fingerprints.Length, fingerprints.Distinct().Count());
        Assert.AreEqual(
            fingerprints[0],
            RightsPolicy.EvaluateExternalUse(
                CreateAsset(
                    RightsStatus.Unconfirmed,
                    assetId: assetId,
                    contentDigest: assetDigest,
                    rights: CreateRightsRecord(RightsStatus.Unconfirmed, false)),
                version,
                profile,
                null).RightsRecordFingerprint.Hex);
    }

    [TestMethod]
    public void RecordedExceptionHasNoPublicConstructionOrMutationSurface()
    {
        AssertClosedImmutableType(
            typeof(RecordedRightsException),
            "Id",
            "AssetId",
            "AssetContentDigest",
            "RightsRecordFingerprint",
            "RightsStatusAtDecision",
            "ExternalPublicationAllowedAtDecision",
            "ProjectId",
            "CompositionVersionId",
            "CompositionVersionDigest",
            "RenderProfileId",
            "RenderProfileDigest",
            "AuditEventId",
            "ActorId",
            "ActorRole",
            "DecidedAt",
            "Reason",
            "EvidenceReference");
        Assert.AreEqual(0, typeof(RecordedRightsException).GetConstructors().Length);
        ConstructorInfo constructor = GetExceptionConstructor();
        Assert.IsTrue(constructor.IsAssembly);
        Assert.IsFalse(constructor.IsPublic);
    }

    [TestMethod]
    public void ExceptionConstructorRequiresExactRoleReferencesAndSubstantiveStrictText()
    {
        Asset asset = CreateAsset(RightsStatus.Unconfirmed);
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();
        ConstructorInfo constructor = GetExceptionConstructor();

        for (int index = 0; index < 5; index++)
        {
            object?[] arguments = CreateExceptionArguments(asset, version, profile);
            arguments[index] = null;
            AssertConstructorParamName<ArgumentNullException>(
                constructor,
                arguments,
                index switch
                {
                    0 => "id",
                    1 => "asset",
                    2 => "compositionVersion",
                    3 => "renderProfile",
                    4 => "auditEventId",
                    _ => throw new AssertFailedException(),
                });
        }

        foreach (ReviewActorRole role in Enum.GetValues<ReviewActorRole>())
        {
            if (role == ReviewActorRole.RightsApprover)
            {
                continue;
            }

            object?[] arguments = CreateExceptionArguments(asset, version, profile);
            arguments[6] = role;
            AssertConstructorParamName<ArgumentOutOfRangeException>(
                constructor,
                arguments,
                "actorRole");
        }

        string[] invalidText =
        [
            string.Empty,
            " ",
            " value",
            "value ",
            "\u200B\u2060",
            "\u0301\u20DD",
            "\u115F\u1160\u3164\uFFA0\u2800",
            new string('\ud800', 1),
            new string('\udc00', 1),
        ];
        foreach (string invalid in invalidText)
        {
            foreach (int argumentIndex in new[] { 5, 8, 9 })
            {
                object?[] arguments = CreateExceptionArguments(asset, version, profile);
                arguments[argumentIndex] = invalid;
                TargetInvocationException failure =
                    Assert.ThrowsExactly<TargetInvocationException>(
                        () => constructor.Invoke(arguments));
                Assert.IsInstanceOfType<ArgumentException>(failure.InnerException);
            }
        }
    }

    [TestMethod]
    public void EvaluationIsPureAndDoesNotRewriteRightsOrContext()
    {
        RightsRecord rights = CreateRightsRecord(RightsStatus.Unconfirmed, false);
        Asset asset = CreateAsset(RightsStatus.Unconfirmed, rights: rights);
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();
        RecordedRightsException exception = CreateRecordedException(
            asset,
            version,
            profile);

        RightsPolicyDecision decision = RightsPolicy.EvaluateExternalUse(
            asset,
            version,
            profile,
            exception);

        Assert.AreEqual(
            RightsPolicyDisposition.AllowedByRecordedException,
            decision.Disposition);
        Assert.AreSame(rights, asset.Rights);
        Assert.AreEqual(RightsStatus.Unconfirmed, rights.Status);
        Assert.IsFalse(rights.ExternalPublicationAllowed);
        Assert.AreSame(version.Id, decision.CompositionVersionId);
        Assert.AreSame(profile.Id, decision.RenderProfileId);
    }

    [TestMethod]
    public void NullEvaluationInputsFailBeforeAnyDecision()
    {
        Asset asset = CreateAsset(RightsStatus.Unconfirmed);
        CompositionVersion version = CreateVersion();
        RenderProfile profile = CreateProfile();

        AssertParamName<ArgumentNullException>(
            "asset",
            () => RightsPolicy.EvaluateExternalUse(
                null!,
                version,
                profile,
                null));
        AssertParamName<ArgumentNullException>(
            "compositionVersion",
            () => RightsPolicy.EvaluateExternalUse(
                asset,
                null!,
                profile,
                null));
        AssertParamName<ArgumentNullException>(
            "renderProfile",
            () => RightsPolicy.EvaluateExternalUse(
                asset,
                version,
                null!,
                null));
    }

    [TestMethod]
    public void PublicPolicySurfaceIsOneTypedExternalUseEvaluation()
    {
        MethodInfo[] methods = typeof(RightsPolicy).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(nameof(RightsPolicy.EvaluateExternalUse), methods[0].Name);
        Assert.AreEqual(typeof(RightsPolicyDecision), methods[0].ReturnType);
        CollectionAssert.AreEqual(
            new[]
            {
                typeof(Asset),
                typeof(CompositionVersion),
                typeof(RenderProfile),
                typeof(RecordedRightsException),
            },
            methods[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "asset",
                "compositionVersion",
                "renderProfile",
                "recordedException",
            },
            methods[0].GetParameters()
                .Select(static parameter => parameter.Name)
                .ToArray());

        AssertClosedImmutableType(
            typeof(RightsPolicyDecision),
            "AssetId",
            "AssetContentDigest",
            "RightsRecordFingerprint",
            "ObservedRightsStatus",
            "ObservedExternalPublicationAllowed",
            "ProjectId",
            "CompositionVersionId",
            "CompositionVersionDigest",
            "RenderProfileId",
            "RenderProfileDigest",
            "WarningCodes",
            "Disposition",
            "HoldExternalOutput",
            "RequiredResolutionOptions",
            "RequiredAuthorizationOperation",
            "RequiredAuthorizationRole",
            "RequiredReviewActorRole",
            "AuditRequired",
            "AppliedException");
        Assert.AreEqual(0, typeof(RightsPolicyDecision).GetConstructors().Length);
    }

    private static Asset CreateAsset(
        RightsStatus status,
        bool externalPublicationAllowed = false,
        EntityId<AssetIdTag>? assetId = null,
        ContentDigest? contentDigest = null,
        RightsRecord? rights = null)
    {
        return new Asset(
            assetId ?? EntityId<AssetIdTag>.New(),
            "recording.mov",
            "video/quicktime",
            contentDigest ?? Digest(DigestA),
            1024,
            rights ?? CreateRightsRecord(status, externalPublicationAllowed));
    }

    private static RightsRecord CreateRightsRecord(
        RightsStatus status,
        bool externalPublicationAllowed,
        string? evidenceReference = null,
        string allowedUse = "local review only")
    {
        if (status == RightsStatus.Approved && evidenceReference is null)
        {
            evidenceReference = "evidence/rights-license.pdf";
        }

        return new RightsRecord(
            status,
            evidenceReference,
            externalPublicationAllowed ? "external publication" : allowedUse,
            externalPublicationAllowed);
    }

    private static CompositionVersion CreateVersion(
        EntityId<ProjectIdTag>? projectId = null,
        EntityId<CompositionVersionIdTag>? id = null,
        ContentDigest? digest = null)
    {
        return new CompositionVersion(
            id ?? EntityId<CompositionVersionIdTag>.New(),
            projectId ?? EntityId<ProjectIdTag>.New(),
            parentVersionId: null,
            ActorId,
            new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero),
            "Rights review target.",
            digest ?? Digest(DigestB),
            []);
    }

    private static RenderProfile CreateProfile(
        EntityId<RenderProfileIdTag>? id = null,
        ContentDigest? digest = null)
    {
        return new RenderProfile(
            id ?? EntityId<RenderProfileIdTag>.New(),
            "external-review-profile",
            digest ?? Digest(DigestC),
            1280,
            720,
            30000,
            1001,
            EntityId<LayoutSpecIdTag>.New(),
            EntityId<TemplateIdTag>.New(),
            Digest(DigestA),
            "h264",
            "aac",
            subtitleBurnIn: true);
    }

    private static RecordedRightsException CreateRecordedException(
        Asset asset,
        CompositionVersion version,
        RenderProfile profile,
        EntityId<RightsExceptionIdTag>? id = null,
        EntityId<AuditEventIdTag>? auditEventId = null,
        string actorId = ActorId,
        ReviewActorRole actorRole = ReviewActorRole.RightsApprover,
        DateTimeOffset? decidedAt = null,
        string reason = ExceptionReason,
        string evidenceReference = ExceptionEvidenceReference)
    {
        object?[] arguments = CreateExceptionArguments(
            asset,
            version,
            profile,
            id,
            auditEventId,
            actorId,
            actorRole,
            decidedAt,
            reason,
            evidenceReference);
        return (RecordedRightsException)GetExceptionConstructor().Invoke(arguments);
    }

    private static object?[] CreateExceptionArguments(
        Asset asset,
        CompositionVersion version,
        RenderProfile profile,
        EntityId<RightsExceptionIdTag>? id = null,
        EntityId<AuditEventIdTag>? auditEventId = null,
        string actorId = ActorId,
        ReviewActorRole actorRole = ReviewActorRole.RightsApprover,
        DateTimeOffset? decidedAt = null,
        string reason = ExceptionReason,
        string evidenceReference = ExceptionEvidenceReference)
    {
        return
        [
            id ?? EntityId<RightsExceptionIdTag>.New(),
            asset,
            version,
            profile,
            auditEventId ?? EntityId<AuditEventIdTag>.New(),
            actorId,
            actorRole,
            decidedAt
                ?? new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero),
            reason,
            evidenceReference,
        ];
    }

    private static ConstructorInfo GetExceptionConstructor()
    {
        return typeof(RecordedRightsException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
    }

    private static ContentDigest Digest(string hex) => new(hex);

    private static void AssertDecisionSnapshot(
        RightsPolicyDecision decision,
        Asset asset,
        CompositionVersion version,
        RenderProfile profile)
    {
        Assert.AreSame(asset.Id, decision.AssetId);
        Assert.AreSame(asset.ContentDigest, decision.AssetContentDigest);
        Assert.AreEqual(asset.Rights.Status, decision.ObservedRightsStatus);
        Assert.AreEqual(
            asset.Rights.ExternalPublicationAllowed,
            decision.ObservedExternalPublicationAllowed);
        Assert.AreSame(version.ProjectId, decision.ProjectId);
        Assert.AreSame(version.Id, decision.CompositionVersionId);
        Assert.AreSame(version.ContentDigest, decision.CompositionVersionDigest);
        Assert.AreSame(profile.Id, decision.RenderProfileId);
        Assert.AreSame(profile.ProfileDigest, decision.RenderProfileDigest);
    }

    private static void AssertAllowedByRightsRecord(RightsPolicyDecision decision)
    {
        Assert.AreEqual(RightsWarningCode.None, decision.WarningCodes);
        Assert.AreEqual(
            RightsPolicyDisposition.AllowedByRightsRecord,
            decision.Disposition);
        Assert.IsFalse(decision.HoldExternalOutput);
        Assert.AreEqual(
            RightsResolutionOption.None,
            decision.RequiredResolutionOptions);
        AssertNoAuthorizationHandoff(decision);
        Assert.IsFalse(decision.AuditRequired);
        Assert.IsNull(decision.AppliedException);
    }

    private static void AssertHeld(
        RightsPolicyDecision decision,
        RightsWarningCode expectedWarning,
        RightsResolutionOption expectedOptions)
    {
        Assert.AreEqual(expectedWarning, decision.WarningCodes);
        Assert.AreEqual(
            RightsPolicyDisposition.WarnAndHold,
            decision.Disposition);
        Assert.IsTrue(decision.HoldExternalOutput);
        Assert.AreEqual(expectedOptions, decision.RequiredResolutionOptions);
        Assert.AreEqual(
            AuthorizationOperation.RecordRightsDecision,
            decision.RequiredAuthorizationOperation);
        Assert.AreEqual(
            AuthorizationRole.RightsApprover,
            decision.RequiredAuthorizationRole);
        Assert.AreEqual(
            ReviewActorRole.RightsApprover,
            decision.RequiredReviewActorRole);
        Assert.IsTrue(decision.AuditRequired);
        Assert.IsNull(decision.AppliedException);
    }

    private static void AssertNoAuthorizationHandoff(
        RightsPolicyDecision decision)
    {
        Assert.IsNull(decision.RequiredAuthorizationOperation);
        Assert.IsNull(decision.RequiredAuthorizationRole);
        Assert.IsNull(decision.RequiredReviewActorRole);
    }

    private static void AssertClosedImmutableType(
        Type type,
        params string[] expectedPropertyNames)
    {
        Assert.IsTrue(type.IsPublic);
        Assert.IsTrue(type.IsSealed);
        Assert.IsFalse(type.IsAbstract);
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedPropertyNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(
            static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
        Assert.AreEqual(
            0,
            type.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Count(static method => !method.IsSpecialName));
    }

    private static void AssertConstructorParamName<TException>(
        ConstructorInfo constructor,
        object?[] arguments,
        string expectedParameterName)
        where TException : ArgumentException
    {
        TargetInvocationException failure =
            Assert.ThrowsExactly<TargetInvocationException>(
                () => constructor.Invoke(arguments));
        Assert.IsInstanceOfType<TException>(failure.InnerException);
        Assert.AreEqual(
            expectedParameterName,
            ((TException)failure.InnerException!).ParamName);
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }

    private sealed record RightsStateCase(
        RightsStatus Status,
        bool ExternalPublicationAllowed,
        RightsWarningCode ExpectedWarning,
        RightsResolutionOption ExpectedResolutionOptions);
}
