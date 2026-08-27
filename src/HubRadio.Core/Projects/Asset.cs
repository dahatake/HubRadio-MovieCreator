using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Projects;

public sealed class Asset
{
    public Asset(
        EntityId<AssetIdTag> id,
        string fileName,
        string mediaType,
        ContentDigest contentDigest,
        long byteSize,
        RightsRecord rights)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(rights);

        ValidateRequiredText(fileName, nameof(fileName), "A file name");
        ValidateRequiredText(mediaType, nameof(mediaType), "A media type");

        if (byteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteSize),
                byteSize,
                "An asset byte size must be positive.");
        }

        Id = id;
        FileName = fileName;
        MediaType = mediaType;
        ContentDigest = contentDigest;
        ByteSize = byteSize;
        Rights = rights;
    }

    public EntityId<AssetIdTag> Id { get; }

    public string FileName { get; }

    public string MediaType { get; }

    public ContentDigest ContentDigest { get; }

    public long ByteSize { get; }

    public RightsRecord Rights { get; }

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