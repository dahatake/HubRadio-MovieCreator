using System.Numerics;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Layout;

public enum ManualLayoutMode
{
    SingleSubjectCrop,
    TwoSpeakerStack,
    SpeakerMaterialStack,
    MaterialWithSpeakerInset,
    ImportantRegionZoom,
    BlurredSourceBackground,
    FixedTemplatePlacement,
}

public enum ManualStaticCropDecision
{
    SingleStaticCropSufficient,
    ManualKeyframesRequired,
}

public enum ManualLayoutResolutionStatus
{
    Resolved,
    ManualKeyframesRequired,
}

public enum ManualSourceSemanticRole
{
    Subject,
    Speaker,
    Material,
    ImportantRegion,
    TemplateVideo,
}

public enum ManualSourceTreatment
{
    Standard,
    BlurredBackground,
}

public enum ManualDestinationSlot
{
    SingleSubject,
    TopSpeaker,
    BottomSpeaker,
    StackedSpeaker,
    StackedMaterial,
    MainMaterial,
    SpeakerInset,
    ZoomedImportantRegion,
    BlurredBackground,
    ForegroundSubject,
    TemplateVideo,
}

/// <summary>
/// Positive display-oriented source dimensions after coded rotation and
/// sample-aspect-ratio (SAR) have been applied by the caller.
/// </summary>
public sealed class SourceDisplayGeometry : IEquatable<SourceDisplayGeometry>
{
    public SourceDisplayGeometry(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "Display-oriented source width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                "Display-oriented source height must be positive.");
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public bool Equals(SourceDisplayGeometry? other)
    {
        return other is not null && Width == other.Width && Height == other.Height;
    }

    public override bool Equals(object? obj)
    {
        return obj is SourceDisplayGeometry other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Width, Height);
    }
}

public sealed class ManualSourceCrop : IEquatable<ManualSourceCrop>
{
    public ManualSourceCrop(NormalizedRect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        Rect = rect;
    }

    public NormalizedRect Rect { get; }

    public bool Equals(ManualSourceCrop? other)
    {
        return other is not null && Rect == other.Rect;
    }

    public override bool Equals(object? obj)
    {
        return obj is ManualSourceCrop other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Rect.GetHashCode();
    }
}

public sealed class ManualSubjectRegion : IEquatable<ManualSubjectRegion>
{
    public ManualSubjectRegion(NormalizedRect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        Rect = rect;
    }

    public NormalizedRect Rect { get; }

    public bool Equals(ManualSubjectRegion? other)
    {
        return other is not null && Rect == other.Rect;
    }

    public override bool Equals(object? obj)
    {
        return obj is ManualSubjectRegion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Rect.GetHashCode();
    }
}

public sealed class ManualDestinationGeometry : IEquatable<ManualDestinationGeometry>
{
    public ManualDestinationGeometry(NormalizedRect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        Rect = rect;
    }

    public NormalizedRect Rect { get; }

    public bool Equals(ManualDestinationGeometry? other)
    {
        return other is not null && Rect == other.Rect;
    }

    public override bool Equals(object? obj)
    {
        return obj is ManualDestinationGeometry other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Rect.GetHashCode();
    }
}

public sealed class ManualLayoutSource : IEquatable<ManualLayoutSource>
{
    public ManualLayoutSource(
        EntityId<AssetIdTag> sourceAssetId,
        OriginRef origin,
        SourceDisplayGeometry displayGeometry,
        ManualSourceCrop crop,
        ManualSubjectRegion subjectRegion)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(displayGeometry);
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentNullException.ThrowIfNull(subjectRegion);

        if (origin.Kind == OriginKind.EditorialRecord)
        {
            throw new ArgumentException(
                "A source placement origin must identify source media or an asset.",
                nameof(origin));
        }

        if (origin.Kind == OriginKind.Asset && origin.AssetId != sourceAssetId)
        {
            throw new ArgumentException(
                "An asset origin must match the source asset identity.",
                nameof(origin));
        }

        if (!ManualLayoutGeometry.Contains(crop.Rect, subjectRegion.Rect))
        {
            throw new ArgumentException(
                "The source crop must contain the manually selected subject region.",
                nameof(subjectRegion));
        }

        if (!ManualLayoutGeometry.IsPixelAligned(crop.Rect, displayGeometry))
        {
            throw new ArgumentException(
                "The source crop must resolve to integer display-oriented source pixels.",
                nameof(crop));
        }

