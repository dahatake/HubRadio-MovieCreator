# Q-007 Recovery Acceptance Results

- タスク: Q-007
- 状態: **合格**
- 実測日時: 2026-08-31T14:40:19.8284066+00:00
- 要求: NFR-003
- 実行基点commit: `5f8f8d8d577c4a2e96a213395af118110759c4f4`
- acceptance result SHA-256: `15fa82e68929c8ad321930610893bdd15a3b339ede96dbcd2f1d22fa454521e1`
- acceptance result UTF-8 byte length: 4,097 byte

## 1. 判定

Q-007の必須scenario group 5/5と、そのaggregate/nested pass gate 9/9が成功した。外部process kill、意図的host crash、実容量枯渇、未完了staging、成果物破損、SQLite破損、検証付きbackup restoreを実行した。job、media、workspace、backupの各scenarioはproduction assemblyを参照し、process containmentはWin32 Job Objectを直接操作する専用probeで確認した。

- durableなOriginal-kind synthetic artifact損失: 0 byte
- durableな承認済み成果物損失: 0 byte
- approval record損失: 0件
- audit event損失: 0件
- durable progress record損失: 0件
- 観測対象の孤立process: 0件
- 実行後のQ-007 work directory: 不在
- 実行後のWSL一時root: 0件
- 実行後の既知probe process: 0件
- 実行後のProcessProbe一時root: 0件

これはsynthetic fixtureを用いてproduction serviceをfresh processで再生成したrecovery試験であり、WinUI startup composition全体の再起動試験ではない。release readiness、実素材固有挙動、組織上のRPO/RTO目標、host NTFS volume全体の容量枯渇を承認するものでもない。

## 2. 実行環境

| 項目 | 実測値 |
|---|---|
| OS | Windows |
| Architecture | X64 |
| PowerShell | Core 7.6.5 |
| .NET SDK | 10.0.400 |
| 容量制約 | WSL上でroot mountした4 MiB `tmpfs` |
| Production assembly | `HubRadio.App`、`HubRadio.Infrastructure` |
| OS mechanism probe | `HubRadio.ProcessProbe`（Win32 Job Objectを直接操作） |

## 3. Scenario結果

| Scenario | Fault / recovery | 実測結果 |
|---|---|---|
| Process containment | 専用Win32 probeによるJob Objectのlast handle close | child/grandchildともJob内、close後に終了、10.607 ms、孤立0件 |
| Host crash | 専用Win32 probeの`Environment.FailFast`によるnonzero exit | crash marker、child/grandchild終了、孤立0件 |
| Job restart | running中のservice processを外部killし、fresh processで`JobRecovery`と`LocalJobRunner`を実行 | `Running -> Interrupted -> Queued -> Running -> Succeeded`、attempt 2件、progress 2件、heartbeat 2件、既存durable evidenceと保護成果物を保持 |
| Partial staging | 512 MiB sourceのproduction import中に外部kill | 4,325,376 byteの未完了stagingをexact candidateとして検出・検証後に清掃。sourceと承認済み成果物は不変 |
| Disk full | 4,194,304 byteの`tmpfs`をproduction import中に実容量枯渇 | failure時available 0 byte、`.NET IOException.HResult`は28（`0x0000001C`）、low 16 bitsもLinux `ENOSPC`の28、`CopyFailed`、source digest/metadataと既存destinationは不変、残留`.partial`/`.ready`各0件 |
| Artifact corruption | 同一sizeの成果物内容を破損 | digest mismatchを検出し、backupから検証付きrestore。破損workspaceをprevious copyとして保持 |
| Database corruption | SQLiteを破損 | invalid databaseを検出し、backupから検証付きrestore。破損workspaceをprevious copyとして保持 |
| Immutable records | restore前後のdomain evidenceを全field比較 | Project、original、approved artifact、composition digest/version、approval、audit chainが不変 |
| Cleanup | scenario完了後の外部再検査 | work root不在、WSL root 0件、既知probe process 0件、ProcessProbe一時root 0件 |

未完了staging 4,325,376 byteはdurable成果物ではなく、external killで中断された未昇格copyである。検証付き清掃対象であり、上記のdurable data loss 0 byteへ算入していない。

## 4. RPO観測

本試験におけるRPOは、fault前にdurable化された保護対象の損失量として扱った。

| 保護対象 | 観測損失 |
|---|---:|
| Original-kind synthetic artifact | 0 byte |
| 承認済み成果物 | 0 byte |
| Approval record | 0件 |
| Audit event | 0件 |
| Job progress record | 0件 |

Job kill時点で最新durable eventは122.000 ms前に記録されていた。これは今回のevent freshness観測であり、将来runの損失上限や組織上のRPO保証ではない。

Artifact corruptionはbackup完了0.591 ms後、database corruptionはbackup完了349.646 ms後に注入した。この短いbackup ageはfault injection条件であり、production backup cadenceを表さない。

## 5. RTO観測

