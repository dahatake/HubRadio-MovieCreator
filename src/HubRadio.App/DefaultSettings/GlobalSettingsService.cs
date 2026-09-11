using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.DefaultSettings;

internal sealed class GlobalSettingsService
{
    private readonly DefaultSettingsStore store;
    private readonly Func<DateTimeOffset> getUtcNow;

    internal GlobalSettingsService(
        DefaultSettingsStore store,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal StoredGlobalSettings Read() =>
        store.GetGlobalSettings() ??
        throw new InvalidOperationException("Global default settings are not initialized.");

    internal StoredGlobalSettings Update(
        string key,
        DefaultSettingValue value,
        int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        StoredGlobalSettings current = ReadForMutation(expectedRevision);
        DefaultSettingsProfile profile = RequireProfile(current);
        _ = profile.GetDefinition(key);
        return SaveValue(current, profile, key, value, expectedRevision);
    }

    internal StoredGlobalSettings Reset(string key, int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(key);
        StoredGlobalSettings current = ReadForMutation(expectedRevision);
        DefaultSettingsProfile profile = RequireProfile(current);
        DefaultSettingValue defaultValue = profile.GetDefinition(key).DefaultValue;
        return SaveValue(current, profile, key, defaultValue, expectedRevision);
    }

    internal StoredGlobalSettings ResetAll(int expectedRevision)
    {
        StoredGlobalSettings current = ReadForMutation(expectedRevision);
        DefaultSettingsProfile profile = RequireProfile(current);
        return Save(current, profile.CreateSnapshot(), expectedRevision);
    }

    private StoredGlobalSettings ReadForMutation(int expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                expectedRevision,
                "An expected settings revision must not be negative.");
        }

        StoredGlobalSettings current = Read();
        if (current.Revision != expectedRevision)
        {
            throw new StoreConcurrencyException(
                "global default settings",
                "singleton",
                expectedRevision,
                current.Revision);
        }

        return current;
    }

    private DefaultSettingsProfile RequireProfile(StoredGlobalSettings current)
    {
        // Resolve the persisted binding, not the latest bundled or registered profile.
        DefaultSettingsSnapshot snapshot = current.Snapshot;
        DefaultSettingsProfile profile = store.GetProfile(
            snapshot.ProfileId,
            snapshot.ProfileVersion) ??
            throw new InvalidOperationException("The bound settings profile is unavailable.");
        if (profile.ContentDigest != snapshot.ProfileDigest)
        {
            throw new InvalidOperationException("The bound settings profile does not match the snapshot.");
        }

        return profile;
    }

    private StoredGlobalSettings SaveValue(
        StoredGlobalSettings current,
        DefaultSettingsProfile profile,
        string key,
        DefaultSettingValue value,
        int expectedRevision)
    {
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot(
            current.Snapshot.Values
                .Select(selection => new DefaultSettingSelection(
                    selection.Key,
                    string.Equals(selection.Key, key, StringComparison.Ordinal)
                        ? value
                        : selection.Value))
                .ToArray());
        return Save(current, snapshot, expectedRevision);
    }

    private StoredGlobalSettings Save(
        StoredGlobalSettings current,
        DefaultSettingsSnapshot snapshot,
        int expectedRevision)
    {
        // ReadForMutation checks the caller's revision even when the values already match.
        if (snapshot.ContentDigest == current.Snapshot.ContentDigest)
        {
            return current;
        }

        DateTimeOffset now = getUtcNow();
        return store.UpdateGlobalSettings(
            snapshot,
            expectedRevision,
            now < current.UpdatedAt ? current.UpdatedAt : now);
    }
}