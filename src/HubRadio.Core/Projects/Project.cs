using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Projects;

public enum PublicationScope
{
    InternalOnly,
    SpecificGroupOnly,
    ExternalCandidate,
    ExternalPublicationApproved,
    PublicationForbidden,
    PendingDecision,
}

public sealed class ProjectIdTag
{
}

public sealed class Project
{
    public Project(
        EntityId<ProjectIdTag> id,
        string contentName,
        string purpose,
        string audience,
        PublicationScope publicationScope,
        DateTimeOffset? deadline,
        IReadOnlyList<Asset> assets,
        IReadOnlyList<Performer> performers)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(contentName);
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(audience);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(performers);

        ValidateRequiredText(contentName, nameof(contentName), "A content name");
        ValidateRequiredText(purpose, nameof(purpose), "A purpose");
        ValidateRequiredText(audience, nameof(audience), "An audience");

        if (!Enum.IsDefined(publicationScope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(publicationScope),
                publicationScope,
                "Unknown publication scope.");
        }

        Asset[] copiedAssets = new Asset[assets.Count];
        HashSet<EntityId<AssetIdTag>> assetIds = [];
        for (int index = 0; index < copiedAssets.Length; index++)
        {
            Asset? asset = assets[index];
            if (asset is null)
            {
                throw new ArgumentException(
                    "Assets must not contain null elements.",
                    nameof(assets));
            }

            if (!assetIds.Add(asset.Id))
            {
                throw new ArgumentException(
                    "Assets must not contain duplicate IDs.",
                    nameof(assets));
            }

            copiedAssets[index] = asset;
        }

        Performer[] copiedPerformers = new Performer[performers.Count];
        HashSet<EntityId<PerformerIdTag>> performerIds = [];
        for (int index = 0; index < copiedPerformers.Length; index++)
        {
            Performer? performer = performers[index];
            if (performer is null)
            {
                throw new ArgumentException(
                    "Performers must not contain null elements.",
                    nameof(performers));
            }

            if (!performerIds.Add(performer.Id))
            {
                throw new ArgumentException(
                    "Performers must not contain duplicate IDs.",
                    nameof(performers));
            }

            copiedPerformers[index] = performer;
        }

        Id = id;
        ContentName = contentName;
        Purpose = purpose;
        Audience = audience;
        PublicationScope = publicationScope;
        Deadline = deadline;
        Assets = Array.AsReadOnly(copiedAssets);
        Performers = Array.AsReadOnly(copiedPerformers);
    }

    public EntityId<ProjectIdTag> Id { get; }

    public string ContentName { get; }

    public string Purpose { get; }

    public string Audience { get; }

    public PublicationScope PublicationScope { get; }

    public DateTimeOffset? Deadline { get; }

    public IReadOnlyList<Asset> Assets { get; }

    public IReadOnlyList<Performer> Performers { get; }

    private static void ValidateRequiredText(
        string value,
        string parameterName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must not be blank or have leading or trailing whitespace.",
                parameterName);
        }
    }
}