        SourceAssetId = sourceAssetId;
        Origin = origin;
        DisplayGeometry = displayGeometry;
        Crop = crop;
        SubjectRegion = subjectRegion;
    }

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public OriginRef Origin { get; }

    public SourceDisplayGeometry DisplayGeometry { get; }

    public ManualSourceCrop Crop { get; }

    public ManualSubjectRegion SubjectRegion { get; }

    public bool Equals(ManualLayoutSource? other)
    {
        return other is not null
            && SourceAssetId == other.SourceAssetId
            && Origin == other.Origin
            && DisplayGeometry.Equals(other.DisplayGeometry)
            && Crop.Equals(other.Crop)
            && SubjectRegion.Equals(other.SubjectRegion);
    }

    public override bool Equals(object? obj)
    {
        return obj is ManualLayoutSource other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            SourceAssetId,
            Origin,
            DisplayGeometry,
            Crop,
            SubjectRegion);
    }
}

public sealed class ManualDestinationRegion : IEquatable<ManualDestinationRegion>
{
    internal ManualDestinationRegion(ManualDestinationSlot slot, LayoutRegion region)
    {
        if (!Enum.IsDefined(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown destination slot.");
        }

        ArgumentNullException.ThrowIfNull(region);
        Slot = slot;
        Region = region;
    }

    public ManualDestinationSlot Slot { get; }

    public LayoutRegion Region { get; }

    public bool Equals(ManualDestinationRegion? other)
    {
        return other is not null && Slot == other.Slot && Region.Equals(other.Region);
    }

    public override bool Equals(object? obj)
    {
        return obj is ManualDestinationRegion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Slot, Region);
    }
}

public sealed class ManualSourcePlacement : IEquatable<ManualSourcePlacement>
{
    internal ManualSourcePlacement(
        ManualLayoutSource source,
        ManualDestinationRegion destination,
        ManualSourceSemanticRole semanticRole,
        ManualSourceTreatment treatment)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (!Enum.IsDefined(semanticRole))
        {
            throw new ArgumentOutOfRangeException(
                nameof(semanticRole),
                semanticRole,
                "Unknown source semantic role.");
        }

        if (!Enum.IsDefined(treatment))
        {
            throw new ArgumentOutOfRangeException(
                nameof(treatment),
                treatment,
                "Unknown source treatment.");
        }

        SourceAssetId = source.SourceAssetId;
        Origin = source.Origin;
        SourceDisplayGeometry = source.DisplayGeometry;
        Crop = source.Crop;
        SubjectRegion = source.SubjectRegion;
        Destination = destination;
        SemanticRole = semanticRole;
        Treatment = treatment;
    }

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public OriginRef Origin { get; }

    public SourceDisplayGeometry SourceDisplayGeometry { get; }

    public ManualSourceCrop Crop { get; }

    public ManualSubjectRegion SubjectRegion { get; }

    public ManualDestinationRegion Destination { get; }

    public ManualSourceSemanticRole SemanticRole { get; }

    public ManualSourceTreatment Treatment { get; }

    public bool Equals(ManualSourcePlacement? other)
    {
        return other is not null
            && SourceAssetId == other.SourceAssetId
            && Origin == other.Origin
            && SourceDisplayGeometry.Equals(other.SourceDisplayGeometry)
            && Crop.Equals(other.Crop)
            && SubjectRegion.Equals(other.SubjectRegion)
            && Destination.Equals(other.Destination)
            && SemanticRole == other.SemanticRole
            && Treatment == other.Treatment;
    }

    public override bool Equals(object? obj)
    {
        return obj is ManualSourcePlacement other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(SourceAssetId);
        hash.Add(Origin);
        hash.Add(SourceDisplayGeometry);
        hash.Add(Crop);
        hash.Add(SubjectRegion);
        hash.Add(Destination);
        hash.Add(SemanticRole);
        hash.Add(Treatment);
        return hash.ToHashCode();
    }
}

public sealed class ManualLayoutRequest
{
    internal const string OutputFrameBoundsName = "output-frame-bounds";

    private readonly ManualLayoutSource[] _sources;
    private readonly ManualDestinationGeometry[] _destinations;

