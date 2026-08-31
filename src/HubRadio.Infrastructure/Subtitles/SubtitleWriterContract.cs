using System.Globalization;
using System.Numerics;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Time;

namespace HubRadio.Infrastructure.Subtitles;

public enum SubtitleFileFormat
{
    WebVtt,
    SubRip,
    AdvancedSubStationAlpha,
}

public enum SubtitleFileCueStyle
{
    Normal,
    Emphasis,
    Caution,
    Quote,
}

public sealed class SubtitleFileCue
{
    public SubtitleFileCue(
        Cue cue,
        string text,
        SubtitleFileCueStyle style)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (cue.Kind != CueKind.Subtitle)
        {
            throw new ArgumentException(
                "A subtitle file cue must contain a subtitle Cue.",
                nameof(cue));
        }

        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(
                nameof(style),
                style,
                "Unknown subtitle file cue style.");
        }

        SubtitleWriterContract.ValidateText(text);
        Cue = cue;
        Text = text;
        Style = style;
    }

    public Cue Cue { get; }

    public string Text { get; }

    public SubtitleFileCueStyle Style { get; }
}

public sealed class SubtitleExportFile
{
    private readonly byte[] _utf8Bytes;

    internal SubtitleExportFile(
        SubtitleFileFormat format,
        int cueCount,
        string timestampRuleVersion,
        byte[] utf8Bytes)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (cueCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cueCount));
        }

        ArgumentNullException.ThrowIfNull(timestampRuleVersion);
        ArgumentNullException.ThrowIfNull(utf8Bytes);

        Format = format;
        CueCount = cueCount;
        TimestampRuleVersion = timestampRuleVersion;
        _utf8Bytes = [.. utf8Bytes];
        ByteSize = _utf8Bytes.LongLength;
        ContentDigest = ContentDigest.Compute(_utf8Bytes);
    }

    public SubtitleFileFormat Format { get; }

    public int CueCount { get; }

    public string TimestampRuleVersion { get; }

    public long ByteSize { get; }

    public ContentDigest ContentDigest { get; }

    public byte[] GetUtf8Bytes() => [.. _utf8Bytes];
}

public enum SubtitleWriterFailureReason
{
    InvalidCue,
    DuplicateCue,
    InvalidText,
    QuantizedRangeCollapsed,
    TimestampOutOfRange,
    SerializationFailed,
}

public sealed class SubtitleWriterException : Exception
{
    internal SubtitleWriterException(SubtitleWriterFailureReason reason)
        : base(GetStableMessage(reason))
    {
        Reason = reason;
    }

    public SubtitleWriterFailureReason Reason { get; }

    private static string GetStableMessage(SubtitleWriterFailureReason reason) =>
        reason switch
        {
            SubtitleWriterFailureReason.InvalidCue =>
                "The subtitle file contains an invalid cue.",
            SubtitleWriterFailureReason.DuplicateCue =>
                "The subtitle file contains a duplicate cue identity.",
            SubtitleWriterFailureReason.InvalidText =>
                "The subtitle text could not be encoded safely.",
            SubtitleWriterFailureReason.QuantizedRangeCollapsed =>
                "A subtitle cue has no positive duration at the target timestamp precision.",
            SubtitleWriterFailureReason.TimestampOutOfRange =>
                "A subtitle timestamp exceeds the supported exact range.",
            SubtitleWriterFailureReason.SerializationFailed =>
                "The subtitle file could not be serialized.",
            _ => "The subtitle file could not be written.",
        };
}

public sealed class AssColor : IEquatable<AssColor>
{
    public AssColor(byte alpha, byte red, byte green, byte blue)
    {
        Alpha = alpha;
        Red = red;
        Green = green;
        Blue = blue;
    }

    public byte Alpha { get; }

    public byte Red { get; }

    public byte Green { get; }

    public byte Blue { get; }

    public bool Equals(AssColor? other) =>
        other is not null
        && Alpha == other.Alpha
        && Red == other.Red
        && Green == other.Green
        && Blue == other.Blue;

