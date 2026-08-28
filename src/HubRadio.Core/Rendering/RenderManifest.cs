using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Rendering;

public sealed class RenderManifestIdTag
{
}

public sealed class DeliveryManifestIdTag
{
}

public sealed class RenderInputEvidence : IEquatable<RenderInputEvidence>
{
    public RenderInputEvidence(string name, ContentDigest contentDigest)
    {
        ArgumentNullException.ThrowIfNull(contentDigest);

        Name = ContractText.RequireNonBlank(name, nameof(name), "A render input name");
        ContentDigest = contentDigest;
    }

    public string Name { get; }

    public ContentDigest ContentDigest { get; }

    public bool Equals(RenderInputEvidence? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && ContentDigest == other.ContentDigest;
    }

    public override bool Equals(object? obj)
    {
        return obj is RenderInputEvidence other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(StringComparer.Ordinal.GetHashCode(Name), ContentDigest);
    }
}

public sealed class RenderToolEvidence : IEquatable<RenderToolEvidence>
{
    public RenderToolEvidence(
        string name,
        string version,
        string buildConfiguration,
        ContentDigest executableDigest,
        ContentDigest? containerImageDigest)
    {
        ArgumentNullException.ThrowIfNull(executableDigest);

        Name = ContractText.RequireNonBlank(name, nameof(name), "A render tool name");
        Version = ContractText.RequireNonBlank(version, nameof(version), "A render tool version");
        BuildConfiguration = ContractText.RequireNonBlank(
            buildConfiguration,
            nameof(buildConfiguration),
            "A render tool build configuration");
        ExecutableDigest = executableDigest;
        ContainerImageDigest = containerImageDigest;
    }

    public string Name { get; }

    public string Version { get; }

    public string BuildConfiguration { get; }

    public ContentDigest ExecutableDigest { get; }

    public ContentDigest? ContainerImageDigest { get; }

    public bool Equals(RenderToolEvidence? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && StringComparer.Ordinal.Equals(Version, other.Version)
            && StringComparer.Ordinal.Equals(BuildConfiguration, other.BuildConfiguration)
            && ExecutableDigest == other.ExecutableDigest
            && ContainerImageDigest == other.ContainerImageDigest;
    }

    public override bool Equals(object? obj)
    {
        return obj is RenderToolEvidence other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(Name),
            StringComparer.Ordinal.GetHashCode(Version),
            StringComparer.Ordinal.GetHashCode(BuildConfiguration),
            ExecutableDigest,
            ContainerImageDigest);
    }
}

public sealed class RenderModelEvidence : IEquatable<RenderModelEvidence>
{
    public RenderModelEvidence(
        string provider,
        string modelIdentifier,
        string modelVersion,
        ContentDigest weightsDigest)
    {
        ArgumentNullException.ThrowIfNull(weightsDigest);

        Provider = ContractText.RequireNonBlank(provider, nameof(provider), "A model provider");
        ModelIdentifier = ContractText.RequireNonBlank(
            modelIdentifier,
            nameof(modelIdentifier),
            "A model identifier");
        ModelVersion = ContractText.RequireNonBlank(
            modelVersion,
            nameof(modelVersion),
            "A model version");
        WeightsDigest = weightsDigest;
    }

    public string Provider { get; }

    public string ModelIdentifier { get; }

    public string ModelVersion { get; }

    public ContentDigest WeightsDigest { get; }

    public bool Equals(RenderModelEvidence? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Provider, other.Provider)
            && StringComparer.Ordinal.Equals(ModelIdentifier, other.ModelIdentifier)
            && StringComparer.Ordinal.Equals(ModelVersion, other.ModelVersion)
            && WeightsDigest == other.WeightsDigest;
    }

    public override bool Equals(object? obj)
    {
        return obj is RenderModelEvidence other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(Provider),
            StringComparer.Ordinal.GetHashCode(ModelIdentifier),
            StringComparer.Ordinal.GetHashCode(ModelVersion),
            WeightsDigest);
    }
}

public sealed class RenderFontEvidence : IEquatable<RenderFontEvidence>
{
    public RenderFontEvidence(string name, ContentDigest fileDigest)
    {
        ArgumentNullException.ThrowIfNull(fileDigest);

        Name = ContractText.RequireNonBlank(name, nameof(name), "A font name");
        FileDigest = fileDigest;
    }

    public string Name { get; }

    public ContentDigest FileDigest { get; }

    public bool Equals(RenderFontEvidence? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Name, other.Name)
            && FileDigest == other.FileDigest;
    }

    public override bool Equals(object? obj)
    {
        return obj is RenderFontEvidence other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(StringComparer.Ordinal.GetHashCode(Name), FileDigest);
    }
}

