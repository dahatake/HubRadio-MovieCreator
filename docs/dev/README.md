# 開発者ドキュメント

HubRadio MovieCreator の開発、保守、技術検証、設計判断に関する入口です。

> [!IMPORTANT]
> Stage 0 と Foundation `F-001〜F-009`は完了済みで、2026年8月30日時点で全203 task中93件を完了しました。`D-001〜D-016`、`J-001〜J-010`、`M-001〜M-010`、`R-001〜R-009/R-016`、`U-001〜U-004`、主要なS系統制を実装済みです。`S-003`は別account受入待ち、`M-011`はdurable restart checkpoint未達で監査中です。業務UI、解析後段、Render IR以降、配布は未完了です。最新状態は[詳細実装計画](planning/implementation-plan.md)を参照してください。

## 目的から探す

| 目的 | 読む文書 |
|---|---|
| 開発環境を用意して小さい検証を実行する | [開発を始める](getting-started.md) |
| 現在と計画中の構成を理解する | [アーキテクチャ概要](architecture/overview.md) |
| Stage 0 の実行方法と証拠を確認する | [Stage 0 ベンチマーク](benchmarks/README.md) |
| task、依存、gate、要求対応を確認する | [詳細実装計画](planning/implementation-plan.md) |
| 技術候補と調査根拠を確認する | [実現方式調査](research/implementation-research.md) |
| 採用済みの設計判断を確認する | [ADR](decisions/) |
| 検証と敵対的レビューの方法を確認する | [品質管理](quality.md) |
| データ、秘密、権利、Azure 境界を確認する | [セキュリティとプライバシー](security-privacy.md) |
| Stage 0 の問題を調べる | [トラブルシューティング](troubleshooting.md) |
| 変更手順と最小実装の原則を確認する | [開発への参加](contributing.md) |
| 第三者依存の受入状態を確認する | [第三者ソフトウェア・モデル受入方針](licenses/third-party-intake.md) |

## 最初に読む順序

### Stage 0 の検証を再現する

1. [開発を始める](getting-started.md)
2. [Stage 0 ベンチマーク](benchmarks/README.md)
3. 対象 stage の canonical result
4. [実装進行成果物の敵対的レビュー](reviews/implementation-adversarial-review.md)
5. [詳細実装計画](planning/implementation-plan.md)の対象 task と完了条件

### 機能を追加・変更する

1. [製品要求](../requirement-definition.md)の対象 ID
2. 関連する [Accepted ADR](decisions/)
3. [詳細実装計画](planning/implementation-plan.md)の task、依存、gate
4. [アーキテクチャ概要](architecture/overview.md)と [品質管理](quality.md)
5. [開発への参加](contributing.md)の変更手順

### セキュリティまたは配布を変更する

1. [セキュリティとプライバシー](security-privacy.md)
2. [公開区分と AI 送信区分](decisions/ADR-0004-data-classification.md)
3. [保存、保持、backup](decisions/ADR-0005-storage-retention.md)
4. [Windows 配布と条件付き Azure](decisions/ADR-0006-distribution-and-azure.md)
5. [Stage 0技術選択と手動fallback](decisions/ADR-0007-stage-0-technology-selection.md)
6. [第三者依存の受入状態](licenses/third-party-intake.md)

## 正本の優先順位

実装判断が競合する場合は、[ADR-0001](decisions/ADR-0001-source-of-truth.md) に従います。

1. [`docs/requirement-definition.md`](../requirement-definition.md) — 何を実現するか、優先順位、受入基準
2. [実現方式調査の敵対的レビュー](reviews/adversarial-review.md) — 確認済み問題と修正条件
3. [Accepted ADR](decisions/) — 要求を変えない範囲の具体的判断
4. [実現方式調査](research/implementation-research.md) — 候補技術と PoC 条件
5. [詳細実装計画](planning/implementation-plan.md) — 実行順序、成果物、依存関係

上位文書と下位文書が矛盾する場合は上位を採用します。後の Accepted ADR が同じ論点の古い ADR を置き換えます。測定前の数値、未確認の model 能力、未承認の外部 service 条件は正本とみなしません。

## 文書構成

```text
docs/
├─ requirement-definition.md          # 製品要求の正本
└─ dev/
   ├─ README.md                        # 開発者向け入口
   ├─ getting-started.md               # Stage 0 開発環境
   ├─ quality.md                       # 品質と敵対的レビュー
   ├─ security-privacy.md              # データ・秘密・権利・Azure 境界
   ├─ troubleshooting.md               # Stage 0 の問題解決
   ├─ contributing.md                  # 変更手順
   ├─ architecture/
   │  ├─ overview.md
   │  ├─ constraints.md
   │  └─ system-overview.svg
   ├─ benchmarks/
   │  ├─ README.md
   │  ├─ annotation-policy.md
   │  ├─ metric-definitions.md
   │  └─ result-schema-policy.md
   ├─ decisions/                       # ADR-0001〜0007
   ├─ licenses/
   │  ├─ third-party-intake.md
   │  ├─ dependencies.csv
   │  ├─ models.csv
   │  └─ THIRD-PARTY-NOTICES.md
   ├─ planning/
   │  └─ implementation-plan.md
   ├─ research/
   │  └─ implementation-research.md
   └─ reviews/
      ├─ adversarial-review.md
      └─ implementation-adversarial-review.md
```

`benchmarks/` の code、fixture manifest、schema、canonical result、`test/` の検証証拠、`work/` の日付付き作業成果物は、読者向けガイドではないため各成果物の近くに残します。

## 文書の更新規則

- ルート `README.md` は非エンジニアの一般利用者向けに保ちます。
- 開発、保守、技術検証、設計判断は `docs/dev/` に置きます。
- 製品要求の正本だけは `docs/requirement-definition.md` に維持します。
- 実装済み、計画中、条件付き、未測定を明記します。
- 新しい空 directory や未使用の文書 template を先に作りません。
- 文書を移動した場合は、リポジトリ内参照と ADR-0001 の path／SHA-256 を同期します。
- 長い説明を複製せず、正本へ相対リンクします。
- アーキテクチャ図は SVG を使用し、`title`、`desc`、代替 text、現在の進捗を確認します。

## 関連するリポジトリ入口

- [一般利用者向け README](../../README.md)
- [ライセンス](../../LICENSE)
- [Issue](https://github.com/dahatake/HubRadio-MovieCreator/issues)
