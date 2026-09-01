using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HubRadio.App.Policy;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.App.Integrity;

public enum IntegrityDisposition
{
    Unchanged,
    ReReview,
    PublicationBlocked,
}

public enum IntegrityEvidenceKind
{
    Snapshot,
    Project,
    Composition,
    RenderGraph,
    Timeline,
    Profile,
    Policy,
    Template,
    Input,
    Tool,
    Model,
    Font,
    Application,
}

public enum IntegrityFindingReason
{
    IdentityChanged,
    VersionChanged,
    ConfigurationChanged,
    EvidenceChanged,
    SameIdentityMutation,
    EvidenceMissing,
    UnexpectedEvidence,
    EvidenceInconsistent,
    FileMissing,
    FileInaccessible,
    NotRegularFile,
    ReparsePoint,
    SizeMismatch,
    DigestMismatch,
    VerificationFailed,
}

public enum IntegrityFileKind
{
    Input,
    ToolExecutable,
    ModelWeights,
    Font,
    Application,
    Original,
}

/// <summary>
/// Captures the approved or current semantic render evidence without any mutable lookup.
/// </summary>
public sealed class IntegrityEvidenceSnapshot
{
    public IntegrityEvidenceSnapshot(
        RenderManifest manifest,
        RenderGraph graph,
        RenderGraphValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(validationContext);

        Manifest = manifest;
        Graph = graph;
        ValidationContext = validationContext;
    }

    public RenderManifest Manifest { get; }

    public RenderGraph Graph { get; }

    public RenderGraphValidationContext ValidationContext { get; }
}

/// <summary>
/// Binds one current evidence item to an exact file. The path is deliberately not public.
/// </summary>
public sealed class IntegrityFileBinding
{
    private IntegrityFileBinding(
        IntegrityFileKey key,
        string componentIdentity,
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize)
    {
        if (!Enum.IsDefined(key.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        ArgumentNullException.ThrowIfNull(expectedDigest);
        var request = new BinaryVerificationRequest(
            componentIdentity,
            filePath,
            expectedDigest,
            expectedByteSize);

        Key = key;
        Kind = key.Kind;
        ComponentIdentity = request.ComponentIdentity;
        FilePath = request.FilePath;
        ExpectedDigest = request.ExpectedDigest;
        ExpectedByteSize = request.ExpectedByteSize;
    }

    public IntegrityFileKind Kind { get; }

    public string ComponentIdentity { get; }

    public ContentDigest ExpectedDigest { get; }

    public long ExpectedByteSize { get; }

    internal IntegrityFileKey Key { get; }

    internal string FilePath { get; }

    public static IntegrityFileBinding ForInput(
        EntityId<RenderInputIdTag> inputId,
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize)
    {
        ArgumentNullException.ThrowIfNull(inputId);
        return new IntegrityFileBinding(
            IntegrityFileKey.Input(inputId),
            string.Concat("input.", inputId.ToString()),
            filePath,
            expectedDigest,
            expectedByteSize);
    }

    public static IntegrityFileBinding ForOriginal(
        EntityId<AssetIdTag> assetId,
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        return new IntegrityFileBinding(
            IntegrityFileKey.Original(assetId),
            IntegrityContract.OriginalComponent(assetId),
            filePath,
            expectedDigest,
            expectedByteSize);
    }

    public static IntegrityFileBinding ForTool(
        string toolName,
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize)
    {
        IntegrityContract.ValidateKeyText(toolName, nameof(toolName));
        return new IntegrityFileBinding(
            IntegrityFileKey.Tool(toolName),
            IntegrityContract.ToolComponent(toolName),
            filePath,
            expectedDigest,
            expectedByteSize);
    }

    public static IntegrityFileBinding ForModel(
        string provider,
        string modelIdentifier,
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize)
    {
        IntegrityContract.ValidateKeyText(provider, nameof(provider));
        IntegrityContract.ValidateKeyText(
            modelIdentifier,
            nameof(modelIdentifier));
        return new IntegrityFileBinding(
            IntegrityFileKey.Model(provider, modelIdentifier),
            IntegrityContract.ModelComponent(provider, modelIdentifier),
            filePath,
            expectedDigest,
            expectedByteSize);
    }

    public static IntegrityFileBinding ForFont(
        string fontName,
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize)
    {
        IntegrityContract.ValidateKeyText(fontName, nameof(fontName));
        return new IntegrityFileBinding(
            IntegrityFileKey.Font(fontName),
            IntegrityContract.FontComponent(fontName),
            filePath,
            expectedDigest,
            expectedByteSize);
    }

    public static IntegrityFileBinding ForApplication(
        string filePath,
        ContentDigest expectedDigest,
        long expectedByteSize) =>
        new(
            IntegrityFileKey.Application,
            IntegrityContract.ApplicationComponent,
            filePath,
            expectedDigest,
            expectedByteSize);
}

public sealed class IntegrityMonitorRequest
{
    public IntegrityMonitorRequest(
        IntegrityEvidenceSnapshot approved,
        IntegrityEvidenceSnapshot current,
        IReadOnlyList<IntegrityFileBinding> currentFiles)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentFiles);

        var copied = new IntegrityFileBinding[currentFiles.Count];
        var keys = new HashSet<IntegrityFileKey>();
        for (int index = 0; index < copied.Length; index++)
        {
            IntegrityFileBinding binding = currentFiles[index]
                ?? throw new ArgumentException(
                    "Current integrity files must not contain null values.",
                    nameof(currentFiles));
            if (!keys.Add(binding.Key))
            {
                throw new ArgumentException(
                    "Current integrity files must use unique evidence identities.",
                    nameof(currentFiles));
            }

            copied[index] = binding;
        }

        Approved = approved;
        Current = current;
        CurrentFiles = Array.AsReadOnly(copied);
    }

    public IntegrityEvidenceSnapshot Approved { get; }

    public IntegrityEvidenceSnapshot Current { get; }

    public IReadOnlyList<IntegrityFileBinding> CurrentFiles { get; }
}

public sealed class IntegrityFinding : IEquatable<IntegrityFinding>
{
    internal IntegrityFinding(
        IntegrityEvidenceKind evidenceKind,
        IntegrityFindingReason reason,
        IntegrityDisposition disposition,
        string componentIdentity,
        ContentDigest? expectedDigest,
        ContentDigest? observedDigest,
        long? expectedByteSize,
        long? observedByteSize)
    {
        if (!Enum.IsDefined(evidenceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceKind));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (disposition is not IntegrityDisposition.ReReview
            and not IntegrityDisposition.PublicationBlocked)
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        IntegrityContract.ValidateComponentIdentity(
            componentIdentity,
            nameof(componentIdentity));
        if (expectedByteSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        if (observedByteSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedByteSize));
        }