public sealed class ManifestArtifact : IEquatable<ManifestArtifact>
{
    public ManifestArtifact(
        string artifactType,
        string mediaType,
        string? language,
        long byteSize,
        ContentDigest sha256,
        string storageReference)
    {
        ArgumentNullException.ThrowIfNull(sha256);

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                byteSize,
                "A manifest artifact byte size must be positive.");
        }

        ArtifactType = ContractText.RequireNonBlank(
            artifactType,
            nameof(artifactType),
            "An artifact type");
        MediaType = ContractText.RequireNonBlank(
            mediaType,
            nameof(mediaType),
            "An artifact media type");
        Language = language is null
            ? null
            : ContractText.RequireNonBlank(language, nameof(language), "An artifact language");
        ByteSize = byteSize;
        Sha256 = sha256;
        StorageReference = ContractText.RequireNonBlank(
            storageReference,
            nameof(storageReference),
            "An artifact storage reference");
    }

    public string ArtifactType { get; }

    public string MediaType { get; }

    public string? Language { get; }

    public long ByteSize { get; }

    public ContentDigest Sha256 { get; }

    public string StorageReference { get; }

    public bool Equals(ManifestArtifact? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(ArtifactType, other.ArtifactType)
            && StringComparer.Ordinal.Equals(MediaType, other.MediaType)
            && StringComparer.Ordinal.Equals(Language, other.Language)
            && ByteSize == other.ByteSize
            && Sha256 == other.Sha256
            && StringComparer.Ordinal.Equals(StorageReference, other.StorageReference);
    }

    public override bool Equals(object? obj)
    {
        return obj is ManifestArtifact other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(ArtifactType, StringComparer.Ordinal);
        hash.Add(MediaType, StringComparer.Ordinal);
        hash.Add(Language, StringComparer.Ordinal);
        hash.Add(ByteSize);
        hash.Add(Sha256);
        hash.Add(StorageReference, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed class RenderManifest
{
    public RenderManifest(
        EntityId<RenderManifestIdTag> id,
        CompositionVersion compositionVersion,
        TimelineMap timelineMap,
        ContentDigest timelineMapDigest,
        RenderProfile profile,
        ContentDigest configurationDigest,
        string applicationVersion,
        ContentDigest applicationDigest,
        IReadOnlyList<RenderInputEvidence> inputs,
        IReadOnlyList<RenderToolEvidence> tools,
        IReadOnlyList<RenderModelEvidence> models,
        IReadOnlyList<RenderFontEvidence> fonts,
        IReadOnlyList<ManifestArtifact> outputs)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(compositionVersion);
        ArgumentNullException.ThrowIfNull(timelineMap);
        ArgumentNullException.ThrowIfNull(timelineMapDigest);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(configurationDigest);
        ArgumentNullException.ThrowIfNull(applicationDigest);

        if (timelineMap.CompositionVersionId != compositionVersion.Id)
        {
            throw new ArgumentException(
                "The timeline map must reference the rendered composition version.",
                nameof(timelineMap));
        }

        Id = id;
        ProjectId = compositionVersion.ProjectId;
        CompositionVersionId = compositionVersion.Id;
        CompositionVersionDigest = compositionVersion.ContentDigest;
        TimelineMapId = timelineMap.Id;
        TimelineMapDigest = timelineMapDigest;
        ProfileId = profile.Id;
        ProfileDigest = profile.ProfileDigest;
        TemplateId = profile.TemplateId;
        TemplateVersionDigest = profile.TemplateVersionDigest;
        ConfigurationDigest = configurationDigest;
        ApplicationVersion = ContractText.RequireNonBlank(
            applicationVersion,
            nameof(applicationVersion),
            "An application version");
        ApplicationDigest = applicationDigest;
        Inputs = ManifestCollection.CopyDistinct(inputs, nameof(inputs), requireAtLeastOne: true);
        Tools = ManifestCollection.CopyDistinct(tools, nameof(tools), requireAtLeastOne: true);
        Models = ManifestCollection.CopyDistinct(models, nameof(models), requireAtLeastOne: false);
        Fonts = ManifestCollection.CopyDistinct(fonts, nameof(fonts), requireAtLeastOne: false);
        Outputs = ManifestCollection.CopyDistinct(outputs, nameof(outputs), requireAtLeastOne: true);
    }

    public EntityId<RenderManifestIdTag> Id { get; }

    public EntityId<ProjectIdTag> ProjectId { get; }

    public EntityId<CompositionVersionIdTag> CompositionVersionId { get; }

    public ContentDigest CompositionVersionDigest { get; }

    public EntityId<TimelineMapIdTag> TimelineMapId { get; }

    public ContentDigest TimelineMapDigest { get; }

    public EntityId<RenderProfileIdTag> ProfileId { get; }

    public ContentDigest ProfileDigest { get; }

    public EntityId<TemplateIdTag> TemplateId { get; }

    public ContentDigest TemplateVersionDigest { get; }

    public ContentDigest ConfigurationDigest { get; }

    public string ApplicationVersion { get; }

    public ContentDigest ApplicationDigest { get; }

    public IReadOnlyList<RenderInputEvidence> Inputs { get; }

    public IReadOnlyList<RenderToolEvidence> Tools { get; }

    public IReadOnlyList<RenderModelEvidence> Models { get; }

    public IReadOnlyList<RenderFontEvidence> Fonts { get; }

    public IReadOnlyList<ManifestArtifact> Outputs { get; }
}

internal static class ManifestCollection
{
    public static IReadOnlyList<T> CopyDistinct<T>(
        IReadOnlyList<T> values,
        string parameterName,
        bool requireAtLeastOne)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        if (requireAtLeastOne && values.Count == 0)
        {
            throw new ArgumentException(
                "The collection must contain at least one item.",
                parameterName);
        }

        T[] copy = new T[values.Count];
        HashSet<T> distinct = [];
        for (int index = 0; index < copy.Length; index++)
        {
            T? value = values[index];
            if (value is null)
            {
                throw new ArgumentException(
                    "The collection must not contain null items.",
                    parameterName);
            }

            if (!distinct.Add(value))
            {
                throw new ArgumentException(
                    "The collection must not contain duplicate items.",
                    parameterName);
            }

            copy[index] = value;
        }

        return Array.AsReadOnly(copy);
    }
}