using System.Reflection;
using System.Text;
using System.Text.Json;
using HubRadio.App.Transcripts;
using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Core.Projects;
using HubRadio.Core.Time;
using HubRadio.Core.Transcripts;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;
using HubRadio.Infrastructure.Integrity;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class TranscriptEditorTests
{
    private const string ActorSid = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset SourceCreatedAt =
        new(2037, 8, 9, 10, 11, 12, TimeSpan.Zero);
    private static readonly DateTimeOffset EditedAt = SourceCreatedAt.AddMinutes(5);

    [TestMethod]
    public void ApplyTokenTextEdits_CreatesHumanChildAndPreservesSource()
    {
        Fixture fixture = new();
        string sourceJsonBefore = TranscriptSnapshotJson.WriteRevision(fixture.Revision);
        var newRevisionId = Id<TranscriptRevisionIdTag>(
            "30000000-0000-0000-0000-000000000001");
        TranscriptTokenTextEdit[] edits =
        [
            new(fixture.WorldTokenId, "世界", "Hub"),
            new(fixture.SecondYesTokenId, "はい", "いいえ"),
        ];

        TranscriptEditResult result = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            edits,
            newRevisionId,
            ActorSid,
            EditedAt,
            "認識結果の誤記を修正");

        Assert.AreSame(fixture.Revision, result.SourceRevision);
        Assert.AreEqual(sourceJsonBefore, TranscriptSnapshotJson.WriteRevision(fixture.Revision));
        Assert.AreEqual(fixture.Revision.Id, result.Revision.ParentRevisionId);
        Assert.AreEqual(newRevisionId, result.Revision.Id);
        Assert.AreEqual(TranscriptChangeSource.Human, result.Revision.ChangeSource);
        Assert.AreEqual(EditedAt, result.Revision.CreatedAt);
        Assert.AreEqual("こんにちは 世界 保留", fixture.Revision.Utterances[0].Text);
        Assert.AreEqual("はい はい", fixture.Revision.Utterances[1].Text);

        Utterance revisedFirst = result.Revision.Utterances[0];
        Assert.AreEqual("こんにちは Hub 保留", revisedFirst.Text);
        Assert.AreEqual("こんにちは Hub 保留", revisedFirst.Sentences[0].Text);
        Assert.AreEqual("Hub", revisedFirst.Tokens[1].Text);
        Assert.AreEqual(fixture.WorldTokenId, revisedFirst.Tokens[1].Id);
        Assert.AreEqual(fixture.Revision.Utterances[0].Tokens[1].Range, revisedFirst.Tokens[1].Range);
        Assert.AreEqual(
            fixture.Revision.Utterances[0].Tokens[1].RecognitionConfidence,
            revisedFirst.Tokens[1].RecognitionConfidence);
        Assert.IsNull(revisedFirst.Tokens[2].Range);
        Assert.IsTrue(revisedFirst.Tokens[2].ManualTimingAdjustmentRequired);

        Utterance revisedSecond = result.Revision.Utterances[1];
        Assert.AreEqual("はい いいえ", revisedSecond.Text);
        Assert.AreEqual("はい", revisedSecond.Tokens[0].Text);
        Assert.AreEqual("いいえ", revisedSecond.Tokens[1].Text);
        Assert.AreEqual(fixture.SecondYesTokenId, revisedSecond.Tokens[1].Id);

        Assert.HasCount(2, result.RevisedSourceSpans);
        for (int index = 0; index < result.RevisedSourceSpans.Count; index++)
        {
            SourceSpan source = fixture.SourceSpans[index];
            SourceSpan revised = result.RevisedSourceSpans[index];
            Utterance utterance = result.Revision.Utterances[index];
            Assert.AreNotEqual(source.Id, revised.Id);
            Assert.AreEqual(utterance.SourceSpanId, revised.Id);
            Assert.AreEqual(source.AssetId, revised.AssetId);
            Assert.AreEqual(source.StreamId, revised.StreamId);
            Assert.AreEqual(source.Range, revised.Range);
            Assert.AreEqual(source.SourceDigest, revised.SourceDigest);
            Assert.AreEqual(result.Revision.Id, revised.TranscriptRevisionId);
            CollectionAssert.AreEqual(
                utterance.Tokens.Select(static token => token.Id).ToArray(),
                revised.TranscriptTokenIds.ToArray());
            Assert.AreEqual(fixture.Revision.Id, source.TranscriptRevisionId);
        }
    }

    [TestMethod]
    public void ApplyTokenTextEdits_RecordsActorReasonTypedDiffAndStaleDigests()
    {
        Fixture fixture = new();
        TranscriptEditResult result = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000002"),
            ActorSid,
            EditedAt,
            "表記を正規化");

        TranscriptEditRecord record = result.EditRecord;
        Assert.AreEqual(ActorSid, record.ActorSid);
        Assert.AreEqual(EditedAt, record.CreatedAt);
        Assert.AreEqual("表記を正規化", record.Reason);
        Assert.AreEqual(fixture.Revision.Id, record.Diff.SourceRevisionId);
        Assert.AreEqual(result.Revision.Id, record.Diff.RevisionId);
        Assert.HasCount(3, record.Diff.Changes);
        AssertChange(
            record.Diff.Changes[0],
            TranscriptTextChangeKind.Utterance,
            fixture.FirstUtteranceId,
            sentenceId: null,
            tokenId: null,
            "こんにちは 世界 保留",
            "こんにちは Hub 保留");
        AssertChange(
            record.Diff.Changes[1],
            TranscriptTextChangeKind.Sentence,
            fixture.FirstUtteranceId,
            fixture.FirstSentenceId,
            tokenId: null,
            "こんにちは 世界 保留",
            "こんにちは Hub 保留");
        AssertChange(
            record.Diff.Changes[2],
            TranscriptTextChangeKind.Token,
            fixture.FirstUtteranceId,
            fixture.FirstSentenceId,
            fixture.WorldTokenId,
            "世界",
            "Hub");

        TranscriptDownstreamStaleRecord stale = result.DownstreamStale;
        Assert.AreEqual(fixture.Revision.Id, stale.SourceRevisionId);
        Assert.AreEqual(result.Revision.Id, stale.RevisionId);
        Assert.AreNotEqual(stale.SourceRevisionDigest, stale.RevisionDigest);
        Assert.AreEqual(result.RevisionDigest, stale.RevisionDigest);
        Assert.IsTrue(stale.PreviousRevisionConsumersAreStale);

        string sourceJson = TranscriptSnapshotJson.WriteRevision(fixture.Revision);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(sourceJson)),
            stale.SourceRevisionDigest);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(result.CanonicalRevisionJson)),
            result.RevisionDigest);
        Assert.AreEqual(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(result.CanonicalEditRecordJson)),
            result.EditRecordDigest);
    }

    [TestMethod]
    public void CanonicalSnapshots_AreClosedAndSeparateRevisionFromEditMetadata()
    {
        Fixture fixture = new();
        TranscriptEditResult result = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000003"),
            ActorSid,
            EditedAt,
            "表記を修正");

        using JsonDocument revisionDocument = JsonDocument.Parse(
            result.CanonicalRevisionJson);
        JsonElement revision = revisionDocument.RootElement;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "id",
                "parentRevisionId",
                "changeSource",
                "createdAt",
                "utterances",
            },
            revision.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("human", revision.GetProperty("changeSource").GetString());
        Assert.AreEqual(
            fixture.Revision.Id.ToString(),
            revision.GetProperty("parentRevisionId").GetString());
        Assert.IsFalse(result.CanonicalRevisionJson.Contains(
            "actorSid",
            StringComparison.Ordinal));
        Assert.IsFalse(result.CanonicalRevisionJson.Contains(
            "reason",
            StringComparison.Ordinal));
        Assert.IsFalse(result.CanonicalRevisionJson.Contains(
            "stale",
            StringComparison.OrdinalIgnoreCase));
        JsonElement untimedToken = revision
            .GetProperty("utterances")[0]
            .GetProperty("sentences")[0]
            .GetProperty("tokens")[2];
        Assert.AreEqual(JsonValueKind.Null, untimedToken.GetProperty("range").ValueKind);
        Assert.IsTrue(untimedToken
            .GetProperty("manualTimingAdjustmentRequired")
            .GetBoolean());

        using JsonDocument recordDocument = JsonDocument.Parse(
            result.CanonicalEditRecordJson);
        JsonElement record = recordDocument.RootElement;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "sourceRevisionId",
                "revisionId",
                "actorSid",
                "createdAt",
                "reason",
                "sourceRevisionDigest",
                "revisionDigest",
                "previousRevisionConsumersAreStale",
                "sourceSpans",
                "changes",
            },
            record.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("1.0.0", record.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(ActorSid, record.GetProperty("actorSid").GetString());
        Assert.AreEqual("表記を修正", record.GetProperty("reason").GetString());
        Assert.IsTrue(record
            .GetProperty("previousRevisionConsumersAreStale")
            .GetBoolean());
        Assert.AreEqual(2, record.GetProperty("sourceSpans").GetArrayLength());
        Assert.AreEqual(
            result.Revision.Id.ToString(),
            record
                .GetProperty("sourceSpans")[0]
                .GetProperty("transcriptRevisionId")
                .GetString());
        Assert.AreEqual(
            result.RevisedSourceSpans[0].SourceDigest.Hex,
            record.GetProperty("sourceSpans")[0].GetProperty("sourceDigest").GetString());
        Assert.AreEqual(3, record.GetProperty("changes").GetArrayLength());
        Assert.AreEqual(
            "token",
            record.GetProperty("changes")[2].GetProperty("kind").GetString());
        Assert.AreEqual(
            "世界",
            record.GetProperty("changes")[2].GetProperty("previousText").GetString());
        Assert.AreEqual(
            "Hub",
            record.GetProperty("changes")[2].GetProperty("currentText").GetString());
    }

    [TestMethod]
    public void ApplyTokenTextEdits_MapsDuplicateTokenTextByStableOrder()
    {
        Fixture fixture = new();

        TranscriptEditResult first = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.FirstYesTokenId, "はい", "うん")],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000004"),
            ActorSid,
            EditedAt,
            "先頭だけ修正");
        Assert.AreEqual("うん はい", first.Revision.Utterances[1].Text);
        Assert.AreEqual("うん", first.Revision.Utterances[1].Tokens[0].Text);
        Assert.AreEqual("はい", first.Revision.Utterances[1].Tokens[1].Text);

        TranscriptEditResult both = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [
                new TranscriptTokenTextEdit(fixture.FirstYesTokenId, "はい", "うん"),
                new TranscriptTokenTextEdit(fixture.SecondYesTokenId, "はい", "いいえ"),
            ],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000005"),
            ActorSid,
            EditedAt,
            "両方を修正");
        Assert.AreEqual("うん いいえ", both.Revision.Utterances[1].Text);
        Assert.AreEqual("うん いいえ", both.Revision.Utterances[1].Sentences[0].Text);
    }

    [TestMethod]
    public void ApplyTokenTextEdits_RejectsNoChangesDuplicatesUnknownAndStaleText()
    {
        Fixture fixture = new();

        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                fixture.Revision,
                fixture.SourceSpans,
                Array.Empty<TranscriptTokenTextEdit>(),
                EntityId<TranscriptRevisionIdTag>.New(),
                ActorSid,
                EditedAt,
                "修正"),
            TranscriptEditorFailureReason.NoChanges);

        var edit = new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub");
        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                fixture.Revision,
                fixture.SourceSpans,
                [edit, edit],
                EntityId<TranscriptRevisionIdTag>.New(),
                ActorSid,
                EditedAt,
                "修正"),
            TranscriptEditorFailureReason.DuplicateEdit);

        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                fixture.Revision,
                fixture.SourceSpans,
                [new TranscriptTokenTextEdit(
                    EntityId<TranscriptTokenIdTag>.New(),
                    "世界",
                    "Hub")],
                EntityId<TranscriptRevisionIdTag>.New(),
                ActorSid,
                EditedAt,
                "修正"),
            TranscriptEditorFailureReason.TokenNotFound);

        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                fixture.Revision,
                fixture.SourceSpans,
                [new TranscriptTokenTextEdit(
                    fixture.WorldTokenId,
                    "別の古い表示",
                    "Hub")],
                EntityId<TranscriptRevisionIdTag>.New(),
                ActorSid,
                EditedAt,
                "修正"),
            TranscriptEditorFailureReason.SourceTextChanged);
    }

    [TestMethod]
    public void ApplyTokenTextEdits_RejectsMismatchedSourceSpanEvidence()
    {
        Fixture fixture = new();
        SourceSpan first = fixture.SourceSpans[0];
        SourceSpan wrongRevision = new(
            first.Id,
            first.AssetId,
            first.StreamId,
            first.Range,
            first.SourceDigest,
            EntityId<TranscriptRevisionIdTag>.New(),
            first.TranscriptTokenIds);

        foreach (IReadOnlyList<SourceSpan> invalid in new IReadOnlyList<SourceSpan>[]
        {
            Array.Empty<SourceSpan>(),
            [fixture.SourceSpans[1], fixture.SourceSpans[0]],
            [wrongRevision, fixture.SourceSpans[1]],
        })
        {
            AssertFailure(
                () => TranscriptEditor.ApplyTokenTextEdits(
                    fixture.Revision,
                    invalid,
                    [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
                    EntityId<TranscriptRevisionIdTag>.New(),
                    ActorSid,
                    EditedAt,
                    "修正"),
                TranscriptEditorFailureReason.SourceSpanBindingMismatch);
        }
    }

    [TestMethod]
    public void ApplyTokenTextEdits_RejectsAmbiguousTextHierarchyWithoutGuessing()
    {
        var revisionId = Id<TranscriptRevisionIdTag>(
            "10000000-0000-0000-0000-000000000010");
        var tokenId = Id<TranscriptTokenIdTag>(
            "23000000-0000-0000-0000-000000000010");
        var spanId = Id<SourceSpanIdTag>(
            "20000000-0000-0000-0000-000000000010");
        var token = new Token(tokenId, "token", null, 0.5);
        var sentence = new Sentence(
            Id<TranscriptSentenceIdTag>("22000000-0000-0000-0000-000000000010"),
            "display text",
            [token]);
        var utterance = new Utterance(
            Id<TranscriptUtteranceIdTag>("21000000-0000-0000-0000-000000000010"),
            spanId,
            Id<TranscriptSpeakerIdTag>("24000000-0000-0000-0000-000000000010"),
            "Speaker 1",
            "display text",
            new TimeRange(default, new RationalTime(1, 1)),
            null,
            [sentence]);
        var revision = new TranscriptRevision(
            revisionId,
            null,
            TranscriptChangeSource.Machine,
            SourceCreatedAt,
            [utterance]);
        var span = new SourceSpan(
            spanId,
            Id<AssetIdTag>("25000000-0000-0000-0000-000000000010"),
            "audio:1",
            utterance.Range,
            new ContentDigest(new string('a', 64)),
            revisionId,
            [tokenId]);

        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                revision,
                [span],
                [new TranscriptTokenTextEdit(tokenId, "token", "revised")],
                EntityId<TranscriptRevisionIdTag>.New(),
                ActorSid,
                EditedAt,
                "修正"),
            TranscriptEditorFailureReason.TextHierarchyMismatch);
        Assert.AreEqual("display text", revision.Utterances[0].Text);
        Assert.AreEqual("token", revision.Utterances[0].Tokens[0].Text);

        var repeatedRevisionId = Id<TranscriptRevisionIdTag>(
            "10000000-0000-0000-0000-000000000011");
        var repeatedTokenId = Id<TranscriptTokenIdTag>(
            "23000000-0000-0000-0000-000000000011");
        var repeatedSpanId = Id<SourceSpanIdTag>(
            "20000000-0000-0000-0000-000000000011");
        var repeatedToken = new Token(repeatedTokenId, "foo", null, 0.5);
        var repeatedSentence = new Sentence(
            Id<TranscriptSentenceIdTag>("22000000-0000-0000-0000-000000000011"),
            "foo foo",
            [repeatedToken]);
        var repeatedUtterance = new Utterance(
            Id<TranscriptUtteranceIdTag>("21000000-0000-0000-0000-000000000011"),
            repeatedSpanId,
            Id<TranscriptSpeakerIdTag>("24000000-0000-0000-0000-000000000011"),
            "Speaker 1",
            "foo foo",
            new TimeRange(default, new RationalTime(1, 1)),
            null,
            [repeatedSentence]);
        var repeatedRevision = new TranscriptRevision(
            repeatedRevisionId,
            null,
            TranscriptChangeSource.Machine,
            SourceCreatedAt,
            [repeatedUtterance]);
        var repeatedSpan = new SourceSpan(
            repeatedSpanId,
            Id<AssetIdTag>("25000000-0000-0000-0000-000000000011"),
            "audio:1",
            repeatedUtterance.Range,
            new ContentDigest(new string('b', 64)),
            repeatedRevisionId,
            [repeatedTokenId]);

        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                repeatedRevision,
                [repeatedSpan],
                [new TranscriptTokenTextEdit(repeatedTokenId, "foo", "bar")],
                EntityId<TranscriptRevisionIdTag>.New(),
                ActorSid,
                EditedAt,
                "曖昧な位置を拒否"),
            TranscriptEditorFailureReason.TextHierarchyMismatch);
        Assert.AreEqual("foo foo", repeatedRevision.Utterances[0].Text);
        Assert.AreEqual("foo", repeatedRevision.Utterances[0].Tokens[0].Text);
    }

    [TestMethod]
    public void ApplyTokenTextEdits_RejectsIdentityReasonAndTimestampFailuresPrivately()
    {
        Fixture fixture = new();
        var edit = new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub");

        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                fixture.Revision,
                fixture.SourceSpans,
                [edit],
                fixture.Revision.Id,
                ActorSid,
                EditedAt,
                "修正"),
            TranscriptEditorFailureReason.RevisionIdentityConflict);
        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                fixture.Revision,
                fixture.SourceSpans,
                [edit],
                EntityId<TranscriptRevisionIdTag>.New(),
                ActorSid,
                SourceCreatedAt.AddTicks(-1),
                "修正"),
            TranscriptEditorFailureReason.TimestampPrecedesSource);

        TranscriptEditResult child = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [edit],
            EntityId<TranscriptRevisionIdTag>.New(),
            ActorSid,
            EditedAt,
            "子revisionを作成");
        AssertFailure(
            () => TranscriptEditor.ApplyTokenTextEdits(
                child.Revision,
                child.RevisedSourceSpans,
                [new TranscriptTokenTextEdit(fixture.WorldTokenId, "Hub", "World")],
                fixture.Revision.Id,
                ActorSid,
                EditedAt.AddMinutes(1),
                "祖先IDの再利用を拒否"),
            TranscriptEditorFailureReason.RevisionIdentityConflict);

        foreach (string invalidActor in new[]
        {
            "not-a-sid",
            " S-1-5-18",
            @"C:\Users\private\Bearer-secret",
        })
        {
            TranscriptEditorException exception = AssertFailure(
                () => TranscriptEditor.ApplyTokenTextEdits(
                    fixture.Revision,
                    fixture.SourceSpans,
                    [edit],
                    EntityId<TranscriptRevisionIdTag>.New(),
                    invalidActor,
                    EditedAt,
                    "修正"),
                TranscriptEditorFailureReason.InvalidActor);
            AssertPrivate(exception, invalidActor);
        }

        foreach (string invalidReason in new[]
        {
            string.Empty,
            " surrounding whitespace ",
        })
        {
            TranscriptEditorException exception = AssertFailure(
                () => TranscriptEditor.ApplyTokenTextEdits(
                    fixture.Revision,
                    fixture.SourceSpans,
                    [edit],
                    EntityId<TranscriptRevisionIdTag>.New(),
                    ActorSid,
                    EditedAt,
                    invalidReason),
                TranscriptEditorFailureReason.InvalidReason);
            AssertPrivate(exception, invalidReason);
        }
    }

    [TestMethod]
    public async Task SaveAsync_PromotesCanonicalRevisionAndEditRecordArtifacts()
    {
        Fixture fixture = new();
        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000020"),
            ActorSid,
            EditedAt,
            "保存経路を検証");
        using var storage = new TranscriptStorageFixture(edit);

        StoredTranscriptEdit stored = await TranscriptEditor.SaveAsync(
            storage.Store,
            new BinaryVerifier(),
            edit,
            storage.Request);

        Assert.AreSame(edit, stored.Edit);
        Assert.AreEqual(
            storage.SourceRevisionArtifactId,
            stored.SourceRevisionArtifact.Id);
        Assert.AreEqual(
            edit.DownstreamStale.SourceRevisionDigest,
            stored.SourceRevisionArtifact.ContentDigest);
        Assert.AreEqual(storage.RevisionArtifactId, stored.RevisionArtifact.Id);
        Assert.AreEqual(storage.EditRecordArtifactId, stored.EditRecordArtifact.Id);
        Assert.AreEqual(ArtifactKind.Analysis, stored.RevisionArtifact.Kind);
        Assert.AreEqual(ArtifactKind.Evidence, stored.EditRecordArtifact.Kind);
        Assert.AreEqual(edit.RevisionDigest, stored.RevisionArtifact.ContentDigest);
        Assert.AreEqual(edit.EditRecordDigest, stored.EditRecordArtifact.ContentDigest);
        Assert.AreEqual(
            Encoding.UTF8.GetByteCount(edit.CanonicalRevisionJson),
            stored.RevisionArtifact.ByteSize);
        Assert.AreEqual(
            Encoding.UTF8.GetByteCount(edit.CanonicalEditRecordJson),
            stored.EditRecordArtifact.ByteSize);
        Assert.IsFalse(File.Exists(storage.RevisionStagingPath));
        Assert.IsFalse(File.Exists(storage.EditRecordStagingPath));
        Assert.AreEqual(
            edit.CanonicalRevisionJson,
            File.ReadAllText(storage.GetArtifactPath(stored.RevisionArtifact), Encoding.UTF8));
        Assert.AreEqual(
            edit.CanonicalEditRecordJson,
            File.ReadAllText(storage.GetArtifactPath(stored.EditRecordArtifact), Encoding.UTF8));
        Assert.HasCount(3, storage.Store.ListProjectArtifacts(storage.ProjectId));
    }

    [TestMethod]
    public async Task SaveAsync_RevisionMismatchAfterEvidencePromotionRequiresReconciliation()
    {
        Fixture fixture = new();
        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000021"),
            ActorSid,
            EditedAt,
            "不一致を検証");
        using var storage = new TranscriptStorageFixture(edit);
        File.WriteAllText(
            storage.RevisionStagingPath,
            "{}",
            new UTF8Encoding(false, true));

        TranscriptEditorException exception =
            await Assert.ThrowsExactlyAsync<TranscriptEditorException>(() =>
                TranscriptEditor.SaveAsync(
                    storage.Store,
                    new BinaryVerifier(),
                    edit,
                    storage.Request));

        Assert.AreEqual(TranscriptEditorFailureReason.PersistenceFailed, exception.Reason);
        Assert.IsTrue(exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        Assert.IsNotNull(storage.Store.GetArtifact(storage.EditRecordArtifactId));
        Assert.IsNull(storage.Store.GetArtifact(storage.RevisionArtifactId));
        Assert.IsTrue(File.Exists(storage.RevisionStagingPath));
        Assert.IsFalse(File.Exists(storage.EditRecordStagingPath));
        AssertPrivate(exception, storage.RevisionStagingPath);

        TranscriptEditorException retryException =
            await Assert.ThrowsExactlyAsync<TranscriptEditorException>(() =>
                TranscriptEditor.SaveAsync(
                    storage.Store,
                    new BinaryVerifier(),
                    edit,
                    storage.Request));
        Assert.AreEqual(
            TranscriptEditorFailureReason.PersistenceFailed,
            retryException.Reason);
        Assert.IsTrue(retryException.ReconciliationRequired);
        Assert.IsNull(retryException.InnerException);
        Assert.HasCount(2, storage.Store.ListProjectArtifacts(storage.ProjectId));
    }

    [TestMethod]
    public async Task SaveAsync_DuplicateLogicalRevisionIdIsRejected()
    {
        Fixture fixture = new();
        var revisionId = Id<TranscriptRevisionIdTag>(
            "30000000-0000-0000-0000-000000000024");
        TranscriptEditResult first = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
            revisionId,
            ActorSid,
            EditedAt,
            "最初の保存");
        TranscriptEditResult conflicting = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "World")],
            revisionId,
            ActorSid,
            EditedAt,
            "同じrevision IDの競合");
        using var storage = new TranscriptStorageFixture(first);
        _ = await TranscriptEditor.SaveAsync(
            storage.Store,
            new BinaryVerifier(),
            first,
            storage.Request);
        TranscriptEditPersistenceRequest conflictingRequest =
            storage.CreateAdditionalRequest(
                conflicting,
                Id<ArtifactIdTag>("41000000-0000-0000-0000-000000000003"),
                Id<ArtifactIdTag>("41000000-0000-0000-0000-000000000004"));

        TranscriptEditorException exception =
            await Assert.ThrowsExactlyAsync<TranscriptEditorException>(() =>
                TranscriptEditor.SaveAsync(
                    storage.Store,
                    new BinaryVerifier(),
                    conflicting,
                    conflictingRequest));

        Assert.AreEqual(TranscriptEditorFailureReason.PersistenceFailed, exception.Reason);
        Assert.IsTrue(exception.ReconciliationRequired);
        Assert.IsNull(exception.InnerException);
        Assert.HasCount(3, storage.Store.ListProjectArtifacts(storage.ProjectId));
    }

    [TestMethod]
    public async Task SaveAsync_PreCanceledRequestDoesNotPromoteEitherArtifact()
    {
        Fixture fixture = new();
        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000022"),
            ActorSid,
            EditedAt,
            "取消を検証");
        using var storage = new TranscriptStorageFixture(edit);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            TranscriptEditor.SaveAsync(
                storage.Store,
                new BinaryVerifier(),
                edit,
                storage.Request,
                cancellation.Token));

        Assert.IsNull(storage.Store.GetArtifact(storage.EditRecordArtifactId));
        Assert.IsNull(storage.Store.GetArtifact(storage.RevisionArtifactId));
        Assert.IsTrue(File.Exists(storage.RevisionStagingPath));
        Assert.IsTrue(File.Exists(storage.EditRecordStagingPath));
    }

    [TestMethod]
    public async Task SaveAsync_TamperedSourceRevisionFailsBeforePromotion()
    {
        Fixture fixture = new();
        TranscriptEditResult edit = TranscriptEditor.ApplyTokenTextEdits(
            fixture.Revision,
            fixture.SourceSpans,
            [new TranscriptTokenTextEdit(fixture.WorldTokenId, "世界", "Hub")],
            Id<TranscriptRevisionIdTag>("30000000-0000-0000-0000-000000000023"),
            ActorSid,
            EditedAt,
            "親改ざんを検証");
        using var storage = new TranscriptStorageFixture(edit);
        StoredArtifact source = storage.Store.GetArtifact(storage.SourceRevisionArtifactId)
            ?? throw new InvalidOperationException("Source revision artifact is missing.");
        File.WriteAllText(
            storage.GetArtifactPath(source),
            "{}",
            new UTF8Encoding(false, true));

        TranscriptEditorException exception =
            await Assert.ThrowsExactlyAsync<TranscriptEditorException>(() =>
                TranscriptEditor.SaveAsync(
                    storage.Store,
                    new BinaryVerifier(),
                    edit,
                    storage.Request));

        Assert.AreEqual(TranscriptEditorFailureReason.PersistenceFailed, exception.Reason);
        Assert.IsFalse(exception.ReconciliationRequired);
        Assert.IsNull(storage.Store.GetArtifact(storage.EditRecordArtifactId));
        Assert.IsNull(storage.Store.GetArtifact(storage.RevisionArtifactId));
        Assert.IsTrue(File.Exists(storage.RevisionStagingPath));
        Assert.IsTrue(File.Exists(storage.EditRecordStagingPath));
        AssertPrivate(exception, storage.GetArtifactPath(source));
    }

    [TestMethod]
    public void TokenTextEdit_RejectsNoOpBlankAndInvalidUnicode()
    {
        EntityId<TranscriptTokenIdTag> tokenId = EntityId<TranscriptTokenIdTag>.New();
        Assert.ThrowsExactly<ArgumentException>(() =>
            new TranscriptTokenTextEdit(tokenId, "same", "same"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new TranscriptTokenTextEdit(tokenId, "source", " "));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new TranscriptTokenTextEdit(tokenId, "source", " revised "));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new TranscriptTokenTextEdit(tokenId, "source", "\ud800"));
    }

    [TestMethod]
    public void PublicSurface_IsImmutableAndFailuresAreStable()
    {
        Type[] immutableTypes =
        [
            typeof(TranscriptTokenTextEdit),
            typeof(TranscriptTextChange),
            typeof(TranscriptDiff),
            typeof(TranscriptDownstreamStaleRecord),
            typeof(TranscriptEditRecord),
            typeof(TranscriptEditResult),
            typeof(TranscriptEditPersistenceRequest),
            typeof(StoredTranscriptEdit),
        ];
        foreach (Type type in immutableTypes)
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsTrue(type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .All(static property => property.SetMethod is null), type.Name);
        }

        Assert.AreEqual(0, typeof(TranscriptTextChange).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TranscriptDiff).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TranscriptDownstreamStaleRecord).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TranscriptEditRecord).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TranscriptEditResult).GetConstructors().Length);
        Assert.AreEqual(0, typeof(StoredTranscriptEdit).GetConstructors().Length);
        Assert.AreEqual(0, typeof(TranscriptEditorException).GetConstructors().Length);
        Assert.IsTrue(typeof(TranscriptEditor).IsAbstract);
        Assert.IsTrue(typeof(TranscriptEditor).IsSealed);

        ConstructorInfo constructor = typeof(TranscriptEditorException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        foreach (TranscriptEditorFailureReason reason in
            Enum.GetValues<TranscriptEditorFailureReason>())
        {
            var exception = (TranscriptEditorException)constructor.Invoke([reason, false]);
            Assert.AreEqual(reason, exception.Reason);
            Assert.IsFalse(exception.ReconciliationRequired);
            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            Assert.IsNull(exception.InnerException);
            AssertPrivate(exception, @"C:\Users\private\Bearer-secret");
        }
    }

    private static void AssertChange(
        TranscriptTextChange change,
        TranscriptTextChangeKind kind,
        EntityId<TranscriptUtteranceIdTag> utteranceId,
        EntityId<TranscriptSentenceIdTag>? sentenceId,
        EntityId<TranscriptTokenIdTag>? tokenId,
        string previousText,
        string currentText)
    {
        Assert.AreEqual(kind, change.Kind);
        Assert.AreEqual(utteranceId, change.UtteranceId);
        Assert.AreEqual(sentenceId, change.SentenceId);
        Assert.AreEqual(tokenId, change.TokenId);
        Assert.AreEqual(previousText, change.PreviousText);
        Assert.AreEqual(currentText, change.CurrentText);
    }

    private static TranscriptEditorException AssertFailure(
        Action action,
        TranscriptEditorFailureReason expectedReason)
    {
        TranscriptEditorException exception =
            Assert.ThrowsExactly<TranscriptEditorException>(action);
        Assert.AreEqual(expectedReason, exception.Reason);
        Assert.IsNull(exception.InnerException);
        return exception;
    }

    private static void AssertPrivate(Exception exception, string secret)
    {
        if (secret.Length != 0)
        {
            Assert.IsFalse(exception.ToString().Contains(secret, StringComparison.Ordinal));
        }

        Assert.IsFalse(exception.ToString().Contains(
            "Bearer-secret",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(
            @"C:\Users\private",
            StringComparison.OrdinalIgnoreCase));
    }

    private static EntityId<TTag> Id<TTag>(string value) => EntityId<TTag>.Parse(value);

    private sealed class Fixture
    {
        internal Fixture()
        {
            var helloTokenId = Id<TranscriptTokenIdTag>(
                "23000000-0000-0000-0000-000000000001");
            WorldTokenId = Id<TranscriptTokenIdTag>(
                "23000000-0000-0000-0000-000000000002");
            var untimedTokenId = Id<TranscriptTokenIdTag>(
                "23000000-0000-0000-0000-000000000003");
            FirstYesTokenId = Id<TranscriptTokenIdTag>(
                "23000000-0000-0000-0000-000000000004");
            SecondYesTokenId = Id<TranscriptTokenIdTag>(
                "23000000-0000-0000-0000-000000000005");
            FirstUtteranceId = Id<TranscriptUtteranceIdTag>(
                "21000000-0000-0000-0000-000000000001");
            var secondUtteranceId = Id<TranscriptUtteranceIdTag>(
                "21000000-0000-0000-0000-000000000002");
            FirstSentenceId = Id<TranscriptSentenceIdTag>(
                "22000000-0000-0000-0000-000000000001");
            var secondSentenceId = Id<TranscriptSentenceIdTag>(
                "22000000-0000-0000-0000-000000000002");
            var firstSpanId = Id<SourceSpanIdTag>(
                "20000000-0000-0000-0000-000000000001");
            var secondSpanId = Id<SourceSpanIdTag>(
                "20000000-0000-0000-0000-000000000002");
            var speakerId = Id<TranscriptSpeakerIdTag>(
                "24000000-0000-0000-0000-000000000001");
            var assetId = Id<AssetIdTag>(
                "25000000-0000-0000-0000-000000000001");
            var digest = new ContentDigest(new string('a', 64));

            var firstRange = new TimeRange(default, new RationalTime(3, 1));
            var secondRange = new TimeRange(
                new RationalTime(3, 1),
                new RationalTime(2, 1));
            var hello = new Token(
                helloTokenId,
                "こんにちは",
                new TimeRange(default, new RationalTime(1, 1)),
                0.99);
            var world = new Token(
                WorldTokenId,
                "世界",
                new TimeRange(
                    new RationalTime(1, 1),
                    new RationalTime(1, 1)),
                0.8);
            var untimed = new Token(untimedTokenId, "保留", null, 0.5);
            var firstSentence = new Sentence(
                FirstSentenceId,
                "こんにちは 世界 保留",
                [hello, world, untimed]);
            var firstUtterance = new Utterance(
                FirstUtteranceId,
                firstSpanId,
                speakerId,
                "Speaker 1",
                "こんにちは 世界 保留",
                firstRange,
                0.9,
                [firstSentence]);

            var firstYes = new Token(
                FirstYesTokenId,
                "はい",
                new TimeRange(
                    new RationalTime(3, 1),
                    new RationalTime(1, 1)),
                1);
            var secondYes = new Token(
                SecondYesTokenId,
                "はい",
                new TimeRange(
                    new RationalTime(4, 1),
                    new RationalTime(1, 1)),
                1);
            var secondSentence = new Sentence(
                secondSentenceId,
                "はい はい",
                [firstYes, secondYes]);
            var secondUtterance = new Utterance(
                secondUtteranceId,
                secondSpanId,
                speakerId,
                "Speaker 1",
                "はい はい",
                secondRange,
                1,
                [secondSentence]);

            Revision = new TranscriptRevision(
                Id<TranscriptRevisionIdTag>(
                    "10000000-0000-0000-0000-000000000001"),
                parentRevisionId: null,
                TranscriptChangeSource.Machine,
                SourceCreatedAt,
                [firstUtterance, secondUtterance]);
            SourceSpans =
            [
                new SourceSpan(
                    firstSpanId,
                    assetId,
                    "audio:1",
                    firstRange,
                    digest,
                    Revision.Id,
                    [helloTokenId, WorldTokenId, untimedTokenId]),
                new SourceSpan(
                    secondSpanId,
                    assetId,
                    "audio:1",
                    secondRange,
                    digest,
                    Revision.Id,
                    [FirstYesTokenId, SecondYesTokenId]),
            ];
        }

        internal TranscriptRevision Revision { get; }

        internal IReadOnlyList<SourceSpan> SourceSpans { get; }

        internal EntityId<TranscriptUtteranceIdTag> FirstUtteranceId { get; }

        internal EntityId<TranscriptSentenceIdTag> FirstSentenceId { get; }

        internal EntityId<TranscriptTokenIdTag> WorldTokenId { get; }

        internal EntityId<TranscriptTokenIdTag> FirstYesTokenId { get; }

        internal EntityId<TranscriptTokenIdTag> SecondYesTokenId { get; }
    }

    private sealed class TranscriptStorageFixture : IDisposable
    {
        internal TranscriptStorageFixture(TranscriptEditResult edit)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "HubRadio.TranscriptEditorTests",
                Guid.NewGuid().ToString("N"));
            WorkspacePath = Path.Combine(RootPath, "workspace");
            Directory.CreateDirectory(WorkspacePath);
            string databasePath = Path.Combine(WorkspacePath, "hubradio.db");
            _ = DatabaseMigrator.Migrate(
                databasePath,
                Path.Combine(RootPath, "backups"));
            ProjectId = Id<ProjectIdTag>(
                "40000000-0000-0000-0000-000000000001");
            var project = new Project(
                ProjectId,
                "A-004 storage test",
                "Verify transcript persistence.",
                "Test runner",
                PublicationScope.InternalOnly,
                deadline: null,
                assets: [],
                performers: []);
            new ProjectStore(databasePath).CreateProject(
                project,
                "{\"project\":true}",
                SourceCreatedAt);
            Store = new ArtifactStore(databasePath, WorkspacePath);
            SourceRevisionArtifactId = Id<ArtifactIdTag>(
                "41000000-0000-0000-0000-000000000000");
            RevisionArtifactId = Id<ArtifactIdTag>(
                "41000000-0000-0000-0000-000000000001");
            EditRecordArtifactId = Id<ArtifactIdTag>(
                "41000000-0000-0000-0000-000000000002");
            string stagingDirectory = Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "transcript-edit",
                edit.Revision.Id.ToString());
            Directory.CreateDirectory(stagingDirectory);
            string sourceRevisionStagingPath = Path.Combine(
                stagingDirectory,
                "source-revision.partial.json");
            RevisionStagingPath = Path.Combine(
                stagingDirectory,
                "revision.partial.json");
            EditRecordStagingPath = Path.Combine(
                stagingDirectory,
                "edit-record.partial.json");
            string sourceRevisionJson = TranscriptSnapshotJson.WriteRevision(
                edit.SourceRevision);
            File.WriteAllText(
                sourceRevisionStagingPath,
                sourceRevisionJson,
                new UTF8Encoding(false, true));
            File.WriteAllText(
                RevisionStagingPath,
                edit.CanonicalRevisionJson,
                new UTF8Encoding(false, true));
            File.WriteAllText(
                EditRecordStagingPath,
                edit.CanonicalEditRecordJson,
                new UTF8Encoding(false, true));
            _ = Store.PromoteAsync(
                    new ArtifactPromotionRequest(
                        SourceRevisionArtifactId,
                        ProjectId,
                        ArtifactKind.Analysis,
                        sourceRevisionStagingPath,
                        string.Concat(
                            edit.SourceRevision.Id.ToString(),
                            "/",
                            SourceRevisionArtifactId.ToString(),
                            ".transcript.json"),
                        edit.DownstreamStale.SourceRevisionDigest,
                        Encoding.UTF8.GetByteCount(sourceRevisionJson),
                        edit.SourceRevision.CreatedAt))
                .GetAwaiter()
                .GetResult();
            Request = new TranscriptEditPersistenceRequest(
                SourceRevisionArtifactId,
                RevisionArtifactId,
                RevisionStagingPath,
                EditRecordArtifactId,
                EditRecordStagingPath);
        }

        private string RootPath { get; }

        private string WorkspacePath { get; }

        internal ArtifactStore Store { get; }

        internal EntityId<ProjectIdTag> ProjectId { get; }

        internal EntityId<ArtifactIdTag> SourceRevisionArtifactId { get; }

        internal EntityId<ArtifactIdTag> RevisionArtifactId { get; }

        internal EntityId<ArtifactIdTag> EditRecordArtifactId { get; }

        internal string RevisionStagingPath { get; }

        internal string EditRecordStagingPath { get; }

        internal TranscriptEditPersistenceRequest Request { get; }

        internal TranscriptEditPersistenceRequest CreateAdditionalRequest(
            TranscriptEditResult edit,
            EntityId<ArtifactIdTag> revisionArtifactId,
            EntityId<ArtifactIdTag> editRecordArtifactId)
        {
            string stagingDirectory = Path.Combine(
                WorkspacePath,
                "projects",
                ProjectId.ToString(),
                "staging",
                "transcript-edit",
                editRecordArtifactId.ToString());
            Directory.CreateDirectory(stagingDirectory);
            string revisionStagingPath = Path.Combine(
                stagingDirectory,
                "revision.partial.json");
            string editRecordStagingPath = Path.Combine(
                stagingDirectory,
                "edit-record.partial.json");
            File.WriteAllText(
                revisionStagingPath,
                edit.CanonicalRevisionJson,
                new UTF8Encoding(false, true));
            File.WriteAllText(
                editRecordStagingPath,
                edit.CanonicalEditRecordJson,
                new UTF8Encoding(false, true));
            return new TranscriptEditPersistenceRequest(
                SourceRevisionArtifactId,
                revisionArtifactId,
                revisionStagingPath,
                editRecordArtifactId,
                editRecordStagingPath);
        }

        internal string GetArtifactPath(StoredArtifact artifact) =>
            Path.Combine(
                WorkspacePath,
                artifact.StorageReference.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Yield();
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            Directory.Delete(RootPath, recursive: true);
        }
    }
}