        EvidenceKind = evidenceKind;
        Reason = reason;
        Disposition = disposition;
        ComponentIdentity = componentIdentity;
        ExpectedDigest = expectedDigest;
        ObservedDigest = observedDigest;
        ExpectedByteSize = expectedByteSize;
        ObservedByteSize = observedByteSize;
    }

    public IntegrityEvidenceKind EvidenceKind { get; }

    public IntegrityFindingReason Reason { get; }

    public IntegrityDisposition Disposition { get; }

    public string ComponentIdentity { get; }

    public ContentDigest? ExpectedDigest { get; }

    public ContentDigest? ObservedDigest { get; }

    public long? ExpectedByteSize { get; }

    public long? ObservedByteSize { get; }

    public bool Equals(IntegrityFinding? other) =>
        other is not null
        && EvidenceKind == other.EvidenceKind
        && Reason == other.Reason
        && Disposition == other.Disposition
        && StringComparer.Ordinal.Equals(
            ComponentIdentity,
            other.ComponentIdentity)
        && ExpectedDigest == other.ExpectedDigest
        && ObservedDigest == other.ObservedDigest
        && ExpectedByteSize == other.ExpectedByteSize
        && ObservedByteSize == other.ObservedByteSize;

    public override bool Equals(object? obj) =>
        obj is IntegrityFinding other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            EvidenceKind,
            Reason,
            Disposition,
            StringComparer.Ordinal.GetHashCode(ComponentIdentity),
            ExpectedDigest,
            ObservedDigest,
            ExpectedByteSize,
            ObservedByteSize);
}

public sealed class IntegrityMonitorResult
{
    internal IntegrityMonitorResult(
        IntegrityDisposition disposition,
        IReadOnlyList<IntegrityFinding> findings,
        ContentDigest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        bool hasBlocked = findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.PublicationBlocked);
        bool hasReReview = findings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.ReReview);
        bool consistent = disposition switch
        {
            IntegrityDisposition.Unchanged => findings.Count == 0,
            IntegrityDisposition.ReReview =>
                findings.Count != 0 && hasReReview && !hasBlocked,
            IntegrityDisposition.PublicationBlocked => hasBlocked,
            _ => false,
        };
        if (!consistent)
        {
            throw new ArgumentException(
                "The integrity disposition and findings are inconsistent.",
                nameof(disposition));
        }

        Disposition = disposition;
        Findings = Array.AsReadOnly(findings.ToArray());
        EvidenceDigest = evidenceDigest;
        RequiredReviewState = disposition switch
        {
            IntegrityDisposition.Unchanged => null,
            IntegrityDisposition.ReReview => ReviewState.ChangesRequested,
            IntegrityDisposition.PublicationBlocked =>
                ReviewState.PublicationBlocked,
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };
    }

    public IntegrityDisposition Disposition { get; }

    public ReviewState? RequiredReviewState { get; }

    public IReadOnlyList<IntegrityFinding> Findings { get; }

    public ContentDigest EvidenceDigest { get; }

    public bool IsUnchanged => Disposition == IntegrityDisposition.Unchanged;
}

/// <summary>
/// Revalidates approved render evidence without repairing or mutating any evidence.
/// </summary>
public sealed class IntegrityMonitor
{
    private readonly BinaryVerifier _binaryVerifier;

    public IntegrityMonitor()
        : this(new BinaryVerifier())
    {
    }

    public IntegrityMonitor(BinaryVerifier binaryVerifier)
    {
        ArgumentNullException.ThrowIfNull(binaryVerifier);
        _binaryVerifier = binaryVerifier;
    }

    public async Task<IntegrityMonitorResult> EvaluateAsync(
        IntegrityMonitorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new FindingCollector();
        ValidateSnapshot(
            request.Approved,
            "snapshot.approved",
            findings,
            cancellationToken);
        ValidateSnapshot(
            request.Current,
            "snapshot.current",
            findings,
            cancellationToken);
        CompareSnapshots(
            request.Approved,
            request.Current,
            findings,
            cancellationToken);
        await VerifyCurrentFilesAsync(
            request,
            findings,
            cancellationToken).ConfigureAwait(false);

        IntegrityFinding[] canonicalFindings = findings.ToCanonicalArray();
        IntegrityDisposition disposition = canonicalFindings.Any(static finding =>
            finding.Disposition == IntegrityDisposition.PublicationBlocked)
                ? IntegrityDisposition.PublicationBlocked
                : canonicalFindings.Length != 0
                    ? IntegrityDisposition.ReReview
                    : IntegrityDisposition.Unchanged;
        ContentDigest evidenceDigest = ComputeEvidenceDigest(
            request,
            disposition,
            canonicalFindings);
        return new IntegrityMonitorResult(
            disposition,
            canonicalFindings,
            evidenceDigest);
    }

    private static void ValidateSnapshot(
        IntegrityEvidenceSnapshot snapshot,
        string componentIdentity,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool consistent;
        try
        {
            RenderGraphValidationResult validation = RenderGraphValidator.Validate(
                snapshot.Graph,
                snapshot.ValidationContext,
                cancellationToken);
            RenderManifest manifest = snapshot.Manifest;
            RenderGraph graph = snapshot.Graph;
            RenderGraphValidationContext context = snapshot.ValidationContext;
            consistent = validation.IsValid
                && manifest.ProjectId == context.Project.Id
                && manifest.CompositionVersionId
                    == context.CompositionVersion.Id
                && manifest.CompositionVersionDigest
                    == context.CompositionVersion.ContentDigest
                && manifest.CompositionVersionId == graph.CompositionVersionId
                && manifest.CompositionVersionDigest
                    == graph.CompositionVersionDigest
                && manifest.TimelineMapId == context.TimelineMap.Id
                && manifest.TimelineMapDigest == context.TimelineMapDigest
                && manifest.TimelineMapId == graph.TimelineMapId
                && manifest.TimelineMapDigest == graph.TimelineMapDigest
                && manifest.ProfileId == context.Profile.Id
                && manifest.ProfileDigest == context.Profile.ProfileDigest
                && manifest.ProfileId == graph.ProfileId
                && manifest.ProfileDigest == graph.ProfileDigest
                && manifest.TemplateId == context.Template.Id
                && manifest.TemplateVersionDigest
                    == context.Template.VersionDigest
                && manifest.TemplateId == graph.Template.Id
                && manifest.TemplateVersionDigest
                    == graph.Template.VersionDigest
                && HaveUniqueManifestKeys(manifest)
                && InputDigestsMatch(manifest.Inputs, graph.Inputs)
                && FontsMatch(manifest.Fonts, graph.Fonts);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch
        {
            consistent = false;
        }

        if (!consistent)
        {
            findings.Add(
                IntegrityEvidenceKind.Snapshot,
                IntegrityFindingReason.EvidenceInconsistent,
                IntegrityDisposition.PublicationBlocked,
                componentIdentity);
        }
    }

    private static bool HaveUniqueManifestKeys(RenderManifest manifest) =>
        manifest.Inputs.Select(static input => input.Name)
            .Distinct(StringComparer.Ordinal).Count() == manifest.Inputs.Count
        && manifest.Tools.Select(static tool => tool.Name)
            .Distinct(StringComparer.Ordinal).Count() == manifest.Tools.Count
        && manifest.Models.Select(static model =>
            new ModelEvidenceKey(
                model.Provider,
                model.ModelIdentifier))
            .Distinct().Count() == manifest.Models.Count
        && manifest.Fonts.Select(static font => font.Name)
            .Distinct(StringComparer.Ordinal).Count() == manifest.Fonts.Count;

    private static bool InputDigestsMatch(
        IReadOnlyList<RenderInputEvidence> manifestInputs,
        IReadOnlyList<RenderGraphInput> graphInputs)
    {
        string[] manifestDigests = manifestInputs
            .Select(static input => input.ContentDigest.Hex)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] graphDigests = graphInputs
            .Select(static input => input.ContentDigest.Hex)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return manifestDigests.SequenceEqual(
            graphDigests,
            StringComparer.Ordinal);
    }

