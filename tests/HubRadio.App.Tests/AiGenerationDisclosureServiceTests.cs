using HubRadio.App.Audit;
using HubRadio.Core.Analysis;
using HubRadio.Core.Audit;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Reviews;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class AiGenerationDisclosureServiceTests
{
    private const string Actor = "S-1-5-21-1000";

    private static readonly DateTimeOffset At =
        new(2033, 4, 5, 6, 7, 8, TimeSpan.FromHours(9));

    [TestMethod]
    // NFR-BR-005 AC-126: AI で生成・大幅に編集した要素が記録され、一覧で確認できる。
    public void AiElementsAreRecordedAndListed()
    {
        using Fixture f = new();
        AiGeneratedElementKind[] kinds = Enum.GetValues<AiGeneratedElementKind>();
        foreach ((AiGeneratedElementKind kind, int i) in kinds.Select((k, i) => (k, i)))
        {
            AuditEvent e = f.Service.RecordAiElement(
                EntityId<AuditEventIdTag>.New(), f.Project.Id, kind, $"element-{i}",
                CreateModelRun(), Actor, ReviewActorRole.Editor, At.AddMinutes(i));
            Assert.AreEqual(AuditEventKind.AiGeneratedElementRecorded, e.Kind);
        }

        IReadOnlyList<AiGeneratedElementRecord> listed = f.Service.ListAiElements(f.Project.Id);
        CollectionAssert.AreEqual(kinds, listed.Select(static r => r.Kind).ToArray());
        Assert.AreEqual("element-1", listed[1].ElementRef);
        Assert.AreEqual("local-model", listed[0].ModelIdentifier);
        Assert.AreEqual("1.2.3", listed[0].ModelVersion);
        Assert.AreEqual(At.AddMinutes(2), listed[2].RecordedAt);
    }

    [TestMethod]
    // NFR-BR-005 AC-126: 公開先のルールに応じたラベルを追加でき、追加したものが成果物に反映される。
    public void DestinationLabelIsReflectedIntoDescriptionAndArtifact()
    {
        using Fixture f = new();
        _ = f.Service.RecordAiElement(
            EntityId<AuditEventIdTag>.New(), f.Project.Id,
            AiGeneratedElementKind.AutoSubtitle, "subtitle-track",
            CreateModelRun(), Actor, ReviewActorRole.Editor, At);
        _ = f.Service.AddDestinationLabel(
            EntityId<AuditEventIdTag>.New(), f.Project.Id, "YouTube",
            "この動画には AI で生成した字幕が含まれます", Actor,
            ReviewActorRole.Editor, At.AddMinutes(1));

        Assert.AreEqual(1, f.Service.ListLabels(f.Project.Id).Count);
        AiDisclosure youtube = f.Service.BuildDisclosure(f.Project.Id, "youtube");
        Assert.IsTrue(youtube.HasContent);
        string description = youtube.ApplyToDescription("本編の説明\n");
        StringAssert.EndsWith(description, "この動画には AI で生成した字幕が含まれます");
        StringAssert.StartsWith(description, "本編の説明");

        string directory = Path.Combine(f.Root, "out");
        string? path = youtube.WriteArtifact(directory);
        Assert.IsNotNull(path);
        string text = File.ReadAllText(path);
        StringAssert.Contains(text, "この動画には AI で生成した字幕が含まれます");
        StringAssert.Contains(text, "AutoSubtitle subtitle-track local-model 1.2.3");

        // ラベルを追加していない公開先には何も付かない。
        AiDisclosure other = f.Service.BuildDisclosure(f.Project.Id, "社内ポータル");
        Assert.IsFalse(other.HasContent);
        Assert.AreEqual("説明", other.ApplyToDescription("説明"));
        Assert.IsNull(other.WriteArtifact(Path.Combine(f.Root, "none")));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Root, "none")));
    }

    [TestMethod]
    // NFR-BR-005 AC-126: 空の参照・改行入りの値は記録しない。
    public void InvalidReferencesAreRejected()
    {
        using Fixture f = new();
        _ = Assert.ThrowsExactly<ArgumentException>(() => f.Service.RecordAiElement(
            EntityId<AuditEventIdTag>.New(), f.Project.Id,
            AiGeneratedElementKind.SpeakerEstimate, " ",
            CreateModelRun(), Actor, ReviewActorRole.Editor, At));
        _ = Assert.ThrowsExactly<ArgumentException>(() => f.Service.AddDestinationLabel(
            EntityId<AuditEventIdTag>.New(), f.Project.Id, "X", "a\nb",
            Actor, ReviewActorRole.Editor, At));
        Assert.AreEqual(0, f.Service.ListAiElements(f.Project.Id).Count);
    }

    private static ModelRun CreateModelRun()
    {
        ContentDigest Digest(char c) => new(new string(c, 64));
        return new ModelRun(
            EntityId<ModelRunIdTag>.New(), "local-provider", "local-model", "1.2.3",
            Digest('a'), ModelHashAvailability.Available, Digest('b'), Digest('c'),
            Digest('d'), ModelRunOutcome.Succeeded, null, ModelExecutionLocation.Local,
            null, null, null, null, At.AddMinutes(-2), At.AddMinutes(-1));
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.AiGenerationDisclosureServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string databasePath = Path.Combine(Root, "store.db");
            _ = DatabaseMigrator.Migrate(databasePath, Path.Combine(Root, "backups"));
            Project = new Project(
                EntityId<ProjectIdTag>.New(), "AI disclosure test", "Verify AI disclosure.",
                "Test runner", PublicationScope.InternalOnly, deadline: null,
                assets: [], performers: []);
            new ProjectStore(databasePath).CreateProject(
                Project, "{\"project\":true}", At.AddMinutes(-10));
            Service = new AiGenerationDisclosureService(new ReviewStore(databasePath));
        }

        internal string Root { get; }

        internal Project Project { get; }

        internal AiGenerationDisclosureService Service { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
