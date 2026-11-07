using System.Globalization;
using System.Text.Json;
using HubRadio.Core.Identity;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Projects;

/// <summary>Existing pre-evaluation generator checkpoint, independent of QC statistics.</summary>
internal sealed record SourceOnlyResultGenerationCheckpoint(SourceOnlyResultArtifactReference Artifact, JsonElement Payload)
{
    internal static async Task<SourceOnlyResultGenerationCheckpoint?> ReadAsync(ArtifactStore store,
        StoredSourceOnlyExecution execution, CancellationToken ct)
    {
        var id = SourceOnlyPipeline.StableId<ArtifactIdTag>(execution.Request.ExecutionId.ToString(), "generation-evidence");
        StoredArtifact? recorded = store.GetArtifact(id);
        if (recorded is null) return null; // Standalone evaluation has no pipeline checkpoint.
        SourceOnlyResultCodec.Require(recorded.Kind == ArtifactKind.Analysis && recorded.ProjectId == execution.Request.ProjectId &&
            recorded.ByteSize is > 0 and <= SourceOnlyResultEvidenceStore.MaximumEvidenceBytes);
        await using ValidatedArtifactReadStream stream = await store.OpenValidatedReadAsync(id, ct).ConfigureAwait(false);
        var reference = SourceOnlyResultEvidenceStore.Reference(recorded);
        SourceOnlyResultEvidenceStore.RequireSame(stream, reference, execution.Request.ProjectId);
        byte[] bytes = new byte[checked((int)stream.ByteSize)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        using JsonDocument document = SourceOnlyResultCodec.ParseDocument(SourceOnlyResultCodec.Utf8.GetString(bytes),
            SourceOnlyResultEvidenceStore.MaximumEvidenceBytes, SourceOnlyResultEvidenceStore.MaximumNativeArrayLength,
            SourceOnlyResultEvidenceStore.MaximumEvidenceBytes);
        JsonElement payload = document.RootElement;
        SourceOnlyResultCodec.Require(payload.GetProperty("schema").GetString() == "source-only.generation-evidence.v1" &&
            payload.GetProperty("executionId").GetString() == execution.Request.ExecutionId.ToString() &&
            payload.GetProperty("settingsDigest").GetString() == execution.Request.SettingsSnapshot.ContentDigest.Hex &&
            payload.GetProperty("outputs").GetArrayLength() <= SourceOnlyResult.MaximumArtifactCount);
        return new(reference, payload.Clone());
    }

    internal void ValidateArtifact(SourceOnlyResultArtifact artifact, JsonElement graph, JsonElement manifest, SourceOnlyRenderingInputsPayload inputs)
    {
        JsonElement output = Payload.GetProperty("outputs").EnumerateArray().Single(o =>
            o.GetProperty("artifact").GetProperty("id").GetString() == artifact.ArtifactId.ToString());
        SourceOnlyResultCodec.Require(output.GetProperty("artifact").GetProperty("digest").GetString() == artifact.ArtifactDigest.Hex &&
            output.GetProperty("artifact").GetProperty("byteSize").GetString() == artifact.ByteSize.ToString(CultureInfo.InvariantCulture) &&
            output.GetProperty("role").GetString() == inputs.BindingEvidence.GetProperty("Role").GetString() &&
            output.GetProperty("version").GetString() == HubRadio.Core.Serialization.CanonicalJson.Canonicalize(inputs.Version.GetRawText()));
        SourceOnlyResultInputValidation.Same(output.GetProperty("graph"), graph);
        SourceOnlyResultInputValidation.Same(output.GetProperty("manifest"), manifest);
        SourceOnlyResultInputValidation.Same(output.GetProperty("binding"), inputs.BindingEvidence);
    }

    internal void ValidateSet(SourceOnlyResult result)
    {
        SourceOnlyResultCodec.Require(Payload.GetProperty("outputs").GetArrayLength() == result.Artifacts.Count &&
            (Payload.GetProperty("disposition").GetString() == result.GenerationDisposition.ToString() ||
                result.GenerationDisposition == HubRadio.App.Rendering.StandardArtifactGenerationDisposition.ReconciliationRequired));
        var expected = Payload.GetProperty("Warnings").EnumerateArray().Select(w => new SourceOnlyResultGenerationWarning(
            w.GetProperty("Role").GetString()!, w.GetProperty("Code").GetString()!)).Distinct().ToArray();
        SourceOnlyResultCodec.Require(expected.SequenceEqual(result.GenerationWarnings));
    }

    internal void ValidateGenerated(HubRadio.App.Rendering.StandardArtifactGenerationResult generated)
    {
        JsonElement[] outputs = Payload.GetProperty("outputs").EnumerateArray().ToArray();
        SourceOnlyResultCodec.Require(outputs.Length == generated.Artifacts.Count &&
            outputs.Select(o => o.GetProperty("artifact").GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal)
                .SetEquals(generated.Artifacts.Select(a => a.Artifact.Id.ToString())) &&
            Payload.GetProperty("disposition").GetString() == generated.Disposition.ToString());
        var warnings = Payload.GetProperty("Warnings").EnumerateArray().Select(w =>
            (Role: w.GetProperty("Role").GetString(), Code: w.GetProperty("Code").GetString()));
        SourceOnlyResultCodec.Require(warnings.SequenceEqual(generated.Warnings.Select(w => ((string?)w.Role, (string?)w.Code))));
    }
}