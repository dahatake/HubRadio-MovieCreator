using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Settings;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Projects;

internal enum SourceOnlyProjectCreationFailureReason
{
    SourceSelectionCanceled,
    UnsafeSourceName,
    SettingsUnavailable,
    SettingsMismatch,
    CatalogUnavailable,
}

internal sealed class SourceOnlyProjectCreationException : InvalidOperationException
{
    internal SourceOnlyProjectCreationException(
        SourceOnlyProjectCreationFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    internal SourceOnlyProjectCreationFailureReason Reason { get; }

    private static string GetStableMessage(
        SourceOnlyProjectCreationFailureReason reason) =>
        reason switch
        {
            SourceOnlyProjectCreationFailureReason.SourceSelectionCanceled =>
                "Source selection was canceled.",
            SourceOnlyProjectCreationFailureReason.UnsafeSourceName =>
                "The source file name cannot be used as a project name.",
            SourceOnlyProjectCreationFailureReason.SettingsUnavailable =>
                "The default project settings are unavailable.",
            SourceOnlyProjectCreationFailureReason.SettingsMismatch =>
                "The default project settings are not compatible with source-only creation.",
            SourceOnlyProjectCreationFailureReason.CatalogUnavailable =>
                "The required project profiles are unavailable.",
            _ => "The source-only project could not be created.",
        };
}

internal sealed record SourceOnlyProjectCreationResolution(
    ProjectCreationDraft Draft,
    DefaultSettingsSnapshot SettingsSnapshot,
    IReadOnlyList<string> ArtifactSet);

internal sealed record StoredSourceOnlyProjectCreation(
    ProjectCreationDraft Draft,
    DefaultSettingsSnapshot SettingsSnapshot,
    IReadOnlyList<string> ArtifactSet);

internal static class SourceOnlyProjectCreationContract
{
    private const string Purpose = "社内向け再利用動画の自動編集";
    private const string Audience = "社内視聴者";
    private const string LanguageFallback = "ja-JP";
    private const string AnonymousSpeaker = "話者1";
    private const string LongFormProfileName = "long-form-1280x720";
    private const string VerticalProfileName = "vertical-short-1080x1920";
    private const string NeutralTemplateName = "development-neutral-long-form";
    private static readonly string[] SourceOnlyMetadataKeys =
    [
        "artifactSet",
        "profileDigest",
        "profileId",
        "profileVersion",
        "settingsSnapshot",
        "settingsSnapshotDigest",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static SourceOnlyProjectCreationResolution Resolve(
        EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> sourceCandidateId,
        string selectedPath,
        StoredGlobalSettings? globalSettings,
        ProjectCreationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        ArgumentNullException.ThrowIfNull(sourceCandidateId);
        ArgumentNullException.ThrowIfNull(selectedPath);
        ArgumentNullException.ThrowIfNull(catalog);

        if (globalSettings is null)
        {
            throw new SourceOnlyProjectCreationException(
                SourceOnlyProjectCreationFailureReason.SettingsUnavailable);
        }

        return Resolve(draftId, sourceCandidateId, selectedPath, globalSettings.Snapshot, catalog);
    }

    /// <summary>FR-054: the same contract for a snapshot already tuned to the source video.</summary>
    internal static SourceOnlyProjectCreationResolution Resolve(
        EntityId<ProjectCreationDraftIdTag> draftId,
        EntityId<AssetIdTag> sourceCandidateId,
        string selectedPath,
        DefaultSettingsSnapshot snapshot,
        ProjectCreationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);
        RequireText(snapshot, "project.anonymousSpeakerPolicy", "anonymousSpeakerLabels");
        RequireText(snapshot, "project.languagePolicy", "autoDetectWithJaJpFallback");
        RequireText(snapshot, "project.publicationDeadlinePolicy", "none");
        RequireText(snapshot, "project.publicationScope", "internalOnly");
        RequireText(snapshot, "project.template", "builtInNeutralNoLogo");
        IReadOnlyList<string> artifactSet = RequireTextList(
            snapshot,
            "project.quickStartArtifactSet");

        ProjectOutputProfileReference[] profiles =
        [
            ResolveProfile(catalog, LongFormProfileName),
            ResolveProfile(catalog, VerticalProfileName),
        ];
        TemplateReference template = ResolveTemplate(catalog, NeutralTemplateName);
        var draft = new ProjectCreationDraft(
            draftId,
            sourceCandidateId,
            DeriveContentName(selectedPath),
            Purpose,
            Audience,
            PublicationScope.InternalOnly,
            profiles,
            LanguageFallback,
            [AnonymousSpeaker],
            new ProjectDeadlineSelection(deadline: null),
            template);
        return new SourceOnlyProjectCreationResolution(
            draft,
            snapshot,
            Array.AsReadOnly(artifactSet.ToArray()));
    }

