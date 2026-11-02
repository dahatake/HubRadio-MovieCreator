using HubRadio.App.Compositions;
using HubRadio.App.Media;
using HubRadio.App.Rendering;
using HubRadio.Core.Identity;
using HubRadio.Core.Compositions;
using HubRadio.Core.Provenance;
using HubRadio.Core.Rendering;
using HubRadio.Core.Settings;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class ShortFormOpenerAndProposalTests
{
    // FR-015 / AC-063: quick start has no opener video; long uses a short title overlay; short starts straight away.
    [TestMethod]
    public async Task FR015_AC063_QuickStartHasNoOpenerAndOpenersDefaultToDisabled()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
        foreach (string key in new[] { "editing.longOpenerEnabled", "editing.shortOpenerEnabled" })
        {
            DefaultSettingValue value = fixture.Settings.Values.Single(item => item.Key == key).Value;
            Assert.IsFalse(value.BooleanValue, key);
        }

        StandardRenderGraphOutput longGraph = fixture.Graph(false);
        StandardGraphicRenderFile title = longGraph.GraphicFiles.Single();
        Assert.AreEqual("EpisodeTitle", title.Evidence.Role);
        Assert.AreEqual(StandardGenerationFixture.Range(0, 3), title.Evidence.OutputRange);

        StandardShortArtifactInput input = fixture.Shorts.Single();
        StandardRenderGraphOutput shortGraph = StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset,
            fixture.Geometry, true, input.Subtitles, fixture.Font.Evidence, input.ShortFormId, fixture.Request().Configuration);
        // The short keeps only the existing metadata overlay on top of the source; no opener segment is inserted before it.
        Assert.IsFalse(shortGraph.GraphicFiles.Any(static item => item.Evidence.Role is "Opening" or "Opener"));
    }

    [TestMethod]
    [DataRow("editing.longOpenerEnabled", false)]
    [DataRow("editing.shortOpenerEnabled", true)]
    public async Task FR015_AC063_EnablingAnOpenerWithoutContentIsNotSilentlyIgnored(string key, bool shortForm)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true, settingsOverrides:
            [new DefaultSettingSelection(key, DefaultSettingValue.FromBoolean(true))]);
        StandardArtifactGenerationRequest request = fixture.Request();
        Assert.IsTrue(shortForm ? request.Configuration.ShortOpenerEnabled : request.Configuration.LongOpenerEnabled);
        Assert.ThrowsExactly<StandardGenerationException>(() => Create(fixture, request, shortForm));
    }

    // FR-015 / AC-063: once enabled, the long template shows program/episode/title/performers/scope/logo and plays the jingle.
    [TestMethod]
    public async Task FR015_AC063_EnabledLongOpenerShowsPermittedContent()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true, settingsOverrides:
            [new DefaultSettingSelection("editing.longOpenerEnabled", DefaultSettingValue.FromBoolean(true))]);
        StandardArtifactGenerationRequest request = fixture.Request();
        string jingle = Path.Combine(Path.GetTempPath(), "Music", "jingle.mp3");
        request.UseOpener(new StandardOpenerInput("番組A", "第3回", "今日の話", ["山田", "佐藤"], "社内限定",
            new StandardOpenerLogo("ロゴX", Permitted: true),
            new StandardOpenerJingle("ジングル", new HubRadio.Infrastructure.Integrity.BinaryVerificationRequest(
                "standard.jingle", jingle, new ContentDigest(new string('a', 64)), 10), Permitted: true)));
        StandardRenderGraphOutput graph = Create(fixture, request, shortForm: false);
        string[] lines = graph.GraphicFiles.Where(static file => file.Evidence.Role == "Opening")
            .Select(static file => file.Evidence.Text).ToArray();
        CollectionAssert.AreEqual(new[] { "ロゴX 番組A 第3回", "今日の話", "山田、佐藤", "社内限定" }, lines);
        Assert.IsTrue(graph.GraphicFiles.Any(static file => file.Evidence.Role == "EpisodeTitle"));
        Assert.HasCount(1, graph.JingleBindings);
        Assert.AreEqual(graph.GraphicFiles.First(static file => file.Evidence.Role == "Opening").Evidence.OutputRange,
            graph.JingleBindings[0].OutputRange);
        // The shorts are not affected by the long opener setting.
        Assert.IsFalse(Create(fixture, request, shortForm: true).GraphicFiles.Any(static file => file.Evidence.Role == "Opening"));
    }

    // FR-015 / AC-063: the short opener is a simplified template (program + title, no logo/performers/jingle).
    [TestMethod]
    public async Task FR015_AC063_EnabledShortOpenerIsSimplifiedAndUnpermittedAssetsAreRejected()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true, settingsOverrides:
            [new DefaultSettingSelection("editing.shortOpenerEnabled", DefaultSettingValue.FromBoolean(true))]);
        StandardArtifactGenerationRequest request = fixture.Request();
        request.UseOpener(new StandardOpenerInput("番組A", null, "今日の話", ["山田"], "社内限定",
            new StandardOpenerLogo("ロゴX", Permitted: true), null));
        StandardRenderGraphOutput graph = Create(fixture, request, shortForm: true);
        string[] lines = graph.GraphicFiles.Where(static file => file.Evidence.Role == "Opening")
            .Select(static file => file.Evidence.Text).ToArray();
        CollectionAssert.AreEqual(new[] { "番組A", "今日の話" }, lines);
        Assert.IsEmpty(graph.JingleBindings);
        Assert.IsFalse(Create(fixture, request, shortForm: false).GraphicFiles.Any(static file => file.Evidence.Role == "Opening"));

        request.UseOpener(new StandardOpenerInput("番組A", null, "今日の話", [], null, new StandardOpenerLogo("未許可", Permitted: false), null));
        Assert.ThrowsExactly<StandardGenerationException>(() => Create(fixture, request, shortForm: true));
    }

    private static StandardRenderGraphOutput Create(StandardGenerationFixture fixture, StandardArtifactGenerationRequest request, bool shortForm)
    {
        StandardShortArtifactInput? input = shortForm ? fixture.Shorts.Single() : null;
        return StandardRenderGraphFactory.Create(fixture.Preparation, fixture.Asset, fixture.Geometry, true,
            input?.Subtitles ?? fixture.Subtitles, fixture.Font.Evidence, input?.ShortFormId, request.Configuration);
    }

    private static OriginRef Origin() => OriginRef.FromSourceSpan(EntityId<SourceSpanIdTag>.New());

    // FR-023 / AC-071: ten types are proposed; ranking only with grounded evidence.
    [TestMethod]
    public void FR023_AC071_TenTypesProposedWhenRankingIsGrounded()
    {
        OriginRef[] origins = [Origin()];
        ShortFormRankingEvidence[] ranking = [new(1, "A", Origin()), new(2, "B", Origin())];
        IReadOnlyList<ShortFormProposal> proposals = ShortFormProposalPlanner.Propose(new ShortFormProposalInput(origins, ranking));
        Assert.HasCount(10, proposals);
        CollectionAssert.AreEquivalent(Enum.GetValues<ShortFormCompositionType>(), proposals.Select(static p => p.Type).ToArray());
        Assert.HasCount(2, proposals.Single(static p => p.Type == ShortFormCompositionType.Ranking).RankingItems);
    }

    [TestMethod]
    public void FR023_AC071_NoRankingProposalWithoutGroundedEvidence()
    {
        OriginRef[] origins = [Origin()];
        foreach (ShortFormRankingEvidence[] weak in new[]
        {
            Array.Empty<ShortFormRankingEvidence>(),
            [new ShortFormRankingEvidence(1, "only one", Origin())],
            [new ShortFormRankingEvidence(1, "dup", Origin()), new ShortFormRankingEvidence(1, "dup2", Origin())],
            [new ShortFormRankingEvidence(0, "zero", Origin()), new ShortFormRankingEvidence(-1, "neg", Origin())],
        })
        {
            IReadOnlyList<ShortFormProposal> proposals = ShortFormProposalPlanner.Propose(new ShortFormProposalInput(origins, weak));
            Assert.HasCount(9, proposals);
            Assert.IsFalse(proposals.Any(static p => p.Type == ShortFormCompositionType.Ranking));
        }

        Assert.IsEmpty(ShortFormProposalPlanner.Propose(new ShortFormProposalInput([], [])));
    }

    // FR-023 / AC-071: the orchestrator's preparation carries the proposals, grounded in the plan's own source spans (no invented ranking).
    [TestMethod]
    public async Task FR023_AC071_PreparationCarriesGroundedProposals()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
        IReadOnlyList<ShortFormProposal> proposals = fixture.Preparation.ShortFormProposals;
        Assert.HasCount(9, proposals);
        Assert.IsFalse(proposals.Any(static p => p.Type == ShortFormCompositionType.Ranking));
        OriginRef[] spans = fixture.Preparation.Plan.LongForm.Provenance.SourceSpans
            .Select(static span => OriginRef.FromSourceSpan(span.Id)).ToArray();
        Assert.IsTrue(proposals.All(p => p.Origins.SequenceEqual(spans)));
    }

    [TestMethod]
    public async Task FR023_AC071_ProposalsFromProcessedShortFormSourceAreGroundedInItsSpans()
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(includeShort: true);
        OriginRef[] origins = fixture.Spans.Select(static span => OriginRef.FromSourceSpan(span.Id)).ToArray();
        IReadOnlyList<ShortFormProposal> proposals = ShortFormProposalPlanner.Propose(new ShortFormProposalInput(origins, []));
        Assert.IsGreaterThan(1, proposals.Count);
        Assert.IsFalse(proposals.Any(static p => p.Type == ShortFormCompositionType.Ranking));
        Assert.IsTrue(proposals.All(p => p.Origins.SequenceEqual(origins)));
    }
}