    private ManualLayoutRequest(
        ManualLayoutMode mode,
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource[] sources,
        ManualDestinationGeometry[] destinations,
        TemplateReference? templateReference)
    {
        ArgumentNullException.ThrowIfNull(derivedLayoutSpecId);
        ArgumentNullException.ThrowIfNull(outputEnvelope);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destinations);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown manual layout mode.");
        }

        if (!Enum.IsDefined(staticCropDecision))
        {
            throw new ArgumentOutOfRangeException(
                nameof(staticCropDecision),
                staticCropDecision,
                "Unknown static crop decision.");
        }

        if (derivedLayoutSpecId == outputEnvelope.Id)
        {
            throw new ArgumentException(
                "The derived LayoutSpec must use a fresh identity.",
                nameof(derivedLayoutSpecId));
        }

        ValidateOutputEnvelope(outputEnvelope);
        ValidateModeShape(mode, sources, destinations, templateReference);

        Mode = mode;
        DerivedLayoutSpecId = derivedLayoutSpecId;
        OutputEnvelope = outputEnvelope;
        StaticCropDecision = staticCropDecision;
        _sources = [.. sources];
        _destinations = [.. destinations];
        TemplateReference = templateReference;
    }

    public ManualLayoutMode Mode { get; }

    public EntityId<LayoutSpecIdTag> DerivedLayoutSpecId { get; }

    public LayoutSpec OutputEnvelope { get; }

    public ManualStaticCropDecision StaticCropDecision { get; }

    internal TemplateReference? TemplateReference { get; }

    internal ManualLayoutSource Source(int index) => _sources[index];

    internal ManualDestinationGeometry Destination(int index) => _destinations[index];

    public static ManualLayoutRequest SingleSubjectCrop(
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource subject,
        ManualDestinationGeometry destination)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(destination);
        return new ManualLayoutRequest(
            ManualLayoutMode.SingleSubjectCrop,
            derivedLayoutSpecId,
            outputEnvelope,
            staticCropDecision,
            [subject],
            [destination],
            null);
    }

    public static ManualLayoutRequest TwoSpeakerStack(
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource topSpeaker,
        ManualDestinationGeometry topDestination,
        ManualLayoutSource bottomSpeaker,
        ManualDestinationGeometry bottomDestination)
    {
        ArgumentNullException.ThrowIfNull(topSpeaker);
        ArgumentNullException.ThrowIfNull(topDestination);
        ArgumentNullException.ThrowIfNull(bottomSpeaker);
        ArgumentNullException.ThrowIfNull(bottomDestination);
        return new ManualLayoutRequest(
            ManualLayoutMode.TwoSpeakerStack,
            derivedLayoutSpecId,
            outputEnvelope,
            staticCropDecision,
            [topSpeaker, bottomSpeaker],
            [topDestination, bottomDestination],
            null);
    }

    public static ManualLayoutRequest SpeakerMaterialStack(
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource speaker,
        ManualDestinationGeometry speakerTopDestination,
        ManualLayoutSource material,
        ManualDestinationGeometry materialBottomDestination)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(speakerTopDestination);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(materialBottomDestination);
        return new ManualLayoutRequest(
            ManualLayoutMode.SpeakerMaterialStack,
            derivedLayoutSpecId,
            outputEnvelope,
            staticCropDecision,
            [speaker, material],
            [speakerTopDestination, materialBottomDestination],
            null);
    }

    public static ManualLayoutRequest MaterialWithSpeakerInset(
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource material,
        ManualDestinationGeometry materialDestination,
        ManualLayoutSource speaker,
        ManualDestinationGeometry speakerInsetDestination)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(materialDestination);
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(speakerInsetDestination);
        return new ManualLayoutRequest(
            ManualLayoutMode.MaterialWithSpeakerInset,
            derivedLayoutSpecId,
            outputEnvelope,
            staticCropDecision,
            [material, speaker],
            [materialDestination, speakerInsetDestination],
            null);
    }

    public static ManualLayoutRequest ImportantRegionZoom(
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource importantRegion,
        ManualDestinationGeometry destination)
    {
        ArgumentNullException.ThrowIfNull(importantRegion);
        ArgumentNullException.ThrowIfNull(destination);
        return new ManualLayoutRequest(
            ManualLayoutMode.ImportantRegionZoom,
            derivedLayoutSpecId,
            outputEnvelope,
            staticCropDecision,
            [importantRegion],
            [destination],
            null);
    }

    public static ManualLayoutRequest BlurredSourceBackground(
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource backgroundSource,
        ManualDestinationGeometry backgroundDestination,
        ManualLayoutSource foregroundSource,
        ManualDestinationGeometry foregroundDestination)
    {
        ArgumentNullException.ThrowIfNull(backgroundSource);
        ArgumentNullException.ThrowIfNull(backgroundDestination);
        ArgumentNullException.ThrowIfNull(foregroundSource);
        ArgumentNullException.ThrowIfNull(foregroundDestination);
        return new ManualLayoutRequest(
            ManualLayoutMode.BlurredSourceBackground,
            derivedLayoutSpecId,
            outputEnvelope,
            staticCropDecision,
            [backgroundSource, foregroundSource],
            [backgroundDestination, foregroundDestination],
            null);
    }

    public static ManualLayoutRequest FixedTemplatePlacement(
        EntityId<LayoutSpecIdTag> derivedLayoutSpecId,
        LayoutSpec outputEnvelope,
        ManualStaticCropDecision staticCropDecision,
        ManualLayoutSource videoSource,
        ManualDestinationGeometry videoSlotDestination,
        TemplateReference templateReference)
    {
        ArgumentNullException.ThrowIfNull(videoSource);
        ArgumentNullException.ThrowIfNull(videoSlotDestination);
        ArgumentNullException.ThrowIfNull(templateReference);
        return new ManualLayoutRequest(
            ManualLayoutMode.FixedTemplatePlacement,
            derivedLayoutSpecId,
            outputEnvelope,
            staticCropDecision,
            [videoSource],
            [videoSlotDestination],
            templateReference);
    }

    private static void ValidateOutputEnvelope(LayoutSpec outputEnvelope)
    {
        if ((BigInteger)outputEnvelope.OutputAspectWidth * 16
            != (BigInteger)outputEnvelope.OutputAspectHeight * 9)
        {
            throw new ArgumentException(
                "The output envelope must have a 9:16 aspect ratio.",
                nameof(outputEnvelope));
        }

        if (outputEnvelope.SafeZones.Count != 1
            || !string.Equals(
                outputEnvelope.SafeZones[0].Name,
                OutputFrameBoundsName,
                StringComparison.Ordinal)
            || !ManualLayoutGeometry.IsFullFrame(outputEnvelope.SafeZones[0].Rect))
        {
            throw new ArgumentException(
                "The output envelope must contain only the full-frame output-frame-bounds zone.",
                nameof(outputEnvelope));
        }
    }

    private static void ValidateModeShape(
        ManualLayoutMode mode,
        IReadOnlyList<ManualLayoutSource> sources,
        IReadOnlyList<ManualDestinationGeometry> destinations,
        TemplateReference? templateReference)
    {
        int expectedCount = mode is ManualLayoutMode.TwoSpeakerStack
            or ManualLayoutMode.SpeakerMaterialStack
            or ManualLayoutMode.MaterialWithSpeakerInset
            or ManualLayoutMode.BlurredSourceBackground
            ? 2
            : 1;

        if (sources.Count != expectedCount || destinations.Count != expectedCount)
        {
            throw new ArgumentException(
                "The source and destination counts do not match the manual layout mode.",
                nameof(sources));
        }

        bool requiresTemplate = mode == ManualLayoutMode.FixedTemplatePlacement;
        if (requiresTemplate != (templateReference is not null))
        {
            throw new ArgumentException(
                "Only fixed-template placement requires a versioned template reference.",
                nameof(templateReference));
        }
    }
}

