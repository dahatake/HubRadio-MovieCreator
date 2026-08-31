using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Layout;
using HubRadio.Core.Time;
using HubRadio.Infrastructure.Subtitles;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class SubtitleWriterTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    [TestMethod]
    public void SameCueSnapshotsProduceClosedUtf8VttSrtAndAssDocuments()
    {
        SubtitleFileCue later = FileCue(
            2,
            Range(2, 2),
            "注意 <script>&\r\n二行目",
            SubtitleFileCueStyle.Caution);
        SubtitleFileCue earlier = FileCue(
            1,
            Range(0, 3, rate: 2),
            "引用 {value} \\ path,comma",
            SubtitleFileCueStyle.Quote);
        IReadOnlyList<SubtitleFileCue> sameSnapshots = [later, earlier];

        SubtitleExportFile vtt = WebVttSubtitleWriter.Write(sameSnapshots);
        SubtitleExportFile srt = SubRipSubtitleWriter.Write(sameSnapshots);
        SubtitleExportFile ass = AssSubtitleWriter.Write(
            sameSnapshots,
            CreateAssProfile());

        AssertFile(vtt, SubtitleFileFormat.WebVtt, expectedCueCount: 2);
        AssertFile(srt, SubtitleFileFormat.SubRip, expectedCueCount: 2);
        AssertFile(
            ass,
            SubtitleFileFormat.AdvancedSubStationAlpha,
            expectedCueCount: 2);
        string vttText = Decode(vtt);
        string srtText = Decode(srt);
        string assText = Decode(ass);
        StringAssert.StartsWith(vttText, "WEBVTT\n\n");
        StringAssert.Contains(vttText, "&lt;script&gt;&amp;");
        StringAssert.Contains(vttText, "<c.caution>");
        StringAssert.StartsWith(srtText, "1\n00:00:00,000 --> 00:00:01,500\n");
        StringAssert.Contains(srtText, "&lt;script&gt;&amp;");
        StringAssert.Contains(assText, "[Script Info]\nScriptType: v4.00+\n");
        StringAssert.Contains(assText, "[V4+ Styles]\n");
        StringAssert.Contains(assText, "[Events]\n");
        StringAssert.Contains(
            assText,
            "Dialogue: 0,0:00:00.00,0:00:01.50,Quote,,0,0,0,,引用 \\{value\\} \\\\ path,comma");
        StringAssert.Contains(assText, "注意 <script>&\\N二行目");

        Assert.IsTrue(vttText.IndexOf(CueId(1).ToString(), StringComparison.Ordinal)
            < vttText.IndexOf(CueId(2).ToString(), StringComparison.Ordinal));
        Assert.AreNotEqual(vtt.ContentDigest, srt.ContentDigest);
        Assert.AreNotEqual(vtt.ContentDigest, ass.ContentDigest);
    }

    [TestMethod]
    public void WebVttRoundTripPreservesQuantizedTimeTextStyleAndStableIdentity()
    {
        SubtitleFileCue[] cues =
        [
            FileCue(
                1,
                new TimeRange(new RationalTime(1, 2000), new RationalTime(1, 1000)),
                "A < B & C > D",
                SubtitleFileCueStyle.Emphasis),
            FileCue(
                2,
                Range(2, 1),
                "通常",
                SubtitleFileCueStyle.Normal),
        ];

        SubtitleExportFile file = WebVttSubtitleWriter.Write(cues);
        IReadOnlyList<ParsedCue> parsed = ParseVtt(Decode(file));

        Assert.HasCount(2, parsed);
        Assert.AreEqual(CueId(1), parsed[0].Id);
        Assert.AreEqual(0L, parsed[0].StartUnits);
        Assert.AreEqual(2L, parsed[0].EndUnits);
        Assert.AreEqual(1000L, parsed[0].Rate);
        Assert.AreEqual("A < B & C > D", parsed[0].Text);
        Assert.AreEqual(SubtitleFileCueStyle.Emphasis, parsed[0].Style);
        Assert.AreEqual(CueId(2), parsed[1].Id);
    }

    [TestMethod]
    public void SubRipRoundTripPreservesQuantizedTimeAndLiteralTextWithoutStyleClaims()
    {
        SubtitleFileCue[] cues =
        [
            FileCue(
                1,
                new TimeRange(
                    new RationalTime(5, 2000),
                    new RationalTime(3, 2000)),
                "<b>literal</b> & text\nnext",
                SubtitleFileCueStyle.Emphasis),
        ];

        SubtitleExportFile file = SubRipSubtitleWriter.Write(cues);
        IReadOnlyList<ParsedCue> parsed = ParseSrt(Decode(file));

        Assert.HasCount(1, parsed);
        Assert.AreEqual(2L, parsed[0].StartUnits);
        Assert.AreEqual(4L, parsed[0].EndUnits);
        Assert.AreEqual(1000L, parsed[0].Rate);
        Assert.AreEqual("<b>literal</b> & text\nnext", parsed[0].Text);
        Assert.IsNull(parsed[0].Style);
        Assert.IsFalse(Decode(file).Contains("<b>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AssRoundTripUsesCallerProfileAndPreservesStyleAndEscapedText()
    {
        AssSubtitleProfile profile = CreateAssProfile();
        SubtitleFileCue[] cues =
        [
            FileCue(
                1,
                Range(1, 2),
                "{literal} \\N not-a-break\nactual-break",
                SubtitleFileCueStyle.Quote),
        ];

        SubtitleExportFile file = AssSubtitleWriter.Write(cues, profile);
        string text = Decode(file);
        IReadOnlyList<ParsedCue> parsed = ParseAss(text);

        StringAssert.Contains(text, "PlayResX: 1280\nPlayResY: 720\n");
        foreach (AssStyleDefinition style in profile.Styles)
        {
            StringAssert.Contains(text, $"Style: {style.Name},");
        }

        Assert.HasCount(1, parsed);
        Assert.AreEqual(100L, parsed[0].StartUnits);
        Assert.AreEqual(300L, parsed[0].EndUnits);
        Assert.AreEqual(100L, parsed[0].Rate);
        Assert.AreEqual(SubtitleFileCueStyle.Quote, parsed[0].Style);
        Assert.AreEqual(
            "{literal} \\N not-a-break\nactual-break",
            parsed[0].Text);
    }

    [TestMethod]
    public void NearestToEvenQuantizationIsExplicitAndCollapsedCueFailsClosed()
    {
        SubtitleFileCue tieToEven = FileCue(
            1,
            new TimeRange(
                new RationalTime(1, 2000),
                new RationalTime(1, 1000)),
            "tie",
            SubtitleFileCueStyle.Normal);
        SubtitleFileCue collapsed = FileCue(
            2,
            new TimeRange(
                new RationalTime(1, 10000),
                new RationalTime(1, 10000)),
            "collapsed",
            SubtitleFileCueStyle.Normal);

        SubtitleExportFile vtt = WebVttSubtitleWriter.Write([tieToEven]);
        StringAssert.Contains(
            Decode(vtt),
            "00:00:00.000 --> 00:00:00.002");
        Assert.AreEqual(
            SubtitleWriterContract.TimestampRuleVersion,
            vtt.TimestampRuleVersion);
        SubtitleWriterException failure = AssertFailure(
            () => WebVttSubtitleWriter.Write([collapsed]),
            SubtitleWriterFailureReason.QuantizedRangeCollapsed);
        Assert.IsFalse(failure.ToString().Contains(
            "collapsed",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void OverlappingAndEqualTimeCuesUseExactDeterministicOrdering()
    {
        SubtitleFileCue third = FileCue(3, Range(0, 3), "third", SubtitleFileCueStyle.Normal);
        SubtitleFileCue first = FileCue(1, Range(0, 2), "first", SubtitleFileCueStyle.Normal);
        SubtitleFileCue second = FileCue(2, Range(0, 2), "second", SubtitleFileCueStyle.Normal);

        SubtitleExportFile one = WebVttSubtitleWriter.Write([third, second, first]);
        SubtitleExportFile two = WebVttSubtitleWriter.Write([first, third, second]);
        IReadOnlyList<ParsedCue> parsed = ParseVtt(Decode(one));

        Assert.AreEqual(one.ContentDigest, two.ContentDigest);
        CollectionAssert.AreEqual(
            new[] { CueId(1), CueId(2), CueId(3) },
            parsed.Select(static cue => cue.Id).ToArray());
        Assert.AreEqual(0L, parsed[0].StartUnits);
        Assert.AreEqual(2000L, parsed[0].EndUnits);
        Assert.AreEqual(3000L, parsed[2].EndUnits);
    }

    [TestMethod]
    public void DuplicateCueIdentityAndNullItemsFailBeforeAnyOutputIsReturned()
    {
        SubtitleFileCue cue = FileCue(1, Range(0, 1), "one", SubtitleFileCueStyle.Normal);
        SubtitleFileCue duplicate = FileCue(1, Range(1, 1), "two", SubtitleFileCueStyle.Normal);

        AssertFailure(
            () => WebVttSubtitleWriter.Write([cue, duplicate]),
            SubtitleWriterFailureReason.DuplicateCue);
        AssertFailure(
            () => SubRipSubtitleWriter.Write([cue, null!]),
            SubtitleWriterFailureReason.InvalidCue);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            WebVttSubtitleWriter.Write(null!));
    }

    [TestMethod]
    public void EmptyCueCollectionsProduceDeterministicEmptyDocuments()
    {
        SubtitleExportFile vtt = WebVttSubtitleWriter.Write([]);
        SubtitleExportFile srt = SubRipSubtitleWriter.Write([]);
        SubtitleExportFile ass = AssSubtitleWriter.Write([], CreateAssProfile());

        Assert.AreEqual("WEBVTT\n\n", Decode(vtt));
        Assert.AreEqual(string.Empty, Decode(srt));
        StringAssert.Contains(Decode(ass), "[Events]\n");
        Assert.AreEqual(0, vtt.CueCount);
        Assert.AreEqual(0, srt.CueCount);
        Assert.AreEqual(0, ass.CueCount);
        Assert.AreEqual(ContentDigest.Compute([]), srt.ContentDigest);
    }

    [TestMethod]
    public void WebVttAndSubRipRejectBlankPayloadLinesInsteadOfSplittingCueBlocks()
    {
        SubtitleFileCue emptyLine = FileCue(
            1,
            Range(0, 1),
            "first\n\nsecond",
            SubtitleFileCueStyle.Normal);
        SubtitleFileCue whitespaceLine = FileCue(
            2,
            Range(1, 1),
            "first\n \t\nsecond",
            SubtitleFileCueStyle.Normal);

        foreach (SubtitleFileCue cue in new[] { emptyLine, whitespaceLine })
        {
            AssertFailure(
                () => WebVttSubtitleWriter.Write([cue]),
                SubtitleWriterFailureReason.InvalidText);
            AssertFailure(
                () => SubRipSubtitleWriter.Write([cue]),
                SubtitleWriterFailureReason.InvalidText);
        }

        SubtitleExportFile ass = AssSubtitleWriter.Write(
            [emptyLine],
            CreateAssProfile());
        Assert.AreEqual(
            "first\n\nsecond",
            ParseAss(Decode(ass)).Single().Text);
    }

    [TestMethod]
    public void FileCueRejectsNonSubtitleCueInvalidStyleAndUnsafeText()
    {
        var graphic = new Cue(
            CueId(1),
            CueKind.Graphic,
            Range(0, 1),
            "graphic");

        AssertParamName<ArgumentException>(
            "cue",
            () => new SubtitleFileCue(
                graphic,
                "text",
                SubtitleFileCueStyle.Normal));
        AssertParamName<ArgumentOutOfRangeException>(
            "style",
            () => new SubtitleFileCue(
                SubtitleCue(2, Range(0, 1)),
                "text",
                (SubtitleFileCueStyle)int.MaxValue));
        foreach (string invalid in new[]
        {
            string.Empty,
            " ",
            " leading",
            "trailing ",
            "nul\0text",
            "escape\u001btext",
            "bad\uD800",
        })
        {
            SubtitleWriterException exception = AssertFailure(
                () => WebVttSubtitleWriter.Write(
                [
                    new SubtitleFileCue(
                        SubtitleCue(3, Range(0, 1)),
                        invalid,
                        SubtitleFileCueStyle.Normal),
                ]),
                SubtitleWriterFailureReason.InvalidText);
            Assert.IsFalse(exception.ToString().Contains(
                "leading",
                StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ExtremeTimestampUsesStableOverflowFailureWithoutContentDisclosure()
    {
        SubtitleFileCue cue = FileCue(
            1,
            new TimeRange(
                new RationalTime(long.MaxValue - 1, 1),
                new RationalTime(1, 1)),
            "private-overflow-text",
            SubtitleFileCueStyle.Normal);

        SubtitleWriterException exception = AssertFailure(
            () => WebVttSubtitleWriter.Write([cue]),
            SubtitleWriterFailureReason.TimestampOutOfRange);

        Assert.IsFalse(exception.ToString().Contains(
            "private-overflow-text",
            StringComparison.Ordinal));
        Assert.IsNull(exception.InnerException);
    }

    [TestMethod]
    public void AssProfileRequiresEveryStyleInCanonicalOrderAndDefensivelyCopies()
    {
        List<AssStyleDefinition> styles = CreateAssStyles().ToList();
        var profile = new AssSubtitleProfile(1280, 720, styles);
        styles.Clear();

        Assert.AreEqual(1280, profile.PlayResolutionWidth);
        Assert.AreEqual(720, profile.PlayResolutionHeight);
        Assert.HasCount(4, profile.Styles);
        CollectionAssert.AreEqual(
            Enum.GetValues<SubtitleFileCueStyle>(),
            profile.Styles.Select(static style => style.CueStyle).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<AssStyleDefinition>)profile.Styles).Clear());
        AssertParamName<ArgumentException>(
            "styles",
            () => new AssSubtitleProfile(1280, 720, CreateAssStyles().Skip(1).ToArray()));
        AssertParamName<ArgumentException>(
            "styles",
            () => new AssSubtitleProfile(
                1280,
                720,
                [
                    CreateAssStyle(SubtitleFileCueStyle.Normal),
                    CreateAssStyle(SubtitleFileCueStyle.Emphasis),
                    CreateAssStyle(SubtitleFileCueStyle.Caution),
                    CreateAssStyle(SubtitleFileCueStyle.Caution),
                ]));
        AssertParamName<ArgumentOutOfRangeException>(
            "playResolutionWidth",
            () => new AssSubtitleProfile(0, 720, CreateAssStyles()));
    }

    [TestMethod]
    public void AssStyleFieldsAreTypedAndRejectInvalidFormatValues()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "fontSize",
            () => CreateAssStyle(SubtitleFileCueStyle.Normal, fontSize: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "scaleX",
            () => CreateAssStyle(SubtitleFileCueStyle.Normal, scaleX: 0));
        AssertParamName<ArgumentOutOfRangeException>(
            "outlineWidth",
            () => CreateAssStyle(SubtitleFileCueStyle.Normal, outlineWidth: -1));
        AssertParamName<ArgumentOutOfRangeException>(
            "encoding",
            () => CreateAssStyle(SubtitleFileCueStyle.Normal, encoding: -1));
        AssertParamName<ArgumentException>(
            "fontName",
            () => new AssStyleDefinition(
                SubtitleFileCueStyle.Normal,
                "bad,font",
                42,
                new AssColor(255, 255, 255, 255),
                new AssColor(255, 255, 255, 255),
                new AssColor(255, 0, 0, 0),
                new AssColor(128, 0, 0, 0),
                bold: false,
                italic: false,
                underline: false,
                strikeOut: false,
                scaleX: 100,
                scaleY: 100,
                spacing: 0,
                angle: 0,
                AssBorderStyle.OutlineAndShadow,
                outlineWidth: 2,
                shadowDepth: 0,
                AssAlignment.BottomCenter,
                new AssMargins(10, 10, 20),
                encoding: 1));
        AssertParamName<ArgumentException>(
            "fontName",
            () => new AssStyleDefinition(
                SubtitleFileCueStyle.Normal,
                "bad\u001bfont",
                42,
                new AssColor(255, 255, 255, 255),
                new AssColor(255, 255, 255, 255),
                new AssColor(255, 0, 0, 0),
                new AssColor(128, 0, 0, 0),
                bold: false,
                italic: false,
                underline: false,
                strikeOut: false,
                scaleX: 100,
                scaleY: 100,
                spacing: 0,
                angle: 0,
                AssBorderStyle.OutlineAndShadow,
                outlineWidth: 2,
                shadowDepth: 0,
                AssAlignment.BottomCenter,
                new AssMargins(10, 10, 20),
                encoding: 1));
        AssertParamName<ArgumentOutOfRangeException>(
            "left",
            () => new AssMargins(-1, 0, 0));
    }

    [TestMethod]
    public void OutputBytesAreDefensiveAndCancellationReturnsNoPartialFile()
    {
        SubtitleFileCue cue = FileCue(1, Range(0, 1), "text", SubtitleFileCueStyle.Normal);
        SubtitleExportFile file = WebVttSubtitleWriter.Write([cue]);
        byte[] first = file.GetUtf8Bytes();
        byte original = first[0];
        first[0] = 0;

        Assert.AreEqual(original, file.GetUtf8Bytes()[0]);
        Assert.AreEqual(file.ByteSize, file.GetUtf8Bytes().LongLength);
        Assert.AreEqual(
            ContentDigest.Compute(file.GetUtf8Bytes()),
            file.ContentDigest);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            WebVttSubtitleWriter.Write([cue], cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            AssSubtitleWriter.Write(
                [cue],
                CreateAssProfile(),
                cancellation.Token));
    }

    [TestMethod]
    public void PublicSurfaceHasThreeConcreteWritersAndNoPathOrFloatingTimeApi()
    {
        CollectionAssert.AreEqual(
            new[] { "WebVtt", "SubRip", "AdvancedSubStationAlpha" },
            Enum.GetNames<SubtitleFileFormat>());
        CollectionAssert.AreEqual(
            new[] { "Normal", "Emphasis", "Caution", "Quote" },
            Enum.GetNames<SubtitleFileCueStyle>());
        Type[] writers =
        [
            typeof(WebVttSubtitleWriter),
            typeof(SubRipSubtitleWriter),
            typeof(AssSubtitleWriter),
        ];
        foreach (Type writer in writers)
        {
            Assert.IsTrue(writer.IsAbstract && writer.IsSealed, writer.Name);
            Assert.AreEqual(
                1,
                writer.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Length,
                writer.Name);
        }

        MemberInfo[] members = typeof(SubtitleExportFile).Assembly.GetExportedTypes()
            .Where(static type => type.Namespace == "HubRadio.Infrastructure.Subtitles")
            .SelectMany(static type => type.GetMembers(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .ToArray();
        Assert.IsFalse(members.Any(static member =>
            member.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("FramesPerSecond", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(members.OfType<MethodBase>()
            .SelectMany(static method => method.GetParameters())
            .Any(static parameter => parameter.ParameterType == typeof(double)
                || parameter.ParameterType == typeof(float)));
    }

    private static SubtitleWriterException AssertFailure(
        Action action,
        SubtitleWriterFailureReason reason)
    {
        SubtitleWriterException exception =
            Assert.ThrowsExactly<SubtitleWriterException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static void AssertFile(
        SubtitleExportFile file,
        SubtitleFileFormat format,
        int expectedCueCount)
    {
        byte[] bytes = file.GetUtf8Bytes();
        Assert.AreEqual(format, file.Format);
        Assert.AreEqual(expectedCueCount, file.CueCount);
        Assert.AreEqual(bytes.LongLength, file.ByteSize);
        Assert.AreEqual(ContentDigest.Compute(bytes), file.ContentDigest);
        Assert.IsFalse(bytes.AsSpan().StartsWith(
            new byte[] { 0xEF, 0xBB, 0xBF }));
        string text = StrictUtf8.GetString(bytes);
        Assert.IsFalse(text.Contains('\r'));
        Assert.IsTrue(text.EndsWith('\n'));
    }

    private static SubtitleFileCue FileCue(
        int id,
        TimeRange range,
        string text,
        SubtitleFileCueStyle style) =>
        new(SubtitleCue(id, range), text, style);

    private static Cue SubtitleCue(int id, TimeRange range) =>
        new(CueId(id), CueKind.Subtitle, range, "subtitle");

    private static EntityId<CueIdTag> CueId(int value) =>
        EntityId<CueIdTag>.Parse($"74000001-0000-0000-0000-{value:x12}");

    private static TimeRange Range(long start, long duration, long rate = 1) =>
        new(new RationalTime(start, rate), new RationalTime(duration, rate));

    private static AssSubtitleProfile CreateAssProfile() =>
        new(1280, 720, CreateAssStyles());

    private static AssStyleDefinition[] CreateAssStyles() =>
        Enum.GetValues<SubtitleFileCueStyle>()
            .Select(static style => CreateAssStyle(style))
            .ToArray();

    private static AssStyleDefinition CreateAssStyle(
        SubtitleFileCueStyle style,
        int fontSize = 42,
        int scaleX = 100,
        int outlineWidth = 2,
        int encoding = 1) =>
        new(
            style,
            "Test Sans",
            fontSize,
            new AssColor(255, 255, 255, 255),
            new AssColor(255, 255, 255, 255),
            new AssColor(255, 0, 0, 0),
            new AssColor(128, 0, 0, 0),
            bold: style is SubtitleFileCueStyle.Emphasis
                or SubtitleFileCueStyle.Caution,
            italic: style == SubtitleFileCueStyle.Quote,
            underline: false,
            strikeOut: false,
            scaleX,
            scaleY: 100,
            spacing: 0,
            angle: 0,
            AssBorderStyle.OutlineAndShadow,
            outlineWidth,
            shadowDepth: 0,
            AssAlignment.BottomCenter,
            new AssMargins(10, 10, 20),
            encoding);

    private static string Decode(SubtitleExportFile file) =>
        StrictUtf8.GetString(file.GetUtf8Bytes());

    private static IReadOnlyList<ParsedCue> ParseVtt(string text)
    {
        string[] blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var parsed = new List<ParsedCue>();
        foreach (string block in blocks.Skip(1))
        {
            string[] lines = block.Split('\n');
            EntityId<CueIdTag> id = EntityId<CueIdTag>.Parse(lines[0]);
            (long start, long end) = ParseArrowTimes(lines[1], '.', rate: 1000);
            string payload = string.Join("\n", lines.Skip(2));
            int openEnd = payload.IndexOf('>');
            int closeStart = payload.LastIndexOf("</c>", StringComparison.Ordinal);
            string open = payload[..(openEnd + 1)];
            string className = open[3..^1];
            string escaped = payload[(openEnd + 1)..closeStart];
            parsed.Add(new ParsedCue(
                id,
                start,
                end,
                1000,
                WebUtility.HtmlDecode(escaped),
                Enum.Parse<SubtitleFileCueStyle>(className, ignoreCase: true)));
        }

        return parsed;
    }

    private static IReadOnlyList<ParsedCue> ParseSrt(string text)
    {
        string[] blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var parsed = new List<ParsedCue>();
        foreach (string block in blocks)
        {
            string[] lines = block.Split('\n');
            (long start, long end) = ParseArrowTimes(lines[1], ',', rate: 1000);
            parsed.Add(new ParsedCue(
                Id: null,
                start,
                end,
                1000,
                WebUtility.HtmlDecode(string.Join("\n", lines.Skip(2))),
                Style: null));
        }

        return parsed;
    }

    private static IReadOnlyList<ParsedCue> ParseAss(string text)
    {
        var parsed = new List<ParsedCue>();
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("Dialogue: ", StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line[10..].Split(',', 10);
            long start = ParseTimestamp(fields[1], '.', rate: 100);
            long end = ParseTimestamp(fields[2], '.', rate: 100);
            string decoded = DecodeAssText(fields[9]);
            parsed.Add(new ParsedCue(
                Id: null,
                start,
                end,
                100,
                decoded,
                Enum.Parse<SubtitleFileCueStyle>(fields[3], ignoreCase: false)));
        }

        return parsed;
    }

    private static (long Start, long End) ParseArrowTimes(
        string line,
        char fractionalSeparator,
        long rate)
    {
        string[] parts = line.Split(" --> ", StringSplitOptions.None);
        return (
            ParseTimestamp(parts[0], fractionalSeparator, rate),
            ParseTimestamp(parts[1], fractionalSeparator, rate));
    }

    private static long ParseTimestamp(
        string value,
        char fractionalSeparator,
        long rate)
    {
        string[] mainAndFraction = value.Split(fractionalSeparator);
        string[] clock = mainAndFraction[0].Split(':');
        long hours = long.Parse(clock[0], CultureInfo.InvariantCulture);
        long minutes = long.Parse(clock[1], CultureInfo.InvariantCulture);
        long seconds = long.Parse(clock[2], CultureInfo.InvariantCulture);
        long fraction = long.Parse(mainAndFraction[1], CultureInfo.InvariantCulture);
        return checked((((hours * 60) + minutes) * 60 + seconds) * rate + fraction);
    }

    private static string DecodeAssText(string value)
    {
        var result = new StringBuilder();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                _ = result.Append(current);
                continue;
            }

            char next = value[++index];
            _ = result.Append(next switch
            {
                'N' => '\n',
                '\\' => '\\',
                '{' => '{',
                '}' => '}',
                _ => next,
            });
        }

        return result.ToString();
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        TException exception = Assert.ThrowsExactly<TException>(action);
        Assert.AreEqual(expected, exception.ParamName);
    }

    private sealed record ParsedCue(
        EntityId<CueIdTag>? Id,
        long StartUnits,
        long EndUnits,
        long Rate,
        string Text,
        SubtitleFileCueStyle? Style);
}