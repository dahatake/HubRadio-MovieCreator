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
    [DataRow("editing.longOpenerEnabled")]
    [DataRow("editing.shortOpenerEnabled")]
    public async Task FR015_AC063_EnablingAnOpenerIsNotSilentlyIgnored(string key)
    {
        using StandardGenerationFixture fixture = await StandardGenerationFixture.CreateAsync(settingsOverrides:
            [new DefaultSettingSelection(key, DefaultSettingValue.FromBoolean(true))]);
        Assert.ThrowsExactly<StandardGenerationException>(() => fixture.Request());
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
