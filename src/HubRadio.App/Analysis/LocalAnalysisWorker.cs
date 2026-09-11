using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Analysis;
using HubRadio.Infrastructure.Processes;

namespace HubRadio.App.Analysis;

public enum LocalAsrLanguagePolicy
{
    Japanese,
    AutoDetectWithJaJpFallback,
}

public sealed record LocalAsrLanguageDetection(
    string RequestedPolicy,
    string? DetectedLanguage,
    double? DetectedLanguageConfidence,
    string? FallbackReason);

/// <summary>Native-only evidence. Do not forward paths or raw recognition text to logs.</summary>
public sealed class LocalAsrResult
{
    private readonly byte[] _bytes;

    internal LocalAsrResult(
        AsrWorkerResult workerResult,
        string artifactPath,
        byte[] bytes,
        ContentDigest digest,
        LocalAsrLanguageDetection languageDetection)
    {
        WorkerResult = workerResult;
        ArtifactPath = artifactPath;
        _bytes = bytes.ToArray();
        Digest = digest;
        LanguageDetection = languageDetection;
    }

    public AsrWorkerResult WorkerResult { get; }
    public string ArtifactPath { get; }
    public ReadOnlyMemory<byte> RawResultBytes => _bytes.ToArray();
    public ContentDigest Digest { get; }
    public long ByteSize => _bytes.LongLength;
    public LocalAsrLanguageDetection LanguageDetection { get; }
}

public sealed class LocalAnalysisWorkerException : Exception
{
    internal LocalAnalysisWorkerException(string code)
        : base("The local analysis worker operation failed.") => Code = code;

    public string Code { get; }
}

/// <summary>Actual repetition task receipt, still proposed evidence rather than permission to cut.</summary>
public sealed class LocalRepetitionResult
{
    private readonly byte[] bytes;
    internal LocalRepetitionResult(IReadOnlyList<RepetitionSuggestionCandidate> candidates,
        byte[] bytes, string artifactPath, ContentDigest digest)
    {
        Candidates = System.Array.AsReadOnly(candidates.ToArray());
        this.bytes = bytes.ToArray();
        ArtifactPath = artifactPath;
        Digest = digest;
    }
    public IReadOnlyList<RepetitionSuggestionCandidate> Candidates { get; }
    public ReadOnlyMemory<byte> RawResultBytes => bytes.ToArray();
    public string ArtifactPath { get; }
    public ContentDigest Digest { get; }
}

/// <summary>
/// Single-use task CLI adapter, not the ping client. The trusted native host supplies a
/// verified installed Python executable and managed workspace, never renderer-supplied
/// paths. hubradio_worker must be installed in that interpreter (-I ignores PYTHONPATH).
/// Result directories already exist below workspace/.../staging and are ACL protected.
/// The caller owns promotion and reconciliation of that attempt's directory after
/// cancellation/failure; this class never recursively deletes shared staging.
/// </summary>
public sealed class LocalAnalysisWorker
{
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxResultBytes = 16 * 1024 * 1024;
    public const string AutoLanguagePolicy = "autoDetectWithJaJpFallback";
    private const string ModelRepository = "Systran/faster-whisper-small";
    private const string ModelRevision = "536b0662742c02347bc0e980a01041f333bce120";
    private const string ModelManifest = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f";
    private const int MaxTextBytes = 65536;
    private const int MaxItems = 100000;
    private const double MaxSeconds = (double)long.MaxValue / AnalysisAudioTimeline.FixedSampleRate;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SupportedLanguages = new(
        ("af am ar as az ba be bg bn bo br bs ca cs cy da de el en es et eu fa fi fo fr " +
         "gl gu ha haw he hi hr ht hu hy id is it ja jw ka kk km kn ko la lb ln lo lt lv " +
         "mg mi mk ml mn mr ms mt my ne nl nn no oc pa pl ps pt ro ru sa sd si sk sl sn " +
         "so sq sr su sv sw ta te tg th tk tl tr tt uk ur uz vi yi yo zh yue").Split(' '),
        StringComparer.Ordinal);
    private static readonly HashSet<string> WorkerErrors = new(StringComparer.Ordinal)
    {
        "task.invalid_request", "task.invalid_path", "task.input_mismatch",
        "task.output_failed", "task.result_too_large", "task.failed", "task.cancelled",
        "asr.invalid_audio", "asr.invalid_model", "asr.input_changed", "asr.model_changed",
        "asr.runtime_unavailable", "asr.processing_failed", "repetition.invalid_input",
        "repetition.input_too_large",
    };

