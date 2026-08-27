using System.Reflection;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class AssemblySmokeTests
{
    [TestMethod]
    public void InfrastructureAssemblyLoads()
    {
        Assembly assembly = Assembly.Load("HubRadio.Infrastructure");

        Assert.AreEqual("HubRadio.Infrastructure", assembly.GetName().Name);
    }
}
