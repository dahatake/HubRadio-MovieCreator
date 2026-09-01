import { useState, type FormEvent } from 'react'

import {
  SubtitleDisplaySelector,
  SubtitleEditor,
  SubtitleVariantSelector,
} from '../subtitles'
import { KeyframeEditor } from './KeyframeEditor'
import { LayoutPreview, type LayoutVariantChoice } from './LayoutPreview'
import { ManualLayoutEditor } from './ManualLayoutEditor'
import {
  isCanonicalLayoutEntityId,
  type LayoutBridgeStatus,
  type LayoutSnapshot,
} from './layoutProtocol'
import {
  useLayoutState,
  type LayoutStateContextValue,
} from './LayoutStateProvider'

const failureMessages: Readonly<
  Record<Exclude<LayoutBridgeStatus, 'available' | 'noChange'>, string>
> = {
  forbidden: '現在の Windows ユーザーには、このレイアウトを閲覧または変更する権限がありません。',
  notFound: '指定した案件のレイアウト aggregate は見つかりませんでした。',
  conflict: 'レイアウトまたは Cue の revision が更新されました。最新版を再読み込みしてください。',
  invalid: '要求または native evidence を閉じた contract として検証できませんでした。',
  unavailable: 'production layout provider は利用できません。利用可能な状態を画面側で補完していません。',
}

export function LayoutPanel() {
  return <LayoutPanelView {...useLayoutState()} />
}

export function LayoutPanelView({
  state,
  changeProjectId,
  loadSnapshot,
  refreshSnapshot,
  applyOperation,
  clearError,
}: LayoutStateContextValue) {
  const pending = state.pending !== null
  const failureStatus = state.status !== null &&
    state.status !== 'available' &&
    state.status !== 'noChange'
    ? state.status
    : null

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    loadSnapshot()
  }

  return (
    <section
      className="layout-panel"
      aria-labelledby="layout-title"
      data-layout-status={state.status ?? 'idle'}
    >
      <div className="layout-heading">
        <div>
          <p className="eyebrow">Exact 9:16 composition evidence</p>
          <h2 id="layout-title">縦型レイアウト・字幕・テロップ</h2>
          <p>
            native の LayoutResolver、SubtitleLayoutEngine、Cue revision evidence を同じ aggregate revision で編集します。
          </p>
        </div>
        {state.snapshot !== null && (
          <span className="layout-evidence-badge">
            revision {state.snapshot.revision}・未永続化
          </span>
        )}
      </div>

      <form className="layout-load-form" onSubmit={submit}>
        <label htmlFor="layout-project-id">確定済みプロジェクト ID</label>
        <div className="layout-load-row">
          <input
            id="layout-project-id"
            type="text"
            autoComplete="off"
            spellCheck={false}
            minLength={36}
            maxLength={36}
            pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
            value={state.projectIdInput}
            disabled={pending}
            aria-describedby="layout-project-help"
            onChange={(event) => changeProjectId(event.currentTarget.value)}
          />
          <button
            className="primary-button"
            type="submit"
            disabled={pending || !isCanonicalLayoutEntityId(state.projectIdInput)}
          >
            レイアウトを開く
          </button>
          {state.snapshot !== null && (
            <button
              className="secondary-button"
              type="button"
              disabled={pending}
              onClick={refreshSnapshot}
            >
              最新版を再読み込み
            </button>
          )}
        </div>
        <p id="layout-project-help" className="field-help">
          小文字 UUID を指定します。authorization と project correlation は native 側でも再検証されます。
        </p>
      </form>

      {state.error !== null && (
        <div className="layout-error" role="alert">
          <div>
            <strong>レイアウト操作を開始できませんでした</strong>
            <p>{state.error.message}</p>
          </div>
          <button className="quiet-button" type="button" onClick={clearError}>
            閉じる
          </button>
        </div>
      )}

      <p
        className="layout-operation-status"
        role="status"
        aria-live="polite"
        data-layout-pending={pending ? 'true' : 'false'}
      >
        {pending
          ? state.pending?.method === 'layout.apply'
            ? '同じ aggregate revision に対する変更を native 側で検証しています。'
            : 'レイアウト aggregate を native 側で検証しています。'
          : statusMessage(state.status, state.lastCompletedMethod)}
      </p>

      {failureStatus !== null ? (
        <div className="layout-failure" role="alert">
          <strong>レイアウト aggregate を利用できません</strong>
          <p>{failureMessages[failureStatus]}</p>
        </div>
      ) : state.snapshot === null ? (
        <div className="layout-empty">
          <strong>レイアウトは未選択です</strong>
          <p>プロジェクトを読み込むまで、mode、座標、Cue、variant evidence は表示しません。</p>
        </div>
      ) : (
        <LayoutWorkspace
          key={`${state.snapshot.compositionVersionId}-${state.snapshot.revision}`}
          snapshot={state.snapshot}
          disabled={pending}
          applyOperation={applyOperation}
        />
      )}
    </section>
  )
}

