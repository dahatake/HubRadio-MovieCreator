using HubRadio.Core.Time;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.App.Rendering;

/// <summary>
/// FR-015 / AC-063: an asset (logo / jingle) the user supplied, with the explicit statement that it may be used. A logo with a
/// verified local image <paramref name="File"/> (PNG / JPEG / WebP, digest-checked at render) is shown as an image overlay over the
/// opening instead of its name as text; a logo without a file keeps showing only its name.
/// </summary>
public sealed record StandardOpenerLogo(string Name, bool Permitted, BinaryVerificationRequest? File = null);

/// <summary>FR-015: a verified local MP3 jingle (digest-checked at render) and whether the user permitted its use.</summary>
public sealed record StandardOpenerJingle(string Name, BinaryVerificationRequest File, bool Permitted);

/// <summary>FR-015 / AC-063: the verified logo image and the opening range it is shown over; its box is fixed above the opening text.</summary>
public sealed record StandardOpenerLogoOverlay(BinaryVerificationRequest File, HubRadio.Core.Time.TimeRange Range)
{
    public const double Left = 0.10;
    public const double Top = 0.10;
    public const double Width = 0.20;
    public const double Height = 0.20;
}

/// <summary>
/// FR-015: what the opening shows once the user enabled <c>editing.longOpenerEnabled</c> / <c>editing.shortOpenerEnabled</c>.
/// Only permitted assets and user-entered text are used; nothing is invented. The long template shows every field; the short
/// template is the simplified one (title and program name only, a shorter time, no logo or performers).
/// </summary>
public sealed class StandardOpenerInput
{
    public const int MaximumPerformers = 8;

    public StandardOpenerInput(
        string? programName, string? episodeNumber, string title, IReadOnlyList<string>? performers,
        string? publicScope, StandardOpenerLogo? logo, StandardOpenerJingle? jingle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ProgramName = Optional(programName, nameof(programName));
        EpisodeNumber = Optional(episodeNumber, nameof(episodeNumber));
        Title = Require(title, nameof(title));
        PublicScope = Optional(publicScope, nameof(publicScope));
        string[] names = (performers ?? []).Select(static item => Require(item, nameof(performers))).ToArray();
        if (names.Length > MaximumPerformers)
        {
            throw new ArgumentException("Too many performers.", nameof(performers));
        }

        Performers = Array.AsReadOnly(names);
        if (logo?.File is { } logoFile
            && Path.GetExtension(logoFile.FilePath).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            throw new ArgumentException("An opener logo image must be a PNG, JPEG or WebP file.", nameof(logo));
        }

        Logo = logo;
        Jingle = jingle;
    }

    public string? ProgramName { get; }
    public string? EpisodeNumber { get; }
    public string Title { get; }
    public IReadOnlyList<string> Performers { get; }
    public string? PublicScope { get; }
    public StandardOpenerLogo? Logo { get; }
    public StandardOpenerJingle? Jingle { get; }

    /// <summary>The opening lines for the long (full) or short (simplified) template, in display order.</summary>
    internal IReadOnlyList<string> Lines(bool shortForm)
    {
        var lines = new List<string>();
        if (shortForm)
        {
            lines.Add(string.Join(" ", new[] { ProgramName, EpisodeNumber }.Where(static item => item is not null)));
            lines[0] = lines[0].Length == 0 ? Title : lines[0];
            if (lines[0] != Title)
            {
                lines.Add(Title);
            }

            return lines;
        }

        string head = string.Join(" ", new[] { Logo?.File is null ? Logo?.Name : null, ProgramName, EpisodeNumber }.Where(static item => item is not null));
        if (head.Length > 0)
        {
            lines.Add(head);
        }

        lines.Add(Title);
        if (Performers.Count > 0)
        {
            lines.Add(string.Join("、", Performers));
        }

        if (PublicScope is not null)
        {
            lines.Add(PublicScope);
        }

        return lines;
    }

    /// <summary>How long the opener shows: the long one up to <paramref name="longMaximum"/>, the short one at most one second.</summary>
    internal static RationalTime Duration(bool shortForm, RationalTime longMaximum)
    {
        RationalTime shortLength = new(1, 1);
        return shortForm && shortLength < longMaximum ? shortLength : longMaximum;
    }

    private static string? Optional(string? value, string name) => string.IsNullOrWhiteSpace(value) ? null : Require(value, name);

    private static string Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new ArgumentException("An opener text must be a single trimmed line of at most 120 characters.", name);
        }

        return value;
    }
}
