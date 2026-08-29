using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Files;

namespace HubRadio.Infrastructure.Backup;

internal sealed class BackupManifest
{
    internal const int CurrentVersion = 1;
    internal const string CompletionValue = "complete";
    internal const string DatabaseRelativePath = "payload/hubradio.db";
    internal const string FileName = "manifest.json";
    internal const int MaximumUtf8Bytes = 64 * 1024 * 1024;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly ArtifactKind[] RequiredIncludedKinds =
    [
        ArtifactKind.Original,
        ArtifactKind.Analysis,
        ArtifactKind.Composition,
        ArtifactKind.Render,
        ArtifactKind.Delivery,
        ArtifactKind.Evidence,
    ];

    private static readonly ArtifactKind[] RequiredExcludedKinds =
    [
        ArtifactKind.Proxy,
        ArtifactKind.Preview,
    ];

    private BackupManifest(
        int version,
        EntityId<BackupIdTag> backupId,
        DateTimeOffset createdAt,
        BackupManifestDatabase database,
        IReadOnlyList<BackupManifestArtifact> artifacts,
        IReadOnlyList<ArtifactKind> includedKinds,
        IReadOnlyList<ArtifactKind> excludedKinds,
        ContentDigest manifestDigest,
        string completionMarker)
    {
        ArgumentNullException.ThrowIfNull(backupId);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(includedKinds);
        ArgumentNullException.ThrowIfNull(excludedKinds);
        ArgumentNullException.ThrowIfNull(manifestDigest);
        ArgumentNullException.ThrowIfNull(completionMarker);

        Version = version;
        BackupId = backupId;
        CreatedAt = createdAt.ToUniversalTime();
        Database = database;
        Artifacts = Array.AsReadOnly(artifacts.ToArray());
        IncludedKinds = Array.AsReadOnly(includedKinds.ToArray());
        ExcludedKinds = Array.AsReadOnly(excludedKinds.ToArray());
        ManifestDigest = manifestDigest;
        CompletionMarker = completionMarker;
        ValidateInvariants();
    }

    internal int Version { get; }

    internal EntityId<BackupIdTag> BackupId { get; }

    internal DateTimeOffset CreatedAt { get; }

    internal BackupManifestDatabase Database { get; }

    internal IReadOnlyList<BackupManifestArtifact> Artifacts { get; }

    internal IReadOnlyList<ArtifactKind> IncludedKinds { get; }

    internal IReadOnlyList<ArtifactKind> ExcludedKinds { get; }

    internal ContentDigest ManifestDigest { get; }

    internal string CompletionMarker { get; }

    internal int PayloadFileCount => checked(Artifacts.Count + 1);

    internal static bool IsIncludedKind(ArtifactKind kind) =>
        Array.IndexOf(RequiredIncludedKinds, kind) >= 0;

    internal static BackupManifest Create(
        EntityId<BackupIdTag> backupId,
        DateTimeOffset createdAt,
        BackupManifestDatabase database,
        IEnumerable<BackupManifestArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        BackupManifestArtifact[] orderedArtifacts = artifacts
            .OrderBy(static artifact => artifact.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        ContentDigest placeholder = ContentDigest.Compute([]);
        var unsigned = new BackupManifest(
            CurrentVersion,
            backupId,
            createdAt,
            database,
            orderedArtifacts,
            RequiredIncludedKinds,
            RequiredExcludedKinds,
            placeholder,
            CompletionValue);
        ContentDigest digest = unsigned.ComputeUnsignedDigest();
        return new BackupManifest(
            unsigned.Version,
            unsigned.BackupId,
            unsigned.CreatedAt,
            unsigned.Database,
            unsigned.Artifacts,
            unsigned.IncludedKinds,
            unsigned.ExcludedKinds,
            digest,
            unsigned.CompletionMarker);
    }

    internal static BackupManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumUtf8Bytes)
        {
            throw InvalidManifest();
        }

