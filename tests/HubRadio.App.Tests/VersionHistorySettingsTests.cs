using HubRadio.App.Projects;
using HubRadio.Core.Compositions;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class VersionHistorySettingsTests
{
    // FR-055 AC-030.1: the list keeps only the settings that differ between the listed videos.
    [TestMethod]
    public void KeepsOnlySettingsThatDifferBetweenTheListedVideos()
    {
        SourceOnlyVersionHistoryEntry Entry(long speed, string color, bool includeHook) => new(
            EntityId<CompositionVersionIdTag>.New(), ContentDigest.Compute([1]), null, EntityId<SourceOnlyExecutionIdTag>.New(),
            SourceOnlyExecutionPhase.Completed, true, false, DateTimeOffset.UnixEpoch,
            [
                new DefaultSettingSelection("subtitles.readingSpeedMaximum", DefaultSettingValue.FromInteger(speed)),
                new DefaultSettingSelection("subtitles.foregroundColor", DefaultSettingValue.FromText(color)),
                .. includeHook ? new[] { new DefaultSettingSelection("editing.shortHookMaximumStart", DefaultSettingValue.FromInteger(2)) } : [],
            ], []);

        IReadOnlyList<SourceOnlyVersionHistoryEntry> kept = SourceOnlyResultService.KeepDifferingSettings(
            [Entry(9, "#FFFFFF", includeHook: true), Entry(7, "#FFFFFF", includeHook: false)]);

        CollectionAssert.AreEqual(new[] { "editing.shortHookMaximumStart", "subtitles.readingSpeedMaximum" },
            kept[0].Settings!.Select(static item => item.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "subtitles.readingSpeedMaximum" },
            kept[1].Settings!.Select(static item => item.Key).ToArray());
        Assert.AreEqual(9L, kept[0].Settings![1].Value.IntegerValue);

        IReadOnlyList<SourceOnlyVersionHistoryEntry> single = SourceOnlyResultService.KeepDifferingSettings(
            [Entry(9, "#FFFFFF", includeHook: true)]);
        Assert.HasCount(0, single[0].Settings!);
        Assert.HasCount(0, SourceOnlyResultService.KeepDifferingSettings([]));
    }
}
