using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TimeRangeTests
{
    [TestMethod]
    public void ConstructorStoresCanonicalStartDurationAndEnd()
    {
        TimeRange range = new(new RationalTime(2, 4), new RationalTime(6, 4));

        Assert.AreEqual(new RationalTime(1, 2), range.Start);
        Assert.AreEqual(new RationalTime(3, 2), range.Duration);
        Assert.AreEqual(new RationalTime(2, 1), range.End);
    }

    [TestMethod]
    public void DefaultRangeIsEmptyAtZero()
    {
        TimeRange range = default;

        Assert.AreEqual(default(RationalTime), range.Start);
        Assert.AreEqual(default(RationalTime), range.Duration);
        Assert.AreEqual(default(RationalTime), range.End);
    }

    [TestMethod]
    public void ConstructorAllowsNegativeStartAndZeroDuration()
    {
        TimeRange negativeStart = new(new RationalTime(-3, 2), new RationalTime(1, 2));
        TimeRange empty = new(new RationalTime(-3, 2), default);

        Assert.AreEqual(new RationalTime(-1, 1), negativeStart.End);
        Assert.AreEqual(empty.Start, empty.End);
    }

    [TestMethod]
    public void ConstructorRejectsNegativeDurationAtAnyRate()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimeRange(default, new RationalTime(-2, 4)));

        Assert.AreEqual("duration", exception.ParamName);
    }

    [TestMethod]
    public void ContainsUsesHalfOpenBoundariesWithEquivalentRates()
    {
        TimeRange range = new(new RationalTime(-1, 2), new RationalTime(3, 2));

        Assert.IsFalse(range.Contains(new RationalTime(-3, 4)));
        Assert.IsTrue(range.Contains(new RationalTime(-2, 4)));
        Assert.IsTrue(range.Contains(default));
        Assert.IsTrue(range.Contains(new RationalTime(3, 4)));
        Assert.IsFalse(range.Contains(new RationalTime(2, 2)));
        Assert.IsFalse(range.Contains(new RationalTime(5, 4)));
    }

    [TestMethod]
    public void EmptyRangeContainsNoTimeIncludingItsStart()
    {
        TimeRange empty = new(new RationalTime(5, 3), default);

        Assert.IsFalse(empty.Contains(empty.Start));
        Assert.IsFalse(empty.Contains(new RationalTime(5, 3)));
    }

    [TestMethod]
    public void OverlapsDetectsPartialContainmentAndSymmetry()
    {
        TimeRange range = new(default, new RationalTime(2, 1));
        TimeRange partial = new(new RationalTime(3, 2), new RationalTime(1, 1));
        TimeRange contained = new(new RationalTime(1, 2), new RationalTime(1, 2));

        Assert.IsTrue(range.Overlaps(partial));
        Assert.IsTrue(partial.Overlaps(range));
        Assert.IsTrue(range.Overlaps(contained));
        Assert.IsTrue(contained.Overlaps(range));
    }

    [TestMethod]
    public void OverlapsExcludesTouchingDisjointAndEmptyRanges()
    {
        TimeRange range = new(default, new RationalTime(1, 1));
        TimeRange touching = new(new RationalTime(2, 2), new RationalTime(1, 1));
        TimeRange disjoint = new(new RationalTime(2, 1), new RationalTime(1, 1));
        TimeRange emptyInside = new(new RationalTime(1, 2), default);
        TimeRange emptyAtBoundary = new(new RationalTime(1, 1), default);

        Assert.IsFalse(range.Overlaps(touching));
        Assert.IsFalse(touching.Overlaps(range));
        Assert.IsFalse(range.Overlaps(disjoint));
        Assert.IsFalse(range.Overlaps(emptyInside));
        Assert.IsFalse(emptyInside.Overlaps(range));
        Assert.IsFalse(range.Overlaps(emptyAtBoundary));
        Assert.IsFalse(emptyAtBoundary.Overlaps(range));
        Assert.IsFalse(emptyInside.Overlaps(emptyInside));
    }

    [TestMethod]
    public void IsAdjacentToUsesExactEquivalentEndpoints()
    {
        TimeRange first = new(new RationalTime(-1, 2), new RationalTime(3, 2));
        TimeRange second = new(new RationalTime(2, 2), new RationalTime(1, 1));
        TimeRange overlapping = new(new RationalTime(1, 2), new RationalTime(1, 1));
        TimeRange disjoint = new(new RationalTime(3, 1), new RationalTime(1, 1));

        Assert.IsTrue(first.IsAdjacentTo(second));
        Assert.IsTrue(second.IsAdjacentTo(first));
        Assert.IsFalse(first.IsAdjacentTo(overlapping));
        Assert.IsFalse(first.IsAdjacentTo(disjoint));
    }

    [TestMethod]
    public void EmptyRangesCanBeAdjacentAtASharedEndpoint()
    {
        TimeRange range = new(default, new RationalTime(1, 1));
        TimeRange emptyAtStart = new(default, default);
        TimeRange emptyAtEnd = new(new RationalTime(1, 1), default);

        Assert.IsTrue(emptyAtStart.IsAdjacentTo(range));
        Assert.IsTrue(emptyAtEnd.IsAdjacentTo(range));
        Assert.IsTrue(emptyAtStart.IsAdjacentTo(emptyAtStart));
    }

    [TestMethod]
    public void EquivalentRatesGiveRangeValueEquality()
    {
        TimeRange first = new(new RationalTime(1, 2), new RationalTime(3, 2));
        TimeRange second = new(new RationalTime(2, 4), new RationalTime(6, 4));

        Assert.AreEqual(first, second);
        Assert.IsTrue(first == second);
        Assert.IsFalse(first != second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
    }

    [TestMethod]
    public void DistinctRangesAreNotEqual()
    {
        TimeRange first = new(default, new RationalTime(1, 1));
        TimeRange differentStart = new(new RationalTime(1, 2), new RationalTime(1, 1));
        TimeRange differentDuration = new(default, new RationalTime(2, 1));

        Assert.AreNotEqual(first, differentStart);
        Assert.AreNotEqual(first, differentDuration);
        Assert.IsTrue(first != differentStart);
    }

    [TestMethod]
    public void ConstructorAcceptsEndThatReducesAfterLargeIntermediate()
    {
        TimeRange range = new(
            new RationalTime(1, long.MaxValue),
            new RationalTime(long.MaxValue - 1, long.MaxValue));

        Assert.AreEqual(new RationalTime(1, 1), range.End);
    }

    [TestMethod]
    public void ConstructorRejectsEndOverflow()
    {
        Assert.Throws<OverflowException>(
            () => new TimeRange(
                new RationalTime(long.MaxValue, 1),
                new RationalTime(1, 1)));
        Assert.Throws<OverflowException>(
            () => new TimeRange(
                new RationalTime(1, long.MaxValue),
                new RationalTime(1, long.MaxValue - 1)));
    }
}
