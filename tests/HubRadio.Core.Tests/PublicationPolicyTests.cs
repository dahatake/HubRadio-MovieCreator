using System.Reflection;
using System.Text;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Policy;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Core.Security;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class PublicationPolicyTests
{
    private const string ActorId = "S-1-5-21-1000";
    private const string ChangeReason =
        "Change the publication classification after human review.";

    private static readonly PublicationScope[] KnownScopes =
    [
        PublicationScope.InternalOnly,
        PublicationScope.SpecificGroupOnly,
        PublicationScope.ExternalCandidate,
        PublicationScope.ExternalPublicationApproved,
        PublicationScope.PublicationForbidden,
        PublicationScope.PendingDecision,
    ];

    private static readonly PublicationChangeOrigin[] AutomaticOrigins =
    [
        PublicationChangeOrigin.Inherited,
        PublicationChangeOrigin.System,
    ];

    private static readonly HashSet<(
        PublicationScope Parent,
        PublicationScope Child)> ProvenRestrictionPairs =
    [
        (PublicationScope.InternalOnly, PublicationScope.PendingDecision),
        (PublicationScope.InternalOnly, PublicationScope.PublicationForbidden),
        (PublicationScope.ExternalCandidate, PublicationScope.InternalOnly),
        (PublicationScope.ExternalCandidate, PublicationScope.PendingDecision),
        (PublicationScope.ExternalCandidate, PublicationScope.PublicationForbidden),
        (PublicationScope.ExternalPublicationApproved, PublicationScope.PendingDecision),
        (PublicationScope.ExternalPublicationApproved,
            PublicationScope.PublicationForbidden),
        (PublicationScope.PendingDecision, PublicationScope.PublicationForbidden),
    ];

    [TestMethod]
    public void PolicyEnumsDefineExactlyTheClosedContractValues()
    {
        CollectionAssert.AreEquivalent(
            KnownScopes,
            Enum.GetValues<PublicationScope>());
        CollectionAssert.AreEqual(
            new[]
            {
                PublicationChangeOrigin.Inherited,
                PublicationChangeOrigin.System,
                PublicationChangeOrigin.ExplicitHuman,
            },
            Enum.GetValues<PublicationChangeOrigin>());
        CollectionAssert.AreEqual(
            new[]
            {
                PublicationScopeRelation.SameScope,
                PublicationScopeRelation.ProvenRestriction,
                PublicationScopeRelation.PotentialExpansionOrIncomparable,
            },
            Enum.GetValues<PublicationScopeRelation>());
        CollectionAssert.AreEqual(
            new[]
            {
                PublicationPolicyDisposition.NoChange,
                PublicationPolicyDisposition.AllowedAutomaticRestriction,
                PublicationPolicyDisposition.DeniedAutomaticUnprovenChange,
                PublicationPolicyDisposition.RequiresAuthorizedRestriction,
                PublicationPolicyDisposition.RequiresVersionedChange,
            },
            Enum.GetValues<PublicationPolicyDisposition>());
    }

    [TestMethod]
    public void AutomaticOriginsReturnExactResultsForAllSeventyTwoCells()
    {
        int evaluatedCellCount = 0;
        int allowedRestrictionCount = 0;

        foreach (PublicationChangeOrigin origin in AutomaticOrigins)
        {
            foreach (PublicationScope parentScope in KnownScopes)
            {
                foreach (PublicationScope childScope in KnownScopes)
                {
                    PublicationPolicyDecision decision = PublicationPolicy.Evaluate(
                        parentScope,
                        childScope,
                        origin,
                        reason: null);
                    PublicationScopeRelation expectedRelation = GetExpectedRelation(
                        parentScope,
                        childScope);
                    PublicationPolicyDisposition expectedDisposition =
                        GetExpectedAutomaticDisposition(expectedRelation);

                    AssertDecisionContext(
                        decision,
                        parentScope,
                        childScope,
                        origin,
                        reason: null,
                        expectedRelation,
                        expectedDisposition);
                    AssertNoWorkflowHandoff(decision);
                    if (expectedDisposition
                        == PublicationPolicyDisposition.AllowedAutomaticRestriction)
                    {
                        allowedRestrictionCount++;
                    }

                    evaluatedCellCount++;
                }
            }
        }

        Assert.AreEqual(72, evaluatedCellCount);
        Assert.AreEqual(16, allowedRestrictionCount);
    }

    [TestMethod]
    public void ExplicitHumanReturnsExactHandoffsForAllThirtySixCells()
    {
        int evaluatedCellCount = 0;
        int noChangeCount = 0;
        int authorizedRestrictionCount = 0;
        int versionedChangeCount = 0;

        foreach (PublicationScope parentScope in KnownScopes)
        {
            foreach (PublicationScope childScope in KnownScopes)
            {
                PublicationScopeRelation expectedRelation = GetExpectedRelation(
                    parentScope,
                    childScope);
                string? reason = expectedRelation == PublicationScopeRelation.SameScope
                    ? null
                    : ChangeReason;
                PublicationPolicyDisposition expectedDisposition = expectedRelation switch
                {
                    PublicationScopeRelation.SameScope =>
                        PublicationPolicyDisposition.NoChange,
                    PublicationScopeRelation.ProvenRestriction =>
                        PublicationPolicyDisposition.RequiresAuthorizedRestriction,
                    PublicationScopeRelation.PotentialExpansionOrIncomparable =>
                        PublicationPolicyDisposition.RequiresVersionedChange,
                    _ => throw new InvalidOperationException("Unexpected test relation."),
                };
                PublicationPolicyDecision decision = PublicationPolicy.Evaluate(
                    parentScope,
                    childScope,
                    PublicationChangeOrigin.ExplicitHuman,
                    reason);

                AssertDecisionContext(
                    decision,
                    parentScope,
                    childScope,
                    PublicationChangeOrigin.ExplicitHuman,
                    reason,
                    expectedRelation,
                    expectedDisposition);

                switch (expectedDisposition)
                {
                    case PublicationPolicyDisposition.NoChange:
                        AssertNoWorkflowHandoff(decision);
                        noChangeCount++;
                        break;
                    case PublicationPolicyDisposition.RequiresAuthorizedRestriction:
                        AssertAuthorizedRestrictionHandoff(decision);
                        authorizedRestrictionCount++;
                        break;
                    case PublicationPolicyDisposition.RequiresVersionedChange:
                        AssertVersionedChangeHandoff(decision);
                        versionedChangeCount++;
                        break;
                    default:
                        Assert.Fail("A human evaluation returned an automatic disposition.");
                        break;
                }

                evaluatedCellCount++;
            }
        }

        Assert.AreEqual(36, evaluatedCellCount);
        Assert.AreEqual(6, noChangeCount);
        Assert.AreEqual(8, authorizedRestrictionCount);
        Assert.AreEqual(22, versionedChangeCount);
    }

    [TestMethod]
    public void PriorTotalOrderCounterexamplesAreDeniedForAutomaticOrigins()
    {
        (PublicationScope Parent, PublicationScope Child)[] counterexamples =
        [
            (PublicationScope.InternalOnly, PublicationScope.SpecificGroupOnly),
            (PublicationScope.SpecificGroupOnly, PublicationScope.InternalOnly),
            (PublicationScope.SpecificGroupOnly, PublicationScope.PublicationForbidden),
            (PublicationScope.ExternalCandidate, PublicationScope.SpecificGroupOnly),
            (PublicationScope.ExternalPublicationApproved,
                PublicationScope.ExternalCandidate),
            (PublicationScope.ExternalPublicationApproved, PublicationScope.InternalOnly),
        ];

        foreach (PublicationChangeOrigin origin in AutomaticOrigins)
        {
            foreach ((PublicationScope parent, PublicationScope child) in counterexamples)
            {
                PublicationPolicyDecision decision = PublicationPolicy.Evaluate(
                    parent,
                    child,
                    origin,
                    reason: null);

                Assert.AreEqual(
                    PublicationScopeRelation.PotentialExpansionOrIncomparable,
                    decision.Relation);
                Assert.AreEqual(
                    PublicationPolicyDisposition.DeniedAutomaticUnprovenChange,
                    decision.Disposition);
                AssertNoWorkflowHandoff(decision);
            }
        }
    }

    [TestMethod]
    public void HumanSameScopeUsesLabelEqualityOnlyAndRejectsEveryNonNullReason()
    {
        foreach (PublicationScope scope in KnownScopes)
        {
            PublicationPolicyDecision decision = PublicationPolicy.Evaluate(
                scope,
                scope,
                PublicationChangeOrigin.ExplicitHuman,
                reason: null);

            Assert.AreEqual(PublicationScopeRelation.SameScope, decision.Relation);
            Assert.AreEqual(PublicationPolicyDisposition.NoChange, decision.Disposition);
            AssertNoWorkflowHandoff(decision);

            foreach (string nonNullReason in new[] { string.Empty, ChangeReason })
            {
                AssertParamName<ArgumentException>(
                    "reason",
                    () => PublicationPolicy.Evaluate(
                        scope,
                        scope,
                        PublicationChangeOrigin.ExplicitHuman,
                        nonNullReason));
            }
        }
    }

    [TestMethod]
    public void SameSpecificGroupLabelDoesNotAuthorizeGroupOrDestinationIdentityChange()
    {
        PublicationPolicyDecision decision = PublicationPolicy.Evaluate(
            PublicationScope.SpecificGroupOnly,
            PublicationScope.SpecificGroupOnly,
            PublicationChangeOrigin.ExplicitHuman,
            reason: null);

        Assert.AreEqual(PublicationScopeRelation.SameScope, decision.Relation);
        Assert.AreEqual(PublicationPolicyDisposition.NoChange, decision.Disposition);
        AssertNoWorkflowHandoff(decision);
    }

    [TestMethod]
    public void AutomaticOriginsAcceptOnlyNullReasonForEveryRelation()
    {
        (PublicationScope Parent, PublicationScope Child)[] relationExamples =
        {
            (PublicationScope.InternalOnly, PublicationScope.InternalOnly),
            (PublicationScope.InternalOnly, PublicationScope.PendingDecision),
            (PublicationScope.InternalOnly, PublicationScope.ExternalCandidate),
        };

        foreach (PublicationChangeOrigin origin in AutomaticOrigins)
        {
            foreach ((PublicationScope parent, PublicationScope child) in relationExamples)
            {
                foreach (string nonNullReason in new[] { string.Empty, ChangeReason })
                {
                    AssertParamName<ArgumentException>(
                        "reason",
                        () => PublicationPolicy.Evaluate(
                            parent,
                            child,
                            origin,
                            nonNullReason));
                }
            }
        }
    }

    [TestMethod]
    public void HumanActualChangesRequireNonBlankTrimmedReason()
    {
        foreach ((PublicationScope parent, PublicationScope child) in new[]
        {
            (PublicationScope.InternalOnly, PublicationScope.PendingDecision),
            (PublicationScope.InternalOnly, PublicationScope.ExternalCandidate),
        })
        {
            AssertParamName<ArgumentNullException>(
                "reason",
                () => PublicationPolicy.Evaluate(
                    parent,
                    child,
                    PublicationChangeOrigin.ExplicitHuman,
                    reason: null));
        }

        foreach (string invalidReason in new[]
        {
            string.Empty,
            " ",
            "\t",
            " reason",
            "reason ",
            "\r\nreason",
            "reason\r\n",
        })
        {
            AssertParamName<ArgumentException>(
                "reason",
                () => PublicationPolicy.Evaluate(
                    PublicationScope.InternalOnly,
                    PublicationScope.ExternalCandidate,
                    PublicationChangeOrigin.ExplicitHuman,
                    invalidReason));
        }
    }

    [TestMethod]
    public void HumanActualChangesRejectInvalidUtf16BeforeProducingAHandoff()
    {
        string[] invalidUtf16Reasons =
        [
            new string('\ud800', 1),
            new string('\udc00', 1),
            string.Concat("reason", new string('\ud800', 1)),
        ];

        foreach (string invalidReason in invalidUtf16Reasons)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => PublicationPolicy.Evaluate(
                    PublicationScope.InternalOnly,
                    PublicationScope.ExternalCandidate,
                    PublicationChangeOrigin.ExplicitHuman,
                    invalidReason));

            Assert.AreEqual("reason", exception.ParamName);
            Assert.IsInstanceOfType<EncoderFallbackException>(exception.InnerException);
        }
    }

    [TestMethod]
    public void HumanActualChangesRejectReasonsContainingOnlyInvisibleScalars()
    {
        string[] invisibleOnlyReasons =
        [
            "\u200B",
            "\u200C\u200D\u2060",
            "\0\u0001\u001F",
            "\u200B\0\u2060",
            "\u2028\u2029",
            "\u0301",
            "\u0903",
            "\u20DD",
            "\uFE00",
            "\uFE0F",
            "\U000E0100",
            "\U000E01EF",
            "\u034F",
            "\u115F",
            "\u1160",
            "\u3164",
            "\uFFA0",
            "\u2800",
            "\u0301\u0903\u20DD\uFE0F\u034F\u115F\u1160\u3164\uFFA0\u2800\U000E0100",
        ];

        foreach (string invalidReason in invisibleOnlyReasons)
        {
            AssertParamName<ArgumentException>(
                "reason",
                () => PublicationPolicy.Evaluate(
                    PublicationScope.InternalOnly,
                    PublicationScope.ExternalCandidate,
                    PublicationChangeOrigin.ExplicitHuman,
                    invalidReason));
        }
    }

    [TestMethod]
    public void HumanActualChangesAllowSubstantiveTextWithNonSubstantiveScalars()
    {
        string[] validReasons =
        [
            "公開区分を変更します。",
            "🚀",
            "\uFFFD",
            "\u200B理由\u2060",
            "\0理由",
            "理由\uFE0F",
            "か\u3099",
            "理由\u034F\u3164\u2800\uFFA0",
        ];

        foreach (string validReason in validReasons)
        {
            PublicationPolicyDecision decision = PublicationPolicy.Evaluate(
                PublicationScope.InternalOnly,
                PublicationScope.ExternalCandidate,
                PublicationChangeOrigin.ExplicitHuman,
                validReason);

            Assert.AreEqual(
                PublicationPolicyDisposition.RequiresVersionedChange,
                decision.Disposition);
            Assert.AreEqual(validReason, decision.Reason);
            AssertVersionedChangeHandoff(decision);
        }
    }

    [TestMethod]
    public void UndefinedEnumsFailClosedWithStableParameterNames()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "parentScope",
            () => PublicationPolicy.Evaluate(
                (PublicationScope)int.MaxValue,
                PublicationScope.InternalOnly,
                PublicationChangeOrigin.Inherited,
                reason: null));
        AssertParamName<ArgumentOutOfRangeException>(
            "requestedChildScope",
            () => PublicationPolicy.Evaluate(
                PublicationScope.InternalOnly,
                (PublicationScope)int.MaxValue,
                PublicationChangeOrigin.Inherited,
                reason: null));
        AssertParamName<ArgumentOutOfRangeException>(
            "origin",
            () => PublicationPolicy.Evaluate(
                PublicationScope.InternalOnly,
                PublicationScope.InternalOnly,
                (PublicationChangeOrigin)int.MaxValue,
                reason: null));
    }

    [TestMethod]
    public void EvaluationDoesNotMutateProjectVersionApprovalOrReviewEvidence()
    {
        Project project = new(
            EntityId<ProjectIdTag>.New(),
            "Publication policy project",
            "Verify immutable policy decisions.",
            "Internal reviewers",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);
        CompositionVersion version = CreateVersion(project.Id);
        Approval approval = new(
            EntityId<ApprovalIdTag>.New(),
            version,
            ActorId,
            ReviewActorRole.ContentApprover,
            new DateTimeOffset(2026, 8, 29, 10, 1, 0, TimeSpan.Zero),
            "Approve the internal composition.");
        ReviewRecord review = new(
            EntityId<ReviewRecordIdTag>.New(),
            version,
            ReviewState.Approved,
            ActorId,
            ReviewActorRole.ContentApprover,
            new DateTimeOffset(2026, 8, 29, 10, 2, 0, TimeSpan.Zero),
            "Record the approved review state.");
        List<Approval> approvals = [approval];
        List<ReviewRecord> reviews = [review];
        EntityId<CompositionVersionIdTag> originalVersionId = version.Id;
        ContentDigest originalDigest = version.ContentDigest;
        CompositionElement[] originalElements = version.Elements.ToArray();

        PublicationPolicyDecision decision = PublicationPolicy.Evaluate(
            project.PublicationScope,
            PublicationScope.ExternalCandidate,
            PublicationChangeOrigin.ExplicitHuman,
            ChangeReason);

        AssertVersionedChangeHandoff(decision);
        Assert.AreEqual(PublicationScope.InternalOnly, project.PublicationScope);
        Assert.AreSame(originalVersionId, version.Id);
        Assert.AreSame(originalDigest, version.ContentDigest);
        CollectionAssert.AreEqual(originalElements, version.Elements.ToArray());
        Assert.AreEqual(1, approvals.Count);
        Assert.AreSame(approval, approvals[0]);
        Assert.AreSame(version.Id, approval.CompositionVersionId);
        Assert.AreSame(version.ContentDigest, approval.CompositionVersionDigest);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreSame(review, reviews[0]);
        Assert.AreSame(version.Id, review.CompositionVersionId);
        Assert.AreSame(version.ContentDigest, review.CompositionVersionDigest);
    }

    [TestMethod]
    public void PublicSurfaceIsMinimalTypedAndImmutable()
    {
        Assert.IsTrue(typeof(PublicationPolicy).IsPublic);
        Assert.IsTrue(typeof(PublicationPolicy).IsAbstract);
        Assert.IsTrue(typeof(PublicationPolicy).IsSealed);
        MethodInfo[] policyMethods = typeof(PublicationPolicy).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, policyMethods.Length);
        Assert.AreEqual(nameof(PublicationPolicy.Evaluate), policyMethods[0].Name);
        Assert.AreEqual(typeof(PublicationPolicyDecision), policyMethods[0].ReturnType);
        CollectionAssert.AreEqual(
            new[]
            {
                typeof(PublicationScope),
                typeof(PublicationScope),
                typeof(PublicationChangeOrigin),
                typeof(string),
            },
            policyMethods[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "parentScope",
                "requestedChildScope",
                "origin",
                "reason",
            },
            policyMethods[0].GetParameters()
                .Select(static parameter => parameter.Name)
                .ToArray());

        Assert.IsTrue(typeof(PublicationPolicyDecision).IsPublic);
        Assert.IsTrue(typeof(PublicationPolicyDecision).IsSealed);
        Assert.AreEqual(0, typeof(PublicationPolicyDecision).GetConstructors().Length);
        AssertGetOnlyProperties(
            typeof(PublicationPolicyDecision),
            "ParentScope",
            "RequestedChildScope",
            "Origin",
            "Reason",
            "Relation",
            "Disposition",
            "RequiredAuthorizationOperation",
            "RequiredActingRole",
            "CreateChildCompositionVersion",
            "InvalidateExistingReviewEvidence",
            "ReevaluateRequiredReviews",
            "RequiredNextState",
            "AuditRequired");
        Assert.AreEqual(
            typeof(PublicationScopeRelation),
            typeof(PublicationPolicyDecision).GetProperty("Relation")!.PropertyType);
        Assert.AreEqual(
            typeof(PublicationPolicyDisposition),
            typeof(PublicationPolicyDecision).GetProperty("Disposition")!.PropertyType);
        Assert.AreEqual(
            typeof(AuthorizationRole?),
            typeof(PublicationPolicyDecision)
                .GetProperty("RequiredActingRole")!
                .PropertyType);
        Assert.AreEqual(
            typeof(ReviewState?),
            typeof(PublicationPolicyDecision).GetProperty("RequiredNextState")!.PropertyType);
        Assert.IsFalse(typeof(PublicationPolicyDecision).GetProperties().Any(
            static property =>
                property.PropertyType == typeof(CompositionVersion)
                || property.PropertyType == typeof(Approval)
                || property.PropertyType == typeof(ReviewRecord)
                || typeof(System.Collections.IEnumerable).IsAssignableFrom(
                    property.PropertyType)
                    && property.PropertyType != typeof(string)));
        Assert.AreEqual(
            0,
            typeof(PublicationPolicyDecision).GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Count(static method => !method.IsSpecialName));
    }

    [TestMethod]
    public void InternalConstructorRejectsContradictoryVersionedChangeFlags()
    {
        ConstructorInfo constructor = typeof(PublicationPolicyDecision)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();

        TargetInvocationException exception =
            Assert.ThrowsExactly<TargetInvocationException>(
                () => constructor.Invoke(
                [
                    PublicationScope.InternalOnly,
                    PublicationScope.ExternalCandidate,
                    PublicationChangeOrigin.ExplicitHuman,
                    ChangeReason,
                    PublicationScopeRelation.PotentialExpansionOrIncomparable,
                    PublicationPolicyDisposition.RequiresVersionedChange,
                    AuthorizationOperation.ChangePublicationClassification,
                    AuthorizationRole.ContentApprover,
                    false,
                    true,
                    true,
                    ReviewState.Editing,
                    true,
                ]));

        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
    }

    [TestMethod]
    public void InternalConstructorRejectsRelationAndDispositionThatContradictScopes()
    {
        ConstructorInfo constructor = typeof(PublicationPolicyDecision)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();

        TargetInvocationException exception =
            Assert.ThrowsExactly<TargetInvocationException>(
                () => constructor.Invoke(
                [
                    PublicationScope.InternalOnly,
                    PublicationScope.ExternalCandidate,
                    PublicationChangeOrigin.System,
                    null,
                    PublicationScopeRelation.ProvenRestriction,
                    PublicationPolicyDisposition.AllowedAutomaticRestriction,
                    null,
                    null,
                    false,
                    false,
                    false,
                    null,
                    false,
                ]));

        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
    }

    [TestMethod]
    public void InternalConstructorRejectsInvalidReasonsForVersionedChanges()
    {
        ConstructorInfo constructor = typeof(PublicationPolicyDecision)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        string?[] invalidReasons =
        [
            null,
            string.Empty,
            " ",
            " reason",
            "reason ",
            "\u200B",
            "\uFE0F",
            new string('\ud800', 1),
        ];

        foreach (string? invalidReason in invalidReasons)
        {
            TargetInvocationException exception =
                Assert.ThrowsExactly<TargetInvocationException>(
                    () => constructor.Invoke(
                    [
                        PublicationScope.InternalOnly,
                        PublicationScope.ExternalCandidate,
                        PublicationChangeOrigin.ExplicitHuman,
                        invalidReason,
                        PublicationScopeRelation.PotentialExpansionOrIncomparable,
                        PublicationPolicyDisposition.RequiresVersionedChange,
                        AuthorizationOperation.ChangePublicationClassification,
                        AuthorizationRole.ContentApprover,
                        true,
                        true,
                        true,
                        ReviewState.Editing,
                        true,
                    ]));

            Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        }
    }

    private static PublicationScopeRelation GetExpectedRelation(
        PublicationScope parentScope,
        PublicationScope childScope)
    {
        if (parentScope == childScope)
        {
            return PublicationScopeRelation.SameScope;
        }

        return ProvenRestrictionPairs.Contains((parentScope, childScope))
            ? PublicationScopeRelation.ProvenRestriction
            : PublicationScopeRelation.PotentialExpansionOrIncomparable;
    }

    private static PublicationPolicyDisposition GetExpectedAutomaticDisposition(
        PublicationScopeRelation relation)
    {
        return relation switch
        {
            PublicationScopeRelation.SameScope =>
                PublicationPolicyDisposition.NoChange,
            PublicationScopeRelation.ProvenRestriction =>
                PublicationPolicyDisposition.AllowedAutomaticRestriction,
            PublicationScopeRelation.PotentialExpansionOrIncomparable =>
                PublicationPolicyDisposition.DeniedAutomaticUnprovenChange,
            _ => throw new InvalidOperationException("Unexpected test relation."),
        };
    }

    private static void AssertDecisionContext(
        PublicationPolicyDecision decision,
        PublicationScope parentScope,
        PublicationScope childScope,
        PublicationChangeOrigin origin,
        string? reason,
        PublicationScopeRelation relation,
        PublicationPolicyDisposition disposition)
    {
        Assert.AreEqual(parentScope, decision.ParentScope);
        Assert.AreEqual(childScope, decision.RequestedChildScope);
        Assert.AreEqual(origin, decision.Origin);
        Assert.AreEqual(reason, decision.Reason);
        Assert.AreEqual(relation, decision.Relation);
        Assert.AreEqual(disposition, decision.Disposition);
    }

    private static void AssertAuthorizedRestrictionHandoff(
        PublicationPolicyDecision decision)
    {
        Assert.AreEqual(
            AuthorizationOperation.ChangePublicationClassification,
            decision.RequiredAuthorizationOperation);
        Assert.AreEqual(
            AuthorizationRole.ContentApprover,
            decision.RequiredActingRole);
        Assert.IsFalse(decision.CreateChildCompositionVersion);
        Assert.IsFalse(decision.InvalidateExistingReviewEvidence);
        Assert.IsFalse(decision.ReevaluateRequiredReviews);
        Assert.IsNull(decision.RequiredNextState);
        Assert.IsTrue(decision.AuditRequired);
    }

    private static void AssertVersionedChangeHandoff(
        PublicationPolicyDecision decision)
    {
        Assert.AreEqual(
            AuthorizationOperation.ChangePublicationClassification,
            decision.RequiredAuthorizationOperation);
        Assert.AreEqual(
            AuthorizationRole.ContentApprover,
            decision.RequiredActingRole);
        Assert.IsTrue(decision.CreateChildCompositionVersion);
        Assert.IsTrue(decision.InvalidateExistingReviewEvidence);
        Assert.IsTrue(decision.ReevaluateRequiredReviews);
        Assert.AreEqual(ReviewState.Editing, decision.RequiredNextState);
        Assert.IsTrue(decision.AuditRequired);
    }

    private static void AssertNoWorkflowHandoff(
        PublicationPolicyDecision decision)
    {
        Assert.IsNull(decision.RequiredAuthorizationOperation);
        Assert.IsNull(decision.RequiredActingRole);
        Assert.IsFalse(decision.CreateChildCompositionVersion);
        Assert.IsFalse(decision.InvalidateExistingReviewEvidence);
        Assert.IsFalse(decision.ReevaluateRequiredReviews);
        Assert.IsNull(decision.RequiredNextState);
        Assert.IsFalse(decision.AuditRequired);
    }

    private static CompositionVersion CreateVersion(
        EntityId<ProjectIdTag>? projectId = null)
    {
        return new CompositionVersion(
            EntityId<CompositionVersionIdTag>.New(),
            projectId ?? EntityId<ProjectIdTag>.New(),
            parentVersionId: null,
            ActorId,
            new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero),
            "Create a publication-policy parent version.",
            new ContentDigest(new string('a', 64)),
            elements: []);
    }

    private static void AssertGetOnlyProperties(Type type, params string[] expectedNames)
    {
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}