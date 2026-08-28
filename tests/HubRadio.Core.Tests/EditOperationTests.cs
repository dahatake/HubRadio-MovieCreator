using System.Reflection;
using HubRadio.Core.Analysis;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class EditOperationTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ContractsExposeExactEnumFactoriesAndGetOnlyProperties()
    {
        Assert.IsTrue(typeof(EditOperationKind).IsPublic);
        CollectionAssert.AreEqual(
            new[] { "Keep", "Delete", "Split", "Move" },
            Enum.GetNames<EditOperationKind>());
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2, 3 },
            Enum.GetValues<EditOperationKind>().Select(static value => (int)value).ToArray());

        Type operationType = typeof(EditOperation);
        Assert.IsTrue(operationType.IsPublic);
        Assert.IsTrue(operationType.IsClass);
        Assert.IsTrue(operationType.IsSealed);
        Assert.IsFalse(operationType.IsAbstract);

        ConstructorInfo[] constructors = operationType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.AreEqual(1, constructors.Length);
        Assert.IsTrue(constructors[0].IsPrivate);

        AssertPublicGetOnlyProperties(
            operationType,
            "Kind",
            "ElementId",
            "SplitOffset",
            "RightElementId",
            "TargetIndex");
        AssertFactory(
            nameof(EditOperation.Keep),
            typeof(EntityId<CompositionElementIdTag>));
        AssertFactory(
            nameof(EditOperation.Delete),
            typeof(EntityId<CompositionElementIdTag>));
        AssertFactory(
            nameof(EditOperation.Split),
            typeof(EntityId<CompositionElementIdTag>),
            typeof(RationalTime),
            typeof(EntityId<CompositionElementIdTag>));
        AssertFactory(
            nameof(EditOperation.Move),
            typeof(EntityId<CompositionElementIdTag>),
            typeof(int));

        Type editorType = typeof(CompositionEditor);
        Assert.IsTrue(editorType.IsPublic);
        Assert.IsTrue(editorType.IsAbstract);
        Assert.IsTrue(editorType.IsSealed);
        MethodInfo apply = editorType.GetMethod(
            nameof(CompositionEditor.Apply),
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new AssertFailedException("CompositionEditor.Apply was not found.");
        Assert.AreEqual(typeof(void), apply.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(WorkingCopy), typeof(EditOperation), typeof(DateTimeOffset) },
            apply.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
    }

    [TestMethod]
    public void FactoriesSetOnlyPropertiesForTheirOperationKind()
    {
        EntityId<CompositionElementIdTag> elementId = EntityId<CompositionElementIdTag>.New();
        EntityId<CompositionElementIdTag> rightId = EntityId<CompositionElementIdTag>.New();
        RationalTime offset = Time(1, 3);

        EditOperation keep = EditOperation.Keep(elementId);
        AssertOperation(keep, EditOperationKind.Keep, elementId);

        EditOperation delete = EditOperation.Delete(elementId);
        AssertOperation(delete, EditOperationKind.Delete, elementId);

        EditOperation split = EditOperation.Split(elementId, offset, rightId);
        AssertOperation(split, EditOperationKind.Split, elementId);
        Assert.AreEqual(offset, split.SplitOffset);
        Assert.AreSame(rightId, split.RightElementId);
        Assert.IsNull(split.TargetIndex);

        EditOperation move = EditOperation.Move(elementId, 2);
        AssertOperation(move, EditOperationKind.Move, elementId);
        Assert.IsNull(move.SplitOffset);
        Assert.IsNull(move.RightElementId);
        Assert.AreEqual(2, move.TargetIndex);
    }

    [TestMethod]
    public void FactoriesRejectNullIdsInvalidOffsetsSameRightIdAndNegativeIndex()
    {
        EntityId<CompositionElementIdTag> elementId = EntityId<CompositionElementIdTag>.New();

        AssertParamName<ArgumentNullException>(
            "elementId",
            () => EditOperation.Keep(null!));
        AssertParamName<ArgumentNullException>(
            "elementId",
            () => EditOperation.Delete(null!));
        AssertParamName<ArgumentNullException>(
            "elementId",
            () => EditOperation.Split(null!, Time(1), EntityId<CompositionElementIdTag>.New()));
        AssertParamName<ArgumentNullException>(
            "rightElementId",
            () => EditOperation.Split(elementId, Time(1), null!));
        AssertParamName<ArgumentNullException>(
            "elementId",
            () => EditOperation.Move(null!, 0));

        foreach (RationalTime offset in new[] { default, Time(-1) })
        {
            AssertParamName<ArgumentOutOfRangeException>(
                "splitOffset",
                () => EditOperation.Split(
                    elementId,
                    offset,
                    EntityId<CompositionElementIdTag>.New()));
        }

        AssertParamName<ArgumentException>(
            "rightElementId",
            () => EditOperation.Split(
                elementId,
                Time(1),
                EntityId<CompositionElementIdTag>.From(elementId.Value)));
        AssertParamName<ArgumentOutOfRangeException>(
            "targetIndex",
            () => EditOperation.Move(elementId, -1));
    }

    [TestMethod]
    public void KeepReplacesReadonlySnapshotWithEqualNewElementsAndTimestamp()
    {
        CompositionElement source = SourceElement(
            outputStart: Time(0),
            duration: Time(2),
            sourceStart: Time(-1));
        CompositionElement asset = AssetElement(2, 3);
        WorkingCopy workingCopy = CreateWorkingCopy(source, asset);
        IReadOnlyList<CompositionElement> previousSnapshot = workingCopy.Elements;
        DateTimeOffset updatedAt = CreatedAt.AddMinutes(1);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Keep(source.Id),
            updatedAt);

        Assert.AreEqual(updatedAt, workingCopy.UpdatedAt);
        CollectionAssert.AreEqual(
            previousSnapshot.ToArray(),
            workingCopy.Elements.ToArray());
        Assert.AreNotSame(previousSnapshot, workingCopy.Elements);
        Assert.AreNotSame(previousSnapshot[0], workingCopy.Elements[0]);
        Assert.AreNotSame(previousSnapshot[1], workingCopy.Elements[1]);
        Assert.AreSame(source, previousSnapshot[0]);
        AssertReadonly(previousSnapshot);
        AssertReadonly(workingCopy.Elements);
    }

    [TestMethod]
    public void DeleteClosesGapAndAllowsEmptyComposition()
    {
        CompositionElement first = SourceElement(Time(0), Time(2), Time(10));
        CompositionElement target = AssetElement(2, 3);
        CompositionElement last = EditorialElement(5, 4);
        WorkingCopy workingCopy = CreateWorkingCopy(first, target, last);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Delete(target.Id),
            CreatedAt.AddMinutes(1));

        CollectionAssert.AreEqual(
            new[] { first.Id, last.Id },
            workingCopy.Elements.Select(static element => element.Id).ToArray());
        Assert.AreEqual(new TimeRange(Time(0), Time(2)), workingCopy.Elements[0].OutputRange);
        Assert.AreEqual(new TimeRange(Time(2), Time(4)), workingCopy.Elements[1].OutputRange);
        Assert.AreEqual(first.SourceRange, workingCopy.Elements[0].SourceRange);
        Assert.AreEqual(last.Origin, workingCopy.Elements[1].Origin);

        WorkingCopy single = CreateWorkingCopy(AssetElement(0, 1));
        CompositionEditor.Apply(
            single,
            EditOperation.Delete(single.Elements[0].Id),
            CreatedAt.AddMinutes(1));
        Assert.AreEqual(0, single.Elements.Count);
        AssertReadonly(single.Elements);
    }

    [TestMethod]
    public void SplitUsesRelativeExactRationalOffsetAndRetainsProvenance()
    {
        CompositionElement prefix = AssetElement(0, 2);
        OriginRef origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
        SuggestionReference adoptedSuggestion = CreateAdoptedSuggestion(origin);
        EntityId<CompositionElementIdTag> targetId = EntityId<CompositionElementIdTag>.New();
        CompositionElement target = new(
            targetId,
            new TimeRange(Time(2), Time(3, 2)),
            origin,
            adoptedSuggestion,
            new TimeRange(Time(-5, 3), Time(3, 2)));
        EntityId<CompositionElementIdTag> rightId = EntityId<CompositionElementIdTag>.New();
        WorkingCopy workingCopy = CreateWorkingCopy(prefix, target);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Split(targetId, Time(1, 2), rightId),
            CreatedAt.AddMinutes(1));

        Assert.AreEqual(3, workingCopy.Elements.Count);
        CompositionElement left = workingCopy.Elements[1];
        CompositionElement right = workingCopy.Elements[2];
        Assert.AreSame(targetId, left.Id);
        Assert.AreSame(rightId, right.Id);
        Assert.AreEqual(new TimeRange(Time(2), Time(1, 2)), left.OutputRange);
        Assert.AreEqual(new TimeRange(Time(5, 2), Time(1)), right.OutputRange);
        Assert.AreEqual(new TimeRange(Time(-5, 3), Time(1, 2)), left.SourceRange);
        Assert.AreEqual(new TimeRange(Time(-7, 6), Time(1)), right.SourceRange);
        Assert.AreSame(origin, left.Origin);
        Assert.AreSame(origin, right.Origin);
        Assert.AreSame(adoptedSuggestion, left.AdoptedSuggestion);
        Assert.AreSame(adoptedSuggestion, right.AdoptedSuggestion);
    }

    [TestMethod]
    public void SplitAssetAndEditorialElementsDoesNotInventSourceRanges()
    {
        foreach (OriginRef origin in new[]
        {
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()),
            OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New()),
        })
        {
            EntityId<CompositionElementIdTag> targetId = EntityId<CompositionElementIdTag>.New();
            EntityId<CompositionElementIdTag> rightId = EntityId<CompositionElementIdTag>.New();
            CompositionElement target = new(
                targetId,
                new TimeRange(default, Time(2)),
                origin);
            WorkingCopy workingCopy = CreateWorkingCopy(target);

            CompositionEditor.Apply(
                workingCopy,
                EditOperation.Split(targetId, Time(1), rightId),
                CreatedAt.AddMinutes(1));

            Assert.AreEqual(2, workingCopy.Elements.Count);
            Assert.IsTrue(workingCopy.Elements.All(element => element.Origin == origin));
            Assert.IsTrue(workingCopy.Elements.All(static element => element.SourceRange is null));
        }
    }

    [TestMethod]
    public void MoveToFrontAndEndReflowsAllOutputRanges()
    {
        CompositionElement first = SourceElement(Time(0), Time(1), Time(10));
        CompositionElement second = AssetElement(1, 2);
        CompositionElement third = EditorialElement(3, 3);
        WorkingCopy workingCopy = CreateWorkingCopy(first, second, third);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Move(third.Id, 0),
            CreatedAt.AddMinutes(1));

        AssertOrderAndRanges(
            workingCopy,
            (third.Id, Time(0), Time(3)),
            (first.Id, Time(3), Time(1)),
            (second.Id, Time(4), Time(2)));
        Assert.AreEqual(first.SourceRange, workingCopy.Elements[1].SourceRange);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Move(third.Id, 2),
            CreatedAt.AddMinutes(2));

        AssertOrderAndRanges(
            workingCopy,
            (first.Id, Time(0), Time(1)),
            (second.Id, Time(1), Time(2)),
            (third.Id, Time(3), Time(3)));
    }

    [TestMethod]
    public void ApplyRejectsNullArgumentsAndMissingTargetsWithoutMutation()
    {
        CompositionElement element = AssetElement(0, 2);
        WorkingCopy workingCopy = CreateWorkingCopy(element);

        AssertParamName<ArgumentNullException>(
            "workingCopy",
            () => CompositionEditor.Apply(null!, EditOperation.Keep(element.Id), CreatedAt));
        AssertParamName<ArgumentNullException>(
            "operation",
            () => CompositionEditor.Apply(workingCopy, null!, CreatedAt));

        EntityId<CompositionElementIdTag> missing = EntityId<CompositionElementIdTag>.New();
        EditOperation[] operations =
        [
            EditOperation.Keep(missing),
            EditOperation.Delete(missing),
            EditOperation.Split(missing, Time(1), EntityId<CompositionElementIdTag>.New()),
            EditOperation.Move(missing, 0),
        ];

        foreach (EditOperation operation in operations)
        {
            IReadOnlyList<CompositionElement> snapshot = workingCopy.Elements;
            DateTimeOffset updatedAt = workingCopy.UpdatedAt;
            AssertParamName<ArgumentException>(
                "operation",
                () => CompositionEditor.Apply(workingCopy, operation, CreatedAt.AddMinutes(1)));
            Assert.AreSame(snapshot, workingCopy.Elements);
            Assert.AreEqual(updatedAt, workingCopy.UpdatedAt);
        }
    }

    [TestMethod]
    public void ApplyRejectsOutOfRangeSplitMoveAndDuplicateRightIdWithoutMutation()
    {
        CompositionElement first = AssetElement(0, 2);
        CompositionElement second = EditorialElement(2, 1);
        WorkingCopy workingCopy = CreateWorkingCopy(first, second);

        foreach (RationalTime offset in new[] { Time(2), Time(3) })
        {
            AssertApplyFailureWithoutMutation<ArgumentOutOfRangeException>(
                workingCopy,
                EditOperation.Split(first.Id, offset, EntityId<CompositionElementIdTag>.New()),
                "operation");
        }

        AssertApplyFailureWithoutMutation<ArgumentOutOfRangeException>(
            workingCopy,
            EditOperation.Move(first.Id, 2),
            "operation");
        AssertApplyFailureWithoutMutation<ArgumentException>(
            workingCopy,
            EditOperation.Split(first.Id, Time(1), second.Id),
            "operation");
    }

    [TestMethod]
    public void ApplyRejectsGapOverlapAndUnorderedInputWithoutMutation()
    {
        CompositionElement[][] invalidSequences =
        [
            [AssetElement(1, 1)],
            [AssetElement(0, 2), AssetElement(1, 1)],
            [AssetElement(0, 1), AssetElement(2, 1), AssetElement(1, 1)],
        ];

        foreach (CompositionElement[] elements in invalidSequences)
        {
            WorkingCopy workingCopy = CreateWorkingCopy(elements);
            IReadOnlyList<CompositionElement> snapshot = workingCopy.Elements;

            AssertParamName<ArgumentException>(
                "workingCopy",
                () => CompositionEditor.Apply(
                    workingCopy,
                    EditOperation.Keep(elements[0].Id),
                    CreatedAt.AddMinutes(1)));
            Assert.AreSame(snapshot, workingCopy.Elements);
            Assert.AreEqual(CreatedAt, workingCopy.UpdatedAt);
        }
    }

    [TestMethod]
    public void ReverseUpdatedAtLeavesWorkingCopyUnchanged()
    {
        CompositionElement element = AssetElement(0, 1);
        WorkingCopy workingCopy = CreateWorkingCopy(element);
        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Keep(element.Id),
            CreatedAt.AddMinutes(1));
        IReadOnlyList<CompositionElement> snapshot = workingCopy.Elements;

        AssertParamName<ArgumentOutOfRangeException>(
            "updatedAt",
            () => CompositionEditor.Apply(
                workingCopy,
                EditOperation.Delete(element.Id),
                CreatedAt));

        Assert.AreSame(snapshot, workingCopy.Elements);
        Assert.AreEqual(CreatedAt.AddMinutes(1), workingCopy.UpdatedAt);
    }

    [TestMethod]
    public void UnrepresentableSplitArithmeticPropagatesOverflowWithoutMutation()
    {
        CompositionElement element = new(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(default, Time(long.MaxValue)),
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()));
        WorkingCopy workingCopy = CreateWorkingCopy(element);
        IReadOnlyList<CompositionElement> snapshot = workingCopy.Elements;

        Assert.ThrowsExactly<OverflowException>(
            () => CompositionEditor.Apply(
                workingCopy,
                EditOperation.Split(
                    element.Id,
                    Time(1, long.MaxValue),
                    EntityId<CompositionElementIdTag>.New()),
                CreatedAt.AddMinutes(1)));

        Assert.AreSame(snapshot, workingCopy.Elements);
        Assert.AreEqual(CreatedAt, workingCopy.UpdatedAt);
    }

    private static void AssertFactory(string name, params Type[] parameterTypes)
    {
        MethodInfo[] methods = typeof(EditOperation).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name)
            .ToArray();
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(typeof(EditOperation), methods[0].ReturnType);
        CollectionAssert.AreEqual(
            parameterTypes,
            methods[0].GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
    }

    private static void AssertOperation(
        EditOperation operation,
        EditOperationKind kind,
        EntityId<CompositionElementIdTag> elementId)
    {
        Assert.AreEqual(kind, operation.Kind);
        Assert.AreSame(elementId, operation.ElementId);
        if (kind is EditOperationKind.Keep or EditOperationKind.Delete)
        {
            Assert.IsNull(operation.SplitOffset);
            Assert.IsNull(operation.RightElementId);
            Assert.IsNull(operation.TargetIndex);
        }
    }

    private static void AssertPublicGetOnlyProperties(Type type, params string[] expectedNames)
    {
        PropertyInfo[] properties = type.GetProperties();
        CollectionAssert.AreEquivalent(
            expectedNames,
            properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.GetMethod?.IsPublic == true));
        Assert.IsTrue(properties.All(static property => property.SetMethod is null));
    }

    private static WorkingCopy CreateWorkingCopy(params CompositionElement[] elements)
    {
        return new WorkingCopy(
            EntityId<WorkingCopyIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            null,
            CreatedAt,
            CreatedAt,
            elements);
    }

    private static CompositionElement SourceElement(
        RationalTime outputStart,
        RationalTime duration,
        RationalTime sourceStart)
    {
        return new CompositionElement(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(outputStart, duration),
            OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New()),
            sourceRange: new TimeRange(sourceStart, duration));
    }

    private static CompositionElement AssetElement(long outputStart, long duration)
    {
        return new CompositionElement(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(Time(outputStart), Time(duration)),
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()));
    }

    private static CompositionElement EditorialElement(long outputStart, long duration)
    {
        return new CompositionElement(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(Time(outputStart), Time(duration)),
            OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New()));
    }

    private static SuggestionReference CreateAdoptedSuggestion(OriginRef origin)
    {
        Suggestion suggestion = new(
            EntityId<SuggestionIdTag>.New(),
            EntityId<ModelRunIdTag>.New(),
            SuggestionKind.Highlight,
            SuggestionStatus.Adopted,
            origin,
            "Retained grounded provenance.",
            null,
            null,
            null);
        return SuggestionReference.FromAdoptedSuggestion(suggestion);
    }

    private static void AssertOrderAndRanges(
        WorkingCopy workingCopy,
        params (EntityId<CompositionElementIdTag> Id, RationalTime Start, RationalTime Duration)[] expected)
    {
        Assert.AreEqual(expected.Length, workingCopy.Elements.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index].Id, workingCopy.Elements[index].Id);
            Assert.AreEqual(
                new TimeRange(expected[index].Start, expected[index].Duration),
                workingCopy.Elements[index].OutputRange);
        }
    }

    private static void AssertApplyFailureWithoutMutation<TException>(
        WorkingCopy workingCopy,
        EditOperation operation,
        string parameterName)
        where TException : ArgumentException
    {
        IReadOnlyList<CompositionElement> snapshot = workingCopy.Elements;
        DateTimeOffset updatedAt = workingCopy.UpdatedAt;

        AssertParamName<TException>(
            parameterName,
            () => CompositionEditor.Apply(
                workingCopy,
                operation,
                updatedAt.AddMinutes(1)));

        Assert.AreSame(snapshot, workingCopy.Elements);
        Assert.AreEqual(updatedAt, workingCopy.UpdatedAt);
    }

    private static void AssertReadonly(IReadOnlyList<CompositionElement> elements)
    {
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<CompositionElement>)elements).Clear());
    }

    private static RationalTime Time(long value, long rate = 1) => new(value, rate);

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}