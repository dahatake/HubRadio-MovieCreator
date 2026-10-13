using System.Globalization;
using System.Text.Json;
using HubRadio.App.Rendering;
using HubRadio.Core.Identity;
using HubRadio.Core.Rendering;
using HubRadio.Core.Serialization;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Projects;

internal sealed record SourceOnlyNativeEvidence(string SchemaVersion, string Kind,
    SourceOnlyResultBindingDto Binding, SourceOnlyResultReferenceDto Artifact, SourceOnlyResultReferenceDto Source,
    string Role, string? RenditionId, string? ThumbnailPlanId, string QcApplicability,
    string? ProfileId, string? ProfileDigest, string VersionId, string VersionDigest, string GraphDigest, string? ManifestDigest, JsonElement Payload);

internal sealed record SourceOnlyQcEvidencePayload(
    string RuleSetVersion, string EvaluationId, string InputDigest, string StartedAt, string CompletedAt,
    string EnvironmentDigest, string ToolVersionsDigest, string? ModelVersionsDigest, string TemplateDigest,
    JsonElement BindingEvidence, JsonElement SettingsProfile, JsonElement SettingsSnapshot,
    JsonElement RenderTools, JsonElement? QcTools, SourceOnlyResultMeasurementsDto? Measurements,
    SourceOnlyResultCueDto[] Cues, SourceOnlyResultTimeDto ExpectedDuration, SourceOnlyResultTimeDto FrameRate,
    bool ProvenanceValid, bool SettingsValid, bool SidecarVerified,
    string? FailureCode, string? QcFailureReason, string? QcFailureStage,
    bool ImageVerified, string RenderingInputsDigest, JsonElement? OutputQc);

