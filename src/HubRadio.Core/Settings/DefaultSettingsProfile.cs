using HubRadio.Core.Identity;
using HubRadio.Core.Layout;

namespace HubRadio.Core.Settings;

public sealed class DefaultSettingsProfileIdTag
{
}

public sealed class DefaultSettingsProfile
{
    private readonly IReadOnlyDictionary<string, DefaultSettingDefinition> _definitionsByKey;

    private DefaultSettingsProfile(
        EntityId<DefaultSettingsProfileIdTag> id,
        string name,
        SettingsProfileVersion version,
        ContentDigest contentDigest,
        DefaultSettingDefinition[] definitions)
    {
        Id = id;
        Name = name;
        Version = version;
        ContentDigest = contentDigest;
        Definitions = Array.AsReadOnly(definitions);
        _definitionsByKey = definitions.ToDictionary(
            static definition => definition.Key,
            StringComparer.Ordinal);
    }

    public EntityId<DefaultSettingsProfileIdTag> Id { get; }

    public string Name { get; }

    public SettingsProfileVersion Version { get; }

    public ContentDigest ContentDigest { get; }

    public IReadOnlyList<DefaultSettingDefinition> Definitions { get; }

    public string CanonicalJson => DefaultSettingsJsonCodec.SerializeProfile(this);

    public static DefaultSettingsProfile Create(
        EntityId<DefaultSettingsProfileIdTag> id,
        string name,
        SettingsProfileVersion version,
        IReadOnlyList<DefaultSettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(version);
        string validatedName = ContractText.RequireNonBlankStrictUtf8Substantive(
            name,
            nameof(name),
            "A default-settings profile name");
        DefaultSettingDefinition[] copiedDefinitions = CopyDefinitions(definitions);
        ContentDigest digest = ContentDigest.Compute(
            DefaultSettingsJsonCodec.SerializeProfileDigestPayload(
                validatedName,
                version,
                copiedDefinitions));

        return new DefaultSettingsProfile(
            id,
            validatedName,
            version,
            digest,
            copiedDefinitions);
    }

    public static DefaultSettingsProfile ParseCanonical(string canonicalJson) =>
        DefaultSettingsJsonCodec.ParseProfileCanonical(canonicalJson);

    public DefaultSettingDefinition GetDefinition(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_definitionsByKey.TryGetValue(key, out DefaultSettingDefinition? definition))
        {
            throw new KeyNotFoundException("The settings key is not defined by this profile.");
        }

        return definition;
    }

    public DefaultSettingsSnapshot CreateSnapshot() =>
        DefaultSettingsSnapshot.Create(
            this,
            Definitions.Select(
                static definition => new DefaultSettingSelection(
                    definition.Key,
                    definition.DefaultValue)).ToArray());

    public DefaultSettingsSnapshot CreateSnapshot(
        IReadOnlyList<DefaultSettingSelection> resolvedValues) =>
        DefaultSettingsSnapshot.Create(this, resolvedValues);

    private static DefaultSettingDefinition[] CopyDefinitions(
        IReadOnlyList<DefaultSettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0)
        {
            throw new ArgumentException(
                "A default-settings profile must contain at least one setting definition.",
                nameof(definitions));
        }

        DefaultSettingDefinition[] copied = new DefaultSettingDefinition[definitions.Count];
        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int index = 0; index < definitions.Count; index++)
        {
            DefaultSettingDefinition definition = definitions[index] ??
                throw new ArgumentException(
                    "Default-settings definitions must not contain null elements.",
                    nameof(definitions));
            if (!keys.Add(definition.Key))
            {
                throw new ArgumentException(
                    "Default-settings definition keys must be unique.",
                    nameof(definitions));
            }

            copied[index] = definition;
        }

        Array.Sort(
            copied,
            static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return copied;
    }
}

public sealed class DefaultSettingSelection
{
    public DefaultSettingSelection(string key, DefaultSettingValue value)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Key { get; }

    public DefaultSettingValue Value { get; }
}

public sealed class DefaultSettingsSnapshot
{
    private DefaultSettingsSnapshot(
        EntityId<DefaultSettingsProfileIdTag> profileId,
        SettingsProfileVersion profileVersion,
        ContentDigest profileDigest,
        ContentDigest contentDigest,
        DefaultSettingSelection[] values)
    {
        ProfileId = profileId;
        ProfileVersion = profileVersion;
        ProfileDigest = profileDigest;
        ContentDigest = contentDigest;
        Values = Array.AsReadOnly(values);
    }

    public EntityId<DefaultSettingsProfileIdTag> ProfileId { get; }

    public SettingsProfileVersion ProfileVersion { get; }

    public ContentDigest ProfileDigest { get; }

    public ContentDigest ContentDigest { get; }

    public IReadOnlyList<DefaultSettingSelection> Values { get; }

    public string CanonicalJson => DefaultSettingsJsonCodec.SerializeSnapshot(this);

    public static DefaultSettingsSnapshot ParseCanonical(
        string canonicalJson,
        DefaultSettingsProfile profile) =>
        DefaultSettingsJsonCodec.ParseSnapshotCanonical(canonicalJson, profile);

    internal static DefaultSettingsSnapshot Create(
        DefaultSettingsProfile profile,
        IReadOnlyList<DefaultSettingSelection> resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(resolvedValues);

        if (resolvedValues.Count != profile.Definitions.Count)
        {
            throw new ArgumentException(
                "A settings snapshot must contain exactly one value for every profile definition.",
                nameof(resolvedValues));
        }

        DefaultSettingSelection[] copiedValues = new DefaultSettingSelection[resolvedValues.Count];
        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int index = 0; index < resolvedValues.Count; index++)
        {
            DefaultSettingSelection selection = resolvedValues[index] ??
                throw new ArgumentException(
                    "Settings snapshot values must not contain null elements.",
                    nameof(resolvedValues));
            if (!keys.Add(selection.Key))
            {
                throw new ArgumentException(
                    "Settings snapshot keys must be unique.",
                    nameof(resolvedValues));
            }

            DefaultSettingDefinition definition;
            try
            {
                definition = profile.GetDefinition(selection.Key);
            }
            catch (KeyNotFoundException exception)
            {
                throw new ArgumentException(
                    "A settings snapshot key is not defined by the profile.",
                    nameof(resolvedValues),
                    exception);
            }

            definition.ValidateValue(selection.Value, nameof(resolvedValues));
            copiedValues[index] = new DefaultSettingSelection(
                selection.Key,
                selection.Value);
        }

        Array.Sort(
            copiedValues,
            static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        ContentDigest contentDigest = ContentDigest.Compute(
            DefaultSettingsJsonCodec.SerializeSnapshotDigestPayload(
                profile.Id,
                profile.Version,
                profile.ContentDigest,
                copiedValues));

        return new DefaultSettingsSnapshot(
            profile.Id,
            profile.Version,
            profile.ContentDigest,
            contentDigest,
            copiedValues);
    }
}
