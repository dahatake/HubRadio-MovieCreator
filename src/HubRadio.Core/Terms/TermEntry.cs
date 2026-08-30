using System.Globalization;
using System.Text;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Templates;

namespace HubRadio.Core.Terms;

public sealed class TermEntryIdTag
{
}

public enum TermScopeKind
{
    Project,
    Series,
}

public sealed class TermScope : IEquatable<TermScope>
{
    private TermScope(EntityId<ProjectIdTag> projectId)
    {
        Kind = TermScopeKind.Project;
        ProjectId = projectId;
    }

    private TermScope(EntityId<SeriesIdTag> seriesId)
    {
        Kind = TermScopeKind.Series;
        SeriesId = seriesId;
    }

    public TermScopeKind Kind { get; }

    public EntityId<ProjectIdTag>? ProjectId { get; }

    public EntityId<SeriesIdTag>? SeriesId { get; }

    public static TermScope ForProject(EntityId<ProjectIdTag> projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        return new TermScope(projectId);
    }

    public static TermScope ForSeries(EntityId<SeriesIdTag> seriesId)
    {
        ArgumentNullException.ThrowIfNull(seriesId);
        return new TermScope(seriesId);
    }

    public bool Equals(TermScope? other)
    {
        if (other is null || Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            TermScopeKind.Project => ProjectId == other.ProjectId,
            TermScopeKind.Series => SeriesId == other.SeriesId,
            _ => false,
        };
    }

    public override bool Equals(object? obj) =>
        obj is TermScope other && Equals(other);

    public override int GetHashCode()
    {
        return Kind switch
        {
            TermScopeKind.Project => HashCode.Combine(Kind, ProjectId),
            TermScopeKind.Series => HashCode.Combine(Kind, SeriesId),
            _ => Kind.GetHashCode(),
        };
    }

    public static bool operator ==(TermScope? left, TermScope? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(TermScope? left, TermScope? right) =>
        !(left == right);
}

public sealed class TermEntry
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public TermEntry(
        EntityId<TermEntryIdTag> id,
        TermScope scope,
        string writtenForm,
        string reading,
        int version)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(scope);

        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "A term-entry version must not be negative.");
        }

        Id = id;
        Scope = scope;
        WrittenForm = ValidateTermText(
            writtenForm,
            nameof(writtenForm),
            "A term written form");
        Reading = ValidateTermText(reading, nameof(reading), "A term reading");
        Version = version;
    }

    public EntityId<TermEntryIdTag> Id { get; }

    public TermScope Scope { get; }

    public string WrittenForm { get; }

    public string Reading { get; }

    public int Version { get; }

    private static string ValidateTermText(
        string? value,
        string parameterName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must not be blank or have leading or trailing whitespace.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                $"{description} must contain valid Unicode text.",
                parameterName,
                exception);
        }

        bool hasSubstantiveScalar = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                throw new ArgumentException(
                    $"{description} must not contain control or line-separator characters.",
                    parameterName);
            }

            if (!Rune.IsWhiteSpace(rune) &&
                !IsNonSubstantiveSpecialScalar(rune) &&
                category is not (UnicodeCategory.Format
                    or UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.SpaceSeparator))
            {
                hasSubstantiveScalar = true;
            }
        }

        if (!hasSubstantiveScalar)
        {
            throw new ArgumentException(
                $"{description} must contain at least one visible, substantive Unicode scalar.",
                parameterName);
        }

        return value;
    }

    private static bool IsNonSubstantiveSpecialScalar(Rune rune)
    {
        return rune.Value switch
        {
            0x115F or 0x1160 or 0x2800 or 0x3164 or 0xFFA0 => true,
            >= 0xFE00 and <= 0xFE0F => true,
            >= 0xE0100 and <= 0xE01EF => true,
            _ => false,
        };
    }
}