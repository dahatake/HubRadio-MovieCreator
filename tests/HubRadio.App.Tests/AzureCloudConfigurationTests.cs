using HubRadio.App.Azure;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AzureCloudConfigurationTests
{
    [TestMethod]
    public void CurrentEvaluationCoordinates_AreExactNonsecretAndHttps()
    {
        Assert.AreEqual("https", AzureCloudConfiguration.OpenAiEndpoint.Scheme);
        Assert.AreEqual(
            "hubradio-moviecreator-jpe-d605e526.openai.azure.com",
            AzureCloudConfiguration.OpenAiEndpoint.Host);
        Assert.AreEqual(
            "/openai/v1",
            AzureCloudConfiguration.OpenAiEndpoint.AbsolutePath);

        var provider = AzureCloudConfiguration.CreateCredentialProvider();
        Assert.AreEqual(AzureCloudConfiguration.TenantId, provider.TenantId);
        Assert.AreEqual(AzureCloudConfiguration.ClientId, provider.ClientId);
    }
}