public sealed class ManualLayoutPlan : IEquatable<ManualLayoutPlan>
{
    internal ManualLayoutPlan(
        ManualLayoutMode mode,
        LayoutSpec derivedLayoutSpec,
        IReadOnlyList<ManualSourcePlacement> sourcePlacements,
        TemplateReference? templateReference)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown manual layout mode.");
        }

        ArgumentNullException.ThrowIfNull(derivedLayoutSpec);
        ArgumentNullException.ThrowIfNull(sourcePlacements);

        ManualSourcePlacement[] copiedPlacements = new ManualSourcePlacement[sourcePlacements.Count];
        for (int index = 0; index < copiedPlacements.Length; index++)
        {
            copiedPlacements[index] = sourcePlacements[index]
                ?? throw new ArgumentException(
                    "Source placements must not contain null elements.",
                    nameof(sourcePlacements));
        }

        bool requiresTemplate = mode == ManualLayoutMode.FixedTemplatePlacement;
        if (requiresTemplate != (templateReference is not null))
        {
            throw new ArgumentException(
                "Only fixed-template placement may retain a template reference.",
                nameof(templateReference));
        }

        Mode = mode;
        DerivedLayoutSpec = derivedLayoutSpec;
        SourcePlacements = Array.AsReadOnly(copiedPlacements);
        TemplateReference = templateReference;
    }

    public ManualLayoutMode Mode { get; }

    public LayoutSpec DerivedLayoutSpec { get; }

    public IReadOnlyList<ManualSourcePlacement> SourcePlacements { get; }

    public TemplateReference? TemplateReference { get; }

    public bool Equals(ManualLayoutPlan? other)
    {
        return other is not null
            && Mode == other.Mode
            && LayoutSpecsEqual(DerivedLayoutSpec, other.DerivedLayoutSpec)
            && SourcePlacements.SequenceEqual(other.SourcePlacements)
            && Equals(TemplateReference, other.TemplateReference);
    }

    public override bool Equals(object? obj)
    {
        return obj is ManualLayoutPlan other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Mode);
        AddLayoutSpecHash(ref hash, DerivedLayoutSpec);
        foreach (ManualSourcePlacement placement in SourcePlacements)
        {
            hash.Add(placement);
        }

        hash.Add(TemplateReference);
        return hash.ToHashCode();
    }

    private static bool LayoutSpecsEqual(LayoutSpec left, LayoutSpec right)
    {
        return left.Id == right.Id
            && left.OutputAspectWidth == right.OutputAspectWidth
            && left.OutputAspectHeight == right.OutputAspectHeight
            && left.Regions.SequenceEqual(right.Regions)
            && left.SafeZones.SequenceEqual(right.SafeZones);
    }

    private static void AddLayoutSpecHash(ref HashCode hash, LayoutSpec layoutSpec)
    {
        hash.Add(layoutSpec.Id);
        hash.Add(layoutSpec.OutputAspectWidth);
        hash.Add(layoutSpec.OutputAspectHeight);
        foreach (LayoutRegion region in layoutSpec.Regions)
        {
            hash.Add(region);
        }

        foreach (SafeZone safeZone in layoutSpec.SafeZones)
        {
            hash.Add(safeZone);
        }
    }
}

