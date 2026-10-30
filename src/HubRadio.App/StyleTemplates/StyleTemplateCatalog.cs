using HubRadio.Core.Time;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-136: vertical or landscape output the template is designed for.</summary>
public enum StyleTemplateOrientation
{
    Landscape,
    Vertical,
}

/// <summary>FR-137: how a telop appears. All motions finish in about 0.5 s.</summary>
public enum StyleTemplateMotionKind
{
    None,
    Fade,
    ShrinkFromLarge,
    Pop,
    Shake,
    Rotate,
}

/// <summary>FR-137: how the subtitle itself is shown.</summary>
public enum StyleTemplateCaptionMode
{
    /// <summary>The whole displayed cue stays as one caption.</summary>
    FullCue,

    /// <summary>One to three words at a time; only the spoken word takes the emphasis color.</summary>
    ShortWords,
}

/// <summary>FR-137: telop kinds other than the emphasis telop. Their text never comes from the system.</summary>
public enum StyleTemplateFreeTelopKind
{
    Retort,
    InnerVoice,
    Topic,
    Title,
    Hook,
    Band,
}

/// <summary>FR-136: the example shown in the template list (a description and sample colors, no media).</summary>
public sealed record StyleTemplatePreview(
    string Description, string SampleText, string TextColor, string OutlineColor, string? EmphasisColor);

/// <summary>FR-137: the templates as definitions over one mechanism (the character dialogue one is not defined, Q-022).</summary>
public static class StyleTemplateCatalog
{
    /// <summary>PARAM-006.</summary>
    public const int MaximumEmphasisColors = 2;

    /// <summary>PARAM-007.</summary>
    public const int MaximumFontFamilies = 3;

    /// <summary>FR-137: motions last about half a second.</summary>
    public static readonly RationalTime MotionDuration = new(1, 2);

    public const string ExplainerId = "explainer";
    public const string ShortPopId = "short-pop";
    public const string VarietyId = "variety";
    public const string TalkShowId = "talk-show";
    public const string Vertical3TierId = "vertical-3-tier";
    public const string HandmadeId = "handmade";

    public static IReadOnlyList<StyleTemplateDefinition> All { get; } =
    [
        new(ExplainerId, 1, "解説", "解説・講座など、話の内容を全文字幕で伝える動画", StyleTemplateOrientation.Landscape,
            new("全文の字幕に、キーワードだけ色を変えた強調テロップを重ねます。", "ここが大事なポイントです", "#FFFFFF", "#000000", "#FFD400"),
            StyleTemplateCaptionMode.FullCue, "#FFFFFF", "#000000", ["#FFD400"], ["Meiryo UI"],
            StyleTemplateMotionKind.Fade, StyleTemplateMotionKind.Fade, true, "bottom-center", [StyleTemplateFreeTelopKind.Title], false),
        new(ShortPopId, 1, "ショート飛び出し", "縦型ショートで、1〜3 語ずつ勢いよく見せたい動画", StyleTemplateOrientation.Vertical,
            new("1〜3 語ずつ表示し、話している語だけ色が変わって約 130% から 100% に縮みます。", "これ 本当に すごい", "#FFFFFF", "#000000", "#FF3B7A"),
            StyleTemplateCaptionMode.ShortWords, "#FFFFFF", "#000000", ["#FF3B7A", "#FFD400"], ["Yu Gothic UI"],
            StyleTemplateMotionKind.ShrinkFromLarge, StyleTemplateMotionKind.ShrinkFromLarge, true, "center-lower",
            [StyleTemplateFreeTelopKind.Hook, StyleTemplateFreeTelopKind.Title], false),
        new(VarietyId, 1, "バラエティ", "ツッコミや心の声を自由な位置に重ねる、にぎやかな動画", StyleTemplateOrientation.Landscape,
            new("ツッコミ・心の声が飛び出し、揺れ、回転し、出現時に効果音が鳴ります。", "なんでやねん！", "#FFFFFF", "#E60012", "#FFEB00"),
            StyleTemplateCaptionMode.FullCue, "#FFFFFF", "#E60012", ["#FFEB00", "#00C2FF"],
            ["Yu Gothic UI", "Meiryo UI", "MS Gothic"],
            StyleTemplateMotionKind.Pop, StyleTemplateMotionKind.Shake, true, "free",
            [StyleTemplateFreeTelopKind.Retort, StyleTemplateFreeTelopKind.InnerVoice], false),
        new(TalkShowId, 1, "対談番組", "2 人以上の対談・インタビュー番組（レイアウトは FR-138）", StyleTemplateOrientation.Landscape,
            new("話題・タイトル・帯を持つ落ち着いた対談向けの見せ方です。", "今日のテーマ", "#FFFFFF", "#1A1A1A", "#4FC3F7"),
            StyleTemplateCaptionMode.FullCue, "#FFFFFF", "#1A1A1A", ["#4FC3F7"], ["Meiryo UI"],
            StyleTemplateMotionKind.Fade, StyleTemplateMotionKind.Fade, false, "bottom-band",
            [StyleTemplateFreeTelopKind.Topic, StyleTemplateFreeTelopKind.Title, StyleTemplateFreeTelopKind.Band], true),
        new(Vertical3TierId, 1, "縦型 3 段", "縦型で、上段・中段・下段に分けて見せたい動画（レイアウトは FR-138）", StyleTemplateOrientation.Vertical,
            new("上にタイトル、中央に映像、下に字幕を置く縦型の 3 段構成です。", "3 段で見やすく", "#FFFFFF", "#000000", "#FFD400"),
            StyleTemplateCaptionMode.FullCue, "#FFFFFF", "#000000", ["#FFD400"], ["Yu Gothic UI"],
            StyleTemplateMotionKind.Fade, StyleTemplateMotionKind.Fade, false, "lower-tier",
            [StyleTemplateFreeTelopKind.Title, StyleTemplateFreeTelopKind.Topic], true),
        new(HandmadeId, 1, "手作り感", "作り込みすぎない、親しみのある手作り風の動画", StyleTemplateOrientation.Landscape,
            new("太いゴシックの白文字に黒縁、強調だけネオン色で控えめに見せます。", "ゆるっと いきます", "#FFFFFF", "#000000", "#39FF14"),
            StyleTemplateCaptionMode.FullCue, "#FFFFFF", "#000000", ["#39FF14"], ["Yu Gothic UI Semibold"],
            StyleTemplateMotionKind.None, StyleTemplateMotionKind.None, false, "bottom-center", [], false),
    ];

    public static StyleTemplateDefinition? Find(string id, int? version = null) =>
        All.FirstOrDefault(item => item.Id == id && (version is null || item.Version == version));

    /// <summary>PARAM-006 / PARAM-007 hold for a definition.</summary>
    public static bool IsWithinLimits(StyleTemplateDefinition definition) =>
        definition.EmphasisColors.Count <= MaximumEmphasisColors
        && definition.FontFamilies.Count is > 0 and <= MaximumFontFamilies;
}