    private static bool FontsMatch(
        IReadOnlyList<RenderFontEvidence> manifestFonts,
        IReadOnlyList<RenderFontEvidence> graphFonts)
    {
        if (manifestFonts.Count != graphFonts.Count)
        {
            return false;
        }

        return manifestFonts.All(font => graphFonts.Any(graphFont =>
            string.Equals(
                graphFont.Name,
                font.Name,
                StringComparison.Ordinal)
            && graphFont.FileDigest == font.FileDigest));
    }

    private static void CompareSnapshots(
        IntegrityEvidenceSnapshot approved,
        IntegrityEvidenceSnapshot current,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompareProject(approved, current, findings);
        CompareComposition(approved, current, findings);
        CompareIdentityBoundEvidence(
            IntegrityEvidenceKind.Timeline,
            "timeline",
            approved.ValidationContext.TimelineMap.Id.ToString(),
            approved.ValidationContext.TimelineMapDigest,
            current.ValidationContext.TimelineMap.Id.ToString(),
            current.ValidationContext.TimelineMapDigest,
            findings);
        CompareProfile(approved, current, findings);
        CompareLayout(approved, current, findings);
        CompareTemplate(approved, current, findings);

        ContentDigest approvedGraphDigest =
            ExportPolicyDigest.ComputeRenderGraphDigest(approved.Graph);
        ContentDigest currentGraphDigest =
            ExportPolicyDigest.ComputeRenderGraphDigest(current.Graph);
        CompareIdentityBoundEvidence(
            IntegrityEvidenceKind.RenderGraph,
            "render-graph",
            approved.Graph.Id.ToString(),
            approvedGraphDigest,
            current.Graph.Id.ToString(),
            currentGraphDigest,
            findings);

        if (approved.Manifest.ConfigurationDigest
            != current.Manifest.ConfigurationDigest)
        {
            findings.Add(
                IntegrityEvidenceKind.Policy,
                IntegrityFindingReason.ConfigurationChanged,
                IntegrityDisposition.ReReview,
                "policy.configuration",
                approved.Manifest.ConfigurationDigest,
                current.Manifest.ConfigurationDigest);
        }

        CompareApplication(approved.Manifest, current.Manifest, findings);
        CompareManifestInputs(
            approved.Manifest.Inputs,
            current.Manifest.Inputs,
            findings,
            cancellationToken);
        CompareGraphInputs(
            approved.Graph.Inputs,
            current.Graph.Inputs,
            findings,
            cancellationToken);
        CompareTools(
            approved.Manifest.Tools,
            current.Manifest.Tools,
            findings,
            cancellationToken);
        CompareModels(
            approved.Manifest.Models,
            current.Manifest.Models,
            findings,
            cancellationToken);
        CompareFonts(
            approved.Manifest.Fonts,
            current.Manifest.Fonts,
            findings,
            cancellationToken);
    }

    private static void CompareProject(
        IntegrityEvidenceSnapshot approved,
        IntegrityEvidenceSnapshot current,
        FindingCollector findings)
    {
        Project expected = approved.ValidationContext.Project;
        Project observed = current.ValidationContext.Project;
        ContentDigest expectedDigest =
            ExportPolicyDigest.ComputeProjectDigest(expected);
        ContentDigest observedDigest =
            ExportPolicyDigest.ComputeProjectDigest(observed);
        bool identityChanged = expected.Id != observed.Id;
        if (identityChanged || expectedDigest != observedDigest)
        {
            findings.Add(
                IntegrityEvidenceKind.Project,
                identityChanged
                    ? IntegrityFindingReason.IdentityChanged
                    : IntegrityFindingReason.EvidenceChanged,
                IntegrityDisposition.ReReview,
                IntegrityContract.IdentityComponent(
                    "project",
                    expected.Id.ToString(),
                    observed.Id.ToString()),
                expectedDigest,
                observedDigest);
        }

        Dictionary<string, Asset> expectedAssets = expected.Assets.ToDictionary(
            static asset => asset.Id.ToString(),
            StringComparer.Ordinal);
        Dictionary<string, Asset> observedAssets = observed.Assets.ToDictionary(
            static asset => asset.Id.ToString(),
            StringComparer.Ordinal);
        foreach (string id in expectedAssets.Keys.Intersect(
            observedAssets.Keys,
            StringComparer.Ordinal))
        {
            Asset expectedAsset = expectedAssets[id];
            Asset observedAsset = observedAssets[id];
            if (expectedAsset.ContentDigest != observedAsset.ContentDigest
                || expectedAsset.ByteSize != observedAsset.ByteSize)
            {
                findings.Add(
                    IntegrityEvidenceKind.Input,
                    IntegrityFindingReason.SameIdentityMutation,
                    IntegrityDisposition.PublicationBlocked,
                    IntegrityContract.OriginalComponent(expectedAsset.Id),
                    expectedAsset.ContentDigest,
                    observedAsset.ContentDigest,
                    expectedAsset.ByteSize,
                    observedAsset.ByteSize);
            }
        }
    }

    private static void CompareProfile(
        IntegrityEvidenceSnapshot approved,
        IntegrityEvidenceSnapshot current,
        FindingCollector findings)
    {
        RenderProfile expected = approved.ValidationContext.Profile;
        RenderProfile observed = current.ValidationContext.Profile;
        bool identityOrDigestChanged = CompareIdentityBoundEvidence(
            IntegrityEvidenceKind.Profile,
            "profile",
            expected.Id.ToString(),
            expected.ProfileDigest,
            observed.Id.ToString(),
            observed.ProfileDigest,
            findings);
        if (!identityOrDigestChanged && !expected.Equals(observed))
        {
            findings.Add(
                IntegrityEvidenceKind.Profile,
                IntegrityFindingReason.SameIdentityMutation,
                IntegrityDisposition.PublicationBlocked,
                IntegrityContract.IdentityComponent(
                    "profile",
                    expected.Id.ToString()),
                ComputeProfileSemanticDigest(expected),
                ComputeProfileSemanticDigest(observed));
        }
    }

    private static void CompareComposition(
        IntegrityEvidenceSnapshot approved,
        IntegrityEvidenceSnapshot current,
        FindingCollector findings)
    {
        CompositionVersion expected =
            approved.ValidationContext.CompositionVersion;
        CompositionVersion observed =
            current.ValidationContext.CompositionVersion;
        bool identityOrDigestChanged = CompareIdentityBoundEvidence(
            IntegrityEvidenceKind.Composition,
            "composition",
            expected.Id.ToString(),
            expected.ContentDigest,
            observed.Id.ToString(),
            observed.ContentDigest,
            findings);
        ContentDigest expectedSemanticDigest =
            ComputeCompositionSemanticDigest(expected);
        ContentDigest observedSemanticDigest =
            ComputeCompositionSemanticDigest(observed);
        if (!identityOrDigestChanged
            && expectedSemanticDigest != observedSemanticDigest)
        {
            findings.Add(
                IntegrityEvidenceKind.Composition,
                IntegrityFindingReason.SameIdentityMutation,
                IntegrityDisposition.PublicationBlocked,
                IntegrityContract.IdentityComponent(
                    "composition",
                    expected.Id.ToString()),
                expectedSemanticDigest,
                observedSemanticDigest);
        }
    }

