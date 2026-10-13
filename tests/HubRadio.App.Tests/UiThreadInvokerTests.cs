using HubRadio.App.Media;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class UiThreadInvokerTests
{
    // FR-061 AC-036: the file dialog is opened on the UI thread even when the bridge handler continues on a pool thread.
    [TestMethod]
    public async Task RunAsync_OnUiThread_RunsWorkDirectlyWithoutEnqueue()
    {
        bool enqueued = false;

        string result = await UiThreadInvoker.RunAsync(
            true,
            _ => { enqueued = true; return true; },
            () => Task.FromResult("direct"));

        Assert.AreEqual("direct", result);
        Assert.IsFalse(enqueued);
    }

    [TestMethod]
    public async Task RunAsync_OffUiThread_RunsWorkInsideTheEnqueuedCallback()
    {
        Action? queued = null;
        bool workRan = false;

        Task<int> task = UiThreadInvoker.RunAsync(
            false,
            callback => { queued = callback; return true; },
            () => { workRan = true; return Task.FromResult(42); });

        Assert.IsFalse(workRan, "work must not run on the calling (non-UI) thread");
        Assert.IsNotNull(queued);
        queued();
        Assert.AreEqual(42, await task);
        Assert.IsTrue(workRan);
    }

    [TestMethod]
    public async Task RunAsync_OffUiThread_PropagatesExceptionsToTheCaller()
    {
        Task<int> task = UiThreadInvoker.RunAsync<int>(
            false,
            callback => { callback(); return true; },
            () => throw new InvalidOperationException("boom"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task);
    }

    [TestMethod]
    public async Task RunAsync_WhenQueueRejects_FailsInsteadOfHanging()
    {
        Task<int> task = UiThreadInvoker.RunAsync(
            false,
            _ => false,
            () => Task.FromResult(1));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task);
    }
}
