using System.Text;
using System.Text.Json;
using HubRadio.App.StyleTemplates;
using HubRadio.App.Web;

namespace HubRadio.App.Tests;

/// <summary>FR-137 (6) AC-155: registering and listing the user's standing pictures over the bridge (portraits.list / portraits.register).</summary>
[TestClass]
public sealed class PortraitBridgeTests
{
    private static readonly string[] ResultKeys = ["status", "portraitCount", "portraits"];
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private string _temp = null!;

    [TestInitialize]
    public void Initialize() =>
        _temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-portrait-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, recursive: true);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static string RegisterParams(string name = "コパ", string rights = "confirmed", string imageType = "png", byte[]? bytes = null,
        string? note = null) =>
        JsonSerializer.Serialize(new { name, imageType, imageBase64 = Convert.ToBase64String(bytes ?? Png), rights, rightsNote = note });

    private static BridgeRequest Request(BridgeMethod method, string parameters) =>
        BridgeRequest.Create(Guid.NewGuid(), method, JsonDocument.Parse(parameters).RootElement.Clone());

    private PortraitBridgeHandler Handler(out CharacterPortraitStore store, bool authorized = true)
    {
        store = new CharacterPortraitStore(Path.Combine(_temp, "store"));
        return new PortraitBridgeHandler(store, () => authorized, new FixedTime(Now));
    }

    // AC-155: closed parameters only.
    [TestMethod]
    public void ProtocolAcceptsOnlyExactClosedParameters()
    {
        string valid = RegisterParams();
        Assert.IsTrue(Parse("portraits.register", valid, out BridgeRequest? request));
        Assert.AreEqual(BridgeMethod.PortraitsRegister, request!.Method);
        Assert.AreEqual("portraits.register", BridgeProtocol.GetMethodName(request.Method));
        Assert.IsTrue(Parse("portraits.list", "{}", out BridgeRequest? list));
        Assert.AreEqual(BridgeMethod.PortraitsList, list!.Method);
        Assert.IsFalse(Parse("portraits.list", "{\"x\":1}", out _));
        foreach (string invalid in new[]
        {
            valid.Replace(",\"rightsNote\":null", "", StringComparison.Ordinal),
            valid.Replace("}", ",\"extra\":1}", StringComparison.Ordinal),
            RegisterParams(name: " "),
            RegisterParams(name: new string('x', 41)),
            RegisterParams(rights: "maybe"),
            RegisterParams(imageType: "gif"),
            RegisterParams(note: new string('n', 201)),
            valid.Replace("\"imageBase64\":\"", "\"imageBase64\":\"\",\"x\":\"", StringComparison.Ordinal),
        })
            Assert.IsFalse(Parse("portraits.register", invalid, out _), invalid);

        string oversized = valid[..^1] + ",\"pad\":\"" + new string('p', BridgeProtocol.MaxPortraitsRequestUtf8Bytes) + "\"}";
        Assert.IsFalse(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint,
            $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"portraits.register","params":{{oversized}}}""", out _, out BridgeRequestRejection rejection));
        Assert.AreEqual(BridgeRequestRejection.MessageTooLarge, rejection);
    }

    [TestMethod]
    public void SchemaDefinesTheClosedPortraitEnvelopeAndResult()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RepositoryPath("schemas", "web-bridge.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        string[] methods = definitions.GetProperty("method").GetProperty("enum").EnumerateArray().Select(static i => i.GetString()!).ToArray();
        CollectionAssert.Contains(methods, "portraits.list");
        CollectionAssert.Contains(methods, "portraits.register");
        CollectionAssert.AreEqual(ResultKeys, definitions.GetProperty("portraitsResult").GetProperty("required").EnumerateArray().Select(static i => i.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "name", "imageType", "imageBase64", "rights", "rightsNote" },
            definitions.GetProperty("portraitsRegisterParams").GetProperty("required").EnumerateArray().Select(static i => i.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "confirmed", "unconfirmed" },
            definitions.GetProperty("portraitRights").GetProperty("enum").EnumerateArray().Select(static i => i.GetString()).ToArray());
        foreach (string name in new[] { "portraitsListRequest", "portraitsRegisterRequest", "portraitsResponse", "portraitsRegisterParams", "portraitsResult" })
            Assert.IsFalse(definitions.GetProperty(name).GetProperty("additionalProperties").GetBoolean(), name);
        JsonElement limits = definitions.GetProperty("portraitsProtocolLimits");
        Assert.AreEqual(BridgeProtocol.MaxPortraitsRequestUtf8Bytes, limits.GetProperty("x-maxRequestUtf8Bytes").GetInt32());
        Assert.AreEqual(BridgeProtocol.MaxPortraitsResponseUtf8Bytes, limits.GetProperty("x-maxResponseUtf8Bytes").GetInt32());
    }

    // AC-155: nothing is registered by default; registering saves the rights state the user chose and the list shows it.
    [TestMethod]
    public void RegistersAPictureWithItsRightsStateAndListsIt()
    {
        PortraitBridgeHandler handler = Handler(out CharacterPortraitStore store);
        JsonElement empty = Result(handler.Handle(Request(BridgeMethod.PortraitsList, "{}")), "ok");
        Assert.AreEqual(0, empty.GetProperty("portraitCount").GetInt32());
        Assert.AreEqual(0, store.Count);

        JsonElement first = Result(handler.Handle(Request(BridgeMethod.PortraitsRegister, RegisterParams("コパ", "confirmed", note: "自作"))), "ok");
        Assert.AreEqual(1, first.GetProperty("portraitCount").GetInt32());
        JsonElement entry = first.GetProperty("portraits")[0];
        Assert.AreEqual("コパ", entry.GetProperty("name").GetString());
        Assert.AreEqual("confirmed", entry.GetProperty("rights").GetString());
        Assert.AreEqual("自作", entry.GetProperty("rightsNote").GetString());
        StringAssert.Matches(entry.GetProperty("id").GetString()!, new System.Text.RegularExpressions.Regex("^char-[0-9a-f]{12}$"));

        JsonElement second = Result(handler.Handle(Request(BridgeMethod.PortraitsRegister, RegisterParams("相方", "unconfirmed"))), "ok");
        Assert.AreEqual(2, second.GetProperty("portraitCount").GetInt32());
        Assert.AreEqual("unconfirmed", second.GetProperty("portraits")[1].GetProperty("rights").GetString());
        Assert.AreNotEqual(second.GetProperty("portraits")[0].GetProperty("subtitleColor").GetString(), second.GetProperty("portraits")[1].GetProperty("subtitleColor").GetString());
        // The state is durable: a new store over the same directory sees both, with the rights recorded.
        CharacterPortrait[] stored = new CharacterPortraitStore(store.Directory).List().ToArray();
        Assert.AreEqual(PortraitRightsStatus.Confirmed, stored[0].Rights);
        Assert.AreEqual(PortraitRightsStatus.Unconfirmed, stored[1].Rights);
        Assert.IsTrue(StyleTemplateCatalog.IsSelectable(StyleTemplateCatalog.Find(StyleTemplateCatalog.CharacterDialogueId)!, store.Count));
    }

    [TestMethod]
    public void RefusesDuplicatesNonImagesBadBase64AndAnUnauthorizedActorWithoutLeakingDetails()
    {
        PortraitBridgeHandler handler = Handler(out CharacterPortraitStore store);
        _ = Result(handler.Handle(Request(BridgeMethod.PortraitsRegister, RegisterParams("コパ"))), "ok");
        Assert.AreEqual("invalid", Result(handler.Handle(Request(BridgeMethod.PortraitsRegister, RegisterParams("コパ"))), "invalid").GetProperty("status").GetString());
        Assert.AreEqual("invalid", Result(handler.Handle(Request(BridgeMethod.PortraitsRegister, RegisterParams("別人", bytes: [1, 2, 3, 4, 5, 6, 7, 8, 9]))), "invalid").GetProperty("status").GetString());
        string badBase64 = RegisterParams("三人目").Replace(Convert.ToBase64String(Png), "!!!!", StringComparison.Ordinal);
        string json = handler.Handle(Request(BridgeMethod.PortraitsRegister, badBase64));
        _ = Result(json, "invalid");
        Assert.AreEqual(1, store.Count);

        PortraitBridgeHandler denied = Handler(out CharacterPortraitStore deniedStore, authorized: false);
        string forbidden = denied.Handle(Request(BridgeMethod.PortraitsRegister, RegisterParams("誰か")));
        _ = Result(forbidden, "forbidden");
        Assert.AreEqual(1, deniedStore.Count);
        foreach (string text in new[] { json, forbidden })
        {
            Assert.IsFalse(text.Contains(_temp, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(text.Contains("S-1-5", StringComparison.Ordinal));
        }

        // Without a permission the read-only list still works.
        _ = Result(denied.Handle(Request(BridgeMethod.PortraitsList, "{}")), "ok");
    }

    [TestMethod]
    public void AnUnavailableHandlerAnswersWithAClosedResult()
    {
        _ = Result(PortraitBridgeJson.Unavailable(Request(BridgeMethod.PortraitsList, "{}")), "unavailable");
    }

    private static JsonElement Result(string json, string status)
    {
        Assert.IsLessThanOrEqualTo(BridgeProtocol.MaxPortraitsResponseUtf8Bytes, Encoding.UTF8.GetByteCount(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" }, root.EnumerateObject().Select(static p => p.Name).ToArray());
        JsonElement result = root.GetProperty("result").Clone();
        CollectionAssert.AreEqual(ResultKeys, result.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.AreEqual(status, result.GetProperty("status").GetString(), json);
        return result;
    }

    private static bool Parse(string method, string parameters, out BridgeRequest? request)
    {
        string json = $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"{{method}}","params":{{parameters}}}""";
        return BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out request, out _);
    }

    private static string RepositoryPath(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
                return Path.Combine([directory.FullName, .. parts]);
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
