using System.Text.Json;
using System.Text.Json.Serialization;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-143 (c): which rule a vertical output was inspected against.</summary>
public enum VerticalPlacementRule
{
    /// <summary>A template was chosen: inside the PARAM-005 margins and the FR-014 subtitle region.</summary>
    TemplateMarginsAndRegion,

    /// <summary>No template (FR-136): the default output, inside the FR-014 subtitle region only.</summary>
    RegionOnly,
}

/// <summary>FR-143 (c): one inspected subtitle or telop position and its verdict.</summary>
public sealed record VerticalPlacementItem(string Id, StyleTemplateRect Rect, bool InsideMargins, bool InsideRegion, bool Passed);

/// <summary>FR-143 (c): the evidence of the vertical placement inspection (the rule, the regions and margins used, the positions, the verdict).</summary>
public sealed record VerticalPlacementEvidence(
    VerticalPlacementRule Rule, int Width, int Height, StyleTemplateMargins? Margins, StyleTemplateRect Region,
    IReadOnlyList<VerticalPlacementItem> Items, bool Passed, IReadOnlyList<string> Warnings)
{
    public const string PlatformUiUnconfirmedWarning = "platform UI未確認";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>
/// FR-143: the default vertical subtitle position and the output inspection of vertical placements. A template Job must
/// stay inside the PARAM-005 margins and the FR-014 region; the default output only inside the region (its position is unchanged).
/// </summary>
public static class VerticalPlacementInspector
{
    /// <summary>
    /// FR-143 (b): the default (no template) subtitle box lies at the bottom of the FR-014 subtitle region; it does not use the
    /// PARAM-005 margins, so an output without a template is not moved by them.
    /// </summary>
    public static StyleTemplateRect DefaultSubtitleRect(StyleTemplateRect region, int height)
    {
        if (region.IsEmpty || height <= 0)
        {
            throw new ArgumentException("The subtitle region must not be empty.", nameof(region));
        }

        int boxHeight = Math.Min(region.Height, Math.Max(1, height * 8 / 100));
        return new(region.Left, region.Bottom - boxHeight, region.Right, region.Bottom);
    }

    /// <summary>FR-143 (a): the box a template Job's subtitle uses — the default box moved into the margins when needed.</summary>
    public static StyleTemplateRect TemplateSubtitleRect(int width, int height, StyleTemplateMargins margins, StyleTemplateRect region)
    {
        StyleTemplateRect allowed = StyleTemplateLayout.AllowedArea(width, height, margins, region);
        if (allowed.IsEmpty)
        {
            throw new ArgumentException("The margins and the subtitle region leave no area.", nameof(region));
        }

        return DefaultSubtitleRect(allowed, height);
    }

    /// <summary>
    /// FR-143 (c): inspects the positions. With <paramref name="margins"/> set the rule is margins and region; with null it
    /// is the region only. A position outside the rule fails the inspection. <paramref name="platformUiConfirmed"/> false adds the FR-014 warning.
    /// </summary>
    public static VerticalPlacementEvidence Inspect(int width, int height, StyleTemplateMargins? margins, StyleTemplateRect region,
        IEnumerable<(string Id, StyleTemplateRect Rect)> positions, bool platformUiConfirmed)
    {
        ArgumentNullException.ThrowIfNull(positions);
        StyleTemplateRect? safe = margins is null ? null : StyleTemplateLayout.SafeArea(width, height, margins);
        var items = new List<VerticalPlacementItem>();
        foreach ((string id, StyleTemplateRect rect) in positions)
        {
            bool inMargins = safe is not { } area || area.Contains(rect);
            bool inRegion = region.Contains(rect);
            items.Add(new(id, rect, inMargins, inRegion, inMargins && inRegion));
        }

        return new(margins is null ? VerticalPlacementRule.RegionOnly : VerticalPlacementRule.TemplateMarginsAndRegion,
            width, height, margins, region, items, items.All(static item => item.Passed),
            platformUiConfirmed ? [] : [VerticalPlacementEvidence.PlatformUiUnconfirmedWarning]);
    }
}
