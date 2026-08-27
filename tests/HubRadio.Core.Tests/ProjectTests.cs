using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class ProjectTests
{
    private const string DigestHex =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void DomainEnumsAndMarkerTagsHaveExactlyTheRequiredShape()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                PublicationScope.InternalOnly,
                PublicationScope.SpecificGroupOnly,
                PublicationScope.ExternalCandidate,
                PublicationScope.ExternalPublicationApproved,
                PublicationScope.PublicationForbidden,
                PublicationScope.PendingDecision,
            },
            Enum.GetValues<PublicationScope>());
        CollectionAssert.AreEqual(
            new[]
            {
                RightsStatus.Unconfirmed,
                RightsStatus.Approved,
                RightsStatus.Rejected,
            },
            Enum.GetValues<RightsStatus>());

        Type[] markerTypes = [typeof(ProjectIdTag), typeof(PerformerIdTag)];
        foreach (Type markerType in markerTypes)
        {
            Assert.IsTrue(markerType.IsPublic);
            Assert.IsTrue(markerType.IsSealed);
        }
    }

    [TestMethod]
    public void DomainClassesAreSealedAndExposeOnlyTheMinimalImmutableProperties()
    {
        AssertPublicGetOnlyProperties(
            typeof(Project),
            "Id",
            "ContentName",
            "Purpose",
            "Audience",
            "PublicationScope",
            "Deadline",
            "Assets",
            "Performers");
        AssertPublicGetOnlyProperties(
            typeof(Asset),
            "Id",
            "FileName",
            "MediaType",
            "ContentDigest",
            "ByteSize",
            "Rights");
        AssertPublicGetOnlyProperties(
            typeof(RightsRecord),
            "Status",
            "EvidenceReference",
            "AllowedUse",
            "ExternalPublicationAllowed");
        AssertPublicGetOnlyProperties(
            typeof(Performer),
            "Id",
            "DisplayName",
            "Role",
            "TitleConfirmedAt");
    }

    [TestMethod]
    public void ProjectStoresValuesAndDefensivelyCopiesAggregateLists()
    {
        EntityId<ProjectIdTag> id = EntityId<ProjectIdTag>.New();
        DateTimeOffset deadline = new(2026, 10, 15, 9, 30, 0, TimeSpan.FromHours(9));
        Asset originalAsset = CreateAsset();
        Performer originalPerformer = CreatePerformer();
        List<Asset> assets = [originalAsset];
        List<Performer> performers = [originalPerformer];

        Project project = new(
            id,
            "Hub Radio Episode",
            "Explain the release",
            "Internal engineering audience",
            PublicationScope.ExternalCandidate,
            deadline,
            assets,
            performers);

        assets[0] = CreateAsset();
        assets.Add(CreateAsset());
        performers[0] = CreatePerformer();
        performers.Add(CreatePerformer());

        Assert.AreEqual(id, project.Id);
        Assert.AreEqual("Hub Radio Episode", project.ContentName);
        Assert.AreEqual("Explain the release", project.Purpose);
        Assert.AreEqual("Internal engineering audience", project.Audience);
        Assert.AreEqual(PublicationScope.ExternalCandidate, project.PublicationScope);
        Assert.AreEqual(deadline, project.Deadline);
        Assert.AreEqual(1, project.Assets.Count);
        Assert.AreSame(originalAsset, project.Assets[0]);
        Assert.AreEqual(1, project.Performers.Count);
        Assert.AreSame(originalPerformer, project.Performers[0]);

        IList<Asset> exposedAssets = (IList<Asset>)project.Assets;
        IList<Performer> exposedPerformers = (IList<Performer>)project.Performers;
        Assert.ThrowsExactly<NotSupportedException>(() => exposedAssets.Add(CreateAsset()));
        Assert.ThrowsExactly<NotSupportedException>(() => exposedAssets[0] = CreateAsset());
        Assert.ThrowsExactly<NotSupportedException>(() => exposedPerformers.Add(CreatePerformer()));
        Assert.ThrowsExactly<NotSupportedException>(
            () => exposedPerformers[0] = CreatePerformer());
    }

    [TestMethod]
    public void ProjectAllowsAnIncompleteDraftWithoutInventingOptionalValues()
    {
        Project project = new(
            EntityId<ProjectIdTag>.New(),
            "Draft",
            "Capture required inputs",
            "To be confirmed",
            PublicationScope.InternalOnly,
            deadline: null,
            assets: [],
            performers: []);

        Assert.IsNull(project.Deadline);
        Assert.AreEqual(0, project.Assets.Count);
        Assert.AreEqual(0, project.Performers.Count);
    }

    [TestMethod]
    public void ProjectRejectsNullRequiredReferences()
    {
        EntityId<ProjectIdTag> id = EntityId<ProjectIdTag>.New();
        IReadOnlyList<Asset> assets = Array.Empty<Asset>();
        IReadOnlyList<Performer> performers = Array.Empty<Performer>();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Project(
                null!,
                "Content",
                "Purpose",
                "Audience",
                PublicationScope.InternalOnly,
                null,
                assets,
                performers));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Project(
                id,
                null!,
                "Purpose",
                "Audience",
                PublicationScope.InternalOnly,
                null,
                assets,
                performers));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Project(
                id,
                "Content",
                null!,
                "Audience",
                PublicationScope.InternalOnly,
                null,
                assets,
                performers));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Project(
                id,
                "Content",
                "Purpose",
                null!,
                PublicationScope.InternalOnly,
                null,
                assets,
                performers));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Project(
                id,
                "Content",
                "Purpose",
                "Audience",
                PublicationScope.InternalOnly,
                null,
                null!,
                performers));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Project(
                id,
                "Content",
                "Purpose",
                "Audience",
                PublicationScope.InternalOnly,
                null,
                assets,
                null!));
    }

    [TestMethod]
    public void ProjectRejectsBlankOrUntrimmedText()
    {
        string[] invalidValues = [string.Empty, " ", "\t", " value", "value ", "\r\nvalue"];

        foreach (string invalidValue in invalidValues)
        {
            AssertProjectTextRejected(invalidValue, "contentName");
            AssertProjectTextRejected(invalidValue, "purpose");
            AssertProjectTextRejected(invalidValue, "audience");
        }
    }

    [TestMethod]
    public void ProjectRejectsUnknownPublicationScope()
    {
        ArgumentOutOfRangeException exception =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new Project(
                    EntityId<ProjectIdTag>.New(),
                    "Content",
                    "Purpose",
                    "Audience",
                    (PublicationScope)99,
                    null,
                    [],
                    []));

        Assert.AreEqual("publicationScope", exception.ParamName);
    }

    [TestMethod]
    public void ProjectRejectsNullElementsAndDuplicateIds()
    {
        Asset asset = CreateAsset();
        Performer performer = CreatePerformer();

        ArgumentException nullAsset = Assert.ThrowsExactly<ArgumentException>(
            () => CreateProject([null!], []));
        ArgumentException nullPerformer = Assert.ThrowsExactly<ArgumentException>(
            () => CreateProject([], [null!]));
        Assert.AreEqual("assets", nullAsset.ParamName);
        Assert.AreEqual("performers", nullPerformer.ParamName);

        Asset duplicateAsset = CreateAsset(EntityId<AssetIdTag>.From(asset.Id.Value));
        Performer duplicatePerformer = CreatePerformer(
            EntityId<PerformerIdTag>.From(performer.Id.Value));
        ArgumentException duplicateAssets = Assert.ThrowsExactly<ArgumentException>(
            () => CreateProject([asset, duplicateAsset], []));
        ArgumentException duplicatePerformers = Assert.ThrowsExactly<ArgumentException>(
            () => CreateProject([], [performer, duplicatePerformer]));
        Assert.AreEqual("assets", duplicateAssets.ParamName);
        Assert.AreEqual("performers", duplicatePerformers.ParamName);
    }

    [TestMethod]
    public void AssetStoresOnlyRequiredSourceFacts()
    {
        EntityId<AssetIdTag> id = EntityId<AssetIdTag>.New();
        ContentDigest digest = new(DigestHex);
        RightsRecord rights = new(
            RightsStatus.Unconfirmed,
            evidenceReference: null,
            "local editing only",
            externalPublicationAllowed: false);

        Asset asset = new(
            id,
            "recording.mov",
            "video/quicktime",
            digest,
            long.MaxValue,
            rights);

        Assert.AreEqual(id, asset.Id);
        Assert.AreEqual("recording.mov", asset.FileName);
        Assert.AreEqual("video/quicktime", asset.MediaType);
        Assert.AreEqual(digest, asset.ContentDigest);
        Assert.AreEqual(long.MaxValue, asset.ByteSize);
        Assert.AreSame(rights, asset.Rights);
    }

    [TestMethod]
    public void AssetRejectsNullReferencesBlankTextAndNonPositiveSizes()
    {
        EntityId<AssetIdTag> id = EntityId<AssetIdTag>.New();
        ContentDigest digest = new(DigestHex);
        RightsRecord rights = CreateUnconfirmedRights();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Asset(null!, "file.mov", "video/quicktime", digest, 1, rights));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Asset(id, null!, "video/quicktime", digest, 1, rights));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Asset(id, "file.mov", null!, digest, 1, rights));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Asset(id, "file.mov", "video/quicktime", null!, 1, rights));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Asset(id, "file.mov", "video/quicktime", digest, 1, null!));

        string[] invalidValues = [string.Empty, " ", "\t", " value", "value "];
        foreach (string invalidValue in invalidValues)
        {
            ArgumentException fileName = Assert.ThrowsExactly<ArgumentException>(
                () => new Asset(id, invalidValue, "video/quicktime", digest, 1, rights));
            ArgumentException mediaType = Assert.ThrowsExactly<ArgumentException>(
                () => new Asset(id, "file.mov", invalidValue, digest, 1, rights));
            Assert.AreEqual("fileName", fileName.ParamName);
            Assert.AreEqual("mediaType", mediaType.ParamName);
        }

        foreach (long byteSize in new[] { 0L, -1L, long.MinValue })
        {
            ArgumentOutOfRangeException exception =
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                    () => new Asset(
                        id,
                        "file.mov",
                        "video/quicktime",
                        digest,
                        byteSize,
                        rights));
            Assert.AreEqual("byteSize", exception.ParamName);
        }
    }

    [TestMethod]
    public void RightsRecordIsFailClosedAndPreservesSuppliedEvidenceAndUse()
    {
        RightsRecord unconfirmed = new(
            RightsStatus.Unconfirmed,
            evidenceReference: null,
            "local review only",
            externalPublicationAllowed: false);
        RightsRecord rejected = new(
            RightsStatus.Rejected,
            "evidence/rights-rejection.txt",
            "no use permitted",
            externalPublicationAllowed: false);
        RightsRecord approvedInternal = new(
            RightsStatus.Approved,
            "evidence/internal-license.pdf",
            "internal presentation",
            externalPublicationAllowed: false);
        RightsRecord approvedExternal = new(
            RightsStatus.Approved,
            "evidence/external-license.pdf",
            "external publication",
            externalPublicationAllowed: true);

        Assert.AreEqual(RightsStatus.Unconfirmed, unconfirmed.Status);
        Assert.IsNull(unconfirmed.EvidenceReference);
        Assert.AreEqual("local review only", unconfirmed.AllowedUse);
        Assert.IsFalse(unconfirmed.ExternalPublicationAllowed);
        Assert.AreEqual("evidence/rights-rejection.txt", rejected.EvidenceReference);
        Assert.IsFalse(rejected.ExternalPublicationAllowed);
        Assert.IsFalse(approvedInternal.ExternalPublicationAllowed);
        Assert.IsTrue(approvedExternal.ExternalPublicationAllowed);
    }

    [TestMethod]
    public void RightsRecordRequiresEvidenceForApproval()
    {
        string?[] invalidEvidence = [null, string.Empty, " ", " evidence/ref", "evidence/ref "];

        foreach (string? evidenceReference in invalidEvidence)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new RightsRecord(
                    RightsStatus.Approved,
                    evidenceReference,
                    "external publication",
                    externalPublicationAllowed: true));
            Assert.AreEqual("evidenceReference", exception.ParamName);
        }
    }

    [TestMethod]
    public void RightsRecordRejectsExternalPublicationWithoutApproval()
    {
        RightsStatus[] nonApprovedStatuses =
        [
            RightsStatus.Unconfirmed,
            RightsStatus.Rejected,
        ];

        foreach (RightsStatus status in nonApprovedStatuses)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new RightsRecord(
                    status,
                    evidenceReference: null,
                    "local review only",
                    externalPublicationAllowed: true));
            Assert.AreEqual("externalPublicationAllowed", exception.ParamName);
        }
    }

    [TestMethod]
    public void RightsRecordRejectsUnknownStatusAndInvalidText()
    {
        ArgumentOutOfRangeException unknownStatus =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new RightsRecord(
                    (RightsStatus)99,
                    evidenceReference: null,
                    "local review only",
                    externalPublicationAllowed: false));
        Assert.AreEqual("status", unknownStatus.ParamName);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RightsRecord(
                RightsStatus.Unconfirmed,
                evidenceReference: null,
                null!,
                externalPublicationAllowed: false));

        string[] invalidValues = [string.Empty, " ", "\t", " value", "value "];
        foreach (string invalidValue in invalidValues)
        {
            ArgumentException allowedUse = Assert.ThrowsExactly<ArgumentException>(
                () => new RightsRecord(
                    RightsStatus.Unconfirmed,
                    evidenceReference: null,
                    invalidValue,
                    externalPublicationAllowed: false));
            ArgumentException evidence = Assert.ThrowsExactly<ArgumentException>(
                () => new RightsRecord(
                    RightsStatus.Rejected,
                    invalidValue,
                    "no use permitted",
                    externalPublicationAllowed: false));
            Assert.AreEqual("allowedUse", allowedUse.ParamName);
            Assert.AreEqual("evidenceReference", evidence.ParamName);
        }
    }

    [TestMethod]
    public void PerformerStoresDisplayIdentityWithoutInferringARealName()
    {
        EntityId<PerformerIdTag> id = EntityId<PerformerIdTag>.New();
        DateTimeOffset confirmedAt = new(2026, 8, 27, 16, 0, 0, TimeSpan.Zero);

        Performer confirmed = new(id, "Host A", "Host", confirmedAt);
        Performer unconfirmed = new(
            EntityId<PerformerIdTag>.New(),
            "Guest B",
            "Guest",
            titleConfirmedAt: null);

        Assert.AreEqual(id, confirmed.Id);
        Assert.AreEqual("Host A", confirmed.DisplayName);
        Assert.AreEqual("Host", confirmed.Role);
        Assert.AreEqual(confirmedAt, confirmed.TitleConfirmedAt);
        Assert.IsNull(unconfirmed.TitleConfirmedAt);
    }

    [TestMethod]
    public void PerformerRejectsNullReferencesAndInvalidText()
    {
        EntityId<PerformerIdTag> id = EntityId<PerformerIdTag>.New();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Performer(null!, "Host A", "Host", null));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Performer(id, null!, "Host", null));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new Performer(id, "Host A", null!, null));

        string[] invalidValues = [string.Empty, " ", "\t", " value", "value "];
        foreach (string invalidValue in invalidValues)
        {
            ArgumentException displayName = Assert.ThrowsExactly<ArgumentException>(
                () => new Performer(id, invalidValue, "Host", null));
            ArgumentException role = Assert.ThrowsExactly<ArgumentException>(
                () => new Performer(id, "Host A", invalidValue, null));
            Assert.AreEqual("displayName", displayName.ParamName);
            Assert.AreEqual("role", role.ParamName);
        }
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        Assert.IsTrue(type.IsSealed);

        System.Reflection.PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static void AssertProjectTextRejected(string invalidValue, string parameterName)
    {
        string contentName = parameterName == "contentName" ? invalidValue : "Content";
        string purpose = parameterName == "purpose" ? invalidValue : "Purpose";
        string audience = parameterName == "audience" ? invalidValue : "Audience";

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new Project(
                EntityId<ProjectIdTag>.New(),
                contentName,
                purpose,
                audience,
                PublicationScope.InternalOnly,
                null,
                [],
                []));
        Assert.AreEqual(parameterName, exception.ParamName);
    }

    private static Project CreateProject(
        IReadOnlyList<Asset> assets,
        IReadOnlyList<Performer> performers)
    {
        return new Project(
            EntityId<ProjectIdTag>.New(),
            "Content",
            "Purpose",
            "Audience",
            PublicationScope.InternalOnly,
            null,
            assets,
            performers);
    }

    private static Asset CreateAsset(EntityId<AssetIdTag>? id = null)
    {
        return new Asset(
            id ?? EntityId<AssetIdTag>.New(),
            "recording.mov",
            "video/quicktime",
            new ContentDigest(DigestHex),
            1024,
            CreateUnconfirmedRights());
    }

    private static RightsRecord CreateUnconfirmedRights()
    {
        return new RightsRecord(
            RightsStatus.Unconfirmed,
            evidenceReference: null,
            "local editing only",
            externalPublicationAllowed: false);
    }

    private static Performer CreatePerformer(EntityId<PerformerIdTag>? id = null)
    {
        return new Performer(
            id ?? EntityId<PerformerIdTag>.New(),
            "Host A",
            "Host",
            titleConfirmedAt: null);
    }
}