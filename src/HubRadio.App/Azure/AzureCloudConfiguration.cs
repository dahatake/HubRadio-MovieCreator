using HubRadio.Infrastructure.Azure;

namespace HubRadio.App.Azure;

internal static class AzureCloudConfiguration
{
    internal const string TenantId = "16b3c013-d300-468d-ac64-7eda0820b6d3";
    internal const string ClientId = "f1088a3a-7414-4afc-bd9f-a845bde4d1f4";
    internal const string Region = "japaneast";
    internal const string DeploymentName = "gpt-5-6-sol";
    internal const string DeploymentType = "DataZoneStandard";
    internal const string ModelName = "gpt-5.6-sol";
    internal const string ModelVersion = "2026-07-09";
    internal const string ProcessingScopeDisclosure =
        "Asia Pacific Data Zone; processing is not guaranteed to remain in Japan East.";
    internal const string RetentionDisclosure =
        "Stateless inference with automated abuse monitoring; flagged samples may be retained for human review.";

    internal static readonly Uri OpenAiEndpoint = new(
        "https://hubradio-moviecreator-jpe-d605e526.openai.azure.com/openai/v1");

    internal static AzureCredentialProvider CreateCredentialProvider() =>
        new(TenantId, ClientId);
}