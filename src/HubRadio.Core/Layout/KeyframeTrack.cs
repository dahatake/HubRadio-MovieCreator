using System.Numerics;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;

namespace HubRadio.Core.Layout;

public enum LayoutKeyframeInterpolation
{
    /// <summary>
    /// Retains the left keyframe's exact pixel rectangles until the next keyframe.
    /// </summary>
    Hold,

    /// <summary>
    /// Interpolates in exact rational pixel space, then quantizes each position and
    /// independent size-scale multiplier once using nearest-to-even rounding.
    /// </summary>
    Linear,
}

public sealed class LayoutKeyframeValue : IEquatable<LayoutKeyframeValue>
{
    public LayoutKeyframeValue(
        NormalizedRect sourceCrop,
        NormalizedRect destination)
    {
        ArgumentNullException.ThrowIfNull(sourceCrop);
        ArgumentNullException.ThrowIfNull(destination);

        SourceCrop = sourceCrop;
        Destination = destination;
    }

    public NormalizedRect SourceCrop { get; }

    public NormalizedRect Destination { get; }

    public bool Equals(LayoutKeyframeValue? other)
    {
        return other is not null
            && SourceCrop == other.SourceCrop
            && Destination == other.Destination;
    }

    public override bool Equals(object? obj)
    {
        return obj is LayoutKeyframeValue other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(SourceCrop, Destination);
    }
}

public sealed class LayoutKeyframe : IEquatable<LayoutKeyframe>
{
    public LayoutKeyframe(
        RationalTime time,
        LayoutKeyframeValue value,
        LayoutKeyframeInterpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!Enum.IsDefined(interpolation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interpolation),
                interpolation,
                "Unknown layout keyframe interpolation.");
        }

        Time = time;
        Value = value;
        Interpolation = interpolation;
    }

    public RationalTime Time { get; }

    public LayoutKeyframeValue Value { get; }

    /// <summary>
    /// Controls the interval from this keyframe to the next keyframe in the segment.
    /// </summary>
    public LayoutKeyframeInterpolation Interpolation { get; }

    public bool Equals(LayoutKeyframe? other)
    {
        return other is not null
            && Time == other.Time
            && Value.Equals(other.Value)
            && Interpolation == other.Interpolation;
    }

    public override bool Equals(object? obj)
    {
        return obj is LayoutKeyframe other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Time, Value, Interpolation);
    }
}

public sealed class LayoutKeyframeSegment : IEquatable<LayoutKeyframeSegment>
{
    public LayoutKeyframeSegment(
        TimeRange range,
        IReadOnlyList<LayoutKeyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);

        if (range.Duration <= default(RationalTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "A layout keyframe segment must have a positive duration.");
        }

        if (keyframes.Count == 0)
        {
            throw new ArgumentException(
                "A layout keyframe segment must contain at least one keyframe.",
                nameof(keyframes));
        }

        LayoutKeyframe[] copiedKeyframes = new LayoutKeyframe[keyframes.Count];
        RationalTime? previousTime = null;
        for (int index = 0; index < copiedKeyframes.Length; index++)
        {
            LayoutKeyframe? keyframe = keyframes[index];
            if (keyframe is null)
            {
                throw new ArgumentException(
                    "Layout keyframes must not contain null elements.",
                    nameof(keyframes));
            }

            if (keyframe.Time < range.Start || keyframe.Time > range.End)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyframes),
                    keyframe.Time,
                    "Every layout keyframe must be within its segment range, including its endpoints.");
            }

            if (previousTime is not null && keyframe.Time <= previousTime.Value)
            {
                throw new ArgumentException(
                    "Layout keyframe times must be strictly increasing.",
                    nameof(keyframes));
            }

            copiedKeyframes[index] = keyframe;
            previousTime = keyframe.Time;
        }

        if (copiedKeyframes[0].Time != range.Start)
        {
            throw new ArgumentException(
                "A layout keyframe segment must have a keyframe at its exact start.",
                nameof(keyframes));
        }

        if (copiedKeyframes[^1].Interpolation != LayoutKeyframeInterpolation.Hold)
        {
            throw new ArgumentException(
                "The last keyframe in a segment must use hold interpolation.",
                nameof(keyframes));
        }

        Range = range;
        Keyframes = Array.AsReadOnly(copiedKeyframes);
    }

    public TimeRange Range { get; }

    public IReadOnlyList<LayoutKeyframe> Keyframes { get; }

    public bool Equals(LayoutKeyframeSegment? other)
    {
        return other is not null
            && Range == other.Range
            && Keyframes.SequenceEqual(other.Keyframes);
    }

    public override bool Equals(object? obj)
    {
        return obj is LayoutKeyframeSegment other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Range);
        foreach (LayoutKeyframe keyframe in Keyframes)
        {
            hash.Add(keyframe);
        }

        return hash.ToHashCode();
    }
}