/// <summary>
/// Native-only immutable snapshots. Graph and manifest payloads deliberately use the
/// generator's existing canonical digest domain. They are inspected as JSON evidence,
/// never deserialized into get-only rendering classes or exposed to the Web.
/// </summary>
internal sealed class SourceOnlyResultEvidenceStore(ArtifactStore artifacts,
    Func<ArtifactPromotionRequest, CancellationToken, Task<StoredArtifact>>? promote = null)
{
    internal const int MaximumEvidenceBytes = 16 * 1024 * 1024;
    internal const int MaximumNativeArrayLength = 262144;
    private const string Schema = "source-only.artifact-evidence.v2";
    private readonly List<SourceOnlyResultPromotionAttempt> _attempts = [];
    internal IReadOnlyList<SourceOnlyResultPromotionAttempt> ReconciliationAttempts => Array.AsReadOnly(_attempts.ToArray());
    internal bool CleanupRequired { get; private set; }

    internal static JsonElement Snapshot<T>(T value)
    {
        // Serialize with the SAME names/numeric enums as StandardRenderGraphFactory.Digest.
        string json = SourceOnlyResultCodec.Write(value, MaximumEvidenceBytes, new JsonSerializerOptions { MaxDepth = 48 });
        using JsonDocument document = SourceOnlyResultCodec.ParseDocument(json, MaximumEvidenceBytes, MaximumNativeArrayLength);
        return document.RootElement.Clone();
    }

    internal static ContentDigest PayloadDigest(JsonElement payload) => ContentDigest.Compute(CanonicalJson.Write(payload));

    internal async Task<SourceOnlyResultEvidenceReference> SaveAsync(SourceOnlyResultBinding binding,
        SourceOnlyResultArtifact artifact, SourceOnlyResultEvidenceKind kind, JsonElement payload, CancellationToken ct)
    {
        var document = new SourceOnlyNativeEvidence(Schema, kind.ToString(), SourceOnlyResultCodec.BindingDto(binding),
            SourceOnlyResultCodec.ReferenceDto(artifact.Artifact), SourceOnlyResultCodec.ReferenceDto(artifact.Source),
            artifact.Role.ToString(), artifact.RenditionId?.ToString(), artifact.ThumbnailPlanId?.ToString(), artifact.QcApplicability.ToString(),
            artifact.ProfileId?.ToString(), artifact.ProfileDigest?.Hex, artifact.VersionId.ToString(), artifact.VersionDigest.Hex,
            artifact.GraphDigest.Hex, artifact.ManifestDigest?.Hex, payload);
        string json = SourceOnlyResultCodec.Write(document, MaximumEvidenceBytes);
        using JsonDocument checkedJson = SourceOnlyResultCodec.ParseDocument(json, MaximumEvidenceBytes, MaximumNativeArrayLength, MaximumEvidenceBytes);
        byte[] bytes = SourceOnlyResultCodec.Utf8.GetBytes(json);
        ContentDigest digest = ContentDigest.Compute(bytes);
        // Root is derived ONLY from a path and storage reference just validated by ArtifactStore,
        // never a locator or path supplied by a JSON payload or caller.
        StoredArtifact output = await artifacts.ValidateRegisteredAsync(artifact.ArtifactId, ct).ConfigureAwait(false);
        RequireSame(output, artifact.Artifact, binding.ProjectId);
        string actualPath = artifacts.GetValidatedRegisteredArtifactPath(output.Id);
        string[] segments = output.StorageReference.Split('/');
        SourceOnlyResultCodec.Require(segments.Length >= 4 && segments[0] == "projects" && segments[1] == binding.ProjectId.ToString());
        string projectRoot = actualPath;
        for (int i = 2; i < segments.Length; i++) projectRoot = Path.GetDirectoryName(projectRoot) ?? throw SourceOnlyResultCodec.Invalid();
        SourceOnlyResultCodec.Require(Path.GetFileName(projectRoot) == ProjectFolders.GetFolderName(artifacts.LocalWorkspacePath, binding.ProjectId.ToString()) &&
            Path.GetFileName(Path.GetDirectoryName(projectRoot)) == "projects");
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(projectRoot);
        string stagingRoot = Path.Combine(projectRoot, "staging");
        Directory.CreateDirectory(stagingRoot);
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(stagingRoot);
        string owned = Path.Combine(stagingRoot, "source-only-proof-" + Guid.NewGuid().ToString("N"));
        SourceOnlyResultCodec.Require(!WorkspacePathSafety.PathEntryExistsConservatively(owned));
        Directory.CreateDirectory(owned);
        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(owned);
        string path = Path.Combine(owned, "evidence.json");
        SourceOnlyResultPromotionAttempt? attempted = null;
        bool promotionCompleted = false;
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            var id = EntityId<ArtifactIdTag>.New();
            string relative = $"source-only/{binding.ExecutionId}/{artifact.ArtifactId}/{kind}/{id}.json";
            attempted = new(new(id, digest, bytes.LongLength, ArtifactKind.Evidence), binding.ProjectId,
                path, $"projects/{binding.ProjectId}/evidence/{relative}", kind);
            _attempts.Add(attempted); // BEFORE File.Move/INSERT: even rollback failure retains the exact attempt.
            StoredArtifact saved = await (promote ?? artifacts.PromoteAsync)(new ArtifactPromotionRequest(id, binding.ProjectId,
                ArtifactKind.Evidence, path, relative, digest, bytes.LongLength, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            RequireSame(saved, attempted.Artifact, binding.ProjectId);
            StoredArtifact validated = await artifacts.ValidateRegisteredAsync(id, ct).ConfigureAwait(false);
            RequireSame(validated, attempted.Artifact, binding.ProjectId);
            SourceOnlyResultCodec.Require(saved.StorageReference == attempted.StorageReference && validated.StorageReference == attempted.StorageReference);
            _attempts.Remove(attempted);
            promotionCompleted = true;
            return new(kind, Reference(saved));
        }
        finally
        {
            // Failed promotion is not a disposable scratch file. Preserve staging and
            // native descriptors for explicit recovery, including an unregistered moved file.
            if (attempted is not null && !promotionCompleted)
                CleanupRequired = true;
            else try
                {
                    if (File.Exists(path))
                    {
                        WorkspacePathSafety.ValidateExistingPathChain(projectRoot, path, targetIsDirectory: false);
                        File.Delete(path);
                    }
                    if (Directory.Exists(owned))
                    {
                        WorkspacePathSafety.ValidateExistingDirectoryAncestorChain(owned);
                        Directory.Delete(owned, recursive: false);
                    }
                }
                catch (Exception e) when (SourceOnlyOutputEvaluation.Recoverable(e)) { CleanupRequired = true; }
        }
    }

    internal async Task<SourceOnlyNativeEvidence> ReadAsync(SourceOnlyResultBinding binding, SourceOnlyResultArtifact artifact,
        SourceOnlyResultEvidenceReference reference, CancellationToken ct)
    {
        SourceOnlyResultCodec.Require(reference.Artifact.ByteSize <= MaximumEvidenceBytes);
        StoredArtifact metadata = artifacts.GetArtifact(reference.Artifact.ArtifactId) ?? throw SourceOnlyResultCodec.Invalid();
        RequireSame(metadata, reference.Artifact, binding.ProjectId);
        await using ValidatedArtifactReadStream stream = await artifacts.OpenValidatedReadAsync(reference.Artifact.ArtifactId, ct).ConfigureAwait(false);
        RequireSame(stream, reference.Artifact, binding.ProjectId);
        byte[] bytes = new byte[checked((int)stream.ByteSize)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        string json = SourceOnlyResultCodec.Utf8.GetString(bytes);
        using JsonDocument parsed = SourceOnlyResultCodec.ParseDocument(json, MaximumEvidenceBytes, MaximumNativeArrayLength, MaximumEvidenceBytes);
        SourceOnlyNativeEvidence document = JsonSerializer.Deserialize<SourceOnlyNativeEvidence>(parsed.RootElement, SourceOnlyResultCodec.Options)
            ?? throw SourceOnlyResultCodec.Invalid();
        SourceOnlyResultCodec.Require(document.SchemaVersion == Schema && document.Kind == reference.Kind.ToString() &&
            SourceOnlyResultCodec.Binding(document.Binding) == binding && document.Artifact == SourceOnlyResultCodec.ReferenceDto(artifact.Artifact) &&
            document.Source == SourceOnlyResultCodec.ReferenceDto(artifact.Source) && document.Role == artifact.Role.ToString() &&
            document.RenditionId == artifact.RenditionId?.ToString() && document.ThumbnailPlanId == artifact.ThumbnailPlanId?.ToString() &&
            document.QcApplicability == artifact.QcApplicability.ToString() && document.ProfileId == artifact.ProfileId?.ToString() &&
            document.ProfileDigest == artifact.ProfileDigest?.Hex && document.VersionId == artifact.VersionId.ToString() &&
            document.VersionDigest == artifact.VersionDigest.Hex && document.GraphDigest == artifact.GraphDigest.Hex &&
            document.ManifestDigest == artifact.ManifestDigest?.Hex && SourceOnlyResultCodec.Write(document, MaximumEvidenceBytes) == json);
        return document;
    }

    internal static SourceOnlyResultArtifactReference Reference(StoredArtifact artifact) =>
        new(artifact.Id, artifact.ContentDigest, artifact.ByteSize, artifact.Kind);

    internal static void RequireSame(StoredArtifact actual, SourceOnlyResultArtifactReference expected, EntityId<HubRadio.Core.Projects.ProjectIdTag> project) =>
        SourceOnlyResultCodec.Require(actual.Id == expected.ArtifactId && actual.ProjectId == project && actual.Kind == expected.Kind &&
            actual.ContentDigest == expected.ContentDigest && actual.ByteSize == expected.ByteSize);
    internal static void RequireSame(ValidatedArtifactReadStream actual, SourceOnlyResultArtifactReference expected, EntityId<HubRadio.Core.Projects.ProjectIdTag> project) =>
        SourceOnlyResultCodec.Require(actual.Id == expected.ArtifactId && actual.ProjectId == project && actual.Kind == expected.Kind &&
            actual.ContentDigest == expected.ContentDigest && actual.ByteSize == expected.ByteSize);

    internal static void ValidateGraph(JsonElement graph, SourceOnlyResultArtifact artifact)
    {
        SourceOnlyResultCodec.Require(PayloadDigest(graph) == artifact.GraphDigest &&
            NativeId(graph, "CompositionVersionId") == artifact.VersionId.ToString() && NativeDigest(graph, "CompositionVersionDigest") == artifact.VersionDigest.Hex &&
            NativeId(graph, "ProfileId") == artifact.ProfileId?.ToString() && NativeDigest(graph, "ProfileDigest") == artifact.ProfileDigest?.Hex);
        JsonElement inputs = graph.GetProperty("Inputs");
        SourceOnlyResultCodec.Require(inputs.ValueKind == JsonValueKind.Array && inputs.GetArrayLength() is > 0 and <= 512);
        foreach (JsonElement input in inputs.EnumerateArray())
        {
            int kind = checked((int)Integer(input.GetProperty("Kind")));
            SourceOnlyResultCodec.Require(Enum.IsDefined((RenderInputKind)kind));
            if ((RenderInputKind)kind is RenderInputKind.Video or RenderInputKind.Audio)
                SourceOnlyResultCodec.Require(NativeDigest(input, "ContentDigest") == artifact.Source.ContentDigest.Hex && Integer(input.GetProperty("ByteSize")) == artifact.Source.ByteSize);
        }
    }

    internal static void ValidateManifest(JsonElement manifest, SourceOnlyResultBinding binding, SourceOnlyResultArtifact artifact, StoredArtifact actual)
    {
        SourceOnlyResultCodec.Require(PayloadDigest(manifest) == artifact.ManifestDigest && NativeId(manifest, "ProjectId") == binding.ProjectId.ToString() &&
            NativeId(manifest, "CompositionVersionId") == artifact.VersionId.ToString() && NativeDigest(manifest, "CompositionVersionDigest") == artifact.VersionDigest.Hex &&
            NativeId(manifest, "ProfileId") == artifact.ProfileId?.ToString() && NativeDigest(manifest, "ProfileDigest") == artifact.ProfileDigest?.Hex);
        JsonElement outputs = manifest.GetProperty("Outputs");
        SourceOnlyResultCodec.Require(outputs.ValueKind == JsonValueKind.Array && outputs.GetArrayLength() == 1);
        JsonElement output = outputs[0];
        SourceOnlyResultCodec.Require(NativeDigest(output, "Sha256") == artifact.ArtifactDigest.Hex && Integer(output.GetProperty("ByteSize")) == artifact.ByteSize &&
            output.GetProperty("MediaType").GetString() == artifact.MediaType && output.GetProperty("StorageReference").GetString() == actual.StorageReference &&
            output.GetProperty("ArtifactType").GetString() == (artifact.IsVideo ? "video" : artifact.IsImage ? "thumbnail" : "subtitle"));
    }

    internal static string NativeId(JsonElement parent, string name) => parent.GetProperty(name).GetProperty("Value").GetString() ?? throw SourceOnlyResultCodec.Invalid();
    internal static string NativeDigest(JsonElement parent, string name) => parent.GetProperty(name).GetProperty("Hex").GetString() ?? throw SourceOnlyResultCodec.Invalid();
    internal static long Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && long.TryParse(value.GetRawText(),
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out long n)
        ? n : throw SourceOnlyResultCodec.Invalid();
}