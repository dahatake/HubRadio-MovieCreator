using HubRadio.App.Projects;
using HubRadio.Core.Evaluation;
using HubRadio.Core.Identity;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Tests;

/// <summary>
/// Delivery of a result that carries the two ancillary artifacts (transcript / used source ranges) through the real
/// SQLite database, the real artifact store and the real <see cref="SourceOnlyDeliveryService"/>, then reload.
/// This does NOT run the real FFmpeg/ASR pipeline: rendered media and OutputQc come from the fixture's synthetic
/// seams, and the ancillary files are synthetic bytes. It is not a substitute for real-media acceptance.
/// Out of scope: rejection of spans of derived candidates (a separate defect class).
/// </summary>
[TestClass]
public sealed class SourceOnlyDeliveryIntegrationTests
{
    [TestMethod]
    public async Task AncillaryArtifactsSurviveDeliveryAndReloadAndAreBoundByTheReceipt()
    {
        using SourceOnlyEvaluationFixture f = await SourceOnlyEvaluationFixture.CreateAsync(includeShort: true, includeThumbnail: true);
        await f.SaveGeneratorCheckpointAsync();
        SourceOnlyResult evaluated = await f.EvaluateAsync();
        Assert.IsFalse(evaluated.HasBlockingFailures);
        SourceOnlyResultEvidenceReference[] ancillary =
        [
            await PromoteAsync(f, evaluated, SourceOnlyResultEvidenceKind.Transcript, "transcript.json", """{"utterances":[]}"""),
            await PromoteAsync(f, evaluated, SourceOnlyResultEvidenceKind.UsedSourceRanges, "used-source-ranges.json", """{"ranges":[]}"""),
        ];
        SourceOnlyResult basis = SourceOnlyPipeline.BindAncillaryResult(evaluated, ancillary, evaluated.Omissions);
        StoredSourceOnlyExecution execution = f.PersistResult(basis);
        var delivery = new SourceOnlyDeliveryService(f.Generation.DatabasePath, f.Generation.Root, f.Generation.Store, f.Service,
            f.Workflows, static (input, output, ct) => input.CopyToAsync(output, ct), null, () => SourceOnlyEvaluationFixture.Now.AddMinutes(2));

        SourceOnlyResult delivered = await SourceOnlyPipeline.DeliverResultAsync(execution, basis, delivery.DeliverAsync,
            (value, ct) => f.Service.ValidateAsync(value, execution, ct), CancellationToken.None);

        Assert.IsTrue(delivered.Artifacts.All(a => a.Delivered));
        CollectionAssert.AreEqual(ancillary, delivered.AncillaryArtifacts.ToArray());

        // The receipt does not list ancillary artifacts itself. It binds them through BaseResultDigest, which hashes the
        // exact pre-delivery result that names them; the delivered result must reduce back to that same result.
        SourceOnlyDeliveryReceipt receipt;
        await using (ValidatedArtifactReadStream stream = await f.Generation.Store.OpenValidatedReadAsync(
            delivered.Artifacts[0].Delivery!.Receipt.Artifact.ArtifactId))
        {
            receipt = await SourceOnlyDeliveryReceipt.ReadAsync(stream, CancellationToken.None);
        }
        Assert.AreEqual(basis.CanonicalDigest.Hex, receipt.BaseResultDigest);
        SourceOnlyResult reduced = SourceOnlyDeliveryReceipt.WithoutDelivery(delivered);
        Assert.AreEqual(receipt.BaseResultDigest, reduced.CanonicalDigest.Hex);
        CollectionAssert.AreEqual(ancillary, reduced.AncillaryArtifacts.ToArray());

        StoredSourceOnlyExecution persisted = f.PersistResult(delivered);
        var restarted = new SourceOnlyResultService(new ArtifactStore(f.Generation.DatabasePath, f.Generation.Root),
            new AutomatedEvaluationStore(f.Generation.DatabasePath), new QuickStartWorkflowStore(f.Generation.DatabasePath),
            new ProjectStore(f.Generation.DatabasePath));
        SourceOnlyResultLoadResult reopened = await restarted.LoadAsync(
            new SourceOnlyExecutionStore(f.Generation.DatabasePath).Get(persisted.Request.ExecutionId)!);

        Assert.IsTrue(reopened.Validation.IsValid, string.Join(",", reopened.Validation.Failures.Select(x => x.Code)));
        Assert.AreEqual(delivered.CanonicalJson, reopened.Result.CanonicalJson);
        CollectionAssert.AreEqual(ancillary, reopened.Result.AncillaryArtifacts.ToArray());

        // Reload must re-verify the ancillary bytes, not only the result JSON bound by the receipt.
        string tampered = f.Generation.Store.GetValidatedRegisteredArtifactPath(ancillary[0].Artifact.ArtifactId);
        byte[] bytes = await File.ReadAllBytesAsync(tampered);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(tampered, bytes);
        SourceOnlyResultLoadResult damaged = await restarted.LoadAsync(
            new SourceOnlyExecutionStore(f.Generation.DatabasePath).Get(persisted.Request.ExecutionId)!);
        Assert.IsFalse(damaged.Validation.IsValid);
        Assert.IsTrue(damaged.Validation.Failures.Any(x => x.Code == SourceOnlyResultFailureCode.StoredEvidenceInvalid
            && x.ArtifactId == ancillary[0].Artifact.ArtifactId));
    }

    private static async Task<SourceOnlyResultEvidenceReference> PromoteAsync(SourceOnlyEvaluationFixture f, SourceOnlyResult result,
        SourceOnlyResultEvidenceKind kind, string name, string json)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        EntityId<ArtifactIdTag> id = EntityId<ArtifactIdTag>.New();
        string staging = Path.Combine(f.Generation.Root, "projects", result.Binding.ProjectId.ToString(), "staging", id + "-" + name);
        await File.WriteAllBytesAsync(staging, bytes);
        StoredArtifact stored = await f.Generation.Store.PromoteAsync(new ArtifactPromotionRequest(id, result.Binding.ProjectId,
            ArtifactKind.Delivery, staging, $"ancillary/{result.Binding.ExecutionId}/{result.Binding.RootJobId}/{name}",
            ContentDigest.Compute(bytes), bytes.LongLength, SourceOnlyEvaluationFixture.Now));
        return new(kind, new SourceOnlyResultArtifactReference(stored.Id, stored.ContentDigest, stored.ByteSize, ArtifactKind.Delivery));
    }
}