public sealed class LayoutKeyframeEvaluation : IEquatable<LayoutKeyframeEvaluation>
{
    internal LayoutKeyframeEvaluation(
        RationalTime requestedTime,
        LayoutKeyframeValue value,
        PixelRect sourceCropPixels,
        PixelRect destinationPixels,
        int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(sourceCropPixels);
        ArgumentNullException.ThrowIfNull(destinationPixels);

        RequestedTime = requestedTime;
        Value = value;
        SourceCropPixels = sourceCropPixels;
        DestinationPixels = destinationPixels;
        SegmentIndex = segmentIndex;
    }

    public RationalTime RequestedTime { get; }

    /// <summary>
    /// Gets the canonical grid-aligned normalized value reconstructed from the
    /// evaluated source and destination pixel rectangles.
    /// </summary>
    public LayoutKeyframeValue Value { get; }

    /// <summary>
    /// Gets the evaluated crop on the display-oriented source pixel grid.
    /// </summary>
    public PixelRect SourceCropPixels { get; }

    /// <summary>
    /// Gets the evaluated destination on the output pixel grid.
    /// </summary>
    public PixelRect DestinationPixels { get; }

    public int SegmentIndex { get; }

    public bool Equals(LayoutKeyframeEvaluation? other)
    {
        return other is not null
            && RequestedTime == other.RequestedTime
            && Value.Equals(other.Value)
            && SourceCropPixels.Equals(other.SourceCropPixels)
            && DestinationPixels.Equals(other.DestinationPixels)
            && SegmentIndex == other.SegmentIndex;
    }

    public override bool Equals(object? obj)
    {
        return obj is LayoutKeyframeEvaluation other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            RequestedTime,
            Value,
            SourceCropPixels,
            DestinationPixels,
            SegmentIndex);
    }
}

