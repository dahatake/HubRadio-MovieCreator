using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public void CompositionContractsUseTypedIdsAndImmutableVersionProperties()
    {
        foreach (Type marker in new[]
        {
            typeof(WorkingCopyIdTag),
            typeof(CompositionVersionIdTag),
            typeof(CompositionElementIdTag),
        })
        {
            Assert.IsTrue(marker.IsPublic);
            Assert.IsTrue(marker.IsSealed);
        }

        AssertPublicGetOnlyProperties(
            typeof(CompositionElement),
            "Id",
            "OutputRange",
            "Origin",
            "AdoptedSuggestion");
        AssertPublicGetOnlyProperties(
            typeof(CompositionVersion),
            "Id",
            "ProjectId",
            "ParentVersionId",
            "CreatedByActorId",
            "CreatedAt",
            "ChangeReason",
            "ContentDigest",
            "Elements");

        Assert.AreEqual(
            typeof(EntityId<CompositionVersionIdTag>),
            typeof(CompositionVersion).GetProperty("Id")!.PropertyType);
        Assert.IsNull(typeof(CompositionVersion).GetProperty("IsApproved"));
        Assert.IsNull(typeof(CompositionVersion).GetProperty("ApprovalStatus"));
    }

    [TestMethod]
    public void WorkingCopyIsRecoverableAndReplacesDraftStateWithoutExposingMutation()
    {
        EntityId<CompositionVersionIdTag> baseVersionId =
            EntityId<CompositionVersionIdTag>.New();
        DateTimeOffset createdAt = new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);
        List<CompositionElement> initial = [CreateElement()];
        WorkingCopy workingCopy = new(
            EntityId<WorkingCopyIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            baseVersionId,
            createdAt,
            createdAt,
            initial);

        CompositionElement persistedElement = workingCopy.Elements[0];
        initial.Clear();
        Assert.AreEqual(1, workingCopy.Elements.Count);
        Assert.AreSame(persistedElement, workingCopy.Elements[0]);
        Assert.AreEqual(baseVersionId, workingCopy.BaseVersionId);

        IReadOnlyList<CompositionElement> previousSnapshot = workingCopy.Elements;
        CompositionElement replacement = CreateElement(start: new RationalTime(2, 1));
        workingCopy.ReplaceElements([replacement], createdAt.AddMinutes(1));

        Assert.AreEqual(createdAt.AddMinutes(1), workingCopy.UpdatedAt);
        Assert.AreEqual(replacement, workingCopy.Elements[0]);
        Assert.AreEqual(persistedElement, previousSnapshot[0]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<CompositionElement>)workingCopy.Elements).Clear());
    }

    [TestMethod]
    public void WorkingCopyRejectsReverseTimestampsAndInvalidElementCollections()
    {
        DateTimeOffset createdAt = new(2026, 8, 29, 1, 0, 0, TimeSpan.Zero);
        EntityId<CompositionElementIdTag> duplicateId = EntityId<CompositionElementIdTag>.New();

        AssertParamName<ArgumentOutOfRangeException>(
            "updatedAt",
            () => CreateWorkingCopy(createdAt, createdAt.AddTicks(-1), []));
        AssertParamName<ArgumentException>(
            "elements",
            () => CreateWorkingCopy(createdAt, createdAt, [null!]));
        AssertParamName<ArgumentException>(
            "elements",
            () => CreateWorkingCopy(
                createdAt,
                createdAt,
                [CreateElement(duplicateId), CreateElement(duplicateId)]));

        WorkingCopy workingCopy = CreateWorkingCopy(createdAt, createdAt, []);
        AssertParamName<ArgumentOutOfRangeException>(
            "updatedAt",
            () => workingCopy.ReplaceElements([], createdAt.AddTicks(-1)));
    }

    [TestMethod]
    public void CompositionVersionKeepsParentReasonDigestActorAndImmutableSnapshot()
    {
        EntityId<CompositionVersionIdTag> parentId = EntityId<CompositionVersionIdTag>.New();
        DateTimeOffset createdAt = new(2026, 8, 29, 2, 0, 0, TimeSpan.Zero);
        CompositionElement element = CreateElement();
        List<CompositionElement> elements = [element];

        CompositionVersion version = new(
            EntityId<CompositionVersionIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            parentId,
            "S-1-5-21-1000",
            createdAt,
            "Remove the confirmed duplicate passage.",
            Digest('a'),
            elements);
        elements.Clear();

        Assert.AreEqual(parentId, version.ParentVersionId);
        Assert.AreEqual("S-1-5-21-1000", version.CreatedByActorId);
        Assert.AreEqual(createdAt, version.CreatedAt);
        Assert.AreEqual("Remove the confirmed duplicate passage.", version.ChangeReason);
        Assert.AreEqual(Digest('a'), version.ContentDigest);
        Assert.AreEqual(1, version.Elements.Count);
        Assert.AreEqual(element, version.Elements[0]);
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<CompositionElement>)version.Elements).Clear());
    }

    [TestMethod]
    public void CompositionVersionRejectsSelfParentInvalidTextAndDuplicateElements()
    {
        EntityId<CompositionVersionIdTag> id = EntityId<CompositionVersionIdTag>.New();
        EntityId<CompositionElementIdTag> duplicateId = EntityId<CompositionElementIdTag>.New();

        AssertParamName<ArgumentException>(
            "parentVersionId",
            () => CreateVersion(id: id, parentVersionId: id));

        foreach (string value in new[] { string.Empty, " ", " actor", "actor " })
        {
            AssertParamName<ArgumentException>(
                "createdByActorId",
                () => CreateVersion(createdByActorId: value));
            AssertParamName<ArgumentException>(
                "changeReason",
                () => CreateVersion(changeReason: value));
        }

        AssertParamName<ArgumentException>(
            "elements",
            () => CreateVersion(
                elements: [CreateElement(duplicateId), CreateElement(duplicateId)]));
    }

    [TestMethod]
    public void AdoptedSuggestionReferenceRetainsSuggestionIdAndSameOrigin()
    {
        Suggestion adopted = CreateSuggestion(SuggestionStatus.Adopted);
        SuggestionReference reference = SuggestionReference.FromAdoptedSuggestion(adopted);
        CompositionElement element = CreateElement(
            origin: adopted.Origin,
            adoptedSuggestion: reference);

        Assert.AreEqual(adopted.Id, element.AdoptedSuggestion!.SuggestionId);
        Assert.AreEqual(adopted.Origin, element.Origin);
        Assert.AreEqual(adopted.Origin, element.AdoptedSuggestion.Origin);

        OriginRef otherOrigin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        AssertParamName<ArgumentException>(
            "adoptedSuggestion",
            () => CreateElement(origin: otherOrigin, adoptedSuggestion: reference));
    }

    [TestMethod]
    public void HumanAuthoredElementUsesEditorialRecordWithoutSuggestionFabrication()
    {
        OriginRef editorial = OriginRef.FromEditorialRecord(
            EntityId<EditorialRecordIdTag>.New());

        CompositionElement element = CreateElement(origin: editorial);

        Assert.AreEqual(OriginKind.EditorialRecord, element.Origin.Kind);
        Assert.IsNull(element.AdoptedSuggestion);
    }

    [TestMethod]
    public void CompositionElementRequiresPositiveNonNegativeOutputRangeAndUsesValueEquality()
    {
        EntityId<CompositionElementIdTag> id = EntityId<CompositionElementIdTag>.New();
        OriginRef origin = OriginRef.FromAsset(EntityId<AssetIdTag>.New());
        CompositionElement first = CreateElement(id, origin: origin);
        CompositionElement equivalent = CreateElement(
            EntityId<CompositionElementIdTag>.From(id.Value),
            origin: OriginRef.FromAsset(EntityId<AssetIdTag>.From(origin.AssetId!.Value)));

        Assert.AreEqual(first, equivalent);
        Assert.AreEqual(first.GetHashCode(), equivalent.GetHashCode());
        AssertParamName<ArgumentOutOfRangeException>(
            "outputRange",
            () => CreateElement(start: new RationalTime(-1, 1)));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputRange",
            () => CreateElement(duration: new RationalTime(0, 1)));
    }

    private static WorkingCopy CreateWorkingCopy(
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        IReadOnlyList<CompositionElement> elements)
    {
        return new WorkingCopy(
            EntityId<WorkingCopyIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            null,
            createdAt,
            updatedAt,
            elements);
    }

    private static CompositionVersion CreateVersion(
        EntityId<CompositionVersionIdTag>? id = null,
        EntityId<CompositionVersionIdTag>? parentVersionId = null,
        string createdByActorId = "S-1-5-21-1000",
        string changeReason = "Initial composition.",
        IReadOnlyList<CompositionElement>? elements = null)
    {
        return new CompositionVersion(
            id ?? EntityId<CompositionVersionIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            parentVersionId,
            createdByActorId,
            new DateTimeOffset(2026, 8, 29, 2, 0, 0, TimeSpan.Zero),
            changeReason,
            Digest('a'),
            elements ?? []);
    }

    private static CompositionElement CreateElement(
        EntityId<CompositionElementIdTag>? id = null,
        OriginRef? origin = null,
        SuggestionReference? adoptedSuggestion = null,
        RationalTime? start = null,
        RationalTime? duration = null)
    {
        return new CompositionElement(
            id ?? EntityId<CompositionElementIdTag>.New(),
            new TimeRange(start ?? default, duration ?? new RationalTime(1, 1)),
            origin ?? OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New()),
            adoptedSuggestion);
    }

    private static Suggestion CreateSuggestion(SuggestionStatus status)
    {
        return new Suggestion(
            EntityId<SuggestionIdTag>.New(),
            EntityId<ModelRunIdTag>.New(),
            SuggestionKind.Highlight,
            status,
            OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New()),
            "Grounded candidate.",
            null,
            null,
            null);
    }

    private static ContentDigest Digest(char value) => new(new string(value, 64));

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

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}