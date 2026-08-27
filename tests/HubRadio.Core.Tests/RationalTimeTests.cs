using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class RationalTimeTests
{
    [TestMethod]
    [DataRow(2L, 4L, 1L, 2L)]
    [DataRow(-2L, 4L, -1L, 2L)]
    [DataRow(0L, long.MaxValue, 0L, 1L)]
    [DataRow(long.MinValue, 2L, -4611686018427387904L, 1L)]
    [DataRow(long.MinValue, long.MaxValue, long.MinValue, long.MaxValue)]
    public void ConstructorReducesToCanonicalLowestTerms(
        long value,
        long rate,
        long expectedValue,
        long expectedRate)
    {
        RationalTime time = new(value, rate);

        Assert.AreEqual(expectedValue, time.Value);
        Assert.AreEqual(expectedRate, time.Rate);
    }

    [TestMethod]
    public void DefaultValueIsCanonicalZero()
    {
        RationalTime value = default;

        Assert.AreEqual(0L, value.Value);
        Assert.AreEqual(1L, value.Rate);
        Assert.AreEqual(new RationalTime(0, long.MaxValue), value);
        Assert.AreEqual(new RationalTime(0, 1).GetHashCode(), value.GetHashCode());
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MinValue)]
    public void ConstructorRejectsNonPositiveRates(long rate)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RationalTime(1, rate));

        Assert.AreEqual("rate", exception.ParamName);
    }

    [TestMethod]
    public void EquivalentRatesHaveEqualValueComparisonAndHashCode()
    {
        RationalTime half = new(1, 2);
        RationalTime equivalentHalf = new(500, 1000);

        Assert.AreEqual(half, equivalentHalf);
        Assert.IsTrue(half == equivalentHalf);
        Assert.IsFalse(half != equivalentHalf);
        Assert.AreEqual(0, half.CompareTo(equivalentHalf));
        Assert.AreEqual(half.GetHashCode(), equivalentHalf.GetHashCode());
    }

    [TestMethod]
    public void ComparisonUsesOverflowSafeCrossProducts()
    {
        RationalTime greater = new(long.MaxValue - 1, long.MaxValue);
        RationalTime lesser = new(long.MaxValue - 2, long.MaxValue - 1);
        RationalTime negative = new(-1, 3);

        Assert.IsTrue(greater > lesser);
        Assert.IsTrue(greater >= lesser);
        Assert.IsTrue(lesser < greater);
        Assert.IsTrue(lesser <= greater);
        Assert.IsTrue(negative < default(RationalTime));
        Assert.IsTrue(default(RationalTime) > negative);
    }

    [TestMethod]
    public void NonGenericComparisonFollowsComparableContract()
    {
        IComparable comparable = new RationalTime(1, 2);

        Assert.AreEqual(1, comparable.CompareTo(null));
        Assert.AreEqual(0, comparable.CompareTo(new RationalTime(2, 4)));
        Assert.Throws<ArgumentException>(() => comparable.CompareTo("1/2"));
    }

    [TestMethod]
    [DataRow(1L, 2L, 1L, 3L, 5L, 6L)]
    [DataRow(-1L, 2L, 1L, 3L, -1L, 6L)]
    [DataRow(-1L, 2L, -1L, 3L, -5L, 6L)]
    public void AdditionProducesReducedExactResult(
        long leftValue,
        long leftRate,
        long rightValue,
        long rightRate,
        long expectedValue,
        long expectedRate)
    {
        RationalTime result = new RationalTime(leftValue, leftRate)
            + new RationalTime(rightValue, rightRate);

        Assert.AreEqual(new RationalTime(expectedValue, expectedRate), result);
    }

    [TestMethod]
    [DataRow(1L, 2L, 3L, 4L, -1L, 4L)]
    [DataRow(-1L, 2L, 1L, 3L, -5L, 6L)]
    [DataRow(-1L, 2L, -1L, 3L, -1L, 6L)]
    public void SubtractionProducesReducedExactResult(
        long leftValue,
        long leftRate,
        long rightValue,
        long rightRate,
        long expectedValue,
        long expectedRate)
    {
        RationalTime result = new RationalTime(leftValue, leftRate)
            - new RationalTime(rightValue, rightRate);

        Assert.AreEqual(new RationalTime(expectedValue, expectedRate), result);
    }

    [TestMethod]
    public void ArithmeticReducesAfterBigIntegerIntermediate()
    {
        RationalTime first = new(1, long.MaxValue);
        RationalTime second = new(long.MaxValue - 1, long.MaxValue);

        RationalTime result = first + second;

        Assert.AreEqual(new RationalTime(1, 1), result);
    }

    [TestMethod]
    public void SubtractingIdenticalMinimumValuesDoesNotOverflow()
    {
        RationalTime minimum = new(long.MinValue, 1);

        Assert.AreEqual(default(RationalTime), minimum - minimum);
    }

    [TestMethod]
    public void ArithmeticThrowsOnlyWhenReducedFinalValueDoesNotFit()
    {
        RationalTime maximum = new(long.MaxValue, 1);
        RationalTime minimum = new(long.MinValue, 1);

        Assert.Throws<OverflowException>(() => _ = maximum + new RationalTime(1, 1));
        Assert.Throws<OverflowException>(() => _ = maximum - minimum);
        Assert.Throws<OverflowException>(
            () => _ = new RationalTime(1, long.MaxValue)
                + new RationalTime(1, long.MaxValue - 1));
    }

    [TestMethod]
    public void UnaryNegationHandlesSignsAndMinimumOverflow()
    {
        Assert.AreEqual(new RationalTime(-1, 2), -new RationalTime(1, 2));
        Assert.AreEqual(new RationalTime(1, 2), -new RationalTime(-1, 2));
        Assert.AreEqual(default(RationalTime), -default(RationalTime));
        Assert.Throws<OverflowException>(() => _ = -new RationalTime(long.MinValue, 1));
    }

    [TestMethod]
    [DataRow(3L, 2L, 1L, RationalTimeRoundingMode.TowardZero, 1L, 1L)]
    [DataRow(3L, 2L, 1L, RationalTimeRoundingMode.Floor, 1L, 1L)]
    [DataRow(3L, 2L, 1L, RationalTimeRoundingMode.Ceiling, 2L, 1L)]
    [DataRow(3L, 2L, 1L, RationalTimeRoundingMode.NearestAwayFromZero, 2L, 1L)]
    [DataRow(3L, 2L, 1L, RationalTimeRoundingMode.NearestToEven, 2L, 1L)]
    [DataRow(-3L, 2L, 1L, RationalTimeRoundingMode.TowardZero, -1L, 1L)]
    [DataRow(-3L, 2L, 1L, RationalTimeRoundingMode.Floor, -2L, 1L)]
    [DataRow(-3L, 2L, 1L, RationalTimeRoundingMode.Ceiling, -1L, 1L)]
    [DataRow(-3L, 2L, 1L, RationalTimeRoundingMode.NearestAwayFromZero, -2L, 1L)]
    [DataRow(-3L, 2L, 1L, RationalTimeRoundingMode.NearestToEven, -2L, 1L)]
    [DataRow(5L, 2L, 1L, RationalTimeRoundingMode.NearestAwayFromZero, 3L, 1L)]
    [DataRow(5L, 2L, 1L, RationalTimeRoundingMode.NearestToEven, 2L, 1L)]
    [DataRow(-5L, 2L, 1L, RationalTimeRoundingMode.NearestAwayFromZero, -3L, 1L)]
    [DataRow(-5L, 2L, 1L, RationalTimeRoundingMode.NearestToEven, -2L, 1L)]
    [DataRow(7L, 4L, 1L, RationalTimeRoundingMode.NearestToEven, 2L, 1L)]
    [DataRow(-7L, 4L, 1L, RationalTimeRoundingMode.NearestToEven, -2L, 1L)]
    [DataRow(5L, 4L, 1L, RationalTimeRoundingMode.NearestAwayFromZero, 1L, 1L)]
    [DataRow(-5L, 4L, 1L, RationalTimeRoundingMode.NearestAwayFromZero, -1L, 1L)]
    [DataRow(1L, 3L, 2L, RationalTimeRoundingMode.NearestToEven, 1L, 2L)]
    [DataRow(1L, 2L, 6L, RationalTimeRoundingMode.Ceiling, 1L, 2L)]
    public void QuantizeAppliesAllRoundingModesExactly(
        long value,
        long rate,
        long targetRate,
        RationalTimeRoundingMode mode,
        long expectedValue,
        long expectedRate)
    {
        RationalTime result = new RationalTime(value, rate).Quantize(targetRate, mode);

        Assert.AreEqual(new RationalTime(expectedValue, expectedRate), result);
    }

    [TestMethod]
    public void QuantizeReducesAfterLargeIntermediate()
    {
        RationalTime value = new(long.MaxValue, 1);

        RationalTime result = value.Quantize(
            long.MaxValue,
            RationalTimeRoundingMode.NearestToEven);

        Assert.AreEqual(value, result);
    }

    [TestMethod]
    public void QuantizeThrowsWhenReducedFinalValueDoesNotFit()
    {
        RationalTime value = new(long.MaxValue, 2);

        Assert.Throws<OverflowException>(
            () => value.Quantize(long.MaxValue, RationalTimeRoundingMode.NearestAwayFromZero));
    }

    [TestMethod]
    public void QuantizeRejectsInvalidRateAndMode()
    {
        RationalTime value = new(1, 2);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => value.Quantize(0, RationalTimeRoundingMode.TowardZero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => value.Quantize(-1, RationalTimeRoundingMode.TowardZero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => value.Quantize(1, (RationalTimeRoundingMode)int.MaxValue));
    }

    [TestMethod]
    public void FromFramesConvertsNtscRatesExactly()
    {
        Assert.AreEqual(
            new RationalTime(1001, 30000),
            RationalTime.FromFrames(1, 30000, 1001));
        Assert.AreEqual(
            new RationalTime(-1001, 30000),
            RationalTime.FromFrames(-1, 30000, 1001));
        Assert.AreEqual(
            new RationalTime(1001, 1),
            RationalTime.FromFrames(30000, 30000, 1001));
    }

    [TestMethod]
    public void FromFramesReducesAfterLargeIntermediate()
    {
        RationalTime result = RationalTime.FromFrames(
            long.MaxValue,
            long.MaxValue,
            long.MaxValue);

        Assert.AreEqual(new RationalTime(long.MaxValue, 1), result);
    }

    [TestMethod]
    public void FromFramesSupportsMinimumFrameNumber()
    {
        Assert.AreEqual(
            new RationalTime(long.MinValue, 1),
            RationalTime.FromFrames(long.MinValue, 1, 1));
    }

    [TestMethod]
    public void FromFramesRejectsInvalidRatesAndFinalOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RationalTime.FromFrames(1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RationalTime.FromFrames(1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RationalTime.FromFrames(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RationalTime.FromFrames(1, 1, -1));
        Assert.Throws<OverflowException>(
            () => RationalTime.FromFrames(long.MaxValue, 1, long.MaxValue));
    }

    [TestMethod]
    public void DisplayConversionsDoNotParticipateInCanonicalMath()
    {
        RationalTime value = new(-2, 4);

        Assert.AreEqual(-0.5, value.ToDouble());
        Assert.AreEqual("-1/2", value.ToString());
    }
}
