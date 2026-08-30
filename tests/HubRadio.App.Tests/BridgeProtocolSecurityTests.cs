using System.Text;
using HubRadio.App.Web;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class BridgeProtocolSecurityTests
{
    private const string RequestId = "00112233-4455-6677-8899-aabbccddeeff";

    [TestMethod]
    [DataRow("<script>x</script>")]
    [DataRow("bridge.ping;calc")]
    [DataRow("bridge.p\u0456ng")]
    [DataRow("--filter=movie=attack")]
    public void AdversarialUnknownMethodsAreRejectedWithoutReflection(string method)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"{method}\",\"params\":{{}}}}";
        Assert.IsLessThanOrEqualTo(
            BridgeProtocol.MaxRequestUtf8Bytes,
            Encoding.UTF8.GetByteCount(message));

        bool accepted = BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection);

        Assert.IsFalse(accepted);
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.MethodNotAllowed, rejection);
        Assert.IsFalse(rejection.ToString().Contains(method, StringComparison.Ordinal));
    }
}