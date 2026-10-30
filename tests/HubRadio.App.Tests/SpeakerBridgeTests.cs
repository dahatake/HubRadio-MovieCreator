using System.Text;
using System.Text.Json;
using HubRadio.App.Rendering;
using HubRadio.App.StyleTemplates;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Time;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-140 over the bridge: the speaker assignment (話者を割り当てる / 確認して確定), the cut-out of one character from a registered
/// image (切り出し範囲の指定), and the name label that tells characters apart in a brand-fixed Project. AC-164 AC-166 AC-167.
/// </summary>
[TestClass]
public sealed class SpeakerBridgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly OriginRef Origin = OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());
    private static readonly string ProjectId = Guid.NewGuid().ToString("D");
    private string _temp = null!;

    [TestInitialize]
    public void Initialize() =>
        _temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-speaker-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, recursive: true);

    private PortraitBridgeHandler Handler(out CharacterPortraitStore store, bool authorized = true)
    {
        store = new CharacterPortraitStore(Path.Combine(_temp, "store"));
        return new PortraitBridgeHandler(store, () => authorized, new FixedTime(Now), Path.Combine(_temp, "speakers"));
    }

    private static BridgeRequest Request(BridgeMethod method, string parameters) =>
        BridgeRequest.Create(Guid.NewGuid(), method, JsonDocument.Parse(parameters).RootElement.Clone());

    private static string Assign(string cue, string speaker) => $$"""{"projectId":"{{ProjectId}}","cueIndex":{{cue}},"speakerId":{{speaker}}}""";

    private static string ProjectOnly() => $$"""{"projectId":"{{ProjectId}}"}""";

    private static JsonElement Speakers(string json, string status)
    {
        Assert.IsLessThanOrEqualTo(BridgeProtocol.MaxPortraitsResponseUtf8Bytes, Encoding.UTF8.GetByteCount(json));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement result = document.RootElement.GetProperty("result").Clone();
        CollectionAssert.AreEqual(new[] { "status", "state", "label", "estimated", "assignments" },
            result.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.AreEqual(status, result.GetProperty("status").GetString(), json);
        return result;
    }

    /// <summary>A real PNG (two halves, red and blue) as base64.</summary>
    private static string TwoCharacterPng(int width = 40, int height = 20)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            bool left = i % width < width / 2;
            pixels[(i * 4) + 0] = left ? (byte)0 : (byte)255;
            pixels[(i * 4) + 2] = left ? (byte)255 : (byte)0;
            pixels[(i * 4) + 3] = 255;
        }

        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        byte[] bytes = new byte[stream.Size];
        using DataReader reader = new(stream);
        _ = reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
        reader.ReadBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string Register(string name, string base64, string rights = "confirmed") =>
        JsonSerializer.Serialize(new { name, imageType = "png", imageBase64 = base64, rights, rightsNote = (string?)null });

    private static bool Parse(string method, string parameters, out BridgeRequest? request)
    {
        string json = $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"{{method}}","params":{{parameters}}}""";
        return BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out request, out _);
    }

    // FR-140: closed parameters only.
    [TestMethod]
    [TestCategory("FR-140")]
    public void ProtocolAcceptsOnlyClosedSpeakerAndCropParameters()
    {
        Assert.IsTrue(Parse("speakers.get", ProjectOnly(), out BridgeRequest? get));
        Assert.AreEqual(BridgeMethod.SpeakersGet, get!.Method);
        Assert.AreEqual("speakers.confirm", BridgeProtocol.GetMethodName(BridgeMethod.SpeakersConfirm));
        Assert.IsTrue(Parse("speakers.confirm", ProjectOnly(), out _));
        Assert.IsTrue(Parse("speakers.assign", Assign("null", "null"), out _));
        Assert.IsTrue(Parse("speakers.assign", Assign("3", "\"char-0123456789ab\""), out _));
        Assert.IsTrue(Parse("speakers.assign", Assign("3", "null"), out _));
        foreach (string invalid in new[]
        {
            Assign("-1", "null"), Assign("10000", "null"), Assign("1.5", "null"), Assign("\"1\"", "null"),
            Assign("null", "\"char-0123456789ab\""), Assign("1", "\"../x\""), Assign("1", "\"\""),
            "{\"projectId\":\"not-a-guid\",\"cueIndex\":null,\"speakerId\":null}", ProjectOnly().Replace("}", ",\"x\":1}", StringComparison.Ordinal),
        })
        {
            Assert.IsFalse(Parse("speakers.assign", invalid, out _), invalid);
        }

        Assert.IsFalse(Parse("speakers.get", "{}", out _));
        Assert.IsFalse(Parse("speakers.confirm", "{\"projectId\":\"x\"}", out _));
        string crop = """{"sourceId":"char-0123456789ab","name":"left","x":0,"y":0,"width":20,"height":20}""";
        Assert.IsTrue(Parse("portraits.crop", crop, out BridgeRequest? parsed), "a crop with 20 (canonical 2e1) must be accepted");
        Assert.AreEqual(BridgeMethod.PortraitsCrop, parsed!.Method);
        foreach (string invalid in new[]
        {
            crop.Replace("\"x\":0", "\"x\":-1", StringComparison.Ordinal), crop.Replace("\"width\":20", "\"width\":1.5", StringComparison.Ordinal),
            crop.Replace("\"width\":20", "\"width\":99999", StringComparison.Ordinal), crop.Replace("char-0123456789ab", "../etc", StringComparison.Ordinal),
            crop.Replace("\"name\":\"left\"", "\"name\":\" \"", StringComparison.Ordinal), crop.Replace("}", ",\"path\":\"C:\\\\x\"}", StringComparison.Ordinal),
        })
        {
            Assert.IsFalse(Parse("portraits.crop", invalid, out _), invalid);
        }
    }

    // AC-166: the estimate is a draft shown as "下書き（未確認）" until the user confirms; the draft is saved and reproduced.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC166_TheEstimateIsADraftUntilTheUserConfirmsIt()
    {
        PortraitBridgeHandler handler = Handler(out CharacterPortraitStore store);
        JsonElement none = Speakers(handler.Handle(Request(BridgeMethod.SpeakersGet, ProjectOnly())), "ok");
        Assert.AreEqual("none", none.GetProperty("state").GetString());
        // Nothing can be estimated before a character is registered (no speaker is invented).
        _ = Speakers(handler.Handle(Request(BridgeMethod.SpeakersAssign, Assign("null", "null"))), "invalid");
        _ = store.Register("コパ", WritePng("a.png"), PortraitRightsStatus.Confirmed, Now);

        JsonElement draft = Speakers(handler.Handle(Request(BridgeMethod.SpeakersAssign, Assign("null", "null"))), "ok");
        Assert.AreEqual("draft", draft.GetProperty("state").GetString());
        Assert.AreEqual(CueSpeakerAssignmentSet.DraftLabel, draft.GetProperty("label").GetString());
        Assert.IsTrue(draft.GetProperty("estimated").GetBoolean());
        // Confirming without any change is the user's decision: the label changes and it survives a new handler (a rerun).
        JsonElement confirmed = Speakers(handler.Handle(Request(BridgeMethod.SpeakersConfirm, ProjectOnly())), "ok");
        Assert.AreEqual("confirmed", confirmed.GetProperty("state").GetString());
        Assert.AreEqual(CueSpeakerAssignmentSet.ConfirmedLabel, confirmed.GetProperty("label").GetString());
        PortraitBridgeHandler again = new(store, () => true, new FixedTime(Now), Path.Combine(_temp, "speakers"));
        Assert.AreEqual("confirmed", Speakers(again.Handle(Request(BridgeMethod.SpeakersGet, ProjectOnly())), "ok").GetProperty("state").GetString());

        // A correction is a draft again; an unregistered speaker is refused; clearing a cue leaves it unassigned.
        string id = store.List()[0].Id;
        JsonElement corrected = Speakers(handler.Handle(Request(BridgeMethod.SpeakersAssign, Assign("1", $"\"{id}\""))), "ok");
        Assert.AreEqual("draft", corrected.GetProperty("state").GetString());
        Assert.AreEqual(1, corrected.GetProperty("assignments")[0].GetProperty("cueIndex").GetInt32());
        _ = Speakers(handler.Handle(Request(BridgeMethod.SpeakersAssign, Assign("2", "\"char-ffffffffffff\""))), "invalid");
        JsonElement cleared = Speakers(handler.Handle(Request(BridgeMethod.SpeakersAssign, Assign("1", "null"))), "ok");
        Assert.AreEqual(0, cleared.GetProperty("assignments").GetArrayLength());
    }

    [TestMethod]
    [TestCategory("FR-140")]
    public void ChangingAnAssignmentNeedsThePermissionButReadingDoesNot()
    {
        PortraitBridgeHandler denied = Handler(out CharacterPortraitStore store, authorized: false);
        _ = store.Register("コパ", WritePng("a.png"), PortraitRightsStatus.Confirmed, Now);
        string forbidden = denied.Handle(Request(BridgeMethod.SpeakersAssign, Assign("null", "null")));
        _ = Speakers(forbidden, "forbidden");
        _ = Speakers(denied.Handle(Request(BridgeMethod.SpeakersConfirm, ProjectOnly())), "forbidden");
        _ = Speakers(denied.Handle(Request(BridgeMethod.SpeakersGet, ProjectOnly())), "ok");
        Assert.IsFalse(Directory.Exists(Path.Combine(_temp, "speakers")), "nothing is saved without the permission");
        Assert.IsFalse(forbidden.Contains(_temp, StringComparison.OrdinalIgnoreCase));
        // A handler without a place to keep assignments answers with a closed result.
        var bare = new PortraitBridgeHandler(store, () => true, new FixedTime(Now));
        _ = Speakers(bare.Handle(Request(BridgeMethod.SpeakersGet, ProjectOnly())), "unavailable");
    }

    // AC-167: the user gives a range, one character is cut out, the rights state is inherited, the source is unchanged.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC167_CropsOneCharacterOutOfARegisteredImageOverTheBridge()
    {
        PortraitBridgeHandler handler = Handler(out CharacterPortraitStore store);
        string registered = handler.Handle(Request(BridgeMethod.PortraitsRegister, Register("元絵", TwoCharacterPng(), "unconfirmed")));
        using JsonDocument listed = JsonDocument.Parse(registered);
        string sourceId = listed.RootElement.GetProperty("result").GetProperty("portraits")[0].GetProperty("id").GetString()!;
        CharacterPortrait source = store.Find(sourceId)!;
        byte[] before = File.ReadAllBytes(store.ImagePath(source));

        string crop = $$"""{"sourceId":"{{sourceId}}","name":"左の子","x":0,"y":0,"width":20,"height":20}""";
        using JsonDocument cut = JsonDocument.Parse(handler.Handle(Request(BridgeMethod.PortraitsCrop, crop)));
        JsonElement result = cut.RootElement.GetProperty("result");
        Assert.AreEqual("ok", result.GetProperty("status").GetString());
        Assert.AreEqual(2, result.GetProperty("portraitCount").GetInt32());
        JsonElement made = result.GetProperty("portraits")[1];
        Assert.AreEqual("左の子", made.GetProperty("name").GetString());
        Assert.AreEqual("unconfirmed", made.GetProperty("rights").GetString(), "the cut-out inherits the rights state of the source");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(store.ImagePath(store.Find(sourceId)!)), "the source image is not changed");

        // A range outside the image, a duplicate name, an unknown source and a missing permission change nothing.
        foreach (string invalid in new[]
        {
            crop.Replace("\"x\":0", "\"x\":30", StringComparison.Ordinal),
            crop,
            crop.Replace(sourceId, "char-ffffffffffff", StringComparison.Ordinal).Replace("左の子", "別", StringComparison.Ordinal),
        })
        {
            using JsonDocument refused = JsonDocument.Parse(handler.Handle(Request(BridgeMethod.PortraitsCrop, invalid)));
            Assert.AreEqual("invalid", refused.RootElement.GetProperty("result").GetProperty("status").GetString(), invalid);
        }

        Assert.AreEqual(2, store.Count);
        PortraitBridgeHandler denied = new(store, () => false, new FixedTime(Now));
        using JsonDocument forbidden = JsonDocument.Parse(denied.Handle(Request(BridgeMethod.PortraitsCrop, crop.Replace("左の子", "三人目", StringComparison.Ordinal))));
        Assert.AreEqual("forbidden", forbidden.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.AreEqual(2, store.Count);
    }

    // AC-164: in a brand-fixed Project the brand color wins; the characters are told apart by the name label and the position.
    [TestMethod]
    [TestCategory("FR-140")]
    public void AC164_ABrandFixedProjectTellsCharactersApartByNameLabelAndPosition()
    {
        var store = new CharacterPortraitStore(Path.Combine(_temp, "brand"));
        CharacterPortrait a = store.Register("コパ", WritePng("a.png"), PortraitRightsStatus.Confirmed, Now);
        CharacterPortrait b = store.Register("相方", WritePng("b.png"), PortraitRightsStatus.Confirmed, Now);
        StandardDisplayedCueText[] cues = [Cue(0, 2000, "こんにちは"), Cue(2000, 2000, "また明日")];
        CueSpeakerAssignmentSet set = CueSpeakerAssignmentSet.Empty.Assign(0, a.Id).Assign(1, b.Id).Confirm();
        var brand = new StyleTemplateBrand("#102030", "#FFFFFF", ["#102030"], [], null);
        StyleTemplatePlan branded = Apply(cues, set, store.List(), brand);
        Assert.IsTrue(branded.NameLabels);
        Assert.AreEqual(1, branded.FreeTelops.Select(static item => item.Color).Distinct().Count(), "the brand color is the only color");
        Assert.AreEqual(2, branded.Portraits.Select(static shot => shot.Left).Distinct().Count(), "positions differ");
        CollectionAssert.AreEqual(new[] { "コパ", "相方" }, branded.Portraits.Select(static shot => shot.CharacterName).ToArray());
        Assert.IsFalse(Apply(cues, set, store.List(), null).NameLabels, "without a brand the characters differ by color");
    }

    private string WritePng(string name)
    {
        string path = Path.Combine(_temp, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, (byte)name.Length]);
        return path;
    }

    private static StandardDisplayedCueText Cue(long startMs, long durationMs, string text) =>
        new(new TimeRange(new RationalTime(startMs, 1000), new RationalTime(durationMs, 1000)), text, Origin);

    private static StyleTemplatePlan Apply(IReadOnlyList<StandardDisplayedCueText> cues, CueSpeakerAssignmentSet set,
        IReadOnlyList<CharacterPortrait> characters, StyleTemplateBrand? brand)
    {
        StyleTemplateDefinition template = StyleTemplateCatalog.Find(StyleTemplateCatalog.CharacterDialogueId)!;
        var limits = new StyleTemplateSubtitleLimits(int.MaxValue / 4, 1, default, new RationalTime(7, 1));
        return StyleTemplateApplier.Apply(template, cues.Select(static cue => new StyleTemplateInputCue(cue, [])).ToArray(),
            set.Resolve(cues.Count, characters).DialogueLines(cues),
            new StyleTemplateApplyOptions(limits, "standard", false, new RationalTime(60, 1), Brand: brand, Characters: characters));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