function LayoutWorkspace({
  snapshot,
  disabled,
  applyOperation,
}: Readonly<{
  snapshot: LayoutSnapshot
  disabled: boolean
  applyOperation: LayoutStateContextValue['applyOperation']
}>) {
  const [variantChoice, setVariantChoice] = useState<LayoutVariantChoice>(null)
  const [displayProfileDigest, setDisplayProfileDigest] = useState<string | null>(
    () => initialDisplayProfileDigest(snapshot),
  )
  const activeDisplay = snapshot.subtitleDisplays.find(
    (display) => display.profileVersionDigest === displayProfileDigest,
  ) ?? null
  const requiresKeyframes =
    snapshot.manualSelection?.status === 'manualKeyframesRequired'
  const unresolvedSubtitles = snapshot.subtitles.filter(
    (cue) => cue.layoutResolution.status === 'manualAdjustmentRequired',
  ).length
  const graphicRegionNames = snapshot.layout.regions
    .filter((region) => region.role === 'graphic')
    .map((region) => region.name)

  const changeDisplayProfile = (digest: string): void => {
    setDisplayProfileDigest(digest)
    if (snapshot.variantPair?.displayProfileDigest !== digest) {
      setVariantChoice(null)
    }
  }

  const changeVariant = (
    choice: Exclude<LayoutVariantChoice, null>,
  ): void => {
    setVariantChoice(choice)
    if (snapshot.variantPair !== null) {
      setDisplayProfileDigest(snapshot.variantPair.displayProfileDigest)
    }
  }

  return (
    <div className="layout-workspace">
      <div className="layout-persistence-notice" role="note">
        <strong>未永続化</strong>
        <span>
          この runtime は persistence = {snapshot.persistence} です。保存済みとは表示しません。
        </span>
      </div>
      {(requiresKeyframes || unresolvedSubtitles > 0) && (
        <div className="layout-manual-required" role="alert">
          <strong>手動調整が必要です</strong>
          <p>
            {requiresKeyframes ? 'manual keyframe が必要です。' : ''}
            {unresolvedSubtitles > 0
              ? ` 自動配置の解がない字幕が ${unresolvedSubtitles} 件あります。`
              : ''}
            自動 fallback は適用しません。
          </p>
        </div>
      )}
      <dl className="layout-identity">
        <div><dt>Composition version</dt><dd><code>{snapshot.compositionVersionId}</code></dd></div>
        <div><dt>Composition digest</dt><dd><code>{snapshot.compositionVersionDigest}</code></dd></div>
        <div><dt>Layout spec</dt><dd><code>{snapshot.layout.layoutSpecId}</code></dd></div>
        <div><dt>Output</dt><dd>{snapshot.output.width} × {snapshot.output.height}（9:16）</dd></div>
      </dl>

      <div className="layout-preview-grid">
        <LayoutPreview
          snapshot={snapshot}
          variantChoice={variantChoice}
          subtitleDisplay={activeDisplay}
        />
        <div className="layout-preview-controls">
          <SubtitleDisplaySelector
            displays={snapshot.subtitleDisplays}
            activeDigest={displayProfileDigest}
            disabled={disabled}
            onChange={changeDisplayProfile}
          />
          <SubtitleVariantSelector
            variantPair={snapshot.variantPair}
            choice={variantChoice}
            disabled={disabled}
            onChange={changeVariant}
          />
        </div>
      </div>

      <ManualLayoutEditor
        snapshot={snapshot}
        disabled={disabled}
        applyOperation={applyOperation}
      />
      <KeyframeEditor
        snapshot={snapshot}
        disabled={disabled}
        applyOperation={applyOperation}
      />
      <SubtitleEditor
        snapshot={snapshot}
        disabled={disabled}
        applyOperation={applyOperation}
        graphicRegionNames={graphicRegionNames}
      />
    </div>
  )
}

function initialDisplayProfileDigest(snapshot: LayoutSnapshot): string | null {
  return snapshot.variantPair?.displayProfileDigest ?? null
}

function statusMessage(
  status: LayoutBridgeStatus | null,
  method: LayoutStateContextValue['state']['lastCompletedMethod'],
): string {
  if (status === null) {
    return 'プロジェクトを指定してレイアウト aggregate を読み込んでください。'
  }
  if (status === 'available') {
    return method === 'layout.apply'
      ? '変更後の immutable snapshot を native 側で検証しました。'
      : 'レイアウト aggregate を native 側で検証しました。'
  }
  if (status === 'noChange') {
    return '変更内容は現行 evidence と同一でした。現行 snapshot を維持します。'
  }
  return 'native 側の検証は閉じた失敗として完了しました。'
}
