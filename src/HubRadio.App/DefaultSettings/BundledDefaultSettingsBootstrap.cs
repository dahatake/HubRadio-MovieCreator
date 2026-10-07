using System.Text;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.DefaultSettings;

internal enum DefaultSettingsBootstrapState
{
    Initialized,
    Existing,
}

internal sealed class DefaultSettingsBootstrapResult
{
    internal DefaultSettingsBootstrapResult(
        DefaultSettingsBootstrapState state,
        DefaultSettingsProfile profile,
        StoredGlobalSettings globalSettings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(globalSettings);

        State = state;
        Profile = profile;
        GlobalSettings = globalSettings;
    }

    internal DefaultSettingsBootstrapState State { get; }

    internal DefaultSettingsProfile Profile { get; }

    internal StoredGlobalSettings GlobalSettings { get; }
}

internal sealed class BundledDefaultSettingsBootstrap
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly DefaultSettingsStore store;

    internal BundledDefaultSettingsBootstrap(DefaultSettingsStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal DefaultSettingsBootstrapResult Initialize(
        string trustedProfilesDirectory,
        DateTimeOffset initializedAt)
    {
        BundledDefaultSettingsProfileCatalog catalog =
            BundledDefaultSettingsProfileCatalog.Load(trustedProfilesDirectory);
        DefaultSettingsProfile bundledProfile = catalog.Current;
        StoredGlobalSettings? existingGlobal = store.GetGlobalSettings();
        DefaultSettingsProfile? existingProfile = null;
        if (existingGlobal is not null)
        {
            existingProfile = catalog.Resolve(
                existingGlobal.Snapshot.ProfileId,
                existingGlobal.Snapshot.ProfileVersion,
                existingGlobal.Snapshot.ProfileDigest);
        }

        byte[] canonicalProfileUtf8 = StrictUtf8.GetBytes(
            bundledProfile.CanonicalJson);
        DefaultSettingsProfile storedProfile = store.RegisterProfile(
            canonicalProfileUtf8);

        if (existingGlobal is not null)
        {
            return new DefaultSettingsBootstrapResult(
                DefaultSettingsBootstrapState.Existing,
                storedProfile,
                MigrateGlobalSettings(existingGlobal, existingProfile!, storedProfile, initializedAt));
        }

        StoredGlobalSettings initializedGlobal = store.InitializeGlobalSettings(
            storedProfile.CreateSnapshot(),
            initializedAt);
        return new DefaultSettingsBootstrapResult(
            DefaultSettingsBootstrapState.Initialized,
            storedProfile,
            initializedGlobal);
    }

    // New projects must be offered the current profile's choices. The global defaults move to the
    // current version only when every saved value stays valid there; project snapshots never move.
    private StoredGlobalSettings MigrateGlobalSettings(
        StoredGlobalSettings existing,
        DefaultSettingsProfile existingProfile,
        DefaultSettingsProfile currentProfile,
        DateTimeOffset migratedAt)
    {
        if (existingProfile.Version == currentProfile.Version)
        {
            return existing;
        }

        DefaultSettingsSnapshot carried;
        try
        {
            // Keys the current profile adds (for example the 1.2.0 keys) start at their defaults.
            var saved = existing.Snapshot.Values.ToDictionary(
                static item => item.Key, static item => item.Value, StringComparer.Ordinal);
            carried = currentProfile.CreateSnapshot(currentProfile.Definitions
                .Select(definition => new DefaultSettingSelection(
                    definition.Key,
                    saved.TryGetValue(definition.Key, out DefaultSettingValue? value) ? value : definition.DefaultValue))
                .ToArray());
            if (existing.Snapshot.Values.Any(item =>
                    !currentProfile.Definitions.Any(definition => definition.Key == item.Key)))
            {
                return existing;
            }
        }
        catch (ArgumentException)
        {
            return existing;
        }

        DateTimeOffset at = migratedAt < existing.UpdatedAt ? existing.UpdatedAt : migratedAt;
        return store.UpdateGlobalSettings(carried, existing.Revision, at);
    }
}
