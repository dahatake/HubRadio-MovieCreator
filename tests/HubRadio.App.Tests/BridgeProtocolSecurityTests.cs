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
    [DataRow("review.approve")]
    [DataRow("review.getSnapshot;calc")]
    [DataRow("review.getSnapsh\u043et")]
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

    [TestMethod]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"actorSid\":\"S-1-5-21-1\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"actorRole\":\"contentApprover\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"windowsSid\":\"S-1-5-18\"}")]
    [DataRow("{\"versionId\":\"11112222-3333-4444-8555-666677778888\",\"__proto__\":{\"actorRole\":\"administrator\"}}")]
    public void ReviewSnapshotRequestRejectsWebIdentityAndOpenParameters(
        string parameters)
    {
        string message =
            $"{{\"version\":1,\"requestId\":\"{RequestId}\",\"method\":\"review.getSnapshot\",\"params\":{parameters}}}";

        Assert.IsFalse(BridgeProtocol.TryParseRequest(
            WebViewHost.EntryPoint,
            message,
            out BridgeRequest? request,
            out BridgeRequestRejection rejection));
        Assert.IsNull(request);
        Assert.AreEqual(BridgeRequestRejection.SchemaViolation, rejection);
    }
}