public sealed class ManualLayoutResolution
{
    private ManualLayoutResolution(
        ManualLayoutMode mode,
        ManualLayoutResolutionStatus status,
        ManualLayoutPlan? plan)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown manual layout mode.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown resolution status.");
        }

        if ((status == ManualLayoutResolutionStatus.Resolved) != (plan is not null))
        {
            throw new ArgumentException(
                "A resolved result requires a plan and a keyframe result must not contain one.",
                nameof(plan));
        }

        if (plan is not null && plan.Mode != mode)
        {
            throw new ArgumentException(
                "A resolved plan must retain the selected manual layout mode.",
                nameof(plan));
        }

        Mode = mode;
        Status = status;
        Plan = plan;
    }

    public ManualLayoutMode Mode { get; }

    public ManualLayoutResolutionStatus Status { get; }

    public ManualLayoutPlan? Plan { get; }

    internal static ManualLayoutResolution Resolved(ManualLayoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ManualLayoutResolution(
            plan.Mode,
            ManualLayoutResolutionStatus.Resolved,
            plan);
    }

    internal static ManualLayoutResolution KeyframesRequired(ManualLayoutMode mode)
    {
        return new ManualLayoutResolution(
            mode,
            ManualLayoutResolutionStatus.ManualKeyframesRequired,
            null);
    }
}

public static class ManualLayoutResolver
{
    public static ManualLayoutResolution Resolve(ManualLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ManualLayoutPlan plan = request.Mode switch
        {
            ManualLayoutMode.SingleSubjectCrop => BuildSingleSubjectPlan(request),
            ManualLayoutMode.TwoSpeakerStack => BuildTwoSpeakerPlan(request),
            ManualLayoutMode.SpeakerMaterialStack => BuildSpeakerMaterialPlan(request),
            ManualLayoutMode.MaterialWithSpeakerInset => BuildMaterialInsetPlan(request),
            ManualLayoutMode.ImportantRegionZoom => BuildImportantRegionPlan(request),
            ManualLayoutMode.BlurredSourceBackground => BuildBlurredBackgroundPlan(request),
            ManualLayoutMode.FixedTemplatePlacement => BuildFixedTemplatePlan(request),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Mode,
                "Unknown manual layout mode."),
        };

