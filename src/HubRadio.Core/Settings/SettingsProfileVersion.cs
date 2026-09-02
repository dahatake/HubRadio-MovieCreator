using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HubRadio.Core.Settings;

public sealed class SettingsProfileVersion : IEquatable<SettingsProfileVersion>
{
    public SettingsProfileVersion(int major, int minor, int patch)
    {
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), major, "A major version must not be negative.");
        }

        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor), minor, "A minor version must not be negative.");
        }

        if (patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(patch), patch, "A patch version must not be negative.");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public static SettingsProfileVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out SettingsProfileVersion? version))
        {
            throw new FormatException("A settings-profile version must be canonical MAJOR.MINOR.PATCH text.");
        }

        return version;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out SettingsProfileVersion? version)
    {
        version = null;
        if (value is null)
        {
            return false;
        }

        string[] components = value.Split('.', StringSplitOptions.None);
        if (components.Length != 3 ||
            !TryParseComponent(components[0], out int major) ||
            !TryParseComponent(components[1], out int minor) ||
            !TryParseComponent(components[2], out int patch))
        {
            return false;
        }

        version = new SettingsProfileVersion(major, minor, patch);
        return string.Equals(value, version.ToString(), StringComparison.Ordinal);
    }

    public bool Equals(SettingsProfileVersion? other) =>
        other is not null &&
        Major == other.Major &&
        Minor == other.Minor &&
        Patch == other.Patch;

    public override bool Equals(object? obj) =>
        obj is SettingsProfileVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Major}.{Minor}.{Patch}");

    public static bool operator ==(
        SettingsProfileVersion? left,
        SettingsProfileVersion? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(
        SettingsProfileVersion? left,
        SettingsProfileVersion? right) =>
        !(left == right);

    private static bool TryParseComponent(string value, out int component)
    {
        component = 0;
        if (value.Length == 0 ||
            (value.Length > 1 && value[0] == '0'))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out component);
    }
}