    private static void CompareLayout(
        IntegrityEvidenceSnapshot approved,
        IntegrityEvidenceSnapshot current,
        FindingCollector findings)
    {
        LayoutSpec expected = approved.ValidationContext.LayoutSpec;
        LayoutSpec observed = current.ValidationContext.LayoutSpec;
        bool identityOrDigestChanged = CompareIdentityBoundEvidence(
            IntegrityEvidenceKind.Policy,
            "layout",
            expected.Id.ToString(),
            approved.ValidationContext.LayoutSpecDigest,
            observed.Id.ToString(),
            current.ValidationContext.LayoutSpecDigest,
            findings);
        ContentDigest expectedSemanticDigest =
            ComputeLayoutSemanticDigest(expected);
        ContentDigest observedSemanticDigest =
            ComputeLayoutSemanticDigest(observed);
        if (!identityOrDigestChanged
            && expectedSemanticDigest != observedSemanticDigest)
        {
            findings.Add(
                IntegrityEvidenceKind.Policy,
                IntegrityFindingReason.SameIdentityMutation,
                IntegrityDisposition.PublicationBlocked,
                IntegrityContract.IdentityComponent(
                    "layout",
                    expected.Id.ToString()),
                expectedSemanticDigest,
                observedSemanticDigest);
        }
    }

    private static void CompareTemplate(
        IntegrityEvidenceSnapshot approved,
        IntegrityEvidenceSnapshot current,
        FindingCollector findings)
    {
        var expected = approved.ValidationContext.Template;
        var observed = current.ValidationContext.Template;
        bool identityOrDigestChanged = CompareIdentityBoundEvidence(
            IntegrityEvidenceKind.Template,
            "template",
            expected.Id.ToString(),
            expected.VersionDigest,
            observed.Id.ToString(),
            observed.VersionDigest,
            findings);
        if (!identityOrDigestChanged && !expected.Equals(observed))
        {
            findings.Add(
                IntegrityEvidenceKind.Template,
                IntegrityFindingReason.SameIdentityMutation,
                IntegrityDisposition.PublicationBlocked,
                IntegrityContract.IdentityComponent(
                    "template",
                    expected.Id.ToString()),
                ComputeTemplateSemanticDigest(expected),
                ComputeTemplateSemanticDigest(observed));
        }
    }

    private static bool CompareIdentityBoundEvidence(
        IntegrityEvidenceKind kind,
        string prefix,
        string approvedIdentity,
        ContentDigest approvedDigest,
        string currentIdentity,
        ContentDigest currentDigest,
        FindingCollector findings)
    {
        bool identityChanged = !string.Equals(
            approvedIdentity,
            currentIdentity,
            StringComparison.Ordinal);
        bool digestChanged = approvedDigest != currentDigest;
        if (!identityChanged && !digestChanged)
        {
            return false;
        }

        findings.Add(
            kind,
            identityChanged
                ? IntegrityFindingReason.IdentityChanged
                : IntegrityFindingReason.SameIdentityMutation,
            identityChanged
                ? IntegrityDisposition.ReReview
                : IntegrityDisposition.PublicationBlocked,
            IntegrityContract.IdentityComponent(
                prefix,
                approvedIdentity,
                currentIdentity),
            approvedDigest,
            currentDigest);
        return true;
    }

    private static void CompareApplication(
        RenderManifest approved,
        RenderManifest current,
        FindingCollector findings)
    {
        if (!string.Equals(
                approved.ApplicationVersion,
                current.ApplicationVersion,
                StringComparison.Ordinal))
        {
            findings.Add(
                IntegrityEvidenceKind.Application,
                IntegrityFindingReason.VersionChanged,
                IntegrityDisposition.ReReview,
                IntegrityContract.ApplicationComponent,
                approved.ApplicationDigest,
                current.ApplicationDigest);
            return;
        }

        if (approved.ApplicationDigest != current.ApplicationDigest)
        {
            findings.Add(
                IntegrityEvidenceKind.Application,
                IntegrityFindingReason.SameIdentityMutation,
                IntegrityDisposition.PublicationBlocked,
                IntegrityContract.ApplicationComponent,
                approved.ApplicationDigest,
                current.ApplicationDigest);
        }
    }

    private static void CompareManifestInputs(
        IReadOnlyList<RenderInputEvidence> approved,
        IReadOnlyList<RenderInputEvidence> current,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        if (!TryCreateMap(
                approved,
                static input => input.Name,
                out Dictionary<string, RenderInputEvidence>? approvedByName)
            || !TryCreateMap(
                current,
                static input => input.Name,
                out Dictionary<string, RenderInputEvidence>? currentByName))
        {
            return;
        }

        foreach (string name in UnionKeys(approvedByName, currentByName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasApproved = approvedByName.TryGetValue(
                name,
                out RenderInputEvidence? expected);
            bool hasCurrent = currentByName.TryGetValue(
                name,
                out RenderInputEvidence? observed);
            string component = IntegrityContract.InputLabelComponent(name);
            if (!hasApproved || !hasCurrent)
            {
                findings.Add(
                    IntegrityEvidenceKind.Input,
                    IntegrityFindingReason.IdentityChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected?.ContentDigest,
                    observed?.ContentDigest);
            }
            else if (expected!.ContentDigest != observed!.ContentDigest)
            {
                findings.Add(
                    IntegrityEvidenceKind.Input,
                    IntegrityFindingReason.EvidenceChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected.ContentDigest,
                    observed.ContentDigest);
            }
        }
    }

    private static void CompareGraphInputs(
        IReadOnlyList<RenderGraphInput> approved,
        IReadOnlyList<RenderGraphInput> current,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        Dictionary<string, RenderGraphInput> approvedById = approved.ToDictionary(
            static input => input.Id.ToString(),
            StringComparer.Ordinal);
        Dictionary<string, RenderGraphInput> currentById = current.ToDictionary(
            static input => input.Id.ToString(),
            StringComparer.Ordinal);
        foreach (string id in UnionKeys(approvedById, currentById))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasApproved = approvedById.TryGetValue(
                id,
                out RenderGraphInput? expected);
            bool hasCurrent = currentById.TryGetValue(
                id,
                out RenderGraphInput? observed);
            string component = string.Concat("input.", id);
            if (!hasApproved || !hasCurrent)
            {
                findings.Add(
                    IntegrityEvidenceKind.Input,
                    IntegrityFindingReason.IdentityChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected?.ContentDigest,
                    observed?.ContentDigest,
                    expected?.ByteSize,
                    observed?.ByteSize);
                continue;
            }

            if (expected!.Kind != observed!.Kind
                || expected.Origin != observed.Origin
                || expected.ContentDigest != observed.ContentDigest
                || expected.ByteSize != observed.ByteSize)
            {
                findings.Add(
                    IntegrityEvidenceKind.Input,
                    IntegrityFindingReason.SameIdentityMutation,
                    IntegrityDisposition.PublicationBlocked,
                    component,
                    expected.ContentDigest,
                    observed.ContentDigest,
                    expected.ByteSize,
                    observed.ByteSize);
            }
        }
    }

