using HubRadio.Core.Identity;

namespace HubRadio.Core.Analysis;

public enum ModelExecutionLocation
{
    Local,
    Cloud,
}

public enum ModelHashAvailability
{
    Available,
    NotDisclosed,
}

public enum ModelRunOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    Canceled,
}

public sealed class ModelRun
{
    public ModelRun(
        EntityId<ModelRunIdTag> id,
        string provider,
        string modelIdentifier,
        string modelVersion,
        ContentDigest? modelHash,
        ModelHashAvailability modelHashAvailability,
        ContentDigest configurationDigest,
        ContentDigest inputDigest,
        ContentDigest? outputDigest,
        ModelRunOutcome outcome,
        string? errorCode,
        ModelExecutionLocation executionLocation,
        string? destination,
        string? region,
        string? sentDataClassification,
        string? retentionPolicy,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(modelIdentifier);
        ArgumentNullException.ThrowIfNull(modelVersion);
        ArgumentNullException.ThrowIfNull(configurationDigest);
        ArgumentNullException.ThrowIfNull(inputDigest);

        AnalysisValidation.ValidateRequiredText(provider, nameof(provider), "A model provider");
        AnalysisValidation.ValidateRequiredText(
            modelIdentifier,
            nameof(modelIdentifier),
            "A model identifier");
        AnalysisValidation.ValidateRequiredText(
            modelVersion,
            nameof(modelVersion),
            "A model version");

        if (!Enum.IsDefined(executionLocation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionLocation),
                executionLocation,
                "Unknown model execution location.");
        }

        ValidateModelHash(modelHash, modelHashAvailability, executionLocation);
        ValidateOutcome(outputDigest, outcome, errorCode);

        ValidateLocationMetadata(
            executionLocation,
            destination,
            region,
            sentDataClassification,
            retentionPolicy);

        if (completedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                completedAt,
                "A model run cannot complete before it starts.");
        }

        Id = id;
        Provider = provider;
        ModelIdentifier = modelIdentifier;
        ModelVersion = modelVersion;
        ModelHash = modelHash;
        ModelHashAvailability = modelHashAvailability;
        ConfigurationDigest = configurationDigest;
        InputDigest = inputDigest;
        OutputDigest = outputDigest;
        Outcome = outcome;
        ErrorCode = errorCode;
        ExecutionLocation = executionLocation;
        Destination = destination;
        Region = region;
        SentDataClassification = sentDataClassification;
        RetentionPolicy = retentionPolicy;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    public EntityId<ModelRunIdTag> Id { get; }

    public string Provider { get; }

    public string ModelIdentifier { get; }

    public string ModelVersion { get; }

    public ContentDigest? ModelHash { get; }

    public ModelHashAvailability ModelHashAvailability { get; }

    public ContentDigest ConfigurationDigest { get; }

    public ContentDigest InputDigest { get; }

    public ContentDigest? OutputDigest { get; }

    public ModelRunOutcome Outcome { get; }

    public string? ErrorCode { get; }

    public ModelExecutionLocation ExecutionLocation { get; }

    public string? Destination { get; }

    public string? Region { get; }

    public string? SentDataClassification { get; }

    public string? RetentionPolicy { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    private static void ValidateModelHash(
        ContentDigest? modelHash,
        ModelHashAvailability modelHashAvailability,
        ModelExecutionLocation executionLocation)
    {
        if (!Enum.IsDefined(modelHashAvailability))
        {
            throw new ArgumentOutOfRangeException(
                nameof(modelHashAvailability),
                modelHashAvailability,
                "Unknown model hash availability.");
        }

        if (modelHashAvailability == ModelHashAvailability.Available && modelHash is null)
        {
            throw new ArgumentNullException(nameof(modelHash));
        }

        if (modelHashAvailability == ModelHashAvailability.NotDisclosed && modelHash is not null)
        {
            throw new ArgumentException(
                "A model hash must be absent when the provider did not disclose one.",
                nameof(modelHash));
        }

        if (executionLocation == ModelExecutionLocation.Local
            && modelHashAvailability != ModelHashAvailability.Available)
        {
            throw new ArgumentException(
                "A local model run requires the verified local model artifact hash.",
                nameof(modelHashAvailability));
        }
    }

    private static void ValidateOutcome(
        ContentDigest? outputDigest,
        ModelRunOutcome outcome,
        string? errorCode)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown model run outcome.");
        }

        if (outcome == ModelRunOutcome.Succeeded)
        {
            ArgumentNullException.ThrowIfNull(outputDigest);
            if (errorCode is not null)
            {
                throw new ArgumentException(
                    "A successful model run must not claim an error code.",
                    nameof(errorCode));
            }

            return;
        }

        if (outputDigest is not null)
        {
            throw new ArgumentException(
                "An unsuccessful model run has no successful output digest.",
                nameof(outputDigest));
        }

        if (errorCode is null)
        {
            throw new ArgumentNullException(nameof(errorCode));
        }

        AnalysisValidation.ValidateErrorCode(errorCode, nameof(errorCode));
    }

    private static void ValidateLocationMetadata(
        ModelExecutionLocation executionLocation,
        string? destination,
        string? region,
        string? sentDataClassification,
        string? retentionPolicy)
    {
        (string? Value, string Name, string Description)[] metadata =
        [
            (destination, nameof(destination), "A cloud destination"),
            (region, nameof(region), "A cloud region"),
            (sentDataClassification, nameof(sentDataClassification), "A sent-data classification"),
            (retentionPolicy, nameof(retentionPolicy), "A cloud retention policy"),
        ];

        if (executionLocation == ModelExecutionLocation.Cloud)
        {
            foreach ((string? value, string name, string description) in metadata)
            {
                if (value is null)
                {
                    throw new ArgumentNullException(name);
                }

                AnalysisValidation.ValidateRequiredText(value, name, description);
            }

            return;
        }

        foreach ((string? value, string name, _) in metadata)
        {
            if (value is not null)
            {
                throw new ArgumentException(
                    "Local model runs must not claim external transmission metadata.",
                    name);
            }
        }
    }
}