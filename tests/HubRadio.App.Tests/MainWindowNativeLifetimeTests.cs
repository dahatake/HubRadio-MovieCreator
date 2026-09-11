using HubRadio.App.Projects;

namespace HubRadio.App.Tests;

/// <summary>Pure close-order tests, not native startup/UX-041/UX-043 acceptance.</summary>
[TestClass]
public sealed class MainWindowNativeLifetimeTests
{
    [TestMethod]
    public async Task LastWindowDoesNotCloseBeforeOwnedShutdownFinishes()
    {
        var stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int closes = 0;
        Task<string?> completion = SourceOnlyApplicationShutdown.CloseAfterStopAsync(stopped.Task, () => closes++);
        Assert.IsFalse(completion.IsCompleted);
        Assert.AreEqual(0, closes);

        stopped.SetResult(null);
        Assert.IsNull(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, closes);
    }

    [TestMethod]
    public async Task ObservedShutdownFailureStillClosesAndRetainsOnlyAStableCode()
    {
        int closes = 0;
        string? failure = await SourceOnlyApplicationShutdown.CloseAfterStopAsync(
            Task.FromException<string?>(new IOException(@"C:\private\cleanup")), () => closes++);
        Assert.AreEqual(1, closes);
        Assert.AreEqual("source_only.shutdown_failed", failure);
        Assert.AreEqual("source_only.window_close_failed", await SourceOnlyApplicationShutdown.CloseAfterStopAsync(
            Task.FromResult<string?>(null), () => throw new InvalidOperationException("private native detail")));
    }

    [TestMethod]
    public async Task DisposalCancellationIsNotMistakenForCanceledInitialization()
    {
        Assert.AreEqual("source_only.shutdown_failed", await SourceOnlyApplicationShutdown.StopAsync(
            Task.FromResult(new CanceledDisposal()), () => Task.CompletedTask));
        Assert.IsNull(await SourceOnlyApplicationShutdown.StopAsync(
            Task.FromCanceled<CanceledDisposal>(new CancellationToken(canceled: true)), () => Task.CompletedTask));
    }

    private sealed class CanceledDisposal : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(Task.FromCanceled(new CancellationToken(canceled: true)));
    }
}