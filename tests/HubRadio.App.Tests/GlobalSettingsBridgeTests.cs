using System.Globalization;
using System.Text;
using System.Text.Json;
using HubRadio.App.DefaultSettings;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.App.Web;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Provenance;
using HubRadio.Core.Security;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Media;
using Microsoft.Data.Sqlite;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class GlobalSettingsBridgeTests
{
    private const string SubtitleKey = "subtitles.enabled";
    private const string CrossfadeKey = "editing.crossfadeEnabled";
    private const string AuthorizedSid = "S-1-5-21-1111111111-2222222222-3333333333-5101";
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] MutationMethods =
    [
        "settings.global.update",
        "settings.global.reset",
        "settings.global.resetAll",
    ];

    [TestMethod]
    public void ReadReturnsAll127DefinitionsWithProjectCompatibleWireShape()
    {
        using Fixture fixture = new();
        StoredGlobalSettings before = fixture.Store.GetGlobalSettings()!;

        JsonElement snapshot = fixture.ReadSnapshot();

        Assert.AreEqual(128, fixture.Profile.Definitions.Count);
        Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("projectId").ValueKind);
        Assert.AreEqual(fixture.Profile.Definitions.Count, snapshot.GetProperty("definitionCount").GetInt32());
        Assert.AreEqual(fixture.Profile.Definitions.Count, snapshot.GetProperty("definitions").GetArrayLength());
        CollectionAssert.AreEquivalent(
            Enum.GetValues<DefaultSettingValueKind>(),
            fixture.Profile.Definitions.Select(static definition => definition.DefaultValue.Kind).Distinct().ToArray());
        CollectionAssert.AreEqual(
            fixture.Profile.Definitions.Select(static definition => definition.Key).ToArray(),
            snapshot.GetProperty("definitions").EnumerateArray()
                .Select(static definition => definition.GetProperty("key").GetString()).ToArray());
        AssertSelections(fixture.Profile.CreateSnapshot(), snapshot.GetProperty("initialValues"));
        AssertSelections(before.Snapshot, snapshot.GetProperty("values"));
        AssertGlobalEqual(before, fixture.Store.GetGlobalSettings()!);
        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(0, fixture.Authorization.EditDemandCount);

        EntityId<ProjectIdTag> projectId = fixture.CreateBoundProject(before.Snapshot);
        BridgeRequest projectRequest = ParseRequest("settings.read", new { projectId = projectId.ToString() });
        JsonElement projectSnapshot = ReadSnapshot(new SettingsBridgeRequestHandler(
            fixture.Store, new FakeAuthorization()).Handle(projectRequest));
        CollectionAssert.AreEqual(
            projectSnapshot.EnumerateObject().Select(static property => property.Name).ToArray(),
            snapshot.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(projectId.ToString(), projectSnapshot.GetProperty("projectId").GetString());
        foreach (JsonProperty property in projectSnapshot.EnumerateObject())
        {
            if (property.Name != "projectId")
            {
                Assert.AreEqual(property.Value.GetRawText(), snapshot.GetProperty(property.Name).GetRawText(), property.Name);
            }
        }
    }

    [TestMethod]
    public void UpdateChangesOnlySelectedValueAndSurvivesRestart()
    {
        using Fixture fixture = new();
        JsonElement initial = fixture.ReadSnapshot();
        DefaultSettingValue replacement = fixture.ChangedBoolean(SubtitleKey);

        JsonElement updated = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(initial, SubtitleKey, BooleanValue(replacement))));

        Assert.AreEqual(1, updated.GetProperty("revision").GetInt32());
        Assert.AreEqual(fixture.UtcNow, updated.GetProperty("updatedAt").GetDateTimeOffset());
        AssertOnlySelectionChanged(initial, updated, SubtitleKey, replacement);
        AssertSnapshotEqual(updated, ReadSnapshot(fixture.Handle(
            fixture.RestartHandler(), "settings.global.read", new { })));
    }

    [TestMethod]
    public void ResetRestoresProfileDefaultAndPreservesOtherOverridesAcrossRestart()
    {
        using Fixture fixture = new();
        JsonElement first = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(fixture.ReadSnapshot(), SubtitleKey, BooleanValue(fixture.ChangedBoolean(SubtitleKey)))));
        JsonElement customized = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(first, CrossfadeKey, BooleanValue(fixture.ChangedBoolean(CrossfadeKey)))));
        fixture.UtcNow = InitialTime.AddMinutes(2);

        JsonElement reset = ReadSnapshot(fixture.Handle(fixture.RestartHandler(), "settings.global.reset",
            Mutation(customized, SubtitleKey)));

        Assert.AreEqual(customized.GetProperty("revision").GetInt32() + 1, reset.GetProperty("revision").GetInt32());
        Assert.AreEqual(fixture.UtcNow, reset.GetProperty("updatedAt").GetDateTimeOffset());
        AssertOnlySelectionChanged(customized, reset, SubtitleKey, fixture.Profile.GetDefinition(SubtitleKey).DefaultValue);
        AssertSnapshotEqual(reset, ReadSnapshot(fixture.Handle(
            fixture.RestartHandler(), "settings.global.read", new { })));
    }

    [TestMethod]
    public void ResetAllRestoresProfileDefaultsAcrossRestart()
    {
        using Fixture fixture = new();
        JsonElement first = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(fixture.ReadSnapshot(), SubtitleKey, BooleanValue(fixture.ChangedBoolean(SubtitleKey)))));
        JsonElement customized = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(first, CrossfadeKey, BooleanValue(fixture.ChangedBoolean(CrossfadeKey)))));
        fixture.UtcNow = InitialTime.AddMinutes(3);

        JsonElement reset = ReadSnapshot(fixture.Handle(fixture.RestartHandler(), "settings.global.resetAll",
            Mutation(customized)));

        Assert.AreEqual(customized.GetProperty("revision").GetInt32() + 1, reset.GetProperty("revision").GetInt32());
        Assert.AreEqual(fixture.UtcNow, reset.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.AreEqual(fixture.Profile.CreateSnapshot().ContentDigest.Hex, reset.GetProperty("snapshotDigest").GetString());
        Assert.AreEqual(reset.GetProperty("initialSnapshotDigest").GetString(), reset.GetProperty("snapshotDigest").GetString());
        Assert.AreEqual(reset.GetProperty("initialValues").GetRawText(), reset.GetProperty("values").GetRawText());
        AssertSnapshotEqual(reset, ReadSnapshot(fixture.Handle(
            fixture.RestartHandler(), "settings.global.read", new { })));
    }

    [TestMethod]
    public void ReadUsesBoundProfileDefaultsRatherThanSavedOverridesOrLatestProfile()
    {
        using Fixture fixture = new(initializeGlobal: false);
        DefaultSettingsSnapshot defaults = fixture.Profile.CreateSnapshot();
        DefaultSettingsSnapshot customized = ReplaceValue(
            fixture.Profile, defaults, SubtitleKey, fixture.ChangedBoolean(SubtitleKey));
        _ = fixture.Store.InitializeGlobalSettings(customized, InitialTime);
        DefaultSettingsProfile newer = DefaultSettingsProfile.Create(
            fixture.Profile.Id, "Newer unbound profile", new SettingsProfileVersion(2, 0, 0),
            [Definition("settings.other", DefaultSettingValue.FromBoolean(false))]);
        _ = fixture.Store.RegisterProfile(Encoding.UTF8.GetBytes(newer.CanonicalJson));

        JsonElement snapshot = ReadSnapshot(fixture.Handle(fixture.RestartHandler(), "settings.global.read", new { }));

        Assert.AreEqual(fixture.Profile.Id.ToString(), snapshot.GetProperty("profileId").GetString());
        Assert.AreEqual(fixture.Profile.Name, snapshot.GetProperty("profileName").GetString());
        Assert.AreEqual(fixture.Profile.Version.ToString(), snapshot.GetProperty("profileVersion").GetString());
        Assert.AreEqual(fixture.Profile.ContentDigest.Hex, snapshot.GetProperty("profileDigest").GetString());
        Assert.AreEqual(defaults.ContentDigest.Hex, snapshot.GetProperty("initialSnapshotDigest").GetString());
        Assert.AreEqual(customized.ContentDigest.Hex, snapshot.GetProperty("snapshotDigest").GetString());
        AssertSelections(defaults, snapshot.GetProperty("initialValues"));
        AssertSelections(customized, snapshot.GetProperty("values"));
        JsonElement reset = ReadSnapshot(fixture.Handle("settings.global.reset", Mutation(snapshot, SubtitleKey)));
        Assert.AreEqual(defaults.ContentDigest.Hex, reset.GetProperty("snapshotDigest").GetString());
        AssertSelections(defaults, reset.GetProperty("values"));
    }

    [TestMethod]
    public async Task NewProjectBindsChangedGlobalWhileExistingDraftAndProjectInitialStayFixed()
    {
        using Fixture fixture = new();
        JsonElement initial = fixture.ReadSnapshot();
        EntityId<ProjectIdTag> existingId = fixture.CreateBoundProject(fixture.Profile.CreateSnapshot());
        StoredProjectSettings existing = fixture.Store.UpdateProjectSettings(
            existingId,
            ReplaceValue(fixture.Profile, fixture.Profile.CreateSnapshot(), CrossfadeKey, fixture.ChangedBoolean(CrossfadeKey)),
            expectedRevision: 0, InitialTime.AddSeconds(30));
        ProjectCreationService projects = fixture.CreateProjectService();
        ProjectCreationDraftView oldDraft = await projects.CreateSourceOnlyDraftAsync();

        JsonElement changed = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(initial, SubtitleKey, BooleanValue(fixture.ChangedBoolean(SubtitleKey)))));
        AssertProjectEqual(existing, fixture.Store.GetProjectSettings(existingId).Settings!);
        ProjectCreationDraftView newDraft = await projects.CreateSourceOnlyDraftAsync();
        Assert.IsNotNull(newDraft.SourceOnly);
        Assert.AreEqual(changed.GetProperty("snapshotDigest").GetString(), newDraft.SourceOnly.SettingsSnapshotDigest);
        Assert.AreEqual(initial.GetProperty("snapshotDigest").GetString(), oldDraft.SourceOnly!.SettingsSnapshotDigest);

        JsonElement reset = ReadSnapshot(fixture.Handle("settings.global.resetAll", Mutation(changed)));
        ProjectCreationDraftView finalizedOld = FinalizeProjectDraft(projects, oldDraft);
        ProjectCreationDraftView finalizedNew = FinalizeProjectDraft(projects, newDraft);
        EntityId<ProjectIdTag> newId = EntityId<ProjectIdTag>.Parse(finalizedNew.ProjectId!);
        StoredProjectSettings bound = fixture.Store.GetProjectSettings(newId).Settings!;
        Assert.AreEqual(newDraft.SourceOnly.SettingsSnapshotDigest, bound.InitialSnapshot.ContentDigest.Hex);
        Assert.AreEqual(bound.InitialSnapshot.ContentDigest, bound.CurrentSnapshot.ContentDigest);
        Assert.AreEqual(0, bound.Revision);
        Assert.AreNotEqual(reset.GetProperty("snapshotDigest").GetString(), bound.InitialSnapshot.ContentDigest.Hex);
        Assert.AreEqual(oldDraft.SourceOnly.SettingsSnapshotDigest,
            fixture.Store.GetProjectSettings(EntityId<ProjectIdTag>.Parse(finalizedOld.ProjectId!))
                .Settings!.InitialSnapshot.ContentDigest.Hex);
        AssertProjectEqual(existing, fixture.Store.GetProjectSettings(existingId).Settings!);

        _ = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(reset, CrossfadeKey, BooleanValue(fixture.ChangedBoolean(CrossfadeKey)))));
        var restartedStore = new DefaultSettingsStore(fixture.DatabasePath);
        AssertProjectEqual(bound, restartedStore.GetProjectSettings(newId).Settings!);
        AssertProjectEqual(existing, restartedStore.GetProjectSettings(existingId).Settings!);
    }

    [TestMethod]
    [DataRow("expectedRevision")]
    [DataRow("expectedProfileId")]
    [DataRow("expectedProfileVersion")]
    [DataRow("expectedProfileDigest")]
    [DataRow("expectedSnapshotDigest")]
    public void MutationsRejectEachStaleIdentityFieldWithoutWriting(string field)
    {
        using Fixture fixture = new();
        JsonElement current = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(fixture.ReadSnapshot(), SubtitleKey, BooleanValue(fixture.ChangedBoolean(SubtitleKey)))));
        object staleValue = field switch
        {
            "expectedRevision" => current.GetProperty("revision").GetInt32() - 1,
            "expectedProfileId" => EntityId<DefaultSettingsProfileIdTag>.New().ToString(),
            "expectedProfileVersion" => "2.0.0",
            "expectedProfileDigest" => new string('0', 64),
            "expectedSnapshotDigest" => new string('0', 64),
            _ => throw new AssertFailedException("Unexpected identity field."),
        };

        foreach (string method in MutationMethods)
        {
            Dictionary<string, object?> parameters = ParametersFor(method, current,
                fixture.Profile.GetDefinition(SubtitleKey).DefaultValue.BooleanValue!.Value);
            Assert.AreNotEqual(parameters[field], staleValue, field);
            parameters[field] = staleValue;

            AssertFailure(fixture.Handle(method, parameters), "conflict");
            AssertSnapshotEqual(current, fixture.ReadSnapshot());
        }
    }

    [TestMethod]
    public void StaleRevisionRejectsNoOpsAfterValuesReturnToOriginalDigest()
    {
        using Fixture fixture = new();
        JsonElement initial = fixture.ReadSnapshot();
        JsonElement changed = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(initial, SubtitleKey, BooleanValue(fixture.ChangedBoolean(SubtitleKey)))));
        JsonElement restored = ReadSnapshot(fixture.Handle("settings.global.reset", Mutation(changed, SubtitleKey)));
        Assert.AreEqual(initial.GetProperty("snapshotDigest").GetString(), restored.GetProperty("snapshotDigest").GetString());
        Assert.AreEqual(2, restored.GetProperty("revision").GetInt32());

        foreach (string method in MutationMethods)
        {
            AssertFailure(fixture.Handle(method, ParametersFor(method, initial,
                fixture.Profile.GetDefinition(SubtitleKey).DefaultValue.BooleanValue!.Value)), "conflict");
            AssertSnapshotEqual(restored, fixture.ReadSnapshot());
        }
    }

    [TestMethod]
    public void NoOpMutationsPreserveRevisionTimestampAndSnapshot()
    {
        using Fixture fixture = new();
        var noWriteHandler = new GlobalSettingsBridgeRequestHandler(fixture.Store, fixture.Authorization,
            () => throw new AssertFailedException("A no-op must not request a write timestamp."));
        JsonElement initial = fixture.ReadSnapshot();
        foreach (string method in MutationMethods)
        {
            AssertSnapshotEqual(initial, ReadSnapshot(fixture.Handle(noWriteHandler, method,
                ParametersFor(method, initial, fixture.Profile.GetDefinition(SubtitleKey).DefaultValue.BooleanValue!.Value))));
        }

        DefaultSettingValue replacement = fixture.ChangedBoolean(SubtitleKey);
        JsonElement customized = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(initial, SubtitleKey, BooleanValue(replacement))));
        AssertSnapshotEqual(customized, ReadSnapshot(fixture.Handle(noWriteHandler, "settings.global.update",
            Mutation(customized, SubtitleKey, BooleanValue(replacement)))));
        AssertSnapshotEqual(customized, ReadSnapshot(fixture.Handle(noWriteHandler, "settings.global.reset",
            Mutation(customized, CrossfadeKey))));
        JsonElement restored = ReadSnapshot(fixture.Handle("settings.global.resetAll", Mutation(customized)));
        AssertSnapshotEqual(restored, ReadSnapshot(fixture.Handle(noWriteHandler, "settings.global.resetAll", Mutation(restored))));
        AssertSnapshotEqual(restored, ReadSnapshot(fixture.Handle(fixture.RestartHandler(), "settings.global.read", new { })));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AuthorizationPrecedesMissingAndCorruptStoreLookups(bool corruptStore)
    {
        using Fixture fixture = new(initializeGlobal: corruptStore);
        JsonElement identity = InitialIdentity(fixture.Profile);
        if (corruptStore)
        {
            fixture.ExecuteSql("UPDATE default_settings_global SET snapshot_digest = $value;", new string('0', 64));
        }

        fixture.Authorization.DenyRead = true;
        fixture.Authorization.DenyEdit = true;
        foreach (string method in MutationMethods.Prepend("settings.global.read"))
        {
            AssertFailure(fixture.Handle(method, ParametersFor(method, identity, false)), "forbidden");
        }

        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(3, fixture.Authorization.EditDemandCount);
        if (!corruptStore)
        {
            Assert.IsNull(fixture.Store.GetGlobalSettings());
        }
    }

    [TestMethod]
    public void ReadPermissionDoesNotAuthorizeMutationsOrExposeStaleIdentity()
    {
        using Fixture fixture = new();
        fixture.Authorization.DenyEdit = true;
        JsonElement initial = fixture.ReadSnapshot();
        foreach (string method in MutationMethods)
        {
            Dictionary<string, object?> parameters = ParametersFor(method, initial, false);
            parameters["expectedRevision"] = 1;
            if (method != "settings.global.resetAll")
            {
                parameters["key"] = "unknown.key";
            }

            AssertFailure(fixture.Handle(method, parameters), "forbidden");
        }

        Assert.AreEqual(1, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(3, fixture.Authorization.EditDemandCount);
        AssertSnapshotEqual(initial, fixture.ReadSnapshot());
    }

    [TestMethod]
    public void UpdateAndResetRejectUnknownKeysAndInvalidValuesWithoutMutation()
    {
        using Fixture fixture = new();
        JsonElement initial = fixture.ReadSnapshot();
        DefaultSettingDefinition integer = fixture.Profile.Definitions.First(static definition =>
            definition.DefaultValue.Kind == DefaultSettingValueKind.Integer &&
            definition.Minimum is not null && definition.Maximum is not null);
        DefaultSettingDefinition allowedText = fixture.Profile.Definitions.First(static definition =>
            definition.AllowedTextValues.Count > 0);
        const string disallowed = "not-an-allowed-setting-value";
        Assert.IsFalse(allowedText.AllowedTextValues.Contains(disallowed, StringComparer.Ordinal));
        (string Key, object Value)[] invalidUpdates =
        [
            ("unknown.key", new { kind = "boolean", booleanValue = false }),
            (SubtitleKey, new { kind = "integer", integerValue = "0" }),
            (integer.Key, new { kind = "integer", integerValue = checked(integer.Minimum!.IntegerValue!.Value - 1).ToString(CultureInfo.InvariantCulture) }),
            (integer.Key, new { kind = "integer", integerValue = checked(integer.Maximum!.IntegerValue!.Value + 1).ToString(CultureInfo.InvariantCulture) }),
            (allowedText.Key, new { kind = "text", textValue = disallowed }),
            (allowedText.Key, new { kind = "text", textValue = " " }),
        ];

        foreach ((string key, object value) in invalidUpdates)
        {
            AssertFailure(fixture.Handle("settings.global.update", Mutation(initial, key, value)), "invalid");
            AssertSnapshotEqual(initial, fixture.ReadSnapshot());
        }

        AssertFailure(fixture.Handle("settings.global.reset", Mutation(initial, "unknown.key")), "invalid");
        AssertSnapshotEqual(initial, fixture.ReadSnapshot());
    }

    [TestMethod]
    public void Int64AndRationalValuesRoundTripWithoutPrecisionLoss()
    {
        DefaultSettingsProfile profile = DefaultSettingsProfile.Create(
            EntityId<DefaultSettingsProfileIdTag>.New(), "Numeric wire test", new SettingsProfileVersion(1, 0, 0),
            [
                Definition("settings.integer", DefaultSettingValue.FromInteger(0),
                    DefaultSettingValue.FromInteger(long.MinValue), DefaultSettingValue.FromInteger(long.MaxValue)),
                Definition("settings.ratio", DefaultSettingValue.FromRational(1, 2),
                    DefaultSettingValue.FromRational(0, 1), DefaultSettingValue.FromRational(1, 1)),
            ]);
        using Fixture fixture = new(profile: profile);
        JsonElement current = fixture.ReadSnapshot();
        Assert.AreEqual(2, current.GetProperty("definitionCount").GetInt32());
        foreach (long value in new[] { long.MinValue, long.MaxValue })
        {
            current = ReadSnapshot(fixture.Handle("settings.global.update", Mutation(current, "settings.integer",
                new { kind = "integer", integerValue = value.ToString(CultureInfo.InvariantCulture) })));
            AssertWireValue(DefaultSettingValue.FromInteger(value), Selection(current, "settings.integer"));
        }

        const long numerator = 9007199254740993;
        const long denominator = 9007199254740995;
        current = ReadSnapshot(fixture.Handle("settings.global.update", Mutation(current, "settings.ratio", new
        {
            kind = "rational",
            numerator = numerator.ToString(CultureInfo.InvariantCulture),
            denominator = denominator.ToString(CultureInfo.InvariantCulture),
        })));
        AssertWireValue(DefaultSettingValue.FromRational(numerator, denominator), Selection(current, "settings.ratio"));
        AssertSnapshotEqual(current, ReadSnapshot(fixture.Handle(fixture.RestartHandler(), "settings.global.read", new { })));
        foreach (string invalid in new[] { "-1", "2" })
        {
            AssertFailure(fixture.Handle("settings.global.update", Mutation(current, "settings.ratio",
                new { kind = "rational", numerator = invalid, denominator = "1" })), "invalid");
            AssertSnapshotEqual(current, fixture.ReadSnapshot());
        }
    }

    [TestMethod]
    public void UninitializedGlobalReturnsUnavailableWithoutInitializing()
    {
        using Fixture fixture = new(initializeGlobal: false);
        JsonElement identity = InitialIdentity(fixture.Profile);
        foreach (string method in MutationMethods.Prepend("settings.global.read"))
        {
            AssertFailure(fixture.Handle(method, ParametersFor(method, identity, false)), "unavailable");
        }

        Assert.IsNull(new DefaultSettingsStore(fixture.DatabasePath).GetGlobalSettings());
        Assert.AreEqual(1, fixture.Store.ListProfiles().Count);
    }

    [TestMethod]
    [DataRow("snapshotDigest")]
    [DataRow("profilePayload")]
    [DataRow("database")]
    public void CorruptOrMissingStoreReturnsUnavailableWithoutLeakingDetails(string failure)
    {
        using Fixture fixture = new();
        JsonElement initial = fixture.ReadSnapshot();
        switch (failure)
        {
            case "snapshotDigest":
                fixture.ExecuteSql("UPDATE default_settings_global SET snapshot_digest = $value;", new string('0', 64));
                break;
            case "profilePayload":
                fixture.ExecuteSql("UPDATE default_settings_profiles SET profile_payload = $value;",
                    Encoding.UTF8.GetBytes("{\"private\":\"ux009-private-store-detail\"}"));
                break;
            case "database":
                File.Move(fixture.DatabasePath, fixture.DatabasePath + ".offline");
                break;
            default:
                throw new AssertFailedException("Unexpected store failure.");
        }

        foreach (string method in MutationMethods.Prepend("settings.global.read"))
        {
            AssertFailure(fixture.Handle(method, ParametersFor(method, initial, false)), "unavailable");
        }
    }

    [TestMethod]
    [DataRow("settings.global.update")]
    [DataRow("settings.global.reset")]
    [DataRow("settings.global.resetAll")]
    public void StoreWriteFailureReturnsUnavailableWithoutChangingSnapshot(string method)
    {
        using Fixture fixture = new();
        JsonElement customized = ReadSnapshot(fixture.Handle("settings.global.update",
            Mutation(fixture.ReadSnapshot(), SubtitleKey, BooleanValue(fixture.ChangedBoolean(SubtitleKey)))));
        StoredGlobalSettings before = fixture.Store.GetGlobalSettings()!;
        fixture.ExecuteSql("""
            CREATE TRIGGER fail_ux009_global_update
            BEFORE UPDATE ON default_settings_global
            BEGIN
                SELECT RAISE(ABORT, 'ux009-private-store-detail');
            END;
            """);

        AssertFailure(fixture.Handle(method, ParametersFor(method, customized,
            fixture.Profile.GetDefinition(SubtitleKey).DefaultValue.BooleanValue!.Value)), "unavailable");

        AssertGlobalEqual(before, new DefaultSettingsStore(fixture.DatabasePath).GetGlobalSettings()!);
    }

    [TestMethod]
    public void ConcurrentStoreWriteReturnsConflictWithoutOverwritingWinner()
    {
        using Fixture fixture = new();
        JsonElement initial = fixture.ReadSnapshot();
        DefaultSettingValue winnerValue = fixture.ChangedBoolean(SubtitleKey);
        var racingHandler = new GlobalSettingsBridgeRequestHandler(fixture.Store, fixture.Authorization, () =>
        {
            _ = new GlobalSettingsService(new DefaultSettingsStore(fixture.DatabasePath), () => fixture.UtcNow)
                .Update(SubtitleKey, winnerValue, expectedRevision: 0);
            return fixture.UtcNow;
        });

        AssertFailure(fixture.Handle(racingHandler, "settings.global.update",
            Mutation(initial, CrossfadeKey, BooleanValue(fixture.ChangedBoolean(CrossfadeKey)))), "conflict");

        JsonElement winner = ReadSnapshot(fixture.Handle(fixture.RestartHandler(), "settings.global.read", new { }));
        Assert.AreEqual(1, winner.GetProperty("revision").GetInt32());
        AssertOnlySelectionChanged(initial, winner, SubtitleKey, winnerValue);
    }

    [TestMethod]
    public void ConstructorAndHandleRejectNullArguments()
    {
        using Fixture fixture = new();
        Assert.AreEqual("store", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new GlobalSettingsBridgeRequestHandler(null!, fixture.Authorization)).ParamName);
        Assert.AreEqual("authorization", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new GlobalSettingsBridgeRequestHandler(fixture.Store, null!)).ParamName);
        Assert.AreEqual("request", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new GlobalSettingsBridgeRequestHandler(fixture.Store, fixture.Authorization).Handle(null!)).ParamName);
    }

    [TestMethod]
    public void NonGlobalMethodsAreRejectedWithoutAuthorizationOrLookup()
    {
        using Fixture fixture = new(initializeGlobal: false);
        AssertFailure(fixture.Handle("settings.read", new { projectId = EntityId<ProjectIdTag>.New().ToString() }), "invalid");
        Dictionary<string, object?> fileParameters = Mutation(InitialIdentity(fixture.Profile));
        fileParameters["projectId"] = null;
        AssertFailure(fixture.Handle("settings.file.preview", fileParameters), "invalid");
        Assert.AreEqual(0, fixture.Authorization.ReadDemandCount);
        Assert.AreEqual(0, fixture.Authorization.EditDemandCount);
        Assert.IsNull(fixture.Store.GetGlobalSettings());
    }

    private static object BooleanValue(DefaultSettingValue value) =>
        new { kind = "boolean", booleanValue = value.BooleanValue!.Value };

    private static JsonElement InitialIdentity(DefaultSettingsProfile profile) =>
        JsonSerializer.SerializeToElement(new
        {
            revision = 0,
            profileId = profile.Id.ToString(),
            profileVersion = profile.Version.ToString(),
            profileDigest = profile.ContentDigest.Hex,
            snapshotDigest = profile.CreateSnapshot().ContentDigest.Hex,
        });

    private static Dictionary<string, object?> ParametersFor(string method, JsonElement identity, bool replacement) =>
        method switch
        {
            "settings.global.read" => [],
            "settings.global.update" => Mutation(identity, SubtitleKey, new { kind = "boolean", booleanValue = replacement }),
            "settings.global.reset" => Mutation(identity, SubtitleKey),
            "settings.global.resetAll" => Mutation(identity),
            _ => throw new AssertFailedException("Unexpected global settings method."),
        };

    private static Dictionary<string, object?> Mutation(JsonElement snapshot, string? key = null, object? value = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["expectedRevision"] = snapshot.GetProperty("revision").GetInt32(),
            ["expectedProfileId"] = snapshot.GetProperty("profileId").GetString(),
            ["expectedProfileVersion"] = snapshot.GetProperty("profileVersion").GetString(),
            ["expectedProfileDigest"] = snapshot.GetProperty("profileDigest").GetString(),
            ["expectedSnapshotDigest"] = snapshot.GetProperty("snapshotDigest").GetString(),
        };
        if (key is not null)
        {
            parameters["key"] = key;
        }

        if (value is not null)
        {
            parameters["value"] = value;
        }

        return parameters;
    }

    private static BridgeRequest ParseRequest(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            requestId = Guid.NewGuid().ToString("D"),
            method,
            @params = parameters,
        });
        Assert.IsTrue(BridgeProtocol.TryParseRequest(WebViewHost.EntryPoint, json,
            out BridgeRequest? request, out BridgeRequestRejection rejection), rejection.ToString());
        Assert.IsNotNull(request);
        return request;
    }

    private static JsonElement ReadSnapshot(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.AreEqual("available", result.GetProperty("status").GetString());
        return result.GetProperty("snapshot").Clone();
    }

    private static void AssertFailure(string response, string expectedStatus)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.AreEqual(expectedStatus, result.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("snapshot").ValueKind);
        Assert.IsFalse(response.Contains("ux009-private-store-detail", StringComparison.Ordinal));
        Assert.IsFalse(response.Contains("settings.db", StringComparison.Ordinal));
        Assert.IsFalse(response.Contains("Exception", StringComparison.Ordinal));
    }

    private static void AssertEnvelope(BridgeRequest request, string response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "version", "requestId", "method", "result" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual(request.RequestId.ToString("D"), root.GetProperty("requestId").GetString());
        Assert.AreEqual(BridgeProtocol.GetMethodName(request.Method), root.GetProperty("method").GetString());
        CollectionAssert.AreEqual(new[] { "status", "snapshot" },
            root.GetProperty("result").EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.IsTrue(Encoding.UTF8.GetByteCount(response) <= BridgeProtocol.GetMaximumResponseUtf8Bytes(request.Method));
    }

    private static void AssertSnapshotEqual(JsonElement expected, JsonElement actual) =>
        Assert.AreEqual(expected.GetRawText(), actual.GetRawText());

    private static JsonElement Selection(JsonElement snapshot, string key) =>
        snapshot.GetProperty("values").EnumerateArray()
            .Single(selection => selection.GetProperty("key").GetString() == key).GetProperty("value");

    private static void AssertOnlySelectionChanged(JsonElement before, JsonElement after, string key, DefaultSettingValue replacement)
    {
        foreach (string property in new[] { "projectId", "profileId", "profileName", "profileVersion", "profileDigest", "initialSnapshotDigest", "definitionCount", "definitions", "initialValues" })
        {
            Assert.AreEqual(before.GetProperty(property).GetRawText(), after.GetProperty(property).GetRawText(), property);
        }

        Assert.AreNotEqual(before.GetProperty("snapshotDigest").GetString(), after.GetProperty("snapshotDigest").GetString());
        CollectionAssert.AreEqual(
            before.GetProperty("values").EnumerateArray().Select(static selection => selection.GetProperty("key").GetString()).ToArray(),
            after.GetProperty("values").EnumerateArray().Select(static selection => selection.GetProperty("key").GetString()).ToArray());
        foreach (JsonElement selection in before.GetProperty("values").EnumerateArray())
        {
            string selectionKey = selection.GetProperty("key").GetString()!;
            if (selectionKey == key)
            {
                AssertWireValue(replacement, Selection(after, key));
            }
            else
            {
                Assert.AreEqual(selection.GetProperty("value").GetRawText(), Selection(after, selectionKey).GetRawText(), selectionKey);
            }
        }
    }

    private static void AssertSelections(DefaultSettingsSnapshot expected, JsonElement actual)
    {
        CollectionAssert.AreEqual(expected.Values.Select(static selection => selection.Key).ToArray(),
            actual.EnumerateArray().Select(static selection => selection.GetProperty("key").GetString()).ToArray());
        foreach (DefaultSettingSelection selection in expected.Values)
        {
            JsonElement value = actual.EnumerateArray()
                .Single(item => item.GetProperty("key").GetString() == selection.Key).GetProperty("value");
            AssertWireValue(selection.Value, value);
        }
    }

    private static void AssertWireValue(DefaultSettingValue expected, JsonElement actual)
    {
        switch (expected.Kind)
        {
            case DefaultSettingValueKind.Boolean:
                Assert.AreEqual("boolean", actual.GetProperty("kind").GetString());
                Assert.AreEqual(expected.BooleanValue!.Value, actual.GetProperty("booleanValue").GetBoolean());
                break;
            case DefaultSettingValueKind.Integer:
                Assert.AreEqual("integer", actual.GetProperty("kind").GetString());
                Assert.AreEqual(expected.IntegerValue!.Value.ToString(CultureInfo.InvariantCulture), actual.GetProperty("integerValue").GetString());
                break;
            case DefaultSettingValueKind.Rational:
                Assert.AreEqual("rational", actual.GetProperty("kind").GetString());
                Assert.AreEqual(expected.RationalValue!.Value.Numerator.ToString(CultureInfo.InvariantCulture), actual.GetProperty("numerator").GetString());
                Assert.AreEqual(expected.RationalValue!.Value.Denominator.ToString(CultureInfo.InvariantCulture), actual.GetProperty("denominator").GetString());
                break;
            case DefaultSettingValueKind.Text:
                Assert.AreEqual("text", actual.GetProperty("kind").GetString());
                Assert.AreEqual(expected.TextValue, actual.GetProperty("textValue").GetString());
                break;
            case DefaultSettingValueKind.TextList:
                Assert.AreEqual("textList", actual.GetProperty("kind").GetString());
                CollectionAssert.AreEqual(expected.TextListValue.ToArray(),
                    actual.GetProperty("textListValue").EnumerateArray().Select(static value => value.GetString()).ToArray());
                break;
            default:
                Assert.Fail("Unexpected setting value kind.");
                break;
        }
    }

    private static void AssertGlobalEqual(StoredGlobalSettings expected, StoredGlobalSettings actual)
    {
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
        Assert.AreEqual(expected.Snapshot.CanonicalJson, actual.Snapshot.CanonicalJson);
    }

    private static void AssertProjectEqual(StoredProjectSettings expected, StoredProjectSettings actual)
    {
        Assert.AreEqual(expected.ProjectId, actual.ProjectId);
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
        Assert.AreEqual(expected.InitialSnapshot.CanonicalJson, actual.InitialSnapshot.CanonicalJson);
        Assert.AreEqual(expected.CurrentSnapshot.CanonicalJson, actual.CurrentSnapshot.CanonicalJson);
    }

    private static DefaultSettingsSnapshot ReplaceValue(DefaultSettingsProfile profile, DefaultSettingsSnapshot snapshot,
        string key, DefaultSettingValue replacement) =>
        profile.CreateSnapshot(snapshot.Values.Select(selection => new DefaultSettingSelection(selection.Key,
            selection.Key == key ? replacement : selection.Value)).ToArray());

    private static DefaultSettingDefinition Definition(string key, DefaultSettingValue value,
        DefaultSettingValue? minimum = null, DefaultSettingValue? maximum = null) =>
        new(key, DefaultSettingGroup.Editing, false, key, "Global bridge test setting", DefaultSettingUnit.None,
            value, minimum, maximum, allowedTextValues: null,
            evidence: [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")]);

    private static ProjectCreationDraftView FinalizeProjectDraft(ProjectCreationService service, ProjectCreationDraftView draft) =>
        service.FinalizeDraft(EntityId<ProjectCreationDraftIdTag>.Parse(draft.DraftId), draft.Revision,
            JsonSerializer.SerializeToElement(new
            {
                sourceCandidateId = draft.SourceCandidateId,
                contentName = draft.ContentName,
                purpose = draft.Purpose,
                audience = draft.Audience,
                publicationScope = draft.PublicationScope,
                outputProfileIds = draft.OutputProfileIds,
                language = draft.Language,
                performerNames = draft.PerformerNames,
                deadlineSelection = new { hasDeadline = draft.HasDeadline, value = draft.Deadline },
                brandTemplateId = draft.BrandTemplateId,
            }));

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve the repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private readonly GlobalSettingsBridgeRequestHandler handler;

        internal Fixture(bool initializeGlobal = true, DefaultSettingsProfile? profile = null)
        {
            root = Path.Combine(Path.GetTempPath(), nameof(GlobalSettingsBridgeTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DatabasePath = Path.Combine(root, "settings.db");
            _ = DatabaseMigrator.Migrate(DatabasePath, Path.Combine(root, "backups"));
            Store = new DefaultSettingsStore(DatabasePath);
            byte[] profileBytes = profile is null
                ? File.ReadAllBytes(ResolveRepositoryPath("src/HubRadio.App/DefaultSettings/Profiles/1.3.0.json"))
                : Encoding.UTF8.GetBytes(profile.CanonicalJson);
            Profile = Store.RegisterProfile(profileBytes);
            if (initializeGlobal)
            {
                _ = Store.InitializeGlobalSettings(Profile.CreateSnapshot(), InitialTime);
            }

            handler = new GlobalSettingsBridgeRequestHandler(Store, Authorization, () => UtcNow);
        }

        internal string DatabasePath { get; }
        internal DefaultSettingsProfile Profile { get; }
        internal DefaultSettingsStore Store { get; }
        internal FakeAuthorization Authorization { get; } = new();
        internal DateTimeOffset UtcNow { get; set; } = InitialTime.AddMinutes(1);

        internal DefaultSettingValue ChangedBoolean(string key) =>
            DefaultSettingValue.FromBoolean(!Profile.GetDefinition(key).DefaultValue.BooleanValue!.Value);

        internal GlobalSettingsBridgeRequestHandler RestartHandler() =>
            new(new DefaultSettingsStore(DatabasePath), Authorization, () => UtcNow);

        internal JsonElement ReadSnapshot() => GlobalSettingsBridgeTests.ReadSnapshot(Handle("settings.global.read", new { }));

        internal string Handle(string method, object parameters) => Handle(handler, method, parameters);

        internal string Handle(GlobalSettingsBridgeRequestHandler target, string method, object parameters)
        {
            BridgeRequest request = ParseRequest(method, parameters);
            string response = target.Handle(request);
            AssertEnvelope(request, response);
            return response;
        }

        internal EntityId<ProjectIdTag> CreateBoundProject(DefaultSettingsSnapshot snapshot)
        {
            EntityId<ProjectIdTag> projectId = EntityId<ProjectIdTag>.New();
            var project = new Project(projectId, "Global bridge isolation test", "Settings bridge", "Internal",
                PublicationScope.InternalOnly, null, [], []);
            new ProjectStore(DatabasePath).CreateProject(project, "{\"project\":true}", InitialTime);
            _ = Store.BindProjectSettings(projectId, snapshot, InitialTime);
            return projectId;
        }

        internal ProjectCreationService CreateProjectService()
        {
            string sourcePath = Path.Combine(root, "source.mov");
            File.WriteAllBytes(sourcePath, [0]);
            var authorization = new AuthorizationService(new RoleAssignmentStore(DatabasePath));
            Assert.IsTrue(authorization.BootstrapInitialRoles(AuthorizedSid));
            var registry = new AssetCandidateRegistry();
            return new ProjectCreationService(new ProjectCreationDraftStore(DatabasePath), authorization, registry,
                ProjectCreationCatalog.Load(ResolveRepositoryPath("src/HubRadio.App/Profiles")), Store,
                () => AuthorizedSid,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult<EntityId<AssetIdTag>?>(registry.RegisterSelection(sourcePath));
                },
                new FixedTimeProvider(InitialTime.AddMinutes(5)));
        }

        internal void ExecuteSql(string sql, object? value = null)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            if (value is not null)
            {
                _ = command.Parameters.AddWithValue("$value", value);
            }

            _ = command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeAuthorization : ISettingsBridgeAuthorization
    {
        internal bool DenyRead { get; set; }
        internal bool DenyEdit { get; set; }
        internal int ReadDemandCount { get; private set; }
        internal int EditDemandCount { get; private set; }

        public void DemandRead()
        {
            ReadDemandCount++;
            if (DenyRead)
            {
                throw new AuthorizationException(AuthorizationOperation.ViewProject, null);
            }
        }

        public void DemandEdit()
        {
            EditDemandCount++;
            if (DenyEdit)
            {
                throw new AuthorizationException(AuthorizationOperation.CreateCompositionVersion, AuthorizationRole.Editor);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}