using HubRadio.App.Web;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class MainWindowStartupLifecycleTests
{
    [TestMethod]
    public void TryStart_AllowsExactlyOneStartup()
    {
        MainWindowStartupLifecycle lifecycle = new();

        Assert.IsTrue(lifecycle.TryStart());
        Assert.IsFalse(lifecycle.TryStart());
        Assert.IsTrue(lifecycle.CanUpdateUi);
    }

    [TestMethod]
    public void Close_BeforeStartupPreventsStartupAndUiUpdates()
    {
        MainWindowStartupLifecycle lifecycle = new();

        lifecycle.Close();

        Assert.IsFalse(lifecycle.TryStart());
        Assert.IsFalse(lifecycle.CanUpdateUi);
    }

    [TestMethod]
    public void Close_DuringStartupPreventsCompletionUiUpdates()
    {
        MainWindowStartupLifecycle lifecycle = new();
        Assert.IsTrue(lifecycle.TryStart());

        lifecycle.Close();

        Assert.IsFalse(lifecycle.CanUpdateUi);
        Assert.IsFalse(lifecycle.TryStart());
    }
}
