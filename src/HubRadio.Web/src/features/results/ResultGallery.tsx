import { useId } from 'react'
import { AppIcon } from '../../components/AppIcons'
import { jobStatusLabels } from '../jobs/jobPresentation'
import { BookendStitchPanel } from '../stitch/BookendStitchPanel'
import type { BookendStitchService } from '../stitch/stitchProtocol'
import type { WorkflowWarningTarget } from '../workflow/workflowNavigation'
import { sourceOnlyMediaUrl, type SourceOnlyResultArtifact, type SourceOnlyResultDto } from '../workflow/pipelineProtocol'
import { evaluationRuleLabel, evaluationStatusLabel, failureCodeLabel, formatByteSize, formatDuration, generationRoleLabel, summarizeGenerationNotes } from './humanReadable'
import type { ResultCard, ResultKind, ResultPresentation, ResultState } from './resultPresentation'
import './results.css'

/** Caller-supplied real warning records, not synthesized from job/QC status. */
export interface ResultGalleryWarning {
  readonly id: string
  readonly message: string
  readonly target: WorkflowWarningTarget | null
  /** True only after the parent validates this destination against its live context. */
  readonly available?: boolean
  readonly unavailableReason?: string
}

export interface ResultGalleryProps {
  readonly presentation: ResultPresentation
  readonly onSelectArtifact?: (id: string) => void
  readonly warnings?: readonly ResultGalleryWarning[]
  readonly selectedWarningId?: string | null
  /** The parent must revalidate before navigating/loading/focusing the destination. */
  readonly onFollowWarning?: (target: WorkflowWarningTarget, warningId: string) => void
  /** FR-035 GUI. null/undefined shows the action as unavailable. */
  readonly bookendStitch?: BookendStitchService | null
}

const primaryGroups = [
  { kind: 'videoLong', title: '長尺動画' },
  { kind: 'videoShort', title: '短尺動画' },
  { kind: 'text', title: '字幕・テキスト' },
  { kind: 'thumbnail', title: 'サムネイル' },
] as const

const stateLabels: Readonly<Record<ResultState, string>> = {
  planned: '作成予定', processing: '生成中', generated: '生成済み',
  notProduced: '該当なし', failed: '失敗', stale: '変更後未生成', unavailable: '未取得',
}
const planLabels: Readonly<Record<ResultPresentation['planState'], string>> = {
  planned: '作成予定', stale: '設定変更あり', unavailable: '未取得',
}
const deliveryLabels: Readonly<Record<NonNullable<ResultPresentation['deliveryStatus']>, string>> = {
  available: '取得済み', unavailable: '利用不可', forbidden: '権限なし',
  incomplete: '情報不足', mismatched: '参照不一致', qualityControlFailed: '品質確認失敗',
}
const warningTargetLabels: Readonly<Record<WorkflowWarningTarget['kind'], string>> = {
  preview: '該当時刻へ', settings: '設定へ', evidence: '根拠へ', editor: '編集へ',
}

