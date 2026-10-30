using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-136 / FR-137: orientation, motion, color, font and placement choices as declarative data.</summary>
public sealed record StyleTemplateDefinition(
    string Id,
    int Version,
    string Name,
    string Purpose,
    StyleTemplateOrientation Orientation,
    StyleTemplatePreview Preview,
    StyleTemplateCaptionMode CaptionMode,
    string TextColor,
    string OutlineColor,
    IReadOnlyList<string> EmphasisColors,
    IReadOnlyList<string> FontFamilies,
    StyleTemplateMotionKind EmphasisMotion,
    StyleTemplateMotionKind FreeTelopMotion,
    bool SoundOnTelopAppearance,
    string Placement,
    IReadOnlyList<StyleTemplateFreeTelopKind> FreeTelopKinds,
    bool LayoutDefinedOnly)
{
    public string Reference => $"{Id}@{Version}";
}
