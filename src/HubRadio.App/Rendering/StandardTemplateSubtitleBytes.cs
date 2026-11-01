using System.Text;
using System.Text.RegularExpressions;

namespace HubRadio.App.Rendering;

/// <summary>
/// FR-136 / FR-138 / FR-141: the stored subtitle file of a template Job differs from the plain standard file only in the
/// override tags of the Dialogue line (the template's color, typeface and motion, and the place inside the margins). This check
/// accepts exactly that: every other byte (header, styles, style name, times inside the plain cue's) must equal the plain file,
/// the override block may hold only the closed set of template tags with a clip inside the output frame, and the cue's own text
/// must be shown in full (as one line or as its consecutive words) — only a short name label may be added.
/// </summary>
internal static partial class StandardTemplateSubtitleBytes
{
    private const string Marker = ",,0,0,0,,";

    [GeneratedRegex(@"^(?:\\(?:an5|q2|pos\(\d+(?:\.\d+)?,\d+(?:\.\d+)?\)|clip\(\d+,\d+,\d+,\d+\)|fn[^\\{}]{1,64}|[134]c&H[0-9A-Fa-f]{6}&|fad\(\d+,\d+\)|fscx\d+|fscy\d+|frz-?\d+|t\(\d+,\d+,(?:\\(?:fscx\d+|fscy\d+|frz-?\d+))+\)))+$")]
    private static partial Regex Overrides();

    [GeneratedRegex(@"\\clip\((\d+),(\d+),(\d+),(\d+)\)")]
    private static partial Regex Clip();

    [GeneratedRegex(@"^\\1c&H[0-9A-Fa-f]{6}&(?:\\t\(-?\d+,-?\d+,(?:\\(?:1c&H[0-9A-Fa-f]{6}&|fscx\d+|fscy\d+))+\))*$")]
    private static partial Regex InlineOverrides();

    internal static bool Matches(byte[] actual, byte[] expected, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        if (actual.AsSpan().SequenceEqual(expected))
        {
            return true;
        }

        string[] left = new UTF8Encoding(false, true).GetString(actual).Split('\n');
        string[] right = new UTF8Encoding(false, true).GetString(expected).Split('\n');
        static bool IsDialogue(string line) => line.StartsWith("Dialogue: ", StringComparison.Ordinal);
        int firstL = Array.FindIndex(left, IsDialogue), firstR = Array.FindIndex(right, IsDialogue);
        int lastL = Array.FindLastIndex(left, IsDialogue), lastR = Array.FindLastIndex(right, IsDialogue);
        if (firstL < 0 || firstR < 0 || firstR != lastR)
        {
            return false;
        }

        // Everything before and after the Dialogue lines (header, styles, trailer) must equal the plain file.
        if (firstL != firstR || left.Length - lastL != right.Length - lastR
            || !left.Take(firstL).SequenceEqual(right.Take(firstR)) || !left.Skip(lastL + 1).SequenceEqual(right.Skip(lastR + 1)))
        {
            return false;
        }

        if (!TryParse(right[firstR], out string[] plainHead, out string plainTail, out _))
        {
            return false;
        }

        int plainClose = plainTail.IndexOf('}', StringComparison.Ordinal);
        long plainStart = 0, plainEnd = 0;
        if (!plainTail.StartsWith('{') || plainClose < 0
            || !TryCentiseconds(plainHead[1], out plainStart) || !TryCentiseconds(plainHead[2], out plainEnd))
        {
            return false;
        }

        string plainText = plainTail[(plainClose + 1)..];
        var texts = new List<(string Text, bool Label)>();
        for (int index = firstL; index <= lastL; index++)
        {
            // The template may replace the one Dialogue line by several: the caption words, the dialogue line, a name label.
            if (!IsDialogue(left[index]) || !TryParse(left[index], out string[] head, out string tail, out _)
                || head[3] != plainHead[3] || head[0] != plainHead[0]
                || !TryCentiseconds(head[1], out long start) || !TryCentiseconds(head[2], out long end)
                || start < plainStart || end > plainEnd || start >= end)
            {
                return false;
            }

            int close = tail.IndexOf('}', StringComparison.Ordinal);
            if (!tail.StartsWith('{') || close < 0)
            {
                return false;
            }

            string block = tail[1..close];
            Match clip = Clip().Match(block);
            if (!Overrides().IsMatch(block) || !clip.Success
                || int.Parse(clip.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) > width
                || int.Parse(clip.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) > height)
            {
                return false;
            }

            // The text may carry inline override blocks (the highlight of the spoken word); only the closed set is accepted.
            string rest = tail[(close + 1)..];
            var visible = new StringBuilder();
            while (rest.Length > 0)
            {
                int open = rest.IndexOf('{', StringComparison.Ordinal);
                if (open < 0)
                {
                    _ = visible.Append(rest);
                    break;
                }

                int end2 = rest.IndexOf('}', open + 1);
                if (end2 < 0 || !InlineOverrides().IsMatch(rest[(open + 1)..end2]))
                {
                    return false;
                }

                _ = visible.Append(rest, 0, open);
                rest = rest[(end2 + 1)..];
            }

            texts.Add((visible.ToString(), !plainText.Contains(visible.ToString(), StringComparison.Ordinal)));
        }

        // The cue's own text must be shown in full, once as one line or as the consecutive words of it; nothing else may be
        // shown unless it is a name label (a short line of the registered character name, shown with the picture).
        bool whole = texts.Any(item => !item.Label && item.Text == plainText)
            || string.Concat(texts.Where(static item => !item.Label).Select(static item => item.Text)) == plainText;
        return whole && texts.Where(static item => item.Label).All(static item => item.Text.Length is > 0 and <= 64);
    }

    private static bool TryParse(string line, out string[] head, out string tail, out string before)
    {
        head = [];
        tail = string.Empty;
        before = string.Empty;
        int marker = line.IndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        before = line[..marker];
        head = before.Split(',');
        tail = line[(marker + Marker.Length)..];
        return head.Length == 4;
    }

    private static bool TryCentiseconds(string stamp, out long value)
    {
        value = 0;
        string[] parts = stamp.Split(':', '.');
        if (parts.Length != 4 || !long.TryParse(parts[0], out long h) || !long.TryParse(parts[1], out long m)
            || !long.TryParse(parts[2], out long s) || !long.TryParse(parts[3], out long c))
        {
            return false;
        }

        value = (((h * 60) + m) * 60 + s) * 100 + c;
        return true;
    }
}
