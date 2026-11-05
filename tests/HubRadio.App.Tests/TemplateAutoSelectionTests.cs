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
        Assert.AreEqual(new JobTemplateOrigin("auto", "自動選択", "explainer", 1, "解説", "2"), JobTemplateOriginStore.Load(directory));
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

        internal Task Start(bool enabled, Func<CancellationToken, Task<TemplateAnalysisInput?>> analyzer, TimeSpan? timeout = null, TimeSpan? lateGrace = null,
            Func<TemplateAnalysisInput, Func<bool>, CancellationToken, Task<TemplateSpeechFacts>>? speechFactsWait = null, int portraits = 0) =>
            TemplateAutoSelectionRunner.Start(Store, _key, enabled, analyzer, () => portraits, timeout, lateGrace, speechFactsWait);

        internal string? JobDirectoryOfLastRun { get; private set; }

        /// <summary>The Job's origin after the user ran a Job with the template (or none).</summary>
        internal JobTemplateOrigin RunJob(string? templateId)
        {
            var job = EntityId<JobIdTag>.New();
            _pipeline.RecordTemplateOrigin(EntityId<ProjectCreationDraftIdTag>.Parse(_key.ToString("D")),
                job, templateId is null ? null : StyleTemplateCatalog.Find(templateId));
            JobDirectoryOfLastRun = _pipeline.StyleTemplateDirectory(job);
            return JobTemplateOriginStore.Load(JobDirectoryOfLastRun)!;
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
        Assert.AreEqual(new JobTemplateOrigin("auto", "自動選択", "explainer", 1, StyleTemplateCatalog.Find("explainer")!.Name, "2"), project.RunJob("explainer"));
    }

    // FR-145 AC-181: changing the automatic choice to another template applies the other template; the origin is 手動.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ChangingTheAutoSelectedTemplateRecordsTheChangedTemplateWithOriginManual()
    {
        await using var project = new Project(_temp);
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape));
        Assert.AreEqual(new JobTemplateOrigin("manual", "手動", "handmade", 1, StyleTemplateCatalog.Find("handmade")!.Name), project.RunJob("handmade"));
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
        Assert.AreEqual(new JobTemplateOrigin("manual", "手動", "variety", 1, StyleTemplateCatalog.Find("variety")!.Name), project.RunJob("variety"));
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

    // FR-145 AC-188 (A-008 規則版 2): the rule version is recorded only with the origin 自動選択; 手動 and なし record "なし" in template-origin.json.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task OnlyAnAutoSelectedJobRecordsTheRuleVersionInTemplateOriginJson()
    {
        await using var project = new Project(_temp);
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape));
        Assert.AreEqual(TemplateAutoSelector.RuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ReadRuleVersion(project.RunJob("explainer"), JobTemplateOrigin.Auto, project));
        Assert.AreEqual("2", TemplateAutoSelector.RuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(JobTemplateOrigin.NoRuleVersion, ReadRuleVersion(project.RunJob("handmade"), JobTemplateOrigin.Manual, project));
        Assert.AreEqual(JobTemplateOrigin.NoRuleVersion, ReadRuleVersion(project.RunJob(null), JobTemplateOrigin.Manual, project));

        await using var skipped = new Project(Directory.CreateDirectory(Path.Combine(_temp, "skipped")).FullName);
        await skipped.Start(false, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape));
        Assert.AreEqual(JobTemplateOrigin.NoRuleVersion, ReadRuleVersion(skipped.RunJob(null), JobTemplateOrigin.None, skipped));
    }

    // The value is read back from the template-origin.json the Job keeps (not only from the in-memory record).
    private static string ReadRuleVersion(JobTemplateOrigin origin, string expectedOrigin, Project project)
    {
        Assert.AreEqual(expectedOrigin, origin.Origin);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(project.JobDirectoryOfLastRun!, JobTemplateOriginStore.FileName)));
        return json.RootElement.GetProperty("RuleVersion").GetString()!;
    }

    private static async Task<TemplateAutoSelectionRecord> WaitForAsync(TemplateAutoSelectionStore store, Guid key, Func<TemplateAutoSelectionRecord, bool> done)
    {
        for (int i = 0; i < 200; i++)
        {
            TemplateAutoSelectionRecord? record = store.Load(key);
            if (record is not null && done(record))
            {
                return record;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The selection did not reach the expected state.");
        return null!;
    }

    // FR-145 AC-187: after the timeout skip the analysis ends with a template: it is shown late (スキップ→選択済み) with the reason; the Job
    // already run stays 出自「なし」, and the next Job takes it as the initial selection (出自「自動選択」, 規則版 1).
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ALateSelectionIsShownAfterTheTimeoutAndLeavesTheRunJobUnchanged()
    {
        var gate = new TaskCompletionSource<TemplateAnalysisInput?>();
        await using var project = new Project(_temp);
        await project.Start(true, _ => gate.Task, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));
        TemplateAutoSelectionRecord skipped = project.Store.Load(project.Key)!;
        Assert.AreEqual(("スキップ", TemplateAutoSelectionRunner.TimeoutReason), (skipped.Label, skipped.Reason));
        JobTemplateOrigin first = project.RunJob(null);
        string firstDirectory = project.JobDirectoryOfLastRun!;
        Assert.AreEqual((JobTemplateOrigin.None, JobTemplateOrigin.NoRuleVersion), (first.Origin, first.RuleVersion));

        gate.SetResult(Landscape);
        TemplateAutoSelectionRecord late = await WaitForAsync(project.Store, project.Key, r => r.State == TemplateAutoSelectionState.SkippedSelected);
        Assert.AreEqual(("スキップ→選択済み", "selected", "explainer"), (late.Label, late.StateName, late.TemplateId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(late.Reason));
        Assert.IsFalse(late.ManualTouched);

        Assert.AreEqual(first, JobTemplateOriginStore.Load(firstDirectory));
        Assert.AreEqual(new JobTemplateOrigin("auto", "自動選択", "explainer", 1, StyleTemplateCatalog.Find("explainer")!.Name, "2"), project.RunJob("explainer"));
    }

    // FR-145 AC-187: a template the user chose after the timeout skip cancels it (スキップ→取消); the late result is neither shown nor overwrites it.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ALateResultNeverOverwritesATemplateChosenAfterTheTimeout()
    {
        var gate = new TaskCompletionSource<TemplateAnalysisInput?>();
        await using var project = new Project(_temp);
        await project.Start(true, _ => gate.Task, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));
        Assert.AreEqual(JobTemplateOrigin.Manual, project.RunJob("handmade").Origin);
        TemplateAutoSelectionRecord cancelled = project.Store.Load(project.Key)!;
        Assert.AreEqual((TemplateAutoSelectionState.Cancelled, true), (cancelled.State, cancelled.ManualTouched));

        gate.SetResult(Landscape);
        await Task.Delay(300);
        Assert.AreEqual(TemplateAutoSelectionState.Cancelled, project.Store.Load(project.Key)!.State);
        Assert.IsFalse(project.Store.TryCompleteLate(project.Key, new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "explainer", 1, "解説")));
    }

    // FR-145 AC-187: a failure, "no template fits" and an analysis that stays over the grace are never shown late; skips that did not time out are final.
    [TestMethod]
    [TestCategory("FR-145")]
    [DataRow("failure")]
    [DataRow("nofit")]
    [DataRow("grace")]
    public async Task OnlyATimedOutAnalysisThatFindsATemplateIsShownLate(string kind)
    {
        var gate = new TaskCompletionSource<TemplateAnalysisInput?>();
        await using var project = new Project(_temp);
        await project.Start(true, _ => gate.Task, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(kind == "grace" ? 200 : 30000));
        Assert.IsTrue(project.Store.Load(project.Key)!.TimedOut);
        switch (kind)
        {
            case "failure": gate.SetException(new IOException("ffprobe failed")); break;
            case "nofit": gate.SetResult(new TemplateAnalysisInput(300, 1920, 1080, false)); break;
            default: await Task.Delay(500); gate.SetResult(Landscape); break;
        }

        await Task.Delay(400);
        Assert.AreEqual(TemplateAutoSelectionState.Skipped, project.Store.Load(project.Key)!.State, kind);

        TemplateAutoSelectionStore store = Store();
        var key = Guid.NewGuid();
        store.Save(key, TemplateAutoSelectionRecord.Skipped(TemplateAutoSelectionRunner.FailedReason));
        Assert.IsFalse(store.TryCompleteLate(key, new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "explainer", 1, "解説")));
        store.Save(key, TemplateAutoSelectionRecord.Skipped(TemplateAutoSelectionRunner.DisabledReason));
        Assert.IsFalse(store.TryCompleteLate(key, new TemplateAutoSelectionRecord(TemplateAutoSelectionState.Selected, "explainer", 1, "解説")));
    }

    private static BridgeRequest Request(BridgeMethod method, string parameters) =>
        BridgeRequest.Create(Guid.NewGuid(), method, JsonDocument.Parse(parameters).RootElement.Clone());

    private static JsonElement Result(string json, string status)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement result = document.RootElement.GetProperty("result").Clone();
        CollectionAssert.AreEqual(new[] { "status", "state", "templateId", "reason", "jobOrigin" }, result.EnumerateObject().Select(static p => p.Name).ToArray());
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
        Assert.AreEqual(JsonValueKind.Null, analyzing.GetProperty("jobOrigin").ValueKind);

        // AC-181 / AC-184: the origin recorded for the latest Job (自動選択 / 手動 / なし) is returned as one of the three values only.
        foreach ((string? recorded, string? expected) in new (string?, string?)[] { ("auto", "auto"), ("manual", "manual"), ("none", "none"), ("other", null), (null, null) })
        {
            var withOrigin = new TemplateAutoSelectionAccess(_ => store.Load(key), _ => store.Touch(key), _ => recorded);
            JsonElement origin = Result(new PortraitBridgeHandler(new CharacterPortraitStore(Path.Combine(_temp, "p")), () => true, TimeProvider.System, null, withOrigin)
                .Handle(Request(BridgeMethod.TemplateAutoGet, parameters)), "ok").GetProperty("jobOrigin");
            Assert.AreEqual(expected, origin.ValueKind == JsonValueKind.Null ? null : origin.GetString());
        }

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

    // FR-145 (2) AC-186 AC-187 (d) AC-188: the provisional selection (length, aspect ratio, audio only) is shown without waiting for the transcription; when the
    // speakers and the speed arrive, the same rules are applied again and the initial selection and the reason are updated for the next Job only; a Job already run
    // keeps its origin; the rule version recorded is the current one (2) for the provisional and the re-decided selection alike.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task TheProvisionalSelectionDoesNotWaitAndTheRedecisionUpdatesTheNextJobOnly()
    {
        var facts = new TaskCompletionSource<TemplateSpeechFacts>();
        await using var project = new Project(_temp);
        // The transcription never arrives within the provisional limit: the provisional selection is still made at once (no wait for the facts).
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape), TimeSpan.FromSeconds(30), null,
            (_, _, _) => facts.Task).WaitAsync(TimeSpan.FromSeconds(10));
        TemplateAutoSelectionRecord provisional = project.Store.Load(project.Key)!;
        Assert.AreEqual((TemplateAutoSelectionState.Selected, "explainer"), (provisional.State, provisional.TemplateId));
        StringAssert.Contains(provisional.Reason, "話者数は不明");
        StringAssert.Contains(provisional.Reason, TemplateAutoSelectionRunner.ProvisionalNote);
        JobTemplateOrigin ran = project.RunJob("explainer");
        Assert.AreEqual(("auto", "2"), (ran.Origin, ran.RuleVersion));
        string ranDirectory = project.JobDirectoryOfLastRun!;

        facts.SetResult(new TemplateSpeechFacts(2, 180));
        TemplateAutoSelectionRecord refined = await WaitForAsync(project.Store, project.Key, r => r.TemplateId == "talk-show");
        Assert.AreEqual(TemplateAutoSelectionState.Selected, refined.State);
        StringAssert.Contains(refined.Reason, "話者 2 人");
        StringAssert.Contains(refined.Reason, "毎分約 180");
        StringAssert.Contains(refined.Reason, TemplateAutoSelectionRunner.RefinedNote);
        Assert.AreEqual(ran, JobTemplateOriginStore.Load(ranDirectory)); // the Job already run is not changed
        JobTemplateOrigin next = project.RunJob("talk-show");
        Assert.AreEqual(("auto", "2", "talk-show"), (next.Origin, next.RuleVersion, next.TemplateId));
    }

    // FR-145 (2) AC-187 (d): once the user chose a template themself the re-decision is neither applied nor shown (取消 stays); the wait is told to stop.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ARedecisionNeverUpdatesASelectionTheUserChose()
    {
        var facts = new TaskCompletionSource<TemplateSpeechFacts>();
        Func<bool>? keepWaiting = null;
        await using var project = new Project(_temp);
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape), null, null, (_, keep, _) => { keepWaiting = keep; return facts.Task; })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("explainer", project.Store.Load(project.Key)!.TemplateId);
        Assert.IsTrue(keepWaiting!());
        Assert.AreEqual(JobTemplateOrigin.Manual, project.RunJob("handmade").Origin);
        Assert.IsFalse(keepWaiting());
        TemplateAutoSelectionRecord touched = project.Store.Load(project.Key)!;

        facts.SetResult(new TemplateSpeechFacts(2, 180));
        await Task.Delay(300);
        Assert.AreEqual(touched, project.Store.Load(project.Key));
        Assert.IsFalse(project.Store.TryRefine(project.Key, r => r with { TemplateId = "talk-show" }));
        Assert.AreEqual(JobTemplateOrigin.Manual, project.RunJob("talk-show").Origin);
    }

    // FR-145 (2) AC-186: facts that stay 不明 (a failed or empty transcription, or the wait limit passed) keep the provisional selection as it is.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task UnknownSpeechFactsKeepTheProvisionalSelection()
    {
        await using var project = new Project(_temp);
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape), null, null,
            (_, _, _) => Task.FromResult(TemplateSpeechFacts.Unknown)).WaitAsync(TimeSpan.FromSeconds(10));
        TemplateAutoSelectionRecord provisional = project.Store.Load(project.Key)!;
        await Task.Delay(300);
        Assert.AreEqual(provisional, project.Store.Load(project.Key));
        Assert.AreEqual("explainer", provisional.TemplateId);
    }

    // FR-145 (2) A-008: when the re-decision finds no template that fits (a quick talk of several speakers and no standing picture), the provisional
    // selection stays and the reason says so.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ARedecisionWithNoFittingTemplateKeepsTheProvisionalSelectionWithANote()
    {
        await using var project = new Project(_temp);
        await project.Start(true, _ => Task.FromResult<TemplateAnalysisInput?>(Landscape), null, null,
            (_, _, _) => Task.FromResult(new TemplateSpeechFacts(2, 330)), portraits: 0);
        TemplateAutoSelectionRecord kept = await WaitForAsync(project.Store, project.Key, r => r.Reason.Contains(TemplateAutoSelectionRunner.RefinedNoFitNote, StringComparison.Ordinal));
        Assert.AreEqual((TemplateAutoSelectionState.Selected, "explainer"), (kept.State, kept.TemplateId));
        StringAssert.Contains(kept.Reason, "話者 2 人");
    }

    // FR-145 (2) AC-187 (b2)+(d): a selection shown late (スキップ→選択済み) is re-decided the same way, and keeps its state.
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task ALateSelectionIsRedecidedToo()
    {
        var gate = new TaskCompletionSource<TemplateAnalysisInput?>();
        var facts = new TaskCompletionSource<TemplateSpeechFacts>();
        await using var project = new Project(_temp);
        await project.Start(true, _ => gate.Task, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30), (_, _, _) => facts.Task);
        gate.SetResult(Landscape);
        _ = await WaitForAsync(project.Store, project.Key, r => r.State == TemplateAutoSelectionState.SkippedSelected);
        facts.SetResult(new TemplateSpeechFacts(1, 100));
        TemplateAutoSelectionRecord refined = await WaitForAsync(project.Store, project.Key, r => r.TemplateId == "handmade");
        Assert.AreEqual(TemplateAutoSelectionState.SkippedSelected, refined.State);
    }

    // FR-145 (2) AC-186 / AC-188: the wait for the speech facts counts from the start of the Job's transcription; before it starts nothing is waited
    // for and the facts are 不明 only when the wait ends; the limit is "length × PARAM-014 + PARAM-015" (150 / 660 / 1260 seconds for 90 / 600 / 1200).
    [TestMethod]
    [TestCategory("FR-145")]
    public async Task TheWaitCountsFromTheStartOfTheTranscription()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(150), TemplateAnalysisTimings.SpeechFactsWaitFor(90));
        Assert.AreEqual(TimeSpan.FromSeconds(660), TemplateAnalysisTimings.SpeechFactsWaitFor(600));
        Assert.AreEqual(TimeSpan.FromSeconds(1260), TemplateAnalysisTimings.SpeechFactsWaitFor(1200));
        var store = new TemplateSpeechFactsStore(Path.Combine(_temp, "speech"));
        var key = Guid.NewGuid();
        TimeSpan poll = TimeSpan.FromMilliseconds(20);
        // Not started: still waiting (the facts do not come, and nothing times out); the caller's check ends it.
        bool keep = true;
        Task<TemplateSpeechFacts> waiting = store.WaitAfterTranscriptionStartAsync(key, 600, CancellationToken.None, () => keep, poll);
        await Task.Delay(200);
        Assert.IsFalse(waiting.IsCompleted);
        keep = false;
        Assert.AreEqual(TemplateSpeechFacts.Unknown, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        // Started long ago (more than 90 × 1 + 60 seconds): the limit has passed, so the answer is 不明 at once.
        store.MarkTranscriptionStarted(key, DateTime.UtcNow.AddSeconds(-151));
        Assert.AreEqual(TemplateSpeechFacts.Unknown, await store.WaitAfterTranscriptionStartAsync(key, 90, CancellationToken.None, null, poll).WaitAsync(TimeSpan.FromSeconds(5)));
        // The same start still leaves time for a 600-second video, and the facts that arrive in that time are returned.
        Task<TemplateSpeechFacts> within = store.WaitAfterTranscriptionStartAsync(key, 600, CancellationToken.None, null, poll);
        await Task.Delay(100);
        store.Save(key, new TemplateSpeechFacts(2, 250));
        Assert.AreEqual(new TemplateSpeechFacts(2, 250), await within.WaitAsync(TimeSpan.FromSeconds(5)));
        // Only the first start is kept.
        store.MarkTranscriptionStarted(key, DateTime.UtcNow);
        Assert.IsTrue(store.LoadTranscriptionStarted(key) < DateTime.UtcNow.AddSeconds(-100));
        store.Delete(key);
        Assert.IsNull(store.LoadTranscriptionStarted(key));
    }

    // FR-145 AC-188: the version the rules are recorded with matches the rules themselves. The decisions of A-008 over a fixed grid of facts are pinned
    // to the rule version: changing the rules changes this fingerprint and fails here until RuleVersion is raised (and the pin below updated with it).
    [TestMethod]
    [TestCategory("FR-145")]
    public void TheRuleVersionMatchesTheRules()
    {
        var decisions = new System.Text.StringBuilder();
        foreach (double seconds in new[] { 60.0, 90, 91, 300, 600, 601, 1200 })
        {
            foreach ((int width, int height) in new[] { (1920, 1080), (1080, 1920) })
            {
                foreach (int? speakers in new int?[] { null, 1, 2, 3 })
                {
                    foreach (double? pace in new double?[] { null, 100, 140, 141, 299, 300, 400 })
                    {
                        foreach (int portraits in new[] { 0, 1 })
                        {
                            TemplateChoice? choice = TemplateAutoSelector.Select(new TemplateAnalysisInput(seconds, width, height, true, speakers, pace), portraits);
                            _ = decisions.Append(choice?.TemplateId ?? "-").Append(';');
                        }
                    }
                }
            }
        }

        string fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(decisions.ToString())))[..16];
        var pinned = new Dictionary<int, string> { [2] = "15DFF7B4C188C88C" };
        Assert.IsTrue(pinned.ContainsKey(TemplateAutoSelector.RuleVersion), "RuleVersion has no pinned fingerprint: add it with the rules' fingerprint " + fingerprint);
        Assert.AreEqual(pinned[TemplateAutoSelector.RuleVersion], fingerprint, "The A-008 rules changed: raise TemplateAutoSelector.RuleVersion and pin the new fingerprint.");
    }

    private static bool Parse(string method, string parameters)
    {
        string json = $$"""{"version":1,"requestId":"{{Guid.NewGuid():D}}","method":"{{method}}","params":{{parameters}}}""";
        return BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json, out _, out _);
    }
}