    private static void CompareTools(
        IReadOnlyList<RenderToolEvidence> approved,
        IReadOnlyList<RenderToolEvidence> current,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        if (!TryCreateMap(
                approved,
                static tool => tool.Name,
                out Dictionary<string, RenderToolEvidence>? approvedByName)
            || !TryCreateMap(
                current,
                static tool => tool.Name,
                out Dictionary<string, RenderToolEvidence>? currentByName))
        {
            return;
        }

        foreach (string name in UnionKeys(approvedByName, currentByName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasApproved = approvedByName.TryGetValue(
                name,
                out RenderToolEvidence? expected);
            bool hasCurrent = currentByName.TryGetValue(
                name,
                out RenderToolEvidence? observed);
            string component = IntegrityContract.ToolComponent(name);
            if (!hasApproved || !hasCurrent)
            {
                findings.Add(
                    IntegrityEvidenceKind.Tool,
                    IntegrityFindingReason.IdentityChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected?.ExecutableDigest,
                    observed?.ExecutableDigest);
                continue;
            }

            bool revisionChanged = false;
            if (!string.Equals(
                    expected!.Version,
                    observed!.Version,
                    StringComparison.Ordinal))
            {
                revisionChanged = true;
                findings.Add(
                    IntegrityEvidenceKind.Tool,
                    IntegrityFindingReason.VersionChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected.ExecutableDigest,
                    observed.ExecutableDigest);
            }

            if (!string.Equals(
                    expected.BuildConfiguration,
                    observed.BuildConfiguration,
                    StringComparison.Ordinal))
            {
                revisionChanged = true;
                findings.Add(
                    IntegrityEvidenceKind.Tool,
                    IntegrityFindingReason.ConfigurationChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected.ExecutableDigest,
                    observed.ExecutableDigest);
            }

            if (!revisionChanged
                && (expected.ExecutableDigest != observed.ExecutableDigest
                    || expected.ContainerImageDigest
                        != observed.ContainerImageDigest))
            {
                bool executableChanged = expected.ExecutableDigest
                    != observed.ExecutableDigest;
                findings.Add(
                    IntegrityEvidenceKind.Tool,
                    IntegrityFindingReason.SameIdentityMutation,
                    IntegrityDisposition.PublicationBlocked,
                    component,
                    executableChanged
                        ? expected.ExecutableDigest
                        : expected.ContainerImageDigest,
                    executableChanged
                        ? observed.ExecutableDigest
                        : observed.ContainerImageDigest);
            }
        }
    }

    private static void CompareModels(
        IReadOnlyList<RenderModelEvidence> approved,
        IReadOnlyList<RenderModelEvidence> current,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        if (!TryCreateModelMap(approved, out var approvedByKey)
            || !TryCreateModelMap(current, out var currentByKey))
        {
            return;
        }

        foreach (ModelEvidenceKey key in approvedByKey.Keys
            .Concat(currentByKey.Keys)
            .Distinct()
            .OrderBy(static key => key.Provider, StringComparer.Ordinal)
            .ThenBy(static key => key.Identifier, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasApproved = approvedByKey.TryGetValue(
                key,
                out RenderModelEvidence? expected);
            bool hasCurrent = currentByKey.TryGetValue(
                key,
                out RenderModelEvidence? observed);
            RenderModelEvidence identity = expected ?? observed!;
            string component = IntegrityContract.ModelComponent(
                identity.Provider,
                identity.ModelIdentifier);
            if (!hasApproved || !hasCurrent)
            {
                findings.Add(
                    IntegrityEvidenceKind.Model,
                    IntegrityFindingReason.IdentityChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected?.WeightsDigest,
                    observed?.WeightsDigest);
                continue;
            }

            if (!string.Equals(
                    expected!.ModelVersion,
                    observed!.ModelVersion,
                    StringComparison.Ordinal))
            {
                findings.Add(
                    IntegrityEvidenceKind.Model,
                    IntegrityFindingReason.VersionChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected.WeightsDigest,
                    observed.WeightsDigest);
            }
            else if (expected.WeightsDigest != observed.WeightsDigest)
            {
                findings.Add(
                    IntegrityEvidenceKind.Model,
                    IntegrityFindingReason.SameIdentityMutation,
                    IntegrityDisposition.PublicationBlocked,
                    component,
                    expected.WeightsDigest,
                    observed.WeightsDigest);
            }
        }
    }

    private static void CompareFonts(
        IReadOnlyList<RenderFontEvidence> approved,
        IReadOnlyList<RenderFontEvidence> current,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        if (!TryCreateMap(
                approved,
                static font => font.Name,
                out Dictionary<string, RenderFontEvidence>? approvedByName)
            || !TryCreateMap(
                current,
                static font => font.Name,
                out Dictionary<string, RenderFontEvidence>? currentByName))
        {
            return;
        }

        foreach (string name in UnionKeys(approvedByName, currentByName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasApproved = approvedByName.TryGetValue(
                name,
                out RenderFontEvidence? expected);
            bool hasCurrent = currentByName.TryGetValue(
                name,
                out RenderFontEvidence? observed);
            string component = IntegrityContract.FontComponent(name);
            if (!hasApproved || !hasCurrent)
            {
                findings.Add(
                    IntegrityEvidenceKind.Font,
                    IntegrityFindingReason.IdentityChanged,
                    IntegrityDisposition.ReReview,
                    component,
                    expected?.FileDigest,
                    observed?.FileDigest);
            }
            else if (expected!.FileDigest != observed!.FileDigest)
            {
                findings.Add(
                    IntegrityEvidenceKind.Font,
                    IntegrityFindingReason.SameIdentityMutation,
                    IntegrityDisposition.PublicationBlocked,
                    component,
                    expected.FileDigest,
                    observed.FileDigest);
            }
        }
    }

    private async Task VerifyCurrentFilesAsync(
        IntegrityMonitorRequest request,
        FindingCollector findings,
        CancellationToken cancellationToken)
    {
        Dictionary<IntegrityFileKey, ExpectedFileEvidence> expected =
            BuildExpectedFiles(request.Current);
        Dictionary<IntegrityFileKey, IntegrityFileBinding> bindings =
            request.CurrentFiles.ToDictionary(static binding => binding.Key);

        foreach (ExpectedFileEvidence evidence in expected.Values
            .OrderBy(static value => value.Key.Kind)
            .ThenBy(static value => value.ComponentIdentity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bindings.TryGetValue(
                    evidence.Key,
                    out IntegrityFileBinding? binding))
            {
                findings.Add(
                    evidence.EvidenceKind,
                    IntegrityFindingReason.EvidenceMissing,
                    IntegrityDisposition.PublicationBlocked,
                    evidence.ComponentIdentity,
                    evidence.Digest,
                    observedDigest: null,
                    evidence.ByteSize,
                    observedByteSize: null);
                continue;
            }

            long verificationSize = evidence.ByteSize
                ?? binding.ExpectedByteSize;
            bool bindingMatches = binding.ExpectedDigest == evidence.Digest
                && (evidence.ByteSize is null
                    || binding.ExpectedByteSize == evidence.ByteSize.Value);
            if (!bindingMatches)
            {
                findings.Add(
                    evidence.EvidenceKind,
                    IntegrityFindingReason.EvidenceInconsistent,
                    IntegrityDisposition.PublicationBlocked,
                    evidence.ComponentIdentity,
                    evidence.Digest,
                    binding.ExpectedDigest,
                    evidence.ByteSize,
                    binding.ExpectedByteSize);
            }

            try
            {
                _ = await _binaryVerifier.VerifyAsync(
                    new BinaryVerificationRequest(
                        evidence.ComponentIdentity,
                        binding.FilePath,
                        evidence.Digest,
                        verificationSize),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested
                    && exception.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch (BinaryVerificationException exception)
            {
                findings.Add(
                    evidence.EvidenceKind,
                    MapVerificationReason(exception.Reason),
                    IntegrityDisposition.PublicationBlocked,
                    evidence.ComponentIdentity,
                    evidence.Digest,
                    exception.Result.ActualDigest,
                    verificationSize,
                    exception.Result.ActualByteSize);
            }
            catch
            {
                findings.Add(
                    evidence.EvidenceKind,
                    IntegrityFindingReason.VerificationFailed,
                    IntegrityDisposition.PublicationBlocked,
                    evidence.ComponentIdentity,
                    evidence.Digest,
                    observedDigest: null,
                    verificationSize,
                    observedByteSize: null);
            }
        }

        foreach (IntegrityFileBinding binding in request.CurrentFiles
            .Where(binding => !expected.ContainsKey(binding.Key)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(
                GetEvidenceKind(binding.Kind),
                IntegrityFindingReason.UnexpectedEvidence,
                IntegrityDisposition.PublicationBlocked,
                binding.ComponentIdentity,
                expectedDigest: null,
                binding.ExpectedDigest,
                expectedByteSize: null,
                binding.ExpectedByteSize);
        }
    }

    private static Dictionary<IntegrityFileKey, ExpectedFileEvidence>
        BuildExpectedFiles(IntegrityEvidenceSnapshot current)
    {
        var expected = new Dictionary<IntegrityFileKey, ExpectedFileEvidence>();
        foreach (Asset asset in GetReferencedAssets(current))
        {
            IntegrityFileKey key = IntegrityFileKey.Original(asset.Id);
            expected.TryAdd(
                key,
                new ExpectedFileEvidence(
                    key,
                    IntegrityEvidenceKind.Input,
                    IntegrityContract.OriginalComponent(asset.Id),
                    asset.ContentDigest,
                    asset.ByteSize));
        }

        foreach (RenderGraphInput input in current.Graph.Inputs)
        {
            IntegrityFileKey key = IntegrityFileKey.Input(input.Id);
            expected.TryAdd(
                key,
                new ExpectedFileEvidence(
                    key,
                    IntegrityEvidenceKind.Input,
                    string.Concat("input.", input.Id.ToString()),
                    input.ContentDigest,
                    input.ByteSize));
        }

        foreach (RenderToolEvidence tool in current.Manifest.Tools)
        {
            IntegrityFileKey key = IntegrityFileKey.Tool(tool.Name);
            expected.TryAdd(
                key,
                new ExpectedFileEvidence(
                    key,
                    IntegrityEvidenceKind.Tool,
                    IntegrityContract.ToolComponent(tool.Name),
                    tool.ExecutableDigest,
                    ByteSize: null));
        }

        foreach (RenderModelEvidence model in current.Manifest.Models)
        {
            IntegrityFileKey key = IntegrityFileKey.Model(
                model.Provider,
                model.ModelIdentifier);
            expected.TryAdd(
                key,
                new ExpectedFileEvidence(
                    key,
                    IntegrityEvidenceKind.Model,
                    IntegrityContract.ModelComponent(
                        model.Provider,
                        model.ModelIdentifier),
                    model.WeightsDigest,
                    ByteSize: null));
        }

        foreach (RenderFontEvidence font in current.Manifest.Fonts)
        {
            IntegrityFileKey key = IntegrityFileKey.Font(font.Name);
            expected.TryAdd(
                key,
                new ExpectedFileEvidence(
                    key,
                    IntegrityEvidenceKind.Font,
                    IntegrityContract.FontComponent(font.Name),
                    font.FileDigest,
                    ByteSize: null));
        }

        expected.Add(
            IntegrityFileKey.Application,
            new ExpectedFileEvidence(
                IntegrityFileKey.Application,
                IntegrityEvidenceKind.Application,
                IntegrityContract.ApplicationComponent,
                current.Manifest.ApplicationDigest,
                ByteSize: null));
        return expected;
    }

    private static IEnumerable<Asset> GetReferencedAssets(
        IntegrityEvidenceSnapshot snapshot)
    {
        RenderGraphValidationContext context = snapshot.ValidationContext;
        var assetIds = new HashSet<EntityId<AssetIdTag>>();
        foreach (RenderGraphInput input in snapshot.Graph.Inputs)
        {
            AddReferencedAsset(input.Origin, context, assetIds);
        }

        foreach (RenderNode node in snapshot.Graph.Nodes)
        {
            if (node.Origin is not null)
            {
                AddReferencedAsset(node.Origin, context, assetIds);
            }
        }

        return context.Project.Assets
            .Where(asset => assetIds.Contains(asset.Id))
            .OrderBy(static asset => asset.Id.ToString(), StringComparer.Ordinal);
    }

    private static void AddReferencedAsset(
        OriginRef origin,
        RenderGraphValidationContext context,
        ISet<EntityId<AssetIdTag>> assetIds)
    {
        if (origin.Kind == OriginKind.Asset
            && origin.AssetId is EntityId<AssetIdTag> assetId)
        {
            _ = assetIds.Add(assetId);
            return;
        }

        if (origin.Kind == OriginKind.SourceSpan
            && origin.SourceSpanId is EntityId<SourceSpanIdTag> sourceSpanId)
        {
            SourceSpan? span = context.SourceSpans.SingleOrDefault(candidate =>
                candidate.Id == sourceSpanId);
            if (span is not null)
            {
                _ = assetIds.Add(span.AssetId);
            }
        }
    }

    private static IntegrityFindingReason MapVerificationReason(
        BinaryVerificationFailureReason reason) =>
        reason switch
        {
            BinaryVerificationFailureReason.FileMissing =>
                IntegrityFindingReason.FileMissing,
            BinaryVerificationFailureReason.FileInaccessible =>
                IntegrityFindingReason.FileInaccessible,
            BinaryVerificationFailureReason.NotRegularFile =>
                IntegrityFindingReason.NotRegularFile,
            BinaryVerificationFailureReason.ReparsePoint =>
                IntegrityFindingReason.ReparsePoint,
            BinaryVerificationFailureReason.SizeMismatch =>
                IntegrityFindingReason.SizeMismatch,
            BinaryVerificationFailureReason.DigestMismatch =>
                IntegrityFindingReason.DigestMismatch,
            BinaryVerificationFailureReason.VerificationFailed =>
                IntegrityFindingReason.VerificationFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

    private static IntegrityEvidenceKind GetEvidenceKind(
        IntegrityFileKind kind) =>
        kind switch
        {
            IntegrityFileKind.Input => IntegrityEvidenceKind.Input,
            IntegrityFileKind.ToolExecutable => IntegrityEvidenceKind.Tool,
            IntegrityFileKind.ModelWeights => IntegrityEvidenceKind.Model,
            IntegrityFileKind.Font => IntegrityEvidenceKind.Font,
            IntegrityFileKind.Application => IntegrityEvidenceKind.Application,
            IntegrityFileKind.Original => IntegrityEvidenceKind.Input,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static bool TryCreateMap<T>(
        IReadOnlyList<T> values,
        Func<T, string> getKey,
        out Dictionary<string, T> result)
    {
        result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (T value in values)
        {
            if (!result.TryAdd(getKey(value), value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateModelMap(
        IReadOnlyList<RenderModelEvidence> values,
        out Dictionary<ModelEvidenceKey, RenderModelEvidence> result)
    {
        result = [];
        foreach (RenderModelEvidence value in values)
        {
            var key = new ModelEvidenceKey(
                value.Provider,
                value.ModelIdentifier);
            if (!result.TryAdd(key, value))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> UnionKeys<T>(
        IReadOnlyDictionary<string, T> first,
        IReadOnlyDictionary<string, T> second) =>
        first.Keys.Concat(second.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static ContentDigest ComputeEvidenceDigest(
        IntegrityMonitorRequest request,
        IntegrityDisposition disposition,
        IReadOnlyList<IntegrityFinding> findings)
    {
        using var writer = new IntegrityDigestWriter();
        writer.Add("hubradio.integrity-monitor.v1");
        AddSnapshotDigest(writer, "approved", request.Approved);
        AddSnapshotDigest(writer, "current", request.Current);
        writer.Add(request.CurrentFiles.Count);
        foreach (IntegrityFileBinding binding in request.CurrentFiles
            .OrderBy(static binding => binding.Kind)
            .ThenBy(
                static binding => binding.ComponentIdentity,
                StringComparer.Ordinal))
        {
            writer.Add((int)binding.Kind);
            writer.Add(binding.ComponentIdentity);
            writer.Add(binding.ExpectedDigest);
            writer.Add(binding.ExpectedByteSize);
        }

        writer.Add((int)disposition);
        writer.Add(findings.Count);
        foreach (IntegrityFinding finding in findings)
        {
            writer.Add((int)finding.EvidenceKind);
            writer.Add((int)finding.Reason);
            writer.Add((int)finding.Disposition);
            writer.Add(finding.ComponentIdentity);
            writer.AddOptional(finding.ExpectedDigest);
            writer.AddOptional(finding.ObservedDigest);
            writer.Add(finding.ExpectedByteSize);
            writer.Add(finding.ObservedByteSize);
        }

        return writer.Finish();
    }

    private static void AddSnapshotDigest(
        IntegrityDigestWriter writer,
        string role,
        IntegrityEvidenceSnapshot snapshot)
    {
        RenderManifest manifest = snapshot.Manifest;
        RenderGraphValidationContext context = snapshot.ValidationContext;
        writer.Add(role);
        writer.Add(context.Project.Id.ToString());
        writer.Add(ExportPolicyDigest.ComputeProjectDigest(context.Project));
        writer.Add(context.CompositionVersion.Id.ToString());
        writer.Add(context.CompositionVersion.ContentDigest);
        writer.Add(ComputeCompositionSemanticDigest(
            context.CompositionVersion));
        writer.Add(context.TimelineMap.Id.ToString());
        writer.Add(context.TimelineMapDigest);
        writer.Add(context.Profile.Id.ToString());
        writer.Add(context.Profile.ProfileDigest);
        writer.Add(ComputeProfileSemanticDigest(context.Profile));
        writer.Add(context.LayoutSpec.Id.ToString());
        writer.Add(context.LayoutSpecDigest);
        writer.Add(ComputeLayoutSemanticDigest(context.LayoutSpec));
        writer.Add(context.Template.Id.ToString());
        writer.Add(context.Template.VersionDigest);
        writer.Add(ComputeTemplateSemanticDigest(context.Template));
        writer.Add(ExportPolicyDigest.ComputeRenderGraphDigest(snapshot.Graph));
        writer.Add(manifest.ConfigurationDigest);
        writer.Add(manifest.ApplicationVersion);
        writer.Add(manifest.ApplicationDigest);

        writer.Add(manifest.Inputs.Count);
        foreach (RenderInputEvidence input in manifest.Inputs
            .OrderBy(static input => input.Name, StringComparer.Ordinal))
        {
            writer.Add(input.Name);
            writer.Add(input.ContentDigest);
        }

        writer.Add(manifest.Tools.Count);
        foreach (RenderToolEvidence tool in manifest.Tools
            .OrderBy(static tool => tool.Name, StringComparer.Ordinal))
        {
            writer.Add(tool.Name);
            writer.Add(tool.Version);
            writer.Add(tool.BuildConfiguration);
            writer.Add(tool.ExecutableDigest);
            writer.AddOptional(tool.ContainerImageDigest);
        }

        writer.Add(manifest.Models.Count);
        foreach (RenderModelEvidence model in manifest.Models
            .OrderBy(static model => model.Provider, StringComparer.Ordinal)
            .ThenBy(
                static model => model.ModelIdentifier,
                StringComparer.Ordinal))
        {
            writer.Add(model.Provider);
            writer.Add(model.ModelIdentifier);
            writer.Add(model.ModelVersion);
            writer.Add(model.WeightsDigest);
        }

        writer.Add(manifest.Fonts.Count);
        foreach (RenderFontEvidence font in manifest.Fonts
            .OrderBy(static font => font.Name, StringComparer.Ordinal))
        {
            writer.Add(font.Name);
            writer.Add(font.FileDigest);
        }
    }

    private static ContentDigest ComputeCompositionSemanticDigest(
        CompositionVersion composition)
    {
        using var writer = new IntegrityDigestWriter();
        writer.Add("hubradio.integrity-composition.v1");
        writer.Add(composition.Id.ToString());
        writer.Add(composition.ProjectId.ToString());
        writer.Add(composition.ParentVersionId is null ? 0 : 1);
        if (composition.ParentVersionId is not null)
        {
            writer.Add(composition.ParentVersionId.ToString());
        }

        writer.Add(composition.CreatedByActorId);
        writer.Add(composition.CreatedAt.UtcDateTime.Ticks);
        writer.Add(composition.CreatedAt.Offset.Ticks);
        writer.Add(composition.ChangeReason);
        writer.Add(composition.ContentDigest);
        return writer.Finish();
    }

    private static ContentDigest ComputeProfileSemanticDigest(
        RenderProfile profile)
    {
        using var writer = new IntegrityDigestWriter();
        writer.Add("hubradio.integrity-profile.v1");
        writer.Add(profile.Id.ToString());
        writer.Add(profile.Name);
        writer.Add(profile.ProfileDigest);
        writer.Add(profile.Width);
        writer.Add(profile.Height);
        writer.Add(profile.FrameRateNumerator);
        writer.Add(profile.FrameRateDenominator);
        writer.Add(profile.LayoutSpecId.ToString());
        writer.Add(profile.TemplateId.ToString());
        writer.Add(profile.TemplateVersionDigest);
        writer.Add(profile.VideoCodec);
        writer.Add(profile.AudioCodec);
        writer.Add(profile.SubtitleBurnIn ? 1 : 0);
        return writer.Finish();
    }

    private static ContentDigest ComputeLayoutSemanticDigest(LayoutSpec layout)
    {
        using var writer = new IntegrityDigestWriter();
        writer.Add("hubradio.integrity-layout.v1");
        writer.Add(layout.Id.ToString());
        writer.Add(layout.OutputAspectWidth);
        writer.Add(layout.OutputAspectHeight);
        writer.Add(layout.Regions.Count);
        foreach (LayoutRegion region in layout.Regions
            .OrderBy(static region => region.Name, StringComparer.Ordinal))
        {
            writer.Add(region.Name);
            writer.Add((int)region.Role);
            AddRect(writer, region.Rect);
        }

        writer.Add(layout.SafeZones.Count);
        foreach (SafeZone safeZone in layout.SafeZones
            .OrderBy(static safeZone => safeZone.Name, StringComparer.Ordinal))
        {
            writer.Add(safeZone.Name);
            AddRect(writer, safeZone.Rect);
        }

        return writer.Finish();
    }

    private static ContentDigest ComputeTemplateSemanticDigest(
        TemplateReference template)
    {
        using var writer = new IntegrityDigestWriter();
        writer.Add("hubradio.integrity-template.v1");
        writer.Add(template.Id.ToString());
        writer.Add(template.Name);
        writer.Add(template.VersionDigest);
        return writer.Finish();
    }

    private static void AddRect(
        IntegrityDigestWriter writer,
        NormalizedRect rect)
    {
        writer.Add(rect.X.Numerator);
        writer.Add(rect.X.Denominator);
        writer.Add(rect.Y.Numerator);
        writer.Add(rect.Y.Denominator);
        writer.Add(rect.Width.Numerator);
        writer.Add(rect.Width.Denominator);
        writer.Add(rect.Height.Numerator);
        writer.Add(rect.Height.Denominator);
    }

    private sealed record ExpectedFileEvidence(
        IntegrityFileKey Key,
        IntegrityEvidenceKind EvidenceKind,
        string ComponentIdentity,
        ContentDigest Digest,
        long? ByteSize);

    private readonly record struct ModelEvidenceKey(
        string Provider,
        string Identifier);

    private sealed class FindingCollector
    {
        private readonly Dictionary<FindingKey, IntegrityFinding> _findings = [];

        internal void Add(
            IntegrityEvidenceKind kind,
            IntegrityFindingReason reason,
            IntegrityDisposition disposition,
            string componentIdentity,
            ContentDigest? expectedDigest = null,
            ContentDigest? observedDigest = null,
            long? expectedByteSize = null,
            long? observedByteSize = null)
        {
            var finding = new IntegrityFinding(
                kind,
                reason,
                disposition,
                componentIdentity,
                expectedDigest,
                observedDigest,
                expectedByteSize,
                observedByteSize);
            var key = new FindingKey(
                finding.EvidenceKind,
                finding.Reason,
                finding.Disposition,
                finding.ComponentIdentity,
                finding.ExpectedDigest?.Hex,
                finding.ObservedDigest?.Hex,
                finding.ExpectedByteSize,
                finding.ObservedByteSize);
            _findings.TryAdd(key, finding);
        }

        internal IntegrityFinding[] ToCanonicalArray() =>
            _findings.Values
                .OrderBy(static finding => finding.EvidenceKind)
                .ThenBy(
                    static finding => finding.ComponentIdentity,
                    StringComparer.Ordinal)
                .ThenBy(static finding => finding.Reason)
                .ThenBy(static finding => finding.Disposition)
                .ThenBy(
                    static finding => finding.ExpectedDigest?.Hex,
                    StringComparer.Ordinal)
                .ThenBy(
                    static finding => finding.ObservedDigest?.Hex,
                    StringComparer.Ordinal)
                .ThenBy(static finding => finding.ExpectedByteSize)
                .ThenBy(static finding => finding.ObservedByteSize)
                .ToArray();

        private sealed record FindingKey(
            IntegrityEvidenceKind Kind,
            IntegrityFindingReason Reason,
            IntegrityDisposition Disposition,
            string ComponentIdentity,
            string? ExpectedDigest,
            string? ObservedDigest,
            long? ExpectedByteSize,
            long? ObservedByteSize);
    }
}

internal readonly record struct IntegrityFileKey(
    IntegrityFileKind Kind,
    string Primary,
    string? Secondary)
{
    internal static IntegrityFileKey Input(EntityId<RenderInputIdTag> id) =>
        new(IntegrityFileKind.Input, id.ToString(), null);

    internal static IntegrityFileKey Original(EntityId<AssetIdTag> id) =>
        new(IntegrityFileKind.Original, id.ToString(), null);

    internal static IntegrityFileKey Tool(string name) =>
        new(IntegrityFileKind.ToolExecutable, name, null);

    internal static IntegrityFileKey Model(string provider, string identifier) =>
        new(IntegrityFileKind.ModelWeights, provider, identifier);

    internal static IntegrityFileKey Font(string name) =>
        new(IntegrityFileKind.Font, name, null);

    internal static IntegrityFileKey Application { get; } =
        new(IntegrityFileKind.Application, "application", null);
}

internal static class IntegrityContract
{
    internal const string ApplicationComponent = "application";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string ToolComponent(string name) =>
        HashedComponent("tool", name, secondary: null);

    internal static string ModelComponent(string provider, string identifier) =>
        HashedComponent("model", provider, identifier);

    internal static string FontComponent(string name) =>
        HashedComponent("font", name, secondary: null);

    internal static string InputLabelComponent(string name) =>
        HashedComponent("input-label", name, secondary: null);

    internal static string OriginalComponent(EntityId<AssetIdTag> assetId) =>
        string.Concat("original.", assetId.ToString());

    internal static string IdentityComponent(
        string prefix,
        string first,
        string? second = null) =>
        second is null || string.Equals(first, second, StringComparison.Ordinal)
            ? string.Concat(prefix, ".", first)
            : HashedComponent(prefix, first, second);

    internal static void ValidateKeyText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An integrity evidence key must be nonblank without surrounding whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "An integrity evidence key must contain valid Unicode.",
                parameterName,
                exception);
        }
    }

    internal static void ValidateComponentIdentity(
        string value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        _ = new BinaryVerificationRequest(
            value,
            Path.Combine(Path.GetTempPath(), "integrity-component-validation"),
            new ContentDigest(new string('0', 64)),
            expectedByteSize: 0);
    }

    private static string HashedComponent(
        string prefix,
        string primary,
        string? secondary)
    {
        using var writer = new IntegrityDigestWriter();
        writer.Add(primary);
        writer.Add(secondary is null ? 0 : 1);
        if (secondary is not null)
        {
            writer.Add(secondary);
        }

        string suffix = writer.Finish().Hex[..24];
        return string.Concat(prefix, ".", suffix);
    }
}

internal sealed class IntegrityDigestWriter : IDisposable
{
    private readonly IncrementalHash _hash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;

    internal void Add(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    internal void Add(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    internal void Add(long? value)
    {
        Add(value.HasValue ? 1 : 0);
        if (value.HasValue)
        {
            Add(value.Value);
        }
    }

    internal void Add(string value)
    {
        Add(value.Length);
        byte[] bytes = new byte[checked(value.Length * sizeof(char))];
        for (int index = 0; index < value.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                bytes.AsSpan(index * sizeof(char), sizeof(char)),
                value[index]);
        }

        _hash.AppendData(bytes);
    }

    internal void Add(ContentDigest value) => Add(value.Hex);

    internal void AddOptional(ContentDigest? value)
    {
        Add(value is null ? 0 : 1);
        if (value is not null)
        {
            Add(value);
        }
    }

    internal ContentDigest Finish()
    {
        if (_finished)
        {
            throw new InvalidOperationException(
                "The integrity evidence digest has already been finalized.");
        }

        _finished = true;
        return ContentDigest.FromBytes(_hash.GetHashAndReset());
    }

    public void Dispose()
    {
        _hash.Dispose();
    }
}