    private readonly Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> _runAsync;
    private readonly string _pythonExecutable;
    private readonly string _workspacePath;

    public LocalAnalysisWorker(ProcessRunner processRunner, string pythonExecutable, string workspacePath)
        : this(GetRunner(processRunner), pythonExecutable, workspacePath)
    {
    }

    internal LocalAnalysisWorker(
        Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> runAsync,
        string pythonExecutable,
        string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(runAsync);
        _runAsync = runAsync;
        _pythonExecutable = RequirePath(pythonExecutable, directory: false);
        _workspacePath = RequirePath(workspacePath, directory: true);
    }

    /// <summary>
    /// Binds to the existing extracted-audio asset evidence (AnalysisAudioTimeline).
    /// Keeps raw doubles, null timing and empty speech results; importer owns source-time
    /// mapping. Language defaults to actual auto-detection with a documented Japanese fallback.
    /// </summary>
    public async Task<LocalAsrResult> TranscribeAsync(
        string audioPath,
        string modelDirectory,
        string resultDirectory,
        AnalysisAudioTimeline audioTimeline,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        LocalAsrLanguagePolicy languagePolicy = LocalAsrLanguagePolicy.AutoDetectWithJaJpFallback)
    {
        ArgumentNullException.ThrowIfNull(audioTimeline);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        string policy = languagePolicy switch
        {
            LocalAsrLanguagePolicy.Japanese => "ja",
            LocalAsrLanguagePolicy.AutoDetectWithJaJpFallback => AutoLanguagePolicy,
            _ => throw new ArgumentOutOfRangeException(nameof(languagePolicy)),
        };

        try
        {
            RequirePath(_pythonExecutable, directory: false);
            RequirePath(_workspacePath, directory: true);
            string audio = RequirePath(audioPath, directory: false);
            string model = RequirePath(modelDirectory, directory: true);
            string output = RequirePath(resultDirectory, directory: true);
            RequireChild(_workspacePath, audio);
            string relativeOutput = RequireChild(_workspacePath, output);
            if (!relativeOutput.Split(Path.DirectorySeparatorChar).Contains("staging", StringComparer.Ordinal))
            {
                throw Failure("task.invalid_path");
            }

            // On Windows this held, non-delete/non-write-shared handle prevents replacement
            // of the audio while the worker opens its own read handle. Directory ACLs are
            // still the trusted boundary; lexical checks alone are not a sandbox.
            await using FileStream heldAudio = OpenRead(audio);
            await VerifyAudioAsync(heldAudio, audioTimeline, cancellationToken).ConfigureAwait(false);
            string requestId = Guid.NewGuid().ToString("D");
            string artifactPath = Path.Combine(output, requestId + ".json");
            if (File.Exists(artifactPath) || Directory.Exists(artifactPath))
            {
                throw Failure("task.output_failed");
            }

            byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                requestId,
                method = "transcribe",
                workspaceRoot = _workspacePath,
                outputRoot = output,
                @params = new
                {
                    audioPath = audio,
                    modelDirectory = model,
                    inputAudioSha256 = audioTimeline.AnalysisAudioDigest.Hex,
                    inputAudioByteSize = audioTimeline.AnalysisAudioByteSize,
                    languagePolicy = policy,
                },
            });
            if (requestBytes.Length > MaxRequestBytes)
            {
                throw Failure("task.invalid_request");
            }

            ProcessRunRequest request = new(
                _pythonExecutable,
                ["-I", "-B", "-m", "hubradio_worker", "--task"],
                output,
                timeout,
                maxCapturedBytes: MaxRequestBytes,
                standardInput: StrictUtf8.GetString(requestBytes));
            ProcessRunResult process = await _runAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            (ContentDigest digest, int size) = ParseMetadata(process, requestId);
            RequirePath(audio, directory: false);
            await VerifyAudioAsync(heldAudio, audioTimeline, cancellationToken).ConfigureAwait(false);
            RequirePath(output, directory: true);
            RequirePath(artifactPath, directory: false);
            byte[] bytes;
            await using (FileStream resultStream = OpenRead(artifactPath))
            {
                if (resultStream.Length != size)
                {
                    throw Failure("task.invalid_result");
                }

                bytes = new byte[size];
                await resultStream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (resultStream.Position != resultStream.Length || ContentDigest.Compute(bytes) != digest)
                {
                    throw Failure("task.invalid_result");
                }
            }

