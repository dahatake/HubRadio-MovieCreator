using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.App.Analysis;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class LocalAnalysisWorkerTests
{
    private const string RequestId = "7e790ba0-3ac6-49a9-bcbd-aac438de61de";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    [TestMethod]
    [DataRow("asr.processing_failed", "asr.processing_failed")]
    [DataRow("task.invalid_result", "task.invalid_result")]
    [DataRow("task.timeout", "task.timeout")]
    [DataRow("task.input_mismatch", "task.input_mismatch")]
    [DataRow("private C:\\user\\input.wav", "source_only.execution_failed")]
    [DataRow("asr.unknown", "source_only.execution_failed")]
    public void PipelineFailureCodesPreserveOnlyClosedContentPrivateReasons(string code, string expected)
    {
        Assert.AreEqual(expected, LocalAnalysisWorker.GetSafeFailureCode(new LocalAnalysisWorkerException(code)));
    }

    [TestMethod]
    public async Task RepetitionUsesActualTokensAndVerifiesClosedWorkerReceiptWithoutGrantingDeletion()
    {
        using var fixture = new Fixture();
        TranscriptRevision transcript = RepeatedTranscript();
        var modelId = EntityId<ModelRunIdTag>.New();
        ContentDigest digest = ContentDigest.Compute(Utf8.GetBytes(HubRadio.App.Transcripts.TranscriptSnapshotJson.WriteRevision(transcript)));
        ProcessRunRequest? captured = null;
        var worker = fixture.Worker((request, token) =>
        {
            captured = request;
            token.ThrowIfCancellationRequested();
            return Task.FromResult(PublishRepetition(request, transcript, modelId));
        });
        LocalRepetitionResult actual = await worker.FindRepetitionsAsync(transcript, digest, modelId, fixture.Output, TimeSpan.FromSeconds(10));
        Assert.IsNotNull(captured);
        CollectionAssert.AreEqual(new[] { "-I", "-B", "-m", "hubradio_worker", "--task" }, captured.ArgumentList.ToArray());
        using JsonDocument requestJson = JsonDocument.Parse(captured.StandardInput!);
        JsonElement input = requestJson.RootElement.GetProperty("params").GetProperty("transcript");
        Assert.AreEqual(transcript.Id.ToString(), input.GetProperty("revision_id").GetString());
        Assert.AreEqual(digest.Hex, input.GetProperty("revision_digest").GetString());
        CollectionAssert.AreEqual(transcript.Utterances.Single().Tokens.Select(item => item.Text).ToArray(),
            input.GetProperty("utterances")[0].GetProperty("sentences")[0].GetProperty("tokens").EnumerateArray()
                .Select(item => item.GetProperty("text").GetString()).ToArray());
        RepetitionSuggestionCandidate candidate = actual.Candidates.Single();
        Assert.AreEqual(transcript.Id, candidate.Payload.SourceRevisionId);
        Assert.AreEqual(modelId, candidate.Payload.ModelRunId);
        Assert.IsFalse(candidate.Payload.AutomaticDeletionAllowed);
        Assert.IsTrue(candidate.Payload.HumanReviewRequired);
        CollectionAssert.AreEqual(transcript.Utterances.Single().Tokens.Skip(2).Select(item => item.Id).ToArray(),
            candidate.Payload.EvidenceTokenIds.ToArray());
        CollectionAssert.AreEqual(File.ReadAllBytes(actual.ArtifactPath), actual.RawResultBytes.ToArray());
        Assert.AreEqual(actual.Digest, ContentDigest.Compute(actual.RawResultBytes.Span));
        string requestId = requestJson.RootElement.GetProperty("requestId").GetString()!;
        Assert.AreEqual(candidate.Id, LocalAnalysisWorker.ParseRepetitionResult(actual.RawResultBytes.ToArray(), actual.Digest,
            actual.ArtifactPath, requestId, transcript, modelId).Candidates.Single().Id);
    }

    [TestMethod]
    public async Task RepetitionRefusesForeignDigestOversizeAndCancellationBeforeInvokingProcess()
    {
        using var fixture = new Fixture();
        int calls = 0;
        var worker = fixture.Worker((_, _) => { calls++; throw new AssertFailedException("No process should run."); });
        TranscriptRevision transcript = RepeatedTranscript();
        var modelId = EntityId<ModelRunIdTag>.New();
        ContentDigest DigestOf(TranscriptRevision value) => ContentDigest.Compute(Utf8.GetBytes(HubRadio.App.Transcripts.TranscriptSnapshotJson.WriteRevision(value)));
        Assert.AreEqual("task.input_mismatch", (await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
            worker.FindRepetitionsAsync(transcript, ContentDigest.Compute([8]), modelId, fixture.Output, TimeSpan.FromSeconds(10)))).Code);
        TranscriptRevision large = RepeatedTranscript(new string('x', LocalAnalysisWorker.MaxRequestBytes));
        Assert.AreEqual("repetition.input_too_large", (await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
            worker.FindRepetitionsAsync(large, DigestOf(large), modelId, fixture.Output, TimeSpan.FromSeconds(10)))).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => worker.FindRepetitionsAsync(transcript,
            DigestOf(transcript), modelId, fixture.Output, TimeSpan.FromSeconds(10), cancellation.Token));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void RepetitionRejectsTamperedCandidateKeysForeignTokensAndInventedApproval()
    {
        TranscriptRevision transcript = RepeatedTranscript();
        var modelId = EntityId<ModelRunIdTag>.New();
        Action<JsonObject>[] defects =
        [
            root => root["method"] = "transcribe",
            root => root["result"]!["suggestions"]![0]!["candidate_key"] = new string('0', 64),
            root => root["result"]!["suggestions"]![0]!["source_revision_id"] = EntityId<TranscriptRevisionIdTag>.New().ToString(),
            root => root["result"]!["suggestions"]![0]!["source_revision_digest"] = new string('0', 64),
            root => root["result"]!["suggestions"]![0]!["model_run_id"] = EntityId<ModelRunIdTag>.New().ToString(),
            root => root["result"]!["suggestions"]![0]!["automatic_deletion_allowed"] = true,
            root => root["result"]!["suggestions"]![0]!["evidence_token_ids"]![0] = EntityId<TranscriptTokenIdTag>.New().ToString(),
            root => root["result"]!["suggestions"]![0]!["features"]!["unrecognized"] = true,
        ];
        foreach (var defect in defects)
        {
            JsonObject envelope = RepetitionEnvelope(RequestId, transcript, modelId);
            defect(envelope);
            byte[] bytes = Utf8.GetBytes(envelope.ToJsonString());
            LocalAnalysisWorkerException failure = Assert.ThrowsExactly<LocalAnalysisWorkerException>(() =>
                LocalAnalysisWorker.ParseRepetitionResult(bytes, ContentDigest.Compute(bytes), "unused", RequestId, transcript, modelId));
            Assert.AreEqual("task.invalid_result", failure.Code);
            Assert.IsNull(failure.InnerException);
        }
    }

    private static TranscriptRevision RepeatedTranscript(string first = "実測")
    {
        Token[] tokens = new[] { first, "確認", first, "確認" }.Select((text, i) =>
            new Token(EntityId<TranscriptTokenIdTag>.New(), text, new TimeRange(new RationalTime(i, 1), new RationalTime(1, 1)), 0.9)).ToArray();
        string text = string.Concat(tokens.Select(item => item.Text));
        return new TranscriptRevision(EntityId<TranscriptRevisionIdTag>.New(), null, TranscriptChangeSource.Machine,
            DateTimeOffset.UtcNow, [new Utterance(EntityId<TranscriptUtteranceIdTag>.New(), EntityId<SourceSpanIdTag>.New(),
                EntityId<TranscriptSpeakerIdTag>.New(), "Speaker 1", text, new TimeRange(default, new RationalTime(4, 1)), null,
                [new Sentence(EntityId<TranscriptSentenceIdTag>.New(), text, tokens)])]);
    }

    private static JsonObject RepetitionEnvelope(string requestId, TranscriptRevision transcript, EntityId<ModelRunIdTag> modelId)
    {
        Utterance utterance = transcript.Utterances.Single();
        EntityId<TranscriptTokenIdTag>[] earlier = utterance.Tokens.Take(2).Select(item => item.Id).ToArray();
        EntityId<TranscriptTokenIdTag>[] later = utterance.Tokens.Skip(2).Select(item => item.Id).ToArray();
        ContentDigest digest = ContentDigest.Compute(Utf8.GetBytes(HubRadio.App.Transcripts.TranscriptSnapshotJson.WriteRevision(transcript)));
        const string rule = RepetitionReasonFeatures.AdjacentExactTokenSequenceRule;
        ContentDigest key = RepetitionSuggestionPayload.ComputeCandidateKey(transcript.Id, digest, rule,
            utterance.SourceSpanId, later, utterance.SourceSpanId, earlier);
        return JsonSerializer.SerializeToNode(new
        {
            version = 1,
            requestId,
            method = "repetition",
            result = new
            {
                suggestions = new[] { new
                {
                    candidate_key = key.Hex, model_run_id = modelId.ToString(), source_revision_id = transcript.Id.ToString(),
                    source_revision_digest = digest.Hex, source_span_id = utterance.SourceSpanId.ToString(), evidence_token_ids = later.Select(item => item.ToString()).ToArray(),
                    rule_version = RepetitionSuggestionPayload.CanonicalRuleVersion, kind = "deleteRange", status = "proposed",
                    reason = "Adjacent token blocks have the same exact normalized sequence.", confidence = (double?)null,
                    automatic_deletion_allowed = false, human_review_required = true, features = new
                    {
                        rule, comparison_source_span_id = utterance.SourceSpanId.ToString(), comparison_token_ids = earlier.Select(item => item.ToString()).ToArray(),
                        matching_token_count = 2, exact_nfkc_match = true, structurally_adjacent = true, same_speaker_required = false,
                        candidate_is_later_occurrence = true, retention_choice_required = true,
                    },
                } },
            },
        })!.AsObject();
    }

    private static ProcessRunResult PublishRepetition(ProcessRunRequest request, TranscriptRevision transcript, EntityId<ModelRunIdTag> modelId)
    {
        using JsonDocument input = JsonDocument.Parse(request.StandardInput!);
        string id = input.RootElement.GetProperty("requestId").GetString()!;
        byte[] bytes = Utf8.GetBytes(RepetitionEnvelope(id, transcript, modelId).ToJsonString());
        string output = input.RootElement.GetProperty("outputRoot").GetString()!;
        using (var file = new FileStream(Path.Combine(output, id + ".json"), FileMode.CreateNew)) { file.Write(bytes); }
        return new ProcessRunResult(0, JsonSerializer.Serialize(new
        {
            version = 1,
            status = "ok",
            requestId = id,
            method = "repetition",
            sha256 = ContentDigest.Compute(bytes).Hex,
            byteSize = bytes.Length,
        }) + "\n", "", false, TimeSpan.Zero);
    }

    [TestMethod]
    public async Task TranscribeAsync_UsesFixedArgumentsAndMapsActualEvidenceWithoutRounding()
    {
        using var fixture = new Fixture();
        ProcessRunRequest? captured = null;
        var worker = fixture.Worker((request, token) =>
        {
            captured = request;
            token.ThrowIfCancellationRequested();
            return Task.FromResult(fixture.Publish(request));
        });

        LocalAsrResult result = await worker.TranscribeAsync(
            fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline, TimeSpan.FromSeconds(20));

        Assert.IsNotNull(captured);
        Assert.AreEqual(fixture.Python, captured.Executable);
        CollectionAssert.AreEqual(new[] { "-I", "-B", "-m", "hubradio_worker", "--task" }, captured.ArgumentList.ToArray());
        Assert.AreEqual(fixture.Output, captured.WorkingDirectory);
        Assert.AreEqual(TimeSpan.FromSeconds(20), captured.Timeout);
        Assert.AreEqual(LocalAnalysisWorker.MaxRequestBytes, captured.MaxCapturedBytes);
        Assert.IsNotNull(captured.StandardInput);
        using JsonDocument document = JsonDocument.Parse(captured.StandardInput);
        JsonElement requestRoot = document.RootElement;
        Assert.AreEqual(6, requestRoot.EnumerateObject().Count());
        JsonElement parameters = requestRoot.GetProperty("params");
        Assert.AreEqual(5, parameters.EnumerateObject().Count());
        Assert.AreEqual(LocalAnalysisWorker.AutoLanguagePolicy, parameters.GetProperty("languagePolicy").GetString());
        Assert.AreEqual(fixture.Timeline.AnalysisAudioDigest.Hex, parameters.GetProperty("inputAudioSha256").GetString());
        Assert.AreEqual(fixture.Timeline.AnalysisAudioByteSize, parameters.GetProperty("inputAudioByteSize").GetInt64());
        Assert.AreEqual("en", result.WorkerResult.Language);
        Assert.AreEqual(LocalAnalysisWorker.AutoLanguagePolicy, result.WorkerResult.Configuration.Language);
        Assert.AreEqual(0.73, result.WorkerResult.LanguageConfidence);
        Assert.AreEqual(7, result.WorkerResult.Segments[0].SegmentId);
        Assert.AreEqual(19, result.WorkerResult.Segments[0].Seek);
        Assert.AreEqual(" private 音声 ", result.WorkerResult.Segments[0].Text);
        Assert.AreEqual(-0.25, result.WorkerResult.Segments[0].AverageLogProbability);
        Assert.AreEqual(0.01, result.WorkerResult.Segments[0].NoSpeechProbability);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(0.10000000000000002),
            BitConverter.DoubleToInt64Bits(result.WorkerResult.Segments[0].Tokens[0].StartSeconds!.Value));
        Assert.AreEqual(0.81, result.WorkerResult.Segments[0].Tokens[0].RecognitionConfidence);
        Assert.IsNull(result.WorkerResult.Segments[0].Tokens[1].StartSeconds);
        Assert.IsTrue(result.WorkerResult.Segments[0].Tokens[1].TimingRequiresReview);
        Assert.AreEqual("en", result.LanguageDetection.DetectedLanguage);
        Assert.AreEqual(0.73, result.LanguageDetection.DetectedLanguageConfidence);
        Assert.IsNull(result.LanguageDetection.FallbackReason);
        byte[] raw = result.RawResultBytes.ToArray();
        Assert.AreEqual(result.Digest, ContentDigest.Compute(raw));
        Assert.AreEqual(result.ByteSize, raw.LongLength);
        CollectionAssert.AreEqual(File.ReadAllBytes(result.ArtifactPath), raw);
        raw[0] = 0;
        Assert.AreEqual(result.Digest, ContentDigest.Compute(result.RawResultBytes.Span));
    }

    [TestMethod]
    public async Task TranscribeAsync_ExplicitJapaneseRemainsPinnedAndSilenceIsEmpty()
    {
        using var fixture = new Fixture();
        var worker = fixture.Worker((request, _) => Task.FromResult(fixture.Publish(request, envelope =>
        {
            envelope["result"]!["segments"] = new JsonArray();
        })));
        LocalAsrResult result = await worker.TranscribeAsync(
            fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline, TimeSpan.FromSeconds(10),
            languagePolicy: LocalAsrLanguagePolicy.Japanese);
        Assert.AreEqual("ja", result.WorkerResult.Language);
        Assert.AreEqual("ja", result.WorkerResult.Configuration.Language);
        Assert.IsNull(result.LanguageDetection.DetectedLanguage);
        Assert.IsNull(result.LanguageDetection.FallbackReason);
        Assert.HasCount(0, result.WorkerResult.Segments);
    }

    [TestMethod]
    public async Task TranscribeAsync_RejectsAudioDriftBeforeInvokingProcess()
    {
        using var fixture = new Fixture();
        bool called = false;
        var worker = fixture.Worker((_, _) =>
        {
            called = true;
            throw new InvalidOperationException("must not invoke");
        });
        byte[] changed = File.ReadAllBytes(fixture.Audio);
        changed[^1] ^= 1;
        File.WriteAllBytes(fixture.Audio, changed);
        LocalAnalysisWorkerException error = await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
            worker.TranscribeAsync(fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline, TimeSpan.FromSeconds(10)));
        Assert.AreEqual("task.input_mismatch", error.Code);
        Assert.IsFalse(called);
    }

    [TestMethod]
    public async Task TranscribeAsync_RefusesNonManagedAndNonCanonicalDestinations()
    {
        using var fixture = new Fixture();
        int calls = 0;
        var worker = fixture.Worker((_, _) =>
        {
            calls++;
            throw new InvalidOperationException();
        });
        foreach (string directory in new[] { fixture.Root, fixture.Model, "relative", Path.Combine(fixture.Output, "..") })
        {
            LocalAnalysisWorkerException error = await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
                worker.TranscribeAsync(fixture.Audio, fixture.Model, directory, fixture.Timeline, TimeSpan.FromSeconds(10)));
            Assert.AreEqual("task.invalid_path", error.Code);
        }

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task TranscribeAsync_CancellationAndTimeoutRemainPrivate()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var worker = fixture.Worker((_, token) =>
        {
            Assert.AreEqual(cancellation.Token, token);
            cancellation.Cancel();
            throw new OperationCanceledException("private path", new IOException("private detail"), token);
        });
        OperationCanceledException cancelled = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            worker.TranscribeAsync(fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline,
                TimeSpan.FromSeconds(10), cancellation.Token));
        Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);
        Assert.IsNull(cancelled.InnerException);
        Assert.IsFalse(cancelled.ToString().Contains("private", StringComparison.Ordinal));

        var timedOut = fixture.Worker((_, _) => throw new TimeoutException("private path"));
        LocalAnalysisWorkerException timeout = await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
            timedOut.TranscribeAsync(fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline, TimeSpan.FromSeconds(1)));
        Assert.AreEqual("task.timeout", timeout.Code);
        Assert.IsNull(timeout.InnerException);
    }

    [TestMethod]
    public async Task TranscribeAsync_PreCancelledNeverInvokesAndOutputHashMustMatch()
    {
        using var fixture = new Fixture();
        int calls = 0;
        var worker = fixture.Worker((request, _) =>
        {
            calls++;
            ProcessRunResult response = fixture.Publish(request);
            JsonObject metadata = JsonNode.Parse(response.StandardOutput)!.AsObject();
            metadata["sha256"] = new string('0', 64);
            return Task.FromResult(response with { StandardOutput = metadata.ToJsonString() + "\n" });
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            worker.TranscribeAsync(fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline,
                TimeSpan.FromSeconds(10), cancelled.Token));
        Assert.AreEqual(0, calls);
        LocalAnalysisWorkerException error = await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
            worker.TranscribeAsync(fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline, TimeSpan.FromSeconds(10)));
        Assert.AreEqual("task.invalid_result", error.Code);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void ParseMetadata_RejectsNonClosedOutputAndAcceptsOnlyStableErrors()
    {
        string valid = Metadata(RequestId, new string('a', 64), 100);
        string[] bad =
        [
            valid.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            valid.Replace("\"version\":1", "\"version\":true", StringComparison.Ordinal),
            valid.Replace("\"status\":\"ok\"", "\"status\":\"ok\",\"path\":\"private\"", StringComparison.Ordinal),
            valid.Replace(new string('a', 64), new string('A', 64), StringComparison.Ordinal),
            valid.Replace("\"byteSize\":100", "\"byteSize\":16777217", StringComparison.Ordinal),
            valid.Replace("\"byteSize\":100", "\"byteSize\":1.2", StringComparison.Ordinal),
            valid + "{}", valid + "\n", "private log\n" + valid, valid.Replace("transcribe", "shell", StringComparison.Ordinal),
            valid.Replace(RequestId, Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
            valid.Replace("ok", "o\ufffdk", StringComparison.Ordinal), valid.TrimEnd(),
        ];
        foreach (string output in bad)
        {
            Assert.ThrowsExactly<LocalAnalysisWorkerException>(() =>
                LocalAnalysisWorker.ParseMetadata(new ProcessRunResult(0, output, "", false, TimeSpan.Zero), RequestId));
        }

        foreach (ProcessRunResult process in new[]
        {
            new ProcessRunResult(0, valid, "private stderr", false, TimeSpan.Zero),
            new ProcessRunResult(0, valid, "", true, TimeSpan.Zero),
            new ProcessRunResult(1, valid, "", false, TimeSpan.Zero),
        })
        {
            Assert.ThrowsExactly<LocalAnalysisWorkerException>(() => LocalAnalysisWorker.ParseMetadata(process, RequestId));
        }

        LocalAnalysisWorkerException known = Assert.ThrowsExactly<LocalAnalysisWorkerException>(() =>
            LocalAnalysisWorker.ParseMetadata(new ProcessRunResult(1,
                "{\"version\":1,\"status\":\"error\",\"code\":\"asr.invalid_model\"}\n", "", false, TimeSpan.Zero), RequestId));
        Assert.AreEqual("asr.invalid_model", known.Code);
        Assert.IsNull(known.InnerException);
        LocalAnalysisWorkerException unknown = Assert.ThrowsExactly<LocalAnalysisWorkerException>(() =>
            LocalAnalysisWorker.ParseMetadata(new ProcessRunResult(1,
                "{\"version\":1,\"status\":\"error\",\"code\":\"private data\"}\n", "", false, TimeSpan.Zero), RequestId));
        Assert.AreEqual("task.invalid_result", unknown.Code);
        Assert.IsFalse(unknown.ToString().Contains("private data", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseResult_RejectsUnknownFieldsMalformedTypesAndUnapprovedConfiguration()
    {
        using var fixture = new Fixture();
        Action<JsonObject>[] mutations =
        [
            root => root["unknown"] = 1,
            root => root["result"]!["unknown"] = 1,
            root => root["result"]!["configuration"]!["unknown"] = 1,
            root => root["result"]!["configuration"]!["beam_size"] = true,
            root => root["result"]!["configuration"]!["device"] = "cuda",
            root => root["result"]!["configuration"]!["language"] = "ja",
            root => root["result"]!["model_revision"] = new string('0', 40),
            root => root["result"]!["model_manifest_sha256"] = new string('A', 64),
            root => root["result"]!["language_confidence"] = 1.01,
            root => root["result"]!["language"] = "unknown",
            root => root["result"]!["language_detection"]!["detected_language"] = "ja",
            root => root["result"]!["language_detection"]!["fallback_reason"] = "error",
            root => root["result"]!["input_audio_sha256"] = new string('0', 64),
            root => root["result"]!["input_audio_byte_size"] = 1,
            root => root["result"]!["input_audio_duration_seconds"] = 2,
            root => root["result"]!["engine_duration_seconds"] = -1,
            root => root["result"]!["segments"]![0]!["unknown"] = 1,
            root => root["result"]!["segments"]![0]!["segment_id"] = 0.5,
            root => root["result"]!["segments"]![0]!["seek"] = -1,
            root => root["result"]!["segments"]![0]!["end_seconds"] = 2,
            root => root["result"]!["segments"]![0]!["tokens"]![0]!["unknown"] = 1,
            root => root["result"]!["segments"]![0]!["tokens"]![0]!["recognition_confidence"] = -0.1,
            root => root["result"]!["segments"]![0]!["tokens"]![0]!["end_seconds"] = null,
            root => root["result"]!["segments"]![0]!["tokens"]![1]!["timing_requires_review"] = false,
            root => root["result"]!["segments"]![0]!["tokens"]![0]!["text"] = new string('x', 65537),
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject root = fixture.Envelope(RequestId, LocalAnalysisWorker.AutoLanguagePolicy);
            mutate(root);
            byte[] bytes = Utf8.GetBytes(root.ToJsonString());
            LocalAnalysisWorkerException error = Assert.ThrowsExactly<LocalAnalysisWorkerException>(() => fixture.Parse(bytes));
            Assert.IsNull(error.InnerException);
            Assert.IsFalse(error.ToString().Contains("private 音声", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ParseResult_RejectsDuplicateKeysNonfiniteUtf8BomAndRawDigestMismatch()
    {
        using var fixture = new Fixture();
        string text = fixture.Envelope(RequestId, LocalAnalysisWorker.AutoLanguagePolicy).ToJsonString();
        string[] malformed =
        [
            text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            text.Replace("\"device\":\"cpu\"", "\"device\":\"cpu\",\"device\":\"cpu\"", StringComparison.Ordinal),
            text.Replace("\"engine_duration_seconds\":1", "\"engine_duration_seconds\":1e9999", StringComparison.Ordinal),
            text.Replace("\"engine_duration_seconds\":1", "\"engine_duration_seconds\":NaN", StringComparison.Ordinal),
            text + "{}",
        ];
        foreach (string candidate in malformed)
        {
            Assert.AreNotEqual(text, candidate);
            Assert.ThrowsExactly<LocalAnalysisWorkerException>(() => fixture.Parse(Utf8.GetBytes(candidate)));
        }

        foreach (byte[] bytes in new[] { new byte[] { 0xff }, new byte[] { 0xef, 0xbb, 0xbf }.Concat(Utf8.GetBytes(text)).ToArray() })
        {
            Assert.ThrowsExactly<LocalAnalysisWorkerException>(() => fixture.Parse(bytes));
        }

        byte[] valid = Utf8.GetBytes(text);
        Assert.ThrowsExactly<LocalAnalysisWorkerException>(() => LocalAnalysisWorker.ParseResult(
            valid, new ContentDigest(new string('0', 64)), "unused", RequestId,
            LocalAnalysisWorker.AutoLanguagePolicy, fixture.Timeline));
    }

    [TestMethod]
    public void ParseResult_PreservesFallbackEvidenceAndImporterAllowsOnlySupportedLanguageBranch()
    {
        using var fixture = new Fixture();
        JsonObject root = fixture.Envelope(RequestId, LocalAnalysisWorker.AutoLanguagePolicy);
        root["result"]!["segments"] = new JsonArray();
        LocalAsrResult automatic = fixture.Parse(Utf8.GetBytes(root.ToJsonString()));
        TranscriptImportResult imported = TranscriptImporter.Import(automatic.WorkerResult, fixture.Timeline,
            EntityId<TranscriptRevisionIdTag>.New(), EntityId<TranscriptSpeakerIdTag>.New(), DateTimeOffset.UtcNow);
        Assert.HasCount(0, imported.Revision.Utterances);
        foreach (string reason in new[] { "absent", "error" })
        {
            root["result"]!["language"] = "ja";
            root["result"]!["language_confidence"] = 1.0;
            root["result"]!["language_detection"]!["detected_language"] = null;
            root["result"]!["language_detection"]!["detected_language_confidence"] = null;
            root["result"]!["language_detection"]!["fallback_reason"] = reason;
            LocalAsrResult fallback = fixture.Parse(Utf8.GetBytes(root.ToJsonString()));
            Assert.AreEqual(reason, fallback.LanguageDetection.FallbackReason);
            Assert.IsNull(fallback.LanguageDetection.DetectedLanguage);
            Assert.AreEqual("ja", fallback.WorkerResult.Language);
            Assert.AreEqual(LocalAnalysisWorker.AutoLanguagePolicy, fallback.WorkerResult.Configuration.Language);
        }
    }

    [TestMethod]
    [DataRow(FileAttributes.Directory | FileAttributes.ReparsePoint)]
    [DataRow(FileAttributes.ReparsePoint)]
    [DataRow(FileAttributes.Directory | FileAttributes.Device)]
    [DataRow(FileAttributes.Normal)]
    public void RequirePath_DeniedAncestorPreventsEveryDescendantProbe(FileAttributes deniedAttributes)
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string denied = Path.Combine(root, "private-ancestor");
        string leaf = Path.Combine(denied, "unprobed", "python.exe");
        var probes = new List<string>();
        LocalAnalysisWorkerException error = Assert.ThrowsExactly<LocalAnalysisWorkerException>(() =>
            LocalAnalysisWorker.RequirePath(leaf, directory: false, getAttributes: entry =>
            {
                probes.Add(entry);
                if (entry == root) { return FileAttributes.Directory; }
                if (entry == denied) { return deniedAttributes; }
                throw new IOException("private descendant must not be probed");
            }));

        Assert.AreEqual("task.invalid_path", error.Code);
        CollectionAssert.AreEqual(new[] { root, denied }, probes.ToArray());
        Assert.IsNull(error.InnerException);
        Assert.IsFalse(error.ToString().Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RequirePath_ProbesRootToLeafAndChecksLeafKind(bool directory)
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string parent = Path.Combine(root, "path-order");
        string leaf = Path.Combine(parent, "leaf");
        foreach (bool correctKind in new[] { true, false })
        {
            var probes = new List<string>();
            FileAttributes Probe(string entry)
            {
                probes.Add(entry);
                return entry != leaf || (correctKind ? directory : !directory)
                    ? FileAttributes.Directory : FileAttributes.Normal;
            }

            if (correctKind)
            {
                Assert.AreEqual(leaf, LocalAnalysisWorker.RequirePath(leaf, directory, Probe));
            }
            else
            {
                LocalAnalysisWorkerException error = Assert.ThrowsExactly<LocalAnalysisWorkerException>(() =>
                    LocalAnalysisWorker.RequirePath(leaf, directory, Probe));
                Assert.AreEqual("task.invalid_path", error.Code);
            }

            CollectionAssert.AreEqual(new[] { root, parent, leaf }, probes.ToArray());
        }
    }

    [TestMethod]
    public async Task TranscribeAsync_RejectsSymlinkDirectoryBeforeStartingProcess()
    {
        using var fixture = new Fixture();
        string link = Path.Combine(fixture.Root, "staging", "link");
        try
        {
            Directory.CreateSymbolicLink(link, fixture.Output);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic link creation requires platform permission.");
            return;
        }

        try
        {
            int calls = 0;
            var worker = fixture.Worker((_, _) =>
            {
                calls++;
                throw new InvalidOperationException();
            });
            LocalAnalysisWorkerException error = await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
                worker.TranscribeAsync(fixture.Audio, fixture.Model, link, fixture.Timeline, TimeSpan.FromSeconds(10)));
            Assert.AreEqual("task.invalid_path", error.Code);
            Assert.AreEqual(0, calls);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task TranscribeAsync_RejectsMissingAndWrongSizeOutputWithoutLeakingProcessErrors()
    {
        using var fixture = new Fixture();
        foreach (bool missing in new[] { true, false })
        {
            var worker = fixture.Worker((request, _) =>
            {
                using JsonDocument document = JsonDocument.Parse(request.StandardInput!);
                string id = document.RootElement.GetProperty("requestId").GetString()!;
                if (!missing)
                {
                    File.WriteAllBytes(Path.Combine(fixture.Output, id + ".json"), [1]);
                }

                return Task.FromResult(new ProcessRunResult(0, Metadata(id, new string('a', 64), 100), "", false, TimeSpan.Zero));
            });
            LocalAnalysisWorkerException error = await Assert.ThrowsExactlyAsync<LocalAnalysisWorkerException>(() =>
                worker.TranscribeAsync(fixture.Audio, fixture.Model, fixture.Output, fixture.Timeline, TimeSpan.FromSeconds(10)));
            Assert.IsNull(error.InnerException);
            Assert.IsFalse(error.ToString().Contains(fixture.Root, StringComparison.Ordinal));
        }
    }

    private static string Metadata(string requestId, string digest, int size) => JsonSerializer.Serialize(new
    {
        version = 1,
        status = "ok",
        requestId,
        method = "transcribe",
        sha256 = digest,
        byteSize = size,
    }) + "\n";

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "hubradio-local-worker-" + Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "staging", "attempt");
            Model = Path.Combine(Root, "models");
            Python = Path.Combine(Root, "python.exe");
            Audio = Path.Combine(Root, "audio.wav");
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Model);
            // Never executed. Production uses ProcessRunner; these tests replace only its delegate.
            File.WriteAllBytes(Python, [1]);
            using (var stream = File.Create(Audio))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write("RIFF"u8); writer.Write(32036); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
                writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(32000); writer.Write(new byte[32000]);
            }

            Timeline = new AnalysisAudioTimeline(
                EntityId<ProjectIdTag>.New(), EntityId<JobIdTag>.New(), EntityId<AssetIdTag>.New(),
                new ContentDigest(new string('a', 64)), 100,
                EntityId<ArtifactIdTag>.New(), new ContentDigest(new string('b', 64)), 0, 1,
                new RationalTime(1, 48000), null, 0, 1024, EntityId<ArtifactIdTag>.New(),
                ContentDigest.Compute(File.ReadAllBytes(Audio)), new FileInfo(Audio).Length, 16000,
                EntityId<ArtifactIdTag>.New());
        }

        public string Root { get; }
        public string Output { get; }
        public string Model { get; }
        public string Python { get; }
        public string Audio { get; }
        public AnalysisAudioTimeline Timeline { get; }

        public LocalAnalysisWorker Worker(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run) =>
            new(run, Python, Root);

        public ProcessRunResult Publish(ProcessRunRequest request, Action<JsonObject>? mutate = null)
        {
            using JsonDocument document = JsonDocument.Parse(request.StandardInput!);
            string id = document.RootElement.GetProperty("requestId").GetString()!;
            string policy = document.RootElement.GetProperty("params").GetProperty("languagePolicy").GetString()!;
            JsonObject root = Envelope(id, policy);
            mutate?.Invoke(root);
            byte[] bytes = Utf8.GetBytes(root.ToJsonString());
            string output = document.RootElement.GetProperty("outputRoot").GetString()!;
            using (var stream = new FileStream(Path.Combine(output, id + ".json"), FileMode.CreateNew))
            {
                stream.Write(bytes);
            }

            return new ProcessRunResult(0, Metadata(id, ContentDigest.Compute(bytes).Hex, bytes.Length), "", false, TimeSpan.Zero);
        }

        public LocalAsrResult Parse(byte[] bytes) => LocalAnalysisWorker.ParseResult(
            bytes, ContentDigest.Compute(bytes), "unused", RequestId, LocalAnalysisWorker.AutoLanguagePolicy, Timeline);

        public JsonObject Envelope(string requestId, string policy)
        {
            bool automatic = policy == LocalAnalysisWorker.AutoLanguagePolicy;
            return JsonSerializer.SerializeToNode(new
            {
                version = 1,
                requestId,
                method = "transcribe",
                result = new
                {
                    model_repository = "Systran/faster-whisper-small",
                    model_revision = "536b0662742c02347bc0e980a01041f333bce120",
                    model_manifest_sha256 = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f",
                    configuration = new
                    {
                        device = "cpu",
                        compute_type = "int8",
                        language = policy,
                        task = "transcribe",
                        beam_size = 5,
                        word_timestamps = true,
                        vad_filter = false,
                        condition_on_previous_text = true,
                    },
                    input_audio_sha256 = Timeline.AnalysisAudioDigest.Hex,
                    input_audio_byte_size = Timeline.AnalysisAudioByteSize,
                    input_audio_duration_seconds = 1.0,
                    language = automatic ? "en" : "ja",
                    language_confidence = automatic ? 0.73 : 1.0,
                    engine_duration_seconds = 1.0,
                    language_detection = new
                    {
                        requested_policy = policy,
                        detected_language = automatic ? "en" : null,
                        detected_language_confidence = automatic ? (double?)0.73 : null,
                        fallback_reason = (string?)null,
                    },
                    segments = new[]
                    {
                        new
                        {
                            segment_id = 7, seek = 19, text = " private 音声 ", start_seconds = 0.1, end_seconds = 0.8,
                            average_log_probability = -0.25, no_speech_probability = 0.01,
                            tokens = new[]
                            {
                                new { text = " private 音声 ", start_seconds = (double?)0.10000000000000002,
                                    end_seconds = (double?)0.8, recognition_confidence = 0.81, timing_requires_review = false },
                                new { text = "untimed", start_seconds = (double?)null, end_seconds = (double?)null,
                                    recognition_confidence = 0.5, timing_requires_review = true },
                            },
                        },
                    },
                },
            })!.AsObject();
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}