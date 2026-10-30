using System.Globalization;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-139 (NFR-A11Y-001): WCAG contrast of the template colors, and the ASS color text they are burned in with.</summary>
public static class StyleTemplateColor
{
    /// <summary>NFR-A11Y-001: the contrast ratio of normal text.</summary>
    public const double MinimumContrast = 4.5;

    public const string Black = "#000000";
    public const string White = "#FFFFFF";

    public static (byte Red, byte Green, byte Blue) Parse(string color)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (color.Length != 7 || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
        {
            throw new ArgumentException("A color is written as #RRGGBB.", nameof(color));
        }

        return ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>The relative luminance of WCAG 2.x.</summary>
    public static double Luminance(string color)
    {
        (byte red, byte green, byte blue) = Parse(color);
        static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(red) + 0.7152 * Linear(green) + 0.0722 * Linear(blue);
    }

    public static double Contrast(string foreground, string background)
    {
        double a = Luminance(foreground);
        double b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// The template's own background when the text reads on it at 4.5:1 or more; otherwise black or white, whichever
    /// reads better. The text color itself is never changed.
    /// </summary>
    public static string EnsureBackground(string foreground, string preferredBackground)
    {
        if (Contrast(foreground, preferredBackground) >= MinimumContrast)
        {
            return preferredBackground;
        }

        return Contrast(foreground, Black) >= Contrast(foreground, White) ? Black : White;
    }

    /// <summary>The first of the preferred, black and white on which every foreground reads at 4.5:1; else the best of them.</summary>
    public static string EnsureBackground(IReadOnlyList<string> foregrounds, string preferredBackground)
    {
        ArgumentNullException.ThrowIfNull(foregrounds);
        string[] candidates = [preferredBackground, Black, White];
        double Worst(string background) => foregrounds.Count == 0 ? double.MaxValue : foregrounds.Min(item => Contrast(item, background));
        foreach (string candidate in candidates)
        {
            if (Worst(candidate) >= MinimumContrast)
            {
                return candidate;
            }
        }

        return candidates.MaxBy(Worst)!;
    }

    /// <summary>The ASS color text of an override tag (<c>&amp;HBBGGRR&amp;</c>).</summary>
    public static string ToAss(string color)
    {
        (byte red, byte green, byte blue) = Parse(color);
        return string.Create(CultureInfo.InvariantCulture, $"&H{blue:X2}{green:X2}{red:X2}&");
    }
}
