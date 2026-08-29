using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HubRadio.Architecture.Tests;

[TestClass]
public sealed class ArchitectureTests
{
    private const string ShellExecutableLiteral =
        """(?:@?"(?:[^"\r\n]*[\\/])?(?:cmd|powershell|pwsh|bash|sh|zsh|dash|ksh|fish)(?:\.exe)?(?:\s+[^"\r\n]*)?"|'(?:[^'\r\n]*[\\/])?(?:cmd|powershell|pwsh|bash|sh|zsh|dash|ksh|fish)(?:\.exe)?(?:\s+[^'\r\n]*)?')""";

    private static readonly HashSet<string> ProductSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".props",
        ".targets",
        ".py",
        ".js",
        ".jsx",
        ".mjs",
        ".cjs",
        ".ts",
        ".tsx",
        ".json",
        ".toml",
    };

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pytest_cache",
        ".venv",
        "__pycache__",
        "bin",
        "coverage",
        "dist",
        "node_modules",
        "obj",
        "test",
        "tests",
        "venv",
    };

    private static readonly RulePattern[] CSharpShellRules =
    {
        new(
            "shell.use-shell-execute",
            CreateRegex("""\bUseShellExecute\s*=\s*true\b"""),
            "ProcessStartInfo.UseShellExecute must remain false."),
        new(
            "shell.known-executable",
            CreateRegex("""(?:\bProcess\s*\.\s*Start|\bProcessStartInfo)\s*\(\s*""" + ShellExecutableLiteral),
            "A process API starts a known command shell."),
        new(
            "shell.known-executable",
            CreateRegex("""\bFileName\s*=\s*""" + ShellExecutableLiteral),
            "ProcessStartInfo.FileName names a known command shell."),
    };

    private static readonly RulePattern[] PythonShellRules =
    {
        new(
            "shell.python-shell-true",
            CreateRegex("""\bshell\s*=\s*True\b"""),
            "Python subprocess shell=True is forbidden."),
        new(
            "shell.python-shell-api",
            CreateRegex("""\b(?:os\.(?:system|popen)|subprocess\.(?:getoutput|getstatusoutput)|asyncio\.create_subprocess_shell)\s*\("""),
            "A Python API that always invokes a command shell is used."),
        new(
            "shell.known-executable",
            CreateRegex("""\b(?:subprocess\.(?:run|Popen|call|check_call|check_output)|asyncio\.create_subprocess_exec)\s*\(\s*(?:\[\s*)?""" + ShellExecutableLiteral),
            "A Python process API starts a known command shell."),
    };

    private static readonly RulePattern[] JavaScriptShellRules =
    {
        new(
            "shell.node-exec-api",
            CreateRegex("""(?:import\s*\{[^}]*\b(?:exec|execSync)\b[^}]*\}\s*from\s*|const\s*\{[^}]*\b(?:exec|execSync)\b[^}]*\}\s*=\s*require\s*\(\s*)["'](?:node:)?child_process["']"""),
            "Node child_process exec/execSync uses a command shell."),
        new(
            "shell.node-exec-api",
            CreateRegex("""\bchild_process\s*\.\s*(?:exec|execSync)\s*\("""),
            "Node child_process exec/execSync uses a command shell."),
        new(
            "shell.node-shell-option",
            CreateRegex("""\b(?:spawn|spawnSync|execFile|execFileSync|execa)\s*\([^;]{0,1000}\bshell\s*:\s*true\b"""),
            "A Node process API enables its shell option."),
        new(
            "shell.known-executable",
            CreateRegex("""\b(?:spawn|spawnSync|execFile|execFileSync|execa)\s*\(\s*""" + ShellExecutableLiteral),
            "A Node process API starts a known command shell."),
    };

    private static readonly RulePattern[] ProjectShellRules =
    {
        new(
            "shell.msbuild-known-executable",
            CreateRegex("""<Exec\b[^>]*\bCommand\s*=\s*["'][^"'\r\n]*\b(?:cmd|powershell|pwsh|bash|sh|zsh|dash|ksh|fish)(?:\.exe)?\b"""),
            "An MSBuild Exec command invokes a known command shell."),
    };

    private static readonly RulePattern[] PackageScriptShellRules =
    {
        new(
            "shell.package-script-known-executable",
            CreateRegex("""(?:^|[\s&|;\\/'"])(?:cmd|powershell|pwsh|bash|sh|zsh|dash|ksh|fish)(?:\.exe)?(?=$|[\s&|;'\"])"""),
            "A package.json script invokes a known command shell."),
    };

    private static readonly RulePattern[] DotNetProjectServerRules =
    {
        new(
            "server.aspnet-sdk",
            CreateRegex("""<Project\b[^>]*\bSdk\s*=\s*["'][^"']*Microsoft\.NET\.Sdk\.Web[^"']*["']"""),
            "A product project uses Microsoft.NET.Sdk.Web."),
        new(
            "server.aspnet-sdk",
            CreateRegex("""<Sdk\b[^>]*\bName\s*=\s*["']Microsoft\.NET\.Sdk\.Web["']"""),
            "A product project imports Microsoft.NET.Sdk.Web."),
        new(
            "server.aspnet-reference",
            CreateRegex("""<(?:FrameworkReference|PackageReference)\b[^>]*(?:Include|Update)\s*=\s*["']Microsoft\.AspNetCore(?:\.App|\.[^"']+)?["']"""),
            "A product project references ASP.NET Core."),
    };

    private static readonly RulePattern[] CSharpServerRules =
    {
        new(
            "server.aspnet-api",
            CreateRegex("""\bMicrosoft\.AspNetCore(?:\.|\b)"""),
            "Product code uses an ASP.NET Core namespace or type."),
        new(
            "server.http-listener",
            CreateRegex("""\bHttpListener\b"""),
            "Product code uses System.Net.HttpListener."),
        new(
            "server.kestrel",
            CreateRegex("""\b[A-Za-z0-9_]*Kestrel[A-Za-z0-9_]*\b"""),
            "Product code uses Kestrel."),
        new(
            "server.web-host",
            CreateRegex("""\b(?:WebApplication\s*\.\s*CreateBuilder|WebHost\s*\.\s*CreateDefaultBuilder|ConfigureWebHostDefaults|UseUrls|ListenLocalhost|ListenAnyIP)\s*\("""),
            "Product code configures a runtime web host."),
        new(
            "server.localhost-binding",
            CreateRegex("""\b(?:Run|RunAsync|Start|StartAsync)\s*\(\s*@?["']https?://(?:localhost|127\.0\.0\.1|\[::1\])(?::\d+)?(?:/|["'])"""),
            "Product code binds a runtime endpoint on localhost."),
        new(
            "server.localhost-binding",
            CreateRegex("""\b(?:Prefixes|Urls)\s*\.\s*Add\s*\(\s*@?["']https?://(?:localhost|127\.0\.0\.1|\[::1\])(?::\d+)?(?:/|["'])"""),
            "Product code registers a runtime endpoint on localhost."),
    };

    private static readonly RulePattern[] PythonServerRules =
    {
        new(
            "server.python-http-server",
            CreateRegex("""\b(?:from\s+http\.server\s+import|import\s+http\.server\b|(?:HTTPServer|ThreadingHTTPServer|TCPServer|ThreadingTCPServer)\s*\()"""),
            "Product code introduces a Python HTTP server."),
        new(
            "server.python-runtime",
            CreateRegex("""\b(?:uvicorn\.run|waitress\.serve|hypercorn\.asyncio\.serve)\s*\("""),
            "Product code starts a Python runtime server."),
        new(
            "server.runtime-listener",
            CreateRegex("""\b(?:app|server)\s*\.\s*(?:run|serve|listen)\s*\("""),
            "Product code starts a Python runtime server."),
    };

    private static readonly RulePattern[] JavaScriptServerRules =
    {
        new(
            "server.node-http-server",
            CreateRegex("""import\s*\{[^}]*\bcreateServer\b[^}]*\}\s*from\s*["'](?:node:)?https?["']"""),
            "Product code imports a Node HTTP server factory."),
        new(
            "server.node-http-server",
            CreateRegex("""\b(?:http|https)\s*\.\s*createServer\s*\("""),
            "Product code creates a Node HTTP server."),
        new(
            "server.runtime-listener",
            CreateRegex("""\b[A-Za-z_$][A-Za-z0-9_$]*\s*\.\s*listen\s*\("""),
            "Product code starts a JavaScript runtime listener."),
        new(
            "server.runtime-listener",
            CreateRegex("""\b(?:Bun|Deno)\s*\.\s*serve\s*\("""),
            "Product code starts a JavaScript runtime server."),
    };

    [TestMethod]
    public void RepositoryRootLocatorFindsRootFromOutputAndNestedDirectory()
    {
        string root = GetRepositoryRoot();
        string nestedRoot = FindRepositoryRoot(Path.Combine(root, "src", "HubRadio.Web", "src"));

        Assert.IsTrue(
            string.Equals(root, nestedRoot, StringComparison.OrdinalIgnoreCase),
            $"Expected both starting points to resolve to '{root}', but nested lookup returned '{nestedRoot}'.");
    }

    [TestMethod]
    public void CoreProjectsDoNotReferenceInfrastructureOrApp()
    {
        string root = GetRepositoryRoot();
        string coreDirectory = Path.Combine(root, "src", "HubRadio.Core");
        string[] coreProjects = Directory.GetFiles(coreDirectory, "*.csproj", SearchOption.AllDirectories);

        Assert.IsTrue(coreProjects.Length > 0, $"No Core project was found below '{coreDirectory}'.");

        var violations = new List<string>();
        foreach (string projectFile in coreProjects.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            XDocument project = XDocument.Load(projectFile, LoadOptions.SetLineInfo);
            foreach (XElement reference in project.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                string? include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include) || !IsForbiddenCoreReference(include))
                {
                    continue;
                }

                int line = reference is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;
                violations.Add(
                    $"{ToRepositoryRelativePath(root, projectFile)}:{line}: dependency.core-outward: " +
                    $"Core must not reference '{include}'.");
            }
        }

        Assert.AreEqual(
            0,
            violations.Count,
            "Core has forbidden outward project references:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void ProductSourceDoesNotUseCommandShells()
    {
        string root = GetRepositoryRoot();
        List<RuleViolation> violations = ScanProductSource(root, FindShellExecutionViolations);

        Assert.AreEqual(
            0,
            violations.Count,
            "Product source contains forbidden shell execution:" + Environment.NewLine + FormatViolations(violations));
    }

    [TestMethod]
    public void WorkspaceAclDoesNotInvokeExternalProcesses()
    {
        string root = GetRepositoryRoot();
        string sourcePath = Path.Combine(
            root,
            "src",
            "HubRadio.Infrastructure",
            "Security",
            "WorkspaceAcl.cs");
        string source = File.ReadAllText(sourcePath);
        string[] forbiddenFragments =
        [
            "System.Diagnostics.Process",
            "ProcessStartInfo",
            "Process.Start",
            "icacls",
        ];

        foreach (string fragment in forbiddenFragments)
        {
            Assert.IsFalse(
                source.Contains(fragment, StringComparison.Ordinal),
                $"WorkspaceAcl must use managed access-control APIs, not '{fragment}'.");
        }
    }

    [TestMethod]
    public void ProductSourceDoesNotIntroduceRuntimeServers()
    {
        string root = GetRepositoryRoot();
        List<RuleViolation> violations = ScanProductSource(root, FindRuntimeServerViolations);

        Assert.AreEqual(
            0,
            violations.Count,
            "Product source contains a forbidden runtime server:" + Environment.NewLine + FormatViolations(violations));
    }

    [TestMethod]
    [DataRow("sample.cs", "var startInfo = new ProcessStartInfo { UseShellExecute = true };")]
    [DataRow("sample.cs", "Process.Start(\"cmd.exe\");")]
    [DataRow("sample.cs", "var startInfo = new ProcessStartInfo(@\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\");")]
    [DataRow("sample.py", "subprocess.run(['ffmpeg', '-version'], shell=True)")]
    [DataRow("sample.py", "os.system('ffmpeg -version')")]
    [DataRow("sample.ts", "import { exec } from 'node:child_process'; exec('tool');")]
    [DataRow("package.json", "{ \"scripts\": { \"unsafe\": \"cmd.exe /c tool\" } }")]
    public void ShellRuleNegativeControlsDetectProhibitedSource(string path, string source)
    {
        IReadOnlyList<RuleViolation> violations = FindShellExecutionViolations(path, source);

        Assert.IsTrue(violations.Count > 0, $"Expected the shell rule to reject {path}: {source}");
    }

    [TestMethod]
    [DataRow("sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />")]
    [DataRow("sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup></Project>")]
    [DataRow("sample.cs", "using System.Net; var listener = new HttpListener();")]
    [DataRow("sample.cs", "builder.WebHost.UseKestrel();")]
    [DataRow("sample.cs", "app.Run(\"http://localhost:5000\");")]
    [DataRow("sample.py", "from http.server import ThreadingHTTPServer")]
    [DataRow("sample.py", "from flask import Flask\napp = Flask(__name__)\napp.run()")]
    [DataRow("sample.ts", "import { createServer } from 'node:http';")]
    [DataRow("sample.ts", "const api = express(); api.listen(3000);")]
    public void RuntimeServerRuleNegativeControlsDetectProhibitedSource(string path, string source)
    {
        IReadOnlyList<RuleViolation> violations = FindRuntimeServerViolations(path, source);

        Assert.IsTrue(violations.Count > 0, $"Expected the runtime-server rule to reject {path}: {source}");
    }

    [TestMethod]
    public void RuntimeRulesAllowViteAsDevelopmentBuildTool()
    {
        const string packageJson =
            """
            {
              "scripts": {
                "dev": "vite --host localhost",
                "build": "vite build"
              },
              "devDependencies": {
                "vite": "8.2.2"
              }
            }
            """;
        const string viteConfig =
            """
            import { defineConfig } from 'vite'

            export default defineConfig({
              server: { host: 'localhost', port: 5173 },
              build: { assetsInlineLimit: 0 },
            })
            """;

        var violations = new List<RuleViolation>();
        violations.AddRange(FindShellExecutionViolations("package.json", packageJson));
        violations.AddRange(FindRuntimeServerViolations("package.json", packageJson));
        violations.AddRange(FindShellExecutionViolations("vite.config.ts", viteConfig));
        violations.AddRange(FindRuntimeServerViolations("vite.config.ts", viteConfig));

        Assert.AreEqual(
            0,
            violations.Count,
            "Vite development/build configuration must not be treated as a product runtime server:" +
            Environment.NewLine + FormatViolations(violations));
    }

    [TestMethod]
    [DataRow("..\\HubRadio.Infrastructure\\HubRadio.Infrastructure.csproj")]
    [DataRow("..\\HubRadio.App\\HubRadio.App.csproj")]
    public void CoreReferenceNegativeControlsDetectForbiddenTargets(string include)
    {
        Assert.IsTrue(IsForbiddenCoreReference(include), $"Expected Core reference '{include}' to be forbidden.");
    }

    [TestMethod]
    public void ShellRulesAllowDirectExecutablesAndNonScriptMetadata()
    {
        const string csharp =
            "var startInfo = new ProcessStartInfo(\"ffmpeg.exe\") { UseShellExecute = false };";
        const string python =
            "subprocess.run(['ffmpeg', '-version'], shell=False, check=True)";
        const string packageJson =
            """
            {
              "description": "Documents why bash and cmd.exe are forbidden at runtime",
              "scripts": { "build": "vite build" }
            }
            """;

        var violations = new List<RuleViolation>();
        violations.AddRange(FindShellExecutionViolations("sample.cs", csharp));
        violations.AddRange(FindShellExecutionViolations("sample.py", python));
        violations.AddRange(FindShellExecutionViolations("package.json", packageJson));

        Assert.AreEqual(
            0,
            violations.Count,
            "Direct executable invocation and non-script package metadata must remain allowed:" +
            Environment.NewLine + FormatViolations(violations));
    }

    private static string GetRepositoryRoot()
    {
        return FindRepositoryRoot(AppContext.BaseDirectory, Environment.CurrentDirectory);
    }

    private static string FindRepositoryRoot(params string[] startPaths)
    {
        foreach (string startPath in startPaths)
        {
            string fullPath = Path.GetFullPath(startPath);
            DirectoryInfo? current = File.Exists(fullPath)
                ? new FileInfo(fullPath).Directory
                : new DirectoryInfo(fullPath);

            while (current is not null)
            {
                bool hasSolution = File.Exists(Path.Combine(current.FullName, "HubRadio.slnx"));
                bool hasBuildProps = File.Exists(Path.Combine(current.FullName, "Directory.Build.props"));
                bool hasProductSource = Directory.Exists(Path.Combine(current.FullName, "src"));
                bool hasTests = Directory.Exists(Path.Combine(current.FullName, "tests"));
                if (hasSolution && hasBuildProps && hasProductSource && hasTests)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root. Expected HubRadio.slnx, Directory.Build.props, src, and tests " +
            $"above one of: {string.Join(", ", startPaths.Select(Path.GetFullPath))}");
    }

    private static bool IsForbiddenCoreReference(string include)
    {
        string normalized = include.Replace('\\', '/').Trim();
        string projectName = Path.GetFileNameWithoutExtension(normalized);
        if (projectName.Equals("HubRadio.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
            projectName.Equals("HubRadio.App", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
                segment.Equals("HubRadio.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("HubRadio.App", StringComparison.OrdinalIgnoreCase));
    }

    private static List<RuleViolation> ScanProductSource(
        string root,
        Func<string, string, IReadOnlyList<RuleViolation>> inspect)
    {
        var violations = new List<RuleViolation>();
        foreach (string file in EnumerateProductSourceFiles(root))
        {
            string relativePath = ToRepositoryRelativePath(root, file);
            violations.AddRange(inspect(relativePath, File.ReadAllText(file)));
        }

        return violations;
    }

    private static IReadOnlyList<string> EnumerateProductSourceFiles(string root)
    {
        string sourceRoot = Path.Combine(root, "src");
        var pendingDirectories = new Stack<string>();
        var files = new List<string>();
        pendingDirectories.Push(sourceRoot);

        while (pendingDirectories.Count > 0)
        {
            string current = pendingDirectories.Pop();
            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                if (!IgnoredDirectoryNames.Contains(Path.GetFileName(directory)))
                {
                    pendingDirectories.Push(directory);
                }
            }

            foreach (string file in Directory.EnumerateFiles(current))
            {
                string fileName = Path.GetFileName(file);
                string extension = Path.GetExtension(file);
                if (!ProductSourceExtensions.Contains(extension) ||
                    fileName.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
                    fileName.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(file);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static IReadOnlyList<RuleViolation> FindShellExecutionViolations(string path, string content)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return FindMatches(path, content, CSharpShellRules);
        }

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return FindMatches(path, content, PythonShellRules);
        }

        if (IsJavaScriptOrTypeScript(extension))
        {
            return FindMatches(path, content, JavaScriptShellRules);
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
        {
            return FindMatches(path, content, ProjectShellRules);
        }

        if (Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return FindPackageScriptShellViolations(path, content);
        }

        return Array.Empty<RuleViolation>();
    }

    private static IReadOnlyList<RuleViolation> FindPackageScriptShellViolations(string path, string content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("scripts", out JsonElement scripts) ||
            scripts.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<RuleViolation>();
        }

        var violations = new List<RuleViolation>();
        foreach (JsonProperty script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string command = script.Value.GetString() ?? string.Empty;
            foreach (RulePattern rule in PackageScriptShellRules)
            {
                if (!rule.Pattern.IsMatch(command))
                {
                    continue;
                }

                int scriptNameIndex = content.IndexOf($"\"{script.Name}\"", StringComparison.Ordinal);
                violations.Add(new RuleViolation(
                    path,
                    GetLineNumber(content, Math.Max(0, scriptNameIndex)),
                    rule.Id,
                    $"package.json script '{script.Name}' invokes a known command shell."));
            }
        }

        return violations;
    }

    private static IReadOnlyList<RuleViolation> FindRuntimeServerViolations(string path, string content)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
        {
            return FindMatches(path, content, DotNetProjectServerRules);
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return FindMatches(path, content, CSharpServerRules);
        }

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return FindMatches(path, content, PythonServerRules);
        }

        if (IsJavaScriptOrTypeScript(extension))
        {
            return FindMatches(path, content, JavaScriptServerRules);
        }

        return Array.Empty<RuleViolation>();
    }

    private static IReadOnlyList<RuleViolation> FindMatches(
        string path,
        string content,
        IEnumerable<RulePattern> rules)
    {
        var violations = new List<RuleViolation>();
        foreach (RulePattern rule in rules)
        {
            foreach (Match match in rule.Pattern.Matches(content))
            {
                violations.Add(new RuleViolation(path, GetLineNumber(content, match.Index), rule.Id, rule.Evidence));
            }
        }

        return violations;
    }

    private static bool IsJavaScriptOrTypeScript(string extension)
    {
        return extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLineNumber(string content, int index)
    {
        int line = 1;
        for (int position = 0; position < index; position++)
        {
            if (content[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string ToRepositoryRelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private static string FormatViolations(IEnumerable<RuleViolation> violations)
    {
        return string.Join(
            Environment.NewLine,
            violations.Select(violation =>
                $"{violation.Path}:{violation.Line}: {violation.Rule}: {violation.Evidence}"));
    }

    private static Regex CreateRegex(string pattern)
    {
        return new Regex(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    private sealed record RulePattern(string Id, Regex Pattern, string Evidence);

    private sealed record RuleViolation(string Path, int Line, string Rule, string Evidence);
}
