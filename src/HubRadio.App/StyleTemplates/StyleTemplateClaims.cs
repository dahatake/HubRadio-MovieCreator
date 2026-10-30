using System.Text.RegularExpressions;

namespace HubRadio.App.StyleTemplates;

/// <summary>
/// FR-139 (AC-161): the description of a template, its list and its guide text make no promise of an effect (view-retention
/// rate, play count) and no claim to copy or equal a channel or a person.
/// </summary>
public static partial class StyleTemplateClaims
{
    [GeneratedRegex(@"(必ず|絶対|確実|間違いなく|保証|断言|100\s*[%％]|確約)")]
    private static partial Regex Assertive();

    [GeneratedRegex(@"(視聴維持率|維持率|再生数|再生回数|視聴回数|登録者|バズ|伸び|伸ばす|増や|アップ|向上|上がる|増える|増加)")]
    private static partial Regex EffectMetric();

    [GeneratedRegex(@"(模倣|真似|マネ|パクリ|そっくり|同じ(?:に|よう)|瓜二つ|\bclone\b|\bcopy\b|\bimitat)", RegexOptions.IgnoreCase)]
    private static partial Regex Imitation();

    /// <summary>The reasons a text is not allowed (empty when it is allowed).</summary>
    public static IReadOnlyList<string> Problems(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var problems = new List<string>();
        foreach (string sentence in text.Split(['。', '！', '!', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            bool metric = EffectMetric().IsMatch(sentence);
            if (metric && Assertive().IsMatch(sentence))
            {
                problems.Add("effect-guarantee");
            }
            else if (metric && !Negated(sentence))
            {
                problems.Add("effect-claim");
            }
        }

        if (Imitation().IsMatch(text) && !text.Contains("模倣しない", StringComparison.Ordinal)
            && !text.Contains("模倣ではあり", StringComparison.Ordinal))
        {
            problems.Add("imitation");
        }

        return problems;
    }

    /// <summary>A sentence that states the absence of a promise ("…を保証するものではありません") is allowed.</summary>
    private static bool Negated(string sentence) =>
        sentence.Contains("ではありません", StringComparison.Ordinal) || sentence.Contains("しません", StringComparison.Ordinal)
        || sentence.Contains("約束しません", StringComparison.Ordinal);

    /// <summary>The user-visible texts of one template (list entry).</summary>
    public static IEnumerable<string> VisibleTexts(StyleTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        yield return template.Name;
        yield return template.Purpose;
        yield return template.Preview.Description;
        yield return template.Preview.SampleText;
    }
}
