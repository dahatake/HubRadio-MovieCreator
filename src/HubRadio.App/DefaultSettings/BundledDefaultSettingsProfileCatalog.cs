using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Settings;

namespace HubRadio.App.DefaultSettings;

internal sealed class BundledDefaultSettingsProfileCatalog
{
    private static readonly string[] ApprovedProfileFileNames = ["1.0.0.json", "1.1.0.json"];

    private BundledDefaultSettingsProfileCatalog(IReadOnlyList<DefaultSettingsProfile> profiles)
    {
        Profiles = profiles;
        Current = profiles[^1];
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
                ApprovedProfileFileNames,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The bundled default-settings catalog must contain exactly the approved profiles (1.0.0 and 1.1.0).");
        }

        DefaultSettingsProfile[] profiles = ApprovedProfileFileNames
            .Select(fileName => BundledDefaultSettingsProfileLoader.Load(
                File.ReadAllBytes(Path.Combine(root, fileName))))
            .ToArray();
        return new BundledDefaultSettingsProfileCatalog(Array.AsReadOnly(profiles));
    }

    internal DefaultSettingsProfile Resolve(
        EntityId<DefaultSettingsProfileIdTag> profileId,
        SettingsProfileVersion profileVersion,
        ContentDigest profileDigest)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(profileVersion);
        ArgumentNullException.ThrowIfNull(profileDigest);

        return Profiles.FirstOrDefault(profile =>
            profileId == profile.Id &&
            profileVersion == profile.Version &&
            profileDigest == profile.ContentDigest)
            ?? throw new InvalidOperationException(
                "The requested default-settings profile is not in the bundled catalog.");
    }
}