/** Display and selection intents only; no bridge reads, rendering or automatic selection. */
export function ResultGallery({
  presentation, onSelectArtifact, warnings = [], onFollowWarning, selectedWarningId = null, bookendStitch,
}: ResultGalleryProps) {
  const headingId = useId()
  // Preserve unclassified records without guessing that a master is a long video
  // or that an arbitrary image is a thumbnail. They do not expand the main 2x2 grid.
  const otherCards = presentation.cards.filter(card => card.kind === 'video' || card.kind === 'other')

  return (
    <section className="result-gallery" aria-labelledby={headingId}>
      <header className="result-gallery-header">
        <h2 id={headingId}><AppIcon name="play" />仕上がり一覧</h2>
        <p className="result-gallery-evaluation">
          自動評価 <strong data-evaluation={presentation.evaluation}>{evaluationStatusLabel(presentation.evaluation)}</strong>
        </p>
      </header>
      {presentation.pipelineResult !== undefined && (
        <p role="status" data-result-source={presentation.resultSource}>
          {presentation.resultSource === 'previous' ? '前回の成果物です。今回の処理とは別の実行・親版の結果を保持しています。' : '今回の実行に結び付いた成果物です。'}
          {' '}内容の理解・自然さ・法的適合など、人による確認は自動評価に含まれません。
        </p>
      )}
      <dl className="result-gallery-context">
        <div><dt>予定</dt><dd data-plan-state={presentation.planState}>{planLabels[presentation.planState]}</dd></div>
        <div>
          <dt>成果物情報</dt>
          <dd data-delivery-status={presentation.deliveryStatus ?? 'notLoaded'}>
            {presentation.deliveryStatus === null ? '未取得' : deliveryLabels[presentation.deliveryStatus]}
          </dd>
        </div>
        {presentation.jobStatus !== null && (
          <div><dt>処理</dt><dd data-job-status={presentation.jobStatus}>{jobStatusLabels[presentation.jobStatus]}</dd></div>
        )}
      </dl>
      <ul className="result-gallery-grid" aria-label="成果物の種類" role="list">
        {primaryGroups.map(group => (
          <li key={group.kind}>
            <ResultGroup
              {...group}
              card={presentation.cards.find(card => card.kind === group.kind)}
              onSelectArtifact={onSelectArtifact}
              pipelineResult={presentation.pipelineResult}
              bookendStitch={bookendStitch}
            />
          </li>
        ))}
      </ul>
      {otherCards.length > 0 && (
        <details className="result-gallery-other">
          <summary tabIndex={0}>尺未分類・その他</summary>
          <ul className="result-gallery-other-list" aria-label="未分類の成果物" role="list">
            {otherCards.map(card => (
              <li key={card.id}>
                <ResultGroup kind={card.kind} title={card.title} card={card} onSelectArtifact={onSelectArtifact} />
              </li>
            ))}
          </ul>
        </details>
      )}
      {warnings.length > 0 && (
        <section className="result-gallery-warnings" aria-label="成果物の警告">
          <h3>確認事項</h3>
          <ul role="list">
            {warnings.map(warning => (
              <ResultWarning key={warning.id} warning={warning} selected={selectedWarningId === warning.id} onFollowWarning={onFollowWarning} />
            ))}
          </ul>
        </section>
      )}
      {presentation.pipelineResult !== undefined && (
        <section className="result-gallery-warnings" aria-label="付帯成果物">
          <h3>文字起こしと使用区間一覧</h3>
          {presentation.pipelineResult.ancillaryArtifacts.length === 0 ? (
            <p role="status">
              今回の結果には文字起こし・使用区間一覧が含まれていません。動画・字幕・サムネイルの生成状況は上の一覧のとおりです。
            </p>
          ) : (
            <ul role="list">
              {presentation.pipelineResult.ancillaryArtifacts.map(item => (
                <li key={item.artifact.artifactId}>
                  {ancillaryArtifactLabels[item.kind]}（{formatByteSize(item.artifact.byteSize)}）
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
      {presentation.pipelineResult !== undefined && (
        <details className="result-gallery-other">
          <summary>生成の省略・失敗・保留の記録</summary>
          <ul>
            {presentation.pipelineResult.omissions.map((item, index) => <li key={`omission-${index}`}>{generationRoleLabel(item.role)}: 今回は作成しませんでした</li>)}
            {presentation.pipelineResult.failures.map((item, index) => <li key={`failure-${index}`}>失敗: {failureCodeLabel(item.code)}</li>)}
            {summarizeGenerationNotes(presentation.pipelineResult.generationWarnings).map(note =>
              <li key={`generation-${note.text}`}>{note.text}{note.count > 1 ? `（${note.count}件）` : ''}</li>)}
          </ul>
          {presentation.pipelineResult.stagingCleanupRequired && <p>一時ファイルの整理が必要です。画面側から削除はしません。</p>}
          {presentation.pipelineResult.reconciliationArtifacts.length > 0 && <p>整合性の再確認が必要な成果物があります。</p>}
        </details>
      )}
    </section>
  )
}

function ResultGroup({ kind, title, card, onSelectArtifact, pipelineResult, bookendStitch }: Readonly<{
  kind: ResultKind
  title: string
  card?: ResultCard
  onSelectArtifact?: ResultGalleryProps['onSelectArtifact']
  pipelineResult?: SourceOnlyResultDto
  bookendStitch?: BookendStitchService | null
}>) {
  const headingId = useId()
  const state = card?.state ?? 'unavailable'
  const artifacts = card?.artifacts ?? []
  const pipelineArtifacts = card?.pipelineArtifacts ?? []
  const label = card?.title ?? title
  const saved = card !== undefined && card.state === 'generated' &&
    card.saveState === 'saved' && (artifacts.length > 0 || pipelineArtifacts.length > 0)

  return (
    <article
      className="result-gallery-card"
      aria-labelledby={headingId}
      data-result-kind={kind}
      data-result-id={card?.id}
      data-result-state={state}
      data-save-state={saved ? 'saved' : 'unavailable'}
    >
      <header className="result-card-heading">
        <h3 id={headingId}>{label}</h3>
        <span className="result-card-state">{stateLabels[state]}</span>
      </header>
      <dl className="result-gallery-facts">
        <div>
          <dt>予定</dt>
          <dd>{card === undefined ? '未取得' : card.plannedLabels.length > 0 ? card.plannedLabels.join('・') : '予定情報なし'}</dd>
        </div>
        <div><dt>実生成</dt><dd className="result-card-count">{card?.count === undefined ? '未取得' : `${card.count}件`}</dd></div>
        <div><dt>ローカル保存</dt><dd>{saved ? '保存済み' : '未確認'}</dd></div>
      </dl>
      {state === 'notProduced' && <p className="result-gallery-note">この保存記録に該当成果物はありません。</p>}
      {kind === 'thumbnail' && artifacts.length > 0 && (
        <p className="result-gallery-note">画像参照は未取得です。</p>
      )}
      {artifacts.length > 0 && (
        <details className="result-gallery-artifacts">
          <summary tabIndex={0}>{kind === 'thumbnail' ? '実候補を選択' : '実成果物を選択'}</summary>
          {onSelectArtifact === undefined && <p className="result-gallery-note">選択操作は未接続です。</p>}
          <ul aria-label={`${label}の実成果物`} role="list">
            {artifacts.map((artifact, index) => (
              <li key={artifact.artifactId}>
                <button
                  type="button"
                  disabled={onSelectArtifact === undefined}
                  onClick={() => onSelectArtifact?.(artifact.artifactId)}
                >
                  {label}{kind === 'thumbnail' ? '候補' : ''} {index + 1}を選択
                </button>
                <span className="result-artifact-reference">{artifact.mediaType.startsWith('image/') ? '画像' : artifact.mediaType.startsWith('video/') ? '動画' : 'ファイル'}</span>
              </li>
            ))}
          </ul>
        </details>
      )}
      {pipelineResult !== undefined && pipelineArtifacts.length > 0 && (
        <ul aria-label={`${label}の実成果物`} role="list">
          {pipelineArtifacts.map(a => <li key={a.artifact.artifactId}>
            <PipelineArtifact result={pipelineResult} artifact={a} onSelectArtifact={onSelectArtifact} bookendStitch={bookendStitch} />
          </li>)}
        </ul>
      )}
    </article>
  )
}

function ResultWarning({ warning, onFollowWarning, selected }: Readonly<{
  warning: ResultGalleryWarning
  selected: boolean
  onFollowWarning?: ResultGalleryProps['onFollowWarning']
}>) {
  // Accessibility IDs are local DOM IDs; the callback always receives the real record ID.
  const messageId = useId()
  const target = warning.target
  const reason = target === null || warning.available !== true
    ? warning.unavailableReason ?? '移動先を確認できません。'
    : onFollowWarning === undefined ? '移動操作は未接続です。' : null

  return (
    <li className="result-gallery-warning" data-warning-id={warning.id} aria-current={selected ? 'true' : undefined} tabIndex={-1}>
      <p id={messageId}>{warning.message}</p>
      <button
        type="button"
        disabled={reason !== null}
        aria-describedby={`${messageId}${reason === null ? '' : ` ${messageId}-reason`}`}
        onClick={() => {
          if (reason === null && target !== null) onFollowWarning?.(target, warning.id)
        }}
      >
        {target === null ? '移動先未取得' : warningTargetLabels[target.kind]}
      </button>
      {reason !== null && <p id={`${messageId}-reason`} className="result-gallery-note">{reason}</p>}
    </li>
  )
}

export const pipelineArtifactLabels: Readonly<Record<SourceOnlyResultArtifact['role'], string>> = {
  LandscapeClean: '長尺・クリーン版', LandscapeBurnIn: '長尺・字幕焼き込み版', VerticalBurnIn: '短尺・字幕焼き込み版',
  SubRip: 'SRT 字幕', WebVtt: 'WebVTT 字幕', LandscapeThumbnail: '横長サムネイル', VerticalThumbnail: '縦型サムネイル', VerticalClean: '短尺・クリーン版',
}
type AncillaryKind = SourceOnlyResultDto['ancillaryArtifacts'][number]['kind']
export const ancillaryArtifactLabels: Readonly<Record<AncillaryKind, string>> = {
  Transcript: '文字起こし', UsedSourceRanges: '元動画のどこを使ったかの一覧',
}
function PipelineArtifact({ result, artifact: a, onSelectArtifact, bookendStitch }: Readonly<{
  result: SourceOnlyResultDto
  artifact: SourceOnlyResultArtifact
  onSelectArtifact?: ResultGalleryProps['onSelectArtifact']
  bookendStitch?: BookendStitchService | null
}>) {
  const url = sourceOnlyMediaUrl(result, a.artifact.artifactId)
  const label = pipelineArtifactLabels[a.role]
  return (
    <article data-artifact-id={a.artifact.artifactId} data-artifact-digest={a.artifact.contentDigest}>
      <h4>{label}</h4>
      {url !== null && (a.mediaType === 'image/png'
        ? <img src={url} alt={label} loading="lazy" style={{ maxWidth: '100%' }} />
        : <video src={url} controls preload="metadata" playsInline aria-label={label} style={{ maxWidth: '100%' }} />)}
      <p>自動評価: <strong data-artifact-evaluation={a.evaluation?.aggregateStatus ?? 'NOT EVALUATED'}>{evaluationStatusLabel(a.evaluation?.aggregateStatus)}</strong></p>
      <p data-artifact-delivered={a.delivered}>保存: {a.delivered ? '保存済み（ファイルを確認済み）' : '未保存'}</p>
      {a.failureCode !== null && <p role="status">失敗: {failureCodeLabel(a.failureCode)}</p>}
      {a.measurements !== null && <p>長さ: {formatDuration(a.measurements.expectedDuration.value, a.measurements.expectedDuration.rate)}</p>}
      <button type="button" disabled={onSelectArtifact === undefined || url === null} onClick={() => onSelectArtifact?.(a.artifact.artifactId)}>{label}をプレビュー</button>
      {url !== null && a.mediaType === 'video/mp4' &&
        <BookendStitchPanel service={bookendStitch} result={result} artifact={a} label={label} />}
      <details>
        <summary>この成果物の自動チェック結果</summary>
        <p>ファイルの大きさ: {formatByteSize(a.artifact.byteSize)}</p>
        {a.evaluation === null ? <p>評価結果はありません。</p> : <ul>{[...a.evaluation.rules, ...a.evaluation.limitations].map(rule =>
          <li key={rule.ruleId} data-rule-id={rule.ruleId} data-rule-status={rule.status}>{evaluationRuleLabel(rule.ruleId)}: <strong>{evaluationStatusLabel(rule.status)}</strong></li>)}</ul>}
      </details>
    </article>
  )
}