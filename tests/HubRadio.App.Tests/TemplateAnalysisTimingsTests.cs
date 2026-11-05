using System.Text.RegularExpressions;
using HubRadio.App.StyleTemplates;

namespace HubRadio.App.Tests;

/// <summary>
/// FR-145 AC-186 / AC-187 / AC-182, FR-054, NFR-REL-002: the time limits of the template auto-selection (PARAM-013 = 60 seconds for the provisional selection,
/// ffprobe 20 seconds inside it, the speech-facts wait = length × PARAM-014 + PARAM-015, 300 seconds of late display) have one source.
/// </summary>
[TestClass]
public sealed class TemplateAnalysisTimingsTests
{
    // FR-145 AC-186: the values follow the requirement (PARAM-013 = 60; ffprobe 20 is an inner share; PARAM-014 = 1, PARAM-015 = 60).
    [TestMethod]
    [TestCategory("FR-145")]
    public void ValuesFollowTheParamsAndNestInsideThem()
    {
        int whole = TemplateAnalysisTimings.MaximumAnalysisSeconds;
        Assert.AreEqual(60, whole);
        Assert.AreEqual(TimeSpan.FromSeconds(60), TemplateAnalysisTimings.MaximumAnalysis);
        Assert.AreEqual(TimeSpan.FromSeconds(20), TemplateAnalysisTimings.Probe);
        Assert.AreEqual(TimeSpan.FromSeconds(1), TemplateAnalysisTimings.SpeechFactsWaitFor(1) - TemplateAnalysisTimings.SpeechFactsWaitFor(0)); // PARAM-014 = 1
        Assert.AreEqual(TimeSpan.FromSeconds(60), TemplateAnalysisTimings.SpeechFactsWaitFor(0)); // PARAM-015 = 60
        Assert.AreEqual(TimeSpan.FromSeconds(300), TemplateAnalysisTimings.LateGrace);
        // AC-186: the limits for 90 / 600 / 1200 second videos.
        Assert.AreEqual(TimeSpan.FromSeconds(150), TemplateAnalysisTimings.SpeechFactsWaitFor(90));
        Assert.AreEqual(TimeSpan.FromSeconds(660), TemplateAnalysisTimings.SpeechFactsWaitFor(600));
        Assert.AreEqual(TimeSpan.FromSeconds(1260), TemplateAnalysisTimings.SpeechFactsWaitFor(1200));
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
        var literal = new Regex(@"FromSeconds\(\s*(20|60|300|660|1260)\s*\)|(MaximumAnalysis|LateGrace|SpeechFactsWaitExtra)Seconds\s*=\s*\d");
        foreach (string file in users.Where(File.Exists))
        {
            Assert.IsFalse(literal.IsMatch(File.ReadAllText(file)), file + " holds its own PARAM-013/014/015 time literal");
        }

        string probe = File.ReadAllText(Path.Combine(app, "Projects", "BuzzEditingPreset.cs"));
        StringAssert.Contains(probe, "TemplateAnalysisTimings.Probe");
        StringAssert.Contains(File.ReadAllText(users[1]), "TemplateAnalysisTimings.SpeechFactsWaitFor");
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
