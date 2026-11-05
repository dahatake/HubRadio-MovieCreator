using System.Text.Json;
using System.Text.Json.Serialization;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-145: the state of one Project's automatic template selection (解析待ち→解析中→選択済み／取消／スキップ).</summary>
public enum TemplateAutoSelectionState
{
    Waiting,
    Analyzing,
    Selected,
    Cancelled,
    Skipped,

    /// <summary>FR-145 (スキップ→選択済み): the analysis ended after the PARAM-013 timeout and found a template; shown late as an initial selection.</summary>
    SkippedSelected,
}

/// <summary>
/// FR-145 / NFR-REL-002: what the automatic selection decided for a Project. <see cref="Reason"/> is the Japanese short sentence the user
/// reads (why a template was chosen, or why none was). <see cref="ManualTouched"/> is true once the user chose a template themself
/// (their own choice is then never overwritten and a Job records the origin 手動).
/// </summary>
public sealed record TemplateAutoSelectionRecord(
    TemplateAutoSelectionState State,
    string? TemplateId,
    int? Version,
    string Reason,
    bool ManualTouched = false,
    bool TimedOut = false)
{
    public static TemplateAutoSelectionRecord Waiting() => new(TemplateAutoSelectionState.Waiting, null, null, "解析を待っています。");

    public static TemplateAutoSelectionRecord Analyzing() => new(TemplateAutoSelectionState.Analyzing, null, null, "元動画を解析しています。");

    public static TemplateAutoSelectionRecord Skipped(string reason) => new(TemplateAutoSelectionState.Skipped, null, null, reason);

    /// <summary>The skip caused by the PARAM-013 timeout; only this skip can still become スキップ→選択済み (a failure, the setting off and "no fit" never do).</summary>
    public static TemplateAutoSelectionRecord TimedOutSkip(string reason) => Skipped(reason) with { TimedOut = true };

    public bool IsOpen => State is TemplateAutoSelectionState.Waiting or TemplateAutoSelectionState.Analyzing;

    /// <summary>True when the record carries a template to show as the initial selection (選択済み, or スキップ→選択済み).</summary>
    public bool HasSelection => State is TemplateAutoSelectionState.Selected or TemplateAutoSelectionState.SkippedSelected;

    /// <summary>The state in Japanese as the requirement names it (FR-145 状態表).</summary>
    public string Label => State switch
    {
        TemplateAutoSelectionState.Waiting => "解析待ち",
        TemplateAutoSelectionState.Analyzing => "解析中",
        TemplateAutoSelectionState.Selected => "選択済み",
        TemplateAutoSelectionState.Cancelled => "取消",
        TemplateAutoSelectionState.SkippedSelected => "スキップ→選択済み",
        _ => "スキップ",
    };

    /// <summary>The bridge state (closed set). スキップ→選択済み is shown as selected: the screen only needs the template and the reason.</summary>
    public string StateName => State switch
    {
        TemplateAutoSelectionState.Waiting => "waiting",
        TemplateAutoSelectionState.Analyzing => "analyzing",
        TemplateAutoSelectionState.Selected or TemplateAutoSelectionState.SkippedSelected => "selected",
        TemplateAutoSelectionState.Cancelled => "cancelled",
        _ => "skipped",
    };
}

/// <summary>FR-145: the facts of the source video the selection uses (FR-001 / FR-054 media facts; FR-003 speakers and speech speed when known).</summary>
public sealed record TemplateAnalysisInput(
    double DurationSeconds,
    int Width,
    int Height,
    bool? HasAudio,
    int? SpeakerCount = null,
    double? WordsPerMinute = null)
{
    public bool IsVertical => Height > Width;
}

/// <summary>FR-145: the template chosen and the Japanese reason, or null when no template fits.</summary>
public sealed record TemplateChoice(string TemplateId, string Reason);