        try
        {
            byte[] source = utf8Json.ToArray();
            byte[] canonical = CanonicalJson.CanonicalizeToUtf8(source);
            if (!source.AsSpan().SequenceEqual(canonical))
            {
                throw InvalidManifest();
            }

            using JsonDocument document = JsonDocument.Parse(
                canonical,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            JsonElement root = document.RootElement;
            RequireExactProperties(
                root,
                "version",
                "backupId",
                "createdAt",
                "database",
                "artifacts",
                "includedKinds",
                "excludedKinds",
                "manifestDigest",
                "completionMarker");

            int version = checked((int)ReadExactInteger(root, "version", 1, 1));
            EntityId<BackupIdTag> backupId = ReadEntityId<BackupIdTag>(root, "backupId");
            DateTimeOffset createdAt = ReadTimestamp(root, "createdAt");
            BackupManifestDatabase database = ReadDatabase(root.GetProperty("database"));
            BackupManifestArtifact[] artifacts = ReadArtifacts(root.GetProperty("artifacts"));
            ArtifactKind[] includedKinds = ReadKinds(root.GetProperty("includedKinds"));
            ArtifactKind[] excludedKinds = ReadKinds(root.GetProperty("excludedKinds"));
            ContentDigest manifestDigest = ReadDigest(root, "manifestDigest");
            string completionMarker = ReadString(root, "completionMarker");

            var manifest = new BackupManifest(
                version,
                backupId,
                createdAt,
                database,
                artifacts,
                includedKinds,
                excludedKinds,
                manifestDigest,
                completionMarker);
            ContentDigest computedDigest = manifest.ComputeUnsignedDigest();
            if (!FixedTimeEquals(computedDigest, manifest.ManifestDigest) ||
                !manifest.Serialize().AsSpan().SequenceEqual(source))
            {
                throw InvalidManifest();
            }

            return manifest;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or
                ArgumentException or FormatException or OverflowException)
        {
            throw InvalidManifest();
        }
    }

    internal byte[] Serialize() => WriteCanonical(includeManifestDigest: true);

    internal ContentDigest ComputeUnsignedDigest() =>
        ContentDigest.Compute(WriteCanonical(includeManifestDigest: false));

    private byte[] WriteCanonical(bool includeManifestDigest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("backupId", BackupId.ToString());
            writer.WriteString("createdAt", FormatTimestamp(CreatedAt));
            writer.WritePropertyName("database");
            WriteDatabase(writer, Database);
            writer.WritePropertyName("artifacts");
            writer.WriteStartArray();
            foreach (BackupManifestArtifact artifact in Artifacts)
            {
                WriteArtifact(writer, artifact);
            }

            writer.WriteEndArray();
            WriteKinds(writer, "includedKinds", IncludedKinds);
            WriteKinds(writer, "excludedKinds", ExcludedKinds);
            if (includeManifestDigest)
            {
                writer.WriteString("manifestDigest", ManifestDigest.Hex);
            }

            writer.WriteString("completionMarker", CompletionMarker);
            writer.WriteEndObject();
            writer.Flush();
        }

