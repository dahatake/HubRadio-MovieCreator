using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;
using HubRadio.Infrastructure.Media;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class MediaImportSecurityTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2036, 7, 8, 9, 10, 11, TimeSpan.Zero);

    private static readonly string TestAssemblyPath =
        typeof(MediaImportSecurityTests).Assembly.Location;

    private static readonly string TestWorkingDirectory =
        Path.GetDirectoryName(TestAssemblyPath)
        ?? throw new InvalidOperationException("The test assembly directory is unavailable.");

    private static readonly string DotNetExecutable = ResolveDotNetExecutable();

    [TestMethod]
    public async Task CopyToStagingAsync_TraversalNetworkDeviceAndAdsSourcesFailClosed()
    {
        using SecurityFixture fixture = new();
        string ordinaryPath = fixture.CreateSource("ordinary.mov", [1]);
        List<string> unsafePaths =
        [
            "../outside.mov",
            @"..\outside.mov",
            @"\\server\share\private.mov",
            @"\\?\C:\private.mov",
            @"\\.\pipe\hubradio-media-import-private",
        ];
        if (OperatingSystem.IsWindows())
        {
            unsafePaths.Add(ordinaryPath + ":private-stream");
        }

        foreach (string sourcePath in unsafePaths)
        {
            EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

            MediaImportException exception =
                await Assert.ThrowsExactlyAsync<MediaImportException>(() =>
                    fixture.Importer.CopyToStagingAsync(
                        candidateId,
                        projectId,
                        jobId,
                        CancellationToken.None));

            Assert.AreEqual(MediaImportFailureReason.UnsafeSource, exception.Reason);
            Assert.IsFalse(exception.RecoveryRequired);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), sourcePath);
            AssertDoesNotContain(exception.ToString(), fixture.WorkspacePath);
            Assert.IsFalse(Directory.Exists(
                fixture.GetStagingDirectory(projectId, jobId)));
        }
    }

    [TestMethod]
    public void PortableWorkspacePaths_TraversalUncAdsAndDevicesAreRejected()
    {
        using SecurityFixture fixture = new();
        string[] unsafePaths =
        [
            "../outside.mov",
            "nested/../../outside.mov",
            "/absolute.mov",
            "C:/absolute.mov",
            @"\\server\share\private.mov",
            @"nested\private.mov",
            "nested/private.mov:stream",
            "nested/CON.txt",
            "nested/NUL.mov",
            "nested/PRN.",
            "nested/COM1.mov",
            "nested/LPT9.mov",
        ];

        foreach (string unsafePath in unsafePaths)
        {
            Assert.IsFalse(
                WorkspacePathSafety.IsValidPortableRelativePath(unsafePath),
                unsafePath);
            Assert.IsFalse(WorkspacePathSafety.TryResolvePortableRelativePath(
                fixture.WorkspacePath,
                unsafePath,
                out string resolved));
            Assert.AreEqual(string.Empty, resolved);
        }

        const string optionLikePath = "nested/--help;-v&literal.mov";
        Assert.IsTrue(WorkspacePathSafety.IsValidPortableRelativePath(optionLikePath));
        Assert.IsTrue(WorkspacePathSafety.TryResolvePortableRelativePath(
            fixture.WorkspacePath,
            optionLikePath,
            out string safePath));
        Assert.IsTrue(WorkspacePathSafety.IsStrictDescendant(
            fixture.WorkspacePath,
            safePath));
    }

    [TestMethod]
    public async Task OptionLikeMetacharacterLeafName_IsQuarantinedFromEveryToolRequest()
    {
        const string sourceFileName = "--help -v;$(literal)&%COMSPEC%^!.mov";
        using SecurityFixture fixture = new();
        byte[] content = Encoding.UTF8.GetBytes("synthetic option-like source bytes");
        string sourcePath = fixture.CreateSource(sourceFileName, content);
        EntityId<AssetIdTag> candidateId = fixture.Register(sourcePath);
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<JobIdTag> jobId = EntityId<JobIdTag>.New();

        StagedMediaImport staged = await fixture.Importer.CopyToStagingAsync(
            candidateId,
            projectId,
            jobId,
            CancellationToken.None);
        MediaProbeResult probeResult = CreateProbeResult(staged);
        PtsIndex ptsIndex = CreatePtsIndex(staged);
        string ffprobePath = Path.Combine(fixture.RootPath, "ffprobe.exe");
        string ffmpegPath = Path.Combine(fixture.RootPath, "ffmpeg.exe");
        ProcessRunRequest[] requests =
        [
            FfprobeClient.CreateProcessRequest(
                ffprobePath,
                staged.StagingPath,
                TimeSpan.FromSeconds(30)),
            PtsIndexer.CreateProcessRequest(
                ffprobePath,
                staged.StagingPath,
                streamIndex: 0,
                TimeSpan.FromSeconds(30)),
            PtsIndexer.CreatePacketProcessRequest(
                ffprobePath,
                staged.StagingPath,
                streamIndex: 0,
                TimeSpan.FromSeconds(30)),
            DecodeValidator.CreateProcessRequest(
                ffmpegPath,
                staged.StagingPath,
                videoStreamIndex: 0,
                audioStreamIndex: null,
                TimeSpan.FromSeconds(30)),
        ];

        Assert.AreEqual(sourceFileName, staged.SourceFileName);
        Assert.AreEqual(
            string.Concat(candidateId.ToString(), ".ready"),
            Path.GetFileName(staged.StagingPath));
        Assert.IsTrue(Path.IsPathFullyQualified(staged.StagingPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(sourcePath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(staged.StagingPath));
        _ = DecodeValidator.ValidateInputBinding(staged, probeResult, ptsIndex);

        foreach (ProcessRunRequest request in requests)
        {
            Assert.AreEqual(
                1,
                request.ArgumentList.Count(argument =>
                    string.Equals(argument, staged.StagingPath, StringComparison.Ordinal)));
            Assert.IsFalse(request.ArgumentList.Any(argument =>
                argument.Contains(sourceFileName, StringComparison.Ordinal)));
            Assert.IsNull(request.StandardInput);

            ProcessStartInfo startInfo = CreateStartInfo(request);
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.AreEqual(request.Executable, startInfo.FileName);
            CollectionAssert.AreEqual(
                request.ArgumentList.ToArray(),
                startInfo.ArgumentList.ToArray());
        }

        Assert.AreEqual(
            ProcessRunRequest.DefaultMaxCapturedBytes,
            requests[0].MaxCapturedBytes);
        Assert.AreEqual(
            ProcessRunRequest.MaximumMaxCapturedBytes,
            requests[1].MaxCapturedBytes);
        Assert.AreEqual(
            ProcessRunRequest.MaximumMaxCapturedBytes,
            requests[2].MaxCapturedBytes);
        Assert.AreEqual(
            ProcessRunRequest.DefaultMaxCapturedBytes,
            requests[3].MaxCapturedBytes);
    }

    [TestMethod]
    public void CorruptAndTruncatedContainerEvidence_CannotBypassProbeFailure()
    {
        using SecurityFixture fixture = new();
        byte[][] corruptContainers =
        [
            Encoding.ASCII.GetBytes("not-a-media-container"),
            [0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p'],
        ];
        ContainerSignature[] expectedSignatures =
        [
            ContainerSignature.Unknown,
            ContainerSignature.IsoBaseMediaFtyp,
        ];

        for (int index = 0; index < corruptContainers.Length; index++)
        {
            byte[] content = corruptContainers[index];
            StagedMediaImport staged = fixture.CreateStagedMedia(
                $"corrupt-{index}.ready",
                $"corrupt-{index}.mov",
                content);
            ContainerSignature signature = FfprobeClient.DetectContainerSignature(
                content,
                content.LongLength);
            string privateDiagnostic = string.Concat(
                "corrupt input ",
                staged.StagingPath);

            Assert.AreEqual(expectedSignatures[index], signature);
            MediaProbeException exception = Assert.ThrowsExactly<MediaProbeException>(() =>
                FfprobeClient.ParseProcessResult(
                    staged,
                    signature,
                    new ProcessRunResult(
                        1,
                        string.Empty,
                        privateDiagnostic,
                        Truncated: false,
                        TimeSpan.FromMilliseconds(1))));

            Assert.AreEqual(MediaProbeFailureReason.ProcessFailed, exception.Reason);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), privateDiagnostic);
            AssertDoesNotContain(exception.ToString(), staged.StagingPath);
        }
    }

    [TestMethod]
    public async Task ProbeAsync_CanonicalFfprobeRejectsSyntheticCorruptContainersWhenAvailable()
    {
        if (!OperatingSystem.IsWindows() ||
            !TryFindCanonicalFfprobe(out string ffprobePath))
        {
            Assert.Inconclusive("The approved FFprobe package is unavailable.");
            return;
        }

        using SecurityFixture fixture = new();
        FfprobeClient client = new(
            new ProcessRunner(),
            new BinaryVerifier(),
            ffprobePath);
        byte[][] corruptContainers =
        [
            Encoding.ASCII.GetBytes("synthetic-wrong-magic"),
            [0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p'],
        ];

        for (int index = 0; index < corruptContainers.Length; index++)
        {
            StagedMediaImport staged = fixture.CreateStagedMedia(
                $"canonical-corrupt-{index}.ready",
                $"canonical-corrupt-{index}.mov",
                corruptContainers[index]);

            MediaProbeException exception =
                await Assert.ThrowsExactlyAsync<MediaProbeException>(() =>
                    client.ProbeAsync(
                        staged,
                        TimeSpan.FromSeconds(30),
                        CancellationToken.None));

            Assert.IsTrue(exception.Reason is
                MediaProbeFailureReason.ProcessFailed or
                MediaProbeFailureReason.InvalidJson or
                MediaProbeFailureReason.InvalidMetadata or
                MediaProbeFailureReason.NoVideoStream);
            Assert.IsNull(exception.InnerException);
            AssertDoesNotContain(exception.ToString(), staged.StagingPath);
            AssertDoesNotContain(exception.ToString(), staged.SourceFileName);
        }
    }

    [TestMethod]
    public void HugeAndDeepProbeMetadata_IsRejectedWithoutEchoingContent()
    {
        using SecurityFixture fixture = new();
        StagedMediaImport staged = fixture.CreateStagedMedia(
            "metadata.ready",
            "metadata.mov",
            Encoding.ASCII.GetBytes("metadata-source"));
        string privateMarker = string.Concat(
            "private-metadata-marker-",
            new string('X', 256 * 1024));
        string hugeMetadata = $$"""
            {
              "streams": [
                {
                  "index": 0,
                  "codec_type": "video",
                  "codec_name": "h264",
                  "width": 16,
                  "height": 16,
                  "r_frame_rate": "1/1",
                  "avg_frame_rate": "1/1",
                  "time_base": "1/1000"
                }
              ],
              "format": { "format_name": "mov" },
              "tags": { "comment": "{{privateMarker}}" }
            }
            """;

        Assert.IsLessThan(
            ProcessRunRequest.DefaultMaxCapturedBytes,
            Encoding.UTF8.GetByteCount(hugeMetadata));
        MediaProbeException hugeException =
            Assert.ThrowsExactly<MediaProbeException>(() =>
                FfprobeClient.ParseProbeOutput(
                    staged,
                    ContainerSignature.IsoBaseMediaFtyp,
                    hugeMetadata));
        Assert.AreEqual(MediaProbeFailureReason.InvalidMetadata, hugeException.Reason);
        AssertDoesNotContain(hugeException.ToString(), "private-metadata-marker");

        string deeplyNestedJson = string.Concat(
            new string('[', 80),
            "0",
            new string(']', 80));
        MediaProbeException deepException =
            Assert.ThrowsExactly<MediaProbeException>(() =>
                FfprobeClient.ParseProbeOutput(
                    staged,
                    ContainerSignature.Unknown,
                    deeplyNestedJson));
        Assert.AreEqual(MediaProbeFailureReason.InvalidJson, deepException.Reason);
        Assert.IsNull(deepException.InnerException);
    }

    [TestMethod]
    public async Task HugeDualStreamOutput_IsBoundedAndRejectedBeforeMetadataParsing()
    {
        using SecurityFixture fixture = new();
        StagedMediaImport staged = fixture.CreateStagedMedia(
            "huge-output.ready",
            "huge-output.mov",
            Encoding.ASCII.GetBytes("huge-output-source"));
        ProcessRunRequest probeRequest = FfprobeClient.CreateProcessRequest(
            Path.Combine(fixture.RootPath, "ffprobe.exe"),
            staged.StagingPath,
            TimeSpan.FromSeconds(30));
        int emittedBytesPerStream = checked(probeRequest.MaxCapturedBytes + (64 * 1024));
        ProcessRunRequest helperRequest = CreateHelperRequest(
            "write-huge-output",
            [emittedBytesPerStream.ToString(CultureInfo.InvariantCulture)],
            timeout: TimeSpan.FromSeconds(30),
            maxCapturedBytes: probeRequest.MaxCapturedBytes);

        ProcessRunResult result = await new ProcessRunner().RunAsync(helperRequest);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(
            probeRequest.MaxCapturedBytes,
            Encoding.UTF8.GetByteCount(result.StandardOutput));
        Assert.AreEqual(
            probeRequest.MaxCapturedBytes,
            Encoding.UTF8.GetByteCount(result.StandardError));
        Assert.IsTrue(result.Truncated);

        MediaProbeException exception = Assert.ThrowsExactly<MediaProbeException>(() =>
            FfprobeClient.ParseProcessResult(
                staged,
                ContainerSignature.Unknown,
                result));
        Assert.AreEqual(MediaProbeFailureReason.OutputTruncated, exception.Reason);
        Assert.IsNull(exception.InnerException);
        AssertDoesNotContain(exception.ToString(), result.StandardOutput);
        AssertDoesNotContain(exception.ToString(), result.StandardError);
    }

    [TestMethod]
    public async Task NonterminatingTool_IsKilledAtDeadlineWithoutPathDisclosure()
    {
        using SecurityFixture fixture = new();
        string processIdPath = Path.Combine(fixture.RootPath, "resource-bomb.pid");
        ProcessRunRequest request = CreateHelperRequest(
            "wait",
            [processIdPath],
            timeout: TimeSpan.FromSeconds(2));
        Stopwatch stopwatch = Stopwatch.StartNew();

        TimeoutException exception =
            await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
                new ProcessRunner().RunAsync(request));
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.IsTrue(File.Exists(processIdPath));
        int processId = int.Parse(
            await File.ReadAllTextAsync(processIdPath),
            CultureInfo.InvariantCulture);
        await AssertProcessExitedAsync(processId);
        AssertDoesNotContain(exception.ToString(), processIdPath);
        AssertDoesNotContain(exception.ToString(), fixture.RootPath);
    }

    private static MediaProbeResult CreateProbeResult(StagedMediaImport staged) =>
        new(
            staged.CandidateId,
            staged.ProjectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            ContainerSignature.IsoBaseMediaFtyp,
            ["mov", "mp4"],
            "QuickTime / MOV",
            durationSeconds: 1m,
            declaredByteSize: staged.ByteSize,
            bitRate: 1024,
            probeScore: 100,
            [
                new ProbedMediaStream(
                    0,
                    MediaStreamKind.Video,
                    "h264",
                    "High",
                    16,
                    16,
                    "yuv420p",
                    new RationalNumber(1, 1),
                    new RationalNumber(1, 1),
                    new RationalNumber(1, 1000),
                    startSeconds: 0m,
                    durationSeconds: 1m,
                    sampleRate: null,
                    channels: null,
                    channelLayout: null),
            ],
            ["audio-track-absent"]);

    private static PtsIndex CreatePtsIndex(StagedMediaImport staged) =>
        new(
            staged.CandidateId,
            staged.ProjectId,
            staged.JobId,
            staged.SourceFileName,
            staged.ContentDigest,
            staged.ByteSize,
            streamIndex: 0,
            new RationalTime(1, 1000),
            [
                new PtsIndexEntry(
                    0,
                    new RationalTime(0, 1),
                    new RationalTime(1, 50),
                    keyFrame: true),
            ]);

    private static ProcessStartInfo CreateStartInfo(ProcessRunRequest request)
    {
        MethodInfo method = typeof(ProcessRunner).GetMethod(
            "CreateStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "The process start-info factory was not found.");
        return (ProcessStartInfo)(method.Invoke(
            obj: null,
            parameters: [request])
            ?? throw new InvalidOperationException(
                "The process runner returned no start info."));
    }

    private static ProcessRunRequest CreateHelperRequest(
        string mode,
        IReadOnlyList<string> helperArguments,
        TimeSpan timeout,
        int maxCapturedBytes = ProcessRunRequest.DefaultMaxCapturedBytes)
    {
        List<string> arguments =
        [
            "exec",
            TestAssemblyPath,
            ProcessRunnerTestHelper.Marker,
            mode,
        ];
        arguments.AddRange(helperArguments);
        return new ProcessRunRequest(
            DotNetExecutable,
            arguments,
            TestWorkingDirectory,
            timeout,
            maxCapturedBytes);
    }

    private static string ResolveDotNetExecutable()
    {
        string? dotNetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(dotNetHostPath) && File.Exists(dotNetHostPath))
        {
            return Path.GetFullPath(dotNetHostPath);
        }

        string executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim().Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException("The current dotnet host could not be located.");
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                using CancellationTokenSource cancellation =
                    new(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cancellation.Token);
            }

            Assert.IsTrue(process.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }

    private static bool TryFindCanonicalFfprobe(out string executablePath)
    {
        executablePath = string.Empty;
        try
        {
            string packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Packages");
            if (!Directory.Exists(packagesRoot))
            {
                return false;
            }

            foreach (string packageDirectory in Directory.GetDirectories(
                packagesRoot,
                string.Concat(FfprobeClient.CanonicalPackageId, "*"),
                SearchOption.TopDirectoryOnly))
            {
                foreach (string candidate in Directory.EnumerateFiles(
                    packageDirectory,
                    "ffprobe.exe",
                    SearchOption.AllDirectories))
                {
                    var fileInfo = new FileInfo(candidate);
                    if (fileInfo.Length != FfprobeClient.CanonicalFfprobeByteSize)
                    {
                        continue;
                    }

                    using FileStream stream = File.OpenRead(candidate);
                    string actualSha256 = Convert.ToHexString(
                        SHA256.HashData(stream)).ToLowerInvariant();
                    if (string.Equals(
                        actualSha256,
                        FfprobeClient.CanonicalFfprobeSha256,
                        StringComparison.Ordinal))
                    {
                        executablePath = Path.GetFullPath(candidate);
                        return true;
                    }
                }
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                CryptographicException or NotSupportedException)
        {
        }

        return false;
    }

    private static void AssertDoesNotContain(string value, string forbiddenValue)
    {
        if (forbiddenValue.Length == 0)
        {
            return;
        }

        Assert.IsFalse(value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SecurityFixture : IDisposable
    {
        internal SecurityFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.MediaImportSecurityTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            SourceDirectory = Path.Combine(RootPath, "sources");
            Directory.CreateDirectory(WorkspacePath);
            Directory.CreateDirectory(SourceDirectory);
            Registry = new AssetCandidateRegistry();
            Importer = new MediaImporter(
                Registry,
                WorkspacePath,
                new FixedTimeProvider(CreatedAt));
        }

        internal string RootPath { get; }

        internal string WorkspacePath { get; }

        internal string SourceDirectory { get; }

        internal AssetCandidateRegistry Registry { get; }

        internal MediaImporter Importer { get; }

        internal string CreateSource(string fileName, byte[] content)
        {
            string path = Path.Combine(SourceDirectory, fileName);
            File.WriteAllBytes(path, content);
            return path;
        }

        internal EntityId<AssetIdTag> Register(string sourcePath) =>
            Registry.RegisterSelection(sourcePath)
            ?? throw new AssertFailedException("A source path must create a candidate.");

        internal StagedMediaImport CreateStagedMedia(
            string stagingFileName,
            string sourceFileName,
            byte[] content)
        {
            string stagingPath = Path.Combine(RootPath, stagingFileName);
            File.WriteAllBytes(stagingPath, content);
            return new StagedMediaImport(
                EntityId<AssetIdTag>.New(),
                EntityId<ProjectIdTag>.New(),
                EntityId<JobIdTag>.New(),
                sourceFileName,
                stagingPath,
                ContentDigest.Compute(content),
                content.LongLength,
                CreatedAt);
        }

        internal string GetStagingDirectory(
            EntityId<ProjectIdTag> projectId,
            EntityId<JobIdTag> jobId) =>
            Path.Combine(
                WorkspacePath,
                "projects",
                projectId.ToString(),
                "staging",
                "imports",
                jobId.ToString());

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}