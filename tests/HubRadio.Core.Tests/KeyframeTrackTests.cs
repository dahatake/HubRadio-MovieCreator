using System.Reflection;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class KeyframeTrackTests
{
    [TestMethod]
    public void OneStaticKeyframeHoldsFromNegativeStartThroughFinalEndpoint()
    {
        LayoutKeyframeValue value = SquareValue(Rational(1, 4));
        KeyframeTrack track = CreateTrack(
            Segment(
                Time(-2),
                Time(5),
                Key(Time(-2), value, LayoutKeyframeInterpolation.Hold)));

        foreach (RationalTime time in new[] { Time(-2), Time(-1, 3), Time(3) })
        {
            LayoutKeyframeEvaluation evaluation = track.Evaluate(time);

            Assert.AreEqual(time, evaluation.RequestedTime);
            Assert.AreEqual(value, evaluation.Value);
            AssertRect(evaluation.SourceCropPixels, 2, 2, 2, 2);
            AssertRect(evaluation.DestinationPixels, 2, 2, 2, 2);
            Assert.AreEqual(0, evaluation.SegmentIndex);
        }

        Assert.AreEqual(new TimeRange(Time(-2), Time(5)), track.Range);
    }

    [TestMethod]
    public void LinearInterpolationPreservesExactEndpointsAndInterpolatesAllEightComponents()
    {
        LayoutKeyframeValue left = PortraitLargeValue();
        LayoutKeyframeValue right = PortraitZoomValue();
        KeyframeTrack track = CreatePortraitTrack(
            Segment(
                Time(0),
                Time(3),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(2), right, LayoutKeyframeInterpolation.Hold)));

        LayoutKeyframeEvaluation start = track.Evaluate(Time(0));
        LayoutKeyframeEvaluation midpoint = track.Evaluate(Time(1));
        LayoutKeyframeEvaluation endpoint = track.Evaluate(Time(2));
        LayoutKeyframeEvaluation finalEnd = track.Evaluate(Time(3));

        Assert.AreEqual(left, start.Value);
        Assert.AreEqual(right, endpoint.Value);
        Assert.AreEqual(right, finalEnd.Value);
        AssertRect(start.SourceCropPixels, 0, 0, 540, 960);
        AssertRect(start.DestinationPixels, 0, 0, 540, 960);
        AssertRect(endpoint.SourceCropPixels, 960, 540, 270, 480);
        AssertRect(endpoint.DestinationPixels, 810, 1440, 270, 480);
        AssertRect(finalEnd.SourceCropPixels, 960, 540, 270, 480);
        AssertRect(finalEnd.DestinationPixels, 810, 1440, 270, 480);
        AssertRect(midpoint.SourceCropPixels, 480, 270, 405, 720);
        AssertRect(midpoint.DestinationPixels, 405, 720, 405, 720);
        AssertRect(midpoint.Value.SourceCrop, 1, 4, 1, 4, 27, 128, 2, 3);
        AssertRect(midpoint.Value.Destination, 3, 8, 3, 8, 3, 8, 3, 8);
    }

    [TestMethod]
    public void LinearOnePixelMotionUsesFixedNearestToEvenAndRemainsResolvable()
    {
        LayoutKeyframeValue left = new(
            GridRect(0, 0, 2, 2, 8, 8),
            GridRect(0, 0, 2, 2, 8, 8));
        LayoutKeyframeValue right = new(
            GridRect(1, 1, 2, 2, 8, 8),
            GridRect(1, 1, 2, 2, 8, 8));
        KeyframeTrack track = CreateTrack(
            Segment(
                Time(0),
                Time(2),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(1), right)));

        LayoutKeyframeEvaluation midpoint = track.Evaluate(Time(1, 2));
        LayoutKeyframeEvaluation threeQuarters = track.Evaluate(Time(3, 4));

        AssertRect(midpoint.SourceCropPixels, 0, 0, 2, 2);
        AssertRect(midpoint.DestinationPixels, 0, 0, 2, 2);
        AssertRect(threeQuarters.SourceCropPixels, 1, 1, 2, 2);
        AssertRect(threeQuarters.DestinationPixels, 1, 1, 2, 2);

        ResolvedLayout resolved = ResolveDestination(track, midpoint);
        Assert.AreEqual(midpoint.DestinationPixels, resolved.Regions.Single().Rect);
    }

    [TestMethod]
    public void NearestToEvenTieSelectsTheEvenPixelOnEitherSide()
    {
        LayoutKeyframeValue left = new(
            GridRect(0, 1, 2, 2, 8, 8),
            GridRect(0, 1, 2, 2, 8, 8));
        LayoutKeyframeValue right = new(
            GridRect(1, 2, 2, 2, 8, 8),
            GridRect(1, 2, 2, 2, 8, 8));
        KeyframeTrack track = CreateTrack(
            Segment(
                Time(0),
                Time(2),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(1), right)));

        LayoutKeyframeEvaluation midpoint = track.Evaluate(Time(1, 2));

        AssertRect(midpoint.SourceCropPixels, 0, 2, 2, 2);
        AssertRect(midpoint.DestinationPixels, 0, 2, 2, 2);
    }

    [TestMethod]
    public void LinearInterpolationHandlesUnequalIntervalsNegativeTimesAndMixedDenominators()
    {
        LayoutKeyframeValue first = SquareValue(Rational(0));
        LayoutKeyframeValue second = SquareValue(Rational(1, 4));
        LayoutKeyframeValue third = SquareValue(Rational(3, 4));
        KeyframeTrack track = CreateTrack(
            Segment(
                Time(-5, 3),
                Time(14, 3),
                Key(Time(-5, 3), first, LayoutKeyframeInterpolation.Linear),
                Key(Time(1, 2), second, LayoutKeyframeInterpolation.Linear),
                Key(Time(5, 2), third, LayoutKeyframeInterpolation.Hold)));

        LayoutKeyframeValue firstMidpoint = track.Evaluate(Time(-7, 12)).Value;
        LayoutKeyframeValue secondMidpoint = track.Evaluate(Time(3, 2)).Value;

        Assert.AreEqual(Rational(1, 8), firstMidpoint.SourceCrop.X);
        Assert.AreEqual(Rational(1, 8), firstMidpoint.Destination.Y);
        Assert.AreEqual(Rational(1, 2), secondMidpoint.SourceCrop.X);
        Assert.AreEqual(Rational(1, 2), secondMidpoint.Destination.Y);
    }

    [TestMethod]
    public void HoldUsesLeftValueWhileLinearUsesExactProgress()
    {
        LayoutKeyframeValue first = SquareValue(Rational(0));
        LayoutKeyframeValue second = SquareValue(Rational(1, 4));
        LayoutKeyframeValue third = SquareValue(Rational(3, 4));
        KeyframeTrack track = CreateTrack(
            Segment(
                Time(0),
                Time(4),
                Key(Time(0), first, LayoutKeyframeInterpolation.Hold),
                Key(Time(1), second, LayoutKeyframeInterpolation.Linear),
                Key(Time(3), third, LayoutKeyframeInterpolation.Hold)));

        LayoutKeyframeEvaluation held = track.Evaluate(Time(1, 2));

        Assert.AreEqual(first, held.Value);
        AssertRect(held.SourceCropPixels, 0, 0, 2, 2);
        AssertRect(held.DestinationPixels, 0, 0, 2, 2);
        Assert.AreEqual(
            Rational(1, 2),
            track.Evaluate(Time(2)).Value.SourceCrop.X);
        Assert.AreEqual(third, track.Evaluate(Time(7, 2)).Value);
    }

    [TestMethod]
    public void SourceAndDestinationScalesInterpolateIndependentlyWithoutDistortion()
    {
        LayoutKeyframeValue left = new(
            GridRect(0, 0, 540, 960, 1920, 1080),
            GridRect(0, 0, 270, 480, 1080, 1920));
        LayoutKeyframeValue right = new(
            GridRect(0, 0, 270, 480, 1920, 1080),
            GridRect(0, 0, 540, 960, 1080, 1920));
        KeyframeTrack track = CreatePortraitTrack(
            Segment(
                Time(0),
                Time(4),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(3), right)));

        LayoutKeyframeEvaluation evaluation = track.Evaluate(Time(1));

        AssertRect(evaluation.SourceCropPixels, 0, 0, 450, 800);
        AssertRect(evaluation.DestinationPixels, 0, 0, 360, 640);
        AssertPixelAspect(evaluation.SourceCropPixels, evaluation.DestinationPixels);
    }

    [TestMethod]
    public void ScaleMultiplierUsesNearestToEvenAndAlwaysProducesPositiveBaseMultiple()
    {
        LayoutKeyframeValue left = new(
            GridRect(0, 0, 2, 3, 8, 12),
            GridRect(0, 0, 2, 3, 8, 12));
        LayoutKeyframeValue right = new(
            GridRect(0, 0, 4, 6, 8, 12),
            GridRect(0, 0, 4, 6, 8, 12));
        KeyframeTrack track = new(
            EntityId<AssetIdTag>.New(),
            ManualDestinationSlot.SingleSubject,
            new SourceDisplayGeometry(8, 12),
            8,
            12,
            [Segment(
                Time(0),
                Time(2),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(1), right))]);

        LayoutKeyframeEvaluation midpoint = track.Evaluate(Time(1, 2));

        AssertRect(midpoint.SourceCropPixels, 0, 0, 4, 6);
        AssertRect(midpoint.DestinationPixels, 0, 0, 4, 6);
        Assert.IsGreaterThan(0, midpoint.SourceCropPixels.Width);
        Assert.IsGreaterThan(0, midpoint.SourceCropPixels.Height);
        Assert.IsGreaterThan(0, midpoint.DestinationPixels.Width);
        Assert.IsGreaterThan(0, midpoint.DestinationPixels.Height);
    }

    [TestMethod]
    public void QuantizedPositionIsClampedAfterSizeQuantizationAtFrameEdge()
    {
        LayoutKeyframeValue left = new(
            GridRect(8, 12, 6, 9, 14, 21),
            GridRect(8, 12, 6, 9, 14, 21));
        LayoutKeyframeValue right = new(
            GridRect(6, 9, 8, 12, 14, 21),
            GridRect(6, 9, 8, 12, 14, 21));
        KeyframeTrack track = new(
            EntityId<AssetIdTag>.New(),
            ManualDestinationSlot.SingleSubject,
            new SourceDisplayGeometry(14, 21),
            14,
            21,
            [Segment(
                Time(0),
                Time(2),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(1), right))]);

        LayoutKeyframeEvaluation midpoint = track.Evaluate(Time(1, 2));

        AssertRect(midpoint.SourceCropPixels, 6, 9, 8, 12);
        AssertRect(midpoint.DestinationPixels, 6, 9, 8, 12);
        Assert.AreEqual(Rational(3, 7), midpoint.Value.SourceCrop.X);
        Assert.AreEqual(Rational(3, 7), midpoint.Value.SourceCrop.Y);
        Assert.AreEqual(midpoint.DestinationPixels, ResolveDestination(track, midpoint).Regions[0].Rect);
    }

    [TestMethod]
    public void InternalBoundarySelectsRightSegmentWithoutCrossInterpolation()
    {
        LayoutKeyframeValue firstStart = SquareValue(Rational(0));
        LayoutKeyframeValue firstEnd = SquareValue(Rational(1, 2));
        LayoutKeyframeValue rightBoundary = SquareValue(Rational(3, 4));
        LayoutKeyframeValue thirdBoundary = SquareValue(Rational(1, 4));
        KeyframeTrack track = CreateTrack(
            Segment(
                Time(-1),
                Time(3),
                Key(Time(-1), firstStart, LayoutKeyframeInterpolation.Linear),
                Key(Time(2), firstEnd, LayoutKeyframeInterpolation.Hold)),
            Segment(
                Time(2),
                Time(2),
                Key(Time(2), rightBoundary, LayoutKeyframeInterpolation.Hold)),
            Segment(
                Time(4),
                Time(1),
                Key(Time(4), thirdBoundary, LayoutKeyframeInterpolation.Hold)));

        Assert.AreNotEqual(
            rightBoundary,
            track.Evaluate(Time(1999, 1000)).Value);

        LayoutKeyframeEvaluation second = track.Evaluate(Time(2));
        Assert.AreEqual(1, second.SegmentIndex);
        Assert.AreEqual(rightBoundary, second.Value);

        LayoutKeyframeEvaluation third = track.Evaluate(Time(4));
        Assert.AreEqual(2, third.SegmentIndex);
        Assert.AreEqual(thirdBoundary, third.Value);
        Assert.AreEqual(thirdBoundary, track.Evaluate(Time(5)).Value);
    }

    [TestMethod]
    public void SegmentRejectsDuplicateUnorderedOutOfRangeMissingStartAndNonHoldFinalKeyframes()
    {
        LayoutKeyframeValue value = SquareValue(Rational(0));
        TimeRange range = new(Time(0), Time(2));

        AssertParamName<ArgumentException>(
            "keyframes",
            () => new LayoutKeyframeSegment(
                range,
                [Key(Time(0), value), Key(Time(0), value)]));
        AssertParamName<ArgumentException>(
            "keyframes",
            () => new LayoutKeyframeSegment(
                range,
                [Key(Time(0), value), Key(Time(3, 2), value), Key(Time(1), value)]));
        AssertParamName<ArgumentOutOfRangeException>(
            "keyframes",
            () => new LayoutKeyframeSegment(
                range,
                [Key(Time(0), value), Key(Time(3), value)]));
        AssertParamName<ArgumentException>(
            "keyframes",
            () => new LayoutKeyframeSegment(range, [Key(Time(1), value)]));
        AssertParamName<ArgumentException>(
            "keyframes",
            () => new LayoutKeyframeSegment(
                range,
                [Key(Time(0), value, LayoutKeyframeInterpolation.Linear)]));
    }

    [TestMethod]
    public void SegmentRejectsZeroDurationEmptyNullCollectionAndNullElement()
    {
        LayoutKeyframeValue value = SquareValue(Rational(0));

        AssertParamName<ArgumentOutOfRangeException>(
            "range",
            () => new LayoutKeyframeSegment(
                new TimeRange(Time(0), Time(0)),
                [Key(Time(0), value)]));
        AssertParamName<ArgumentException>(
            "keyframes",
            () => new LayoutKeyframeSegment(
                new TimeRange(Time(0), Time(1)),
                []));
        AssertParamName<ArgumentNullException>(
            "keyframes",
            () => new LayoutKeyframeSegment(
                new TimeRange(Time(0), Time(1)),
                null!));
        AssertParamName<ArgumentException>(
            "keyframes",
            () => new LayoutKeyframeSegment(
                new TimeRange(Time(0), Time(1)),
                [null!]));
    }

    [TestMethod]
    public void TrackRejectsSegmentGapOverlapEmptyNullCollectionAndNullElement()
    {
        LayoutKeyframeSegment first = StaticSegment(Time(0), Time(1), Rational(0));
        LayoutKeyframeSegment gap = StaticSegment(Time(2), Time(1), Rational(1, 4));
        LayoutKeyframeSegment overlap = StaticSegment(Time(1, 2), Time(1), Rational(1, 4));

        AssertParamName<ArgumentException>(
            "segments",
            () => CreateTrack(first, gap));
        AssertParamName<ArgumentException>(
            "segments",
            () => CreateTrack(first, overlap));
        AssertParamName<ArgumentException>(
            "segments",
            () => CreateTrack([]));
        AssertParamName<ArgumentNullException>(
            "segments",
            () => CreateTrack(null!));
        AssertParamName<ArgumentException>(
            "segments",
            () => CreateTrack([null!]));
    }

    [TestMethod]
    public void TrackRejectsAnUnrepresentableTotalRangeWithoutIntegerOverflow()
    {
        LayoutKeyframeSegment first = StaticSegment(
            Time(long.MinValue),
            Time(long.MaxValue),
            Rational(0));
        LayoutKeyframeSegment second = StaticSegment(
            Time(-1),
            Time(long.MaxValue),
            Rational(1, 4));

        Assert.ThrowsExactly<OverflowException>(() => CreateTrack(first, second));
    }

    [TestMethod]
    public void ConstructorsRejectUndefinedEnumsNullsAndInvalidOutputPixelDimensions()
    {
        LayoutKeyframeValue value = SquareValue(Rational(0));
        LayoutKeyframeSegment segment = StaticSegment(Time(0), Time(1), Rational(0));
        EntityId<AssetIdTag> sourceId = EntityId<AssetIdTag>.New();
        SourceDisplayGeometry geometry = new(8, 8);

        AssertParamName<ArgumentNullException>(
            "sourceCrop",
            () => new LayoutKeyframeValue(null!, FullRect()));
        AssertParamName<ArgumentNullException>(
            "destination",
            () => new LayoutKeyframeValue(FullRect(), null!));
        AssertParamName<ArgumentNullException>(
            "value",
            () => new LayoutKeyframe(Time(0), null!, LayoutKeyframeInterpolation.Hold));
        AssertParamName<ArgumentOutOfRangeException>(
            "interpolation",
            () => new LayoutKeyframe(
                Time(0),
                value,
                (LayoutKeyframeInterpolation)int.MaxValue));
        AssertParamName<ArgumentNullException>(
            "sourceAssetId",
            () => new KeyframeTrack(
                null!, ManualDestinationSlot.SingleSubject, geometry, 8, 8, [segment]));
        AssertParamName<ArgumentNullException>(
            "sourceDisplayGeometry",
            () => new KeyframeTrack(
                sourceId, ManualDestinationSlot.SingleSubject, null!, 8, 8, [segment]));
        AssertParamName<ArgumentOutOfRangeException>(
            "destinationSlot",
            () => new KeyframeTrack(
                sourceId, (ManualDestinationSlot)int.MaxValue, geometry, 8, 8, [segment]));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputPixelWidth",
            () => new KeyframeTrack(
                sourceId, ManualDestinationSlot.SingleSubject, geometry, 0, 8, [segment]));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputPixelWidth",
            () => new KeyframeTrack(
                sourceId, ManualDestinationSlot.SingleSubject, geometry, -1, 8, [segment]));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputPixelHeight",
            () => new KeyframeTrack(
                sourceId, ManualDestinationSlot.SingleSubject, geometry, 8, 0, [segment]));
        AssertParamName<ArgumentOutOfRangeException>(
            "outputPixelHeight",
            () => new KeyframeTrack(
                sourceId, ManualDestinationSlot.SingleSubject, geometry, 8, -1, [segment]));
    }

    [TestMethod]
    public void TrackRejectsTheFormer607Point5PixelSourceEndpoint()
    {
        LayoutKeyframeValue fractionalSource = new(
            Rect(0, 1, 0, 1, 81, 256, 8, 9),
            Rect(0, 1, 0, 1, 1, 2, 1, 2));

        AssertParamName<ArgumentException>(
            "segments",
            () => CreatePortraitTrack(
                Segment(Time(0), Time(1), Key(Time(0), fractionalSource))));
    }

    [TestMethod]
    [DataRow("source-x")]
    [DataRow("source-y")]
    [DataRow("source-width")]
    [DataRow("source-height")]
    [DataRow("destination-x")]
    [DataRow("destination-y")]
    [DataRow("destination-width")]
    [DataRow("destination-height")]
    public void TrackRejectsEveryFractionalEndpointPixelComponent(string component)
    {
        LayoutKeyframeValue value = ValueWithFractionalComponent(component);

        AssertParamName<ArgumentException>(
            "segments",
            () => CreateTrack(
                Segment(Time(0), Time(1), Key(Time(0), value))));
    }

    [TestMethod]
    public void TrackRejectsDistortionAtAKeyframeAndAspectChangesBetweenKeyframes()
    {
        LayoutKeyframeValue distorted = new(
            Rect(0, 1, 0, 1, 1, 2, 1, 4),
            Rect(0, 1, 0, 1, 1, 4, 1, 4));
        LayoutKeyframeValue square = SquareValue(Rational(0));
        LayoutKeyframeValue wide = new(
            Rect(0, 1, 0, 1, 1, 2, 1, 4),
            Rect(0, 1, 0, 1, 1, 2, 1, 4));

        AssertParamName<ArgumentException>(
            "segments",
            () => CreateTrack(
                Segment(Time(0), Time(1), Key(Time(0), distorted))));
        AssertParamName<ArgumentException>(
            "segments",
            () => CreateTrack(
                Segment(
                    Time(0),
                    Time(2),
                    Key(Time(0), square, LayoutKeyframeInterpolation.Linear),
                    Key(Time(1), wide))));
    }

    [TestMethod]
    public void ExactAspectIsPreservedAtKeyframesAndIntermediateValues()
    {
        LayoutKeyframeValue left = PortraitLargeValue();
        LayoutKeyframeValue right = PortraitZoomValue();
        KeyframeTrack track = CreatePortraitTrack(
            Segment(
                Time(0),
                Time(2),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(2), right)));

        foreach (RationalTime time in new[] { Time(0), Time(1, 3), Time(1), Time(5, 3), Time(2) })
        {
            LayoutKeyframeEvaluation actual = track.Evaluate(time);
            AssertDisplayAspect(
                actual.Value,
                new SourceDisplayGeometry(1920, 1080),
                track.OutputAspectWidth,
                track.OutputAspectHeight);
            AssertPixelAspect(actual.SourceCropPixels, actual.DestinationPixels);
        }
    }

    [TestMethod]
    public void ExtremeRationalProgressUsesBigIntegerWithoutIntermediateOverflow()
    {
        LayoutKeyframeValue left = new(
            GridRect(0, 0, 2, 2, 8, 8),
            GridRect(0, 0, 2, 2, 8, 8));
        LayoutKeyframeValue right = new(
            GridRect(1, 1, 2, 2, 8, 8),
            GridRect(1, 1, 2, 2, 8, 8));
        KeyframeTrack track = CreateTrack(
            Segment(
                Time(0),
                Time(2),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(1), right)));

        LayoutKeyframeEvaluation nearStart = track.Evaluate(Time(1, long.MaxValue));
        LayoutKeyframeEvaluation nearEnd = track.Evaluate(
            Time(long.MaxValue - 1, long.MaxValue));

        AssertRect(nearStart.SourceCropPixels, 0, 0, 2, 2);
        AssertRect(nearStart.DestinationPixels, 0, 0, 2, 2);
        AssertRect(nearEnd.SourceCropPixels, 1, 1, 2, 2);
        AssertRect(nearEnd.DestinationPixels, 1, 1, 2, 2);
    }

    [TestMethod]
    public void EvaluateRejectsOnlyTimesOutsideClosedTrackRange()
    {
        KeyframeTrack track = CreateTrack(
            StaticSegment(Time(-1), Time(2), Rational(0)));

        Assert.AreEqual(Time(-1), track.Evaluate(Time(-1)).RequestedTime);
        Assert.AreEqual(Time(1), track.Evaluate(Time(1)).RequestedTime);
        AssertParamName<ArgumentOutOfRangeException>(
            "time",
            () => track.Evaluate(Time(-1000001, 1000000)));
        AssertParamName<ArgumentOutOfRangeException>(
            "time",
            () => track.Evaluate(Time(1000001, 1000000)));
    }

    [TestMethod]
    public void SourceSlotCollectionsAndValuesRemainFixedWithValueSemantics()
    {
        EntityId<AssetIdTag> sourceId = EntityId<AssetIdTag>.New();
        LayoutKeyframeValue value = SquareValue(Rational(0));
        var keyframes = new List<LayoutKeyframe> { Key(Time(0), value) };
        var segment = new LayoutKeyframeSegment(
            new TimeRange(Time(0), Time(1)),
            keyframes);
        var segments = new List<LayoutKeyframeSegment> { segment };
        KeyframeTrack track = new(
            sourceId,
            ManualDestinationSlot.SpeakerInset,
            new SourceDisplayGeometry(8, 8),
            8,
            8,
            segments);

        keyframes.Clear();
        segments.Clear();

        KeyframeTrack equivalent = new(
            EntityId<AssetIdTag>.From(sourceId.Value),
            ManualDestinationSlot.SpeakerInset,
            new SourceDisplayGeometry(8, 8),
            8,
            8,
            [StaticSegment(Time(0), Time(1), Rational(0))]);
        KeyframeTrack differentSource = new(
            EntityId<AssetIdTag>.New(),
            ManualDestinationSlot.SpeakerInset,
            new SourceDisplayGeometry(8, 8),
            8,
            8,
            [StaticSegment(Time(0), Time(1), Rational(0))]);
        KeyframeTrack differentSlot = new(
            EntityId<AssetIdTag>.From(sourceId.Value),
            ManualDestinationSlot.MainMaterial,
            new SourceDisplayGeometry(8, 8),
            8,
            8,
            [StaticSegment(Time(0), Time(1), Rational(0))]);
        KeyframeTrack differentOutputGrid = new(
            EntityId<AssetIdTag>.From(sourceId.Value),
            ManualDestinationSlot.SpeakerInset,
            new SourceDisplayGeometry(8, 8),
            16,
            16,
            [StaticSegment(Time(0), Time(1), Rational(0))]);

        Assert.AreEqual(sourceId, track.SourceAssetId);
        Assert.AreEqual(ManualDestinationSlot.SpeakerInset, track.DestinationSlot);
        Assert.AreEqual(8, track.OutputPixelWidth);
        Assert.AreEqual(8, track.OutputPixelHeight);
        Assert.AreEqual(1, track.OutputAspectWidth);
        Assert.AreEqual(1, track.OutputAspectHeight);
        Assert.AreEqual(1, track.Segments.Count);
        Assert.AreEqual(1, track.Segments[0].Keyframes.Count);
        Assert.AreEqual(track, equivalent);
        Assert.AreEqual(track.GetHashCode(), equivalent.GetHashCode());
        Assert.AreNotEqual(track, differentSource);
        Assert.AreNotEqual(track, differentSlot);
        Assert.AreNotEqual(track, differentOutputGrid);
        Assert.AreNotEqual(track.GetHashCode(), differentOutputGrid.GetHashCode());
        Assert.AreEqual(track.Evaluate(Time(1, 2)), equivalent.Evaluate(Time(1, 2)));
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<LayoutKeyframeSegment>)track.Segments).Clear());
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<LayoutKeyframe>)track.Segments[0].Keyframes).Clear());
    }

    [TestMethod]
    public void EvaluatedDestinationUsesOneCanonicalResultForPreviewAndRenderPixels()
    {
        LayoutKeyframeValue left = PortraitLargeValue();
        LayoutKeyframeValue right = PortraitZoomValue();
        KeyframeTrack track = CreatePortraitTrack(
            Segment(
                Time(0),
                Time(2),
                Key(Time(0), left, LayoutKeyframeInterpolation.Linear),
                Key(Time(2), right)));
        LayoutKeyframeEvaluation canonical = track.Evaluate(Time(1));
        LayoutKeyframeEvaluation previewEvaluation = canonical;
        LayoutKeyframeEvaluation renderEvaluation = canonical;

        Assert.AreEqual(1080, track.OutputPixelWidth);
        Assert.AreEqual(1920, track.OutputPixelHeight);
        Assert.AreEqual(9, track.OutputAspectWidth);
        Assert.AreEqual(16, track.OutputAspectHeight);

        ResolvedLayout preview = ResolveDestination(track, previewEvaluation);
        ResolvedLayout render = ResolveDestination(track, renderEvaluation);

        Assert.AreSame(previewEvaluation, renderEvaluation);
        AssertRect(canonical.SourceCropPixels, 480, 270, 405, 720);
        AssertRect(preview.Regions.Single().Rect, 405, 720, 405, 720);
        Assert.AreEqual(canonical.DestinationPixels, preview.Regions.Single().Rect);
        Assert.AreEqual(canonical.DestinationPixels, render.Regions.Single().Rect);
        Assert.AreEqual(preview.Regions.Single().Rect, render.Regions.Single().Rect);
    }

    [TestMethod]
    public void PublicApiIsTypedImmutableAndIndependentOfAutomaticDetectors()
    {
        Type[] contractTypes =
        [
            typeof(LayoutKeyframeInterpolation),
            typeof(LayoutKeyframeValue),
            typeof(LayoutKeyframe),
            typeof(LayoutKeyframeSegment),
            typeof(LayoutKeyframeEvaluation),
            typeof(KeyframeTrack),
        ];

        foreach (Type type in contractTypes.Where(static type => type.IsClass))
        {
            Assert.IsTrue(type.IsSealed);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null));
        }

        CollectionAssert.AreEquivalent(
            new[]
            {
                "RequestedTime",
                "Value",
                "SourceCropPixels",
                "DestinationPixels",
                "SegmentIndex",
            },
            typeof(LayoutKeyframeEvaluation).GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "SourceAssetId",
                "DestinationSlot",
                "SourceDisplayGeometry",
                "OutputPixelWidth",
                "OutputPixelHeight",
                "OutputAspectWidth",
                "OutputAspectHeight",
                "Range",
                "Segments",
            },
            typeof(KeyframeTrack).GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "sourceAssetId",
                "destinationSlot",
                "sourceDisplayGeometry",
                "outputPixelWidth",
                "outputPixelHeight",
                "segments",
            },
            typeof(KeyframeTrack).GetConstructors().Single().GetParameters()
                .Select(static parameter => parameter.Name)
                .ToArray());

        foreach (Type type in contractTypes)
        {
            Assert.IsFalse(type.Name.Contains("Face", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(type.Name.Contains("Detector", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(type.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase));

            IEnumerable<Type> surfaceTypes = type.GetConstructors()
                .SelectMany(static constructor => constructor.GetParameters())
                .Select(static parameter => parameter.ParameterType)
                .Concat(type.GetProperties().Select(static property => property.PropertyType))
                .Concat(type.GetMethods(
                        BindingFlags.Public
                        | BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.DeclaredOnly)
                    .Where(static method => !method.IsSpecialName)
                    .SelectMany(static method => method.GetParameters()
                        .Select(static parameter => parameter.ParameterType)
                        .Append(method.ReturnType)));

            Assert.IsFalse(surfaceTypes.Any(IsForbiddenSurfaceType));
        }

        MethodInfo[] evaluationMethods = typeof(KeyframeTrack).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => method.Name.Contains("Evaluate", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(1, evaluationMethods.Length);
        Assert.AreEqual(nameof(KeyframeTrack.Evaluate), evaluationMethods[0].Name);
        Assert.IsFalse(typeof(KeyframeTrack).Assembly.GetReferencedAssemblies().Any(static assembly =>
            assembly.Name?.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static bool IsForbiddenSurfaceType(Type type)
    {
        if (type == typeof(string) || type == typeof(double) || type == typeof(float))
        {
            return true;
        }

        if (type.Namespace == "HubRadio.Core.Analysis")
        {
            return true;
        }

        return type.IsGenericType
            && type.GetGenericArguments().Any(IsForbiddenSurfaceType);
    }

    private static KeyframeTrack CreateTrack(params LayoutKeyframeSegment[] segments)
    {
        return CreateTrack((IReadOnlyList<LayoutKeyframeSegment>)segments);
    }

    private static KeyframeTrack CreateTrack(IReadOnlyList<LayoutKeyframeSegment> segments)
    {
        return new KeyframeTrack(
            EntityId<AssetIdTag>.New(),
            ManualDestinationSlot.SingleSubject,
            new SourceDisplayGeometry(8, 8),
            8,
            8,
            segments);
    }

    private static KeyframeTrack CreatePortraitTrack(params LayoutKeyframeSegment[] segments)
    {
        return new KeyframeTrack(
            EntityId<AssetIdTag>.New(),
            ManualDestinationSlot.SingleSubject,
            new SourceDisplayGeometry(1920, 1080),
            1080,
            1920,
            segments);
    }

    private static ResolvedLayout ResolveDestination(
        KeyframeTrack track,
        LayoutKeyframeEvaluation evaluation)
    {
        LayoutSpec spec = new(
            EntityId<LayoutSpecIdTag>.New(),
            track.OutputAspectWidth,
            track.OutputAspectHeight,
            [new LayoutRegion(
                "evaluated-destination",
                evaluation.Value.Destination,
                LayoutRegionRole.Video)],
            []);
        return LayoutResolver.Resolve(
            spec,
            track.OutputPixelWidth,
            track.OutputPixelHeight);
    }

    private static LayoutKeyframeSegment StaticSegment(
        RationalTime start,
        RationalTime duration,
        RationalNumber position)
    {
        return Segment(start, duration, Key(start, SquareValue(position)));
    }

    private static LayoutKeyframeSegment Segment(
        RationalTime start,
        RationalTime duration,
        params LayoutKeyframe[] keyframes)
    {
        return new LayoutKeyframeSegment(new TimeRange(start, duration), keyframes);
    }

    private static LayoutKeyframe Key(
        RationalTime time,
        LayoutKeyframeValue value,
        LayoutKeyframeInterpolation interpolation = LayoutKeyframeInterpolation.Hold)
    {
        return new LayoutKeyframe(time, value, interpolation);
    }

    private static LayoutKeyframeValue SquareValue(RationalNumber position)
    {
        NormalizedRect rect = new(position, position, Rational(1, 4), Rational(1, 4));
        return new LayoutKeyframeValue(rect, rect);
    }

    private static LayoutKeyframeValue PortraitLargeValue()
    {
        return new LayoutKeyframeValue(
            Rect(0, 1, 0, 1, 9, 32, 8, 9),
            Rect(0, 1, 0, 1, 1, 2, 1, 2));
    }

    private static LayoutKeyframeValue PortraitZoomValue()
    {
        return new LayoutKeyframeValue(
            Rect(1, 2, 1, 2, 9, 64, 4, 9),
            Rect(3, 4, 3, 4, 1, 4, 1, 4));
    }

    private static LayoutKeyframeValue ValueWithFractionalComponent(string component)
    {
        NormalizedRect aligned = GridRect(0, 0, 2, 2, 8, 8);
        NormalizedRect fractionalX = Rect(1, 16, 0, 1, 1, 4, 1, 4);
        NormalizedRect fractionalY = Rect(0, 1, 1, 16, 1, 4, 1, 4);
        NormalizedRect fractionalWidth = Rect(0, 1, 0, 1, 3, 16, 1, 4);
        NormalizedRect fractionalHeight = Rect(0, 1, 0, 1, 1, 4, 3, 16);

        return component switch
        {
            "source-x" => new LayoutKeyframeValue(fractionalX, aligned),
            "source-y" => new LayoutKeyframeValue(fractionalY, aligned),
            "source-width" => new LayoutKeyframeValue(fractionalWidth, aligned),
            "source-height" => new LayoutKeyframeValue(fractionalHeight, aligned),
            "destination-x" => new LayoutKeyframeValue(aligned, fractionalX),
            "destination-y" => new LayoutKeyframeValue(aligned, fractionalY),
            "destination-width" => new LayoutKeyframeValue(aligned, fractionalWidth),
            "destination-height" => new LayoutKeyframeValue(aligned, fractionalHeight),
            _ => throw new ArgumentOutOfRangeException(
                nameof(component),
                component,
                "Unknown fractional component fixture."),
        };
    }

    private static NormalizedRect GridRect(
        int x,
        int y,
        int width,
        int height,
        int pixelWidth,
        int pixelHeight)
    {
        return new NormalizedRect(
            Rational(x, pixelWidth),
            Rational(y, pixelHeight),
            Rational(width, pixelWidth),
            Rational(height, pixelHeight));
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
            Rational(xNumerator, xDenominator),
            Rational(yNumerator, yDenominator),
            Rational(widthNumerator, widthDenominator),
            Rational(heightNumerator, heightDenominator));
    }

    private static RationalNumber Rational(long numerator, long denominator = 1)
    {
        return new RationalNumber(numerator, denominator);
    }

    private static RationalTime Time(long value, long rate = 1)
    {
        return new RationalTime(value, rate);
    }

    private static void AssertDisplayAspect(
        LayoutKeyframeValue value,
        SourceDisplayGeometry source,
        int outputAspectWidth,
        int outputAspectHeight)
    {
        System.Numerics.BigInteger sourceNumerator =
            (System.Numerics.BigInteger)source.Width
            * value.SourceCrop.Width.Numerator
            * value.SourceCrop.Height.Denominator;
        System.Numerics.BigInteger sourceDenominator =
            (System.Numerics.BigInteger)source.Height
            * value.SourceCrop.Height.Numerator
            * value.SourceCrop.Width.Denominator;
        System.Numerics.BigInteger destinationNumerator =
            (System.Numerics.BigInteger)outputAspectWidth
            * value.Destination.Width.Numerator
            * value.Destination.Height.Denominator;
        System.Numerics.BigInteger destinationDenominator =
            (System.Numerics.BigInteger)outputAspectHeight
            * value.Destination.Height.Numerator
            * value.Destination.Width.Denominator;

        Assert.AreEqual(
            sourceNumerator * destinationDenominator,
            destinationNumerator * sourceDenominator);
    }

    private static void AssertPixelAspect(PixelRect source, PixelRect destination)
    {
        Assert.AreEqual(
            (System.Numerics.BigInteger)source.Width * destination.Height,
            (System.Numerics.BigInteger)destination.Width * source.Height);
    }

    private static void AssertRect(
        NormalizedRect actual,
        long xNumerator,
        long xDenominator,
        long yNumerator,
        long yDenominator,
        long widthNumerator,
        long widthDenominator,
        long heightNumerator,
        long heightDenominator)
    {
        Assert.AreEqual(Rational(xNumerator, xDenominator), actual.X);
        Assert.AreEqual(Rational(yNumerator, yDenominator), actual.Y);
        Assert.AreEqual(Rational(widthNumerator, widthDenominator), actual.Width);
        Assert.AreEqual(Rational(heightNumerator, heightDenominator), actual.Height);
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

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }
}