    internal static string DeriveContentName(string selectedPath)
    {
        ArgumentNullException.ThrowIfNull(selectedPath);
        string stem;
        try
        {
            stem = Path.GetFileNameWithoutExtension(selectedPath);
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            throw UnsafeName(exception);
        }

        if (string.IsNullOrEmpty(stem) ||
            !string.Equals(stem, stem.Trim(), StringComparison.Ordinal) ||
            stem.EndsWith('.') ||
            stem.Any(char.IsControl) ||
            ReservedWindowsNames.Contains(stem))
        {
            throw UnsafeName();
        }

        try
        {
            if (StrictUtf8.GetByteCount(stem) >
                ProjectCreationDraftStore.MaximumPayloadUtf8Bytes)
            {
                throw UnsafeName();
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw UnsafeName(exception);
        }

        if (!ContainsSubstantiveUnicodeScalar(stem))
        {
            throw UnsafeName();
        }

        return stem;
    }

    internal static bool IsSourceOnlyPayload(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        try
        {
            JsonObject root = JsonNode.Parse(canonicalJson)?.AsObject()
                ?? throw new JsonException();
            return root.ContainsKey("sourceOnly");
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException)
        {
            throw new JsonException(
                "The stored project creation payload is invalid.");
        }
    }

    internal static string Serialize(SourceOnlyProjectCreationResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        JsonObject root = JsonNode.Parse(
            ProjectCreationPayloadCodec.Serialize(resolution.Draft))!.AsObject();
        DefaultSettingsSnapshot snapshot = resolution.SettingsSnapshot;
        var artifactSet = new JsonArray();
        foreach (string artifact in resolution.ArtifactSet)
        {
            artifactSet.Add(artifact);
        }

        root["sourceOnly"] = new JsonObject
        {
            ["artifactSet"] = artifactSet,
            ["profileDigest"] = snapshot.ProfileDigest.Hex,
            ["profileId"] = snapshot.ProfileId.ToString(),
            ["profileVersion"] = snapshot.ProfileVersion.ToString(),
            ["settingsSnapshot"] = snapshot.CanonicalJson,
            ["settingsSnapshotDigest"] = snapshot.ContentDigest.Hex,
        };
        return CanonicalJson.Canonicalize(root.ToJsonString());
    }

    internal static StoredSourceOnlyProjectCreation ParseStored(
        string canonicalJson,
        ProjectCreationCatalog catalog,
        DefaultSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settingsStore);
        if (!string.Equals(
                canonicalJson,
                CanonicalJson.Canonicalize(canonicalJson),
                StringComparison.Ordinal))
        {
            throw new JsonException(
                "The stored source-only project creation payload is not canonical.");
        }

        JsonObject root = JsonNode.Parse(canonicalJson)!.AsObject();
        JsonObject metadata = root["sourceOnly"]?.AsObject()
            ?? throw new JsonException(
                "The stored source-only project creation metadata is missing.");
        string[] actualKeys = metadata.Select(static property => property.Key).ToArray();
        Array.Sort(actualKeys, StringComparer.Ordinal);
        if (!actualKeys.SequenceEqual(SourceOnlyMetadataKeys, StringComparer.Ordinal))
        {
            throw new JsonException(
                "The stored source-only project creation metadata has an invalid shape.");
        }

        EntityId<DefaultSettingsProfileIdTag> profileId =
            ParseEntityId<DefaultSettingsProfileIdTag>(metadata, "profileId");
        string profileVersionText = ReadString(metadata, "profileVersion");
        if (!SettingsProfileVersion.TryParse(
                profileVersionText,
                out SettingsProfileVersion? profileVersion))
        {
            throw new JsonException(
                "The stored source-only settings profile version is invalid.");
        }

        ContentDigest profileDigest = ParseDigest(metadata, "profileDigest");
        DefaultSettingsProfile profile = settingsStore.GetProfile(
            profileId,
            profileVersion)
            ?? throw new SourceOnlyProjectCreationException(
                SourceOnlyProjectCreationFailureReason.SettingsUnavailable);
        if (profile.ContentDigest != profileDigest)
        {
            throw SettingsMismatch();
        }

        string snapshotJson = ReadString(metadata, "settingsSnapshot");
        DefaultSettingsSnapshot snapshot;
        try
        {
            snapshot = DefaultSettingsSnapshot.ParseCanonical(snapshotJson, profile);
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException)
        {
            throw SettingsMismatch();
        }

        if (snapshot.ContentDigest != ParseDigest(metadata, "settingsSnapshotDigest"))
        {
            throw SettingsMismatch();
        }

        string[] artifactSet = ReadStringArray(metadata, "artifactSet");
        IReadOnlyList<string> snapshotArtifactSet = RequireTextList(
            snapshot,
            "project.quickStartArtifactSet");
        if (!artifactSet.SequenceEqual(snapshotArtifactSet, StringComparer.Ordinal))
        {
            throw SettingsMismatch();
        }

        _ = root.Remove("sourceOnly");
        ProjectCreationDraft draft = ProjectCreationPayloadCodec.ParseStored(
            CanonicalJson.Canonicalize(root.ToJsonString()),
            catalog);
        return new StoredSourceOnlyProjectCreation(
            draft,
            snapshot,
            Array.AsReadOnly(artifactSet));
    }