| Recovery境界 | 実測値 |
|---|---:|
| Job recovery state transition | 82.689 ms |
| Retry execution | 34.928 ms |
| External killからjob `Succeeded` | 575.964 ms |
| Partial staging検出 | 79.246 ms |
| Partial staging検証付き清掃 | 29.940 ms |
| External killからpartial staging清掃確認 | 1,235.708 ms |
| Disk-full failure検出 | 1,752.440 ms |
| Disk-full後のproduction cleanup | 431.974 ms |
| Backup作成 | 300.471 ms |
| Artifact corruption検出 | 39.866 ms |
| Artifact corruption検出からrestore検証完了 | 342.128 ms |
| Database corruption検出 | 6.220 ms |
| Database corruption検出からrestore検証完了 | 333.705 ms |

全値はこのhost上の単回観測であり、production SLO、上限、percentileではない。scenarioごとに測定境界が異なるため、相互比較や合算をしない。

## 6. 不変性と復旧契約

- `MediaImporter`は容量枯渇時にsource、既存destinationを変更せず、作成済みpartialを清掃した。
- Linux disk-full probeは`IOException.HResult`のlow 16 bitsを実測済み`ENOSPC` 28に限定する。Windowsの`ERROR_HANDLE_DISK_FULL` 39と`ERROR_DISK_FULL` 112をLinux判定へ流用しない。
- `WorkspaceRecovery`はexternal killで残ったexact staging candidateを内容digest、size、timestamp、candidate digestで再検証してから清掃した。
- `JobRecovery`はfresh processでrunning attemptを`Interrupted`へ閉じ、安全に再開可能なkindだけを`Queued`へ戻した。
- `BackupService`はonline SQLite snapshot、artifact digest、manifest、catalog、audit chain、`integrity_check`を検証し、別directoryでrestoreを構築してからcutoverした。
- 同一size artifact corruptionとdatabase corruptionはいずれも黙って受理されず、検出後にactual backup restoreを通した。

## 7. Evidence source fingerprints

成功run中はsourceを凍結し、完了後に次のSHA-256を記録した。

| Relative source | SHA-256 |
|---|---|
| `tests/Reliability/Invoke-Q007RecoveryAcceptance.ps1` | `3b77c225530deeef4cbf1ac7dd2988dbd3291f5874b0f80e4cf7b1e9bb9bf585` |
| `tests/Reliability/HubRadio.Q007.ReliabilityProbe/Program.cs` | `299918b87a50a1e4f126b1bc296748102f9236dd7b88d754f81257493c19d224` |
| `tests/Reliability/HubRadio.Q007.ReliabilityProbe/HubRadio.Q007.ReliabilityProbe.csproj` | `d1970d813442b8098eff7505761b7df6c449976600f2598fe2899cf63c6aec0c` |
| `tests/Reliability/HubRadio.Q007.ReliabilityProbe/packages.lock.json` | `a35fd0278b807b9a8be54ff59af76a1a5a3109351777ee0dcd53f37c1b68d00b` |
| `tests/Reliability/HubRadio.Q007.JobRecoveryProbe/Program.cs` | `98c5826c7f2c478c7aa03d562533f22a13bd394f44da3ee817e8504ff07d864a` |
| `tests/Reliability/HubRadio.Q007.JobRecoveryProbe/HubRadio.Q007.JobRecoveryProbe.csproj` | `71e80eb08600e928cd3c39c7d4039770f981cb9388a00f8dcc2731629065d3c8` |
| `tests/Reliability/HubRadio.Q007.JobRecoveryProbe/packages.lock.json` | `6de5484f3522de0bf6299c8114eda80254806f3e38fbbc1c46be56d751947dd1` |
| `benchmarks/probes/ProcessProbe/Program.cs` | `f3a0c56866b4c8e9e0de9b2d3b8cbaf004d96141402678bfdb2682389851cf0c` |

Acceptance JSONは標準出力からmemory内でcaptureし、SHA-256と実測値を抽出した後は永続保存していない。canonical reportへはpath、SID、credential、媒体内容を入れていない。captureしたJSONのprivacy scanではWindows absolute path、WSL上のWindows mount path、SID、user name、repository名、credential assignment、path fieldの一致はいずれも0件だった。

## 8. 再実行

PowerShell 7で`tests/Reliability/Invoke-Q007RecoveryAcceptance.ps1`を実行する。runnerはWeb dependency/build gate、locked probe publish、全scenario、cleanupをfail-closedで実行し、成功時だけ単一JSONを標準出力へ返す。WSL default distributionでroot mount可能な`tmpfs`が必要である。

## 9. 残余範囲

- full WinUI startup compositionは未実施。fresh production service processの再起動だけを検証した。
- source、Original-kind artifact、承認済みRender-kind artifactは決定的synthetic bytesであり、実素材固有のrecovery挙動は未測定である。
- Process containment/host crashはWin32 Job ObjectのOS mechanism probeであり、production `WindowsJobObject`型を直接呼ぶscenarioではない。
- Disk-fullはWSL `tmpfs`上のLinux self-contained buildで同じ`HubRadio.Infrastructure` production codeを実行した。Windows host volume、NTFS、quota固有挙動は未測定である。
- RPO/RTO値は単一host・単一runの観測値であり、保証値ではない。
- power loss、kernel crash、filesystem controller failure、backup media喪失、長期backup retention、cross-machine restoreは未測定である。
- 破損workspaceをprevious copyとして保持できることは確認したが、そのretention/delete policyは本taskの対象外である。