            LocalAsrResult parsed = ParseResult(bytes, digest, artifactPath, requestId, policy, audioTimeline);
            cancellationToken.ThrowIfCancellationRequested();
            return parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Do not retain inner exceptions, which may include private process output.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException)
        {
            throw Failure("task.timeout");
        }
        catch (LocalAnalysisWorkerException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure("task.failed");
        }
    }

    private static async Task VerifyAudioAsync(
        FileStream stream, AnalysisAudioTimeline timeline, CancellationToken cancellationToken)
    {
        if (stream.Length != timeline.AnalysisAudioByteSize)
        {
            throw Failure("task.input_mismatch");
        }

        stream.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (ContentDigest.FromBytes(hash) != timeline.AnalysisAudioDigest ||
            stream.Length != timeline.AnalysisAudioByteSize)
        {
            throw Failure("task.input_mismatch");
        }
    }

    /// <summary>
    /// Runs the existing deterministic task on the actual immutable word tokens.
    /// The closed worker transport is bounded to 64 KiB: oversized inputs are
    /// explicitly refused, never truncated, sampled or supplied invented tokens.
    /// </summary>
    public async Task<LocalRepetitionResult> FindRepetitionsAsync(TranscriptRevision transcript,
        ContentDigest transcriptDigest, EntityId<ModelRunIdTag> modelRunId,
        string resultDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(transcriptDigest);
        ArgumentNullException.ThrowIfNull(modelRunId);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout > ProcessRunRequest.MaximumTimeout) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        try
        {
            if (transcriptDigest != ContentDigest.Compute(StrictUtf8.GetBytes(TranscriptSnapshotJson.WriteRevision(transcript))))
            { throw Failure("task.input_mismatch"); }
            _ = RequirePath(_pythonExecutable, directory: false);
            _ = RequirePath(_workspacePath, directory: true);
            string output = RequirePath(resultDirectory, directory: true);
            if (!RequireChild(_workspacePath, output).Split(Path.DirectorySeparatorChar).Contains("staging", StringComparer.Ordinal))
            { throw Failure("task.invalid_path"); }
            string requestId = Guid.NewGuid().ToString("D");
            byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                requestId,
                method = "repetition",
                workspaceRoot = _workspacePath,
                outputRoot = output,
                @params = new
                {
                    transcript = new
                    {
                        revision_id = transcript.Id.ToString(),
                        revision_digest = transcriptDigest.Hex,
                        utterances = transcript.Utterances.Select(utterance => new
                        {
                            utterance_id = utterance.Id.ToString(),
                            source_span_id = utterance.SourceSpanId.ToString(),
                            speaker_id = utterance.SpeakerId.ToString(),
                            sentences = utterance.Sentences.Select(sentence => new
                            {
                                sentence_id = sentence.Id.ToString(),
                                tokens = sentence.Tokens.Select(token => new { token_id = token.Id.ToString(), text = token.Text }).ToArray(),
                            }).ToArray(),
                        }).ToArray(),
                    },
                    modelRunId = modelRunId.ToString(),
                },
            });
            if (requestBytes.Length > MaxRequestBytes) { throw Failure("repetition.input_too_large"); }
            string artifactPath = Path.Combine(output, requestId + ".json");
            if (File.Exists(artifactPath) || Directory.Exists(artifactPath)) { throw Failure("task.output_failed"); }
            ProcessRunResult process = await _runAsync(new ProcessRunRequest(_pythonExecutable,
                ["-I", "-B", "-m", "hubradio_worker", "--task"], output, timeout,
                maxCapturedBytes: MaxRequestBytes, standardInput: StrictUtf8.GetString(requestBytes)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            (ContentDigest digest, int size) = ParseMetadata(process, requestId, "repetition");
            _ = RequirePath(output, directory: true);
            _ = RequirePath(artifactPath, directory: false);
            await using FileStream resultStream = OpenRead(artifactPath);
            if (resultStream.Length != size) { throw Failure("task.invalid_result"); }
            byte[] bytes = new byte[size];
            await resultStream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (resultStream.Length != size) { throw Failure("task.invalid_result"); }
            return ParseRepetitionResult(bytes, digest, artifactPath, requestId, transcript, modelRunId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (TimeoutException) { throw Failure("task.timeout"); }
        catch (LocalAnalysisWorkerException) { throw; }
        catch { throw Failure("task.failed"); }
    }

    internal static LocalRepetitionResult ParseRepetitionResult(byte[] bytes, ContentDigest digest, string artifactPath,
        string requestId, TranscriptRevision transcript, EntityId<ModelRunIdTag> modelRunId)
    {
        try
        {
            if (bytes.Length is 0 or > MaxResultBytes || ContentDigest.Compute(bytes) != digest) { throw Failure("task.invalid_result"); }
            using JsonDocument document = ParseJson(bytes);
            JsonElement envelope = document.RootElement;
            Shape(envelope, "version", "requestId", "method", "result");
            if (Integer(envelope, "version", 1, 1) != 1 || Text(envelope, "requestId") != requestId || Text(envelope, "method") != "repetition")
            { throw Failure("task.invalid_result"); }
            JsonElement result = envelope.GetProperty("result");
            Shape(result, "suggestions");
            ContentDigest transcriptDigest = ContentDigest.Compute(StrictUtf8.GetBytes(TranscriptSnapshotJson.WriteRevision(transcript)));
            var registered = new List<SuggestionCandidate>();
            foreach (JsonElement candidate in Array(result, "suggestions"))
            {
                Shape(candidate, "candidate_key", "model_run_id", "source_revision_id", "source_revision_digest", "source_span_id",
                    "evidence_token_ids", "rule_version", "kind", "status", "reason", "confidence", "automatic_deletion_allowed", "human_review_required", "features");
                JsonElement features = candidate.GetProperty("features");
                Shape(features, "rule", "comparison_source_span_id", "comparison_token_ids", "matching_token_count", "exact_nfkc_match",
                    "structurally_adjacent", "same_speaker_required", "candidate_is_later_occurrence", "retention_choice_required");
                EntityId<T> Id<T>(JsonElement value) => EntityId<T>.TryParse(value.GetString(), out var id) &&
                    id.ToString() == value.GetString() ? id : throw Failure("task.invalid_result");
                var spanId = Id<SourceSpanIdTag>(candidate.GetProperty("source_span_id"));
                var comparisonSpanId = Id<SourceSpanIdTag>(features.GetProperty("comparison_source_span_id"));
                EntityId<TranscriptTokenIdTag>[] tokenIds = Array(candidate, "evidence_token_ids").Select(Id<TranscriptTokenIdTag>).ToArray();
                EntityId<TranscriptTokenIdTag>[] comparisonIds = Array(features, "comparison_token_ids").Select(Id<TranscriptTokenIdTag>).ToArray();
                Utterance later = transcript.Utterances.Single(item => item.SourceSpanId == spanId);
                Utterance earlier = transcript.Utterances.Single(item => item.SourceSpanId == comparisonSpanId);
                if (Id<ModelRunIdTag>(candidate.GetProperty("model_run_id")) != modelRunId ||
                    Id<TranscriptRevisionIdTag>(candidate.GetProperty("source_revision_id")) != transcript.Id ||
                    Digest(candidate, "source_revision_digest") != transcriptDigest ||
                    Text(candidate, "kind") != "deleteRange" || Text(candidate, "status") != "proposed" ||
                    candidate.GetProperty("confidence").ValueKind != JsonValueKind.Null ||
                    tokenIds.Any(id => !later.Tokens.Any(token => token.Id == id)) ||
                    comparisonIds.Any(id => !earlier.Tokens.Any(token => token.Id == id))) { throw Failure("task.invalid_result"); }
                var evidence = new RepetitionReasonFeatures(Text(features, "rule"), comparisonSpanId, comparisonIds,
                    (int)Integer(features, "matching_token_count", 1, MaxItems), Boolean(features, "exact_nfkc_match"),
                    Boolean(features, "structurally_adjacent"), Boolean(features, "same_speaker_required"),
                    Boolean(features, "candidate_is_later_occurrence"), Boolean(features, "retention_choice_required"));
                var payload = new RepetitionSuggestionPayload(Digest(candidate, "candidate_key"), modelRunId, transcript.Id, transcriptDigest,
                    spanId, tokenIds, Text(candidate, "rule_version"), SuggestionKind.DeleteRange, SuggestionStatus.Proposed,
                    Text(candidate, "reason"), null, Boolean(candidate, "automatic_deletion_allowed"), Boolean(candidate, "human_review_required"), evidence);
                byte[] identity = SHA256.HashData(StrictUtf8.GetBytes("source-only.repetition-suggestion.v1\0" + modelRunId + "\0" + payload.CandidateKey.Hex));
                identity[7] = (byte)((identity[7] & 15) | 0x50);
                identity[8] = (byte)((identity[8] & 63) | 0x80);
                var suggestion = new Suggestion(EntityId<SuggestionIdTag>.From(new Guid(identity.AsSpan(0, 16))), modelRunId,
                    SuggestionKind.DeleteRange, SuggestionStatus.Proposed, OriginRef.FromSourceSpan(spanId), payload.Reason, null, null, null);
                registered.Add(SuggestionService.Register(payload, suggestion, registered));
            }
            return new LocalRepetitionResult(registered.Cast<RepetitionSuggestionCandidate>().ToArray(), bytes, artifactPath, digest);
        }
        catch (LocalAnalysisWorkerException) { throw; }
        catch { throw Failure("task.invalid_result"); }
    }

    internal static (ContentDigest Digest, int ByteSize) ParseMetadata(ProcessRunResult process, string requestId,
        string expectedMethod = "transcribe")
    {
        try
        {
            if (process.Truncated || process.StandardError.Length != 0 || process.Duration < TimeSpan.Zero ||
                process.StandardOutput.Length is 0 or > 1024 ||
                !process.StandardOutput.EndsWith('\n') ||
                process.StandardOutput[..^1].Any(static character => character is < ' ' or > '~'))
            {
                throw Failure("task.invalid_result");
            }

            using JsonDocument document = ParseJson(StrictUtf8.GetBytes(process.StandardOutput));
            JsonElement root = document.RootElement;
            string status = Text(root, "status");
            if (status == "error")
            {
                Shape(root, "version", "status", "code");
                string code = Text(root, "code");
                if (Integer(root, "version", 1, 1) != 1 || process.ExitCode != 1 || !WorkerErrors.Contains(code))
                {
                    throw Failure("task.invalid_result");
                }

                throw Failure(code);
            }

            Shape(root, "version", "status", "requestId", "method", "sha256", "byteSize");
            if (status != "ok" || process.ExitCode != 0 || Integer(root, "version", 1, 1) != 1 ||
                Text(root, "requestId") != requestId || expectedMethod is not ("transcribe" or "repetition") || Text(root, "method") != expectedMethod)
            {
                throw Failure("task.invalid_result");
            }

            return (Digest(root, "sha256"), (int)Integer(root, "byteSize", 1, MaxResultBytes));
        }
        catch (LocalAnalysisWorkerException) { throw; }
        catch (Exception) { throw Failure("task.invalid_result"); }
    }

    internal static LocalAsrResult ParseResult(
        byte[] bytes, ContentDigest digest, string artifactPath, string requestId,
        string requestedPolicy, AnalysisAudioTimeline timeline)
    {
        try
        {
            if (bytes.Length is 0 or > MaxResultBytes || ContentDigest.Compute(bytes) != digest ||
                requestedPolicy is not ("ja" or AutoLanguagePolicy))
            {
                throw Failure("task.invalid_result");
            }

            using JsonDocument document = ParseJson(bytes);
            JsonElement envelope = document.RootElement;
            Shape(envelope, "version", "requestId", "method", "result");
            if (Integer(envelope, "version", 1, 1) != 1 || Text(envelope, "requestId") != requestId ||
                Text(envelope, "method") != "transcribe")
            {
                throw Failure("task.invalid_result");
            }

            JsonElement result = envelope.GetProperty("result");
            Shape(result, "model_repository", "model_revision", "model_manifest_sha256", "configuration",
                "input_audio_sha256", "input_audio_byte_size", "input_audio_duration_seconds", "language",
                "language_confidence", "engine_duration_seconds", "segments", "language_detection");
            JsonElement config = result.GetProperty("configuration");
            Shape(config, "device", "compute_type", "language", "task", "beam_size",
                "word_timestamps", "vad_filter", "condition_on_previous_text");
            var configuration = new AsrWorkerConfiguration(
                Text(config, "device"), Text(config, "compute_type"), Text(config, "language"),
                Text(config, "task"), (int)Integer(config, "beam_size", 1, int.MaxValue),
                Boolean(config, "word_timestamps"), Boolean(config, "vad_filter"),
                Boolean(config, "condition_on_previous_text"));
            if (Text(result, "model_repository") != ModelRepository ||
                Text(result, "model_revision") != ModelRevision || Digest(result, "model_manifest_sha256").Hex != ModelManifest ||
                configuration.Device != "cpu" || configuration.ComputeType != "int8" ||
                configuration.Language != requestedPolicy || configuration.Task != "transcribe" ||
                configuration.BeamSize != 5 || !configuration.WordTimestamps || configuration.VadFilter ||
                !configuration.ConditionOnPreviousText)
            {
                throw Failure("task.invalid_result");
            }

            string language = Text(result, "language");
            double confidence = Number(result, "language_confidence", 0, 1);
            LocalAsrLanguageDetection detection = ParseDetection(
                result.GetProperty("language_detection"), requestedPolicy, language, confidence);
            ContentDigest audioDigest = Digest(result, "input_audio_sha256");
            long audioSize = Integer(result, "input_audio_byte_size", 1, long.MaxValue);
            double audioDuration = Number(result, "input_audio_duration_seconds", double.Epsilon, MaxSeconds);
            if (audioDigest != timeline.AnalysisAudioDigest || audioSize != timeline.AnalysisAudioByteSize ||
                audioDuration != (double)timeline.AnalysisSampleCount / AnalysisAudioTimeline.FixedSampleRate)
            {
                throw Failure("task.input_mismatch");
            }

            var segments = new List<AsrWorkerSegment>();
            int totalTokens = 0;
            foreach (JsonElement segment in Array(result, "segments"))
            {
                Shape(segment, "segment_id", "seek", "text", "start_seconds", "end_seconds",
                    "average_log_probability", "no_speech_probability", "tokens");
                var tokens = new List<AsrWorkerToken>();
                foreach (JsonElement token in Array(segment, "tokens"))
                {
                    if (++totalTokens > MaxItems) { throw Failure("task.invalid_result"); }
                    Shape(token, "text", "start_seconds", "end_seconds", "recognition_confidence", "timing_requires_review");
                    double? start = NullableNumber(token, "start_seconds", 0, MaxSeconds);
                    double? end = NullableNumber(token, "end_seconds", 0, MaxSeconds);
                    bool review = Boolean(token, "timing_requires_review");
                    if ((start > audioDuration || end > audioDuration) && !review)
                    {
                        throw Failure("task.invalid_result");
                    }

                    tokens.Add(new AsrWorkerToken(Text(token, "text"), start, end,
                        Number(token, "recognition_confidence", 0, 1), review));
                }

                segments.Add(new AsrWorkerSegment(
                    (int)Integer(segment, "segment_id", 0, int.MaxValue),
                    (int)Integer(segment, "seek", 0, int.MaxValue), Text(segment, "text"),
                    Number(segment, "start_seconds", 0, audioDuration),
                    Number(segment, "end_seconds", 0, audioDuration),
                    Number(segment, "average_log_probability", -double.MaxValue, double.MaxValue),
                    Number(segment, "no_speech_probability", 0, 1), tokens));
            }

            var worker = new AsrWorkerResult(
                Text(result, "model_repository"), Text(result, "model_revision"),
                Digest(result, "model_manifest_sha256"), configuration, audioDigest, audioSize,
                audioDuration, language, confidence, Number(result, "engine_duration_seconds", 0, MaxSeconds), segments);
            return new LocalAsrResult(worker, artifactPath, bytes, digest, detection);
        }
        catch (LocalAnalysisWorkerException) { throw; }
        catch (Exception) { throw Failure("task.invalid_result"); }
    }

    private static LocalAsrLanguageDetection ParseDetection(
        JsonElement value, string policy, string language, double confidence)
    {
        Shape(value, "requested_policy", "detected_language", "detected_language_confidence", "fallback_reason");
        string? detected = NullableText(value, "detected_language");
        double? probability = NullableNumber(value, "detected_language_confidence", 0, 1);
        string? fallback = NullableText(value, "fallback_reason");
        if (Text(value, "requested_policy") != policy || !IsSupportedLanguage(language))
        {
            throw Failure("task.invalid_result");
        }

        bool valid = policy == "ja"
            ? language == "ja" && detected is null && probability is null && fallback is null
            : fallback is null
                ? detected == language && probability == confidence
                : (fallback is "absent" or "error") && language == "ja" && detected is null && probability is null;
        if (!valid) { throw Failure("task.invalid_result"); }
        return new LocalAsrLanguageDetection(policy, detected, probability, fallback);
    }

    internal static bool IsSupportedLanguage(string language) => SupportedLanguages.Contains(language);

    internal static string GetSafeFailureCode(LocalAnalysisWorkerException failure) =>
        WorkerErrors.Contains(failure.Code) || failure.Code is "task.invalid_result" or "task.timeout"
            ? failure.Code : "source_only.execution_failed";

    private static JsonDocument ParseJson(byte[] bytes)
    {
        _ = StrictUtf8.GetCharCount(bytes);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            throw Failure("task.invalid_result");
        }

        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
    }

    private static void Shape(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw Failure("task.invalid_result"); }
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name)) { throw Failure("task.invalid_result"); }
        }

        if (remaining.Count != 0) { throw Failure("task.invalid_result"); }
    }

    private static string Text(JsonElement value, string name)
    {
        string text = value.GetProperty(name).GetString() ?? throw Failure("task.invalid_result");
        if (StrictUtf8.GetByteCount(text) > MaxTextBytes) { throw Failure("task.invalid_result"); }
        return text;
    }

    private static string? NullableText(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name);

    private static bool Boolean(JsonElement value, string name) => value.GetProperty(name).GetBoolean();

    private static long Integer(JsonElement value, string name, long minimum, long maximum)
    {
        if (!value.GetProperty(name).TryGetInt64(out long result) || result < minimum || result > maximum)
        {
            throw Failure("task.invalid_result");
        }

        return result;
    }

    private static double Number(JsonElement value, string name, double minimum, double maximum)
    {
        double result = value.GetProperty(name).GetDouble();
        if (!double.IsFinite(result) || result < minimum || result > maximum)
        {
            throw Failure("task.invalid_result");
        }

        return result;
    }

    private static double? NullableNumber(JsonElement value, string name, double minimum, double maximum) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Number(value, name, minimum, maximum);

    private static ContentDigest Digest(JsonElement value, string name)
    {
        string text = Text(value, name);
        if (!ContentDigest.TryParse(text, out ContentDigest? digest) || text != digest.Hex)
        {
            throw Failure("task.invalid_result");
        }

        return digest;
    }

    private static JsonElement.ArrayEnumerator Array(JsonElement value, string name)
    {
        JsonElement array = value.GetProperty(name);
        if (array.GetArrayLength() > MaxItems) { throw Failure("task.invalid_result"); }
        return array.EnumerateArray();
    }

    private static string RequirePath(string path, bool directory) =>
        RequirePath(path, directory, File.GetAttributes);

    // The internal probe seam lets tests assert that denied ancestors prevent ALL
    // descendant metadata IO, not merely that a process never starts.
    internal static string RequirePath(string path, bool directory, Func<string, FileAttributes> getAttributes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path != path.Trim() ||
                !Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) ||
                OperatingSystem.IsWindows() && (path.AsSpan(2).Contains(':') ||
                    path.Split('\\').Skip(1).Any(static part => part.EndsWith('.') || part.EndsWith(' '))))
            {
                throw Failure("task.invalid_path");
            }

            // Build the chain lexically, then probe root-to-leaf. Probing the leaf
            // first could already traverse an ancestor junction outside the workspace.
            var ancestors = new Stack<string>();
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                ancestors.Push(current);
            }

            foreach (string entry in ancestors)
            {
                FileAttributes attributes = getAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                    attributes.HasFlag(FileAttributes.Directory) != (entry == path ? directory : true))
                {
                    throw Failure("task.invalid_path");
                }
            }

            return path;
        }
        catch (Exception) { throw Failure("task.invalid_path"); }
    }

    private static string RequireChild(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative == ".." || Path.IsPathFullyQualified(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Failure("task.invalid_path");
        }

        return relative;
    }

    private static FileStream OpenRead(string path)
    {
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if ((File.GetAttributes(stream.SafeFileHandle) &
                (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw Failure("task.invalid_path");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> GetRunner(ProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync;
    }

    private static LocalAnalysisWorkerException Failure(string code) => new(code);
}