    public override bool Equals(object? obj) =>
        obj is AssColor other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Alpha, Red, Green, Blue);

    internal string ToAssValue() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"&H{Alpha:X2}{Blue:X2}{Green:X2}{Red:X2}");
}

public sealed class AssMargins : IEquatable<AssMargins>
{
    public AssMargins(int left, int right, int vertical)
    {
        if (left < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(left));
        }

        if (right < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(right));
        }

        if (vertical < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vertical));
        }

        Left = left;
        Right = right;
        Vertical = vertical;
    }

    public int Left { get; }

    public int Right { get; }

    public int Vertical { get; }

    public bool Equals(AssMargins? other) =>
        other is not null
        && Left == other.Left
        && Right == other.Right
        && Vertical == other.Vertical;

    public override bool Equals(object? obj) =>
        obj is AssMargins other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Left, Right, Vertical);
}

public enum AssBorderStyle
{
    OutlineAndShadow = 1,
    OpaqueBox = 3,
}

public enum AssAlignment
{
    BottomLeft = 1,
    BottomCenter = 2,
    BottomRight = 3,
    MiddleLeft = 4,
    MiddleCenter = 5,
    MiddleRight = 6,
    TopLeft = 7,
    TopCenter = 8,
    TopRight = 9,
}

public sealed class AssStyleDefinition
{
    public AssStyleDefinition(
        SubtitleFileCueStyle cueStyle,
        string fontName,
        int fontSize,
        AssColor primaryColor,
        AssColor secondaryColor,
        AssColor outlineColor,
        AssColor backgroundColor,
        bool bold,
        bool italic,
        bool underline,
        bool strikeOut,
        int scaleX,
        int scaleY,
        int spacing,
        int angle,
        AssBorderStyle borderStyle,
        int outlineWidth,
        int shadowDepth,
        AssAlignment alignment,
        AssMargins margins,
        int encoding)
    {
        if (!Enum.IsDefined(cueStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(cueStyle));
        }

        ValidateFontName(fontName);
        if (fontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }

        ArgumentNullException.ThrowIfNull(primaryColor);
        ArgumentNullException.ThrowIfNull(secondaryColor);
        ArgumentNullException.ThrowIfNull(outlineColor);
        ArgumentNullException.ThrowIfNull(backgroundColor);
        if (scaleX <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX));
        }

        if (scaleY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleY));
        }

        if (!Enum.IsDefined(borderStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(borderStyle));
        }

        if (outlineWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outlineWidth));
        }

        if (shadowDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shadowDepth));
        }

        if (!Enum.IsDefined(alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment));
        }

        ArgumentNullException.ThrowIfNull(margins);
        if (encoding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(encoding));
        }

        CueStyle = cueStyle;
        FontName = fontName;
        FontSize = fontSize;
        PrimaryColor = primaryColor;
        SecondaryColor = secondaryColor;
        OutlineColor = outlineColor;
        BackgroundColor = backgroundColor;
        Bold = bold;
        Italic = italic;
        Underline = underline;
        StrikeOut = strikeOut;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Spacing = spacing;
        Angle = angle;
        BorderStyle = borderStyle;
        OutlineWidth = outlineWidth;
        ShadowDepth = shadowDepth;
        Alignment = alignment;
        Margins = margins;
        Encoding = encoding;
    }

    public SubtitleFileCueStyle CueStyle { get; }

    public string Name => CueStyle.ToString();

    public string FontName { get; }

    public int FontSize { get; }

    public AssColor PrimaryColor { get; }

    public AssColor SecondaryColor { get; }

    public AssColor OutlineColor { get; }

    public AssColor BackgroundColor { get; }

    public bool Bold { get; }

    public bool Italic { get; }

    public bool Underline { get; }

    public bool StrikeOut { get; }

    public int ScaleX { get; }

    public int ScaleY { get; }

    public int Spacing { get; }

    public int Angle { get; }

    public AssBorderStyle BorderStyle { get; }

    public int OutlineWidth { get; }

    public int ShadowDepth { get; }

    public AssAlignment Alignment { get; }

    public AssMargins Margins { get; }

    public int Encoding { get; }

    private static void ValidateFontName(string fontName)
    {
        ArgumentNullException.ThrowIfNull(fontName);
        if (string.IsNullOrWhiteSpace(fontName)
            || !string.Equals(fontName, fontName.Trim(), StringComparison.Ordinal)
            || fontName.Contains(',')
            || fontName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An ASS font name must be a single nonblank field.",
                nameof(fontName));
        }

        try
        {
            _ = SubtitleWriterContract.StrictUtf8.GetByteCount(fontName);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "An ASS font name must be valid Unicode.",
                nameof(fontName),
                exception);
        }
    }
}

