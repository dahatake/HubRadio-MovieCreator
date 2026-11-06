using HubRadio.App.Projects;
using HubRadio.App.Rendering;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Jobs;
using HubRadio.Core.Projects;
using HubRadio.Core.Rendering;
using HubRadio.Core.Workflow;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class SourceOnlyGenerationWarningSummaryTests
{
    // FR-145 AC-189: the generator keeps one subtitle warning per affected cue, so a 10-minute video carries thousands of them (5786 measured).
    // The result records each warning kind once; otherwise a failed or finished generation became a malformed result (JsonException,
    // "reconciliation required") and the Job failed.
    [TestMethod]
    public void WarningsOfEveryCueAreRecordedAsTheirDistinctRoleAndCode()
    {
        StandardGenerationWarning[] generated = [.. Enumerable.Range(0, 5786).Select(index =>
            new StandardGenerationWarning("landscape-subtitles", index % 2 == 0 ? "SpeechOverlap" : "NoTrackLongEnough") { Key = "cue-" + index }),
            new StandardGenerationWarning("plan", "BlurredBackgroundFallback")];

        SourceOnlyResultGenerationWarning[] summary = SourceOnlyResultGenerationWarning.FromGenerated(generated);

        CollectionAssert.AreEqual(new[]
        {
            new SourceOnlyResultGenerationWarning("landscape-subtitles", "SpeechOverlap"),
            new SourceOnlyResultGenerationWarning("landscape-subtitles", "NoTrackLongEnough"),
            new SourceOnlyResultGenerationWarning("plan", "BlurredBackgroundFallback"),
        }, summary);
    }

    [TestMethod]
    public void ResultOfALongVideoWithThousandsOfCueWarningsIsAcceptedAndRawWarningsAreNot()
    {
        StandardGenerationWarning[] generated = [.. Enumerable.Range(0, 5786).Select(index =>
            new StandardGenerationWarning("landscape-subtitles", "SpeechOverlap") { Key = "cue-" + index })];
        var binding = new SourceOnlyResultBinding(EntityId<ProjectIdTag>.New(), EntityId<SourceOnlyExecutionIdTag>.New(),
            EntityId<JobIdTag>.New(), new ContentDigest(new string('1', 64)), EntityId<CompositionVersionIdTag>.New(), new ContentDigest(new string('2', 64)));
        SourceOnlyResultFailure[] failures = [new SourceOnlyResultFailure(SourceOnlyResultFailureCode.GenerationFailed)];

        SourceOnlyResult result = new(binding, StandardArtifactGenerationDisposition.Failed, [], [], failures, [], [], null, false,
            SourceOnlyResultGenerationWarning.FromGenerated(generated));

        Assert.AreEqual(1, result.GenerationWarnings.Count);
        Assert.Throws<System.Text.Json.JsonException>(() => new SourceOnlyResult(binding, StandardArtifactGenerationDisposition.Failed, [], [], failures,
            [], [], null, false, [.. generated.Select(static item => new SourceOnlyResultGenerationWarning(item.Role, item.Code))]));
    }
}
