using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Media;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class AssetCandidateRegistryTests
{
    [TestMethod]
    public void RegisterSelection_Cancel_ReturnsNullWithoutCreatingCandidate()
    {
        AssetCandidateRegistry registry = new();

        EntityId<AssetIdTag>? candidateId = registry.RegisterSelection(null);

        Assert.IsNull(candidateId);
        Assert.AreEqual(0, registry.PendingCount);
    }

    [TestMethod]
    public void RegisterSelection_Path_ReturnsOpaqueTypedCandidate()
    {
        const string selectedPath = @"C:\private\recording.mov";
        AssetCandidateRegistry registry = new();

        EntityId<AssetIdTag>? candidateId = registry.RegisterSelection(selectedPath);

        Assert.IsNotNull(candidateId);
        Assert.AreNotEqual(Guid.Empty, candidateId.Value);
        Assert.AreEqual(candidateId.ToString(), EntityId<AssetIdTag>.Parse(candidateId.ToString()).ToString());
        Assert.IsFalse(candidateId.ToString().Contains("recording", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, registry.PendingCount);
    }

    [TestMethod]
    public void TryClaim_RegisteredCandidate_ReturnsPathExactlyOnce()
    {
        const string selectedPath = @"C:\media\source.mov";
        AssetCandidateRegistry registry = new();
        EntityId<AssetIdTag> candidateId = registry.RegisterSelection(selectedPath)
            ?? throw new AssertFailedException("A selected path must create a candidate.");

        bool firstClaim = registry.TryClaim(candidateId, out string? firstPath);
        bool secondClaim = registry.TryClaim(candidateId, out string? secondPath);

        Assert.IsTrue(firstClaim);
        Assert.AreEqual(selectedPath, firstPath);
        Assert.IsFalse(secondClaim);
        Assert.IsNull(secondPath);
        Assert.AreEqual(0, registry.PendingCount);
    }

    [TestMethod]
    public async Task TryClaim_ConcurrentCalls_HasExactlyOneWinner()
    {
        const string selectedPath = @"C:\media\concurrent.mov";
        AssetCandidateRegistry registry = new();
        EntityId<AssetIdTag> candidateId = registry.RegisterSelection(selectedPath)
            ?? throw new AssertFailedException("A selected path must create a candidate.");

        ClaimResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => Claim(registry, candidateId))));

        ClaimResult[] winners = results.Where(static result => result.Succeeded).ToArray();
        Assert.HasCount(1, winners);
        ClaimResult winner = winners[0];
        Assert.AreEqual(selectedPath, winner.Path);
        Assert.IsTrue(results.Where(static result => !result.Succeeded).All(static result => result.Path is null));
        Assert.AreEqual(0, registry.PendingCount);
    }

    [TestMethod]
    public void RegisterSelection_SamePathTwice_CreatesIndependentCandidates()
    {
        const string selectedPath = @"C:\media\duplicate.mov";
        AssetCandidateRegistry registry = new();
        EntityId<AssetIdTag> first = registry.RegisterSelection(selectedPath)
            ?? throw new AssertFailedException("A selected path must create a candidate.");
        EntityId<AssetIdTag> second = registry.RegisterSelection(selectedPath)
            ?? throw new AssertFailedException("A selected path must create a candidate.");

        Assert.AreNotEqual(first, second);
        Assert.IsTrue(registry.TryClaim(first, out string? firstPath));
        Assert.IsTrue(registry.TryClaim(second, out string? secondPath));
        Assert.AreEqual(selectedPath, firstPath);
        Assert.AreEqual(selectedPath, secondPath);
    }

    [TestMethod]
    public void RegisterSelection_UnverifiedPath_DoesNotInspectOrNormalizeFile()
    {
        string selectedPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-{Guid.NewGuid():N}.unsupported");
        Assert.IsFalse(File.Exists(selectedPath));
        AssetCandidateRegistry registry = new();

        EntityId<AssetIdTag> candidateId = registry.RegisterSelection(selectedPath)
            ?? throw new AssertFailedException("A selected path must create a candidate.");

        Assert.IsTrue(registry.TryClaim(candidateId, out string? claimedPath));
        Assert.AreEqual(selectedPath, claimedPath);
        Assert.IsFalse(File.Exists(selectedPath));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public void RegisterSelection_BlankPath_IsRejectedWithoutCreatingCandidate(string selectedPath)
    {
        AssetCandidateRegistry registry = new();

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => registry.RegisterSelection(selectedPath));

        Assert.AreEqual("selectedPath", exception.ParamName);
        Assert.AreEqual(0, registry.PendingCount);
    }

    [TestMethod]
    public void TryClaim_NullCandidate_IsRejectedWithoutChangingState()
    {
        AssetCandidateRegistry registry = new();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => registry.TryClaim(null!, out _));

        Assert.AreEqual(0, registry.PendingCount);
    }

    [TestMethod]
    public void TryClaim_UnknownCandidate_FailsWithoutReturningPath()
    {
        AssetCandidateRegistry registry = new();

        bool claimed = registry.TryClaim(
            EntityId<AssetIdTag>.New(),
            out string? selectedPath);

        Assert.IsFalse(claimed);
        Assert.IsNull(selectedPath);
    }

    private static ClaimResult Claim(
        AssetCandidateRegistry registry,
        EntityId<AssetIdTag> candidateId) =>
        registry.TryClaim(candidateId, out string? selectedPath)
            ? new ClaimResult(true, selectedPath)
            : new ClaimResult(false, null);

    private sealed record ClaimResult(bool Succeeded, string? Path);
}