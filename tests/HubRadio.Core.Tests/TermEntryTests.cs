using System.Reflection;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Templates;
using HubRadio.Core.Terms;

namespace HubRadio.Core.Tests;

[TestClass]
public sealed class TermEntryTests
{
    [TestMethod]
    public void TermScopeKindDefinesExactlyProjectAndSeries()
    {
        CollectionAssert.AreEqual(
            new[] { TermScopeKind.Project, TermScopeKind.Series },
            Enum.GetValues<TermScopeKind>());
        Assert.AreEqual(0, typeof(TermScope).GetConstructors().Length);
    }

    [TestMethod]
    public void ProjectAndSeriesFactoriesCreateExclusiveTypedScopes()
    {
        EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
        EntityId<SeriesIdTag> seriesId = EntityId<SeriesIdTag>.New();

        TermScope project = TermScope.ForProject(projectId);
        TermScope series = TermScope.ForSeries(seriesId);

        Assert.AreEqual(TermScopeKind.Project, project.Kind);
        Assert.AreEqual(projectId, project.ProjectId);
        Assert.IsNull(project.SeriesId);
        Assert.AreEqual(TermScopeKind.Series, series.Kind);
        Assert.AreEqual(seriesId, series.SeriesId);
        Assert.IsNull(series.ProjectId);
    }

    [TestMethod]
    public void EquivalentScopesHaveValueEqualityWithoutCrossKindAliasing()
    {
        Guid sharedValue = Guid.NewGuid();
        TermScope firstProject = TermScope.ForProject(
            EntityId<ProjectIdTag>.From(sharedValue));
        TermScope equivalentProject = TermScope.ForProject(
            EntityId<ProjectIdTag>.From(sharedValue));
        TermScope seriesWithSameGuid = TermScope.ForSeries(
            EntityId<SeriesIdTag>.From(sharedValue));

        Assert.AreEqual(firstProject, equivalentProject);
        Assert.IsTrue(firstProject == equivalentProject);
        Assert.IsFalse(firstProject != equivalentProject);
        Assert.AreEqual(firstProject.GetHashCode(), equivalentProject.GetHashCode());
        Assert.AreNotEqual(firstProject, seriesWithSameGuid);
        Assert.IsTrue(firstProject != seriesWithSameGuid);
        Assert.IsFalse(firstProject.Equals(null));
        Assert.IsTrue((TermScope?)null == null);
    }

    [TestMethod]
    public void ScopeFactoriesRejectNullTypedIds()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => TermScope.ForProject(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => TermScope.ForSeries(null!));
    }

    [TestMethod]
    public void ConstructorPreservesTypedScopeTextAndVersionWithoutNormalization()
    {
        EntityId<TermEntryIdTag> id = EntityId<TermEntryIdTag>.New();
        TermScope scope = TermScope.ForSeries(EntityId<SeriesIdTag>.New());

        var entry = new TermEntry(id, scope, "GitHub Copilot", "ギットハブ コパイロット", 7);

        Assert.AreEqual(id, entry.Id);
        Assert.AreSame(scope, entry.Scope);
        Assert.AreEqual("GitHub Copilot", entry.WrittenForm);
        Assert.AreEqual("ギットハブ コパイロット", entry.Reading);
        Assert.AreEqual(7, entry.Version);
        Assert.IsTrue(typeof(TermEntry).IsSealed);
        Assert.IsTrue(
            typeof(TermEntry)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .All(static property => property.SetMethod is null));
    }

    [TestMethod]
    public void ConstructorAcceptsInitialZeroVersionAndSubstantiveUnicodeTerms()
    {
        TermScope scope = TermScope.ForProject(EntityId<ProjectIdTag>.New());

        var symbol = new TermEntry(
            EntityId<TermEntryIdTag>.New(),
            scope,
            "C++™",
            "シープラスプラス",
            version: 0);
        var supplementary = new TermEntry(
            EntityId<TermEntryIdTag>.New(),
            scope,
            "動画🎬",
            "どうが",
            version: int.MaxValue);

        Assert.AreEqual(0, symbol.Version);
        Assert.AreEqual("動画🎬", supplementary.WrittenForm);
        Assert.AreEqual(int.MaxValue, supplementary.Version);
    }

    [TestMethod]
    public void ConstructorRejectsNullIdentityScopeAndText()
    {
        EntityId<TermEntryIdTag> id = EntityId<TermEntryIdTag>.New();
        TermScope scope = TermScope.ForProject(EntityId<ProjectIdTag>.New());

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TermEntry(null!, scope, "表記", "よみ", 0));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new TermEntry(id, null!, "表記", "よみ", 0));
        Assert.AreEqual(
            "writtenForm",
            Assert.ThrowsExactly<ArgumentNullException>(
                () => new TermEntry(id, scope, null!, "よみ", 0)).ParamName);
        Assert.AreEqual(
            "reading",
            Assert.ThrowsExactly<ArgumentNullException>(
                () => new TermEntry(id, scope, "表記", null!, 0)).ParamName);
    }

    [TestMethod]
    [DataRow("", "よみ", "writtenForm")]
    [DataRow(" ", "よみ", "writtenForm")]
    [DataRow(" 表記", "よみ", "writtenForm")]
    [DataRow("表記 ", "よみ", "writtenForm")]
    [DataRow("表記", "", "reading")]
    [DataRow("表記", "\t", "reading")]
    [DataRow("表記", " よみ", "reading")]
    [DataRow("表記", "よみ\u3000", "reading")]
    public void ConstructorRejectsBlankOrBoundaryWhitespace(
        string writtenForm,
        string reading,
        string expectedParameter)
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => new TermEntry(
                EntityId<TermEntryIdTag>.New(),
                TermScope.ForProject(EntityId<ProjectIdTag>.New()),
                writtenForm,
                reading,
                version: 0));

        Assert.AreEqual(expectedParameter, exception.ParamName);
    }

    [TestMethod]
    public void ConstructorRejectsMalformedOrNonSubstantiveUnicodeAndControls()
    {
        TermScope scope = TermScope.ForSeries(EntityId<SeriesIdTag>.New());
        string[] invalidWrittenForms =
        [
            "bad\uD800",
            "\u0301",
            "\u200D\uFE0F",
            "term\0hidden",
            "term\nother",
        ];

        foreach (string value in invalidWrittenForms)
        {
            ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
                () => new TermEntry(
                    EntityId<TermEntryIdTag>.New(),
                    scope,
                    value,
                    "よみ",
                    version: 0));
            Assert.AreEqual("writtenForm", exception.ParamName);
            Assert.IsFalse(exception.Message.Contains(value, StringComparison.Ordinal));
        }

        ArgumentException invalidReading = Assert.ThrowsExactly<ArgumentException>(
            () => new TermEntry(
                EntityId<TermEntryIdTag>.New(),
                scope,
                "表記",
                "bad\uDFFF",
                version: 0));
        Assert.AreEqual("reading", invalidReading.ParamName);
    }

    [TestMethod]
    public void ConstructorRejectsNegativeVersion()
    {
        ArgumentOutOfRangeException exception =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new TermEntry(
                    EntityId<TermEntryIdTag>.New(),
                    TermScope.ForProject(EntityId<ProjectIdTag>.New()),
                    "表記",
                    "よみ",
                    version: -1));

        Assert.AreEqual("version", exception.ParamName);
    }
}