using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CpuArchitecture = System.Runtime.InteropServices.Architecture;

namespace HubRadio.Architecture.Tests;

// OSL-013: independent public-candidate verification. Fixtures are tiny and
// deliberately never represent a valid runnable portable application.
[TestClass]
public sealed class PortableArtifactVerificationTests
{
    private const string ScriptPath = "packaging/portable/verify-portable.ps1";

    [TestMethod]
    [DataRow("missing", "OSL013:CONTENTS")]
    [DataRow("changed", "OSL013:RELEASE_CANDIDATE")]
    [DataRow("extra", "OSL013:CONTENTS")]
    [DataRow("buildlogs", "OSL013:PUBLIC_LAYOUT")]
    [DataRow("componentinputs", "OSL013:PUBLIC_LAYOUT")]
    [DataRow("hash", "OSL013:RELEASE_CANDIDATE")]
    [DataRow("sbom", "OSL013:SBOM")]
    public void PublicCandidateContractRejectsSyntheticTampering(string scenario, string expected)
    {
        using var fixture = new CandidateFixture();
        fixture.Mutate(scenario);
        (int exitCode, string stdout, string stderr) = fixture.Run();
        Assert.AreNotEqual(0, exitCode, stdout + stderr);
        StringAssert.Contains(stderr, expected);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), "Rejected candidates must not emit success JSON.");
        Assert.IsFalse((stdout + stderr).Contains(fixture.PrivateCanary, StringComparison.Ordinal));
        Assert.IsFalse((stdout + stderr).Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MetadataCompleteSyntheticCandidateNeverPassesAsRealBundle()
    {
        using var fixture = new CandidateFixture();
        (int exitCode, string stdout, string stderr) = fixture.Run();
        Assert.AreNotEqual(0, exitCode, stdout + stderr);
        StringAssert.Contains(stderr, "OSL013:APP_BUNDLE");
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout));
    }

    [TestMethod]
    public void ScriptRequiresOnlyMandatoryAbsoluteCandidatePathAndExcludesPrivateLogs()
    {
        string script = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ScriptPath)).ReplaceLineEndings("\n");
        StringAssert.StartsWith(script, "#Requires -Version 7.0");
        StringAssert.Contains(script, "[Parameter(Mandatory)]");
        StringAssert.Contains(script, "[string]$CandidatePath");
        StringAssert.Contains(script, "BuildLogs");
        StringAssert.Contains(script, "releaseApproved -ne $false");
    }

    private sealed class CandidateFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "HubRadio.OSL013-" + Guid.NewGuid().ToString("N"));
        public string PrivateCanary { get; } = "private-osl013-canary";
        private string AppPath => Path.Combine(Root, "App/HubRadio.App.exe");
        private string ContentsPath => Path.Combine(Root, "Contents/Worker/data.bin");
        private string PayloadPath => Path.Combine(Root, "Contents/Evidence/portable-payload.json");
        private string ReleasePath => Path.Combine(Root, "Evidence/portable-release.json");
        private string SbomPath => Path.Combine(Root, "Sbom/manifest.json");

        public CandidateFixture()
        {
            foreach (string directory in new[] { "App", "Contents/Evidence", "Contents/Worker", "Evidence", "Sbom" }) { Directory.CreateDirectory(Path.Combine(Root, directory)); }
            File.WriteAllBytes(AppPath, [0x4d, 0x5a, 0x01]);
            File.WriteAllText(ContentsPath, "synthetic payload", new UTF8Encoding(false));
            WriteManifests();
        }

        public void Mutate(string scenario)
        {
            switch (scenario)
            {
                case "missing": File.Delete(ContentsPath); break;
                case "changed": File.AppendAllText(AppPath, "changed"); break;
                case "extra": File.WriteAllText(Path.Combine(Root, "Contents/extra.bin"), "extra"); break;
                case "buildlogs": Directory.CreateDirectory(Path.Combine(Root, "BuildLogs")); break;
                case "componentinputs": Directory.CreateDirectory(Path.Combine(Root, "ComponentInputs")); break;
                case "hash":
                    JsonObject release = JsonNode.Parse(File.ReadAllBytes(ReleasePath))!.AsObject();
                    release["candidate"]!["sha256"] = new string('0', 64);
                    WriteJson(ReleasePath, release);
                    break;
                case "sbom":
                    JsonObject sbom = JsonNode.Parse(File.ReadAllBytes(SbomPath))!.AsObject();
                    sbom["files"]![0]!["checksums"]![0]!["checksumValue"] = new string('0', 64);
                    WriteJson(SbomPath, sbom);
                    break;
                default: Assert.Fail("Unknown fixture scenario: " + scenario); break;
            }
        }

        private void WriteManifests()
        {
            byte[] content = File.ReadAllBytes(ContentsPath);
            string contentHash = Hash(content);
            string payloadText = JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0.0",
                taskId = "OSL-009",
                architecture = "x64",
                releaseApproved = false,
                fileCount = 1,
                totalBytes = content.LongLength,
                artifactSetSha256 = Digest("Worker/data.bin", content),
                files = new[] { new { path = "Worker/data.bin", byteSize = content.LongLength, sha256 = contentHash } },
            });
            File.WriteAllText(PayloadPath, payloadText, new UTF8Encoding(false));
            JsonObject payload = JsonNode.Parse(payloadText)!.AsObject();
            string payloadHash = Hash(File.ReadAllBytes(PayloadPath));
            string contentsDigest = Digest("Evidence/portable-payload.json", File.ReadAllBytes(PayloadPath), "Worker/data.bin", content);
            string sbomText = JsonSerializer.Serialize(new
            {
                spdxVersion = "SPDX-2.2",
                files = new[]
                {
                    new { fileName = "./Evidence/portable-payload.json", checksums = new[] { new { algorithm = "SHA256", checksumValue = payloadHash } } },
                    new { fileName = "./Worker/data.bin", checksums = new[] { new { algorithm = "SHA256", checksumValue = contentHash } } },
                },
            });
            File.WriteAllText(SbomPath, sbomText, new UTF8Encoding(false));
            string appHash = Hash(File.ReadAllBytes(AppPath));
            JsonObject release = new()
            {
                ["schemaVersion"] = "1.0.0",
                ["taskId"] = "OSL-012",
                ["architecture"] = "x64",
                ["releaseApproved"] = false,
                ["candidate"] = new JsonObject { ["path"] = "App/HubRadio.App.exe", ["byteSize"] = 3, ["sha256"] = appHash },
                ["contents"] = new JsonObject
                {
                    ["path"] = "Contents",
                    ["fileCount"] = 2,
                    ["totalBytes"] = File.ReadAllBytes(PayloadPath).LongLength + content.LongLength,
                    ["artifactSetSha256"] = contentsDigest,
                    ["files"] = new JsonArray(Record("Evidence/portable-payload.json", File.ReadAllBytes(PayloadPath)), Record("Worker/data.bin", content))
                },
                ["preparedPayload"] = new JsonObject { ["manifestPath"] = "Contents/Evidence/portable-payload.json", ["manifestSha256"] = payloadHash, ["artifactSetSha256"] = payload["artifactSetSha256"]!.GetValue<string>() },
                ["sbom"] = new JsonObject { ["path"] = "Sbom/manifest.json", ["byteSize"] = new FileInfo(SbomPath).Length, ["sha256"] = Hash(File.ReadAllBytes(SbomPath)), ["format"] = "SPDX-2.2" },
            };
            WriteJson(ReleasePath, release);
        }

        private static JsonObject Record(string path, byte[] bytes) => new() { ["path"] = path, ["byteSize"] = bytes.LongLength, ["sha256"] = Hash(bytes) };
        private static string Digest(params object[] values)
        {
            var text = new StringBuilder();
            for (int i = 0; i < values.Length; i += 2) { text.Append((string)values[i]).Append('\0').Append(((byte[])values[i + 1]).LongLength).Append('\0').Append(Hash((byte[])values[i + 1])).Append('\n'); }
            return Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        private static void WriteJson(string path, JsonNode value) => File.WriteAllText(path, value.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));

        public (int ExitCode, string Stdout, string Stderr) Run()
        {
            if (!OperatingSystem.IsWindows() || CpuArchitecture.X64 != System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ||
                CpuArchitecture.X64 != System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) { Assert.Inconclusive("OSL-013 requires Windows x64."); }
            string? pwsh = Environment.ProcessPath is { } current
                && Path.GetFileName(current).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
                ? current
                : new[]
                {
                    Environment.GetEnvironmentVariable("PSHOME"),
                    Environment.GetEnvironmentVariable("PATH")?
                        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(path => Path.Combine(path, "pwsh.exe"))
                        .FirstOrDefault(path => File.Exists(path)),
                }
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => string.Equals(Path.GetFileName(path), "pwsh.exe", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : Path.Combine(path!, "pwsh.exe"))
                .FirstOrDefault(static path => File.Exists(path));
            if (pwsh is null) { Assert.Inconclusive("PowerShell 7+ is required; Windows PowerShell is not accepted."); }
            var start = new ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = GetRepositoryRoot(), RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(GetRepositoryRoot(), ScriptPath), "-CandidatePath", Root }) { start.ArgumentList.Add(arg); }
            using var process = Process.Start(start)!;
            string stdout = process.StandardOutput.ReadToEnd(); string stderr = process.StandardError.ReadToEnd();
            Assert.IsTrue(process.WaitForExit(10_000), "OSL-013 verifier did not terminate promptly.");
            return (process.ExitCode, stdout, stderr);
        }

        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null) { if (File.Exists(Path.Combine(current.FullName, "HubRadio.slnx"))) { return current.FullName; } current = current.Parent; }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
