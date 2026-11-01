namespace HubRadio.Infrastructure.Rendering;

/// <summary>FR-143 (c): a rectangle (pixels) of the placement evidence.</summary>
public sealed record OutputQcPlacementBox(int X, int Y, int Width, int Height);

/// <summary>FR-143 (c): the margins (pixels) the inspection applied (PARAM-005).</summary>
public sealed record OutputQcPlacementMargins(int Top, int Bottom, int Left, int Right);

/// <summary>FR-143 (c): one inspected subtitle or telop position and its verdict.</summary>
public sealed record OutputQcPlacementItem(string Id, OutputQcPlacementBox Box, bool InsideMargins, bool InsideRegion, bool Passed);

/// <summary>
/// FR-143 (c): the evidence of the placement inspection of a vertical output that <see cref="OutputQc"/> carries with its result: the
/// rule applied (<c>template-margins-and-region</c> for a Job that applied a production template, <c>subtitle-region</c> without one),
/// the region and margins used, the positions inspected and the verdict. A position outside the rule fails the inspection.
/// </summary>
public sealed record OutputQcPlacementEvidence(
    string Rule, int OutputWidth, int OutputHeight, OutputQcPlacementMargins? Margins, OutputQcPlacementBox Region,
    IReadOnlyList<OutputQcPlacementItem> Items, IReadOnlyList<string> Warnings)
{
    /// <summary>The inspection passes only when every inspected position passes.</summary>
    public bool Passed => Items.All(static item => item.Passed);

    public string Verdict => Passed ? "pass" : "fail";
}
