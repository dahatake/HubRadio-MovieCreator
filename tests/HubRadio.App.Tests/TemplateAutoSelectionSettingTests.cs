using HubRadio.App.DefaultSettings;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TemplateAutoSelectionSettingTests
{
    private const string Key = "project.templateAutoSelectEnabled";
    private static readonly DateTimeOffset InitialTime = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    // FR-146 AC-183: 初期状態は PARAM-012（使う）で、プロファイルは選択肢で選べる boolean の項目を持つ。
    [TestMethod]
    public void InitialValueIsUseAndTheProfileDefinesTheSetting()
    {
        using TempDatabase database = new();
        StoredGlobalSettings global = database.Bootstrap();

        Assert.IsTrue(TemplateAutoSelectionSetting.IsEnabled(global));
        DefaultSettingDefinition definition = database.Store
            .GetProfile(global.Snapshot.ProfileId, global.Snapshot.ProfileVersion)!
            .GetDefinition(Key);
        Assert.AreEqual(DefaultSettingValueKind.Boolean, definition.DefaultValue.Kind);
        Assert.AreEqual(true, definition.DefaultValue.BooleanValue);
        Assert.AreEqual(DefaultSettingGroup.ProjectAndOutput, definition.Group);
    }

    // FR-146 AC-184: 「使わない」の保存は再起動後も保持され、「使う」に戻せる。
    [TestMethod]
    public void ChoiceIsSavedKeptAfterRestartAndCanBeSwitchedBack()
    {
        using TempDatabase database = new();
        StoredGlobalSettings initial = database.Bootstrap();
        var service = new GlobalSettingsService(database.Store, () => InitialTime.AddMinutes(1));

        StoredGlobalSettings off = service.Update(Key, DefaultSettingValue.FromBoolean(false), initial.Revision);
        Assert.IsFalse(TemplateAutoSelectionSetting.IsEnabled(off));

        var restarted = new GlobalSettingsService(new DefaultSettingsStore(database.Path), () => InitialTime.AddMinutes(2));
        StoredGlobalSettings afterRestart = restarted.Read();
        Assert.IsFalse(TemplateAutoSelectionSetting.IsEnabled(afterRestart));
        // The restart bootstrap keeps the saved choice too.
        StoredGlobalSettings rebooted = database.Bootstrap();
        Assert.IsFalse(TemplateAutoSelectionSetting.IsEnabled(rebooted));

        StoredGlobalSettings on = restarted.Update(Key, DefaultSettingValue.FromBoolean(true), afterRestart.Revision);
        Assert.IsTrue(TemplateAutoSelectionSetting.IsEnabled(on));
    }

    // FR-146: 前の版（1.2.0）の snapshot にはこの項目がなく、既定（使う）として読む。
    [TestMethod]
    public void SnapshotOfAnOlderProfileReadsAsTheDefault()
    {
        byte[] bytes = File.ReadAllBytes(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.2.0.json"));
        DefaultSettingsProfile older = BundledDefaultSettingsProfileLoader.Load(bytes);

        Assert.IsFalse(older.Definitions.Any(static definition => definition.Key == Key));
        Assert.IsTrue(TemplateAutoSelectionSetting.IsEnabled(older.CreateSnapshot()));
    }

    // FR-146 AC-184: 設定の変更は global の値だけで、すでにある snapshot（Project・Job の記録）は変わらない。
    [TestMethod]
    public void ChangingTheGlobalSettingDoesNotChangeAnExistingSnapshot()
    {
        using TempDatabase database = new();
        StoredGlobalSettings initial = database.Bootstrap();
        DefaultSettingsSnapshot copiedBefore = initial.Snapshot;
        var service = new GlobalSettingsService(database.Store, () => InitialTime.AddMinutes(1));

        _ = service.Update(Key, DefaultSettingValue.FromBoolean(false), initial.Revision);

        Assert.IsTrue(TemplateAutoSelectionSetting.IsEnabled(copiedBefore));
    }

    private static string RepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return System.IO.Path.Combine(directory.FullName, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve the repository root.");
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), nameof(TemplateAutoSelectionSettingTests), Guid.NewGuid().ToString("N"));

        internal TempDatabase()
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "settings.db");
            _ = DatabaseMigrator.Migrate(Path, System.IO.Path.Combine(root, "backups"));
            Store = new DefaultSettingsStore(Path);
        }

        internal string Path { get; }

        internal DefaultSettingsStore Store { get; }

        internal StoredGlobalSettings Bootstrap() =>
            new BundledDefaultSettingsBootstrap(Store)
                .Initialize(RepositoryPath("src/HubRadio.App/DefaultSettings/Profiles"), InitialTime)
                .GlobalSettings;

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
