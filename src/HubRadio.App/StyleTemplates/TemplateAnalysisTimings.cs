namespace HubRadio.App.StyleTemplates;

/// <summary>
/// FR-145 / FR-054 / NFR-REL-002: the single home of the template auto-selection's time limits, so the numbers (PARAM-013 = 60,
/// ffprobe 20, PARAM-014 = 1, PARAM-015 = 60, late display 300) are written once here and nowhere else.
/// </summary>
public static class TemplateAnalysisTimings
{
    /// <summary>PARAM-013: the longest wait for the provisional selection (length, aspect ratio and audio only), in seconds.</summary>
    public const int MaximumAnalysisSeconds = 60;

    /// <summary>FR-054 / AC-186: one ffprobe run is cut at this many seconds (an inner share of <see cref="MaximumAnalysisSeconds"/>).</summary>
    public const int ProbeSeconds = 20;

    /// <summary>PARAM-014: the wait for the speakers and the speech speed is the source video's length times this factor ...</summary>
    public const int SpeechFactsWaitFactor = 1;

    /// <summary>PARAM-015: ... plus this many seconds (model loading, audio extraction and Job preparation).</summary>
    public const int SpeechFactsWaitExtraSeconds = 60;

    /// <summary>FR-145: how long after the timeout an analysis may still finish and be shown late (スキップ→選択済み), in seconds.</summary>
    public const int LateGraceSeconds = 300;

    public static TimeSpan MaximumAnalysis { get; } = TimeSpan.FromSeconds(MaximumAnalysisSeconds);

    public static TimeSpan Probe { get; } = TimeSpan.FromSeconds(ProbeSeconds);

    public static TimeSpan LateGrace { get; } = TimeSpan.FromSeconds(LateGraceSeconds);

    /// <summary>
    /// FR-145 / AC-186: the longest wait for the speakers and the speech speed, counted from the start of the Job's transcription:
    /// the source video's length (seconds) × PARAM-014 + PARAM-015 (90 s → 150 s, 600 s → 660 s, 1200 s → 1260 s).
    /// </summary>
    public static TimeSpan SpeechFactsWaitFor(double sourceDurationSeconds) =>
        TimeSpan.FromSeconds((Math.Max(0, sourceDurationSeconds) * SpeechFactsWaitFactor) + SpeechFactsWaitExtraSeconds);
}
