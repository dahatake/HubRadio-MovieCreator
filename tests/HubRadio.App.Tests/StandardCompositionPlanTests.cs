using System.Reflection;
using System.Text;
using HubRadio.App.Compositions;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Media;
using HubRadio.App.Profiles;
using HubRadio.Core.Analysis;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Media;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Settings;
using HubRadio.Core.Templates;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class StandardCompositionPlanTests
{
    [TestMethod]
    public void PlanRetainsOneLongFormAndDefensivelyCopiesGroundedOutputs()
    {
        Fixture fixture = new();
        var shorts = new List<StandardShortFormPlan>
        {
            fixture.CreateShort(1),
        };
        var thumbnails = new List<StandardThumbnailPlan>
        {
            fixture.CreateThumbnail(1),
        };
        var subtitleVariants = new List<StandardSubtitleVariantReference>
        {
            fixture.Subtitle(StandardSubtitleVariantKind.BurnedIn, "long-burned-in"),
            fixture.Subtitle(StandardSubtitleVariantKind.SubtitleFree, "long-clean"),
            fixture.Subtitle(StandardSubtitleVariantKind.SubRip, "long-srt"),
            fixture.Subtitle(StandardSubtitleVariantKind.WebVtt, "long-vtt"),
        };
        var warnings = new List<StandardCompositionWarning>
        {
            new(StandardCompositionWarningCode.PlatformUiUnmeasured),
        };
        var inputs = fixture.Inputs.ToList();

        ContentDigest planDigest = StandardCompositionCanonical.ComputePlanDigest(
            fixture.PlanId,
            fixture.LongForm,
            fixture.ShortFormProfile,
            shorts,
            exclusions: [],
            thumbnails,
            subtitleVariants,
            omissions:
            [
                new StandardArtifactOmission(
                    StandardCompositionArtifactKind.ShortForm,
                    StandardCompositionOmissionCode.NoAdditionalGroundedCandidate),
                new StandardArtifactOmission(
                    StandardCompositionArtifactKind.Thumbnail,
                    StandardCompositionOmissionCode
                        .NoAdditionalMeasuredSourceFrame),
            ],
            warnings,
            inputs);
        var plan = new StandardCompositionPlan(
            fixture.PlanId,
            fixture.LongForm,
            fixture.ShortFormProfile,
            fixture.ProjectStoreState,
            fixture.MusicPlanning,
            shorts,
            shortCandidateExclusions: [],
            thumbnails,
            subtitleVariants,
            omissions:
            [
                new StandardArtifactOmission(
                    StandardCompositionArtifactKind.ShortForm,
                    StandardCompositionOmissionCode.NoAdditionalGroundedCandidate),
                new StandardArtifactOmission(
                    StandardCompositionArtifactKind.Thumbnail,
                    StandardCompositionOmissionCode
                        .NoAdditionalMeasuredSourceFrame),
            ],
            warnings,
            inputs,
            planDigest);

        shorts.Clear();
        thumbnails.Clear();
        subtitleVariants.Clear();
        warnings.Clear();
        inputs.Clear();

        Assert.AreSame(fixture.LongForm, plan.LongForm);
        Assert.HasCount(1, plan.ShortForms);
        Assert.IsEmpty(plan.ShortCandidateExclusions);
        Assert.HasCount(1, plan.Thumbnails);
        Assert.HasCount(4, plan.LongFormSubtitleVariants);
        Assert.HasCount(2, plan.Omissions);
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoAdditionalGroundedCandidate,
            plan.Omissions.Single(static omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.ShortForm).Code);
        Assert.AreEqual(
            StandardCompositionOmissionCode.NoAdditionalMeasuredSourceFrame,
            plan.Omissions.Single(static omission => omission.ArtifactKind
                == StandardCompositionArtifactKind.Thumbnail).Code);
        Assert.HasCount(1, plan.Warnings);
        Assert.HasCount(17, plan.InputDigests);
        Assert.AreEqual(planDigest, plan.PlanDigest);
        Assert.AreEqual(fixture.Project.Id, plan.ProjectId);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardShortFormPlan>)plan.ShortForms).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardShortCandidateExclusion>)plan
                .ShortCandidateExclusions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardThumbnailPlan>)plan.Thumbnails).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardSubtitleVariantReference>)plan
                .LongFormSubtitleVariants).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardArtifactOmission>)plan.Omissions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardCompositionWarning>)plan.Warnings).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardPlanInputDigest>)plan.InputDigests).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<SourceSpan>)plan.ShortForms[0].SourceSpans).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<StandardSubtitleVariantReference>)plan.ShortForms[0]
                .SubtitleVariants).Clear());
    }

    [TestMethod]
    public void EmptyCandidateCollectionsRequireExactNonInventedOmissionReasons()
    {
        Fixture fixture = new();

        StandardArtifactOmission[] omissions =
        [
            new StandardArtifactOmission(
                StandardCompositionArtifactKind.ShortForm,
                StandardCompositionOmissionCode.NoGroundedCandidate),
            new StandardArtifactOmission(
                StandardCompositionArtifactKind.Thumbnail,
                StandardCompositionOmissionCode.NoMeasuredSourceFrame),
        ];
        ContentDigest planDigest = StandardCompositionCanonical.ComputePlanDigest(
            fixture.PlanId,
            fixture.LongForm,
            fixture.ShortFormProfile,
            shortForms: [],
            exclusions: [],
            thumbnails: [],
            fixture.LongSubtitles,
            omissions,
            warnings: [],
            fixture.Inputs);
        var plan = new StandardCompositionPlan(
            fixture.PlanId,
            fixture.LongForm,
            fixture.ShortFormProfile,
            fixture.ProjectStoreState,
            fixture.MusicPlanning,
            shortForms: [],
            shortCandidateExclusions: [],
            thumbnails: [],
            fixture.LongSubtitles,
            omissions,
            warnings: [],
            fixture.Inputs,
            planDigest);

        Assert.IsEmpty(plan.ShortForms);
        Assert.IsEmpty(plan.Thumbnails);
        CollectionAssert.AreEqual(
            new[]
            {
                StandardCompositionArtifactKind.ShortForm,
                StandardCompositionArtifactKind.Thumbnail,
            },
            plan.Omissions.Select(static reason => reason.ArtifactKind).ToArray());
        Assert.IsFalse(plan.Omissions.Any(static reason =>
            reason.Code == StandardCompositionOmissionCode.Unspecified));

        Assert.ThrowsExactly<ArgumentException>(() => new StandardCompositionPlan(
            fixture.PlanId,
            fixture.LongForm,
            fixture.ShortFormProfile,
            fixture.ProjectStoreState,
            fixture.MusicPlanning,
            shortForms: [],
            shortCandidateExclusions: [],
            thumbnails: [],
            fixture.LongSubtitles,
            omissions: [],
            warnings: [],
            fixture.Inputs,
            Digest("missing-reasons")));

        Assert.ThrowsExactly<ArgumentException>(() => new StandardCompositionPlan(
            fixture.PlanId,
            fixture.LongForm,
            fixture.ShortFormProfile,
            fixture.ProjectStoreState,
            fixture.MusicPlanning,
            [fixture.CreateShort(1)],
            shortCandidateExclusions: [],
            [fixture.CreateThumbnail(1)],
            fixture.LongSubtitles,
            omissions:
            [
                new StandardArtifactOmission(
                    StandardCompositionArtifactKind.ShortForm,
                    StandardCompositionOmissionCode.NoGroundedCandidate),
            ],
            warnings: [],
            fixture.Inputs,
            Digest("invented-reason")));
    }

    [TestMethod]
    public void ClosedPlanRejectsNullDuplicateOverLimitAndUndefinedValues()
    {
        Fixture fixture = new();
        StandardShortFormPlan firstShort = fixture.CreateShort(1);
        StandardThumbnailPlan firstThumbnail = fixture.CreateThumbnail(1);

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort, firstShort],
            thumbnails: [firstThumbnail]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(1), fixture.CreateShort(2)],
            thumbnails: [firstThumbnail]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [
                fixture.CreateShort(1),
                fixture.CreateShort(2),
                fixture.CreateShort(3),
                fixture.CreateShort(4),
            ],
            thumbnails: [firstThumbnail]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort],
            thumbnails:
            [
                fixture.CreateThumbnail(1),
                fixture.CreateThumbnail(2),
                fixture.CreateThumbnail(3),
                fixture.CreateThumbnail(4),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort],
            thumbnails: [firstThumbnail],
            inputs:
            [
                fixture.Input(StandardPlanInputKind.SourceAsset, "one"),
                fixture.Input(StandardPlanInputKind.SourceAsset, "two"),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort],
            thumbnails: [firstThumbnail],
            subtitleVariants:
            [
                fixture.Subtitle(StandardSubtitleVariantKind.BurnedIn, "one"),
                fixture.Subtitle(StandardSubtitleVariantKind.BurnedIn, "two"),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort],
            thumbnails: [firstThumbnail],
            subtitleVariants:
            [
                fixture.Subtitle(StandardSubtitleVariantKind.BurnedIn, "burned-in"),
                fixture.Subtitle(StandardSubtitleVariantKind.SubtitleFree, "clean"),
                fixture.Subtitle(StandardSubtitleVariantKind.SubRip, "srt-only"),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort],
            thumbnails: [firstThumbnail],
            subtitleVariants:
            [
                fixture.Subtitle(StandardSubtitleVariantKind.BurnedIn, "burned-in"),
                fixture.Subtitle(StandardSubtitleVariantKind.SubtitleFree, "clean"),
                fixture.Subtitle(StandardSubtitleVariantKind.WebVtt, "vtt-only"),
            ]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort],
            thumbnails: [firstThumbnail],
            longForm: fixture.CreateLongFormWithProfile(
                "src/HubRadio.App/Profiles/9x16/vertical-short.json")));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [null!],
            thumbnails: [firstThumbnail]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [firstShort],
            thumbnails: [null!]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StandardPlanInputDigest(
                (StandardPlanInputKind)int.MaxValue,
                Digest("input")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StandardSubtitleVariantReference(
                (StandardSubtitleVariantKind)int.MaxValue,
                Digest("subtitle")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StandardCompositionWarning(
                (StandardCompositionWarningCode)int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StandardArtifactOmission(
                (StandardCompositionArtifactKind)int.MaxValue,
                StandardCompositionOmissionCode.NoGroundedCandidate));

        var wrongTarget = new StandardCallToActionReference(
            Id<LongFormPlanIdTag>("10000000-0000-0000-0000-000000000099"),
            StandardCallToActionKind.ViewLongForm);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(9, wrongTarget)],
            thumbnails: [firstThumbnail]));
    }

    [TestMethod]
    public void PlanRequiresEveryClosedInputDigestAndBindsKnownInputs()
    {
        Fixture fixture = new();
        StandardShortFormPlan shortForm = fixture.CreateShort(1);
        StandardThumbnailPlan thumbnail = fixture.CreateThumbnail(1);

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [shortForm],
            thumbnails: [thumbnail],
            inputs: fixture.Inputs.Where(static input =>
                input.Kind != StandardPlanInputKind.MusicCatalog).ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [shortForm],
            thumbnails: [thumbnail],
            inputs: fixture.Inputs.Select(input =>
                input.Kind == StandardPlanInputKind.LongFormPlan
                    ? fixture.Input(StandardPlanInputKind.LongFormPlan, "forged")
                    : input).ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [shortForm],
            thumbnails: [thumbnail],
            inputs: fixture.Inputs.Select(input =>
                input.Kind == StandardPlanInputKind.TranscriptRevision
                    ? fixture.Input(
                        StandardPlanInputKind.TranscriptRevision,
                        "forged")
                    : input).ToArray()));
    }

    [TestMethod]
    public void PlanRejectsDigestThatDoesNotMatchCanonicalContent()
    {
        Fixture fixture = new();

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(1)],
            thumbnails: [fixture.CreateThumbnail(1)],
            planDigest: Digest("forged-standard-plan-digest")));
    }

    [TestMethod]
    public void ProfileBindingDigestSeparatesCapabilitiesFromUnmeasuredItems()
    {
        Fixture fixture = new();
        BundledRenderProfileDefinition source = fixture.ShortFormProfile;
        var first = new BundledRenderProfileDefinition(
            source.Profile,
            source.Layout,
            source.LayoutDigest,
            source.TemplateReference,
            source.PixelFormatEvidence,
            ["call-to-action"],
            source.PlatformSafeZonesMeasured,
            source.EvidenceSource,
            source.TemplateStatus,
            source.RequiredExternalBrandAssets,
            ["graphic", "material"]);
        var second = new BundledRenderProfileDefinition(
            source.Profile,
            source.Layout,
            source.LayoutDigest,
            source.TemplateReference,
            source.PixelFormatEvidence,
            ["call-to-action", "graphic"],
            source.PlatformSafeZonesMeasured,
            source.EvidenceSource,
            source.TemplateStatus,
            source.RequiredExternalBrandAssets,
            ["material"]);

        Assert.AreNotEqual(
            StandardCompositionCanonical.ComputeProfileBindingDigest(first),
            StandardCompositionCanonical.ComputeProfileBindingDigest(second));
    }

    [TestMethod]
    public void PlanRejectsNoncontiguousTokenSubsetInsideProvenanceSpan()
    {
        Fixture fixture = new();
        EntityId<TranscriptTokenIdTag> first = Id<TranscriptTokenIdTag>(
            "60000000-0000-0000-0000-000000000091");
        EntityId<TranscriptTokenIdTag> middle = Id<TranscriptTokenIdTag>(
            "60000000-0000-0000-0000-000000000092");
        EntityId<TranscriptTokenIdTag> last = Id<TranscriptTokenIdTag>(
            "60000000-0000-0000-0000-000000000093");
        var evidence = new SourceSpan(
            Id<SourceSpanIdTag>("50000000-0000-0000-0000-000000000091"),
            fixture.Asset.Id,
            fixture.SourceSpan.StreamId,
            new TimeRange(new RationalTime(5, 1), new RationalTime(30, 1)),
            fixture.Asset.ContentDigest,
            fixture.TranscriptRevisionId,
            [first, middle, last]);
        var noncontiguous = new SourceSpan(
            Id<SourceSpanIdTag>("50000000-0000-0000-0000-000000000092"),
            fixture.Asset.Id,
            fixture.SourceSpan.StreamId,
            new TimeRange(new RationalTime(10, 1), new RationalTime(15, 1)),
            fixture.Asset.ContentDigest,
            fixture.TranscriptRevisionId,
            [first, last]);

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(91, sourceSpan: noncontiguous)],
            thumbnails: [fixture.CreateThumbnail(1)],
            longForm: fixture.CreateLongFormWithSourceSpans([evidence])));
    }

    [TestMethod]
    public void PlanRejectsSameContentWithDifferentSpanIdsAndCrossShortOverlap()
    {
        Fixture fixture = new();
        SourceSpan sameContent = fixture.CloneSourceSpan(
            fixture.SourceSpan,
            "50000000-0000-0000-0000-000000000002");
        SourceSpan overlap = fixture.CreateSourceSpan(
            "50000000-0000-0000-0000-000000000003",
            fixture.Asset.Id,
            fixture.Asset.ContentDigest,
            fixture.TranscriptRevisionId,
            start: 20,
            duration: 20,
            tokenOrdinal: 3);

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms:
            [
                fixture.CreateShort(1),
                fixture.CreateShort(2, sourceSpan: sameContent),
            ],
            thumbnails: [fixture.CreateThumbnail(1)],
            longForm: fixture.CreateLongFormWithSourceSpans(
                [fixture.SourceSpan, sameContent])));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms:
            [
                fixture.CreateShort(1),
                fixture.CreateShort(3, sourceSpan: overlap),
            ],
            thumbnails: [fixture.CreateThumbnail(1)],
            longForm: fixture.CreateLongFormWithSourceSpans(
                [fixture.SourceSpan, overlap])));
    }

    [TestMethod]
    public void OneOrTwoShortsRequireTypedGroundedCandidateShortfall()
    {
        Fixture fixture = new();
        SourceSpan second = fixture.CreateSourceSpan(
            "50000000-0000-0000-0000-000000000006",
            fixture.Asset.Id,
            fixture.Asset.ContentDigest,
            fixture.TranscriptRevisionId,
            start: 40,
            duration: 15,
            tokenOrdinal: 6);
        LongFormCompositionPlan twoSpanLongForm =
            fixture.CreateLongFormWithSourceSpans([fixture.SourceSpan, second]);

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(1)],
            thumbnails: [fixture.CreateThumbnail(1)],
            omissions: []));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms:
            [
                fixture.CreateShort(1),
                fixture.CreateShort(2, sourceSpan: second),
            ],
            thumbnails: [fixture.CreateThumbnail(1)],
            omissions: [],
            longForm: twoSpanLongForm));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(1)],
            thumbnails: [fixture.CreateThumbnail(1)],
            omissions:
            [
                new StandardArtifactOmission(
                    StandardCompositionArtifactKind.ShortForm,
                    StandardCompositionOmissionCode.NoGroundedCandidate),
            ]));

        StandardCompositionPlan one = fixture.CreatePlan(
            shortForms: [fixture.CreateShort(1)],
            thumbnails: [fixture.CreateThumbnail(1)]);
        StandardCompositionPlan two = fixture.CreatePlan(
            shortForms:
            [
                fixture.CreateShort(1),
                fixture.CreateShort(2, sourceSpan: second),
            ],
            thumbnails: [fixture.CreateThumbnail(1)],
            longForm: twoSpanLongForm);

        Assert.HasCount(1, one.ShortForms);
        Assert.HasCount(2, two.ShortForms);
        Assert.IsTrue(new[] { one, two }.All(static plan =>
            plan.Omissions.Single(omission => omission.ArtifactKind
                    == StandardCompositionArtifactKind.ShortForm).Code
                == StandardCompositionOmissionCode.NoAdditionalGroundedCandidate));
    }

    [TestMethod]
    public void PlanRejectsShortLayoutThumbnailAndTranscriptOutsideLongFormContext()
    {
        Fixture fixture = new();
        EntityId<AssetIdTag> otherAssetId = Id<AssetIdTag>(
            "20000000-0000-0000-0000-000000000099");
        SourceSpan otherAssetSpan = fixture.CreateSourceSpan(
            "50000000-0000-0000-0000-000000000004",
            otherAssetId,
            fixture.Asset.ContentDigest,
            fixture.TranscriptRevisionId,
            start: 40,
            duration: 15,
            tokenOrdinal: 4);
        SourceSpan otherTranscriptSpan = fixture.CreateSourceSpan(
            "50000000-0000-0000-0000-000000000005",
            fixture.Asset.Id,
            fixture.Asset.ContentDigest,
            Id<TranscriptRevisionIdTag>(
                "40000000-0000-0000-0000-000000000099"),
            start: 40,
            duration: 15,
            tokenOrdinal: 5);
        ManualLayoutPlan otherLayout = fixture.CreateLayout(otherAssetId);
        var otherThumbnail = new StandardThumbnailPlan(
            Id<StandardThumbnailPlanIdTag>(
                "c0000000-0000-0000-0000-000000000099"),
            OriginRef.FromAsset(otherAssetId),
            sourceFrameSequence: 1,
            sourcePresentationTime: new RationalTime(1, 1));

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(4, sourceSpan: otherAssetSpan)],
            thumbnails: [fixture.CreateThumbnail(1)]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(5, sourceSpan: otherTranscriptSpan)],
            thumbnails: [fixture.CreateThumbnail(1)]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(6, layout: otherLayout)],
            thumbnails: [fixture.CreateThumbnail(1)]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(7)],
            thumbnails: [otherThumbnail]));
    }

    [TestMethod]
    public void ThumbnailRetainsNegativeSourcePresentationTimestamp()
    {
        Fixture fixture = new();
        var thumbnail = new StandardThumbnailPlan(
            Id<StandardThumbnailPlanIdTag>(
                "c0000000-0000-0000-0000-000000000099"),
            OriginRef.FromAsset(fixture.Asset.Id),
            sourceFrameSequence: 0,
            sourcePresentationTime: new RationalTime(-1, 20));

        Assert.AreEqual(
            new RationalTime(-1, 20),
            thumbnail.SourcePresentationTime);
    }

    [TestMethod]
    public void StandardLongFormRequiresExactProductionResolution()
    {
        Fixture fixture = new();

        Assert.ThrowsExactly<ArgumentException>(() => fixture.CreatePlan(
            shortForms: [fixture.CreateShort(1)],
            thumbnails: [fixture.CreateThumbnail(1)],
            longForm: fixture.CreateLongFormWithProfile(
                "src/HubRadio.App/Profiles/16x9/long-form.json",
                deriveProductionResolution: false)));
    }

    [TestMethod]
    public void PublicPlanSurfaceIsSealedGetOnlyAndPlannerOwned()
    {
        foreach (Type type in new[]
        {
            typeof(StandardProjectStoreState),
            typeof(StandardPlanInputDigest),
            typeof(StandardSubtitleVariantReference),
            typeof(StandardCallToActionReference),
            typeof(StandardShortHookReference),
            typeof(StandardShortFormPlan),
            typeof(StandardShortCandidateExclusion),
            typeof(StandardThumbnailFrameEvidence),
            typeof(StandardThumbnailEvidence),
            typeof(StandardThumbnailPlan),
            typeof(StandardArtifactOmission),
            typeof(StandardCompositionWarning),
            typeof(StandardCompositionPlan),
        })
        {
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        foreach (Type type in new[]
        {
            typeof(StandardPlanInputDigest),
            typeof(StandardSubtitleVariantReference),
            typeof(StandardCallToActionReference),
            typeof(StandardShortHookReference),
            typeof(StandardShortFormPlan),
            typeof(StandardShortCandidateExclusion),
            typeof(StandardThumbnailFrameEvidence),
            typeof(StandardThumbnailEvidence),
            typeof(StandardThumbnailPlan),
            typeof(StandardArtifactOmission),
            typeof(StandardCompositionWarning),
            typeof(StandardCompositionPlan),
        })
        {
            Assert.AreEqual(0, type.GetConstructors().Length, type.Name);
        }
    }

    private static ContentDigest Digest(string value) =>
        ContentDigest.Compute(Encoding.UTF8.GetBytes(value));

    private static EntityId<TTag> Id<TTag>(string value) =>
        EntityId<TTag>.Parse(value);

    private sealed class Fixture
    {
        internal Fixture()
        {
            Asset = new Asset(
                Id<AssetIdTag>("20000000-0000-0000-0000-000000000001"),
                "source.mp4",
                "video/mp4",
                Digest("source"),
                byteSize: 1234,
                new RightsRecord(
                    RightsStatus.Approved,
                    "evidence://internal-source",
                    "Internal use only",
                    externalPublicationAllowed: false));
            Project = new Project(
                Id<ProjectIdTag>("30000000-0000-0000-0000-000000000001"),
                "Source",
                "Internal reuse",
                "Internal viewers",
                PublicationScope.InternalOnly,
                deadline: null,
                [Asset],
                performers: []);
            TranscriptRevisionId = Id<TranscriptRevisionIdTag>(
                "40000000-0000-0000-0000-000000000001");
            SourceSpan = new SourceSpan(
                Id<SourceSpanIdTag>("50000000-0000-0000-0000-000000000001"),
                Asset.Id,
                "video:0",
                new TimeRange(new RationalTime(5, 1), new RationalTime(30, 1)),
                Asset.ContentDigest,
                TranscriptRevisionId,
                [Id<TranscriptTokenIdTag>(
                    "60000000-0000-0000-0000-000000000001")]);
            SettingsProfile = BundledDefaultSettingsProfileLoader.Load(
                File.ReadAllBytes(ResolveRepositoryPath(
                    "src/HubRadio.App/DefaultSettings/Profiles/1.0.0.json")));
            Settings = SettingsProfile.CreateSnapshot();
            ProjectStoreState = new StandardProjectStoreState(
                revision: 0,
                Digest("project-payload"),
                DateTimeOffset.UnixEpoch);
            MusicPlanning = new MusicPlanningService().Plan(
                MusicCatalog.CreateUnavailable(
                    MusicLibraryState.Absent,
                    MusicCatalogWarning.LibraryAbsent),
                cues: [],
                MusicPlanningOptions.Default);
            BundledRenderProfileDefinition measuredProfile =
                BundledRenderProfileLoader.Load(File.ReadAllBytes(
                    ResolveRepositoryPath(
                        "src/HubRadio.App/Profiles/16x9/long-form.json")));
            BundledRenderProfileDefinition profile =
                StandardRenderProfileFactory.CreateLandscape(
                    SettingsProfile,
                    Settings,
                    measuredProfile,
                    measuredProfile.Profile.FrameRate);
            BundledRenderProfileDefinition measuredVertical =
                BundledRenderProfileLoader.Load(File.ReadAllBytes(
                    ResolveRepositoryPath(
                        "src/HubRadio.App/Profiles/9x16/vertical-short.json")));
            ShortFormProfile =
                StandardRenderProfileFactory.CreateVerticalShort(
                    SettingsProfile,
                    Settings,
                    measuredVertical,
                    measuredVertical.Profile.FrameRate);
            AppliedBrandTemplate template = new BrandTemplateVersion(
                Id<SeriesIdTag>("70000000-0000-0000-0000-000000000001"),
                new TemplateReference(
                    profile.Profile.TemplateId,
                    "Standard-plan test template",
                    profile.Profile.TemplateVersionDigest),
                BrandTemplateKind.LongForm,
                parameterDefinitions: []).ApplyEditorOverrides([]);
            var provenance = new LongFormProvenanceSnapshot(
                Project.Id,
                TranscriptRevisionId,
                Digest("transcript"),
                [SourceSpan],
                trustedEditorialRecordIds: []);
            LongForm = new LongFormCompositionPlan(
                Id<LongFormPlanIdTag>("80000000-0000-0000-0000-000000000001"),
                Project,
                provenance,
                template,
                profile,
                LongFormDestination.InternalDistribution,
                speakerAssignments: null,
                elements: [],
                templateAssets: [],
                referencedAssets: [Asset],
                warnings: [],
                timingComplete: true,
                externalRightsReady: false,
                externalPublicationReady: false,
                Digest("long-form"));
            NormalizedRect fullFrame = Rect(0, 1, 0, 1, 1, 1, 1, 1);
            NormalizedRect centeredHalf = Rect(1, 4, 1, 4, 1, 2, 1, 2);
            var source = new ManualLayoutSource(
                Asset.Id,
                OriginRef.FromAsset(Asset.Id),
                new SourceDisplayGeometry(900, 1600),
                new ManualSourceCrop(fullFrame),
                new ManualSubjectRegion(fullFrame));
            var outputEnvelope = new LayoutSpec(
                Id<LayoutSpecIdTag>(
                    "90000000-0000-0000-0000-000000000001"),
                9,
                16,
                regions: [],
                safeZones: [new SafeZone("output-frame-bounds", fullFrame)]);
            ManualLayoutResolution layoutResolution = ManualLayoutResolver.Resolve(
                ManualLayoutRequest.BlurredSourceBackground(
                    Id<LayoutSpecIdTag>(
                        "90000000-0000-0000-0000-000000000002"),
                    outputEnvelope,
                    ManualStaticCropDecision.SingleStaticCropSufficient,
                    source,
                    new ManualDestinationGeometry(fullFrame),
                    source,
                    new ManualDestinationGeometry(centeredHalf)));
            Layout = layoutResolution.Plan
                ?? throw new InvalidOperationException(
                    "The fixed test layout must resolve without keyframes.");
            PlanId = Id<StandardCompositionPlanIdTag>(
                "a0000000-0000-0000-0000-000000000001");
            LongSubtitles =
            [
                Subtitle(StandardSubtitleVariantKind.BurnedIn, "long-burned-in"),
                Subtitle(StandardSubtitleVariantKind.SubtitleFree, "long-clean"),
                Subtitle(StandardSubtitleVariantKind.SubRip, "long-srt"),
                Subtitle(StandardSubtitleVariantKind.WebVtt, "long-vtt"),
            ];
            Inputs =
            [
                new StandardPlanInputDigest(
                    StandardPlanInputKind.SourceAsset,
                    Asset.ContentDigest),
                new StandardPlanInputDigest(
                    StandardPlanInputKind.SourceFrameRate,
                    StandardCompositionCanonical.ComputeDigest(
                        "hubradio.standard-source-frame-rate.v1",
                        [
                            LongForm.Profile.Profile.FrameRateNumerator.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            LongForm.Profile.Profile.FrameRateDenominator.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        ])),
                new StandardPlanInputDigest(
                    StandardPlanInputKind.ProjectSnapshot,
                    StandardCompositionCanonical.ComputeProjectSnapshotDigest(
                        Project)),
                new StandardPlanInputDigest(
                    StandardPlanInputKind.ProjectStoreState,
                    StandardCompositionCanonical.ComputeProjectStoreStateDigest(
                        Project,
                        ProjectStoreState)),
                new StandardPlanInputDigest(
                    StandardPlanInputKind.TranscriptRevision,
                    LongForm.Provenance.TranscriptRevisionDigest),
                Input(StandardPlanInputKind.TimelineMap, "timeline-map"),
                Input(StandardPlanInputKind.SettingsProfile, "settings-profile"),
                Input(StandardPlanInputKind.SettingsSnapshot, "settings"),
                Input(StandardPlanInputKind.AutoDecision, "auto-decision"),
                new StandardPlanInputDigest(
                    StandardPlanInputKind.LongFormPlan,
                    LongForm.PlanDigest),
                Input(StandardPlanInputKind.Layout, "layout"),
                Input(StandardPlanInputKind.Tool, "toolchain"),
                Input(StandardPlanInputKind.Model, "model-set"),
                new StandardPlanInputDigest(
                    StandardPlanInputKind.MusicCatalog,
                    MusicPlanning.CatalogDigest),
                new StandardPlanInputDigest(
                    StandardPlanInputKind.MusicPlanning,
                    StandardCompositionCanonical.ComputeMusicPlanningDigest(
                        MusicPlanning)),
                Input(
                    StandardPlanInputKind.ThumbnailEvidence,
                    "thumbnail-evidence-absence"),
                Input(StandardPlanInputKind.EvaluationInput, "evaluation-input"),
            ];
        }

        internal Asset Asset { get; }

        internal Project Project { get; }

        internal EntityId<TranscriptRevisionIdTag> TranscriptRevisionId { get; }

        internal SourceSpan SourceSpan { get; }

        internal DefaultSettingsProfile SettingsProfile { get; }

        internal DefaultSettingsSnapshot Settings { get; }

        internal StandardProjectStoreState ProjectStoreState { get; }

        internal MusicPlanningResult MusicPlanning { get; }

        internal LongFormCompositionPlan LongForm { get; }

        internal BundledRenderProfileDefinition ShortFormProfile { get; }

        internal ManualLayoutPlan Layout { get; }

        internal EntityId<StandardCompositionPlanIdTag> PlanId { get; }

        internal IReadOnlyList<StandardSubtitleVariantReference> LongSubtitles
        { get; }

        internal IReadOnlyList<StandardPlanInputDigest> Inputs { get; }

        internal StandardCompositionPlan CreatePlan(
            IReadOnlyList<StandardShortFormPlan> shortForms,
            IReadOnlyList<StandardThumbnailPlan> thumbnails,
            IReadOnlyList<StandardSubtitleVariantReference>? subtitleVariants = null,
            IReadOnlyList<StandardPlanInputDigest>? inputs = null,
            LongFormCompositionPlan? longForm = null,
            IReadOnlyList<StandardArtifactOmission>? omissions = null,
            ContentDigest? planDigest = null)
        {
            LongFormCompositionPlan resolvedLongForm = longForm ?? LongForm;
            IReadOnlyList<StandardSubtitleVariantReference> resolvedSubtitles =
                subtitleVariants ?? LongSubtitles;
            IReadOnlyList<StandardArtifactOmission> resolvedOmissions =
                omissions ?? CreateDefaultOmissions(shortForms, thumbnails);
            IReadOnlyList<StandardPlanInputDigest> resolvedInputs = inputs ?? Inputs;
            ContentDigest resolvedDigest = planDigest
                ?? (shortForms.Any(static value => value is null)
                    || thumbnails.Any(static value => value is null)
                        ? Digest("invalid-plan")
                        : StandardCompositionCanonical.ComputePlanDigest(
                            PlanId,
                            resolvedLongForm,
                            ShortFormProfile,
                            shortForms,
                            exclusions: [],
                            thumbnails,
                            resolvedSubtitles,
                            resolvedOmissions,
                            warnings: [],
                            resolvedInputs));
            return new StandardCompositionPlan(
                PlanId,
                resolvedLongForm,
                ShortFormProfile,
                ProjectStoreState,
                MusicPlanning,
                shortForms,
                shortCandidateExclusions: [],
                thumbnails,
                resolvedSubtitles,
                resolvedOmissions,
                warnings: [],
                resolvedInputs,
                resolvedDigest);
        }

        internal LongFormCompositionPlan CreateLongFormWithSourceSpans(
            IReadOnlyList<SourceSpan> sourceSpans)
        {
            var provenance = new LongFormProvenanceSnapshot(
                Project.Id,
                TranscriptRevisionId,
                LongForm.Provenance.TranscriptRevisionDigest,
                sourceSpans,
                trustedEditorialRecordIds: []);
            return new LongFormCompositionPlan(
                LongForm.PlanId,
                Project,
                provenance,
                LongForm.Template,
                LongForm.Profile,
                LongForm.Destination,
                LongForm.SpeakerAssignments,
                LongForm.Elements,
                LongForm.TemplateAssets,
                LongForm.ReferencedAssets,
                LongForm.Warnings,
                LongForm.TimingComplete,
                LongForm.ExternalRightsReady,
                LongForm.ExternalPublicationReady,
                LongForm.PlanDigest);
        }

        internal LongFormCompositionPlan CreateLongFormWithProfile(
            string relativeProfilePath,
            bool deriveProductionResolution = true)
        {
            BundledRenderProfileDefinition profile =
                BundledRenderProfileLoader.Load(File.ReadAllBytes(
                    ResolveRepositoryPath(relativeProfilePath)));
            if (deriveProductionResolution
                && profile.Layout.OutputAspectWidth == 16
                && profile.Layout.OutputAspectHeight == 9)
            {
                profile = StandardRenderProfileFactory.CreateLandscape(
                    SettingsProfile,
                    Settings,
                    profile,
                    profile.Profile.FrameRate);
            }

            AppliedBrandTemplate template = new BrandTemplateVersion(
                Id<SeriesIdTag>("70000000-0000-0000-0000-000000000002"),
                new TemplateReference(
                    profile.Profile.TemplateId,
                    "Standard-plan alternate test template",
                    profile.Profile.TemplateVersionDigest),
                BrandTemplateKind.LongForm,
                parameterDefinitions: []).ApplyEditorOverrides([]);
            return new LongFormCompositionPlan(
                LongForm.PlanId,
                Project,
                LongForm.Provenance,
                template,
                profile,
                LongForm.Destination,
                LongForm.SpeakerAssignments,
                LongForm.Elements,
                LongForm.TemplateAssets,
                LongForm.ReferencedAssets,
                LongForm.Warnings,
                LongForm.TimingComplete,
                LongForm.ExternalRightsReady,
                LongForm.ExternalPublicationReady,
                LongForm.PlanDigest);
        }

        internal StandardShortFormPlan CreateShort(
            int ordinal,
            StandardCallToActionReference? callToAction = null,
            SourceSpan? sourceSpan = null,
            ManualLayoutPlan? layout = null) =>
            new(
                Id<StandardShortFormPlanIdTag>(
                    $"b0000000-0000-0000-0000-{ordinal:000000000000}"),
                [sourceSpan ?? SourceSpan],
                StandardShortSelectionKind.GroundedCandidate,
                SuggestionReference.FromPersistedReference(
                    Id<SuggestionIdTag>(
                        $"d0000000-0000-0000-0000-{ordinal:000000000000}"),
                    OriginRef.FromSourceSpan((sourceSpan ?? SourceSpan).Id)),
                "Grounded test selection.",
                "Test context may be incomplete.",
                "Review the test candidate.",
                hook: null,
                layout ?? Layout,
                callToAction ?? new StandardCallToActionReference(
                    LongForm.PlanId,
                    StandardCallToActionKind.ViewLongForm),
                [Subtitle(
                    StandardSubtitleVariantKind.BurnedIn,
                    $"short-{ordinal}-burned-in")]);

        internal StandardThumbnailPlan CreateThumbnail(int ordinal) =>
            new(
                Id<StandardThumbnailPlanIdTag>(
                    $"c0000000-0000-0000-0000-{ordinal:000000000000}"),
                OriginRef.FromAsset(Asset.Id),
                sourceFrameSequence: ordinal,
                sourcePresentationTime: new RationalTime(ordinal, 1));

        internal SourceSpan CloneSourceSpan(SourceSpan source, string id) =>
            new(
                Id<SourceSpanIdTag>(id),
                source.AssetId,
                source.StreamId,
                source.Range,
                source.SourceDigest,
                source.TranscriptRevisionId,
                source.TranscriptTokenIds);

        internal SourceSpan CreateSourceSpan(
            string id,
            EntityId<AssetIdTag> assetId,
            ContentDigest sourceDigest,
            EntityId<TranscriptRevisionIdTag> transcriptRevisionId,
            long start,
            long duration,
            int tokenOrdinal) =>
            new(
                Id<SourceSpanIdTag>(id),
                assetId,
                "video:0",
                new TimeRange(
                    new RationalTime(start, 1),
                    new RationalTime(duration, 1)),
                sourceDigest,
                transcriptRevisionId,
                [Id<TranscriptTokenIdTag>(
                    $"60000000-0000-0000-0000-{tokenOrdinal:000000000000}")]);

        internal ManualLayoutPlan CreateLayout(
            EntityId<AssetIdTag> sourceAssetId)
        {
            NormalizedRect fullFrame = Rect(0, 1, 0, 1, 1, 1, 1, 1);
            NormalizedRect centeredHalf = Rect(1, 4, 1, 4, 1, 2, 1, 2);
            var source = new ManualLayoutSource(
                sourceAssetId,
                OriginRef.FromAsset(sourceAssetId),
                new SourceDisplayGeometry(900, 1600),
                new ManualSourceCrop(fullFrame),
                new ManualSubjectRegion(fullFrame));
            var outputEnvelope = new LayoutSpec(
                EntityId<LayoutSpecIdTag>.New(),
                9,
                16,
                regions: [],
                safeZones: [new SafeZone("output-frame-bounds", fullFrame)]);
            return ManualLayoutResolver.Resolve(
                    ManualLayoutRequest.BlurredSourceBackground(
                        EntityId<LayoutSpecIdTag>.New(),
                        outputEnvelope,
                        ManualStaticCropDecision.SingleStaticCropSufficient,
                        source,
                        new ManualDestinationGeometry(fullFrame),
                        source,
                        new ManualDestinationGeometry(centeredHalf)))
                .Plan
                ?? throw new InvalidOperationException(
                    "The test layout must resolve without keyframes.");
        }

        internal StandardSubtitleVariantReference Subtitle(
            StandardSubtitleVariantKind kind,
            string digestSeed) =>
            new(kind, Digest(digestSeed));

        internal StandardPlanInputDigest Input(
            StandardPlanInputKind kind,
            string digestSeed) =>
            new(kind, Digest(digestSeed));

        private static IReadOnlyList<StandardArtifactOmission>
            CreateDefaultOmissions(
                IReadOnlyList<StandardShortFormPlan> shortForms,
                IReadOnlyList<StandardThumbnailPlan> thumbnails)
        {
            var omissions = new List<StandardArtifactOmission>();
            if (shortForms.Count is > 0
                and < StandardCompositionPlan.MaximumShortFormCount)
            {
                omissions.Add(
                    new StandardArtifactOmission(
                        StandardCompositionArtifactKind.ShortForm,
                        StandardCompositionOmissionCode
                            .NoAdditionalGroundedCandidate));
            }

            if (thumbnails.Count is > 0
                and < StandardCompositionPlan.MaximumThumbnailCount)
            {
                omissions.Add(new StandardArtifactOmission(
                    StandardCompositionArtifactKind.Thumbnail,
                    StandardCompositionOmissionCode
                        .NoAdditionalMeasuredSourceFrame));
            }

            return omissions;
        }

        private static NormalizedRect Rect(
            long xNumerator,
            long xDenominator,
            long yNumerator,
            long yDenominator,
            long widthNumerator,
            long widthDenominator,
            long heightNumerator,
            long heightDenominator) =>
            new(
                new RationalNumber(xNumerator, xDenominator),
                new RationalNumber(yNumerator, yDenominator),
                new RationalNumber(widthNumerator, widthDenominator),
                new RationalNumber(heightNumerator, heightDenominator));

        private static string ResolveRepositoryPath(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
                {
                    return Path.Combine(
                        directory.FullName,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not resolve repository-relative path '{relativePath}'.");
        }
    }
}