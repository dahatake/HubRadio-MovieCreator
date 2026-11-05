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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Waits for the local transcript's facts for at most <paramref name="maximumWait"/> (a share of the PARAM-013 whole, the ffprobe 20 seconds
    /// being a separate inner limit). Returns 不明 when they do not arrive in time, so the choice rests on length and aspect ratio alone.
    /// </summary>
    public async Task<TemplateSpeechFacts> WaitAsync(Guid key, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + maximumWait;
        while (true)
        {
            if (Load(key) is { } facts)
            {
                return facts;
            }

            if (DateTime.UtcNow >= deadline || cancellationToken.IsCancellationRequested)
            {
                return TemplateSpeechFacts.Unknown;
            }

            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return TemplateSpeechFacts.Unknown;
            }
        }
    }

    internal static string Describe(TemplateSpeechFacts? facts)
    {
        string speakers = facts?.SpeakerCount is { } count ? $"話者 {count} 人" : "話者数は不明";
        string pace = facts?.WordsPerMinute is { } speed ? $"話の速さ 毎分約 {speed.ToString("0", CultureInfo.InvariantCulture)} 語（日本語は文字）" : "話の速さは不明";
        return $"（判定に使った値: {speakers}、{pace}）";
    }
}