/// <summary>
/// FR-145: picks the best of the seven FR-137 templates from the source video's facts. Only the template kind is chosen; the video and
/// the speech are never changed (NFR-FACT-001). The rules are fixed (no model, no network), so the same facts always choose the same template.
/// </summary>
public static class TemplateAutoSelector
{
    /// <summary>A vertical video up to this many seconds is a short (ショート飛び出し).</summary>
    public const double ShortSeconds = 90;

    /// <summary>
    /// A-008 規則版: the version of the "最適" decision rules below. Raise it whenever Select's rules (thresholds, order, templates) change,
    /// so a Job's template-origin.json says which rules chose its template (FR-145, AC-188). 規則版 1 = the A-008 initial rules; 規則版 2 = the
    /// same order and thresholds applied in two steps (a provisional selection without the speech facts, then a re-decision with them).
    /// </summary>
    public const int RuleVersion = 2;

    public const string NoFitReason = "この動画に適した演出テンプレートがないため、テンプレートなし（いつもの仕上がり）にしました。";

    public static TemplateChoice? Select(TemplateAnalysisInput input, int registeredPortraitCount)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DurationSeconds <= 0 || input.Width <= 0 || input.Height <= 0 || input.HasAudio == false)
        {
            return null;
        }

        string size = input.IsVertical ? "縦型" : "横長";
        string length = $"長さ約 {Math.Round(input.DurationSeconds):0} 秒の{size}の動画";
        int speakers = input.SpeakerCount ?? 0;
        double pace = input.WordsPerMinute ?? 0;
        if (input.IsVertical)
        {
            return input.DurationSeconds <= ShortSeconds
                ? new TemplateChoice("short-pop", $"{length}なので、縦型ショート向けの「ショート飛び出し」を選びました。")
                : new TemplateChoice("vertical-3-tier", $"{length}なので、上・中・下に分けて見せる「縦型 3 段」を選びました。");
        }

        if (speakers >= 2 && pace >= 300)
        {
            // Only the character dialogue template fits a quick exchange between several speakers; it needs the user's own standing pictures.
            return registeredPortraitCount > 0
                ? new TemplateChoice("character-dialogue", $"{length}で話者が {speakers} 人、話の速さが速いため、「キャラクター対話」を選びました。")
                : null;
        }

        if (speakers >= 2)
        {
            return new TemplateChoice("talk-show", $"{length}で話者が {speakers} 人のため、対談向けの「対談番組」を選びました。");
        }

        if (pace >= 300 && input.DurationSeconds <= 600)
        {
            return new TemplateChoice("variety", $"{length}で話の速さが速いため、にぎやかな「バラエティ」を選びました。");
        }

        if (pace > 0 && pace <= 140)
        {
            return new TemplateChoice("handmade", $"{length}で話がゆっくりなため、親しみのある「手作り感」を選びました。");
        }

        return new TemplateChoice("explainer", $"{length}なので、話の内容を全文字幕で伝える「解説」を選びました。");
    }
}