public sealed class AssSubtitleProfile
{
    public AssSubtitleProfile(
        int playResolutionWidth,
        int playResolutionHeight,
        IReadOnlyList<AssStyleDefinition> styles)
    {
        if (playResolutionWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playResolutionWidth));
        }

        if (playResolutionHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playResolutionHeight));
        }

        ArgumentNullException.ThrowIfNull(styles);
        SubtitleFileCueStyle[] knownStyles = Enum.GetValues<SubtitleFileCueStyle>();
        if (styles.Count != knownStyles.Length)
        {
            throw new ArgumentException(
                "An ASS profile must define every subtitle cue style exactly once in canonical order.",
                nameof(styles));
        }

        var copied = new AssStyleDefinition[styles.Count];
        for (int index = 0; index < copied.Length; index++)
        {
            AssStyleDefinition style = styles[index]
                ?? throw new ArgumentException(
                    "ASS styles must not contain null values.",
                    nameof(styles));
            if (style.CueStyle != knownStyles[index])
            {
                throw new ArgumentException(
                    "An ASS profile must define every subtitle cue style exactly once in canonical order.",
                    nameof(styles));
            }

            copied[index] = style;
        }

        PlayResolutionWidth = playResolutionWidth;
        PlayResolutionHeight = playResolutionHeight;
        Styles = Array.AsReadOnly(copied);
    }

    public int PlayResolutionWidth { get; }

    public int PlayResolutionHeight { get; }

    public IReadOnlyList<AssStyleDefinition> Styles { get; }
}

internal sealed record PreparedSubtitleCue(
    SubtitleFileCue Source,
    long StartUnits,
    long EndUnits);

internal static class SubtitleWriterContract
{
    internal const string TimestampRuleVersion =
        "r013.nearest-to-even-target-units.v1";

    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static PreparedSubtitleCue[] Prepare(
        IReadOnlyList<SubtitleFileCue> cues,
        long timestampRate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cues);
        cancellationToken.ThrowIfCancellationRequested();
        var copied = new SubtitleFileCue[cues.Count];
        var ids = new HashSet<EntityId<CueIdTag>>();
        for (int index = 0; index < copied.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubtitleFileCue? cue = cues[index];
            if (cue is null)
            {
                throw Failure(SubtitleWriterFailureReason.InvalidCue);
            }

            if (!ids.Add(cue.Cue.Id))
            {
                throw Failure(SubtitleWriterFailureReason.DuplicateCue);
            }

            copied[index] = cue;
        }

        SubtitleFileCue[] ordered = copied
            .OrderBy(static cue => cue.Cue.Range.Start)
            .ThenBy(static cue => cue.Cue.Range.End)
            .ThenBy(
                static cue => cue.Cue.Id.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        var prepared = new PreparedSubtitleCue[ordered.Length];
        for (int index = 0; index < prepared.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubtitleFileCue cue = ordered[index];
            long startUnits;
            long endUnits;
            try
            {
                startUnits = QuantizeUnits(cue.Cue.Range.Start, timestampRate);
                endUnits = QuantizeUnits(cue.Cue.Range.End, timestampRate);
            }
            catch (OverflowException)
            {
                throw Failure(SubtitleWriterFailureReason.TimestampOutOfRange);
            }

            if (startUnits < 0)
            {
                throw Failure(SubtitleWriterFailureReason.InvalidCue);
            }

            if (endUnits <= startUnits)
            {
                throw Failure(
                    SubtitleWriterFailureReason.QuantizedRangeCollapsed);
            }

            prepared[index] = new PreparedSubtitleCue(
                cue,
                startUnits,
                endUnits);
        }

        return prepared;
    }