    private static ProjectOutputProfileReference ResolveProfile(
        ProjectCreationCatalog catalog,
        string requiredName)
    {
        ProjectCatalogOption? option = catalog.OutputProfiles.SingleOrDefault(
            candidate => string.Equals(
                candidate.Name,
                requiredName,
                StringComparison.Ordinal));
        return option is null
            ? throw new SourceOnlyProjectCreationException(
                SourceOnlyProjectCreationFailureReason.CatalogUnavailable)
            : catalog.ResolveOutputProfile(option.Id);
    }

    private static TemplateReference ResolveTemplate(
        ProjectCreationCatalog catalog,
        string requiredName)
    {
        ProjectCatalogOption? option = catalog.BrandTemplates.SingleOrDefault(
            candidate => string.Equals(
                candidate.Name,
                requiredName,
                StringComparison.Ordinal));
        return option is null
            ? throw new SourceOnlyProjectCreationException(
                SourceOnlyProjectCreationFailureReason.CatalogUnavailable)
            : catalog.ResolveBrandTemplate(option.Id);
    }

    private static void RequireText(
        DefaultSettingsSnapshot snapshot,
        string key,
        string expected)
    {
        DefaultSettingSelection selection = RequireSelection(snapshot, key);
        if (selection.Value.Kind != DefaultSettingValueKind.Text ||
            !string.Equals(
                selection.Value.TextValue,
                expected,
                StringComparison.Ordinal))
        {
            throw SettingsMismatch();
        }
    }

    private static IReadOnlyList<string> RequireTextList(
        DefaultSettingsSnapshot snapshot,
        string key)
    {
        DefaultSettingSelection selection = RequireSelection(snapshot, key);
        if (selection.Value.Kind != DefaultSettingValueKind.TextList ||
            selection.Value.TextListValue.Count == 0)
        {
            throw SettingsMismatch();
        }

        return selection.Value.TextListValue;
    }

    private static DefaultSettingSelection RequireSelection(
        DefaultSettingsSnapshot snapshot,
        string key)
    {
        DefaultSettingSelection? selection = snapshot.Values.SingleOrDefault(
            value => string.Equals(value.Key, key, StringComparison.Ordinal));
        return selection ?? throw SettingsMismatch();
    }

    private static EntityId<TTag> ParseEntityId<TTag>(
        JsonObject value,
        string propertyName)
    {
        string text = ReadString(value, propertyName);
        if (!EntityId<TTag>.TryParse(text, out EntityId<TTag>? id) ||
            !string.Equals(text, id.ToString(), StringComparison.Ordinal))
        {
            throw new JsonException(
                "The stored source-only project creation identity is invalid.");
        }

        return id;
    }

    private static ContentDigest ParseDigest(
        JsonObject value,
        string propertyName)
    {
        try
        {
            return new ContentDigest(ReadString(value, propertyName));
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "The stored source-only project creation digest is invalid.",
                exception);
        }
    }

    private static string ReadString(JsonObject value, string propertyName)
    {
        JsonNode? node = value[propertyName];
        if (node is null || node.GetValueKind() != JsonValueKind.String)
        {
            throw new JsonException(
                "The stored source-only project creation value is invalid.");
        }

        return node.GetValue<string>();
    }

    private static string[] ReadStringArray(
        JsonObject value,
        string propertyName)
    {
        JsonArray array = value[propertyName]?.AsArray()
            ?? throw new JsonException(
                "The stored source-only project creation list is invalid.");
        var values = new List<string>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is null || item.GetValueKind() != JsonValueKind.String)
            {
                throw new JsonException(
                    "The stored source-only project creation list is invalid.");
            }

            values.Add(item.GetValue<string>());
        }

        return values.ToArray();
    }

    private static bool ContainsSubstantiveUnicodeScalar(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (!Rune.IsWhiteSpace(rune) &&
                !IsNonSubstantiveSpecialScalar(rune) &&
                category is not (UnicodeCategory.Control or
                    UnicodeCategory.Format or
                    UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or
                    UnicodeCategory.EnclosingMark or
                    UnicodeCategory.SpaceSeparator or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonSubstantiveSpecialScalar(Rune rune) =>
        rune.Value switch
        {
            0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0 => true,
            >= 0xFE00 and <= 0xFE0F => true,
            >= 0xE0100 and <= 0xE01EF => true,
            _ => false,
        };

    private static SourceOnlyProjectCreationException UnsafeName(
        Exception? innerException = null)
    {
        _ = innerException;
        return new SourceOnlyProjectCreationException(
            SourceOnlyProjectCreationFailureReason.UnsafeSourceName);
    }

    private static SourceOnlyProjectCreationException SettingsMismatch() =>
        new(SourceOnlyProjectCreationFailureReason.SettingsMismatch);
}