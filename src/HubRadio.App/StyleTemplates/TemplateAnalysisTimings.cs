namespace HubRadio.App.StyleTemplates;

/// <summary>
/// FR-145 / FR-054 / NFR-REL-002: the single home of the template auto-selection's time limits. Every limit derives from PARAM-013, so the
/// numbers (60 / 20 / 40 / 300 seconds) are written once here and nowhere else.
/// </summary>
public static class TemplateAnalysisTimings
{
    /// <summary>PARAM-013: the longest wait for the whole analysis, in seconds.</summary>
    public const int MaximumAnalysisSeconds = 60;

    /// <summary>FR-054 / AC-186: one ffprobe run is cut at this many seconds (an inner share of <see cref="MaximumAnalysisSeconds"/>).</summary>
    public const int ProbeSeconds = 20;

    /// <summary>FR-145: the longest wait for the first Job's transcript (speech facts: speakers and speech speed), inside the whole.</summary>
    public const int SpeechFactsWaitSeconds = 40;

    /// <summary>FR-145: how long after the timeout an analysis may still finish and be shown late (スキップ→選択済み), in seconds.</summary>
    public const int LateGraceSeconds = 300;

    public static TimeSpan MaximumAnalysis { get; } = TimeSpan.FromSeconds(MaximumAnalysisSeconds);

    public static TimeSpan Probe { get; } = TimeSpan.FromSeconds(ProbeSeconds);

    public static TimeSpan SpeechFactsWait { get; } = TimeSpan.FromSeconds(SpeechFactsWaitSeconds);

    public static TimeSpan LateGrace { get; } = TimeSpan.FromSeconds(LateGraceSeconds);
}
