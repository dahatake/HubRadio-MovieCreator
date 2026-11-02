using System.Text.Json;
using HubRadio.App.Analysis;
using HubRadio.App.Projects;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-010 AC-002 / FR-132 AC-136,AC-137: the composition stage fed with the REAL ASR worker's tokens
/// (leading spaces, trailing full stops, word timings that are not on the frame grid), not synthetic ones.
/// tests/HubRadio.App.Tests/Fixtures/real-asr-speech-27s.json is the unmodified worker output for the
/// 7-sentence English System.Speech source of tests/system/e2e (27.9 s, 48 kHz AAC); regenerate it with
/// Fixtures/make-real-asr-fixture.ps1 (opt-in live test below: HUBRADIO_RUN_LIVE_ASR_WORKER_TESTS=1).
/// </summary>
public sealed partial class SourceOnlyPipelineTests
{
    internal sealed record RealAsrInput(AnalysisAudioTimeline Timeline, AsrWorkerResult Worker, int Frames, bool CutPauses = true);

    internal static RealAsrInput LoadRealAsr(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement result = document.RootElement.GetProperty("result");
        string requestId = document.RootElement.GetProperty("requestId").GetString()!;
        var digest = new ContentDigest(result.GetProperty("input_audio_sha256").GetString()!);
        long size = result.GetProperty("input_audio_byte_size").GetInt64();
        double seconds = result.GetProperty("input_audio_duration_seconds").GetDouble();
        long samples = (size - 44) / 2;
        var timeline = new AnalysisAudioTimeline(EntityId<ProjectIdTag>.New(), EntityId<JobIdTag>.New(),
            EntityId<AssetIdTag>.New(), ContentDigest.Compute([1, 2, 3]), 100, EntityId<ArtifactIdTag>.New(),
            ContentDigest.Compute([4, 5, 6]), 0, 1, new RationalTime(1, 48000), default(RationalTime),
            0, 1024, EntityId<ArtifactIdTag>.New(), digest, size, samples, EntityId<ArtifactIdTag>.New());
        LocalAsrResult parsed = LocalAnalysisWorker.ParseResult(bytes, ContentDigest.Compute(bytes), path, requestId,
            LocalAnalysisWorker.AutoLanguagePolicy, timeline);
        return new RealAsrInput(timeline, parsed.WorkerResult, (int)Math.Ceiling(seconds * 30));
    }

    private static string RealAsrFixturePath() => RepositoryPath("tests/HubRadio.App.Tests/Fixtures/real-asr-speech-27s.json");

    private static async Task AssertRealAsrCompositionAsync(string asrJsonPath)
    {
        RealAsrInput real = LoadRealAsr(asrJsonPath);
        await using RuntimeCheckpointFixture f = await RuntimeCheckpointFixture.CreateAsync(real: real);
        EmphasisSuggestionCandidate[] speech = f.Analysis.EmphasisCandidates
            .Where(item => item.Payload.Reasons.Contains(EmphasisReasonKind.SpeechSegment)).ToArray();
        Assert.IsTrue(speech.Length > 0, "real ASR: no speech-segment candidate");
        Assert.IsTrue(f.RealCandidates.Length > 0, "real ASR: no representable (frame/sample exact) short candidate");
        foreach (EmphasisSuggestionCandidate item in f.RealCandidates)
        {
            double seconds = (double)item.Payload.SourceSpan.Range.Duration.Value / item.Payload.SourceSpan.Range.Duration.Rate;
            Assert.IsTrue(seconds is >= 15 and <= 60, "short length " + seconds);
        }
        // FR-010 AC-002: a short plan reaches the composition.
        Assert.IsTrue(f.Preparation.Plan.ShortForms.Count > 0, "real ASR: the standard plan adopted no short form");
        // FR-132: the generated-runtime checkpoint (subtitles of the long form and of every short) is saved.
        SourceOnlyGeneratedRuntimeEvidence produced = f.BuildGenerated();
        // FR-010 AC-002: a short with no subtitle cue is omitted from the render (NoDisplayCues) - the real tokens must give every short cues.
        Assert.IsTrue(produced.ShortForms.Count > 0 && produced.ShortForms.All(item => item.Subtitles.Revisions.Count > 0),
            "real ASR: shorts without display cues: " + string.Join(",", produced.ShortForms.Select(item => item.Subtitles.Revisions.Count)) +
            " warnings=" + string.Join("|", produced.ShortForms.SelectMany(item => item.Subtitles.Warnings).Select(item => item.Reason).Distinct()));
        await f.Pipeline.CheckpointGeneratedRuntimeAsync(f.Execution, produced, CancellationToken.None);
    }

    // FR-010 AC-002, FR-132 AC-136, AC-137: real worker tokens (fixed fixture) through candidates → plan → artifact store.
    [TestMethod]
    public Task FR010_AC002_RealAsrTokens_ProduceShortFormPlanAndStoreCompositionEvidence() =>
        AssertRealAsrCompositionAsync(RealAsrFixturePath());

    // Opt-in: HUBRADIO_RUN_LIVE_ASR_WORKER_TESTS=1 and HUBRADIO_LIVE_ASR_JSON=<worker output made by Fixtures/make-real-asr-fixture.ps1>.
    [TestMethod]
    [TestCategory("LivePipeline")]
    public Task FR010_AC002_LiveWorkerOutput_ProducesShortFormPlanAndStoresCompositionEvidence()
    {
        string? path = Environment.GetEnvironmentVariable("HUBRADIO_LIVE_ASR_JSON");
        if (Environment.GetEnvironmentVariable("HUBRADIO_RUN_LIVE_ASR_WORKER_TESTS") != "1" || string.IsNullOrWhiteSpace(path))
        {
            Assert.Inconclusive("live_asr.opt_in_required.HUBRADIO_RUN_LIVE_ASR_WORKER_TESTS");
        }
        return AssertRealAsrCompositionAsync(path!);
    }
}
