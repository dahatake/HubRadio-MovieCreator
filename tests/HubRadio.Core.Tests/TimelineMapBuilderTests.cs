using System.Reflection;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TimelineMapBuilderTests
{
    private const int DeterministicCaseCount = 100;

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ContractExposesOnlyTheStaticBuildMethod()
    {
        Type builderType = typeof(TimelineMapBuilder);

        Assert.IsTrue(builderType.IsPublic);
        Assert.IsTrue(builderType.IsAbstract);
        Assert.IsTrue(builderType.IsSealed);
        Assert.AreEqual(
            0,
            builderType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length);
        Assert.AreEqual(
            0,
            builderType.GetProperties(BindingFlags.Public | BindingFlags.Static).Length);
        Assert.AreEqual(
            0,
            builderType.GetFields(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);

        MethodInfo[] methods = builderType.GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual(nameof(TimelineMapBuilder.Build), methods[0].Name);
        Assert.AreEqual(typeof(TimelineMap), methods[0].ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(EntityId<TimelineMapIdTag>), typeof(CompositionVersion) },
            methods[0].GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
    }

    [TestMethod]
    public void EmptyVersionBuildsEmptyMapWithExactSuppliedIdentities()
    {
        EntityId<TimelineMapIdTag> mapId = EntityId<TimelineMapIdTag>.New();
        CompositionVersion version = CreateVersion([]);

        TimelineMap map = TimelineMapBuilder.Build(mapId, version);

        Assert.AreSame(mapId, map.Id);
        Assert.AreSame(version.Id, map.CompositionVersionId);
        Assert.AreEqual(0, map.Entries.Count);
        Assert.IsFalse(map.TryMapOutputTime(default, out _));
    }

    [TestMethod]
    public void DeleteCutAfterR001ClosesOutputGapAndRemovesOnlyItsMapping()
    {
        CompositionElement first = SourceElement(Time(0), Time(2), Time(10));
        CompositionElement removed = SourceElement(Time(2), Time(3), Time(20));
        CompositionElement last = SourceElement(Time(5), Time(4), Time(30));
        WorkingCopy workingCopy = CreateWorkingCopy(first, removed, last);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Delete(removed.Id),
            CreatedAt.AddMinutes(1));
        CompositionVersion version = CreateVersion(workingCopy.Elements);

        TimelineMap map = TimelineMapBuilder.Build(
            EntityId<TimelineMapIdTag>.New(),
            version);

        AssertEntriesExactlyMatch(map, version.Elements);
        Assert.AreEqual(new TimeRange(Time(0), Time(2)), map.Entries[0].OutputRange);
        Assert.AreEqual(new TimeRange(Time(2), Time(4)), map.Entries[1].OutputRange);
        Assert.AreEqual(first.SourceRange, map.Entries[0].SourceRange);
        Assert.AreEqual(last.SourceRange, map.Entries[1].SourceRange);
        Assert.AreEqual(0, map.MapOriginToOutputRanges(removed.Origin).Count);

        Assert.IsTrue(map.TryMapOutputTime(Time(2), out TimelineMapPoint mapped));
        Assert.AreSame(last.Origin, mapped.Origin);
        Assert.AreEqual(Time(30), mapped.SourceTime);
    }

    [TestMethod]
    public void MixedOriginConcatPreservesOrderAndMapsStartMiddleAndEndExclusiveTimes()
    {
        CompositionElement source = SourceElement(
            Time(0),
            Time(3, 2),
            Time(10));
        CompositionElement asset = AssetElement(Time(3, 2), Time(2, 3));
        CompositionElement editorial = EditorialElement(Time(13, 6), Time(5, 6));
        CompositionVersion version = CreateVersion([source, asset, editorial]);

        TimelineMap map = TimelineMapBuilder.Build(
            EntityId<TimelineMapIdTag>.New(),
            version);

        AssertEntriesExactlyMatch(map, version.Elements);
        AssertMappedSource(map, Time(0), source.Origin, Time(10));
        AssertMappedSource(map, Time(3, 4), source.Origin, Time(43, 4));
        AssertMappedSource(
            map,
            Time(1499, 1000),
            source.Origin,
            Time(11499, 1000));
        AssertMappedOriginWithoutSource(map, Time(3, 2), asset.Origin);
        AssertMappedOriginWithoutSource(map, Time(2), asset.Origin);
        AssertMappedOriginWithoutSource(map, Time(2999, 1000), editorial.Origin);
        Assert.IsFalse(map.TryMapOutputTime(Time(3), out _));
    }

    [TestMethod]
    public void MoveAfterR001ReordersMappingsAndPreservesReverseSourceOffsets()
    {
        CompositionElement first = SourceElement(Time(0), Time(1), Time(10));
        CompositionElement second = AssetElement(Time(1), Time(2));
        CompositionElement third = SourceElement(Time(3), Time(3), Time(30));
        WorkingCopy workingCopy = CreateWorkingCopy(first, second, third);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Move(third.Id, 0),
            CreatedAt.AddMinutes(1));
        CompositionVersion version = CreateVersion(workingCopy.Elements);

        TimelineMap map = TimelineMapBuilder.Build(
            EntityId<TimelineMapIdTag>.New(),
            version);

        AssertEntriesExactlyMatch(map, version.Elements);
        CollectionAssert.AreEqual(
            new[] { third.Origin, first.Origin, second.Origin },
            map.Entries.Select(static entry => entry.Origin).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new TimeRange(Time(0), Time(3)),
                new TimeRange(Time(3), Time(1)),
                new TimeRange(Time(4), Time(2)),
            },
            map.Entries.Select(static entry => entry.OutputRange).ToArray());
        CollectionAssert.AreEqual(
            new[] { Time(2) },
            map.MapSourceTimeToOutput(third.Origin.SourceSpanId!, Time(32)).ToArray());
        CollectionAssert.AreEqual(
            new[] { new TimeRange(Time(3), Time(1)) },
            map.MapOriginToOutputRanges(first.Origin).ToArray());
    }

    [TestMethod]
    public void SplitAfterR001PreservesExactNegativeSourceSubrangesAndReverseMappings()
    {
        CompositionElement target = SourceElement(
            Time(0),
            Time(5, 2),
            Time(-7, 3));
        WorkingCopy workingCopy = CreateWorkingCopy(target);

        CompositionEditor.Apply(
            workingCopy,
            EditOperation.Split(
                target.Id,
                Time(2, 3),
                EntityId<CompositionElementIdTag>.New()),
            CreatedAt.AddMinutes(1));
        CompositionVersion version = CreateVersion(workingCopy.Elements);

        TimelineMap map = TimelineMapBuilder.Build(
            EntityId<TimelineMapIdTag>.New(),
            version);

        AssertEntriesExactlyMatch(map, version.Elements);
        Assert.AreEqual(
            new TimeRange(Time(-7, 3), Time(2, 3)),
            map.Entries[0].SourceRange);
        Assert.AreEqual(
            new TimeRange(Time(-5, 3), Time(11, 6)),
            map.Entries[1].SourceRange);
        AssertMappedSource(map, Time(2, 3), target.Origin, Time(-5, 3));
        CollectionAssert.AreEqual(
            new[] { Time(2, 3) },
            map.MapSourceTimeToOutput(
                target.Origin.SourceSpanId!,
                Time(-5, 3)).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new TimeRange(Time(0), Time(2, 3)),
                new TimeRange(Time(2, 3), Time(11, 6)),
            },
            map.MapOriginToOutputRanges(target.Origin).ToArray());
    }

    [TestMethod]
    public void AssetAndEditorialEntriesNeverAcquireFabricatedSourceRanges()
    {
        CompositionElement asset = AssetElement(Time(0), Time(2));
        CompositionElement editorial = EditorialElement(Time(2), Time(3));
        CompositionVersion version = CreateVersion([asset, editorial]);

        TimelineMap map = TimelineMapBuilder.Build(
            EntityId<TimelineMapIdTag>.New(),
            version);

        AssertEntriesExactlyMatch(map, version.Elements);
        Assert.IsNull(map.Entries[0].SourceRange);
        Assert.IsNull(map.Entries[1].SourceRange);
        AssertMappedOriginWithoutSource(map, Time(1), asset.Origin);
        AssertMappedOriginWithoutSource(map, Time(4), editorial.Origin);
        CollectionAssert.AreEqual(
            new[] { new TimeRange(Time(0), Time(2)) },
            map.MapOriginToOutputRanges(asset.Origin).ToArray());
        CollectionAssert.AreEqual(
            new[] { new TimeRange(Time(2), Time(3)) },
            map.MapOriginToOutputRanges(editorial.Origin).ToArray());
    }

    [TestMethod]
    public void SourceSpanWithoutSourceRangeCannotReachBuilder()
    {
        OriginRef source = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());

        AssertParamName<ArgumentNullException>(
            "sourceRange",
            () => new CompositionElement(
                EntityId<CompositionElementIdTag>.New(),
                new TimeRange(default, Time(1)),
                source));
    }

    [TestMethod]
    public void BuildRejectsNullMapIdAndVersion()
    {
        CompositionVersion version = CreateVersion([]);

        AssertParamName<ArgumentNullException>(
            "id",
            () => TimelineMapBuilder.Build(null!, version));
        AssertParamName<ArgumentNullException>(
            "version",
            () => TimelineMapBuilder.Build(EntityId<TimelineMapIdTag>.New(), null!));
    }

    [TestMethod]
    public void BuildRejectsGapOverlapUnorderedAndDuplicateRangesWithoutMutation()
    {
        CompositionElement[][] invalidSequences =
        [
            [AssetElement(Time(0), Time(1)), EditorialElement(Time(2), Time(1))],
            [AssetElement(Time(0), Time(2)), EditorialElement(Time(1), Time(1))],
            [
                AssetElement(Time(0), Time(1)),
                EditorialElement(Time(1), Time(1)),
                SourceElement(Time(1, 2), Time(1), Time(5)),
            ],
            [AssetElement(Time(0), Time(1)), EditorialElement(Time(0), Time(1))],
        ];

        foreach (CompositionElement[] elements in invalidSequences)
        {
            CompositionVersion version = CreateVersion(elements);
            IReadOnlyList<CompositionElement> snapshot = version.Elements;
            CompositionElement[] elementSnapshot = version.Elements.ToArray();
            EntityId<TimelineMapIdTag> mapId = EntityId<TimelineMapIdTag>.New();
            Guid mapIdValue = mapId.Value;

            AssertParamName<ArgumentException>(
                "version",
                () => TimelineMapBuilder.Build(mapId, version));

            Assert.AreSame(snapshot, version.Elements);
            CollectionAssert.AreEqual(elementSnapshot, version.Elements.ToArray());
            for (int index = 0; index < elementSnapshot.Length; index++)
            {
                Assert.AreSame(elementSnapshot[index], version.Elements[index]);
            }

            Assert.AreEqual(mapIdValue, mapId.Value);
        }
    }

    [TestMethod]
    public void EquivalentVersionsBuildEquivalentEntriesAndDifferOnlyBySuppliedMapId()
    {
        EntityId<CompositionVersionIdTag> firstVersionId =
            DeterministicId<CompositionVersionIdTag>(1);
        EntityId<CompositionVersionIdTag> secondVersionId =
            EntityId<CompositionVersionIdTag>.From(firstVersionId.Value);
        EntityId<CompositionElementIdTag> firstElementId =
            DeterministicId<CompositionElementIdTag>(2);
        EntityId<CompositionElementIdTag> secondElementId =
            EntityId<CompositionElementIdTag>.From(firstElementId.Value);
        EntityId<SourceSpanIdTag> firstSourceSpanId =
            DeterministicId<SourceSpanIdTag>(3);
        EntityId<SourceSpanIdTag> secondSourceSpanId =
            EntityId<SourceSpanIdTag>.From(firstSourceSpanId.Value);
        TimeRange outputRange = new(Time(0), Time(7, 3));
        TimeRange sourceRange = new(Time(-11, 5), Time(7, 3));

        CompositionVersion firstVersion = CreateVersion(
            [
                new CompositionElement(
                    firstElementId,
                    outputRange,
                    OriginRef.FromSourceSpan(firstSourceSpanId),
                    sourceRange: sourceRange),
            ],
            firstVersionId);
        CompositionVersion secondVersion = CreateVersion(
            [
                new CompositionElement(
                    secondElementId,
                    outputRange,
                    OriginRef.FromSourceSpan(secondSourceSpanId),
                    sourceRange: sourceRange),
            ],
            secondVersionId);
        EntityId<TimelineMapIdTag> firstMapId = DeterministicId<TimelineMapIdTag>(4);
        EntityId<TimelineMapIdTag> secondMapId = DeterministicId<TimelineMapIdTag>(5);

        TimelineMap firstMap = TimelineMapBuilder.Build(firstMapId, firstVersion);
        TimelineMap secondMap = TimelineMapBuilder.Build(secondMapId, secondVersion);

        Assert.AreSame(firstMapId, firstMap.Id);
        Assert.AreSame(secondMapId, secondMap.Id);
        Assert.AreNotEqual(firstMap.Id, secondMap.Id);
        Assert.AreSame(firstVersion.Id, firstMap.CompositionVersionId);
        Assert.AreSame(secondVersion.Id, secondMap.CompositionVersionId);
        Assert.AreEqual(firstMap.CompositionVersionId, secondMap.CompositionVersionId);
        CollectionAssert.AreEqual(firstMap.Entries.ToArray(), secondMap.Entries.ToArray());
    }

    [TestMethod]
    public void SeededEditCasesPreserveContiguousCoverageAndExactSourceOffsets()
    {
        Random random = new(2002);
        int casesWithDeletion = 0;
        int casesWithPermutation = 0;

        for (int caseIndex = 0; caseIndex < DeterministicCaseCount; caseIndex++)
        {
            int elementCount = random.Next(3, 9);
            List<CompositionElement> initial = [];
            RationalTime outputStart = default;

            for (int elementIndex = 0; elementIndex < elementCount; elementIndex++)
            {
                RationalTime duration = Time(
                    random.Next(1, 9),
                    1L << random.Next(0, 3));
                RationalTime sourceStart = Time(
                    random.Next(-20, 21),
                    1L << random.Next(0, 3));
                int deterministicIndex = (caseIndex * 100) + elementIndex + 1;
                OriginRef origin = OriginRef.FromSourceSpan(
                    DeterministicId<SourceSpanIdTag>(20_000 + deterministicIndex));
                initial.Add(
                    new CompositionElement(
                        DeterministicId<CompositionElementIdTag>(deterministicIndex),
                        new TimeRange(outputStart, duration),
                        origin,
                        sourceRange: new TimeRange(sourceStart, duration)));
                outputStart += duration;
            }

            WorkingCopy workingCopy = CreateWorkingCopy(initial.ToArray());
            DateTimeOffset updatedAt = CreatedAt;
            bool deleted = false;

            foreach (CompositionElement element in initial)
            {
                if (random.Next(0, 4) != 0)
                {
                    continue;
                }

                updatedAt = updatedAt.AddTicks(1);
                CompositionEditor.Apply(
                    workingCopy,
                    EditOperation.Delete(element.Id),
                    updatedAt);
                deleted = true;
            }

            if (deleted)
            {
                casesWithDeletion++;
            }

            EntityId<CompositionElementIdTag>[] originalOrder =
                workingCopy.Elements.Select(static element => element.Id).ToArray();
            List<EntityId<CompositionElementIdTag>> targetOrder = [.. originalOrder];
            Shuffle(targetOrder, random);
            if (!targetOrder.SequenceEqual(originalOrder))
            {
                casesWithPermutation++;
            }

            for (int targetIndex = 0; targetIndex < targetOrder.Count; targetIndex++)
            {
                if (workingCopy.Elements[targetIndex].Id == targetOrder[targetIndex])
                {
                    continue;
                }

                updatedAt = updatedAt.AddTicks(1);
                CompositionEditor.Apply(
                    workingCopy,
                    EditOperation.Move(targetOrder[targetIndex], targetIndex),
                    updatedAt);
            }

            CompositionVersion version = CreateVersion(
                workingCopy.Elements,
                DeterministicId<CompositionVersionIdTag>(40_000 + caseIndex));
            TimelineMap map = TimelineMapBuilder.Build(
                DeterministicId<TimelineMapIdTag>(50_000 + caseIndex),
                version);

            AssertEntriesExactlyMatch(map, version.Elements);
            RationalTime expectedStart = default;
            foreach (TimelineMapEntry entry in map.Entries)
            {
                Assert.AreEqual(expectedStart, entry.OutputRange.Start);
                Assert.IsNotNull(entry.SourceRange);
                RationalTime sourceOffset = Half(entry.OutputRange.Duration);
                RationalTime outputTime = entry.OutputRange.Start + sourceOffset;
                RationalTime sourceTime = entry.SourceRange.Value.Start + sourceOffset;

                AssertMappedSource(map, outputTime, entry.Origin, sourceTime);
                CollectionAssert.AreEqual(
                    new[] { outputTime },
                    map.MapSourceTimeToOutput(
                        entry.Origin.SourceSpanId!,
                        sourceTime).ToArray());
                expectedStart = entry.OutputRange.End;
            }

            Assert.IsFalse(map.TryMapOutputTime(expectedStart, out _));
        }

        Assert.IsTrue(casesWithDeletion > 0);
        Assert.IsTrue(casesWithPermutation > 0);
    }

    private static void AssertEntriesExactlyMatch(
        TimelineMap map,
        IReadOnlyList<CompositionElement> elements)
    {
        Assert.AreEqual(elements.Count, map.Entries.Count);
        for (int index = 0; index < elements.Count; index++)
        {
            Assert.AreEqual(elements[index].OutputRange, map.Entries[index].OutputRange);
            Assert.AreSame(elements[index].Origin, map.Entries[index].Origin);
            Assert.AreEqual(elements[index].SourceRange, map.Entries[index].SourceRange);
        }
    }

    private static void AssertMappedSource(
        TimelineMap map,
        RationalTime outputTime,
        OriginRef expectedOrigin,
        RationalTime expectedSourceTime)
    {
        Assert.IsTrue(map.TryMapOutputTime(outputTime, out TimelineMapPoint mapped));
        Assert.AreSame(expectedOrigin, mapped.Origin);
        Assert.AreEqual(expectedSourceTime, mapped.SourceTime);
    }

    private static void AssertMappedOriginWithoutSource(
        TimelineMap map,
        RationalTime outputTime,
        OriginRef expectedOrigin)
    {
        Assert.IsTrue(map.TryMapOutputTime(outputTime, out TimelineMapPoint mapped));
        Assert.AreSame(expectedOrigin, mapped.Origin);
        Assert.IsNull(mapped.SourceTime);
    }

    private static CompositionVersion CreateVersion(
        IReadOnlyList<CompositionElement> elements,
        EntityId<CompositionVersionIdTag>? id = null)
    {
        return new CompositionVersion(
            id ?? EntityId<CompositionVersionIdTag>.New(),
            EntityId<ProjectIdTag>.New(),
            null,
            "S-1-5-21-1000",
            CreatedAt,
            "Build the canonical timeline map.",
            new ContentDigest(new string('a', 64)),
            elements);
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

    private static CompositionElement AssetElement(
        RationalTime outputStart,
        RationalTime duration)
    {
        return new CompositionElement(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(outputStart, duration),
            OriginRef.FromAsset(EntityId<AssetIdTag>.New()));
    }

    private static CompositionElement EditorialElement(
        RationalTime outputStart,
        RationalTime duration)
    {
        return new CompositionElement(
            EntityId<CompositionElementIdTag>.New(),
            new TimeRange(outputStart, duration),
            OriginRef.FromEditorialRecord(EntityId<EditorialRecordIdTag>.New()));
    }

    private static EntityId<TTag> DeterministicId<TTag>(int value)
    {
        return EntityId<TTag>.From(
            Guid.ParseExact($"00000000-0000-0000-0000-{value:x12}", "D"));
    }

    private static RationalTime Half(RationalTime value)
    {
        return new RationalTime(value.Value, checked(value.Rate * 2));
    }

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (int index = values.Count - 1; index > 0; index--)
        {
            int otherIndex = random.Next(0, index + 1);
            (values[index], values[otherIndex]) = (values[otherIndex], values[index]);
        }
    }

    private static RationalTime Time(long value, long rate = 1) => new(value, rate);

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}