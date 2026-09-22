using HubRadio.App.Delivery;
using HubRadio.App.Projects;

namespace HubRadio.App.Tests;

/// <summary>
/// Pins the source-only delivery surface so that any later change to the
/// delivered ancillary artifact set is visible as a deliberate contract change
/// rather than an accidental one.
/// </summary>
[TestClass]
public sealed class SourceOnlyAncillaryContractTests
{
    [TestMethod]
    public void SourceOnlyResultRoleContainsOnlyTheSevenStandardVideoAndSidecarRoles()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                SourceOnlyResultRole.LandscapeClean,
                SourceOnlyResultRole.LandscapeBurnIn,
                SourceOnlyResultRole.VerticalBurnIn,
                SourceOnlyResultRole.SubRip,
                SourceOnlyResultRole.WebVtt,
                SourceOnlyResultRole.LandscapeThumbnail,
                SourceOnlyResultRole.VerticalThumbnail,
            },
            Enum.GetValues<SourceOnlyResultRole>());
    }

    [TestMethod]
    public void AncillaryArtifactKindDeclaresAllSixExportableKinds()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                AncillaryArtifactKind.Transcript,
                AncillaryArtifactKind.Chapters,
                AncillaryArtifactKind.PublishingText,
                AncillaryArtifactKind.EditingInstructions,
                AncillaryArtifactKind.ReviewEvidence,
                AncillaryArtifactKind.UsedSourceRanges,
            },
            Enum.GetValues<AncillaryArtifactKind>());
    }

    /// <summary>
    /// The delivered evidence kinds are the contract that decides whether the
    /// standard set carries the transcript and the used-source-range list.
    /// Update this list only together with the codec and the delivery service.
    /// </summary>
    [TestMethod]
    public void SourceOnlyResultEvidenceKindCarriesTheTwoGroundedAncillaryKinds()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                SourceOnlyResultEvidenceKind.Graph,
                SourceOnlyResultEvidenceKind.Manifest,
                SourceOnlyResultEvidenceKind.RenderingInputs,
                SourceOnlyResultEvidenceKind.QualityControl,
                SourceOnlyResultEvidenceKind.DeliveryReceipt,
                SourceOnlyResultEvidenceKind.Transcript,
                SourceOnlyResultEvidenceKind.UsedSourceRanges,
            },
            Enum.GetValues<SourceOnlyResultEvidenceKind>());
    }

    [TestMethod]
    public void SourceOnlyDeliversExactlyTheTranscriptAndUsedSourceRanges()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                AncillaryArtifactKind.Transcript,
                AncillaryArtifactKind.UsedSourceRanges,
            },
            SourceOnlyPipeline.AncillaryKinds);
        Assert.AreEqual(
            SourceOnlyResultEvidenceKind.Transcript,
            SourceOnlyPipeline.AncillaryEvidenceKind(AncillaryArtifactKind.Transcript));
        Assert.AreEqual(
            SourceOnlyResultEvidenceKind.UsedSourceRanges,
            SourceOnlyPipeline.AncillaryEvidenceKind(AncillaryArtifactKind.UsedSourceRanges));

        foreach (AncillaryArtifactKind excluded in new[]
        {
            AncillaryArtifactKind.Chapters,
            AncillaryArtifactKind.PublishingText,
            AncillaryArtifactKind.EditingInstructions,
            AncillaryArtifactKind.ReviewEvidence,
        })
        {
            Assert.IsFalse(SourceOnlyPipeline.AncillaryKinds.Contains(excluded));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => SourceOnlyPipeline.AncillaryEvidenceKind(excluded));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => SourceOnlyPipeline.AncillaryOmissionRole(excluded));
        }
    }

    /// <summary>
    /// A kind that cannot be written must name itself in the manifest, which is
    /// what FR-031 requires instead of an empty artifact.
    /// </summary>
    [TestMethod]
    public void EveryAncillaryKindHasItsOwnOmissionRole()
    {
        Assert.AreEqual(
            SourceOnlyResultOmissionRole.Transcript,
            SourceOnlyPipeline.AncillaryOmissionRole(AncillaryArtifactKind.Transcript));
        Assert.AreEqual(
            SourceOnlyResultOmissionRole.UsedSourceRanges,
            SourceOnlyPipeline.AncillaryOmissionRole(AncillaryArtifactKind.UsedSourceRanges));
        Assert.AreEqual(
            SourceOnlyPipeline.AncillaryKinds.Length,
            SourceOnlyPipeline.AncillaryKinds
                .Select(SourceOnlyPipeline.AncillaryOmissionRole).Distinct().Count());
    }
}
