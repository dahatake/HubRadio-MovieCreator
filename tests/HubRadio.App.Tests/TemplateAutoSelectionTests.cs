using System.Security.Cryptography;
using System.Text.Json;
using HubRadio.App.Projects;
using HubRadio.App.StyleTemplates;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Media;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-145 (the automatic template selection, AC-179 AC-180 AC-181 AC-182) and NFR-REL-002 (a slow, failed or cancelled analysis never stops a
/// Project or a Job, AC-185), below the Project-creation level: the rules, the store, the background runner and the bridge.
/// </summary>
[TestClass]
public sealed class TemplateAutoSelectionTests
{
    private string _temp = null!;
    private static readonly string ProjectId = Guid.NewGuid().ToString("D");

    [TestInitialize]
    public void Initialize() =>
        _temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hubradio-autotemplate-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_temp, recursive: true);

    private TemplateAutoSelectionStore Store() => new(Path.Combine(_temp, "auto"));

    // FR-145 AC-179: only one of the seven FR-137 templates is ever chosen, with a Japanese reason, and the same facts give the same choice.
    [TestMethod]
    [TestCategory("FR-145")]
    [DataRow(60.0, 1080, 1920, 0, 0.0, 0, "short-pop")]
    [DataRow(600.0, 1080, 1920, 0, 0.0, 0, "vertical-3-tier")]
    [DataRow(600.0, 1920, 1080, 0, 0.0, 0, "explainer")]
    [DataRow(600.0, 1920, 1080, 2, 180.0, 0, "talk-show")]
    [DataRow(600.0, 1920, 1080, 1, 320.0, 0, "variety")]
    [DataRow(600.0, 1920, 1080, 1, 120.0, 0, "handmade")]
    [DataRow(600.0, 1920, 1080, 2, 330.0, 1, "character-dialogue")]
    public void SelectsTheTemplateThatFitsTheFacts(double seconds, int width, int height, int speakers, double pace, int portraits, string expected)
    {
        var input = new TemplateAnalysisInput(seconds, width, height, true, speakers == 0 ? null : speakers, pace == 0 ? null : pace);
        TemplateChoice? first = TemplateAutoSelector.Select(input, portraits);
        Assert.IsNotNull(first);
        Assert.AreEqual(expected, first.TemplateId);
        Assert.IsNotNull(StyleTemplateCatalog.Find(first.TemplateId));
        Assert.IsTrue(first.Reason.Any(static c => c > 0x3000), first.Reason);
        Assert.AreEqual(first, TemplateAutoSelector.Select(input, portraits));
    }

    // FR-145 AC-182: when only the character dialogue fits but no standing picture is registered, no template is chosen.
    [TestMethod]
    [TestCategory("FR-145")]
    public void ChoosesNothingWhenOnlyTheCharacterDialogueFitsAndNoPortraitIsRegistered()
    {
        var input = new TemplateAnalysisInput(600, 1920, 1080, true, 2, 330);
        Assert.IsNull(TemplateAutoSelector.Select(input, 0));
        Assert.IsNull(TemplateAutoSelector.Select(new TemplateAnalysisInput(0, 1920, 1080, true), 0));
        Assert.IsNull(TemplateAutoSelector.Select(new TemplateAnalysisInput(60, 1920, 1080, false), 0));
    }

    // FR-145 / NFR-REL-002: the result of the analysis is kept only while the selection is open; a manual choice is never overwritten.
    [TestMethod]
    [TestCategory("FR-145")]
    public void ALateAnalysisNeverOverwritesACancelledSelection()
    {
        TemplateAutoSelectionStore store = Store();
        var key = Guid.NewGuid();
        store.Save(key, TemplateAutoSelectionRecord.Analyzing());
        TemplateAutoSelectionRecord cancelled = store.Touch(key)!;
        Assert.AreEqual(TemplateAutoSelectionState.Cancelled, cancelled.State);
        Assert.IsTrue(cancelled.ManualTouched);
        Assert.IsFalse(store.TryComplete(key, new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "explainer", 1, "解説")));
        Assert.AreEqual(TemplateAutoSelectionState.Cancelled, store.Load(key)!.State);
        Assert.IsNull(store.Touch(Guid.NewGuid()));
    }

    // FR-145: touching a decided selection keeps it (the user changed the choice) and remembers the touch.
    [TestMethod]
    [TestCategory("FR-145")]
    public void TouchingADecidedSelectionRemembersTheManualChoice()
    {
        TemplateAutoSelectionStore store = Store();
        var key = Guid.NewGuid();
        store.Save(key, new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "explainer", 1, "解説"));
        TemplateAutoSelectionRecord touched = store.Touch(key)!;
        Assert.AreEqual((TemplateAutoSelectionState.Selected, true), (touched.State, touched.ManualTouched));
        Assert.AreEqual(touched, store.Load(key));
    }

    // NFR-REL-002 AC-185: a time-out, a failure, an unreadable video and the setting off each end as スキップ with a reason, in the time
    // limit; the source video is only read (its hash is the same before and after).
    [TestMethod]
    [TestCategory("NFR-REL-002")]
    [DataRow("timeout")]
    [DataRow("failure")]
    [DataRow("unreadable")]
    public async Task AnalysisProblemsEndAsSkippedWithAReasonAndNeverChangeTheSource(string kind)
    {
        string source = Path.Combine(_temp, "source.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4, 5]);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        TemplateAutoSelectionStore store = Store();
        var key = Guid.NewGuid();
        Task run = TemplateAutoSelectionRunner.Start(store, key, true, async cancellation =>
        {
            _ = await File.ReadAllBytesAsync(source, cancellation);
            return kind switch
            {
                "timeout" => await Task.Delay(Timeout.Infinite, cancellation).ContinueWith(static _ => (TemplateAnalysisInput?)null, TaskScheduler.Default),
                "failure" => throw new IOException("ffprobe failed"),
                _ => null,
            };
        }, () => 0, TimeSpan.FromMilliseconds(150));
        Assert.AreEqual(TemplateAutoSelectionState.Analyzing, store.Load(key)!.State);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        TemplateAutoSelectionRecord record = store.Load(key)!;
        Assert.AreEqual(TemplateAutoSelectionState.Skipped, record.State, kind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(record.Reason));
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    // FR-146 / FR-145 AC-184: with the setting off the analysis never runs and the state is スキップ.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task DoesNotRunTheAnalysisWhenTheSettingIsOff()
    {
        TemplateAutoSelectionStore store = Store();
        var key = Guid.NewGuid();
        bool ran = false;
        await TemplateAutoSelectionRunner.Start(store, key, false, _ => { ran = true; return Task.FromResult<TemplateAnalysisInput?>(null); }, () => 0);
        Assert.IsFalse(ran);
        TemplateAutoSelectionRecord record = store.Load(key)!;
        Assert.AreEqual((TemplateAutoSelectionState.Skipped, TemplateAutoSelectionRunner.DisabledReason), (record.State, record.Reason));
    }

    // FR-145: the origin of a Job's template (自動選択 / 手動 / なし) and its record in the Job's directory.
    [TestMethod]
    [TestCategory("FR-145")]
    public void DecidesAndStoresTheJobTemplateOrigin()
    {
        StyleTemplateDefinition explainer = StyleTemplateCatalog.Find("explainer")!;
        StyleTemplateDefinition handmade = StyleTemplateCatalog.Find("handmade")!;
        var selected = new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "explainer", 1, "解説");
        Assert.AreEqual(JobTemplateOrigin.Auto, JobTemplateOrigin.Decide(explainer, selected).Origin);
        Assert.AreEqual(JobTemplateOrigin.Manual, JobTemplateOrigin.Decide(handmade, selected).Origin);
        Assert.AreEqual(JobTemplateOrigin.Manual, JobTemplateOrigin.Decide(explainer, selected with { ManualTouched = true }).Origin);
        Assert.AreEqual(JobTemplateOrigin.Manual, JobTemplateOrigin.Decide(explainer, null).Origin);
        Assert.AreEqual(JobTemplateOrigin.None, JobTemplateOrigin.Decide(null, selected).Origin);
        Assert.AreEqual(JobTemplateOrigin.Manual, JobTemplateOrigin.Decide(null, selected with { ManualTouched = true }).Origin);
        Assert.AreEqual(JobTemplateOrigin.None, JobTemplateOrigin.Decide(null, null).Origin);

        string directory = Path.Combine(_temp, "job");
        JobTemplateOriginStore.Save(directory, JobTemplateOrigin.Decide(explainer, selected));
        Assert.AreEqual(new JobTemplateOrigin("auto", "自動選択", "explainer", 1), JobTemplateOriginStore.Load(directory));
        Assert.IsNull(JobTemplateOriginStore.Load(Path.Combine(_temp, "missing")));
    }

    private static readonly TemplateAnalysisInput Landscape = new(300, 1920, 1080, true);

    /// <summary>FR-145: a pipeline over a real workspace; the Job's origin is recorded the way a regeneration records it.</summary>
    private sealed class Project : IAsyncDisposable
    {
        private readonly SourceOnlyPipeline _pipeline;
        private readonly Guid _key = Guid.NewGuid();

        internal Project(string root)
        {
            string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            string database = Path.Combine(workspace, "hubradio.db");
            _ = DatabaseMigrator.Migrate(database, Path.Combine(workspace, "backups"));
            _pipeline = new SourceOnlyPipeline(database, workspace, new AssetCandidateRegistry(), (_, _) => Task.CompletedTask);
        }

        internal TemplateAutoSelectionStore Store => _pipeline.TemplateAutoSelections;

        internal Guid Key => _key;

        internal Task Start(bool enabled, Func<CancellationToken, Task<TemplateAnalysisInput?>> analyzer, TimeSpan? timeout = null) =>
            TemplateAutoSelectionRunner.Start(Store, _key, enabled, analyzer, () => 0, timeout);

        /// <summary>The Job's origin after the user ran a Job with the template (or none).</summary>
        internal JobTemplateOrigin RunJob(string? templateId)
        {
            var job = EntityId<JobIdTag>.New();
            _pipeline.RecordTemplateOrigin(EntityId<ProjectCreationDraftIdTag>.Parse(_key.ToString("D")),
                job, templateId is null ? null : StyleTemplateCatalog.Find(templateId));
            return JobTemplateOriginStore.Load(_pipeline.StyleTemplateDirectory(job))!;
        }

        public ValueTask DisposeAsync() => _pipeline.DisposeAsync();
    }

    // FR-145 AC-179: the analysis selects a template with a reason; a Job run without touching the choice records the template ID and
    // version with the origin 自動選択.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task AutoSelectedTemplateIsRecordedWithOriginAutoWhenTheUserDoesNotTouchTheChoice()
    {
        await using var project = new Project(_temp);
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape));
        TemplateAutoSelectionRecord record = project.Store.Load(project.Key)!;
        Assert.AreEqual((TemplateAutoSelectionState.Selected, "explainer"), (record.State, record.TemplateId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(record.Reason));
        Assert.AreEqual(new JobTemplateOrigin("auto", "自動選択", "explainer", 1), project.RunJob("explainer"));
    }

    // FR-145 AC-181: changing the automatic choice to another template applies the other template; the origin is 手動.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ChangingTheAutoSelectedTemplateRecordsTheChangedTemplateWithOriginManual()
    {
        await using var project = new Project(_temp);
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape));
        Assert.AreEqual(new JobTemplateOrigin("manual", "手動", "handmade", 1), project.RunJob("handmade"));
    }

    // FR-145 AC-180: a template chosen while the analysis still runs cancels the automatic selection; the analysis that ends later never
    // overwrites it; choosing 「テンプレートなし」 afterwards is still 手動.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ManualChoiceWhileAnalyzingCancelsTheAutoSelectionAndIsNeverOverwritten()
    {
        var gate = new TaskCompletionSource<TemplateAnalysisInput?>();
        await using var project = new Project(_temp);
        Task analysis = project.Start(true, _ => gate.Task);
        Assert.AreEqual(TemplateAutoSelectionState.Analyzing, project.Store.Load(project.Key)!.State);
        Assert.AreEqual(new JobTemplateOrigin("manual", "手動", "variety", 1), project.RunJob("variety"));
        Assert.AreEqual(TemplateAutoSelectionState.Cancelled, project.Store.Load(project.Key)!.State);
        gate.SetResult(Landscape);
        await analysis;
        TemplateAutoSelectionRecord after = project.Store.Load(project.Key)!;
        Assert.AreEqual((TemplateAutoSelectionState.Cancelled, (string?)null), (after.State, after.TemplateId));
        Assert.AreEqual(new JobTemplateOrigin("manual", "手動", null, null), project.RunJob(null));
    }

    // FR-145 AC-182 / AC-184: the setting off, a time-out, a failure, an unreadable video and "no template fits" each end as スキップ with a
    // reason and the Job without a template has the origin なし (the default output of FR-136). With the setting off a template the user
    // chooses is 手動, never 自動選択.
    [TestMethod]
    [TestCategory("FR-145")]
    [DataRow(false, "disabled")]
    [DataRow(true, "timeout")]
    [DataRow(true, "failure")]
    [DataRow(true, "unreadable")]
    [DataRow(true, "nofit")]
    public async Task SkippedAutoSelectionLeavesTheJobWithoutATemplateAndOriginNone(bool enabled, string kind)
    {
        Func<CancellationToken, Task<TemplateAnalysisInput?>> analyzer = kind switch
        {
            "timeout" => async cancellation => { await Task.Delay(Timeout.Infinite, cancellation); return Landscape; },
            "failure" => _ => throw new IOException("ffprobe failed"),
            "unreadable" => _ => Task.FromResult<TemplateAnalysisInput?>(null),
            "nofit" => _ => Task.FromResult<TemplateAnalysisInput?>(new(300, 1920, 1080, false)),
            _ => _ => Task.FromResult<TemplateAnalysisInput?>(Landscape),
        };
        await using var project = new Project(_temp);
        await project.Start(enabled, analyzer, TimeSpan.FromMilliseconds(150));
        TemplateAutoSelectionRecord record = project.Store.Load(project.Key)!;
        Assert.AreEqual(TemplateAutoSelectionState.Skipped, record.State, kind);
        Assert.IsNull(record.TemplateId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(record.Reason));
        Assert.AreEqual(JobTemplateOrigin.None, project.RunJob(null).Origin);
        if (!enabled)
        {
            Assert.AreEqual(JobTemplateOrigin.Manual, project.RunJob("explainer").Origin);
        }
    }

    private static BridgeRequest Request(BridgeMethod method, string parameters) =>
        BridgeRequest.Create(Guid.NewGuid(), method, JsonDocument.Parse(parameters).RootElement.Clone());

    private static JsonElement Result(string json, string status)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement result = document.RootElement.GetProperty("result").Clone();
        CollectionAssert.AreEqual(new[] { "status", "state", "templateId", "reason" }, result.EnumerateObject().Select(static p => p.Name).ToArray());
        Assert.AreEqual(status, result.GetProperty("status").GetString(), json);
        return result;
    }

    // FR-145: templateAuto.get is read-only and shows the state, the template and the reason; templateAuto.touch needs permission and cancels
    // an open selection; a malformed request is invalid.
    [TestMethod]
    [TestCategory("FR-145")]
    public void BridgeReadsTheSelectionAndTouchCancelsAnOpenOne()
    {
        TemplateAutoSelectionStore store = Store();
        var key = Guid.NewGuid();
        store.Save(key, TemplateAutoSelectionRecord.Analyzing());
        var access = new TemplateAutoSelectionAccess(_ => store.Load(key), _ => store.Touch(key));
        string parameters = $$"""{"projectId":"{{ProjectId}}"}""";
        var handler = new PortraitBridgeHandler(new CharacterPortraitStore(Path.Combine(_temp, "p")), () => true, TimeProvider.System, null, access);

        JsonElement analyzing = Result(handler.Handle(Request(BridgeMethod.TemplateAutoGet, parameters)), "ok");
        Assert.AreEqual("analyzing", analyzing.GetProperty("state").GetString());
        Assert.AreEqual(JsonValueKind.Null, analyzing.GetProperty("templateId").ValueKind);

        _ = store.TryComplete(key, new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "short-pop", 1, "縦型ショート向けです。"));
        JsonElement selected = Result(handler.Handle(Request(BridgeMethod.TemplateAutoGet, parameters)), "ok");
        Assert.AreEqual(("selected", "short-pop", "縦型ショート向けです。"),
            (selected.GetProperty("state").GetString(), selected.GetProperty("templateId").GetString(), selected.GetProperty("reason").GetString()));

        var key2 = Guid.NewGuid();
        store.Save(key2, TemplateAutoSelectionRecord.Analyzing());
        var access2 = new TemplateAutoSelectionAccess(_ => store.Load(key2), _ => store.Touch(key2));
        var denied = new PortraitBridgeHandler(new CharacterPortraitStore(Path.Combine(_temp, "p")), () => false, TimeProvider.System, null, access2);
        _ = Result(denied.Handle(Request(BridgeMethod.TemplateAutoTouch, parameters)), "forbidden");
        Assert.AreEqual(TemplateAutoSelectionState.Analyzing, store.Load(key2)!.State);
        JsonElement touched = Result(new PortraitBridgeHandler(new CharacterPortraitStore(Path.Combine(_temp, "p")), () => true, TimeProvider.System, null, access2)
            .Handle(Request(BridgeMethod.TemplateAutoTouch, parameters)), "ok");
        Assert.AreEqual("cancelled", touched.GetProperty("state").GetString());

        _ = Result(handler.Handle(Request(BridgeMethod.TemplateAutoGet, """{"projectId":"x"}""")), "invalid");
        _ = Result(new PortraitBridgeHandler(new CharacterPortraitStore(Path.Combine(_temp, "p")), () => true, TimeProvider.System)
            .Handle(Request(BridgeMethod.TemplateAutoGet, parameters)), "unavailable");
    }

    [TestMethod]
    [TestCategory("FR-145")]
    public void ProtocolAcceptsOnlyTheProjectIdParameter()
    {
        foreach (string method in new[] { "templateAuto.get", "templateAuto.touch" })
        {
            Assert.IsTrue(Parse(method, $$"""{"projectId":"{{ProjectId}}"}"""), method);
            Assert.IsFalse(Parse(method, "{}"), method);
            Assert.IsFalse(Parse(method, $$"""{"projectId":"{{ProjectId}}","extra":1}"""), method);
            Assert.IsFalse(Parse(method, """{"projectId":"../x"}"""), method);
        }
    }

    private static bool Parse(string method, string parameters)
    {
        string json = $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"{{method}}","params":{{parameters}}}""";
        return BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out _, out _);
    }
}