        return CanonicalJson.CanonicalizeToUtf8(buffer.WrittenSpan);
    }

    private void ValidateInvariants()
    {
        ArtifactKind[] declaredKinds =
        [
            .. RequiredIncludedKinds,
            .. RequiredExcludedKinds,
        ];
        if (Version != CurrentVersion ||
            !string.Equals(CompletionMarker, CompletionValue, StringComparison.Ordinal) ||
            !IncludedKinds.SequenceEqual(RequiredIncludedKinds) ||
            !ExcludedKinds.SequenceEqual(RequiredExcludedKinds) ||
            declaredKinds.Length != Enum.GetValues<ArtifactKind>().Length ||
            !declaredKinds.ToHashSet().SetEquals(Enum.GetValues<ArtifactKind>()) ||
            !string.Equals(
                Database.RelativePath,
                DatabaseRelativePath,
                StringComparison.Ordinal) ||
            Database.ByteSize <= 0 ||
            !string.Equals(
                FormatTimestamp(CreatedAt),
                FormatTimestamp(CreatedAt.ToUniversalTime()),
                StringComparison.Ordinal))
        {
            throw InvalidManifest();
        }

        var ids = new HashSet<EntityId<ArtifactIdTag>>();
        var sourceReferences = new HashSet<string>(StringComparer.Ordinal);
        var backupReferences = new HashSet<string>(StringComparer.Ordinal);
        string? previousId = null;
        foreach (BackupManifestArtifact artifact in Artifacts)
        {
            string id = artifact.Id.ToString();
            if ((previousId is not null &&
                    StringComparer.Ordinal.Compare(previousId, id) >= 0) ||
                !ids.Add(artifact.Id) ||
                !sourceReferences.Add(artifact.SourceStorageReference) ||
                !backupReferences.Add(artifact.BackupRelativePath) ||
                !IsIncludedKind(artifact.Kind) ||
                artifact.ByteSize <= 0)
            {
                throw InvalidManifest();
            }

            string expectedPrefix = string.Concat(
                "projects/",
                artifact.ProjectId.ToString(),
                "/",
                ArtifactStore.GetKindFolder(artifact.Kind),
                "/");
            if (!artifact.SourceStorageReference.StartsWith(
                    expectedPrefix,
                    StringComparison.Ordinal) ||
                !WorkspacePathSafety.IsValidPortableRelativePath(
                    artifact.SourceStorageReference) ||
                !string.Equals(
                    artifact.BackupRelativePath,
                    string.Concat("payload/", artifact.SourceStorageReference),
                    StringComparison.Ordinal))
            {
                throw InvalidManifest();
            }

            _ = FormatTimestamp(artifact.CreatedAt);
            previousId = id;
        }
    }

    private static void WriteDatabase(
        Utf8JsonWriter writer,
        BackupManifestDatabase database)
    {
        writer.WriteStartObject();
        writer.WriteString("relativePath", database.RelativePath);
        writer.WriteNumber("byteSize", database.ByteSize);
        writer.WriteString("sha256", database.Sha256.Hex);
        writer.WriteEndObject();
    }

    private static void WriteArtifact(
        Utf8JsonWriter writer,
        BackupManifestArtifact artifact)
    {
        writer.WriteStartObject();
        writer.WriteString("id", artifact.Id.ToString());
        writer.WriteString("projectId", artifact.ProjectId.ToString());
        writer.WriteString("kind", artifact.Kind.ToString());
        writer.WriteString("sourceStorageReference", artifact.SourceStorageReference);
        writer.WriteString("backupRelativePath", artifact.BackupRelativePath);
        writer.WriteNumber("byteSize", artifact.ByteSize);
        writer.WriteString("sha256", artifact.Sha256.Hex);
        writer.WriteString("createdAt", FormatTimestamp(artifact.CreatedAt));
        writer.WriteEndObject();
    }

    private static void WriteKinds(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<ArtifactKind> kinds)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (ArtifactKind kind in kinds)
        {
            writer.WriteStringValue(kind.ToString());
        }

        writer.WriteEndArray();
    }

    private static BackupManifestDatabase ReadDatabase(JsonElement value)
    {
        RequireExactProperties(value, "relativePath", "byteSize", "sha256");
        return new BackupManifestDatabase(
            ReadString(value, "relativePath"),
            ReadExactInteger(value, "byteSize", 1, long.MaxValue),
            ReadDigest(value, "sha256"));
    }

    private static BackupManifestArtifact[] ReadArtifacts(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest();
        }

        var artifacts = new List<BackupManifestArtifact>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            RequireExactProperties(
                item,
                "id",
                "projectId",
                "kind",
                "sourceStorageReference",
                "backupRelativePath",
                "byteSize",
                "sha256",
                "createdAt");
            artifacts.Add(new BackupManifestArtifact(
                ReadEntityId<ArtifactIdTag>(item, "id"),
                ReadEntityId<ProjectIdTag>(item, "projectId"),
                ReadKind(item, "kind"),
                ReadString(item, "sourceStorageReference"),
                ReadString(item, "backupRelativePath"),
                ReadExactInteger(item, "byteSize", 1, long.MaxValue),
                ReadDigest(item, "sha256"),
                ReadTimestamp(item, "createdAt")));
        }

        return artifacts.ToArray();
    }

    private static ArtifactKind[] ReadKinds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest();
        }

        return value.EnumerateArray()
            .Select(static item => ParseKind(ReadStringValue(item)))
            .ToArray();
    }

    private static TId ReadEntityId<TTag, TId>(
        JsonElement owner,
        string propertyName,
        Func<string, TId> parser,
        Func<TId, string> formatter)
    {
        string value = ReadString(owner, propertyName);
        TId parsed = parser(value);
        if (!string.Equals(value, formatter(parsed), StringComparison.Ordinal))
        {
            throw InvalidManifest();
        }

        return parsed;
    }

    private static EntityId<TTag> ReadEntityId<TTag>(
        JsonElement owner,
        string propertyName) =>
        ReadEntityId<TTag, EntityId<TTag>>(
            owner,
            propertyName,
            EntityId<TTag>.Parse,
            static id => id.ToString());

    private static ContentDigest ReadDigest(
        JsonElement owner,
        string propertyName)
    {
        string value = ReadString(owner, propertyName);
        ContentDigest digest = ContentDigest.Parse(value);
        if (!string.Equals(value, digest.Hex, StringComparison.Ordinal))
        {
            throw InvalidManifest();
        }

        return digest;
    }

    private static ArtifactKind ReadKind(
        JsonElement owner,
        string propertyName) =>
        ParseKind(ReadString(owner, propertyName));

    private static ArtifactKind ParseKind(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: false, out ArtifactKind kind) ||
            !Enum.IsDefined(kind) ||
            !string.Equals(value, kind.ToString(), StringComparison.Ordinal))
        {
            throw InvalidManifest();
        }

        return kind;
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement owner,
        string propertyName)
    {
        string value = ReadString(owner, propertyName);
        if (!DateTimeOffset.TryParseExact(
                value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed) ||
            !string.Equals(value, FormatTimestamp(parsed), StringComparison.Ordinal))
        {
            throw InvalidManifest();
        }

        return parsed;
    }

    private static string ReadString(
        JsonElement owner,
        string propertyName) =>
        ReadStringValue(owner.GetProperty(propertyName));

    private static string ReadStringValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw InvalidManifest();
        }

        return text;
    }

    private static long ReadExactInteger(
        JsonElement owner,
        string propertyName,
        long minimum,
        long maximum)
    {
        JsonElement value = owner.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out decimal number) ||
            decimal.Truncate(number) != number ||
            number < minimum ||
            number > maximum)
        {
            throw InvalidManifest();
        }

        return decimal.ToInt64(number);
    }

    private static void RequireExactProperties(
        JsonElement value,
        params string[] expectedNames)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest();
        }

        string[] actualNames = value.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        if (actualNames.Length != expectedNames.Length ||
            !actualNames.ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedNames))
        {
            throw InvalidManifest();
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        try
        {
            return value
                .ToUniversalTime()
                .UtcDateTime
                .ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidManifest();
        }
    }

    private static bool FixedTimeEquals(
        ContentDigest left,
        ContentDigest right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left.Hex),
            Convert.FromHexString(right.Hex));

    private static InvalidDataException InvalidManifest() =>
        new("The backup manifest is invalid.");
}