/// <summary>FR-145: keeps the selection record per Project (key = the Project creation draft id) as <c>{key}.json</c>, written atomically.</summary>
public sealed class TemplateAutoSelectionStore
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly object Gate = new();
    private readonly string directory;

    public TemplateAutoSelectionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
    }

    private string PathFor(Guid key) => Path.Combine(directory, key.ToString("D") + ".json");

    public TemplateAutoSelectionRecord? Load(Guid key)
    {
        lock (Gate)
        {
            return LoadUnlocked(key);
        }
    }

    private TemplateAutoSelectionRecord? LoadUnlocked(Guid key)
    {
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            TemplateAutoSelectionRecord? record = JsonSerializer.Deserialize<TemplateAutoSelectionRecord>(File.ReadAllText(path), Options);
            return record is not null && Enum.IsDefined(record.State) && record.Reason is { Length: <= 400 } ? record : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void SaveUnlocked(Guid key, TemplateAutoSelectionRecord record)
    {
        Directory.CreateDirectory(directory);
        string path = PathFor(key);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Saves the record (the first write for a Project, e.g. 解析中 or スキップ).</summary>
    public void Save(Guid key, TemplateAutoSelectionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (Gate)
        {
            SaveUnlocked(key, record);
        }
    }

    /// <summary>
    /// Saves a result of the analysis only while the selection is still open (解析待ち or 解析中). A selection the user cancelled (or one
    /// already decided) is never overwritten. Returns false when the result was dropped.
    /// </summary>
    public bool TryComplete(Guid key, TemplateAutoSelectionRecord result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (Gate)
        {
            TemplateAutoSelectionRecord? current = LoadUnlocked(key);
            if (current is null || !current.IsOpen)
            {
                return false;
            }

            SaveUnlocked(key, result with { ManualTouched = current.ManualTouched });
            return true;
        }
    }

    /// <summary>
    /// FR-145 (スキップ→選択済み): a template found after the timeout is kept only while the Project still shows the timed-out skip and the user
    /// has not chosen a template themself. A failure, the setting off, "no fit" and 取消 never become 選択済み. Returns false when dropped.
    /// </summary>
    public bool TryCompleteLate(Guid key, TemplateAutoSelectionRecord selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        lock (Gate)
        {
            TemplateAutoSelectionRecord? current = LoadUnlocked(key);
            if (current is not { State: TemplateAutoSelectionState.Skipped, TimedOut: true, ManualTouched: false })
            {
                return false;
            }

            SaveUnlocked(key, selected with { State = TemplateAutoSelectionState.SkippedSelected, ManualTouched = false, TimedOut = false });
            return true;
        }
    }

    /// <summary>
    /// FR-145 / AC-187 (d): true while the Project's selection can still be re-decided — it carries a template (選択済み / スキップ→選択済み)
    /// and the user has not chosen a template themself. A re-decision wait stops when this turns false.
    /// </summary>
    public bool IsRefinable(Guid key)
    {
        lock (Gate)
        {
            return LoadUnlocked(key) is { HasSelection: true, ManualTouched: false };
        }
    }

    /// <summary>
    /// FR-145 (再判定): replaces the template and the reason of a selection the analysis already made, only while it carries a template and the
    /// user has not chosen one themself (取消 is never updated). The state stays 選択済み (or スキップ→選択済み). Jobs already run keep their
    /// own origin; only the initial selection of the next Job changes. Returns false when dropped.
    /// </summary>
    public bool TryRefine(Guid key, Func<TemplateAutoSelectionRecord, TemplateAutoSelectionRecord> refine)
    {
        ArgumentNullException.ThrowIfNull(refine);
        lock (Gate)
        {
            TemplateAutoSelectionRecord? current = LoadUnlocked(key);
            if (current is not { HasSelection: true, ManualTouched: false })
            {
                return false;
            }

            TemplateAutoSelectionRecord next = refine(current) with { State = current.State, ManualTouched = false, TimedOut = false };
            if (next != current)
            {
                SaveUnlocked(key, next);
            }

            return true;
        }
    }

    /// <summary>
    /// The user chose a template themself (or ran a Job with one): an open selection becomes 取消, a decided one only remembers the touch.
    /// Returns the record after the change, or null when the Project has none.
    /// </summary>
    public TemplateAutoSelectionRecord? Touch(Guid key)
    {
        lock (Gate)
        {
            TemplateAutoSelectionRecord? current = LoadUnlocked(key);
            if (current is null)
            {
                return null;
            }

            bool cancellable = current.IsOpen || current is { State: TemplateAutoSelectionState.Skipped, TimedOut: true, ManualTouched: false };
            TemplateAutoSelectionRecord next = cancellable
                ? new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Cancelled, null, null,
                    "テンプレートを手動で選んだため、自動選択を取り消しました。", ManualTouched: true)
                : current with { ManualTouched = true };
            if (next != current)
            {
                SaveUnlocked(key, next);
            }

            return next;
        }
    }

    public void Delete(Guid key)
    {
        lock (Gate)
        {
            try
            {
                File.Delete(PathFor(key));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// FR-145 / NFR-REL-002: runs the analysis in the background so Project creation and the source video are never kept waiting. The analysis
/// ends within PARAM-013 seconds: a timeout, a failure or "no template fits" becomes スキップ with a reason and never stops the Project or a Job.
/// Nothing leaves the machine and the source video is only read.
/// </summary>
public static class TemplateAutoSelectionRunner
{
    // The time limits (PARAM-013 and the shares inside it) live only in TemplateAnalysisTimings.

    public const string DisabledReason = "設定でテンプレートの自動選択を使わないため、テンプレートは選びませんでした。";
    public const string TimeoutReason = "解析が時間内に終わらなかったため、テンプレートは選びませんでした。";
    public const string FailedReason = "解析に失敗したため、テンプレートは選びませんでした。";
    public const string UnreadableReason = "元動画の情報を読み取れなかったため、テンプレートは選びませんでした。";

    /// <summary>
    /// Records the start (解析中, or スキップ when the setting is off) and returns the background analysis task, which never throws.
    /// <paramref name="analyze"/> returns the facts of the provisional selection (null when the video could not be read); it ends within PARAM-013 seconds.
    /// <paramref name="speechFactsWait"/> (optional) is the re-decision of FR-145 (2): it is started once a provisional selection exists, is never
    /// awaited by the returned task (nothing waits for the transcription), and returns the speakers and the speech speed when the Job's
    /// transcription gives them in time (不明 otherwise). Its argument is the record check that turns false once the user chose a template.
    /// </summary>
    public static Task Start(TemplateAutoSelectionStore store, Guid key, bool enabled,
        Func<CancellationToken, Task<TemplateAnalysisInput?>> analyze, Func<int> registeredPortraitCount, TimeSpan? timeout = null, TimeSpan? lateGrace = null,
        Func<TemplateAnalysisInput, Func<bool>, CancellationToken, Task<TemplateSpeechFacts>>? speechFactsWait = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(analyze);
        ArgumentNullException.ThrowIfNull(registeredPortraitCount);
        try
        {
            store.Save(key, enabled ? TemplateAutoSelectionRecord.Analyzing() : TemplateAutoSelectionRecord.Skipped(DisabledReason));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.CompletedTask;
        }

        return enabled ? Task.Run(() => RunAsync(store, key, analyze, registeredPortraitCount, timeout ?? TemplateAnalysisTimings.MaximumAnalysis,
                    lateGrace ?? TemplateAnalysisTimings.LateGrace, speechFactsWait)) : Task.CompletedTask;
    }

    private static async Task RunAsync(TemplateAutoSelectionStore store, Guid key,
        Func<CancellationToken, Task<TemplateAnalysisInput?>> analyze, Func<int> registeredPortraitCount, TimeSpan timeout, TimeSpan lateGrace,
        Func<TemplateAnalysisInput, Func<bool>, CancellationToken, Task<TemplateSpeechFacts>>? speechFactsWait)
    {
        TemplateAutoSelectionRecord result;
        TemplateAnalysisInput? input = null;
        var cancellation = new CancellationTokenSource();
        Task<TemplateAnalysisInput?>? lateWork = null;
        try
        {
            Task<TemplateAnalysisInput?> work = Task.Run(() => analyze(cancellation.Token), CancellationToken.None);
            Task finished = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != work)
            {
                // The analysis keeps running for a bounded grace: if it ends with a template, it is shown late (スキップ→選択済み).
                lateWork = work;
                result = TemplateAutoSelectionRecord.TimedOutSkip(TimeoutReason);
            }
            else
            {
                input = await work.ConfigureAwait(false);
                result = Decide(input, registeredPortraitCount, speechFactsWait is not null);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = TemplateAutoSelectionRecord.Skipped(FailedReason);
        }

        try
        {
            _ = store.TryComplete(key, result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The record stays 解析中 on disk only when the disk fails; the user can still choose a template themself.
        }

        if (lateWork is not null)
        {
            // Not awaited: the Start task ends with the PARAM-013 timeout; the late result is picked up in the background (bounded by the grace).
            _ = CompleteLateAsync(store, key, lateWork, registeredPortraitCount, lateGrace, cancellation, speechFactsWait);
        }
        else
        {
            cancellation.Dispose();
            if (input is not null && speechFactsWait is not null)
            {
                // FR-145 (2): the re-decision runs in the background; the provisional selection above is already shown and nothing waits for it.
                _ = RefineAsync(store, key, input, registeredPortraitCount, speechFactsWait);
            }
        }
    }

    /// <summary>
    /// FR-145 (2) / AC-187 (d): once the speakers and the speech speed are known (from the Job's transcription, within the PARAM-014/015 limit),
    /// the same A-008 rules are applied again with them; the initial selection and the reason are updated for the next Job only. A selection
    /// the user already chose themself is never touched. When the speech facts stay 不明, or no template fits, the provisional selection stands.
    /// </summary>
    private static async Task RefineAsync(TemplateAutoSelectionStore store, Guid key, TemplateAnalysisInput input, Func<int> registeredPortraitCount,
        Func<TemplateAnalysisInput, Func<bool>, CancellationToken, Task<TemplateSpeechFacts>> speechFactsWait)
    {
        try
        {
            if (input.HasAudio == false)
            {
                return;
            }

            TemplateSpeechFacts facts = await speechFactsWait(input, () => store.IsRefinable(key), CancellationToken.None).ConfigureAwait(false);
            if (facts.SpeakerCount is null && facts.WordsPerMinute is null)
            {
                return;
            }

            TemplateAnalysisInput refined = input with { SpeakerCount = facts.SpeakerCount, WordsPerMinute = facts.WordsPerMinute };
            TemplateChoice? choice = TemplateAutoSelector.Select(refined, registeredPortraitCount());
            StyleTemplateDefinition? template = choice is null ? null : StyleTemplateCatalog.Find(choice.TemplateId);
            string values = TemplateSpeechFactsStore.Describe(facts);
            _ = store.TryRefine(key, current => choice is null || template is null
                // No template fits after the re-decision (e.g. a quick talk but no standing picture): the provisional selection stands, with a note.
                ? current with { Reason = current.Reason + RefinedNoFitNote + values }
                : current with { TemplateId = template.Id, Version = template.Version, Reason = choice.Reason + values + RefinedNote });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed re-decision leaves the provisional selection as it is (NFR-REL-002).
        }
    }

    public const string ProvisionalNote = "（暫定の選択です。Job の文字起こしが終わり次第、話者数・話の速さを加えて選び直します。）";
    public const string RefinedNote = "（文字起こしの完了後に、話者数・話の速さを加えて選び直しました。）";
    public const string RefinedNoFitNote = "（文字起こしの完了後に選び直しましたが、適するテンプレートがないため、暫定の選択のままにしました。）";

    private static TemplateAutoSelectionRecord Decide(TemplateAnalysisInput? input, Func<int> registeredPortraitCount, bool provisional = false)
    {
        if (input is null)
        {
            return TemplateAutoSelectionRecord.Skipped(UnreadableReason);
        }

        TemplateChoice? choice = TemplateAutoSelector.Select(input, registeredPortraitCount());
        StyleTemplateDefinition? template = choice is null ? null : StyleTemplateCatalog.Find(choice.TemplateId);
        bool speechUnknown = input.SpeakerCount is null && input.WordsPerMinute is null;
        return choice is null || template is null
            ? TemplateAutoSelectionRecord.Skipped(TemplateAutoSelector.NoFitReason)
            : new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, template.Id, template.Version,
                // AC-186: the values the decision used (or that they were 不明) are shown with the reason.
                choice.Reason + TemplateSpeechFactsStore.Describe(new TemplateSpeechFacts(input.SpeakerCount, input.WordsPerMinute))
                + (provisional && speechUnknown && input.HasAudio != false ? ProvisionalNote : string.Empty));
    }

    /// <summary>FR-145: waits (bounded) for an analysis that passed the timeout; only a found template becomes スキップ→選択済み, never a failure or "no fit".</summary>
    private static async Task CompleteLateAsync(TemplateAutoSelectionStore store, Guid key, Task<TemplateAnalysisInput?> work,
        Func<int> registeredPortraitCount, TimeSpan grace, CancellationTokenSource cancellation,
        Func<TemplateAnalysisInput, Func<bool>, CancellationToken, Task<TemplateSpeechFacts>>? speechFactsWait)
    {
        try
        {
            Task finished = await Task.WhenAny(work, Task.Delay(grace)).ConfigureAwait(false);
            if (finished != work)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                _ = work.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                return;
            }

            TemplateAnalysisInput? input = await work.ConfigureAwait(false);
            TemplateAutoSelectionRecord late = Decide(input, registeredPortraitCount, speechFactsWait is not null);
            if (late.State == TemplateAutoSelectionState.Selected && store.TryCompleteLate(key, late) && input is not null && speechFactsWait is not null)
            {
                _ = RefineAsync(store, key, input, registeredPortraitCount, speechFactsWait);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A late failure leaves the timed-out skip as it is (never shown late).
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}

/// <summary>FR-145 / FR-136: how a Job's template was decided. 自動選択 and 手動 are recorded with the template's ID and version; なし is the default output.</summary>
public sealed record JobTemplateOrigin(string Origin, string Label, string? TemplateId, int? Version, string? TemplateName = null, string RuleVersion = JobTemplateOrigin.NoRuleVersion)
{
    public const string Auto = "auto";
    public const string Manual = "manual";
    public const string None = "none";

    /// <summary>AC-188: recorded as the rule version unless the origin is 自動選択 (手動 and なし apply no A-008 rule).</summary>
    public const string NoRuleVersion = "なし";

    /// <summary>
    /// <paramref name="auto"/> is the Project's selection (null when it has none). A template equal to an untouched automatic selection is
    /// 自動選択 (and records the A-008 rule version that chose it); any other template, or none after the user touched the choice, is 手動; the rest is なし.
    /// </summary>
    public static JobTemplateOrigin Decide(StyleTemplateDefinition? template, TemplateAutoSelectionRecord? auto)
    {
        if (template is not null)
        {
            bool isAuto = auto is { HasSelection: true, ManualTouched: false } && auto.TemplateId == template.Id;
            return new(isAuto ? Auto : Manual, isAuto ? "自動選択" : "手動", template.Id, template.Version, template.Name,
                isAuto ? TemplateAutoSelector.RuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : NoRuleVersion);
        }

        return auto is { ManualTouched: true }
            ? new(Manual, "手動", null, null)
            : new(None, "なし", null, null);
    }
}

/// <summary>FR-145: keeps the origin in the Job's own directory (<c>template-origin.json</c>), written atomically.</summary>
public static class JobTemplateOriginStore
{
    public const string FileName = "template-origin.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // The record is read by people: the Japanese label and template name stay readable instead of \uXXXX escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Save(string jobDirectory, JobTemplateOrigin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        ArgumentNullException.ThrowIfNull(origin);
        Directory.CreateDirectory(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(origin, Options));
        File.Move(temporary, path, overwrite: true);
    }

    public static JobTemplateOrigin? Load(string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        string path = Path.Combine(jobDirectory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<JobTemplateOrigin>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
