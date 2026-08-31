using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ProjectCreationDraftTests
{
    private const string ProfileDigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string TemplateDigestHex =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [TestMethod]
    public void ContractEnumsAndMarkersHaveTheRequiredDeterministicShape()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                ProjectCreationField.SourceMedia,
                ProjectCreationField.ContentName,
                ProjectCreationField.Purpose,
                ProjectCreationField.Audience,
                ProjectCreationField.PublicationScope,
                ProjectCreationField.OutputFormats,
                ProjectCreationField.Language,
                ProjectCreationField.PerformerNames,
                ProjectCreationField.Deadline,
                ProjectCreationField.BrandTemplate,
            },
            Enum.GetValues<ProjectCreationField>());
        Assert.IsTrue(typeof(ProjectCreationDraftIdTag).IsPublic);
        Assert.IsTrue(typeof(ProjectCreationDraftIdTag).IsSealed);
    }

    [TestMethod]
    public void EmptyDraftPreservesMissingValuesInRequirementOrder()
    {
        ProjectCreationDraft draft = CreateDraft(
            includeSourceCandidate: false,
            contentName: null,
            purpose: null,
            audience: null,
            publicationScope: null,
            outputProfiles: [],
            language: null,
            performerNames: [],
            includeDeadlineSelection: false,
            includeBrandTemplate: false);

        CollectionAssert.AreEqual(
            Enum.GetValues<ProjectCreationField>(),
            draft.MissingFields.ToArray());
        Assert.IsFalse(draft.IsComplete);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<ProjectCreationField>)draft.MissingFields).Clear());
    }

    [TestMethod]
    public void CompleteDraftStoresAllInputsAndDefensivelyCopiesCollections()
    {
        EntityId<ProjectCreationDraftIdTag> id = EntityId<ProjectCreationDraftIdTag>.New();
        EntityId<AssetIdTag> candidateId = EntityId<AssetIdTag>.New();
        ProjectOutputProfileReference outputProfile = CreateOutputProfile();
        TemplateReference brandTemplate = CreateBrandTemplate();
        DateTimeOffset deadline = new(2026, 10, 15, 9, 30, 0, TimeSpan.FromHours(9));
        List<ProjectOutputProfileReference> outputProfiles = [outputProfile];
        List<string> performerNames = ["Host A", "Guest B"];

        ProjectCreationDraft draft = new(
            id,
            candidateId,
            "<script>text stays data</script>",
            "Explain the release",
            "Internal engineering audience",
            PublicationScope.ExternalCandidate,
            outputProfiles,
            "ja-JP",
            performerNames,
            new ProjectDeadlineSelection(deadline),
            brandTemplate);
        outputProfiles.Clear();
        performerNames[0] = "Changed";
        performerNames.Clear();

        Assert.AreEqual(id, draft.Id);
        Assert.AreEqual(candidateId, draft.SourceCandidateId);
        Assert.AreEqual("<script>text stays data</script>", draft.ContentName);
        Assert.AreEqual("Explain the release", draft.Purpose);
        Assert.AreEqual("Internal engineering audience", draft.Audience);
        Assert.AreEqual(PublicationScope.ExternalCandidate, draft.PublicationScope);
        Assert.HasCount(1, draft.OutputProfiles);
        Assert.AreSame(outputProfile, draft.OutputProfiles[0]);
        Assert.AreEqual("ja-JP", draft.Language);
        CollectionAssert.AreEqual(
            new[] { "Host A", "Guest B" },
            draft.PerformerNames.ToArray());
        Assert.AreEqual(deadline, draft.DeadlineSelection?.Deadline);
        Assert.AreSame(brandTemplate, draft.BrandTemplate);
        Assert.HasCount(0, draft.MissingFields);
        Assert.IsTrue(draft.IsComplete);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<ProjectOutputProfileReference>)draft.OutputProfiles).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<string>)draft.PerformerNames).Clear());
    }

    [TestMethod]
    public void ExplicitNoDeadlineCountsAsAnAnsweredRequiredInput()
    {
        ProjectCreationDraft draft = CreateDraft(
            deadlineSelection: new ProjectDeadlineSelection(deadline: null));

        Assert.IsTrue(draft.IsComplete);
        Assert.IsNotNull(draft.DeadlineSelection);
        Assert.IsNull(draft.DeadlineSelection.Deadline);
        Assert.IsFalse(draft.MissingFields.Contains(ProjectCreationField.Deadline));
    }

    [TestMethod]
    public void MissingFieldsReflectOnlyActuallyUnansweredInputs()
    {
        ProjectCreationDraft draft = CreateDraft(
            includeSourceCandidate: false,
            audience: null,
            outputProfiles: [],
            performerNames: []);

        CollectionAssert.AreEqual(
            new[]
            {
                ProjectCreationField.SourceMedia,
                ProjectCreationField.Audience,
                ProjectCreationField.OutputFormats,
                ProjectCreationField.PerformerNames,
            },
            draft.MissingFields.ToArray());
    }

    [TestMethod]
    public void DraftRejectsNullRequiredReferencesAndUnknownScope()
    {
        EntityId<ProjectCreationDraftIdTag> id = EntityId<ProjectCreationDraftIdTag>.New();
        EntityId<AssetIdTag> candidateId = EntityId<AssetIdTag>.New();
        ProjectOutputProfileReference outputProfile = CreateOutputProfile();
        ProjectDeadlineSelection deadline = new(deadline: null);
        TemplateReference template = CreateBrandTemplate();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProjectCreationDraft(
                null!,
                candidateId,
                "Content",
                "Purpose",
                "Audience",
                PublicationScope.InternalOnly,
                [outputProfile],
                "ja-JP",
                ["Host A"],
                deadline,
                template));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProjectCreationDraft(
                id,
                candidateId,
                "Content",
                "Purpose",
                "Audience",
                PublicationScope.InternalOnly,
                null!,
                "ja-JP",
                ["Host A"],
                deadline,
                template));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProjectCreationDraft(
                id,
                candidateId,
                "Content",
                "Purpose",
                "Audience",
                PublicationScope.InternalOnly,
                [outputProfile],
                "ja-JP",
                null!,
                deadline,
                template));
        AssertParamName<ArgumentOutOfRangeException>(
            "publicationScope",
            () => CreateDraft(publicationScope: (PublicationScope)999));
    }

    [TestMethod]
    public void DraftRejectsAmbiguousOrMalformedOptionalText()
    {
        string[] invalid =
        [
            string.Empty,
            " ",
            " leading",
            "trailing ",
            "\u200b",
            "\ud800",
        ];

        foreach (string value in invalid)
        {
            AssertParamName<ArgumentException>(
                "contentName",
                () => CreateDraft(contentName: value));
            AssertParamName<ArgumentException>(
                "purpose",
                () => CreateDraft(purpose: value));
            AssertParamName<ArgumentException>(
                "audience",
                () => CreateDraft(audience: value));
            AssertParamName<ArgumentException>(
                "language",
                () => CreateDraft(language: value));
        }
    }

    [TestMethod]
    public void DraftRejectsNullAndDuplicateCollectionValues()
    {
        ProjectOutputProfileReference outputProfile = CreateOutputProfile();
        ProjectOutputProfileReference duplicateProfile = new(
            EntityId<RenderProfileIdTag>.From(outputProfile.Id.Value),
            "different-name",
            new ContentDigest(new string('0', 64)));

        AssertParamName<ArgumentException>(
            "outputProfiles",
            () => CreateDraft(outputProfiles: [null!]));
        AssertParamName<ArgumentException>(
            "outputProfiles",
            () => CreateDraft(outputProfiles: [outputProfile, duplicateProfile]));
        AssertParamName<ArgumentException>(
            "performerNames",
            () => CreateDraft(performerNames: [null!]));
        AssertParamName<ArgumentException>(
            "performerNames",
            () => CreateDraft(performerNames: ["Host A", "Host A"]));
        AssertParamName<ArgumentException>(
            "performerNames",
            () => CreateDraft(performerNames: ["Host A", " Host B"]));
    }

    [TestMethod]
    public void OutputProfileReferenceValidatesAndUsesValueEquality()
    {
        EntityId<RenderProfileIdTag> id = EntityId<RenderProfileIdTag>.New();
        ProjectOutputProfileReference first = CreateOutputProfile(id);
        ProjectOutputProfileReference equivalent = new(
            EntityId<RenderProfileIdTag>.From(id.Value),
            first.Name,
            new ContentDigest(first.ProfileDigest.Hex.ToUpperInvariant()));

        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        Assert.AreNotEqual(first, CreateOutputProfile());
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProjectOutputProfileReference(null!, "profile", first.ProfileDigest));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProjectOutputProfileReference(id, "profile", null!));
        AssertParamName<ArgumentException>(
            "name",
            () => new ProjectOutputProfileReference(id, " ", first.ProfileDigest));
        AssertParamName<ArgumentException>(
            "name",
            () => new ProjectOutputProfileReference(id, "\ud800", first.ProfileDigest));
    }

    [TestMethod]
    public void DeadlineSelectionHasExplicitValueEquality()
    {
        DateTimeOffset instant = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

        Assert.AreEqual(
            new ProjectDeadlineSelection(instant),
            new ProjectDeadlineSelection(instant));
        Assert.AreEqual(
            new ProjectDeadlineSelection(deadline: null),
            new ProjectDeadlineSelection(deadline: null));
        Assert.AreNotEqual(
            new ProjectDeadlineSelection(instant),
            new ProjectDeadlineSelection(deadline: null));
    }

    private static ProjectCreationDraft CreateDraft(
        bool includeSourceCandidate = true,
        string? contentName = "Hub Radio Episode",
        string? purpose = "Explain the release",
        string? audience = "Internal engineering audience",
        PublicationScope? publicationScope = PublicationScope.InternalOnly,
        IReadOnlyList<ProjectOutputProfileReference>? outputProfiles = null,
        string? language = "ja-JP",
        IReadOnlyList<string>? performerNames = null,
        ProjectDeadlineSelection? deadlineSelection = null,
        bool includeDeadlineSelection = true,
        bool includeBrandTemplate = true)
    {
        return new ProjectCreationDraft(
            EntityId<ProjectCreationDraftIdTag>.New(),
            includeSourceCandidate ? EntityId<AssetIdTag>.New() : null,
            contentName,
            purpose,
            audience,
            publicationScope,
            outputProfiles ?? [CreateOutputProfile()],
            language,
            performerNames ?? ["Host A"],
            includeDeadlineSelection
                ? deadlineSelection ?? new ProjectDeadlineSelection(deadline: null)
                : null,
            includeBrandTemplate ? CreateBrandTemplate() : null);
    }

    private static ProjectOutputProfileReference CreateOutputProfile(
        EntityId<RenderProfileIdTag>? id = null)
    {
        return new ProjectOutputProfileReference(
            id ?? EntityId<RenderProfileIdTag>.New(),
            "long-form-1280x720",
            new ContentDigest(ProfileDigestHex));
    }

    private static TemplateReference CreateBrandTemplate()
    {
        return new TemplateReference(
            EntityId<TemplateIdTag>.New(),
            "development-neutral-long-form",
            new ContentDigest(TemplateDigestHex));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}