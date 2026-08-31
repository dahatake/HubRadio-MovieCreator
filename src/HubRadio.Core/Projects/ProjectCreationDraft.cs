using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;

namespace HubRadio.Core.Projects;

public enum ProjectCreationField
{
    SourceMedia,
    ContentName,
    Purpose,
    Audience,
    PublicationScope,
    OutputFormats,
    Language,
    PerformerNames,
    Deadline,
    BrandTemplate,
}

public sealed class ProjectCreationDraftIdTag
{
}

public sealed class ProjectDeadlineSelection : IEquatable<ProjectDeadlineSelection>
{
    public ProjectDeadlineSelection(DateTimeOffset? deadline)
    {
        Deadline = deadline;
    }

    public DateTimeOffset? Deadline { get; }

    public bool Equals(ProjectDeadlineSelection? other)
    {
        return other is not null && Deadline == other.Deadline;
    }

    public override bool Equals(object? obj)
    {
        return obj is ProjectDeadlineSelection other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Deadline.GetHashCode();
    }
}

public sealed class ProjectOutputProfileReference : IEquatable<ProjectOutputProfileReference>
{
    public ProjectOutputProfileReference(
        EntityId<RenderProfileIdTag> id,
        string name,
        ContentDigest profileDigest)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(profileDigest);

        Id = id;
        Name = ContractText.RequireNonBlankStrictUtf8Substantive(
            name,
            nameof(name),
            "An output-profile name");
        ProfileDigest = profileDigest;
    }

    public EntityId<RenderProfileIdTag> Id { get; }

    public string Name { get; }

    public ContentDigest ProfileDigest { get; }

    public bool Equals(ProjectOutputProfileReference? other)
    {
        return other is not null
            && Id == other.Id
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && ProfileDigest == other.ProfileDigest;
    }

    public override bool Equals(object? obj)
    {
        return obj is ProjectOutputProfileReference other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Id,
            StringComparer.Ordinal.GetHashCode(Name),
            ProfileDigest);
    }
}

public sealed class ProjectCreationDraft
{
    public ProjectCreationDraft(
        EntityId<ProjectCreationDraftIdTag> id,
        EntityId<AssetIdTag>? sourceCandidateId,
        string? contentName,
        string? purpose,
        string? audience,
        PublicationScope? publicationScope,
        IReadOnlyList<ProjectOutputProfileReference> outputProfiles,
        string? language,
        IReadOnlyList<string> performerNames,
        ProjectDeadlineSelection? deadlineSelection,
        TemplateReference? brandTemplate)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(outputProfiles);
        ArgumentNullException.ThrowIfNull(performerNames);

        if (publicationScope is PublicationScope scope && !Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(publicationScope),
                publicationScope,
                "Unknown publication scope.");
        }

        ProjectOutputProfileReference[] copiedOutputProfiles =
            CopyOutputProfiles(outputProfiles);
        string[] copiedPerformerNames = CopyPerformerNames(performerNames);

        Id = id;
        SourceCandidateId = sourceCandidateId;
        ContentName = ValidateOptionalText(contentName, nameof(contentName), "A content name");
        Purpose = ValidateOptionalText(purpose, nameof(purpose), "A purpose");
        Audience = ValidateOptionalText(audience, nameof(audience), "An audience");
        PublicationScope = publicationScope;
        OutputProfiles = Array.AsReadOnly(copiedOutputProfiles);
        Language = ValidateOptionalText(language, nameof(language), "A language");
        PerformerNames = Array.AsReadOnly(copiedPerformerNames);
        DeadlineSelection = deadlineSelection;
        BrandTemplate = brandTemplate;
        MissingFields = Array.AsReadOnly(BuildMissingFields());
    }

    public EntityId<ProjectCreationDraftIdTag> Id { get; }

    public EntityId<AssetIdTag>? SourceCandidateId { get; }

    public string? ContentName { get; }

    public string? Purpose { get; }

    public string? Audience { get; }

    public PublicationScope? PublicationScope { get; }

    public IReadOnlyList<ProjectOutputProfileReference> OutputProfiles { get; }

    public string? Language { get; }

    public IReadOnlyList<string> PerformerNames { get; }

    public ProjectDeadlineSelection? DeadlineSelection { get; }

    public TemplateReference? BrandTemplate { get; }

    public IReadOnlyList<ProjectCreationField> MissingFields { get; }

    public bool IsComplete => MissingFields.Count == 0;

    private static ProjectOutputProfileReference[] CopyOutputProfiles(
        IReadOnlyList<ProjectOutputProfileReference> outputProfiles)
    {
        var ids = new HashSet<EntityId<RenderProfileIdTag>>();
        ProjectOutputProfileReference[] copied = new ProjectOutputProfileReference[outputProfiles.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            ProjectOutputProfileReference? outputProfile = outputProfiles[index];
            if (outputProfile is null || !ids.Add(outputProfile.Id))
            {
                throw new ArgumentException(
                    "Output profiles must contain non-null values with unique IDs.",
                    nameof(outputProfiles));
            }

            copied[index] = outputProfile;
        }

        return copied;
    }

    private static string[] CopyPerformerNames(IReadOnlyList<string> performerNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string[] copied = new string[performerNames.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            string performerName;
            try
            {
                performerName = ContractText.RequireNonBlankStrictUtf8Substantive(
                    performerNames[index],
                    nameof(performerNames),
                    "A performer name");
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "Performer names must contain nonblank, valid Unicode values.",
                    nameof(performerNames),
                    exception);
            }

            if (!names.Add(performerName))
            {
                throw new ArgumentException(
                    "Performer names must not contain exact duplicates.",
                    nameof(performerNames));
            }

            copied[index] = performerName;
        }

        return copied;
    }

    private static string? ValidateOptionalText(
        string? value,
        string parameterName,
        string description)
    {
        return value is null
            ? null
            : ContractText.RequireNonBlankStrictUtf8Substantive(
                value,
                parameterName,
                description);
    }

    private ProjectCreationField[] BuildMissingFields()
    {
        var missing = new List<ProjectCreationField>(10);
        AddIfMissing(SourceCandidateId is null, ProjectCreationField.SourceMedia);
        AddIfMissing(ContentName is null, ProjectCreationField.ContentName);
        AddIfMissing(Purpose is null, ProjectCreationField.Purpose);
        AddIfMissing(Audience is null, ProjectCreationField.Audience);
        AddIfMissing(PublicationScope is null, ProjectCreationField.PublicationScope);
        AddIfMissing(OutputProfiles.Count == 0, ProjectCreationField.OutputFormats);
        AddIfMissing(Language is null, ProjectCreationField.Language);
        AddIfMissing(PerformerNames.Count == 0, ProjectCreationField.PerformerNames);
        AddIfMissing(DeadlineSelection is null, ProjectCreationField.Deadline);
        AddIfMissing(BrandTemplate is null, ProjectCreationField.BrandTemplate);
        return missing.ToArray();

        void AddIfMissing(bool condition, ProjectCreationField field)
        {
            if (condition)
            {
                missing.Add(field);
            }
        }
    }
}