/// <summary>
/// An immutable manual crop and destination track bound to one source asset and one
/// destination slot and to concrete source and output pixel grids. Every keyframe must
/// resolve exactly on both grids and retain one display aspect for source crops and one
/// display aspect for destinations. Segment ranges are contiguous. Evaluation uses
/// half-open segment ranges so an internal boundary selects the right segment; the final
/// track end is additionally accepted as an exact endpoint.
/// </summary>
public sealed class KeyframeTrack : IEquatable<KeyframeTrack>
{
    public KeyframeTrack(
        EntityId<AssetIdTag> sourceAssetId,
        ManualDestinationSlot destinationSlot,
        SourceDisplayGeometry sourceDisplayGeometry,
        int outputPixelWidth,
        int outputPixelHeight,
        IReadOnlyList<LayoutKeyframeSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(sourceDisplayGeometry);
        ArgumentNullException.ThrowIfNull(segments);

        if (!Enum.IsDefined(destinationSlot))
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationSlot),
                destinationSlot,
                "Unknown manual destination slot.");
        }

        if (outputPixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputPixelWidth),
                outputPixelWidth,
                "Output pixel width must be positive.");
        }

        if (outputPixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputPixelHeight),
                outputPixelHeight,
                "Output pixel height must be positive.");
        }

        if (segments.Count == 0)
        {
            throw new ArgumentException(
                "A layout keyframe track must contain at least one segment.",
                nameof(segments));
        }

        LayoutKeyframeSegment[] copiedSegments = new LayoutKeyframeSegment[segments.Count];
        PixelSizeBase? sourceSizeBase = null;
        PixelSizeBase? destinationSizeBase = null;
        for (int segmentIndex = 0; segmentIndex < copiedSegments.Length; segmentIndex++)
        {
            LayoutKeyframeSegment? segment = segments[segmentIndex];
            if (segment is null)
            {
                throw new ArgumentException(
                    "Layout keyframe segments must not contain null elements.",
                    nameof(segments));
            }

            if (segmentIndex > 0)
            {
                RationalTime previousEnd = copiedSegments[segmentIndex - 1].Range.End;
                if (segment.Range.Start != previousEnd)
                {
                    string reason = segment.Range.Start < previousEnd ? "overlap" : "gap";
                    throw new ArgumentException(
                        $"Layout keyframe segments must be ordered and contiguous without a {reason}.",
                        nameof(segments));
                }
            }

            foreach (LayoutKeyframe keyframe in segment.Keyframes)
            {
                PixelGeometry pixels = ResolveValueToPixels(
                    keyframe.Value,
                    sourceDisplayGeometry,
                    outputPixelWidth,
                    outputPixelHeight,
                    nameof(segments));

                sourceSizeBase ??= PixelSizeBase.From(pixels.SourceCrop);
                destinationSizeBase ??= PixelSizeBase.From(pixels.Destination);
                ValidateEndpointGeometry(
                    pixels,
                    sourceSizeBase.Value,
                    destinationSizeBase.Value,
                    nameof(segments));
            }

            copiedSegments[segmentIndex] = segment;
        }

        RationalTime trackDuration =
            copiedSegments[^1].Range.End - copiedSegments[0].Range.Start;

        SourceAssetId = sourceAssetId;
        DestinationSlot = destinationSlot;
        SourceDisplayGeometry = sourceDisplayGeometry;
        OutputPixelWidth = outputPixelWidth;
        OutputPixelHeight = outputPixelHeight;
        int outputAspectDivisor = GreatestCommonDivisor(outputPixelWidth, outputPixelHeight);
        OutputAspectWidth = outputPixelWidth / outputAspectDivisor;
        OutputAspectHeight = outputPixelHeight / outputAspectDivisor;
        Range = new TimeRange(copiedSegments[0].Range.Start, trackDuration);
        Segments = Array.AsReadOnly(copiedSegments);
        _sourceSizeBase = sourceSizeBase!.Value;
        _destinationSizeBase = destinationSizeBase!.Value;
    }

    private readonly PixelSizeBase _sourceSizeBase;
    private readonly PixelSizeBase _destinationSizeBase;

    public EntityId<AssetIdTag> SourceAssetId { get; }

    public ManualDestinationSlot DestinationSlot { get; }

    public SourceDisplayGeometry SourceDisplayGeometry { get; }

    public int OutputPixelWidth { get; }

    public int OutputPixelHeight { get; }

    /// <summary>
    /// Gets the reduced output aspect width derived from the concrete output pixel grid.
    /// </summary>
    public int OutputAspectWidth { get; }

    /// <summary>
    /// Gets the reduced output aspect height derived from the concrete output pixel grid.
    /// </summary>
    public int OutputAspectHeight { get; }

    public TimeRange Range { get; }

    public IReadOnlyList<LayoutKeyframeSegment> Segments { get; }

    /// <summary>
    /// Evaluates the common source-crop and destination geometry for preview and render.
    /// </summary>
    /// <remarks>
    /// Linear interpolation uses <see cref="BigInteger"/> exact arithmetic in pixel space.
    /// Each x/y coordinate and each independent source/destination scale multiplier is
    /// quantized exactly once with nearest-to-even rounding. After size quantization,
    /// x/y is deterministically clamped to <c>[0, extent - size]</c>. Hold intervals retain
    /// the left endpoint pixels, and evaluating a keyframe returns its exact endpoint pixels.
    /// </remarks>
    public LayoutKeyframeEvaluation Evaluate(RationalTime time)
    {
        if (time < Range.Start || time > Range.End)
        {
            throw new ArgumentOutOfRangeException(
                nameof(time),
                time,
                "The requested time must be within the keyframe track, including its final endpoint.");
        }

        int segmentIndex = FindSegmentIndex(time);
        PixelGeometry pixels;
        LayoutKeyframeValue value;
        try
        {
            pixels = EvaluateSegment(Segments[segmentIndex], time);
            ValidateEvaluatedGeometry(
                pixels,
                SourceDisplayGeometry,
                OutputPixelWidth,
                OutputPixelHeight,
                _sourceSizeBase,
                _destinationSizeBase);
            value = CreateCanonicalValue(
                pixels,
                SourceDisplayGeometry,
                OutputPixelWidth,
                OutputPixelHeight);

            PixelGeometry roundTrip = ResolveValueToPixels(
                value,
                SourceDisplayGeometry,
                OutputPixelWidth,
                OutputPixelHeight,
                nameof(time));
            if (!pixels.Equals(roundTrip))
            {
                throw new InvalidOperationException(
                    "The evaluated normalized geometry did not round-trip to its pixel rectangles.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                "The evaluated keyframe geometry cannot be represented on the track pixel grids.",
                exception);
        }

        return new LayoutKeyframeEvaluation(
            time,
            value,
            pixels.SourceCrop,
            pixels.Destination,
            segmentIndex);
    }

    public bool Equals(KeyframeTrack? other)
    {
        return other is not null
            && SourceAssetId == other.SourceAssetId
            && DestinationSlot == other.DestinationSlot
            && SourceDisplayGeometry.Equals(other.SourceDisplayGeometry)
            && OutputPixelWidth == other.OutputPixelWidth
            && OutputPixelHeight == other.OutputPixelHeight
            && Segments.SequenceEqual(other.Segments);
    }

    public override bool Equals(object? obj)
    {
        return obj is KeyframeTrack other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(SourceAssetId);
        hash.Add(DestinationSlot);
        hash.Add(SourceDisplayGeometry);
        hash.Add(OutputPixelWidth);
        hash.Add(OutputPixelHeight);
        foreach (LayoutKeyframeSegment segment in Segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }

    private int FindSegmentIndex(RationalTime time)
    {
        if (time == Range.End)
        {
            return Segments.Count - 1;
        }

        for (int index = 0; index < Segments.Count; index++)
        {
            if (Segments[index].Range.Contains(time))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "The contiguous keyframe track did not contain an in-range requested time.");
    }

    private PixelGeometry EvaluateSegment(
        LayoutKeyframeSegment segment,
        RationalTime time)
    {
        IReadOnlyList<LayoutKeyframe> keyframes = segment.Keyframes;
        for (int index = 0; index < keyframes.Count; index++)
        {
            LayoutKeyframe left = keyframes[index];
            if (time == left.Time || index == keyframes.Count - 1)
            {
                return ResolveEndpoint(left.Value);
            }

            LayoutKeyframe right = keyframes[index + 1];
            if (time < right.Time)
            {
                return left.Interpolation switch
                {
                    LayoutKeyframeInterpolation.Hold => ResolveEndpoint(left.Value),
                    LayoutKeyframeInterpolation.Linear => Interpolate(left, right, time),
                    _ => throw new InvalidOperationException(
                        "The keyframe track contains an unknown interpolation mode."),
                };
            }
        }

        throw new InvalidOperationException(
            "The keyframe segment did not contain an in-range requested time.");
    }

    private PixelGeometry ResolveEndpoint(LayoutKeyframeValue value)
    {
        return ResolveValueToPixels(
            value,
            SourceDisplayGeometry,
            OutputPixelWidth,
            OutputPixelHeight,
            "time");
    }

    private PixelGeometry Interpolate(
        LayoutKeyframe left,
        LayoutKeyframe right,
        RationalTime time)
    {
        ExactRational elapsed =
            ExactRational.From(time) - ExactRational.From(left.Time);
        ExactRational duration =
            ExactRational.From(right.Time) - ExactRational.From(left.Time);
        ExactRational progress = elapsed / duration;

        PixelGeometry leftPixels = ResolveEndpoint(left.Value);
        PixelGeometry rightPixels = ResolveEndpoint(right.Value);
        return new PixelGeometry(
            InterpolateRect(
                leftPixels.SourceCrop,
                rightPixels.SourceCrop,
                progress,
                SourceDisplayGeometry.Width,
                SourceDisplayGeometry.Height,
                _sourceSizeBase),
            InterpolateRect(
                leftPixels.Destination,
                rightPixels.Destination,
                progress,
                OutputPixelWidth,
                OutputPixelHeight,
                _destinationSizeBase));
    }

    private static PixelRect InterpolateRect(
        PixelRect left,
        PixelRect right,
        ExactRational progress,
        int pixelWidth,
        int pixelHeight,
        PixelSizeBase sizeBase)
    {
        int leftScale = sizeBase.GetScaleMultiplier(left);
        int rightScale = sizeBase.GetScaleMultiplier(right);
        BigInteger quantizedScale = InterpolateInteger(leftScale, rightScale, progress)
            .RoundNearestToEven();
        if (quantizedScale <= BigInteger.Zero || quantizedScale > int.MaxValue)
        {
            throw new InvalidOperationException(
                "Linear keyframe interpolation produced an unrepresentable positive size scale.");
        }

        BigInteger widthValue = (BigInteger)sizeBase.Width * quantizedScale;
        BigInteger heightValue = (BigInteger)sizeBase.Height * quantizedScale;
        if (widthValue > pixelWidth || heightValue > pixelHeight)
        {
            throw new InvalidOperationException(
                "Linear keyframe interpolation produced a size outside its pixel grid.");
        }

        int width = checked((int)widthValue);
        int height = checked((int)heightValue);
        int x = QuantizeAndConstrainPosition(
            left.X,
            right.X,
            progress,
            pixelWidth - width);
        int y = QuantizeAndConstrainPosition(
            left.Y,
            right.Y,
            progress,
            pixelHeight - height);
        return new PixelRect(x, y, width, height);
    }

    private static ExactRational InterpolateInteger(
        int left,
        int right,
        ExactRational progress)
    {
        ExactRational leftValue = ExactRational.From(left);
        ExactRational rightValue = ExactRational.From(right);
        return leftValue + ((rightValue - leftValue) * progress);
    }

    private static int QuantizeAndConstrainPosition(
        int left,
        int right,
        ExactRational progress,
        int maximum)
    {
        BigInteger quantized = InterpolateInteger(left, right, progress)
            .RoundNearestToEven();
        BigInteger constrained = BigInteger.Max(
            BigInteger.Zero,
            BigInteger.Min(quantized, maximum));
        return checked((int)constrained);
    }

    private static PixelGeometry ResolveValueToPixels(
        LayoutKeyframeValue value,
        SourceDisplayGeometry sourceDisplayGeometry,
        int outputPixelWidth,
        int outputPixelHeight,
        string parameterName)
    {
        return new PixelGeometry(
            ResolveRect(
                value.SourceCrop,
                sourceDisplayGeometry.Width,
                sourceDisplayGeometry.Height,
                "Source crop",
                parameterName),
            ResolveRect(
                value.Destination,
                outputPixelWidth,
                outputPixelHeight,
                "Destination",
                parameterName));
    }

    private static PixelRect ResolveRect(
        NormalizedRect rect,
        int pixelWidth,
        int pixelHeight,
        string description,
        string parameterName)
    {
        int x = ResolveExactPixel(rect.X, pixelWidth, description, "x", parameterName);
        int y = ResolveExactPixel(rect.Y, pixelHeight, description, "y", parameterName);
        int width = ResolveExactPixel(
            rect.Width,
            pixelWidth,
            description,
            "width",
            parameterName);
        int height = ResolveExactPixel(
            rect.Height,
            pixelHeight,
            description,
            "height",
            parameterName);

        var result = new PixelRect(x, y, width, height);
        if (!IsWithinGrid(result, pixelWidth, pixelHeight))
        {
            throw new ArgumentException(
                $"{description} must be a positive rectangle within its pixel grid.",
                parameterName);
        }

        return result;
    }

    private static int ResolveExactPixel(
        RationalNumber value,
        int pixelExtent,
        string description,
        string component,
        string parameterName)
    {
        BigInteger scaledNumerator = (BigInteger)value.Numerator * pixelExtent;
        BigInteger quotient = BigInteger.DivRem(
            scaledNumerator,
            value.Denominator,
            out BigInteger remainder);
        if (!remainder.IsZero)
        {
            throw new ArgumentException(
                $"{description} {component} must resolve to an exact integer pixel.",
                parameterName);
        }

        return checked((int)quotient);
    }

    private static void ValidateEndpointGeometry(
        PixelGeometry pixels,
        PixelSizeBase sourceSizeBase,
        PixelSizeBase destinationSizeBase,
        string parameterName)
    {
        if (!HasSamePixelAspect(pixels.SourceCrop, pixels.Destination))
        {
            throw new ArgumentException(
                "The source crop and destination must have exactly equal display pixel aspect ratios.",
                parameterName);
        }

        if (!sourceSizeBase.TryGetScaleMultiplier(pixels.SourceCrop, out _))
        {
            throw new ArgumentException(
                "Every source crop size must be an integer multiple of the track source aspect base.",
                parameterName);
        }

        if (!destinationSizeBase.TryGetScaleMultiplier(pixels.Destination, out _))
        {
            throw new ArgumentException(
                "Every destination size must be an integer multiple of the track destination aspect base.",
                parameterName);
        }
    }

    private static void ValidateEvaluatedGeometry(
        PixelGeometry pixels,
        SourceDisplayGeometry sourceDisplayGeometry,
        int outputPixelWidth,
        int outputPixelHeight,
        PixelSizeBase sourceSizeBase,
        PixelSizeBase destinationSizeBase)
    {
        if (!IsWithinGrid(
                pixels.SourceCrop,
                sourceDisplayGeometry.Width,
                sourceDisplayGeometry.Height)
            || !IsWithinGrid(
                pixels.Destination,
                outputPixelWidth,
                outputPixelHeight))
        {
            throw new InvalidOperationException(
                "The evaluated keyframe geometry has invalid pixel bounds.");
        }

        if (!HasSamePixelAspect(pixels.SourceCrop, pixels.Destination)
            || !sourceSizeBase.TryGetScaleMultiplier(pixels.SourceCrop, out _)
            || !destinationSizeBase.TryGetScaleMultiplier(pixels.Destination, out _))
        {
            throw new InvalidOperationException(
                "The evaluated keyframe geometry violates the track display-aspect invariant.");
        }
    }

    private static bool HasSamePixelAspect(PixelRect source, PixelRect destination)
    {
        return (BigInteger)source.Width * destination.Height
            == (BigInteger)destination.Width * source.Height;
    }

    private static bool IsWithinGrid(PixelRect rect, int pixelWidth, int pixelHeight)
    {
        return rect.X >= 0
            && rect.Y >= 0
            && rect.Width > 0
            && rect.Height > 0
            && (long)rect.X + rect.Width <= pixelWidth
            && (long)rect.Y + rect.Height <= pixelHeight;
    }

    private static LayoutKeyframeValue CreateCanonicalValue(
        PixelGeometry pixels,
        SourceDisplayGeometry sourceDisplayGeometry,
        int outputPixelWidth,
        int outputPixelHeight)
    {
        return new LayoutKeyframeValue(
            CreateCanonicalRect(
                pixels.SourceCrop,
                sourceDisplayGeometry.Width,
                sourceDisplayGeometry.Height),
            CreateCanonicalRect(
                pixels.Destination,
                outputPixelWidth,
                outputPixelHeight));
    }

    private static NormalizedRect CreateCanonicalRect(
        PixelRect rect,
        int pixelWidth,
        int pixelHeight)
    {
        return new NormalizedRect(
            new RationalNumber(rect.X, pixelWidth),
            new RationalNumber(rect.Y, pixelHeight),
            new RationalNumber(rect.Width, pixelWidth),
            new RationalNumber(rect.Height, pixelHeight));
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            int remainder = left % right;
            left = right;
            right = remainder;
        }

        return left;
    }

    private readonly struct PixelGeometry : IEquatable<PixelGeometry>
    {
        public PixelGeometry(PixelRect sourceCrop, PixelRect destination)
        {
            SourceCrop = sourceCrop;
            Destination = destination;
        }

        public PixelRect SourceCrop { get; }

        public PixelRect Destination { get; }

        public bool Equals(PixelGeometry other)
        {
            return SourceCrop.Equals(other.SourceCrop)
                && Destination.Equals(other.Destination);
        }
    }

    private readonly struct PixelSizeBase
    {
        private PixelSizeBase(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        public static PixelSizeBase From(PixelRect rect)
        {
            int divisor = GreatestCommonDivisor(rect.Width, rect.Height);
            return new PixelSizeBase(rect.Width / divisor, rect.Height / divisor);
        }

        public int GetScaleMultiplier(PixelRect rect)
        {
            if (!TryGetScaleMultiplier(rect, out int multiplier))
            {
                throw new InvalidOperationException(
                    "A keyframe rectangle does not retain its track aspect base.");
            }

            return multiplier;
        }

        public bool TryGetScaleMultiplier(PixelRect rect, out int multiplier)
        {
            if (rect.Width % Width == 0
                && rect.Height % Height == 0
                && rect.Width / Width == rect.Height / Height
                && rect.Width / Width > 0)
            {
                multiplier = rect.Width / Width;
                return true;
            }

            multiplier = 0;
            return false;
        }
    }

    private readonly struct ExactRational
    {
        private ExactRational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero)
            {
                throw new DivideByZeroException();
            }

            if (denominator.Sign < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }

            BigInteger divisor = BigInteger.GreatestCommonDivisor(
                BigInteger.Abs(numerator),
                denominator);
            Numerator = numerator / divisor;
            Denominator = denominator / divisor;
        }

        private BigInteger Numerator { get; }

        private BigInteger Denominator { get; }

        public static ExactRational From(RationalTime value)
        {
            return new ExactRational(value.Value, value.Rate);
        }

        public static ExactRational From(int value)
        {
            return new ExactRational(value, BigInteger.One);
        }

        public BigInteger RoundNearestToEven()
        {
            BigInteger quotient = BigInteger.DivRem(
                Numerator,
                Denominator,
                out BigInteger remainder);
            int halfComparison = (BigInteger.Abs(remainder) * 2).CompareTo(Denominator);
            if (halfComparison < 0 || (halfComparison == 0 && quotient.IsEven))
            {
                return quotient;
            }

            return quotient + remainder.Sign;
        }

        public static ExactRational operator +(
            ExactRational left,
            ExactRational right)
        {
            return new ExactRational(
                (left.Numerator * right.Denominator)
                    + (right.Numerator * left.Denominator),
                left.Denominator * right.Denominator);
        }

        public static ExactRational operator -(
            ExactRational left,
            ExactRational right)
        {
            return new ExactRational(
                (left.Numerator * right.Denominator)
                    - (right.Numerator * left.Denominator),
                left.Denominator * right.Denominator);
        }

        public static ExactRational operator *(
            ExactRational left,
            ExactRational right)
        {
            return new ExactRational(
                left.Numerator * right.Numerator,
                left.Denominator * right.Denominator);
        }

        public static ExactRational operator /(
            ExactRational left,
            ExactRational right)
        {
            return new ExactRational(
                left.Numerator * right.Denominator,
                left.Denominator * right.Numerator);
        }
    }
}