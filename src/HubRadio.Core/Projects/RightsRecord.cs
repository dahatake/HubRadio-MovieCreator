namespace HubRadio.Core.Projects;

public enum RightsStatus
{
    Unconfirmed,
    Approved,
    Rejected,
}

public sealed class RightsRecord
{
    public RightsRecord(
        RightsStatus status,
        string? evidenceReference,
        string allowedUse,
        bool externalPublicationAllowed)
    {
        ArgumentNullException.ThrowIfNull(allowedUse);

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown rights status.");
        }

        ValidateRequiredText(allowedUse, nameof(allowedUse), "An allowed use");

        if (evidenceReference is not null)
        {
            ValidateRequiredText(
                evidenceReference,
                nameof(evidenceReference),
                "An evidence reference");
        }

        if (status == RightsStatus.Approved && evidenceReference is null)
        {
            throw new ArgumentException(
                "Approved rights require an evidence reference.",
                nameof(evidenceReference));
        }

        if (status != RightsStatus.Approved && externalPublicationAllowed)
        {
            throw new ArgumentException(
                "External publication cannot be allowed unless rights are approved.",
                nameof(externalPublicationAllowed));
        }

        Status = status;
        EvidenceReference = evidenceReference;
        AllowedUse = allowedUse;
        ExternalPublicationAllowed =
            status == RightsStatus.Approved && externalPublicationAllowed;
    }

    public RightsStatus Status { get; }

    public string? EvidenceReference { get; }

    public string AllowedUse { get; }

    public bool ExternalPublicationAllowed { get; }

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