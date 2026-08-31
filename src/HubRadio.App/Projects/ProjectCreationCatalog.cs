using HubRadio.App.Profiles;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Rendering;

namespace HubRadio.App.Projects;

internal sealed record ProjectCatalogOption(string Id, string Name);

internal sealed class ProjectCreationCatalog
{
    private static readonly string[] BundledRelativePaths =
    [
        Path.Combine("16x9", "long-form.json"),
        Path.Combine("16x9", "youtube-landscape.json"),
        Path.Combine("16x9", "landscape-short.json"),
        Path.Combine("9x16", "vertical-short.json"),
    ];

    private readonly IReadOnlyDictionary<string, ProjectOutputProfileReference> profiles;
    private readonly IReadOnlyDictionary<string, TemplateReference> templates;

    private ProjectCreationCatalog(
        IReadOnlyDictionary<string, ProjectOutputProfileReference> profiles,
        IReadOnlyDictionary<string, TemplateReference> templates)
    {
        this.profiles = profiles;
        this.templates = templates;
        OutputProfiles = Array.AsReadOnly(profiles.Values
            .OrderBy(static profile => profile.Name, StringComparer.Ordinal)
            .Select(static profile => new ProjectCatalogOption(
                profile.Id.ToString(),
                profile.Name))
            .ToArray());
        BrandTemplates = Array.AsReadOnly(templates.Values
            .OrderBy(static template => template.Name, StringComparer.Ordinal)
            .Select(static template => new ProjectCatalogOption(
                template.Id.ToString(),
                template.Name))
            .ToArray());
    }

    internal IReadOnlyList<ProjectCatalogOption> OutputProfiles { get; }

    internal IReadOnlyList<ProjectCatalogOption> BrandTemplates { get; }

    internal static ProjectCreationCatalog Load(string profilesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesDirectory);
        string root = Path.GetFullPath(profilesDirectory);
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                "The bundled project-creation catalog is unavailable.");
        }

        var profiles = new Dictionary<string, ProjectOutputProfileReference>(
            StringComparer.Ordinal);
        var templates = new Dictionary<string, TemplateReference>(
            StringComparer.Ordinal);
        foreach (string relativePath in BundledRelativePaths)
        {
            string path = Path.Combine(root, relativePath);
            BundledRenderProfileDefinition definition =
                BundledRenderProfileLoader.Load(File.ReadAllBytes(path));
            RenderProfile profile = definition.Profile;
            var profileReference = new ProjectOutputProfileReference(
                profile.Id,
                profile.Name,
                profile.ProfileDigest);
            if (!profiles.TryAdd(profile.Id.ToString(), profileReference) ||
                !templates.TryAdd(
                    definition.TemplateReference.Id.ToString(),
                    definition.TemplateReference))
            {
                throw new InvalidOperationException(
                    "The bundled project-creation catalog contains duplicate IDs.");
            }
        }

        return new ProjectCreationCatalog(profiles, templates);
    }

    internal ProjectOutputProfileReference ResolveOutputProfile(string value)
    {
        RequireCanonicalEntityId<RenderProfileIdTag>(value);
        return profiles.TryGetValue(value, out ProjectOutputProfileReference? profile)
            ? profile
            : throw new InvalidOperationException(
                "The selected output profile is not in the bundled catalog.");
    }

    internal TemplateReference ResolveBrandTemplate(string value)
    {
        RequireCanonicalEntityId<TemplateIdTag>(value);
        return templates.TryGetValue(value, out TemplateReference? template)
            ? template
            : throw new InvalidOperationException(
                "The selected brand template is not in the bundled catalog.");
    }

    private static void RequireCanonicalEntityId<TTag>(string value)
    {
        if (!EntityId<TTag>.TryParse(value, out EntityId<TTag>? id) ||
            !string.Equals(value, id.ToString(), StringComparison.Ordinal))
        {
            throw new FormatException(
                "A catalog ID must be a nonempty lowercase UUID in D format.");
        }
    }
}