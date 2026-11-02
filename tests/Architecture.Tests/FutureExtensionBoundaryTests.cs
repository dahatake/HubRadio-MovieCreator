using System.Text.RegularExpressions;

namespace HubRadio.Architecture.Tests;

// NFR-EXT-001 AC-119: 将来機能のために、実在する 2 つ目の実装がない provider・factory・plugin 基盤を先行して実装しない。
[TestClass]
public sealed class FutureExtensionBoundaryTests
{
    private static readonly Regex ExtensionInterface = new(
        @"\binterface\s+(I\w*(?:Provider|Factory|Plugin|Registry|Strategy))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PluginInfrastructure = new(
        @"\b(?:AssemblyLoadContext|Assembly\s*\.\s*LoadFrom|Assembly\s*\.\s*LoadFile|IPlugin|PluginLoader|PluginHost|PluginManager|CompositionContainer|ExportAttribute)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // NFR-EXT-001 AC-119
    [TestMethod]
    public void ProviderFactoryPluginInterfacesHaveASecondRealImplementation()
    {
        string root = FindRoot();
        var sources = ReadCs(Path.Combine(root, "src"));
        var all = sources.Concat(ReadCs(Path.Combine(root, "tests"))).ToList();
        var violations = new List<string>();
        foreach (var (path, text) in sources)
        {
            foreach (Match m in ExtensionInterface.Matches(text))
            {
                string name = m.Groups[1].Value;
                var implementer = new Regex(
                    @"\b(?:class|record|struct)\s+\w+[^{;=]*?:[^{;]*\b" + Regex.Escape(name) + @"\b",
                    RegexOptions.CultureInvariant);
                int count = all.Sum(f => implementer.Matches(f.Text).Count);
                if (count < 2)
                {
                    violations.Add($"{Path.GetRelativePath(root, path)}: {name} has {count} implementation(s)");
                }
            }
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    // NFR-EXT-001 AC-119
    [TestMethod]
    public void NoPluginLoadingInfrastructureExistsInProductSource()
    {
        string root = FindRoot();
        var hits = ReadCs(Path.Combine(root, "src"))
            .Where(f => PluginInfrastructure.IsMatch(f.Text))
            .Select(f => Path.GetRelativePath(root, f.Path))
            .ToList();
        Assert.AreEqual(0, hits.Count, string.Join(", ", hits));
    }

    private static List<(string Path, string Text)> ReadCs(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            .Select(p => (p, File.ReadAllText(p)))
            .ToList();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
