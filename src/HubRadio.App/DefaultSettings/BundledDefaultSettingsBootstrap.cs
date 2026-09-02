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
        if (existingGlobal is not null)
        {
            _ = catalog.Resolve(
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
                existingGlobal);
        }

        StoredGlobalSettings initializedGlobal = store.InitializeGlobalSettings(
            storedProfile.CreateSnapshot(),
            initializedAt);
        return new DefaultSettingsBootstrapResult(
            DefaultSettingsBootstrapState.Initialized,
            storedProfile,
            initializedGlobal);
    }
}
