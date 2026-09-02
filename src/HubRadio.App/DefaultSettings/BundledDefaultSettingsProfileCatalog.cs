using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Settings;

namespace HubRadio.App.DefaultSettings;

internal sealed class BundledDefaultSettingsProfileCatalog
{
    private const string CurrentProfileFileName = "1.0.0.json";

    private BundledDefaultSettingsProfileCatalog(DefaultSettingsProfile current)
    {
        Current = current;
        Profiles = Array.AsReadOnly([current]);
    }

    internal DefaultSettingsProfile Current { get; }

    internal IReadOnlyList<DefaultSettingsProfile> Profiles { get; }

    internal static BundledDefaultSettingsProfileCatalog Load(string profilesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesDirectory);
        string root = Path.GetFullPath(profilesDirectory);
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                "The bundled default-settings profile catalog is unavailable.");
        }

        string[] relativeJsonPaths = Directory
            .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (!relativeJsonPaths.SequenceEqual(
                new[] { CurrentProfileFileName },
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The bundled default-settings catalog must contain exactly the approved 1.0.0 profile.");
        }

        string profilePath = Path.Combine(root, CurrentProfileFileName);
        DefaultSettingsProfile profile = BundledDefaultSettingsProfileLoader.Load(
            File.ReadAllBytes(profilePath));
        return new BundledDefaultSettingsProfileCatalog(profile);
    }

    internal DefaultSettingsProfile Resolve(
        EntityId<DefaultSettingsProfileIdTag> profileId,
        SettingsProfileVersion profileVersion,
        ContentDigest profileDigest)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(profileVersion);
        ArgumentNullException.ThrowIfNull(profileDigest);

        return profileId == Current.Id &&
            profileVersion == Current.Version &&
            profileDigest == Current.ContentDigest
            ? Current
            : throw new InvalidOperationException(
                "The requested default-settings profile is not in the bundled catalog.");
    }
}
