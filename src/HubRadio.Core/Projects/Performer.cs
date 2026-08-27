using HubRadio.Core.Identity;

namespace HubRadio.Core.Projects;

public sealed class PerformerIdTag
{
}

public sealed class Performer
{
    public Performer(
        EntityId<PerformerIdTag> id,
        string displayName,
        string role,
        DateTimeOffset? titleConfirmedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(role);

        ValidateRequiredText(displayName, nameof(displayName), "A display name");
        ValidateRequiredText(role, nameof(role), "A role");

        Id = id;
        DisplayName = displayName;
        Role = role;
        TitleConfirmedAt = titleConfirmedAt;
    }

    public EntityId<PerformerIdTag> Id { get; }

    public string DisplayName { get; }

    public string Role { get; }

    public DateTimeOffset? TitleConfirmedAt { get; }

    private static void ValidateRequiredText(
        string value,
        string parameterName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} must not be blank or have leading or trailing whitespace.",
                parameterName);
        }
    }
}