        return request.StaticCropDecision == ManualStaticCropDecision.ManualKeyframesRequired
            ? ManualLayoutResolution.KeyframesRequired(request.Mode)
            : ManualLayoutResolution.Resolved(plan);
    }

    private static ManualLayoutPlan BuildSingleSubjectPlan(ManualLayoutRequest request)
    {
        ManualSourcePlacement placement = CreatePlacement(
            request,
            request.Source(0),
            request.Destination(0),
            ManualDestinationSlot.SingleSubject,
            ManualSourceSemanticRole.Subject,
            ManualSourceTreatment.Standard);
        return BuildPlan(request, [placement]);
    }

    private static ManualLayoutPlan BuildTwoSpeakerPlan(ManualLayoutRequest request)
    {
        ValidateVerticalStack(request, request.Destination(0), request.Destination(1));
        return BuildPlan(
            request,
            [
                CreatePlacement(
                    request,
                    request.Source(0),
                    request.Destination(0),
                    ManualDestinationSlot.TopSpeaker,
                    ManualSourceSemanticRole.Speaker,
                    ManualSourceTreatment.Standard),
                CreatePlacement(
                    request,
                    request.Source(1),
                    request.Destination(1),
                    ManualDestinationSlot.BottomSpeaker,
                    ManualSourceSemanticRole.Speaker,
                    ManualSourceTreatment.Standard),
            ]);
    }

    private static ManualLayoutPlan BuildSpeakerMaterialPlan(ManualLayoutRequest request)
    {
        ValidateVerticalStack(request, request.Destination(0), request.Destination(1));
        return BuildPlan(
            request,
            [
                CreatePlacement(
                    request,
                    request.Source(0),
                    request.Destination(0),
                    ManualDestinationSlot.StackedSpeaker,
                    ManualSourceSemanticRole.Speaker,
                    ManualSourceTreatment.Standard),
                CreatePlacement(
                    request,
                    request.Source(1),
                    request.Destination(1),
                    ManualDestinationSlot.StackedMaterial,
                    ManualSourceSemanticRole.Material,
                    ManualSourceTreatment.Standard),
            ]);
    }

    private static ManualLayoutPlan BuildMaterialInsetPlan(ManualLayoutRequest request)
    {
        NormalizedRect material = request.Destination(0).Rect;
        NormalizedRect inset = request.Destination(1).Rect;
        if (!ManualLayoutGeometry.Contains(material, inset) || material == inset)
        {
            throw new ArgumentException(
                "The speaker inset must be strictly contained by the material destination.",
                nameof(request));
        }

        return BuildPlan(
            request,
            [
                CreatePlacement(
                    request,
                    request.Source(0),
                    request.Destination(0),
                    ManualDestinationSlot.MainMaterial,
                    ManualSourceSemanticRole.Material,
                    ManualSourceTreatment.Standard),
                CreatePlacement(
                    request,
                    request.Source(1),
                    request.Destination(1),
                    ManualDestinationSlot.SpeakerInset,
                    ManualSourceSemanticRole.Speaker,
                    ManualSourceTreatment.Standard),
            ]);
    }

    private static ManualLayoutPlan BuildImportantRegionPlan(ManualLayoutRequest request)
    {
        ManualSourcePlacement placement = CreatePlacement(
            request,
            request.Source(0),
            request.Destination(0),
            ManualDestinationSlot.ZoomedImportantRegion,
            ManualSourceSemanticRole.ImportantRegion,
            ManualSourceTreatment.Standard);
        return BuildPlan(request, [placement]);
    }

    private static ManualLayoutPlan BuildBlurredBackgroundPlan(ManualLayoutRequest request)
    {
        ManualLayoutSource background = request.Source(0);
        ManualLayoutSource foreground = request.Source(1);
        if (background.SourceAssetId != foreground.SourceAssetId
            || background.Origin != foreground.Origin
            || !background.DisplayGeometry.Equals(foreground.DisplayGeometry))
        {
            throw new ArgumentException(
                "Blurred background and foreground placements must use the same source.",
                nameof(request));
        }

        NormalizedRect backgroundDestination = request.Destination(0).Rect;
        NormalizedRect foregroundDestination = request.Destination(1).Rect;
        if (!ManualLayoutGeometry.Contains(backgroundDestination, foregroundDestination)
            || backgroundDestination == foregroundDestination)
        {
            throw new ArgumentException(
                "The blurred background must strictly contain the foreground destination.",
                nameof(request));
        }

        return BuildPlan(
            request,
            [
                CreatePlacement(
                    request,
                    background,
                    request.Destination(0),
                    ManualDestinationSlot.BlurredBackground,
                    ManualSourceSemanticRole.Subject,
                    ManualSourceTreatment.BlurredBackground),
                CreatePlacement(
                    request,
                    foreground,
                    request.Destination(1),
                    ManualDestinationSlot.ForegroundSubject,
                    ManualSourceSemanticRole.Subject,
                    ManualSourceTreatment.Standard),
            ]);
    }

    private static ManualLayoutPlan BuildFixedTemplatePlan(ManualLayoutRequest request)
    {
        ManualSourcePlacement placement = CreatePlacement(
            request,
            request.Source(0),
            request.Destination(0),
            ManualDestinationSlot.TemplateVideo,
            ManualSourceSemanticRole.TemplateVideo,
            ManualSourceTreatment.Standard);
        return BuildPlan(request, [placement]);
    }

    private static ManualSourcePlacement CreatePlacement(
        ManualLayoutRequest request,
        ManualLayoutSource source,
        ManualDestinationGeometry destination,
        ManualDestinationSlot slot,
        ManualSourceSemanticRole semanticRole,
        ManualSourceTreatment treatment)
    {
        if (!ManualLayoutGeometry.HasSameDisplayAspect(
            source.DisplayGeometry,
            source.Crop.Rect,
            request.OutputEnvelope,
            destination.Rect))
        {
            throw new ArgumentException(
                "The display-oriented source crop and output destination must have exactly equal aspect ratios.",
                nameof(request));
        }

        LayoutRegionRole regionRole = semanticRole == ManualSourceSemanticRole.Material
            ? LayoutRegionRole.Material
            : LayoutRegionRole.Video;
        var region = new LayoutRegion(
            RegionName(slot),
            destination.Rect,
            regionRole);
        var binding = new ManualDestinationRegion(slot, region);
        return new ManualSourcePlacement(source, binding, semanticRole, treatment);
    }

    private static void ValidateVerticalStack(
        ManualLayoutRequest request,
        ManualDestinationGeometry top,
        ManualDestinationGeometry bottom)
    {
        if (top.Rect.X != bottom.Rect.X || top.Rect.Width != bottom.Rect.Width)
        {
            throw new ArgumentException(
                "Top and bottom stack destinations must have aligned horizontal bounds.",
                nameof(request));
        }

        int boundaryComparison = ManualLayoutGeometry.CompareBottomToY(top.Rect, bottom.Rect);
        if (boundaryComparison < 0)
        {
            throw new ArgumentException(
                "Top and bottom stack destinations must not contain a vertical gap.",
                nameof(request));
        }

        if (boundaryComparison > 0)
        {
            throw new ArgumentException(
                "Top and bottom stack destinations must not overlap or be reversed.",
                nameof(request));
        }
    }

    private static ManualLayoutPlan BuildPlan(
        ManualLayoutRequest request,
        IReadOnlyList<ManualSourcePlacement> placements)
    {
        var regions = new List<LayoutRegion>(
            placements.Count + request.OutputEnvelope.Regions.Count);
        var regionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManualSourcePlacement placement in placements)
        {
            LayoutRegion region = placement.Destination.Region;
            regions.Add(region);
            regionNames.Add(region.Name);
        }

        foreach (LayoutRegion envelopeRegion in request.OutputEnvelope.Regions)
        {
            if (envelopeRegion.Role is not (LayoutRegionRole.Subtitle or LayoutRegionRole.Graphic))
            {
                continue;
            }

            if (!regionNames.Add(envelopeRegion.Name))
            {
                throw new ArgumentException(
                    "A retained output-envelope region conflicts with a manual destination.",
                    nameof(request));
            }

            regions.Add(envelopeRegion);
        }

        var derivedLayout = new LayoutSpec(
            request.DerivedLayoutSpecId,
            request.OutputEnvelope.OutputAspectWidth,
            request.OutputEnvelope.OutputAspectHeight,
            regions,
            [request.OutputEnvelope.SafeZones[0]]);
        return new ManualLayoutPlan(
            request.Mode,
            derivedLayout,
            placements,
            request.TemplateReference);
    }

    private static string RegionName(ManualDestinationSlot slot)
    {
        return slot switch
        {
            ManualDestinationSlot.SingleSubject => "manual-single-subject",
            ManualDestinationSlot.TopSpeaker => "manual-top-speaker",
            ManualDestinationSlot.BottomSpeaker => "manual-bottom-speaker",
            ManualDestinationSlot.StackedSpeaker => "manual-stacked-speaker",
            ManualDestinationSlot.StackedMaterial => "manual-stacked-material",
            ManualDestinationSlot.MainMaterial => "manual-main-material",
            ManualDestinationSlot.SpeakerInset => "manual-speaker-inset",
            ManualDestinationSlot.ZoomedImportantRegion => "manual-important-region",
            ManualDestinationSlot.BlurredBackground => "manual-blurred-background",
            ManualDestinationSlot.ForegroundSubject => "manual-foreground-subject",
            ManualDestinationSlot.TemplateVideo => "manual-template-video",
            _ => throw new ArgumentOutOfRangeException(
                nameof(slot),
                slot,
                "Unknown destination slot."),
        };
    }
}

