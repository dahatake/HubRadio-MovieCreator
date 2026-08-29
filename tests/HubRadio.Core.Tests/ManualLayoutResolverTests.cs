using System.Reflection;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ManualLayoutResolverTests
{
    private const string DigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void ContractsExposeExactlySevenModesAndModeSpecificFactories()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "SingleSubjectCrop",
                "TwoSpeakerStack",
                "SpeakerMaterialStack",
                "MaterialWithSpeakerInset",
                "ImportantRegionZoom",
                "BlurredSourceBackground",
                "FixedTemplatePlacement",
            },
            Enum.GetNames<ManualLayoutMode>());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 7).ToArray(),
            Enum.GetValues<ManualLayoutMode>()
                .Select(static value => Convert.ToInt32(value))
                .ToArray());

        ConstructorInfo[] requestConstructors = typeof(ManualLayoutRequest).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.AreEqual(1, requestConstructors.Length);
        Assert.IsTrue(requestConstructors[0].IsPrivate);

        MethodInfo[] factories = typeof(ManualLayoutRequest)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            Enum.GetNames<ManualLayoutMode>().OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            factories.Select(static method => method.Name).ToArray());
        Assert.IsTrue(factories.All(static method => method.ReturnType == typeof(ManualLayoutRequest)));
        Assert.AreEqual(
            1,
            factories.Count(static method => method.GetParameters().Any(
                static parameter => parameter.ParameterType == typeof(TemplateReference))));
        Assert.AreEqual(
            nameof(ManualLayoutRequest.FixedTemplatePlacement),
            factories.Single(static method => method.GetParameters().Any(
                static parameter => parameter.ParameterType == typeof(TemplateReference))).Name);

        AssertPublicGetOnlyProperties(
            typeof(ManualLayoutRequest),
            "Mode",
            "DerivedLayoutSpecId",
            "OutputEnvelope",
            "StaticCropDecision");
        AssertPublicGetOnlyProperties(
            typeof(ManualLayoutPlan),
            "Mode",
            "DerivedLayoutSpec",
            "SourcePlacements",
            "TemplateReference");
        AssertPublicGetOnlyProperties(
            typeof(ManualLayoutResolution),
            "Mode",
            "Status",
            "Plan");
        AssertPublicGetOnlyProperties(
            typeof(ManualSourcePlacement),
            "SourceAssetId",
            "Origin",
            "SourceDisplayGeometry",
            "Crop",
            "SubjectRegion",
            "Destination",
            "SemanticRole",
            "Treatment");
    }

    [TestMethod]
    public void SingleSubjectCropBuildsFreshPortraitLayoutAndPreservesEnvelopeBounds()
    {
        LayoutSpec envelope = CreateEnvelope();
        EntityId<LayoutSpecIdTag> derivedId = EntityId<LayoutSpecIdTag>.New();
        ManualLayoutSource source = CreateSource(PortraitCrop());

        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.SingleSubjectCrop(
            derivedId,
            envelope,
            ManualStaticCropDecision.SingleStaticCropSufficient,
            source,
            Destination(FullRect())));

        Assert.AreEqual(ManualLayoutMode.SingleSubjectCrop, plan.Mode);
        Assert.AreEqual(derivedId, plan.DerivedLayoutSpec.Id);
        Assert.AreNotEqual(envelope.Id, plan.DerivedLayoutSpec.Id);
        Assert.AreEqual(9, plan.DerivedLayoutSpec.OutputAspectWidth);
        Assert.AreEqual(16, plan.DerivedLayoutSpec.OutputAspectHeight);
        Assert.AreEqual(1, plan.DerivedLayoutSpec.SafeZones.Count);
        Assert.AreEqual("output-frame-bounds", plan.DerivedLayoutSpec.SafeZones[0].Name);
        Assert.AreEqual(envelope.SafeZones[0], plan.DerivedLayoutSpec.SafeZones[0]);
        Assert.AreEqual("primary-video", envelope.Regions[0].Name);
        Assert.AreEqual(2, envelope.Regions.Count);
        AssertPlacement(
            plan.SourcePlacements[0],
            source,
            ManualDestinationSlot.SingleSubject,
            ManualSourceSemanticRole.Subject,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);
        Assert.IsTrue(plan.DerivedLayoutSpec.Regions.Contains(
            plan.SourcePlacements[0].Destination.Region));
        Assert.IsNull(plan.TemplateReference);
    }

    [TestMethod]
    public void TwoSpeakerStackPreservesTopBottomOrderWithoutGapOrOverlap()
    {
        ManualLayoutSource top = CreateSource(StackCrop());
        ManualLayoutSource bottom = CreateSource(StackCrop());

        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.TwoSpeakerStack(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            top,
            Destination(TopHalf()),
            bottom,
            Destination(BottomHalf())));

        Assert.AreEqual(ManualLayoutMode.TwoSpeakerStack, plan.Mode);
        Assert.AreEqual(2, plan.SourcePlacements.Count);
        AssertPlacement(
            plan.SourcePlacements[0],
            top,
            ManualDestinationSlot.TopSpeaker,
            ManualSourceSemanticRole.Speaker,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);
        AssertPlacement(
            plan.SourcePlacements[1],
            bottom,
            ManualDestinationSlot.BottomSpeaker,
            ManualSourceSemanticRole.Speaker,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);
        Assert.AreEqual(
            default(RationalNumber),
            plan.SourcePlacements[0].Destination.Region.Rect.Y);
        Assert.AreEqual(
            new RationalNumber(1, 2),
            plan.SourcePlacements[0].Destination.Region.Rect.Height);
        Assert.AreEqual(
            new RationalNumber(1, 2),
            plan.SourcePlacements[1].Destination.Region.Rect.Y);
    }

    [TestMethod]
    public void SpeakerMaterialStackFixesSpeakerTopAndMaterialBottomRoles()
    {
        ManualLayoutSource speaker = CreateSource(StackCrop());
        ManualLayoutSource material = CreateSource(StackCrop());

        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.SpeakerMaterialStack(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            speaker,
            Destination(TopHalf()),
            material,
            Destination(BottomHalf())));

        AssertPlacement(
            plan.SourcePlacements[0],
            speaker,
            ManualDestinationSlot.StackedSpeaker,
            ManualSourceSemanticRole.Speaker,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);
        AssertPlacement(
            plan.SourcePlacements[1],
            material,
            ManualDestinationSlot.StackedMaterial,
            ManualSourceSemanticRole.Material,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Material);
    }

    [TestMethod]
    public void MaterialWithSpeakerInsetAllowsTypedPipOverlapBackToFront()
    {
        ManualLayoutSource material = CreateSource(PortraitCrop());
        ManualLayoutSource speaker = CreateSource(SquareCrop());

        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.MaterialWithSpeakerInset(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            material,
            Destination(FullRect()),
            speaker,
            Destination(SquareInset())));

        Assert.AreEqual(ManualLayoutMode.MaterialWithSpeakerInset, plan.Mode);
        AssertPlacement(
            plan.SourcePlacements[0],
            material,
            ManualDestinationSlot.MainMaterial,
            ManualSourceSemanticRole.Material,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Material);
        AssertPlacement(
            plan.SourcePlacements[1],
            speaker,
            ManualDestinationSlot.SpeakerInset,
            ManualSourceSemanticRole.Speaker,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);
        Assert.AreEqual(SquareInset(), plan.SourcePlacements[1].Destination.Region.Rect);
    }

    [TestMethod]
    public void ImportantRegionZoomRetainsManualRegionWithoutAutomaticSelection()
    {
        ManualLayoutSource importantRegion = CreateSource(PortraitCrop());

        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.ImportantRegionZoom(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            importantRegion,
            Destination(FullRect())));

        Assert.AreEqual(ManualLayoutMode.ImportantRegionZoom, plan.Mode);
        AssertPlacement(
            plan.SourcePlacements.Single(),
            importantRegion,
            ManualDestinationSlot.ZoomedImportantRegion,
            ManualSourceSemanticRole.ImportantRegion,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);
    }

    [TestMethod]
    public void BlurredSourceBackgroundUsesSameSourceInBackgroundForegroundOrder()
    {
        EntityId<AssetIdTag> sourceId = EntityId<AssetIdTag>.New();
        OriginRef origin = OriginRef.FromAsset(sourceId);
        ManualLayoutSource background = CreateSource(PortraitCrop(), sourceId, origin);
        ManualLayoutSource foreground = CreateSource(TallForegroundCrop(), sourceId, origin);

        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.BlurredSourceBackground(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            background,
            Destination(FullRect()),
            foreground,
            Destination(ForegroundDestination())));

        Assert.AreEqual(ManualLayoutMode.BlurredSourceBackground, plan.Mode);
        Assert.AreEqual(2, plan.SourcePlacements.Count);
        Assert.AreEqual(sourceId, plan.SourcePlacements[0].SourceAssetId);
        Assert.AreEqual(sourceId, plan.SourcePlacements[1].SourceAssetId);
        AssertPlacement(
            plan.SourcePlacements[0],
            background,
            ManualDestinationSlot.BlurredBackground,
            ManualSourceSemanticRole.Subject,
            ManualSourceTreatment.BlurredBackground,
            LayoutRegionRole.Video);
        AssertPlacement(
            plan.SourcePlacements[1],
            foreground,
            ManualDestinationSlot.ForegroundSubject,
            ManualSourceSemanticRole.Subject,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);
    }

    [TestMethod]
    public void FixedTemplatePlacementRequiresVersionedTemplateAndTypedVideoSlot()
    {
        TemplateReference template = CreateTemplateReference();
        ManualLayoutSource video = CreateSource(TallForegroundCrop());

        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.FixedTemplatePlacement(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            video,
            Destination(ForegroundDestination()),
            template));

        Assert.AreEqual(ManualLayoutMode.FixedTemplatePlacement, plan.Mode);
        Assert.AreEqual(template, plan.TemplateReference);
        AssertPlacement(
            plan.SourcePlacements.Single(),
            video,
            ManualDestinationSlot.TemplateVideo,
            ManualSourceSemanticRole.TemplateVideo,
            ManualSourceTreatment.Standard,
            LayoutRegionRole.Video);

        AssertParamName<ArgumentNullException>(
            "templateReference",
            () => ManualLayoutRequest.FixedTemplatePlacement(
                EntityId<LayoutSpecIdTag>.New(),
                CreateEnvelope(),
                ManualStaticCropDecision.SingleStaticCropSufficient,
                video,
                Destination(ForegroundDestination()),
                null!));
    }

    [TestMethod]
    public void OutputDestinationsResolveExactlyAt1080x1920()
    {
        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.MaterialWithSpeakerInset(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            CreateSource(PortraitCrop()),
            Destination(FullRect()),
            CreateSource(SquareCrop()),
            Destination(SquareInset())));

        ResolvedLayout resolved = LayoutResolver.Resolve(plan.DerivedLayoutSpec, 1080, 1920);

        AssertRect(resolved.Regions[0].Rect, 0, 0, 1080, 1920);
        AssertRect(resolved.Regions[1].Rect, 720, 120, 360, 360);
        AssertRect(resolved.Regions[2].Rect, 0, 0, 1080, 1920);
        AssertRect(resolved.SafeZones.Single().Rect, 0, 0, 1080, 1920);
    }

    [TestMethod]
    public void FractionalDestinationPixelsAreRejectedRatherThanRounded()
    {
        NormalizedRect fractionalDestination = Rect(1, 7, 0, 1, 6, 7, 1, 1);
        ManualLayoutSource source = CreateSource(
            FullRect(),
            displayGeometry: new SourceDisplayGeometry(270, 560));
        ManualLayoutPlan plan = ResolvePlan(ManualLayoutRequest.SingleSubjectCrop(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            source,
            Destination(fractionalDestination)));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => LayoutResolver.Resolve(plan.DerivedLayoutSpec, 1080, 1920));
    }

    [TestMethod]
    public void SourceCropMustContainTheManualSubjectRegion()
    {
        EntityId<AssetIdTag> sourceId = EntityId<AssetIdTag>.New();
        NormalizedRect crop = Rect(0, 1, 0, 1, 1, 2, 1, 1);
        NormalizedRect outsideSubject = Rect(1, 2, 0, 1, 1, 2, 1, 1);

        AssertParamName<ArgumentException>(
            "subjectRegion",
            () => new ManualLayoutSource(
                sourceId,
                OriginRef.FromAsset(sourceId),
                new SourceDisplayGeometry(1920, 1080),
                new ManualSourceCrop(crop),
                new ManualSubjectRegion(outsideSubject)));
    }

    [TestMethod]
    public void SourceCropAndDestinationAspectMustMatchExactly()
    {
        ManualLayoutSource landscapeSource = CreateSource(FullRect());
        ManualLayoutRequest request = ManualLayoutRequest.SingleSubjectCrop(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            landscapeSource,
            Destination(FullRect()));

        AssertParamName<ArgumentException>(
            "request",
            () => ManualLayoutResolver.Resolve(request));
    }

    [TestMethod]
    public void StackRejectsGapOverlapReverseOrderAndHorizontalMisalignment()
    {
        ManualLayoutSource first = CreateSource(StackCrop());
        ManualLayoutSource second = CreateSource(StackCrop());
        (NormalizedRect Top, NormalizedRect Bottom)[] invalidPairs =
        [
            (
                Rect(0, 1, 0, 1, 1, 1, 2, 5),
                Rect(0, 1, 3, 5, 1, 1, 2, 5)),
            (
                Rect(0, 1, 0, 1, 1, 1, 3, 5),
                Rect(0, 1, 1, 2, 1, 1, 1, 2)),
            (BottomHalf(), TopHalf()),
            (
                TopHalf(),
                Rect(1, 10, 1, 2, 9, 10, 1, 2)),
        ];

        foreach ((NormalizedRect top, NormalizedRect bottom) in invalidPairs)
        {
            ManualLayoutRequest request = ManualLayoutRequest.TwoSpeakerStack(
                EntityId<LayoutSpecIdTag>.New(),
                CreateEnvelope(),
                ManualStaticCropDecision.SingleStaticCropSufficient,
                first,
                Destination(top),
                second,
                Destination(bottom));

            AssertParamName<ArgumentException>(
                "request",
                () => ManualLayoutResolver.Resolve(request));
        }
    }

    [TestMethod]
    public void PipOverlapExceptionDoesNotPermitDetachedInsetOrStackOverlap()
    {
        ManualLayoutRequest detachedPip = ManualLayoutRequest.MaterialWithSpeakerInset(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            CreateSource(PortraitCrop()),
            Destination(Rect(0, 1, 0, 1, 1, 2, 1, 1)),
            CreateSource(SquareCrop()),
            Destination(Rect(1, 2, 0, 1, 1, 2, 1, 2)));
        ManualLayoutRequest overlappingStack = ManualLayoutRequest.TwoSpeakerStack(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            CreateSource(StackCrop()),
            Destination(Rect(0, 1, 0, 1, 1, 1, 3, 5)),
            CreateSource(StackCrop()),
            Destination(Rect(0, 1, 1, 2, 1, 1, 1, 2)));

        AssertParamName<ArgumentException>(
            "request",
            () => ManualLayoutResolver.Resolve(detachedPip));
        AssertParamName<ArgumentException>(
            "request",
            () => ManualLayoutResolver.Resolve(overlappingStack));
    }

    [TestMethod]
    public void BlurModeRejectsDifferentSourceIdentityOriginOrDisplayGeometry()
    {
        EntityId<AssetIdTag> firstId = EntityId<AssetIdTag>.New();
        OriginRef firstOrigin = OriginRef.FromAsset(firstId);
        ManualLayoutSource background = CreateSource(PortraitCrop(), firstId, firstOrigin);
        ManualLayoutSource differentSource = CreateSource(TallForegroundCrop());
        ManualLayoutRequest request = ManualLayoutRequest.BlurredSourceBackground(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            background,
            Destination(FullRect()),
            differentSource,
            Destination(ForegroundDestination()));

        AssertParamName<ArgumentException>(
            "request",
            () => ManualLayoutResolver.Resolve(request));
    }

    [TestMethod]
    public void ExplicitModeIsNeverChangedOrSilentlyFallenBack()
    {
        ManualLayoutRequest request = ManualLayoutRequest.ImportantRegionZoom(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.SingleStaticCropSufficient,
            CreateSource(PortraitCrop()),
            Destination(FullRect()));

        ManualLayoutResolution resolution = ManualLayoutResolver.Resolve(request);

        Assert.AreEqual(ManualLayoutMode.ImportantRegionZoom, request.Mode);
        Assert.AreEqual(ManualLayoutMode.ImportantRegionZoom, resolution.Plan!.Mode);
        Assert.AreEqual(ManualDestinationSlot.ZoomedImportantRegion,
            resolution.Plan.SourcePlacements.Single().Destination.Slot);
    }

    [TestMethod]
    public void ExplicitDynamicSignalReturnsManualKeyframesRequiredWithoutException()
    {
        ManualLayoutRequest request = ManualLayoutRequest.ImportantRegionZoom(
            EntityId<LayoutSpecIdTag>.New(),
            CreateEnvelope(),
            ManualStaticCropDecision.ManualKeyframesRequired,
            CreateSource(PortraitCrop()),
            Destination(FullRect()));

        ManualLayoutResolution resolution = ManualLayoutResolver.Resolve(request);

        Assert.AreEqual(
            ManualLayoutResolutionStatus.ManualKeyframesRequired,
            resolution.Status);
        Assert.AreEqual(ManualLayoutMode.ImportantRegionZoom, resolution.Mode);
        Assert.IsNull(resolution.Plan);
        Assert.AreEqual(ManualLayoutMode.ImportantRegionZoom, request.Mode);
    }

    [TestMethod]
    public void InvalidEnvelopeFreshIdentityNullsAndUndefinedDecisionHaveStableExceptions()
    {
        ManualLayoutSource source = CreateSource(PortraitCrop());
        ManualDestinationGeometry destination = Destination(FullRect());
        LayoutSpec portraitEnvelope = CreateEnvelope();
        LayoutSpec landscapeEnvelope = CreateEnvelope(16, 9);
        LayoutSpec platformClaimEnvelope = CreateEnvelope(
            additionalSafeZones:
            [
                new SafeZone("platform-ui", FullRect()),
            ]);

        AssertParamName<ArgumentException>(
            "outputEnvelope",
            () => ManualLayoutRequest.SingleSubjectCrop(
                EntityId<LayoutSpecIdTag>.New(),
                landscapeEnvelope,
                ManualStaticCropDecision.SingleStaticCropSufficient,
                source,
                destination));
        AssertParamName<ArgumentException>(
            "outputEnvelope",
            () => ManualLayoutRequest.SingleSubjectCrop(
                EntityId<LayoutSpecIdTag>.New(),
                platformClaimEnvelope,
                ManualStaticCropDecision.SingleStaticCropSufficient,
                source,
                destination));
        AssertParamName<ArgumentException>(
            "derivedLayoutSpecId",
            () => ManualLayoutRequest.SingleSubjectCrop(
                portraitEnvelope.Id,
                portraitEnvelope,
                ManualStaticCropDecision.SingleStaticCropSufficient,
                source,
                destination));
        AssertParamName<ArgumentOutOfRangeException>(
            "staticCropDecision",
            () => ManualLayoutRequest.SingleSubjectCrop(
                EntityId<LayoutSpecIdTag>.New(),
                portraitEnvelope,
                (ManualStaticCropDecision)int.MaxValue,
                source,
                destination));
        AssertParamName<ArgumentNullException>(
            "derivedLayoutSpecId",
            () => ManualLayoutRequest.SingleSubjectCrop(
                null!,
                portraitEnvelope,
                ManualStaticCropDecision.SingleStaticCropSufficient,
                source,
                destination));
        AssertParamName<ArgumentNullException>(
            "outputEnvelope",
            () => ManualLayoutRequest.SingleSubjectCrop(
                EntityId<LayoutSpecIdTag>.New(),
                null!,
                ManualStaticCropDecision.SingleStaticCropSufficient,
                source,
                destination));
        AssertParamName<ArgumentNullException>(
            "subject",
            () => ManualLayoutRequest.SingleSubjectCrop(
                EntityId<LayoutSpecIdTag>.New(),
                portraitEnvelope,
                ManualStaticCropDecision.SingleStaticCropSufficient,
                null!,
                destination));
        AssertParamName<ArgumentNullException>(
            "destination",
            () => ManualLayoutRequest.SingleSubjectCrop(
                EntityId<LayoutSpecIdTag>.New(),
                portraitEnvelope,
                ManualStaticCropDecision.SingleStaticCropSufficient,
                source,
                null!));
        AssertParamName<ArgumentNullException>(
            "request",
            () => ManualLayoutResolver.Resolve(null!));
    }

    [TestMethod]
    public void SourceGeometryOriginAndPixelGridAreFailClosed()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "width",
            () => new SourceDisplayGeometry(0, 1080));
        AssertParamName<ArgumentOutOfRangeException>(
            "height",
            () => new SourceDisplayGeometry(1920, -1));

        EntityId<AssetIdTag> sourceId = EntityId<AssetIdTag>.New();
        EntityId<AssetIdTag> otherId = EntityId<AssetIdTag>.New();
        AssertParamName<ArgumentException>(
            "origin",
            () => new ManualLayoutSource(
                sourceId,
                OriginRef.FromAsset(otherId),
                new SourceDisplayGeometry(1920, 1080),
                new ManualSourceCrop(PortraitCrop()),
                new ManualSubjectRegion(PortraitCrop())));
        AssertParamName<ArgumentException>(
            "origin",
            () => new ManualLayoutSource(
                sourceId,
                OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New()),
                new SourceDisplayGeometry(1920, 1080),
                new ManualSourceCrop(PortraitCrop()),
                new ManualSubjectRegion(PortraitCrop())));
        AssertParamName<ArgumentException>(
            "crop",
            () => new ManualLayoutSource(
                sourceId,
                OriginRef.FromAsset(sourceId),
                new SourceDisplayGeometry(1920, 1080),
                new ManualSourceCrop(Rect(1, 7, 0, 1, 6, 7, 1, 1)),
                new ManualSubjectRegion(Rect(1, 7, 0, 1, 6, 7, 1, 1))));
    }

    [TestMethod]
    public void ManualApiHasNoFaceCandidateOrArbitraryStringSurface()
    {
        Type[] manualTypes = typeof(ManualLayoutResolver).Assembly
            .GetExportedTypes()
            .Where(static type =>
                type.Namespace == typeof(ManualLayoutResolver).Namespace
                && (type.Name.StartsWith("Manual", StringComparison.Ordinal)
                    || type == typeof(SourceDisplayGeometry)))
            .ToArray();

        Assert.IsFalse(manualTypes.Any(static type => type.IsInterface));
        Assert.IsFalse(manualTypes.Any(static type =>
            type.Name.Contains("Face", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Detector", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Confidence", StringComparison.OrdinalIgnoreCase)));

        foreach (Type type in manualTypes)
        {
            Assert.IsFalse(type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(static property => property.PropertyType == typeof(string)));
            Assert.IsFalse(type.GetConstructors()
                .SelectMany(static constructor => constructor.GetParameters())
                .Any(static parameter => parameter.ParameterType == typeof(string)));
            Assert.IsFalse(type.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .SelectMany(static method => method.GetParameters())
                .Any(static parameter =>
                    parameter.ParameterType == typeof(string)
                    || parameter.ParameterType.Namespace == "HubRadio.Core.Analysis"));
        }

        MethodInfo[] resolverMethods = typeof(ManualLayoutResolver).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, resolverMethods.Length);
        Assert.AreEqual(nameof(ManualLayoutResolver.Resolve), resolverMethods[0].Name);
    }

    [TestMethod]
    public void PlansAndValueObjectsHaveDefensiveCollectionsAndValueSemantics()
    {
        EntityId<AssetIdTag> sourceId = EntityId<AssetIdTag>.New();
        OriginRef origin = OriginRef.FromAsset(sourceId);
        ManualLayoutSource firstSource = CreateSource(PortraitCrop(), sourceId, origin);
        ManualLayoutSource equivalentSource = new(
            EntityId<AssetIdTag>.From(sourceId.Value),
            OriginRef.FromAsset(EntityId<AssetIdTag>.From(sourceId.Value)),
            new SourceDisplayGeometry(1920, 1080),
            new ManualSourceCrop(PortraitCrop()),
            new ManualSubjectRegion(PortraitCrop()));
        EntityId<LayoutSpecIdTag> derivedId = EntityId<LayoutSpecIdTag>.New();
        LayoutSpec envelope = CreateEnvelope();

        ManualLayoutPlan first = ResolvePlan(ManualLayoutRequest.SingleSubjectCrop(
            derivedId,
            envelope,
            ManualStaticCropDecision.SingleStaticCropSufficient,
            firstSource,
            Destination(FullRect())));
        ManualLayoutPlan equivalent = ResolvePlan(ManualLayoutRequest.SingleSubjectCrop(
            EntityId<LayoutSpecIdTag>.From(derivedId.Value),
            envelope,
            ManualStaticCropDecision.SingleStaticCropSufficient,
            equivalentSource,
            Destination(FullRect())));

        Assert.AreEqual(firstSource, equivalentSource);
        Assert.AreEqual(firstSource.GetHashCode(), equivalentSource.GetHashCode());
        Assert.AreEqual(first.SourcePlacements[0], equivalent.SourcePlacements[0]);
        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<ManualSourcePlacement>)first.SourcePlacements).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<LayoutRegion>)first.DerivedLayoutSpec.Regions).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<SafeZone>)first.DerivedLayoutSpec.SafeZones).Clear());
    }

    private static ManualLayoutPlan ResolvePlan(ManualLayoutRequest request)
    {
        ManualLayoutResolution resolution = ManualLayoutResolver.Resolve(request);
        Assert.AreEqual(request.Mode, resolution.Mode);
        Assert.AreEqual(ManualLayoutResolutionStatus.Resolved, resolution.Status);
        Assert.IsNotNull(resolution.Plan);
        Assert.AreEqual(resolution.Mode, resolution.Plan.Mode);
        return resolution.Plan;
    }

    private static LayoutSpec CreateEnvelope(
        int aspectWidth = 9,
        int aspectHeight = 16,
        IReadOnlyList<SafeZone>? additionalSafeZones = null)
    {
        var safeZones = new List<SafeZone>
        {
            new("output-frame-bounds", FullRect()),
        };
        if (additionalSafeZones is not null)
        {
            safeZones.AddRange(additionalSafeZones);
        }

        return new LayoutSpec(
            EntityId<LayoutSpecIdTag>.New(),
            aspectWidth,
            aspectHeight,
            [
                new LayoutRegion("primary-video", FullRect(), LayoutRegionRole.Video),
                new LayoutRegion("subtitle-placement", FullRect(), LayoutRegionRole.Subtitle),
            ],
            safeZones);
    }

    private static ManualLayoutSource CreateSource(
        NormalizedRect crop,
        EntityId<AssetIdTag>? sourceId = null,
        OriginRef? origin = null,
        NormalizedRect? subject = null,
        SourceDisplayGeometry? displayGeometry = null)
    {
        sourceId ??= EntityId<AssetIdTag>.New();
        origin ??= OriginRef.FromAsset(sourceId);
        return new ManualLayoutSource(
            sourceId,
            origin,
            displayGeometry ?? new SourceDisplayGeometry(1920, 1080),
            new ManualSourceCrop(crop),
            new ManualSubjectRegion(subject ?? crop));
    }

    private static TemplateReference CreateTemplateReference()
    {
        return new TemplateReference(
            EntityId<TemplateIdTag>.New(),
            "versioned-fixed-template",
            new ContentDigest(DigestHex));
    }

    private static ManualDestinationGeometry Destination(NormalizedRect rect)
    {
        return new ManualDestinationGeometry(rect);
    }

    private static NormalizedRect PortraitCrop()
    {
        return Rect(329, 960, 1, 270, 201, 640, 134, 135);
    }

    private static NormalizedRect StackCrop()
    {
        return Rect(7, 32, 1, 18, 9, 16, 8, 9);
    }

    private static NormalizedRect SquareCrop()
    {
        return Rect(7, 24, 7, 54, 5, 12, 20, 27);
    }

    private static NormalizedRect TallForegroundCrop()
    {
        return Rect(23, 64, 0, 1, 9, 32, 1, 1);
    }

    private static NormalizedRect ForegroundDestination()
    {
        return Rect(1, 6, 1, 8, 2, 3, 3, 4);
    }

    private static NormalizedRect SquareInset()
    {
        return Rect(2, 3, 1, 16, 1, 3, 3, 16);
    }

    private static NormalizedRect TopHalf()
    {
        return Rect(0, 1, 0, 1, 1, 1, 1, 2);
    }

    private static NormalizedRect BottomHalf()
    {
        return Rect(0, 1, 1, 2, 1, 1, 1, 2);
    }

    private static NormalizedRect FullRect()
    {
        return Rect(0, 1, 0, 1, 1, 1, 1, 1);
    }

    private static NormalizedRect Rect(
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator)
    {
        return new NormalizedRect(
            new RationalNumber(xNumerator, xDenominator),
            new RationalNumber(yNumerator, yDenominator),
            new RationalNumber(widthNumerator, widthDenominator),
            new RationalNumber(heightNumerator, heightDenominator));
    }

    private static void AssertPlacement(
        ManualSourcePlacement placement,
        ManualLayoutSource expectedSource,
        ManualDestinationSlot expectedSlot,
        ManualSourceSemanticRole expectedRole,
        ManualSourceTreatment expectedTreatment,
        LayoutRegionRole expectedRegionRole)
    {
        Assert.AreEqual(expectedSource.SourceAssetId, placement.SourceAssetId);
        Assert.AreEqual(expectedSource.Origin, placement.Origin);
        Assert.AreEqual(expectedSource.DisplayGeometry, placement.SourceDisplayGeometry);
        Assert.AreEqual(expectedSource.Crop, placement.Crop);
        Assert.AreEqual(expectedSource.SubjectRegion, placement.SubjectRegion);
        Assert.AreEqual(expectedSlot, placement.Destination.Slot);
        Assert.AreEqual(expectedRole, placement.SemanticRole);
        Assert.AreEqual(expectedTreatment, placement.Treatment);
        Assert.AreEqual(expectedRegionRole, placement.Destination.Region.Role);
    }

    private static void AssertRect(
        PixelRect actual,
        int x,
        int y,
        int width,
        int height)
    {
        Assert.AreEqual(x, actual.X);
        Assert.AreEqual(y, actual.Y);
        Assert.AreEqual(width, actual.Width);
        Assert.AreEqual(height, actual.Height);
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        PropertyInfo[] properties = type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}