using System.Reflection;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class AssemblySmokeTests
{
    [TestMethod]
    public void CoreAssemblyLoads()
    {
        Assembly assembly = Assembly.Load("HubRadio.Core");

        Assert.AreEqual("HubRadio.Core", assembly.GetName().Name);
    }
}
