using System.Text.RegularExpressions;
using HubRadio.App.StyleTemplates;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-145 AC-186 / AC-187 / AC-182, FR-054, NFR-REL-002: the time limits of the template auto-selection (PARAM-013 = 60 seconds as a whole,
/// ffprobe 20 seconds and the speech-facts wait 40 seconds inside it, 300 seconds of late display) have one source.
/// </summary>
[TestClass]
public sealed class TemplateAnalysisTimingsTests
{
    // FR-145 AC-186: the values follow the requirement (PARAM-013 = 60; ffprobe 20 is an inner share; 20 < 40 < 60).
    [TestMethod]
    [TestCategory("FR-145")]
    public void ValuesFollowParam013AndNestInsideIt()
    {
        int whole = TemplateAnalysisTimings.MaximumAnalysisSeconds;
        Assert.AreEqual(60, whole);
        Assert.AreEqual(TimeSpan.FromSeconds(60), TemplateAnalysisTimings.MaximumAnalysis);
        Assert.AreEqual(TimeSpan.FromSeconds(20), TemplateAnalysisTimings.Probe);
        Assert.AreEqual(TimeSpan.FromSeconds(40), TemplateAnalysisTimings.SpeechFactsWait);
        Assert.AreEqual(TimeSpan.FromSeconds(300), TemplateAnalysisTimings.LateGrace);
        Assert.IsTrue(TemplateAnalysisTimings.Probe < TemplateAnalysisTimings.SpeechFactsWait);
        Assert.IsTrue(TemplateAnalysisTimings.SpeechFactsWait < TemplateAnalysisTimings.MaximumAnalysis);
        Assert.IsTrue(TemplateAnalysisTimings.MaximumAnalysis < TemplateAnalysisTimings.LateGrace);
    }

    // FR-145 AC-187 / FR-054 AC-186: the code that uses the limits reads them from the one place and holds no literal of its own.
    [TestMethod]
    [TestCategory("FR-145")]
    public void TheUsersReadTheLimitsFromTheOnePlace()
    {
        string root = FindRoot();
        string app = Path.Combine(root, "src", "HubRadio.App");
        string[] users =
        [
            Path.Combine(app, "StyleTemplates", "TemplateAutoSelection.cs"),
            Path.Combine(app, "StyleTemplates", "TemplateSpeechFacts.cs"),
            Path.Combine(app, "Projects", "ProjectApplicationRuntime.cs"),
            Path.Combine(app, "Projects", "ProjectCreationService.cs"),
            Path.Combine(app, "Projects", "SourceOnlyPipeline.cs"),
        ];
        var literal = new Regex(@"FromSeconds\(\s*(20|40|60|300)\s*\)|(MaximumAnalysis|LateGrace|SpeechFactsWait)Seconds\s*=\s*\d");
        foreach (string file in users.Where(File.Exists))
        {
            Assert.IsFalse(literal.IsMatch(File.ReadAllText(file)), file + " holds its own PARAM-013 time literal");
        }

        string probe = File.ReadAllText(Path.Combine(app, "Projects", "BuzzEditingPreset.cs"));
        StringAssert.Contains(probe, "TemplateAnalysisTimings.Probe");
        StringAssert.Contains(File.ReadAllText(users[2]), "TemplateAnalysisTimings.SpeechFactsWait");
        StringAssert.Contains(File.ReadAllText(users[0]), "TemplateAnalysisTimings.MaximumAnalysis");
        StringAssert.Contains(File.ReadAllText(users[0]), "TemplateAnalysisTimings.LateGrace");
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("HubRadio.slnx not found.");
    }
}