internal static class ManualLayoutGeometry
{
    public static bool IsFullFrame(NormalizedRect rect)
    {
        return rect.X == default
            && rect.Y == default
            && rect.Width == new RationalNumber(1, 1)
            && rect.Height == new RationalNumber(1, 1);
    }

    public static bool Contains(NormalizedRect outer, NormalizedRect inner)
    {
        return outer.X <= inner.X
            && outer.Y <= inner.Y
            && CompareSums(outer.X, outer.Width, inner.X, inner.Width) >= 0
            && CompareSums(outer.Y, outer.Height, inner.Y, inner.Height) >= 0;
    }

    public static bool IsPixelAligned(
        NormalizedRect rect,
        SourceDisplayGeometry displayGeometry)
    {
        return IsIntegralProduct(rect.X, displayGeometry.Width)
            && IsIntegralProduct(rect.Y, displayGeometry.Height)
            && IsIntegralProduct(rect.Width, displayGeometry.Width)
            && IsIntegralProduct(rect.Height, displayGeometry.Height);
    }

    public static int CompareBottomToY(NormalizedRect top, NormalizedRect bottom)
    {
        return CompareSums(top.Y, top.Height, bottom.Y, default);
    }

    public static bool HasSameDisplayAspect(
        SourceDisplayGeometry sourceGeometry,
        NormalizedRect sourceCrop,
        LayoutSpec outputEnvelope,
        NormalizedRect destination)
    {
        BigInteger sourceAspectNumerator =
            (BigInteger)sourceGeometry.Width
            * sourceCrop.Width.Numerator
            * sourceCrop.Height.Denominator;
        BigInteger sourceAspectDenominator =
            (BigInteger)sourceGeometry.Height
            * sourceCrop.Height.Numerator
            * sourceCrop.Width.Denominator;
        BigInteger destinationAspectNumerator =
            (BigInteger)outputEnvelope.OutputAspectWidth
            * destination.Width.Numerator
            * destination.Height.Denominator;
        BigInteger destinationAspectDenominator =
            (BigInteger)outputEnvelope.OutputAspectHeight
            * destination.Height.Numerator
            * destination.Width.Denominator;

        return sourceAspectNumerator * destinationAspectDenominator
            == destinationAspectNumerator * sourceAspectDenominator;
    }

    private static bool IsIntegralProduct(RationalNumber value, int extent)
    {
        return ((BigInteger)value.Numerator * extent) % value.Denominator == 0;
    }

    private static int CompareSums(
        RationalNumber firstLeft,
        RationalNumber secondLeft,
        RationalNumber firstRight,
        RationalNumber secondRight)
    {
        BigInteger leftNumerator =
            ((BigInteger)firstLeft.Numerator * secondLeft.Denominator)
            + ((BigInteger)secondLeft.Numerator * firstLeft.Denominator);
        BigInteger leftDenominator =
            (BigInteger)firstLeft.Denominator * secondLeft.Denominator;
        BigInteger rightNumerator =
            ((BigInteger)firstRight.Numerator * secondRight.Denominator)
            + ((BigInteger)secondRight.Numerator * firstRight.Denominator);
        BigInteger rightDenominator =
            (BigInteger)firstRight.Denominator * secondRight.Denominator;

        return (leftNumerator * rightDenominator)
            .CompareTo(rightNumerator * leftDenominator);
    }
}