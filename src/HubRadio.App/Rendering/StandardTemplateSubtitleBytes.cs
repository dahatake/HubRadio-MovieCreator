using System.Text;
using System.Text.RegularExpressions;

namespace HubRadio.App.Rendering;

/// <summary>
/// FR-136 / FR-138 / FR-141: the stored subtitle file of a template Job differs from the plain standard file only in the
/// override tags of the Dialogue line (the template's color, typeface and motion, and the place inside the margins). This check
/// accepts exactly that: every other byte (header, styles, times, style name, text) must equal the plain file, and the override
/// block may hold only the closed set of template tags, with a clip inside the output frame.
/// </summary>
internal static partial class StandardTemplateSubtitleBytes
{
    private const string Marker = ",,0,0,0,,";

    [GeneratedRegex(@"^(?:\\(?:an5|q2|pos\(\d+(?:\.\d+)?,\d+(?:\.\d+)?\)|clip\(\d+,\d+,\d+,\d+\)|fn[^\\{}]{1,64}|[134]c&H[0-9A-Fa-f]{6}&|fad\(\d+,\d+\)|fscx\d+|fscy\d+|frz-?\d+|t\(\d+,\d+,(?:\\(?:fscx\d+|fscy\d+|frz-?\d+))+\)))+$")]
    private static partial Regex Overrides();

    [GeneratedRegex(@"\\clip\((\d+),(\d+),(\d+),(\d+)\)")]
    private static partial Regex Clip();

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
        if (left.Length != right.Length)
        {
            return false;
        }

        int dialogues = 0;
        for (int index = 0; index < left.Length; index++)
        {
            if (!left[index].StartsWith("Dialogue: ", StringComparison.Ordinal) || !right[index].StartsWith("Dialogue: ", StringComparison.Ordinal))
            {
                if (left[index] != right[index])
                {
                    return false;
                }

                continue;
            }

            int a = left[index].IndexOf(Marker, StringComparison.Ordinal);
            int b = right[index].IndexOf(Marker, StringComparison.Ordinal);
            if (a < 0 || b < 0 || left[index][..a] != right[index][..b])
            {
                return false;
            }

            string tailA = left[index][(a + Marker.Length)..];
            string tailB = right[index][(b + Marker.Length)..];
            int closeA = tailA.IndexOf('}', StringComparison.Ordinal);
            int closeB = tailB.IndexOf('}', StringComparison.Ordinal);
            if (!tailA.StartsWith('{') || !tailB.StartsWith('{') || closeA < 0 || closeB < 0 || tailA[(closeA + 1)..] != tailB[(closeB + 1)..])
            {
                return false;
            }

            string block = tailA[1..closeA];
            Match clip = Clip().Match(block);
            if (!Overrides().IsMatch(block) || !clip.Success
                || int.Parse(clip.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) > width
                || int.Parse(clip.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) > height)
            {
                return false;
            }

            dialogues++;
        }

        return dialogues == 1;
    }
}
