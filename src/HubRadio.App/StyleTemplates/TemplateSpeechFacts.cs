using System.Globalization;
using System.Text.Json;
using HubRadio.Core.Transcripts;

namespace HubRadio.App.StyleTemplates;

/// <summary>
/// FR-145 / AC-186: the speech facts of the source video taken from the local transcript the Project already has (FR-002 transcription,
/// FR-003 speaker identification): how many speakers and how fast they talk. Null means 不明 (not obtainable); nothing is sent outside the machine.
/// </summary>
public sealed record TemplateSpeechFacts(int? SpeakerCount, double? WordsPerMinute)
{
    public static TemplateSpeechFacts Unknown { get; } = new(null, null);

    /// <summary>Speakers = distinct speakers with spoken text. Pace = words per minute (Japanese counts characters) over the spoken time. An empty transcript is 不明.</summary>
    public static TemplateSpeechFacts From(TranscriptRevision? revision)
    {
        if (revision is null)
        {
            return Unknown;
        }

        var speakers = new HashSet<string>(StringComparer.Ordinal);
        double units = 0;
        double seconds = 0;
        foreach (Utterance utterance in revision.Utterances)
        {
            int count = CountUnits(utterance.Text);
            if (count == 0)
            {
                continue;
            }

            _ = speakers.Add(utterance.SpeakerId.ToString());
            units += count;
            seconds += utterance.Range.Duration.ToDouble();
        }

        if (speakers.Count == 0)
        {
            return Unknown;
        }

        double? pace = seconds > 0 ? Math.Round(units / (seconds / 60.0), 1) : null;
        return new TemplateSpeechFacts(speakers.Count, pace);
    }

    /// <summary>Words for space-separated text, characters (letters and digits) for Japanese text.</summary>
    public static int CountUnits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        bool japanese = text.Any(static c => c is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF');
        return japanese
            ? text.Count(static c => char.IsLetterOrDigit(c))
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(static word => word.Any(char.IsLetterOrDigit));
    }
}

/// <summary>FR-145: keeps the speech facts per Project (key = the Project creation draft id) as <c>{key}.speech.json</c>, written atomically.</summary>
public sealed class TemplateSpeechFactsStore
{
    private readonly string directory;

    public TemplateSpeechFactsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
    }

    private string PathFor(Guid key) => Path.Combine(directory, key.ToString("D") + ".speech.json");

    public void Save(Guid key, TemplateSpeechFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        _ = Directory.CreateDirectory(directory);
        string path = PathFor(key);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(facts));
        File.Move(temporary, path, overwrite: true);
    }

    public TemplateSpeechFacts? Load(Guid key)
    {
        try
        {
            string path = PathFor(key);
            return File.Exists(path) ? JsonSerializer.Deserialize<TemplateSpeechFacts>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Delete(Guid key)
    {
        try
        {
            File.Delete(PathFor(key));
            File.Delete(StartPathFor(key));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string StartPathFor(Guid key) => Path.Combine(directory, key.ToString("D") + ".transcription-started");

    /// <summary>
    /// FR-145: records that the Project's Job started its transcription (FR-002); the wait for the speech facts counts from this moment.
    /// Only the first start is kept (later Jobs of the Project do not move it). Never throws.
    /// </summary>
    public void MarkTranscriptionStarted(Guid key, DateTime? startedUtc = null)
    {
        try
        {
            string path = StartPathFor(key);
            if (File.Exists(path))
            {
                return;
            }

            _ = Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, (startedUtc ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture));
            File.Move(temporary, path, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The moment the first Job of the Project started its transcription, or null while no Job has started it.</summary>
    public DateTime? LoadTranscriptionStarted(Guid key)
    {
        try
        {
            string path = StartPathFor(key);
            return File.Exists(path)
                && DateTime.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime started)
                ? started.ToUniversalTime()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for the local transcript's facts for at most <paramref name="maximumWait"/>. Returns 不明 when they do not arrive in time (or
    /// <paramref name="keepWaiting"/> turns false, e.g. the user chose a template themself), so the provisional selection stands.
    /// </summary>
    public async Task<TemplateSpeechFacts> WaitAsync(Guid key, TimeSpan maximumWait, CancellationToken cancellationToken,
        Func<bool>? keepWaiting = null, TimeSpan? pollInterval = null)
    {
        DateTime deadline = DateTime.UtcNow + maximumWait;
        while (true)
        {
            if (Load(key) is { } facts)
            {
                return facts;
            }

            if (DateTime.UtcNow >= deadline || cancellationToken.IsCancellationRequested || (keepWaiting is not null && !keepWaiting()))
            {
                return TemplateSpeechFacts.Unknown;
            }

            try
            {
                await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return TemplateSpeechFacts.Unknown;
            }
        }
    }

    /// <summary>
    /// FR-145 / AC-186: the re-decision's wait. No wait starts while the Job's transcription has not started (the provisional selection stands
    /// and nothing blocks); from its start the facts are waited for at most "source length × PARAM-014 + PARAM-015" seconds.
    /// </summary>
    public async Task<TemplateSpeechFacts> WaitAfterTranscriptionStartAsync(Guid key, double sourceDurationSeconds, CancellationToken cancellationToken,
        Func<bool>? keepWaiting = null, TimeSpan? pollInterval = null)
    {
        TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(1);
        DateTime? started;
        while ((started = LoadTranscriptionStarted(key)) is null)
        {
            if (Load(key) is { } early)
            {
                return early;
            }

            if (cancellationToken.IsCancellationRequested || (keepWaiting is not null && !keepWaiting()))
            {
                return TemplateSpeechFacts.Unknown;
            }

            try
            {
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return TemplateSpeechFacts.Unknown;
            }
        }

        TimeSpan remaining = started.Value + TemplateAnalysisTimings.SpeechFactsWaitFor(sourceDurationSeconds) - DateTime.UtcNow;
        return await WaitAsync(key, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, cancellationToken, keepWaiting, pollInterval).ConfigureAwait(false);
    }

    internal static string Describe(TemplateSpeechFacts? facts)
    {
        string speakers = facts?.SpeakerCount is { } count ? $"話者 {count} 人" : "話者数は不明";
        string pace = facts?.WordsPerMinute is { } speed ? $"話の速さ 毎分約 {speed.ToString("0", CultureInfo.InvariantCulture)} 語（日本語は文字）" : "話の速さは不明";
        return $"（判定に使った値: {speakers}、{pace}）";
    }
}