internal sealed class BackupManifestDatabase
{
    internal BackupManifestDatabase(
        string relativePath,
        long byteSize,
        ContentDigest sha256)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(sha256);
        RelativePath = relativePath;
        ByteSize = byteSize;
        Sha256 = sha256;
    }

    internal string RelativePath { get; }

    internal long ByteSize { get; }

    internal ContentDigest Sha256 { get; }
}

internal sealed class BackupManifestArtifact
{
    internal BackupManifestArtifact(
        EntityId<ArtifactIdTag> id,
        EntityId<ProjectIdTag> projectId,
        ArtifactKind kind,
        string sourceStorageReference,
        string backupRelativePath,
        long byteSize,
        ContentDigest sha256,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(sourceStorageReference);
        ArgumentNullException.ThrowIfNull(backupRelativePath);
        ArgumentNullException.ThrowIfNull(sha256);
        Id = id;
        ProjectId = projectId;
        Kind = kind;
        SourceStorageReference = sourceStorageReference;
        BackupRelativePath = backupRelativePath;
        ByteSize = byteSize;
        Sha256 = sha256;
        CreatedAt = createdAt.ToUniversalTime();
    }

    internal EntityId<ArtifactIdTag> Id { get; }

    internal EntityId<ProjectIdTag> ProjectId { get; }

    internal ArtifactKind Kind { get; }

    internal string SourceStorageReference { get; }

    internal string BackupRelativePath { get; }

    internal long ByteSize { get; }

    internal ContentDigest Sha256 { get; }

    internal DateTimeOffset CreatedAt { get; }
}