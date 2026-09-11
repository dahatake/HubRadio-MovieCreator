using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HubRadio.Core.Identity;
using HubRadio.Core.Serialization;
using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Security;
using HubRadio.Infrastructure.Settings;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SettingsTextFileTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ContentDigest WrongHash = ContentDigest.Compute("not the settings file"u8);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    [TestInitialize]
    public void RequireWindowsFixedTempDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("These tests require actual Windows file and ACL semantics.");
            return;
        }

        string driveRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))
            ?? throw new AssertInconclusiveException("The temporary directory has no drive root.");
        if (new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
        {
            Assert.Inconclusive("These tests require a fixed local temporary drive.");
        }
    }

    [TestMethod]
    public void PublicSurfaceIsConcreteMinimalAndContainsNoPathEvidence()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "Missing", "Invalid", "TooLarge", "ProfileMismatch", "SnapshotMismatch",
                "OverwriteRequired", "Conflict", "AccessDenied", "UnsafePath", "Unavailable",
            },
            Enum.GetNames<SettingsTextFileFailureReason>());
        Assert.IsTrue(typeof(SettingsTextFile).IsSealed);
        ConstructorInfo constructor = typeof(SettingsTextFile).GetConstructors().Single();
        CollectionAssert.AreEqual(
            new[] { typeof(string), typeof(string) },
            constructor.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
        MethodInfo[] methods = typeof(SettingsTextFile).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(
            new[] { "Export", "Read", "InspectIdentity" },
            methods.Select(static method => method.Name).ToArray());
        MethodInfo export = methods.Single(static method => method.Name == "Export");
        Assert.AreEqual(typeof(SettingsTextFileIdentity), export.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(DefaultSettingsSnapshot), typeof(ContentDigest), typeof(CancellationToken) },
            export.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
        Assert.IsTrue(export.GetParameters()[2].HasDefaultValue);
        MethodInfo read = methods.Single(static method => method.Name == "Read");
        Assert.AreEqual(typeof(SettingsTextFileReadResult), read.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(DefaultSettingsProfile), typeof(CancellationToken) },
            read.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
        Assert.IsTrue(read.GetParameters()[1].HasDefaultValue);
        Assert.AreEqual(0, methods.Single(static method => method.Name == "InspectIdentity").GetParameters().Length);
        AssertImmutable(typeof(SettingsTextFileIdentity), "FileSha256", "ByteSize");
        AssertImmutable(typeof(SettingsTextFileReadResult), "Snapshot", "Identity");
        Assert.AreEqual(typeof(DefaultSettingsSnapshot), typeof(SettingsTextFileReadResult).GetProperty("Snapshot")!.PropertyType);
        Assert.AreEqual(typeof(SettingsTextFileIdentity), typeof(SettingsTextFileReadResult).GetProperty("Identity")!.PropertyType);
        AssertImmutable(typeof(SettingsTextFileException), "Reason", "ExistingFileSha256", "ReconciliationRequired");

        foreach (SettingsTextFileFailureReason reason in Enum.GetValues<SettingsTextFileFailureReason>())
        {
            var exception = new SettingsTextFileException(reason, WrongHash, reconciliationRequired: true);
            Assert.IsNull(exception.InnerException);
            Assert.IsEmpty(exception.Data);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsTrue(exception.ReconciliationRequired);
            Assert.AreEqual(reason == SettingsTextFileFailureReason.OverwriteRequired ? WrongHash : null, exception.ExistingFileSha256);
            Assert.IsFalse(exception.Message.Contains(WrongHash.Hex, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ConstructorAndMissingInspectionDoNotCreateFilesOrChangeRootAcl()
    {
        using AppFixture fixture = new();
        byte[] rootAcl = DirectoryDescriptor(fixture.AppRoot);
        var file = new SettingsTextFile(fixture.AppRoot + Path.DirectorySeparatorChar, "  " + fixture.CurrentSid + "  ");

        Assert.IsNull(file.InspectIdentity());
        Assert.IsNull(file.InspectIdentity());
        AssertRootEntries(fixture);
        CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(fixture.AppRoot));
    }

    [TestMethod]
    [DataRow(false, false, 9007199254740993L, 30000L, 1001L)]
    [DataRow(true, false, long.MaxValue, long.MinValue, long.MaxValue)]
    [DataRow(false, true, long.MinValue, long.MaxValue, 10L)]
    [DataRow(true, true, 1280L, 48000L, 1000L)]
    public void Read_AllFiveKindsPreserveValuesAndRawEvidenceWithUtf8BomAndCrLf(
        bool bom, bool crlf, long integer, long numerator, long denominator)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(profile, integer, numerator, denominator);
        using JsonDocument document = JsonDocument.Parse(snapshot.CanonicalJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            // Object property order is formatting; values-array and text-list order are not.
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject().Reverse())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        string text = StrictUtf8.GetString(buffer.ToArray()) + "\n";
        Assert.IsTrue(text.Contains("日本語", StringComparison.Ordinal));
        if (crlf)
        {
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        Assert.ThrowsExactly<JsonException>(() => DefaultSettingsSnapshot.ParseCanonical(text, profile));
        byte[] raw = bom ? [0xef, 0xbb, 0xbf, .. StrictUtf8.GetBytes(text)] : StrictUtf8.GetBytes(text);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        byte[] acl = FileDescriptor(fixture.FilePath);
        byte[] rootAcl = DirectoryDescriptor(fixture.AppRoot);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid,
            () => Assert.Fail("Reading must not stage or publish settings."));

        SettingsTextFileReadResult result = file.Read(profile);

        AssertReadResult(snapshot, raw, result);
        CollectionAssert.AreEquivalent(Enum.GetValues<DefaultSettingValueKind>(),
            result.Snapshot.Values.Select(static selection => selection.Value.Kind).ToArray());
        Assert.AreNotEqual(snapshot.ContentDigest, result.Identity.FileSha256);
        AssertIdentity(result.Identity, file.InspectIdentity());
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(fixture.AppRoot));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Read_MissingFixedFileDoesNotSearchWorkspaceOrRecoveryFiles()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        byte[] raw = StrictUtf8.GetBytes(CreateSnapshot(profile).CanonicalJson);
        string workspace = Path.Combine(fixture.AppRoot, "Workspace");
        Directory.CreateDirectory(workspace);
        string other = Path.Combine(workspace, "setting.txt");
        string retained = Path.Combine(fixture.AppRoot, ".setting-retained.tmp");
        WriteProtectedFile(other, raw, fixture.CurrentSid);
        WriteProtectedFile(retained, raw, fixture.CurrentSid);
        byte[] rootAcl = DirectoryDescriptor(fixture.AppRoot);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.Missing, fixture);

        Assert.IsFalse(File.Exists(fixture.FilePath));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(other));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(retained));
        CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(fixture.AppRoot));
        AssertRootEntries(fixture, "Workspace", ".setting-retained.tmp");
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("whitespace")]
    [DataRow("notJson")]
    [DataRow("null")]
    [DataRow("array")]
    [DataRow("utf16")]
    [DataRow("partialBom")]
    [DataRow("doubleBom")]
    [DataRow("misplacedBom")]
    [DataRow("comment")]
    [DataRow("trailingComma")]
    [DataRow("trailingToken")]
    [DataRow("duplicateRoot")]
    [DataRow("escapedDuplicateRoot")]
    [DataRow("duplicateValue")]
    [DataRow("unpairedSurrogate")]
    [DataRow("depth")]
    public void Read_RejectsMalformedEncodingJsonDuplicatesAndDepthWithoutMutation(string variation)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        string canonical = CreateSnapshot(profile).CanonicalJson;
        byte[] raw = variation switch
        {
            "empty" => [],
            "whitespace" => " \t\r\n"u8.ToArray(),
            "notJson" => "private-settings-body"u8.ToArray(),
            "null" => "null"u8.ToArray(),
            "array" => "[]"u8.ToArray(),
            "utf16" => [0xff, 0xfe, .. Encoding.Unicode.GetBytes(canonical)],
            "partialBom" => [0xef, 0xbb],
            "doubleBom" => [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, .. StrictUtf8.GetBytes(canonical)],
            "misplacedBom" => [32, 0xef, 0xbb, 0xbf, .. StrictUtf8.GetBytes(canonical)],
            "comment" => StrictUtf8.GetBytes(canonical.Insert(1, "/* not allowed */")),
            "trailingComma" => StrictUtf8.GetBytes(canonical.Insert(canonical.Length - 1, ",")),
            "trailingToken" => StrictUtf8.GetBytes(canonical + "{}"),
            "duplicateRoot" => StrictUtf8.GetBytes(canonical.Insert(1, "\"profileId\":\"" + profile.Id + "\",")),
            "escapedDuplicateRoot" => StrictUtf8.GetBytes(canonical.Insert(1, "\"profile\\u0049d\":\"" + profile.Id + "\",")),
            "duplicateValue" => StrictUtf8.GetBytes(canonical.Replace("\"integer\":1", "\"integer\":1,\"integer\":1", StringComparison.Ordinal)),
            "unpairedSurrogate" => StrictUtf8.GetBytes(canonical.Replace("\\u65E5", "\\uD800", StringComparison.Ordinal)),
            "depth" => StrictUtf8.GetBytes(new string('[', 65) + canonical + new string(']', 65)),
            _ => throw new AssertFailedException("Unknown input variation."),
        };
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        byte[] acl = FileDescriptor(fixture.FilePath);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        SettingsTextFileException failure = AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.Invalid, fixture);

        AssertNotDisclosed(failure, "private-settings-body");
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Read_InvalidUtf8CannotBeAcceptedThroughReplacementCharacters()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateTextProfile();
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot(
            [new DefaultSettingSelection("settings.text", DefaultSettingValue.FromText("x\uFFFDy"))]);
        string canonical = snapshot.CanonicalJson;
        int marker = canonical.IndexOf("\\uFFFD", StringComparison.Ordinal);
        Assert.IsTrue(marker >= 0);
        byte[] raw = [.. StrictUtf8.GetBytes(canonical[..marker]), 0xff, .. StrictUtf8.GetBytes(canonical[(marker + 6)..])];
        // A permissive decoder would turn this byte into exactly the expected text value.
        Assert.AreEqual(snapshot.CanonicalJson, CanonicalJson.Canonicalize(Encoding.UTF8.GetString(raw)));
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.Invalid, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow("profileId")]
    [DataRow("profileVersion")]
    [DataRow("profileDigest")]
    public void Read_ValidProfileHeaderMismatchIsNotAFileHashOrSnapshotMismatch(string property)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        JsonObject root = JsonNode.Parse(CreateSnapshot(profile).CanonicalJson)!.AsObject();
        root[property] = property switch
        {
            "profileId" => EntityId<DefaultSettingsProfileIdTag>.New().ToString(),
            "profileVersion" => "2.0.0",
            _ => WrongHash.Hex,
        };
        byte[] raw = StrictUtf8.GetBytes(root.ToJsonString());
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.ProfileMismatch, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow("schemaVersion", null)]
    [DataRow("profileId", null)]
    [DataRow("profileVersion", null)]
    [DataRow("profileDigest", null)]
    [DataRow("contentDigest", null)]
    [DataRow("values", null)]
    [DataRow("unknown", "true")]
    [DataRow("schemaVersion", "\"2.0.0\"")]
    [DataRow("schemaVersion", "1")]
    [DataRow("profileId", "false")]
    [DataRow("profileId", "\"00000000-0000-0000-0000-000000000000\"")]
    [DataRow("profileId", "\"AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA\"")]
    [DataRow("profileVersion", "1")]
    [DataRow("profileVersion", "\"01.0.0\"")]
    [DataRow("profileDigest", "null")]
    [DataRow("profileDigest", "\"bad-digest\"")]
    [DataRow("contentDigest", "false")]
    [DataRow("contentDigest", "\"bad-digest\"")]
    [DataRow("values", "{}")]
    [DataRow("values", "[]")]
    public void Read_MalformedOrUnknownEnvelopeIsInvalidBeforeProfileMismatch(string property, string? replacementJson)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        JsonObject root = JsonNode.Parse(CreateSnapshot(profile).CanonicalJson)!.AsObject();
        root["profileId"] = EntityId<DefaultSettingsProfileIdTag>.New().ToString();
        if (replacementJson is null)
        {
            Assert.IsTrue(root.Remove(property));
        }
        else
        {
            root[property] = JsonNode.Parse(replacementJson);
        }

        byte[] raw = StrictUtf8.GetBytes(root.ToJsonString());
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.Invalid, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow("profileDigest")]
    [DataRow("contentDigest")]
    public void Read_NonCanonicalDigestTextIsInvalidRatherThanMismatch(string property)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        JsonObject root = JsonNode.Parse(CreateSnapshot(profile).CanonicalJson)!.AsObject();
        root[property] = new string('A', 64);
        byte[] raw = StrictUtf8.GetBytes(root.ToJsonString());
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);

        AssertFailure(() => new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid).Read(profile), SettingsTextFileFailureReason.Invalid, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Read_ContentDigestMismatchRejectsRatherThanRepairingOrReturningASnapshot(bool editValue)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        JsonObject root = JsonNode.Parse(CreateSnapshot(profile).CanonicalJson)!.AsObject();
        if (editValue)
        {
            root["values"]!.AsArray().Single(node => node!["key"]!.GetValue<string>() == "settings.integer")!["value"]!["integer"] = 2;
        }
        else
        {
            root["contentDigest"] = WrongHash.Hex;
        }

        byte[] raw = StrictUtf8.GetBytes(root.ToJsonString());
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        byte[] acl = FileDescriptor(fixture.FilePath);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.SnapshotMismatch, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        AssertIdentity(new SettingsTextFileIdentity(ContentDigest.Compute(raw), raw.LongLength), file.InspectIdentity());
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow("unknownKey")]
    [DataRow("missingKey")]
    [DataRow("duplicateKey")]
    [DataRow("unknownSelectionProperty")]
    [DataRow("missingSelectionProperty")]
    [DataRow("selectionType")]
    [DataRow("wrongKind")]
    [DataRow("unknownKind")]
    [DataRow("unknownValueProperty")]
    [DataRow("missingValueProperty")]
    [DataRow("integerType")]
    [DataRow("integerOverflow")]
    [DataRow("integerFraction")]
    [DataRow("integerBelowMinimum")]
    [DataRow("integerAboveMaximum")]
    [DataRow("rationalDenominator")]
    [DataRow("rationalAboveMaximum")]
    [DataRow("blankText")]
    [DataRow("textAllowlist")]
    [DataRow("duplicateTextListItem")]
    [DataRow("textListItemType")]
    [DataRow("keyOrder")]
    public void Read_InvalidValuesRemainInvalidWithEitherStaleOrRecomputedDigest(string variation)
    {
        DefaultSettingsProfile baseline = CreateProfile();
        DefaultSettingsProfile profile = DefaultSettingsProfile.Create(baseline.Id, baseline.Name, baseline.Version,
            baseline.Definitions.Select(definition => new DefaultSettingDefinition(
                definition.Key, definition.Group, definition.IsAdvanced, definition.DisplayName, definition.Description,
                definition.Unit, definition.DefaultValue,
                minimum: definition.DefaultValue.Kind switch
                {
                    DefaultSettingValueKind.Integer => DefaultSettingValue.FromInteger(0),
                    DefaultSettingValueKind.Rational => DefaultSettingValue.FromRational(1, 2),
                    _ => null,
                },
                maximum: definition.DefaultValue.Kind switch
                {
                    DefaultSettingValueKind.Integer => DefaultSettingValue.FromInteger(5),
                    DefaultSettingValueKind.Rational => DefaultSettingValue.FromRational(2, 1),
                    _ => null,
                },
                allowedTextValues: definition.DefaultValue.Kind == DefaultSettingValueKind.Text ? ["既定値", "許可値"] : null,
                evidence: definition.Evidence)).ToArray());
        JsonObject root = JsonNode.Parse(profile.CreateSnapshot().CanonicalJson)!.AsObject();
        JsonArray values = root["values"]!.AsArray();
        JsonObject integer = values.Single(node => node!["key"]!.GetValue<string>() == "settings.integer")!["value"]!.AsObject();
        JsonObject rational = values.Single(node => node!["key"]!.GetValue<string>() == "settings.ratio")!["value"]!.AsObject();
        JsonObject text = values.Single(node => node!["key"]!.GetValue<string>() == "settings.text")!["value"]!.AsObject();
        JsonObject list = values.Single(node => node!["key"]!.GetValue<string>() == "settings.list")!["value"]!.AsObject();
        switch (variation)
        {
            case "unknownKey": values[0]!["key"] = "settings.unknown"; break;
            case "missingKey": values.RemoveAt(0); break;
            case "duplicateKey": values[1]!["key"] = values[0]!["key"]!.GetValue<string>(); break;
            case "unknownSelectionProperty": values[0]!["unknown"] = true; break;
            case "missingSelectionProperty": Assert.IsTrue(values[0]!.AsObject().Remove("value")); break;
            case "selectionType": values[0] = false; break;
            case "wrongKind":
                integer.Remove("integer");
                integer["kind"] = "boolean";
                integer["boolean"] = true;
                break;
            case "unknownKind": integer["kind"] = "unknown"; break;
            case "unknownValueProperty": integer["unknown"] = true; break;
            case "missingValueProperty": Assert.IsTrue(integer.Remove("integer")); break;
            case "integerType": integer["integer"] = "1"; break;
            case "integerOverflow": integer["integer"] = JsonNode.Parse("9223372036854775808"); break;
            case "integerFraction": integer["integer"] = JsonNode.Parse("1e-1"); break;
            case "integerBelowMinimum": integer["integer"] = -1; break;
            case "integerAboveMaximum": integer["integer"] = 6; break;
            case "rationalDenominator": rational["denominator"] = 0; break;
            case "rationalAboveMaximum": rational["numerator"] = 3; break;
            case "blankText": text["text"] = " \t "; break;
            case "textAllowlist": text["text"] = "非許可"; break;
            case "duplicateTextListItem": list["values"] = new JsonArray("same", "same"); break;
            case "textListItemType": list["values"] = JsonNode.Parse("[1]"); break;
            case "keyOrder":
                JsonNode first = values[0]!.DeepClone();
                values[0] = values[1]!.DeepClone();
                values[1] = first;
                break;
            default: throw new AssertFailedException("Unknown invalid value variation.");
        }

        foreach (bool recomputeDigest in new[] { false, true })
        {
            using AppFixture fixture = new();
            JsonObject candidate = root.DeepClone().AsObject();
            if (recomputeDigest)
            {
                JsonObject payload = candidate.DeepClone().AsObject();
                Assert.IsTrue(payload.Remove("contentDigest"));
                candidate["contentDigest"] = CanonicalJson.ComputeDigest(StrictUtf8.GetBytes(payload.ToJsonString())).Hex;
            }

            byte[] raw = StrictUtf8.GetBytes(candidate.ToJsonString());
            WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
            byte[] acl = FileDescriptor(fixture.FilePath);
            var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

            AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.Invalid, fixture);

            CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
            CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
            AssertRootEntries(fixture, "setting.txt");
        }
    }

    [TestMethod]
    [DataRow(4 * 1024 * 1024 - 1)]
    [DataRow(4 * 1024 * 1024)]
    [DataRow(4 * 1024 * 1024 + 1)]
    public void Read_EnforcesRawByteBoundaryIncludingBomAndWhitespace(int byteSize)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateTextProfile();
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot();
        byte[] raw = new byte[byteSize];
        Array.Fill(raw, (byte)' ');
        raw[0] = 0xef;
        raw[1] = 0xbb;
        raw[2] = 0xbf;
        StrictUtf8.GetBytes(snapshot.CanonicalJson).CopyTo(raw, 3);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        if (byteSize > SettingsTextFile.MaximumRawUtf8Bytes)
        {
            AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.TooLarge, fixture);
        }
        else
        {
            AssertReadResult(snapshot, raw, file.Read(profile));
        }

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(1024 * 1024 - 1)]
    [DataRow(1024 * 1024)]
    [DataRow(1024 * 1024 + 1)]
    public void Read_EnforcesCanonicalUtf8ByteBoundary(int byteSize)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateTextProfile();
        DefaultSettingsSnapshot snapshot = SnapshotOfCanonicalSize(profile, byteSize);
        byte[] raw = StrictUtf8.GetBytes(snapshot.CanonicalJson);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        if (byteSize > SettingsTextFile.MaximumCanonicalJsonUtf8Bytes)
        {
            AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.TooLarge, fixture);
        }
        else
        {
            AssertReadResult(snapshot, raw, file.Read(profile));
        }

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Read_CanonicalLimitIsCheckedAfterUnicodeEscapingExpansion()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateTextProfile();
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot(
            [new DefaultSettingSelection("settings.text", DefaultSettingValue.FromText(
                new string('日', SettingsTextFile.MaximumCanonicalJsonUtf8Bytes / 6)))]);
        byte[] raw = StrictUtf8.GetBytes(snapshot.CanonicalJson.Replace("\\u65E5", "日", StringComparison.Ordinal));
        Assert.IsTrue(raw.Length < SettingsTextFile.MaximumCanonicalJsonUtf8Bytes);
        Assert.IsTrue(StrictUtf8.GetByteCount(snapshot.CanonicalJson) > SettingsTextFile.MaximumCanonicalJsonUtf8Bytes);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);

        AssertFailure(() => new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid).Read(profile), SettingsTextFileFailureReason.TooLarge, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Read_NullProfileIsInvalidWithoutChangingTheFile()
    {
        using AppFixture fixture = new();
        byte[] raw = StrictUtf8.GetBytes(CreateSnapshot(CreateProfile()).CanonicalJson);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);

        AssertFailure(() => new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid).Read(null!), SettingsTextFileFailureReason.Invalid, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Read_PreCancelledTokenLeavesMissingOrExistingFileUntouched(bool existing)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        byte[]? before = existing ? StrictUtf8.GetBytes(CreateSnapshot(profile).CanonicalJson) : null;
        if (before is not null)
        {
            WriteProtectedFile(fixture.FilePath, before, fixture.CurrentSid);
        }

        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(() => file.Read(profile, cancellation.Token));

        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        AssertOriginalOrAbsent(fixture, before);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Read_SharesWithReadersButRefusesAnExistingWriterEvenWithPermissiveSharing(bool writer)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(profile);
        byte[] raw = StrictUtf8.GetBytes(snapshot.CanonicalJson);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        using (var held = new FileStream(fixture.FilePath, FileMode.Open,
            writer ? FileAccess.Write : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (writer)
            {
                AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.Unavailable, fixture);
            }
            else
            {
                AssertReadResult(snapshot, raw, file.Read(profile));
            }
        }

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Read_RechecksFileAclAfterConstructionWithoutRepairingIt(bool inherited)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        byte[] raw = StrictUtf8.GetBytes(CreateSnapshot(profile).CanonicalJson);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        FileSecurity security = ReadFileSecurity(fixture.FilePath);
        if (inherited)
        {
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        }
        else
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        }

        FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), security);
        byte[] acl = FileDescriptor(fixture.FilePath);

        AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.AccessDenied, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Read_RechecksRootAndRejectsANonRegularFixedTarget(bool moveRoot)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        byte[] raw = StrictUtf8.GetBytes(CreateSnapshot(profile).CanonicalJson);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        string displaced = Path.Combine(fixture.RootPath, "displaced");
        Directory.Move(fixture.AppRoot, displaced);
        if (!moveRoot)
        {
            Directory.CreateDirectory(fixture.FilePath);
        }

        AssertFailure(() => file.Read(profile), SettingsTextFileFailureReason.UnsafePath, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(Path.Combine(displaced, "setting.txt")));
        Assert.AreEqual(!moveRoot, Directory.Exists(fixture.AppRoot));
        Assert.IsFalse(File.Exists(fixture.FilePath));
    }

    [TestMethod]
    public void Read_ExportedSnapshotAndFailuresDoNotMutateDatabaseSentinelsOrRecoveryCopies()
    {
        using AppFixture fixture = new();
        string workspace = Path.Combine(fixture.AppRoot, "Workspace");
        _ = WorkspaceAcl.CreateNew(workspace, fixture.CurrentSid);
        string[] databaseNames = ["hubradio.db", "hubradio.db-wal", "hubradio.db-shm"];
        byte[] sentinel = [0, 1, 2, 255, 13, 10];
        foreach (string name in databaseNames)
        {
            File.WriteAllBytes(Path.Combine(workspace, name), sentinel);
        }

        string retained = Path.Combine(fixture.AppRoot, ".setting-retained.tmp");
        WriteProtectedFile(retained, sentinel, fixture.CurrentSid);
        DefaultSettingsProfile profile = CreateProfile();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(profile);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity exported = file.Export(snapshot, null);
        byte[] raw = File.ReadAllBytes(fixture.FilePath);
        byte[] acl = FileDescriptor(fixture.FilePath);
        byte[] appAcl = DirectoryDescriptor(fixture.AppRoot);
        byte[] workspaceAcl = DirectoryDescriptor(workspace);

        SettingsTextFileReadResult result = file.Read(profile);
        AssertReadResult(snapshot, raw, result);
        AssertIdentity(exported, result.Identity);
        AssertFailure(() => file.Read(CreateProfile()), SettingsTextFileFailureReason.ProfileMismatch, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(retained));
        foreach (string name in databaseNames)
        {
            CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(Path.Combine(workspace, name)));
        }

        CollectionAssert.AreEqual(appAcl, DirectoryDescriptor(fixture.AppRoot));
        CollectionAssert.AreEqual(workspaceAcl, DirectoryDescriptor(workspace));
        Assert.IsFalse(File.Exists(Path.Combine(workspace, "setting.txt")));
        AssertRootEntries(fixture, "Workspace", "setting.txt", ".setting-retained.tmp");
    }

    [TestMethod]
    [DataRow("content")]
    [DataRow("acl")]
    [DataRow("oversized")]
    [DataRow("missing")]
    public async Task Read_WaitsForTheExistingCrossProcessGateAndChecksFreshFileState(string variation)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        byte[] before = StrictUtf8.GetBytes(CreateSnapshot(profile).CanonicalJson);
        WriteProtectedFile(fixture.FilePath, before, fixture.CurrentSid);
        DefaultSettingsSnapshot replacement = CreateSnapshot(profile, integer: 2);
        byte[] changed = variation == "oversized"
            ? new byte[SettingsTextFile.MaximumRawUtf8Bytes + 1]
            : StrictUtf8.GetBytes(replacement.CanonicalJson);
        var file = new SettingsTextFile(fixture.AppRoot.ToUpperInvariant().Replace('\\', '/') + "/", fixture.CurrentSid);
        using Process owner = await StartMutexOwnerAsync(fixture);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = Task.Run(() =>
        {
            started.SetResult();
            if (variation == "content")
            {
                AssertReadResult(replacement, changed, file.Read(profile));
            }
            else
            {
                SettingsTextFileFailureReason expected = variation switch
                {
                    "acl" => SettingsTextFileFailureReason.AccessDenied,
                    "oversized" => SettingsTextFileFailureReason.TooLarge,
                    _ => SettingsTextFileFailureReason.Missing,
                };
                AssertFailure(() => file.Read(profile), expected, fixture);
            }
        });
        try
        {
            await started.Task.WaitAsync(ProcessTimeout);
            Assert.AreNotSame(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(200))),
                "Read bypassed the child-owned settings mutex.");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
            if (variation == "missing")
            {
                File.Delete(fixture.FilePath);
            }
            else
            {
                File.WriteAllBytes(fixture.FilePath, changed);
                if (variation == "acl")
                {
                    FileSecurity security = ReadFileSecurity(fixture.FilePath);
                    security.AddAccessRule(new FileSystemAccessRule(
                        new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                    FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), security);
                }
            }

            byte[]? acl = variation == "missing" ? null : FileDescriptor(fixture.FilePath);
            await FinishMutexOwnerAsync(owner, abandon: false);
            await pending.WaitAsync(ProcessTimeout);
            AssertOriginalOrAbsent(fixture, variation == "missing" ? null : changed);
            if (acl is not null)
            {
                CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
            }
        }
        finally
        {
            await StopMutexOwnerAsync(owner);
            await pending.WaitAsync(ProcessTimeout);
        }
    }

    [TestMethod]
    public async Task Read_CrossProcessWaitIsCancellableWithoutBlockingAnotherRootOrLeakingOwnership()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(profile);
        byte[] raw = StrictUtf8.GetBytes(snapshot.CanonicalJson);
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        using Process owner = await StartMutexOwnerAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = Task.Run(() =>
        {
            started.SetResult();
            OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(() => file.Read(profile, cancellation.Token));
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        });
        try
        {
            await started.Task.WaitAsync(ProcessTimeout);
            Assert.AreNotSame(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(200))),
                "Read bypassed the child-owned settings mutex.");
            using (AppFixture other = new())
            using (var deadline = new CancellationTokenSource(ProcessTimeout))
            {
                WriteProtectedFile(other.FilePath, raw, other.CurrentSid);
                AssertReadResult(snapshot, raw, new SettingsTextFile(other.AppRoot, other.CurrentSid).Read(profile, deadline.Token));
            }

            cancellation.Cancel();
            await pending.WaitAsync(ProcessTimeout);
            Assert.IsFalse(owner.HasExited);
            AssertOriginalOrAbsent(fixture, raw);
            await FinishMutexOwnerAsync(owner, abandon: false);
            SettingsTextFileReadResult result = await Task.Run(() => file.Read(profile)).WaitAsync(ProcessTimeout);
            AssertReadResult(snapshot, raw, result);
            AssertOriginalOrAbsent(fixture, raw);
        }
        finally
        {
            cancellation.Cancel();
            await StopMutexOwnerAsync(owner);
            await pending.WaitAsync(ProcessTimeout);
        }
    }

    [TestMethod]
    [DataRow(9007199254740993L, 30000L, 1001L)]
    [DataRow(long.MaxValue, long.MinValue, long.MaxValue)]
    [DataRow(long.MinValue, long.MaxValue, 10L)]
    [DataRow(1280L, 48000L, 1000L)]
    public void Export_AllFiveKindsRoundTripCanonicalWithExactNumbersAndRawEvidence(
        long integer,
        long numerator,
        long denominator)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(profile, integer, numerator, denominator);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        SettingsTextFileIdentity exported = file.Export(snapshot, expectedExistingSha256: null);

        byte[] raw = File.ReadAllBytes(fixture.FilePath);
        string text = StrictUtf8.GetString(raw);
        Assert.IsFalse(raw.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.IsFalse(text.Contains('\r'));
        Assert.IsTrue(text.Contains("\n  \"", StringComparison.Ordinal));
        Assert.IsTrue(text.EndsWith('\n'));
        Assert.IsTrue(raw.Length <= SettingsTextFile.MaximumRawUtf8Bytes);
        Assert.AreEqual(raw.LongLength, exported.ByteSize);
        Assert.AreEqual(ContentDigest.Compute(raw), exported.FileSha256);
        Assert.AreNotEqual(snapshot.ContentDigest, exported.FileSha256);
        AssertIdentity(exported, file.InspectIdentity());
        AssertRoundTrip(snapshot, profile, raw);
        using JsonDocument source = JsonDocument.Parse(snapshot.CanonicalJson);
        using JsonDocument written = JsonDocument.Parse(raw);
        CollectionAssert.AreEqual(
            NumberTokens(source.RootElement).ToArray(),
            NumberTokens(written.RootElement).ToArray());
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_IsOutsideWorkspaceAndDoesNotTouchDirectoryAclsOrDatabaseSentinels()
    {
        using AppFixture fixture = new();
        string workspace = Path.Combine(fixture.AppRoot, "Workspace");
        _ = WorkspaceAcl.CreateNew(workspace, fixture.CurrentSid);
        // These opaque bytes are deliberately not a database: the utility must not open it.
        string[] databaseNames = ["hubradio.db", "hubradio.db-wal", "hubradio.db-shm"];
        byte[] sentinel = [0, 1, 2, 255, 13, 10];
        foreach (string name in databaseNames)
        {
            File.WriteAllBytes(Path.Combine(workspace, name), sentinel);
        }

        byte[] appAcl = DirectoryDescriptor(fixture.AppRoot);
        byte[] workspaceAcl = DirectoryDescriptor(workspace);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity first = file.Export(CreateSnapshot(profile), null);
        SettingsTextFileIdentity second = file.Export(CreateSnapshot(profile, integer: 2), first.FileSha256);
        AssertFailure(() => file.Export(CreateSnapshot(profile), first.FileSha256), SettingsTextFileFailureReason.Conflict, fixture);

        AssertIdentity(second, file.InspectIdentity());
        AssertRootEntries(fixture, "Workspace", "setting.txt");
        Assert.IsFalse(File.Exists(Path.Combine(workspace, "setting.txt")));
        foreach (string name in databaseNames)
        {
            CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(Path.Combine(workspace, name)));
        }

        CollectionAssert.AreEqual(appAcl, DirectoryDescriptor(fixture.AppRoot));
        CollectionAssert.AreEqual(workspaceAcl, DirectoryDescriptor(workspace));
    }

    [TestMethod]
    public void Export_NullExpectedHashRequiresConfirmationEvenForIdenticalContent()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        SettingsTextFileIdentity first = file.Export(snapshot, null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        byte[] acl = FileDescriptor(fixture.FilePath);

        SettingsTextFileException failure = AssertFailure(
            () => file.Export(snapshot, null), SettingsTextFileFailureReason.OverwriteRequired, fixture);

        Assert.AreEqual(ContentDigest.Compute(before), failure.ExistingFileSha256);
        Assert.AreEqual(first.FileSha256, failure.ExistingFileSha256);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_WrongHashPreservesOldFileAndAclAndDoesNotCreateStaging()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsProfile profile = CreateProfile();
        _ = file.Export(CreateSnapshot(profile), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        byte[] acl = FileDescriptor(fixture.FilePath);

        AssertFailure(() => file.Export(CreateSnapshot(profile, integer: 2), WrongHash), SettingsTextFileFailureReason.Conflict, fixture);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_ExpectedHashForMissingFileIsConflictNotCreatePermission()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        AssertFailure(() => file.Export(CreateSnapshot(CreateProfile()), WrongHash), SettingsTextFileFailureReason.Conflict, fixture);

        Assert.IsNull(file.InspectIdentity());
        AssertRootEntries(fixture);
    }

    [TestMethod]
    public void Export_ConfirmedReplacementValidatesNewEvidenceAndRejectsStaleConfirmation()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = file.Export(CreateSnapshot(profile), null);
        DefaultSettingsSnapshot replacement = CreateSnapshot(profile, integer: 99);
        byte[] appAcl = DirectoryDescriptor(fixture.AppRoot);

        SettingsTextFileIdentity result = file.Export(replacement, original.FileSha256);
        byte[] committed = File.ReadAllBytes(fixture.FilePath);
        AssertRoundTrip(replacement, profile, committed);
        Assert.AreNotEqual(original.FileSha256, result.FileSha256);
        Assert.AreEqual(ContentDigest.Compute(committed), result.FileSha256);
        Assert.AreEqual(committed.LongLength, result.ByteSize);
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        var restarted = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        AssertIdentity(result, restarted.InspectIdentity());
        AssertFailure(() => restarted.Export(CreateSnapshot(profile), original.FileSha256), SettingsTextFileFailureReason.Conflict, fixture);

        CollectionAssert.AreEqual(committed, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(appAcl, DirectoryDescriptor(fixture.AppRoot));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void InspectIdentity_HashesOriginalBytesWithoutImportingOrNormalizingThem()
    {
        using AppFixture fixture = new();
        byte[] raw = [0xef, 0xbb, 0xbf, 0xff, 0x00, 13, 10, 32];
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        SettingsTextFileIdentity identity = file.InspectIdentity()
            ?? throw new AssertFailedException("The existing file was not inspected.");
        Assert.AreEqual(ContentDigest.Compute(raw), identity.FileSha256);
        Assert.AreEqual(raw.LongLength, identity.ByteSize);
        SettingsTextFileException confirmation = AssertFailure(
            () => file.Export(CreateSnapshot(CreateProfile()), null),
            SettingsTextFileFailureReason.OverwriteRequired,
            fixture);
        Assert.AreEqual(identity.FileSha256, confirmation.ExistingFileSha256);
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        byte[] different = [0xef, 0xbb, 0xbf, 0xff, 0x00, 10, 32];
        File.WriteAllBytes(fixture.FilePath, different);
        Assert.AreNotEqual(identity.FileSha256, file.InspectIdentity()!.FileSha256);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4 * 1024 * 1024 - 1)]
    [DataRow(4 * 1024 * 1024)]
    public void InspectIdentity_AcceptsEmptyAndRawSizeBoundary(int byteSize)
    {
        using AppFixture fixture = new();
        byte[] raw = new byte[byteSize];
        Array.Fill(raw, (byte)' ');
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        SettingsTextFileIdentity? actual = file.InspectIdentity();

        Assert.IsNotNull(actual);
        Assert.AreEqual((long)byteSize, actual.ByteSize);
        Assert.AreEqual(ContentDigest.Compute(raw), actual.FileSha256);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void InspectAndExport_RejectOversizedRawFileWithoutChangingIt()
    {
        using AppFixture fixture = new();
        byte[] raw = new byte[SettingsTextFile.MaximumRawUtf8Bytes + 1];
        Array.Fill(raw, (byte)' ');
        WriteProtectedFile(fixture.FilePath, raw, fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.TooLarge, fixture);
        AssertFailure(() => file.Export(CreateSnapshot(CreateProfile()), ContentDigest.Compute(raw)), SettingsTextFileFailureReason.TooLarge, fixture);

        CollectionAssert.AreEqual(raw, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(1024 * 1024 - 1)]
    [DataRow(1024 * 1024)]
    public void Export_AcceptsCanonicalSizeBoundaryAndRoundTrips(int canonicalByteSize)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateTextProfile();
        DefaultSettingsSnapshot snapshot = SnapshotOfCanonicalSize(profile, canonicalByteSize);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);

        SettingsTextFileIdentity evidence = file.Export(snapshot, null);

        Assert.IsTrue(evidence.ByteSize <= SettingsTextFile.MaximumRawUtf8Bytes);
        AssertRoundTrip(snapshot, profile, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_RejectsOversizedCanonicalSourceAndPreservesOldFile()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateTextProfile();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity first = file.Export(profile.CreateSnapshot(), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        DefaultSettingsSnapshot tooLarge = SnapshotOfCanonicalSize(profile, SettingsTextFile.MaximumCanonicalJsonUtf8Bytes + 1);

        AssertFailure(() => file.Export(tooLarge, first.FileSha256), SettingsTextFileFailureReason.TooLarge, fixture);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_NullSnapshotIsTypedInvalidWithoutFileMutation()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity first = file.Export(CreateSnapshot(CreateProfile()), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);

        AssertFailure(() => file.Export(null!, first.FileSha256), SettingsTextFileFailureReason.Invalid, fixture);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_PreCancelledTokenLeavesAbsentOrExistingFileUntouched(bool existing)
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        ContentDigest? expected = existing ? file.Export(snapshot, null).FileSha256 : null;
        byte[]? before = existing ? File.ReadAllBytes(fixture.FilePath) : null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(
            () => file.Export(snapshot, expected, cancellation.Token));

        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        AssertOriginalOrAbsent(fixture, before);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_CancellationAfterStagingVerifiesPrivateTemporaryAclsAndCleansOnlyOwnFiles(bool existing)
    {
        using AppFixture fixture = new();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        var original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        ContentDigest? expected = existing ? original.Export(snapshot, null).FileSha256 : null;
        byte[]? before = existing ? File.ReadAllBytes(fixture.FilePath) : null;
        string foreign = Path.Combine(fixture.AppRoot, ".setting-unrelated.tmp");
        byte[] foreignBytes = [42, 19, 7];
        File.WriteAllBytes(foreign, foreignBytes);
        using var cancellation = new CancellationTokenSource();
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, () =>
        {
            callbacks++;
            string[] ownFiles = Directory.GetFiles(fixture.AppRoot, ".setting-*.tmp")
                .Where(path => !string.Equals(path, foreign, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(existing ? 2 : 1, ownFiles.Length);
            foreach (string path in ownFiles)
            {
                AssertFileAcl(path, fixture.CurrentSid);
            }

            cancellation.Cancel();
        });

        Assert.ThrowsExactly<OperationCanceledException>(() => file.Export(snapshot, expected, cancellation.Token));

        Assert.AreEqual(1, callbacks);
        CollectionAssert.AreEqual(foreignBytes, File.ReadAllBytes(foreign));
        if (before is not null)
        {
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        }
        else
        {
            Assert.IsFalse(File.Exists(fixture.FilePath));
        }

        AssertRootEntries(fixture, existing ? ["setting.txt", ".setting-unrelated.tmp"] : [".setting-unrelated.tmp"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_CancellationAfterFinalVerificationDoesNotPublishAndReleasesTarget(bool existing)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        var original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        ContentDigest? expected = existing ? original.Export(CreateSnapshot(profile), null).FileSha256 : null;
        byte[]? before = existing ? File.ReadAllBytes(fixture.FilePath) : null;
        using var cancellation = new CancellationTokenSource();
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, beforeCommit: null,
            afterFinalVerification: () =>
            {
                callbacks++;
                cancellation.Cancel();
            });
        DefaultSettingsSnapshot replacement = CreateSnapshot(profile, integer: 2);

        OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(
            () => file.Export(replacement, expected, cancellation.Token));

        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        Assert.AreEqual(1, callbacks);
        AssertOriginalOrAbsent(fixture, before);
        if (before is not null)
        {
            // The cancelled final-verification handle must no longer deny ordinary writes.
            File.WriteAllBytes(fixture.FilePath, before);
        }

        var restarted = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity exported = restarted.Export(replacement, expected);
        SettingsTextFileReadResult read = restarted.Read(profile);
        AssertReadResult(replacement, File.ReadAllBytes(fixture.FilePath), read);
        AssertIdentity(exported, read.Identity);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_RechecksOldRawHashAfterStagingAndPreservesConcurrentEdit()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid)
            .Export(CreateSnapshot(profile), null);
        byte[] changed = File.ReadAllBytes(fixture.FilePath);
        changed[^1] = (byte)' ';
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid,
            () => File.WriteAllBytes(fixture.FilePath, changed));

        AssertFailure(() => file.Export(CreateSnapshot(profile, integer: 2), original.FileSha256), SettingsTextFileFailureReason.Conflict, fixture);

        CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
        Assert.AreEqual(ContentDigest.Compute(changed), file.InspectIdentity()!.FileSha256);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_FinalVerificationBlocksInPlaceWriteWithSharingViolationButAllowsReplacement()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid)
            .Export(CreateSnapshot(profile), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        FileSecurity acl = ReadFileSecurity(fixture.FilePath);
        byte[] changed = [19, 42, 13, 10];
        // Positive control: this very writer and DACL permit in-place writes without the guard.
        File.WriteAllBytes(fixture.FilePath, changed);
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
        File.WriteAllBytes(fixture.FilePath, before);
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, beforeCommit: null,
            afterFinalVerification: () =>
            {
                callbacks++;
                IOException failure = Assert.ThrowsExactly<IOException>(
                    () => File.WriteAllBytes(fixture.FilePath, changed));
                Assert.AreEqual(32, failure.HResult & 0xffff, "The guard must deny sharing, not ACL access.");
                CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
            });
        DefaultSettingsSnapshot replacement = CreateSnapshot(profile, integer: 2);

        // The same held read/delete-sharing handle must allow our actual Windows replacement.
        SettingsTextFileIdentity exported = file.Export(replacement, original.FileSha256);

        Assert.AreEqual(1, callbacks);
        var restarted = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileReadResult read = restarted.Read(profile);
        AssertReadResult(replacement, File.ReadAllBytes(fixture.FilePath), read);
        AssertIdentity(exported, read.Identity);
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRestoredAcl(acl, ReadFileSecurity(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow("invalidUtf8")]
    [DataRow("empty")]
    [DataRow("staleDigest")]
    public void Export_AtomicReplacementAfterFinalVerificationRestoresActualDisplacedRawFile(string variation)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid)
            .Export(CreateSnapshot(profile), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        DefaultSettingsSnapshot concurrent = CreateSnapshot(profile, integer: 3);
        byte[] changed = variation switch
        {
            "invalidUtf8" => [0xef, 0xbb, 0xbf, 0xff, 0x00, 13, 10, 32],
            "empty" => [],
            "staleDigest" => StrictUtf8.GetBytes("\uFEFF" + concurrent.CanonicalJson.Replace(
                concurrent.ContentDigest.Hex, WrongHash.Hex, StringComparison.Ordinal) + "\r\n"),
            _ => throw new AssertFailedException("Unknown concurrent input variation."),
        };
        string replacementPath = Path.Combine(fixture.RootPath, "atomic-b.tmp");
        string displacedAPath = Path.Combine(fixture.RootPath, "displaced-a.tmp");
        WriteProtectedFile(replacementPath, changed, fixture.CurrentSid);
        FileSecurity? changedAcl = null;
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, beforeCommit: null,
            afterFinalVerification: () =>
            {
                callbacks++;
                // No settings mutex: the guard intentionally shares delete with an atomic writer.
                File.Replace(replacementPath, fixture.FilePath, displacedAPath, ignoreMetadataErrors: false);
                CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
                AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
                // ReplaceFile preserves the destination DACL, not the incoming file's descriptor.
                changedAcl = ReadFileSecurity(fixture.FilePath);
            });

        AssertFailure(() => file.Export(CreateSnapshot(profile, integer: 2), original.FileSha256),
            SettingsTextFileFailureReason.Conflict, fixture);

        Assert.AreEqual(1, callbacks);
        Assert.IsNotNull(changedAcl);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(displacedAPath));
        var restarted = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        AssertIdentity(new SettingsTextFileIdentity(ContentDigest.Compute(changed), changed.LongLength), restarted.InspectIdentity());
        AssertFailure(() => restarted.Read(profile), variation == "staleDigest"
            ? SettingsTextFileFailureReason.SnapshotMismatch : SettingsTextFileFailureReason.Invalid, fixture);
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
        AssertRestoredAcl(changedAcl, ReadFileSecurity(fixture.FilePath));
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_RecoveryReplacementRetainsUnseenConcurrentFile(bool atomic)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid)
            .Export(CreateSnapshot(profile), null);
        DefaultSettingsSnapshot candidate = CreateSnapshot(profile, integer: 2);
        byte[] changed = [0xef, 0xbb, 0xbf, 0xff, 0x00, 13, 10, 32];
        byte[] unseen = [0xff, 0x7f, 4, 0, 13, 10];
        string replacementPath = Path.Combine(fixture.RootPath, "atomic-b.tmp");
        string displacedAPath = Path.Combine(fixture.RootPath, "displaced-a.tmp");
        string unseenPath = Path.Combine(fixture.RootPath, "atomic-d.tmp");
        string displacedCPath = Path.Combine(fixture.RootPath, "displaced-c.tmp");
        WriteProtectedFile(replacementPath, changed, fixture.CurrentSid);
        FileSecurity? changedAcl = null;
        FileSecurity? unseenAcl = null;
        int finalCallbacks = 0;
        int recoveryCallbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, beforeCommit: null,
            afterFinalVerification: () =>
            {
                finalCallbacks++;
                File.Replace(replacementPath, fixture.FilePath, displacedAPath, ignoreMetadataErrors: false);
                CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
                AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
                changedAcl = ReadFileSecurity(fixture.FilePath);
            },
            beforeRecoveryCommit: () =>
            {
                recoveryCallbacks++;
                AssertRoundTrip(candidate, profile, File.ReadAllBytes(fixture.FilePath));
                string[] backups = Directory.GetFiles(fixture.AppRoot, ".setting-*.tmp");
                Assert.AreEqual(2, backups.Length);
                foreach (string backup in backups)
                {
                    AssertFileAcl(backup, fixture.CurrentSid);
                }

                if (atomic)
                {
                    WriteProtectedFile(unseenPath, unseen, fixture.CurrentSid);
                    File.Replace(unseenPath, fixture.FilePath, displacedCPath, ignoreMetadataErrors: false);
                }
                else
                {
                    File.WriteAllBytes(fixture.FilePath, unseen);
                }

                CollectionAssert.AreEqual(unseen, File.ReadAllBytes(fixture.FilePath));
                AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
                unseenAcl = ReadFileSecurity(fixture.FilePath);
            });

        AssertFailure(() => file.Export(candidate, original.FileSha256),
            SettingsTextFileFailureReason.Unavailable, fixture, reconciliationRequired: true);

        Assert.AreEqual(1, finalCallbacks);
        Assert.AreEqual(1, recoveryCallbacks);
        Assert.IsNotNull(changedAcl);
        Assert.IsNotNull(unseenAcl);
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRestoredAcl(changedAcl, ReadFileSecurity(fixture.FilePath));
        string retained = Directory.GetFiles(fixture.AppRoot, ".setting-*.tmp").Single();
        CollectionAssert.AreEqual(unseen, File.ReadAllBytes(retained));
        AssertRestoredAcl(unseenAcl, ReadFileSecurity(retained));
        AssertFileAcl(retained, fixture.CurrentSid);
        var restarted = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        AssertIdentity(new SettingsTextFileIdentity(ContentDigest.Compute(changed), changed.LongLength), restarted.InspectIdentity());
        CollectionAssert.AreEqual(unseen, File.ReadAllBytes(retained));
        if (atomic)
        {
            AssertRoundTrip(candidate, profile, File.ReadAllBytes(displacedCPath));
        }

        AssertRootEntries(fixture, "setting.txt", Path.GetFileName(retained));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_UnverifiableDisplacedBackupIsRetainedWithoutRepair(bool invalidAcl)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid)
            .Export(CreateSnapshot(profile), null);
        DefaultSettingsSnapshot candidate = CreateSnapshot(profile, integer: 2);
        byte[] changed = invalidAcl ? [0xff, 0, 13, 10] : new byte[SettingsTextFile.MaximumRawUtf8Bytes + 1];
        string replacementPath = Path.Combine(fixture.RootPath, "atomic-b.tmp");
        string displacedAPath = Path.Combine(fixture.RootPath, "displaced-a.tmp");
        WriteProtectedFile(replacementPath, changed, fixture.CurrentSid);
        byte[] changedAcl = FileDescriptor(replacementPath);
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, beforeCommit: null,
            afterFinalVerification: () =>
            {
                callbacks++;
                File.Replace(replacementPath, fixture.FilePath, displacedAPath, ignoreMetadataErrors: false);
                if (invalidAcl)
                {
                    FileSecurity security = ReadFileSecurity(fixture.FilePath);
                    security.AddAccessRule(new FileSystemAccessRule(
                        new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                    FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), security);
                }

                changedAcl = FileDescriptor(fixture.FilePath);
            },
            beforeRecoveryCommit: () => Assert.Fail("An unverified displaced backup must not be used for recovery."));

        AssertFailure(() => file.Export(candidate, original.FileSha256),
            SettingsTextFileFailureReason.Unavailable, fixture, reconciliationRequired: true);

        Assert.AreEqual(1, callbacks);
        string retained = Directory.GetFiles(fixture.AppRoot, ".setting-*.tmp").Single();
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(retained));
        CollectionAssert.AreEqual(changedAcl, FileDescriptor(retained));
        // Verification fails closed; neither backup bytes nor the inherited replacement DACL are repaired.
        AssertRoundTrip(candidate, profile, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(changedAcl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt", Path.GetFileName(retained));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Export_FailedInitialReplaceRestoresOnlyVerifiedOriginalNotReservedEmpty(bool backupHasOriginal, bool originalEmpty)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        var initial = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity original = initial.Export(CreateSnapshot(profile), null);
        if (originalEmpty)
        {
            File.WriteAllBytes(fixture.FilePath, []);
            original = initial.InspectIdentity()
                ?? throw new AssertFailedException("The empty original was not inspected.");
        }

        byte[] before = File.ReadAllBytes(fixture.FilePath);
        byte[] acl = FileDescriptor(fixture.FilePath);
        string displacedPath = Path.Combine(fixture.RootPath, "displaced-original.tmp");
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, beforeCommit: null,
            afterFinalVerification: () =>
            {
                callbacks++;
                if (backupHasOriginal)
                {
                    // Reproduce the documented partial-rename layout, without a native fault injector.
                    displacedPath = Directory.GetFiles(fixture.AppRoot, ".setting-*.tmp")
                        .Single(path => new FileInfo(path).Length == 0);
                }

                File.Move(fixture.FilePath, displacedPath, overwrite: backupHasOriginal);
                // The initial File.Replace will really fail: its fixed destination is now missing.
            });

        bool canRestore = backupHasOriginal && !originalEmpty;
        AssertFailure(() => file.Export(CreateSnapshot(profile, integer: 2), original.FileSha256),
            canRestore ? SettingsTextFileFailureReason.Conflict : SettingsTextFileFailureReason.Unavailable,
            fixture, reconciliationRequired: !canRestore);

        Assert.AreEqual(1, callbacks);
        if (canRestore)
        {
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
            CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
            AssertRootEntries(fixture, "setting.txt");
        }
        else
        {
            Assert.IsFalse(File.Exists(fixture.FilePath), "A failed replacement cannot prove that an empty backup was displaced.");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(displacedPath));
            CollectionAssert.AreEqual(acl, FileDescriptor(displacedPath));
            string retained = Directory.GetFiles(fixture.AppRoot, ".setting-*.tmp").Single();
            if (backupHasOriginal)
            {
                Assert.AreEqual(displacedPath, retained, "The ambiguous empty original must remain at its backup path.");
            }

            Assert.IsEmpty(File.ReadAllBytes(retained));
            AssertFileAcl(retained, fixture.CurrentSid);
            AssertRootEntries(fixture, Path.GetFileName(retained));
        }
    }

    [TestMethod]
    public void Export_FailedInitialReplaceKeepsUnchangedEmptyOriginalWithoutReconciliation()
    {
        using AppFixture fixture = new();
        WriteProtectedFile(fixture.FilePath, [], fixture.CurrentSid);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity original = file.InspectIdentity()
            ?? throw new AssertFailedException("The empty original was not inspected.");
        byte[] acl = FileDescriptor(fixture.FilePath);
        using (var locked = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Replace really fails, but the unchanged empty original still proves recovery unnecessary.
            AssertFailure(() => file.Export(CreateSnapshot(CreateProfile()), original.FileSha256),
                SettingsTextFileFailureReason.Unavailable, fixture);
        }

        AssertIdentity(original, file.InspectIdentity());
        Assert.IsEmpty(File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_FileAppearingAfterStagingRequiresItsOwnConfirmation()
    {
        using AppFixture fixture = new();
        byte[] appeared = [1, 2, 3, 13, 10];
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid,
            () => WriteProtectedFile(fixture.FilePath, appeared, fixture.CurrentSid));

        SettingsTextFileException failure = AssertFailure(
            () => file.Export(CreateSnapshot(CreateProfile()), null),
            SettingsTextFileFailureReason.OverwriteRequired,
            fixture);

        Assert.AreEqual(ContentDigest.Compute(appeared), failure.ExistingFileSha256);
        CollectionAssert.AreEqual(appeared, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_FileAppearingAfterFinalVerificationIsNeverOverwritten()
    {
        using AppFixture fixture = new();
        byte[] appeared = [1, 2, 3, 13, 10];
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, beforeCommit: null,
            afterFinalVerification: () =>
            {
                callbacks++;
                WriteProtectedFile(fixture.FilePath, appeared, fixture.CurrentSid);
            });

        SettingsTextFileException failure = AssertFailure(
            () => file.Export(CreateSnapshot(CreateProfile()), null),
            SettingsTextFileFailureReason.OverwriteRequired, fixture);

        Assert.AreEqual(1, callbacks);
        Assert.AreEqual(ContentDigest.Compute(appeared), failure.ExistingFileSha256);
        CollectionAssert.AreEqual(appeared, File.ReadAllBytes(fixture.FilePath));
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_FileDeletedAfterStagingDoesNotBecomeAnUnconfirmedCreation()
    {
        using AppFixture fixture = new();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid).Export(snapshot, null);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, () => File.Delete(fixture.FilePath));

        AssertFailure(() => file.Export(snapshot, original.FileSha256), SettingsTextFileFailureReason.Conflict, fixture);

        AssertRootEntries(fixture);
    }

    [TestMethod]
    public void Export_PreCommitIoFailureIsRedactedAndLeavesOldFileAndForeignTemporaryFileAlone()
    {
        using AppFixture fixture = new();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid).Export(snapshot, null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        string foreign = Path.Combine(fixture.AppRoot, ".setting-unrelated.tmp");
        File.WriteAllBytes(foreign, [17]);
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid,
            () => throw new IOException(fixture.FilePath + " private-settings-body"));

        SettingsTextFileException failure = AssertFailure(
            () => file.Export(snapshot, original.FileSha256), SettingsTextFileFailureReason.Unavailable, fixture);

        Assert.IsFalse(failure.ToString().Contains("private-settings-body", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(new byte[] { 17 }, File.ReadAllBytes(foreign));
        AssertRootEntries(fixture, "setting.txt", ".setting-unrelated.tmp");
    }

    [TestMethod]
    public void InspectAndExport_ExclusiveFileLockFailsWithoutMutation()
    {
        using AppFixture fixture = new();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity original = file.Export(snapshot, null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        using (var locked = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.Unavailable, fixture);
            AssertFailure(() => file.Export(snapshot, original.FileSha256), SettingsTextFileFailureReason.Unavailable, fixture);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Export_DenyDeleteSharingExercisesActualReplaceFailureAndPreservesOldFile()
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity original = file.Export(CreateSnapshot(profile), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        byte[] acl = FileDescriptor(fixture.FilePath);
        using (var locked = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AssertIdentity(original, file.InspectIdentity());
            AssertFailure(() => file.Export(CreateSnapshot(profile, integer: 2), original.FileSha256), SettingsTextFileFailureReason.Unavailable, fixture);
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_DeniedDirectoryWriteFailsWithoutRepairingRootAcl(bool existing)
    {
        using AppFixture fixture = new();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        ContentDigest? expected = existing ? file.Export(snapshot, null).FileSha256 : null;
        byte[]? before = existing ? File.ReadAllBytes(fixture.FilePath) : null;
        DirectorySecurity denied = ReadDirectorySecurity(fixture.AppRoot);
        var denialRule = new FileSystemAccessRule(
            new SecurityIdentifier(fixture.CurrentSid),
            FileSystemRights.CreateFiles,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        denied.AddAccessRule(denialRule);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(fixture.AppRoot), denied);
        try
        {
            byte[] deniedDescriptor = DirectoryDescriptor(fixture.AppRoot);
            AssertFailure(() => file.Export(snapshot, expected), SettingsTextFileFailureReason.AccessDenied, fixture);
            CollectionAssert.AreEqual(deniedDescriptor, DirectoryDescriptor(fixture.AppRoot));
            AssertOriginalOrAbsent(fixture, before);
        }
        finally
        {
            denied.RemoveAccessRuleSpecific(denialRule);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(fixture.AppRoot), denied);
        }
    }

    [TestMethod]
    public void Export_ReadOnlyTargetFailurePreservesOldBytesAndAttributes()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = file.Export(CreateSnapshot(profile), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        FileAttributes attributes = File.GetAttributes(fixture.FilePath);
        File.SetAttributes(fixture.FilePath, attributes | FileAttributes.ReadOnly);
        try
        {
            AssertIdentity(original, file.InspectIdentity());
            AssertFailure(() => file.Export(CreateSnapshot(profile, integer: 2), original.FileSha256), SettingsTextFileFailureReason.AccessDenied, fixture);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
            Assert.AreEqual(attributes | FileAttributes.ReadOnly, File.GetAttributes(fixture.FilePath));
            AssertRootEntries(fixture, "setting.txt");
        }
        finally
        {
            File.SetAttributes(fixture.FilePath, attributes);
        }
    }

    [TestMethod]
    public void InspectAndExport_DeniedFileReadIsNotReportedAsMissing()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        SettingsTextFileIdentity original = file.Export(snapshot, null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        byte[] originalAcl = FileDescriptor(fixture.FilePath);
        FileSecurity denied = ReadFileSecurity(fixture.FilePath);
        var denialRule = new FileSystemAccessRule(
            new SecurityIdentifier(fixture.CurrentSid), FileSystemRights.ReadData, AccessControlType.Deny);
        denied.AddAccessRule(denialRule);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), denied);
        try
        {
            byte[] deniedDescriptor = FileDescriptor(fixture.FilePath);
            AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.AccessDenied, fixture);
            AssertFailure(() => file.Export(snapshot, original.FileSha256), SettingsTextFileFailureReason.AccessDenied, fixture);
            CollectionAssert.AreEqual(deniedDescriptor, FileDescriptor(fixture.FilePath));
            AssertRootEntries(fixture, "setting.txt");
        }
        finally
        {
            denied.RemoveAccessRuleSpecific(denialRule);
            FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), denied);
        }

        AssertRestoredAcl(originalAcl, FileDescriptor(fixture.FilePath));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
    }

    [TestMethod]
    [DataRow("inherited")]
    [DataRow("extra")]
    [DataRow("missing")]
    [DataRow("deny")]
    [DataRow("rights")]
    public void InspectAndExport_RejectInvalidFileAclWithoutChangingIt(string variation)
    {
        using AppFixture fixture = new();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity original = file.Export(snapshot, null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        FileSecurity security = ReadFileSecurity(fixture.FilePath);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        switch (variation)
        {
            case "inherited":
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                break;
            case "extra":
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                break;
            case "missing":
            case "rights":
                FileSystemAccessRule rule = Rules(security).Single(entry => entry.IdentityReference.Equals(systemSid));
                security.RemoveAccessRuleSpecific(rule);
                if (variation == "rights")
                {
                    security.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.Read, AccessControlType.Allow));
                }

                break;
            case "deny":
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier("S-1-5-21-1-2-3-424242"), FileSystemRights.ReadData, AccessControlType.Deny));
                break;
        }

        FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), security);
        byte[] changedAcl = FileDescriptor(fixture.FilePath);

        AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.AccessDenied, fixture);
        AssertFailure(() => file.Export(snapshot, original.FileSha256), SettingsTextFileFailureReason.AccessDenied, fixture);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        CollectionAssert.AreEqual(changedAcl, FileDescriptor(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void InspectAndExport_RejectDifferentOwnerWhenOwnerChangeIsPermitted()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        SettingsTextFileIdentity original = file.Export(snapshot, null);
        FileSecurity security = ReadFileSecurity(fixture.FilePath);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        try
        {
            FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            Assert.Inconclusive("The runner cannot change file ownership to test this negative case.");
            return;
        }

        byte[] before = File.ReadAllBytes(fixture.FilePath);
        AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.AccessDenied, fixture);
        AssertFailure(() => file.Export(snapshot, original.FileSha256), SettingsTextFileFailureReason.AccessDenied, fixture);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Constructor_RejectsBadCurrentSidsWithNoDisclosureOrMutation()
    {
        using AppFixture fixture = new();
        string?[] invalid = [null, "", " ", "not-a-sid", "S-1-5-invalid", "S-1-5-18", "S-1-5-32-544", "S-1-1-0", "S-1-5-21-1-2-3-424242"];
        foreach (string? sid in invalid)
        {
            SettingsTextFileException failure = AssertFailure(
                () => new SettingsTextFile(fixture.AppRoot, sid!), SettingsTextFileFailureReason.Invalid, fixture);
            AssertNotDisclosed(failure, sid);
        }

        AssertRootEntries(fixture);
    }

    [TestMethod]
    public void Constructor_RejectsUnsafeNamespacesAndAmbiguousPaths()
    {
        using AppFixture fixture = new();
        string?[] paths =
        [
            null, "", " ", "relative-root", @"\drive-relative", @"C:drive-relative",
            @"C:\", @"\\server\share\private-root", @"\\?\C:\private-root", @"\\.\C:\private-root", @"\??\C:\private-root",
            fixture.AppRoot + ":stream", fixture.AppRoot + ".", fixture.AppRoot + " ",
            Path.Combine(fixture.AppRoot, "..", "other"), Path.Combine(fixture.AppRoot, "CON.txt"),
            Path.Combine(fixture.AppRoot, "bad*name"), Path.Combine(fixture.AppRoot, "bad\0name"),
        ];
        foreach (string? path in paths)
        {
            SettingsTextFileException failure = AssertFailure(
                () => new SettingsTextFile(path!, fixture.CurrentSid), SettingsTextFileFailureReason.UnsafePath, fixture);
            if (path == @"C:\")
            {
                // Debug stacks may include the source drive; the stable message must not.
                Assert.IsFalse(failure.Message.Contains(path, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                AssertNotDisclosed(failure, path);
            }
        }

        AssertRootEntries(fixture);
    }

    [TestMethod]
    public void Constructor_RejectsActualExecutableRootAndDescendants()
    {
        using AppFixture fixture = new();
        foreach (string path in new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "application-data"),
            Path.Combine(AppContext.BaseDirectory, "nested", "application-data").ToUpperInvariant(),
        })
        {
            AssertFailure(() => new SettingsTextFile(path, fixture.CurrentSid), SettingsTextFileFailureReason.UnsafePath, fixture);
        }

        AssertRootEntries(fixture);
    }

    [TestMethod]
    [DataRow("cache")]
    [DataRow("Workspace")]
    [DataRow("WebAssets")]
    [DataRow(".net")]
    [DataRow(".hubradio.restore-123")]
    [DataRow(".hubradio.previous-123")]
    public void Export_AcceptsBenignParentNamesWithoutChangingDirectoryAcls(string parentName)
    {
        using AppFixture fixture = new();
        string appRoot = Path.Combine(fixture.RootPath, parentName, "application");
        Directory.CreateDirectory(appRoot);
        byte[] rootAcl = DirectoryDescriptor(appRoot);
        var file = new SettingsTextFile(appRoot, fixture.CurrentSid);
        Assert.IsNull(file.InspectIdentity());

        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity first = file.Export(CreateSnapshot(profile), null);
        DefaultSettingsSnapshot replacement = CreateSnapshot(profile, integer: 2);
        SettingsTextFileIdentity second = file.Export(replacement, first.FileSha256);

        AssertIdentity(second, file.InspectIdentity());
        string path = Path.Combine(appRoot, "setting.txt");
        AssertRoundTrip(replacement, profile, File.ReadAllBytes(path));
        AssertFileAcl(path, fixture.CurrentSid);
        CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(appRoot));
        CollectionAssert.AreEquivalent(new[] { path }, Directory.GetFileSystemEntries(appRoot));
        AssertRootEntries(fixture);
    }

    [TestMethod]
    public void Constructor_RejectsMissingRootAndFileRootWithoutCreatingParents()
    {
        using AppFixture fixture = new();
        string missing = Path.Combine(fixture.RootPath, "missing", "child");
        string notDirectory = Path.Combine(fixture.RootPath, "file-root");
        File.WriteAllBytes(notDirectory, [1]);

        AssertFailure(() => new SettingsTextFile(missing, fixture.CurrentSid), SettingsTextFileFailureReason.UnsafePath, fixture);
        AssertFailure(() => new SettingsTextFile(notDirectory, fixture.CurrentSid), SettingsTextFileFailureReason.UnsafePath, fixture);

        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(missing)));
        CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(notDirectory));
        AssertRootEntries(fixture);
    }

    [TestMethod]
    public void InspectAndExport_RevalidateRootAfterConstructionWithoutRecreatingIt()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        SettingsTextFileIdentity original = file.Export(snapshot, null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        string displaced = Path.Combine(fixture.RootPath, "displaced");
        Directory.Move(fixture.AppRoot, displaced);

        AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.UnsafePath, fixture);
        AssertFailure(() => file.Export(snapshot, original.FileSha256), SettingsTextFileFailureReason.UnsafePath, fixture);

        Assert.IsFalse(Directory.Exists(fixture.AppRoot));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(displaced, "setting.txt")));
    }

    [TestMethod]
    public void InspectAndExport_RejectNonRegularFixedTarget()
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        Directory.CreateDirectory(fixture.FilePath);

        AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.UnsafePath, fixture);
        AssertFailure(() => file.Export(CreateSnapshot(CreateProfile()), null), SettingsTextFileFailureReason.UnsafePath, fixture);

        Assert.IsTrue(Directory.Exists(fixture.FilePath));
        AssertRootEntries(fixture, "setting.txt");
    }

    [TestMethod]
    public void Constructor_RejectsRootAndAncestorReparsePointsWhenLinksAreAvailable()
    {
        using AppFixture fixture = new();
        string link = Path.Combine(fixture.RootPath, "alias");
        CreateLinkOrInconclusive(link, fixture.AppRoot, directory: true);
        try
        {
            string child = Path.Combine(fixture.AppRoot, "child");
            Directory.CreateDirectory(child);
            AssertFailure(() => new SettingsTextFile(link, fixture.CurrentSid), SettingsTextFileFailureReason.UnsafePath, fixture);
            AssertFailure(() => new SettingsTextFile(Path.Combine(link, "child"), fixture.CurrentSid), SettingsTextFileFailureReason.UnsafePath, fixture);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(child));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InspectAndExport_RejectFileReparsePointIncludingDanglingLink(bool dangling)
    {
        using AppFixture fixture = new();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        string target = Path.Combine(fixture.RootPath, "other-file");
        if (!dangling)
        {
            File.WriteAllBytes(target, [23]);
        }

        CreateLinkOrInconclusive(fixture.FilePath, target, directory: false);
        try
        {
            AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.UnsafePath, fixture);
            AssertFailure(() => file.Export(CreateSnapshot(CreateProfile()), null), SettingsTextFileFailureReason.UnsafePath, fixture);
            if (dangling)
            {
                Assert.IsFalse(File.Exists(target));
            }
            else
            {
                CollectionAssert.AreEqual(new byte[] { 23 }, File.ReadAllBytes(target));
            }
        }
        finally
        {
            File.Delete(fixture.FilePath);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentInstancesHaveOnlyOneCreateOrReplaceWinner(bool replacing)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        var first = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        var second = new SettingsTextFile(fixture.AppRoot.ToUpperInvariant().Replace('\\', '/') + "/", fixture.CurrentSid);
        ContentDigest? expected = replacing ? first.Export(CreateSnapshot(profile), null).FileSha256 : null;
        using var release = new ManualResetEventSlim(false);
        Task<bool> left = Task.Run(() => Attempt(first, CreateSnapshot(profile, integer: 2)));
        Task<bool> right = Task.Run(() => Attempt(second, CreateSnapshot(profile, integer: 3)));
        release.Set();
        bool[] winners = await Task.WhenAll(left, right);

        Assert.AreEqual(1, winners.Count(static result => result));
        AssertIdentity(first.InspectIdentity()!, second.InspectIdentity());
        AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
        AssertRootEntries(fixture, "setting.txt");

        bool Attempt(SettingsTextFile file, DefaultSettingsSnapshot snapshot)
        {
            release.Wait();
            try
            {
                _ = file.Export(snapshot, expected);
                return true;
            }
            catch (SettingsTextFileException exception)
            {
                Assert.AreEqual(replacing ? SettingsTextFileFailureReason.Conflict : SettingsTextFileFailureReason.OverwriteRequired, exception.Reason);
                Assert.IsFalse(exception.ReconciliationRequired);
                return false;
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InspectAndExport_WaitForOtherProcessAndRecheckRawIdentityBeforeStaging(bool exporting)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        SettingsTextFileIdentity original = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid)
            .Export(CreateSnapshot(profile), null);
        byte[] before = File.ReadAllBytes(fixture.FilePath);
        byte[] changed = [17, 29, 13, 10];
        byte[] acl = FileDescriptor(fixture.FilePath);
        int callbacks = 0;
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid, () => Interlocked.Increment(ref callbacks));
        using Process owner = await StartMutexOwnerAsync(fixture);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = Task.Run(() =>
        {
            started.SetResult();
            if (exporting)
            {
                AssertFailure(() => file.Export(CreateSnapshot(profile, integer: 2), original.FileSha256),
                    SettingsTextFileFailureReason.Conflict, fixture);
            }
            else
            {
                AssertIdentity(new SettingsTextFileIdentity(ContentDigest.Compute(changed), changed.LongLength), file.InspectIdentity());
            }
        });
        try
        {
            await started.Task.WaitAsync(ProcessTimeout);
            Assert.AreNotSame(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(200))),
                "The operation bypassed the child-owned mutex.");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
            AssertRootEntries(fixture, "setting.txt");
            File.WriteAllBytes(fixture.FilePath, changed);
            await FinishMutexOwnerAsync(owner, abandon: false);
            await pending.WaitAsync(ProcessTimeout);

            Assert.AreEqual(0, Volatile.Read(ref callbacks));
            CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
            CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
            AssertRootEntries(fixture, "setting.txt");
        }
        finally
        {
            await StopMutexOwnerAsync(owner);
            await pending.WaitAsync(ProcessTimeout);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Export_CrossProcessWaitIsCancellableAndDoesNotBlockAnotherRoot(bool existing)
    {
        using AppFixture fixture = new();
        DefaultSettingsProfile profile = CreateProfile();
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        ContentDigest? expected = existing ? file.Export(CreateSnapshot(profile), null).FileSha256 : null;
        byte[]? before = existing ? File.ReadAllBytes(fixture.FilePath) : null;
        DefaultSettingsSnapshot replacement = CreateSnapshot(profile, integer: 2);
        using Process owner = await StartMutexOwnerAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = Task.Run(() =>
        {
            started.SetResult();
            OperationCanceledException failure = Assert.ThrowsExactly<OperationCanceledException>(
                () => file.Export(replacement, expected, cancellation.Token));
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        });
        try
        {
            await started.Task.WaitAsync(ProcessTimeout);
            Assert.AreNotSame(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(200))),
                "The export bypassed the child-owned mutex.");
            using (AppFixture other = new())
            using (var deadline = new CancellationTokenSource(ProcessTimeout))
            {
                var independent = new SettingsTextFile(other.AppRoot, other.CurrentSid);
                SettingsTextFileIdentity evidence = independent.Export(replacement, null, deadline.Token);
                Assert.AreEqual(evidence.FileSha256, ContentDigest.Compute(File.ReadAllBytes(other.FilePath)));
            }

            cancellation.Cancel();
            await pending.WaitAsync(ProcessTimeout);
            Assert.IsFalse(owner.HasExited);
            AssertOriginalOrAbsent(fixture, before);
            await FinishMutexOwnerAsync(owner, abandon: false);
            SettingsTextFileIdentity exported = await Task.Run(() => file.Export(replacement, expected)).WaitAsync(ProcessTimeout);
            AssertIdentity(exported, file.InspectIdentity());
            AssertFileAcl(fixture.FilePath, fixture.CurrentSid);
            AssertRootEntries(fixture, "setting.txt");
        }
        finally
        {
            cancellation.Cancel();
            await StopMutexOwnerAsync(owner);
            await pending.WaitAsync(ProcessTimeout);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task InspectAndExport_AbandonedMutexRevalidatesFilesWithoutCleaningRecoveryCopies(bool exporting, bool invalidAcl)
    {
        using AppFixture fixture = new();
        DefaultSettingsSnapshot snapshot = CreateSnapshot(CreateProfile());
        var file = new SettingsTextFile(fixture.AppRoot, fixture.CurrentSid);
        SettingsTextFileIdentity original = file.Export(snapshot, null);
        byte[] oldBytes = File.ReadAllBytes(fixture.FilePath);
        byte[] changed = [42, 19, 10];
        using Process owner = await StartMutexOwnerAsync(fixture);
        try
        {
            // Keep the kernel object alive so owner exit really delivers abandonment.
            using var lifetime = new Mutex(false, MutexName(fixture), new NamedWaitHandleOptions
            {
                CurrentUserOnly = true,
                CurrentSessionOnly = true,
            });
            bool acquired = lifetime.WaitOne(0);
            if (acquired)
            {
                lifetime.ReleaseMutex();
            }

            Assert.IsFalse(acquired, "The child must own the independently named mutex.");
            string retained = Path.Combine(fixture.AppRoot, ".setting-retained.tmp");
            WriteProtectedFile(retained, oldBytes, fixture.CurrentSid);
            File.WriteAllBytes(fixture.FilePath, changed);
            if (invalidAcl)
            {
                FileSecurity security = ReadFileSecurity(fixture.FilePath);
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                FileSystemAclExtensions.SetAccessControl(new FileInfo(fixture.FilePath), security);
            }

            byte[] acl = FileDescriptor(fixture.FilePath);
            byte[] rootAcl = DirectoryDescriptor(fixture.AppRoot);
            await FinishMutexOwnerAsync(owner, abandon: true);
            using var cancellation = new CancellationTokenSource();
            if (exporting)
            {
                AssertFailure(() => file.Export(snapshot, original.FileSha256, cancellation.Token),
                    invalidAcl ? SettingsTextFileFailureReason.AccessDenied : SettingsTextFileFailureReason.Conflict, fixture);
            }
            else if (invalidAcl)
            {
                AssertFailure(() => file.InspectIdentity(), SettingsTextFileFailureReason.AccessDenied, fixture);
            }
            else
            {
                AssertIdentity(new SettingsTextFileIdentity(ContentDigest.Compute(changed), changed.LongLength), file.InspectIdentity());
            }

            CollectionAssert.AreEqual(changed, File.ReadAllBytes(fixture.FilePath));
            CollectionAssert.AreEqual(acl, FileDescriptor(fixture.FilePath));
            CollectionAssert.AreEqual(rootAcl, DirectoryDescriptor(fixture.AppRoot));
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(retained));
            AssertFileAcl(retained, fixture.CurrentSid);
            AssertRootEntries(fixture, "setting.txt", ".setting-retained.tmp");
        }
        finally
        {
            await StopMutexOwnerAsync(owner);
        }
    }

    private static string MutexName(AppFixture fixture)
    {
        using Process process = Process.GetCurrentProcess();
        string key = string.Concat(
            Path.GetFullPath(fixture.AppRoot).ToUpperInvariant(), "\n", fixture.CurrentSid, "\n",
            process.SessionId.ToString(CultureInfo.InvariantCulture));
        return @"Local\HubRadio.SettingsTextFile." + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(key)));
    }

    private static async Task<Process> StartMutexOwnerAsync(AppFixture fixture)
    {
        const string code = """
            $ErrorActionPreference = 'Stop'
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false, $true)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false, $true)
            try {
                if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion.Major -lt 7 -or
                    $null -eq ('System.Threading.NamedWaitHandleOptions' -as [type])) {
                    [Console]::Out.WriteLine('UNSUPPORTED')
                    exit 2
                }
                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                $self = [Diagnostics.Process]::GetCurrentProcess()
                try {
                    $sid = $identity.User.Value
                    $session = $self.SessionId.ToString([Globalization.CultureInfo]::InvariantCulture)
                }
                finally { $identity.Dispose(); $self.Dispose() }
                if ($sid -cne $env:HUBRADIO_SETTINGS_TEST_SID -or $session -cne $env:HUBRADIO_SETTINGS_TEST_SESSION) {
                    throw 'IDENTITY'
                }
                $root = [IO.Path]::GetFullPath([IO.Path]::TrimEndingDirectorySeparator($env:HUBRADIO_SETTINGS_TEST_ROOT))
                $key = [string]::Concat($root.ToUpperInvariant(), "`n", $sid, "`n", $session)
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
                $options = [Threading.NamedWaitHandleOptions]::new()
                $options.CurrentUserOnly = $true
                $options.CurrentSessionOnly = $true
                $mutex = [Threading.Mutex]::new($false, ('Local\HubRadio.SettingsTextFile.' + $hash), $options)
                if (-not $mutex.WaitOne(15000)) { throw 'WAIT' }
                [Console]::Out.WriteLine('READY')
                [Console]::Out.Flush()
                $command = [Console]::ReadLine()
                if ($command -ceq 'ABANDON') { [Environment]::Exit(0) }
                if ($command -cne 'RELEASE') { throw 'COMMAND' }
                $mutex.ReleaseMutex()
                $mutex.Dispose()
                [Console]::Out.WriteLine('RELEASED')
                [Console]::Out.Flush()
                exit 0
            }
            catch { [Console]::Out.WriteLine('FAILED'); exit 3 }
            """;
        var start = new ProcessStartInfo(FindLatestPowerShell())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = fixture.AppRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = StrictUtf8,
            StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = StrictUtf8,
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" })
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(code)));
        start.Environment["HUBRADIO_SETTINGS_TEST_ROOT"] = fixture.AppRoot.ToUpperInvariant().Replace('\\', '/') + "/";
        start.Environment["HUBRADIO_SETTINGS_TEST_SID"] = fixture.CurrentSid;
        using Process current = Process.GetCurrentProcess();
        start.Environment["HUBRADIO_SETTINGS_TEST_SESSION"] = current.SessionId.ToString(CultureInfo.InvariantCulture);
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Assert.IsTrue(started);
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(ProcessTimeout);
            if (ready == "UNSUPPORTED")
            {
                Assert.Inconclusive("Cross-process mutex tests require PowerShell 7+ running on .NET 10 or newer.");
            }

            Assert.AreEqual("READY", ready, "The child did not acquire the settings mutex.");
            return process;
        }
        catch
        {
            if (started)
            {
                await StopMutexOwnerAsync(process);
            }

            process.Dispose();
            throw;
        }
    }

    private static async Task FinishMutexOwnerAsync(Process process, bool abandon)
    {
        await process.StandardInput.WriteLineAsync(abandon ? "ABANDON" : "RELEASE");
        await process.StandardInput.FlushAsync();
        await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
        Assert.AreEqual(0, process.ExitCode, "The mutex child did not exit successfully.");
        if (!abandon)
        {
            Assert.AreEqual("RELEASED", await process.StandardOutput.ReadLineAsync().WaitAsync(ProcessTimeout));
        }
    }

    private static async Task StopMutexOwnerAsync(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        }

        await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
    }

    private static string FindLatestPowerShell()
    {
        var candidates = new List<string>();
        string installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
        if (Directory.Exists(installedRoot))
        {
            candidates.AddRange(Directory.GetDirectories(installedRoot).Select(static path => Path.Combine(path, "pwsh.exe")));
        }

        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static path => path.Trim('"')).Where(Path.IsPathFullyQualified).Select(static path => Path.Combine(path, "pwsh.exe")));
        string? executable = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)
            .Select(static path => (Path: path, Info: FileVersionInfo.GetVersionInfo(path)))
            .Where(static entry => entry.Info.FileMajorPart >= 7)
            .OrderByDescending(static entry => new Version(entry.Info.FileMajorPart, entry.Info.FileMinorPart, entry.Info.FileBuildPart, entry.Info.FilePrivatePart))
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal).Select(static entry => entry.Path).FirstOrDefault();
        if (executable is null)
        {
            Assert.Inconclusive("Install PowerShell 7+; never fall back to Windows PowerShell 5.1.");
        }

        return executable!;
    }

    private static DefaultSettingsProfile CreateProfile() =>
        DefaultSettingsProfile.Create(
            EntityId<DefaultSettingsProfileIdTag>.New(),
            "Settings file focused test",
            new SettingsProfileVersion(1, 0, 0),
            [
                Definition("settings.flag", DefaultSettingValue.FromBoolean(true)),
                Definition("settings.integer", DefaultSettingValue.FromInteger(0)),
                Definition("settings.ratio", DefaultSettingValue.FromRational(1, 1)),
                Definition("settings.text", DefaultSettingValue.FromText("既定値")),
                Definition("settings.list", DefaultSettingValue.FromTextList(["既定値"])),
            ]);

    private static DefaultSettingsProfile CreateTextProfile() =>
        DefaultSettingsProfile.Create(
            EntityId<DefaultSettingsProfileIdTag>.New(),
            "Settings size focused test",
            new SettingsProfileVersion(1, 0, 0),
            [Definition("settings.text", DefaultSettingValue.FromText("x"))]);

    private static DefaultSettingDefinition Definition(string key, DefaultSettingValue value) =>
        new(
            key, DefaultSettingGroup.Editing, isAdvanced: false, key,
            "Used only by isolated settings file tests.", DefaultSettingUnit.None, value,
            minimum: null, maximum: null, allowedTextValues: null,
            evidence: [new DefaultSettingEvidence(DefaultSettingEvidenceKind.ProductDecision, "productDecision")]);

    private static DefaultSettingsSnapshot CreateSnapshot(
        DefaultSettingsProfile profile,
        long integer = 1,
        long numerator = 30000,
        long denominator = 1001) =>
        profile.CreateSnapshot(
        [
            new DefaultSettingSelection("settings.flag", DefaultSettingValue.FromBoolean(false)),
            new DefaultSettingSelection("settings.integer", DefaultSettingValue.FromInteger(integer)),
            new DefaultSettingSelection("settings.ratio", DefaultSettingValue.FromRational(numerator, denominator)),
            new DefaultSettingSelection("settings.text", DefaultSettingValue.FromText("日本語\r\n  \"引用\"\tΩ🙂  終端")),
            new DefaultSettingSelection("settings.list", DefaultSettingValue.FromTextList(["末尾", "先頭", "値\t空白"])),
        ]);

    private static DefaultSettingsSnapshot SnapshotOfCanonicalSize(DefaultSettingsProfile profile, int size)
    {
        int overhead = StrictUtf8.GetByteCount(profile.CreateSnapshot().CanonicalJson) - 1;
        DefaultSettingsSnapshot snapshot = profile.CreateSnapshot(
            [new DefaultSettingSelection("settings.text", DefaultSettingValue.FromText(new string('x', size - overhead)))]);
        Assert.AreEqual(size, StrictUtf8.GetByteCount(snapshot.CanonicalJson));
        return snapshot;
    }

    private static void AssertReadResult(DefaultSettingsSnapshot expected, byte[] raw, SettingsTextFileReadResult actual)
    {
        Assert.AreEqual(expected.CanonicalJson, actual.Snapshot.CanonicalJson);
        Assert.AreEqual(expected.ProfileId, actual.Snapshot.ProfileId);
        Assert.AreEqual(expected.ProfileVersion, actual.Snapshot.ProfileVersion);
        Assert.AreEqual(expected.ProfileDigest, actual.Snapshot.ProfileDigest);
        Assert.AreEqual(expected.ContentDigest, actual.Snapshot.ContentDigest);
        CollectionAssert.AreEqual(expected.Values.Select(static selection => selection.Key).ToArray(),
            actual.Snapshot.Values.Select(static selection => selection.Key).ToArray());
        CollectionAssert.AreEqual(expected.Values.Select(static selection => selection.Value).ToArray(),
            actual.Snapshot.Values.Select(static selection => selection.Value).ToArray());
        Assert.AreEqual(ContentDigest.Compute(raw), actual.Identity.FileSha256);
        Assert.AreEqual(raw.LongLength, actual.Identity.ByteSize);
    }

    private static void AssertRoundTrip(DefaultSettingsSnapshot expected, DefaultSettingsProfile profile, byte[] raw)
    {
        byte[] canonical = CanonicalJson.CanonicalizeToUtf8(raw);
        Assert.IsTrue(canonical.Length <= SettingsTextFile.MaximumCanonicalJsonUtf8Bytes);
        DefaultSettingsSnapshot actual = DefaultSettingsSnapshot.ParseCanonical(StrictUtf8.GetString(canonical), profile);
        Assert.AreEqual(expected.CanonicalJson, actual.CanonicalJson);
        Assert.AreEqual(expected.ProfileId, actual.ProfileId);
        Assert.AreEqual(expected.ProfileVersion, actual.ProfileVersion);
        Assert.AreEqual(expected.ProfileDigest, actual.ProfileDigest);
        Assert.AreEqual(expected.ContentDigest, actual.ContentDigest);
        CollectionAssert.AreEqual(expected.Values.Select(static selection => selection.Key).ToArray(), actual.Values.Select(static selection => selection.Key).ToArray());
        CollectionAssert.AreEqual(expected.Values.Select(static selection => selection.Value).ToArray(), actual.Values.Select(static selection => selection.Value).ToArray());
    }

    private static IEnumerable<string> NumberTokens(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            yield return element.GetRawText();
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                foreach (string token in NumberTokens(property.Value))
                {
                    yield return token;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                foreach (string token in NumberTokens(item))
                {
                    yield return token;
                }
            }
        }
    }

    private static SettingsTextFileException AssertFailure(
        Action action, SettingsTextFileFailureReason reason, AppFixture fixture, bool reconciliationRequired = false)
    {
        SettingsTextFileException exception = Assert.ThrowsExactly<SettingsTextFileException>(action);
        Assert.AreEqual(reason, exception.Reason);
        Assert.AreEqual(reconciliationRequired, exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        Assert.IsEmpty(exception.Data);
        if (reason != SettingsTextFileFailureReason.OverwriteRequired)
        {
            Assert.IsNull(exception.ExistingFileSha256);
        }

        AssertNotDisclosed(exception, fixture.AppRoot);
        AssertNotDisclosed(exception, fixture.FilePath);
        AssertNotDisclosed(exception, fixture.CurrentSid);
        return exception;
    }

    private static void AssertNotDisclosed(Exception exception, string? privateValue)
    {
        if (!string.IsNullOrWhiteSpace(privateValue))
        {
            Assert.IsFalse(exception.Message.Contains(privateValue, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(exception.ToString().Contains(privateValue, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertIdentity(SettingsTextFileIdentity expected, SettingsTextFileIdentity? actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.FileSha256, actual.FileSha256);
        Assert.AreEqual(expected.ByteSize, actual.ByteSize);
    }

    private static void AssertOriginalOrAbsent(AppFixture fixture, byte[]? before)
    {
        if (before is null)
        {
            Assert.IsFalse(File.Exists(fixture.FilePath));
            AssertRootEntries(fixture);
        }
        else
        {
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.FilePath));
            AssertRootEntries(fixture, "setting.txt");
        }
    }

    private static void AssertRootEntries(AppFixture fixture, params string[] expected) =>
        CollectionAssert.AreEquivalent(expected,
            Directory.GetFileSystemEntries(fixture.AppRoot).Select(static path => Path.GetFileName(path)).ToArray());

    private static void AssertImmutable(Type type, params string[] expectedProperties)
    {
        Assert.IsTrue(type.IsPublic && type.IsSealed);
        Assert.AreEqual(0, type.GetConstructors().Length);
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        CollectionAssert.AreEquivalent(expectedProperties, properties.Select(static property => property.Name).ToArray());
        Assert.IsTrue(properties.All(static property => property.SetMethod is null && property.PropertyType != typeof(string)));
    }

    private static void AssertFileAcl(string path, string actorSid)
    {
        FileSecurity security = ReadFileSecurity(path);
        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.AreEqual(actorSid, ((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!).Value);
        FileSystemAccessRule[] rules = Rules(security);
        Assert.AreEqual(3, rules.Length);
        CollectionAssert.AreEquivalent(
            new[] { actorSid, "S-1-5-18", "S-1-5-32-544" },
            rules.Select(static rule => rule.IdentityReference.Value).ToArray());
        Assert.IsTrue(rules.All(static rule =>
            !rule.IsInherited && rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights == FileSystemRights.FullControl &&
            rule.InheritanceFlags == InheritanceFlags.None && rule.PropagationFlags == PropagationFlags.None));
        Assert.AreEqual((FileAttributes)0, File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint));
    }

    private static void WriteProtectedFile(string path, byte[] bytes, string actorSid)
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier(actorSid));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (string sid in new[] { actorSid, "S-1-5-18", "S-1-5-32-544" })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        }

        using FileStream stream = FileSystemAclExtensions.Create(
            new FileInfo(path), FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.None, security);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static FileSecurity ReadFileSecurity(string path) =>
        FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Access | AccessControlSections.Owner);

    private static DirectorySecurity ReadDirectorySecurity(string path) =>
        FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Access | AccessControlSections.Owner);

    private static byte[] FileDescriptor(string path) => ReadFileSecurity(path).GetSecurityDescriptorBinaryForm();

    private static byte[] DirectoryDescriptor(string path) => ReadDirectorySecurity(path).GetSecurityDescriptorBinaryForm();

    private static void AssertRestoredAcl(FileSecurity expected, FileSecurity actual)
    {
        // Compare owner, protection and complete access records, not descriptor-header bookkeeping.
        Assert.AreEqual(expected.GetOwner(typeof(SecurityIdentifier)), actual.GetOwner(typeof(SecurityIdentifier)));
        Assert.AreEqual(expected.AreAccessRulesProtected, actual.AreAccessRulesProtected);
        CollectionAssert.AreEquivalent(
            Rules(expected).Select(static rule => (
                Sid: rule.IdentityReference.Value,
                rule.AccessControlType,
                rule.FileSystemRights,
                rule.IsInherited,
                rule.InheritanceFlags,
                rule.PropagationFlags)).ToArray(),
            Rules(actual).Select(static rule => (
                Sid: rule.IdentityReference.Value,
                rule.AccessControlType,
                rule.FileSystemRights,
                rule.IsInherited,
                rule.InheritanceFlags,
                rule.PropagationFlags)).ToArray());
    }

    private static void AssertRestoredAcl(byte[] expected, byte[] actual)
    {
        var expectedDescriptor = new RawSecurityDescriptor(expected, 0);
        var actualDescriptor = new RawSecurityDescriptor(actual, 0);
        expectedDescriptor.SetFlags(expectedDescriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        actualDescriptor.SetFlags(actualDescriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        byte[] expectedBytes = new byte[expectedDescriptor.BinaryLength];
        byte[] actualBytes = new byte[actualDescriptor.BinaryLength];
        expectedDescriptor.GetBinaryForm(expectedBytes, 0);
        actualDescriptor.GetBinaryForm(actualBytes, 0);
        CollectionAssert.AreEqual(expectedBytes, actualBytes);
    }

    private static FileSystemAccessRule[] Rules(FileSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();

    private static void CreateLinkOrInconclusive(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic link creation is unavailable for this runner.");
        }
    }

    private sealed class AppFixture : IDisposable
    {
        internal AppFixture()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            CurrentSid = identity.User?.Value
                ?? throw new AssertInconclusiveException("The Windows runner has no user SID.");
            RootPath = Path.Combine(Path.GetTempPath(), "HubRadio.SettingsTextFileTests", Guid.NewGuid().ToString("N"));
            AppRoot = Path.Combine(RootPath, "application");
            FilePath = Path.Combine(AppRoot, "setting.txt");
            Directory.CreateDirectory(AppRoot);
        }

        internal string RootPath { get; }
        internal string AppRoot { get; }
        internal string FilePath { get; }
        internal string CurrentSid { get; }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }
}