    internal static SubtitleExportFile CreateFile(
        SubtitleFileFormat format,
        int cueCount,
        StringBuilder document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            byte[] bytes = StrictUtf8.GetBytes(document.ToString());
            cancellationToken.ThrowIfCancellationRequested();
            return new SubtitleExportFile(
                format,
                cueCount,
                TimestampRuleVersion,
                bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EncoderFallbackException)
        {
            throw Failure(SubtitleWriterFailureReason.SerializationFailed);
        }
    }

    internal static string FormatTimestamp(
        long units,
        long rate,
        char fractionalSeparator,
        int fractionalDigits,
        bool padHours)
    {
        long totalSeconds = units / rate;
        long fraction = units % rate;
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;
        string hourText = padHours
            ? hours.ToString("D2", CultureInfo.InvariantCulture)
            : hours.ToString(CultureInfo.InvariantCulture);
        return string.Concat(
            hourText,
            ":",
            minutes.ToString("D2", CultureInfo.InvariantCulture),
            ":",
            seconds.ToString("D2", CultureInfo.InvariantCulture),
            fractionalSeparator,
            fraction.ToString(
                string.Concat("D", fractionalDigits.ToString(CultureInfo.InvariantCulture)),
                CultureInfo.InvariantCulture));
    }

    internal static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    internal static string EscapeHtmlText(string text) =>
        NormalizeLineEndings(text)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    internal static string EscapeAssText(string text) =>
        NormalizeLineEndings(text)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace("\n", "\\N", StringComparison.Ordinal);

    internal static void ValidateBlockText(string text)
    {
        if (NormalizeLineEndings(text)
            .Split('\n')
            .Any(string.IsNullOrWhiteSpace))
        {
            throw Failure(SubtitleWriterFailureReason.InvalidText);
        }
    }

    internal static string StyleClass(SubtitleFileCueStyle style) =>
        style switch
        {
            SubtitleFileCueStyle.Normal => "normal",
            SubtitleFileCueStyle.Emphasis => "emphasis",
            SubtitleFileCueStyle.Caution => "caution",
            SubtitleFileCueStyle.Quote => "quote",
            _ => throw new InvalidOperationException("Unknown subtitle cue style."),
        };

    internal static void ValidateText(string text)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(text);
            if (string.IsNullOrWhiteSpace(text)
                || !string.Equals(text, text.Trim(), StringComparison.Ordinal)
                || text.Any(static character =>
                    char.IsControl(character)
                    && character is not ('\t' or '\r' or '\n')))
            {
                throw new ArgumentException("Invalid subtitle text.");
            }

            _ = StrictUtf8.GetByteCount(text);
        }
        catch (ArgumentException)
        {
            throw Failure(SubtitleWriterFailureReason.InvalidText);
        }
    }

    private static long QuantizeUnits(RationalTime value, long targetRate)
    {
        RationalTime quantized = value.Quantize(
            targetRate,
            RationalTimeRoundingMode.NearestToEven);
        BigInteger scaled = (BigInteger)quantized.Value * targetRate;
        BigInteger units = BigInteger.DivRem(
            scaled,
            quantized.Rate,
            out BigInteger remainder);
        if (!remainder.IsZero || units < long.MinValue || units > long.MaxValue)
        {
            throw new OverflowException();
        }

        return (long)units;
    }

    internal static SubtitleWriterException Failure(
        SubtitleWriterFailureReason reason) =>
        new(reason);
}