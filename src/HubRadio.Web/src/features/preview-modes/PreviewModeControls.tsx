import { formatRationalTime, type PlayerExactFrameSelection } from '../player'
import { isCanonicalEntityId } from '../player/playerProtocol'
import { usePreviewModes } from './PreviewModeProvider'
import type { PreviewOrientation } from './previewModeState'

export interface PreviewModeControlsProps {
  readonly selection: PlayerExactFrameSelection | null
  readonly context?: Readonly<{ jobId: string | null }>
  readonly active?: boolean
  /** FR-026: プレビューから編集タイムライン上の該当箇所へ移動する。 */
  readonly onOpenTimeline?: (position: PlayerExactFrameSelection) => void
}

const orientations: ReadonlyArray<
  Readonly<{ value: PreviewOrientation; label: string }>
> = Object.freeze([
  Object.freeze({ value: 'landscape', label: '横長表示枠（16:9）' }),
  Object.freeze({ value: 'portrait', label: '縦型表示枠（9:16）' }),
])

export function PreviewModeControls({
  selection,
  context,
  active = true,
  onOpenTimeline,
}: PreviewModeControlsProps) {
  const { state, dispatch } = usePreviewModes()
  const currentSelection = selection !== null && (
    context === undefined || (
      context.jobId !== null &&
      isCanonicalEntityId(context.jobId) &&
      selection.jobId === context.jobId
    )
  ) ? selection : null
  const disabled = currentSelection === null || !active

  return (
    <section
      className={context === undefined
        ? 'preview-mode-card'
        : 'preview-mode-card preview-mode-workflow'}
      aria-labelledby="preview-mode-title"
      hidden={!active}
      inert={!active}
      data-preview-orientation={state.orientation ?? 'source'}
      data-subtitles-visible={String(state.subtitlesVisible)}
      data-generic-mask={String(state.genericUiMaskVisible)}
      data-thumbnail-reduced={String(state.thumbnailReduced)}
      data-mobile-preview={String(state.mobilePreview)}
      data-audio-only={String(state.audioOnly)}
    >
      <div className="preview-mode-heading">
        <div>
          {context === undefined ? <p className="eyebrow">Session-only display</p> : null}
          <h2 id="preview-mode-title">プレビューモード</h2>
        </div>
        <output
          className="preview-mode-position"
          data-media-available={currentSelection === null ? 'false' : 'true'}
        >
          {currentSelection === null
            ? '動画・媒体のプレビュー根拠がありません'
            : context === undefined
              ? `共有 timeline: proxy frame ${currentSelection.frame.proxyFrameSequence}・Source ${formatRationalTime(currentSelection.frame.sourcePresentationTime)}・Proxy ${formatRationalTime(currentSelection.frame.proxyPresentationTime)}`
              : `代理フレーム ${currentSelection.frame.proxyFrameSequence + 1}・Source ${formatRationalTime(currentSelection.frame.sourcePresentationTime)}・Proxy ${formatRationalTime(currentSelection.frame.proxyPresentationTime)}`}
        </output>
      </div>

      {onOpenTimeline !== undefined ? (
        <button
          type="button"
          className="preview-open-timeline"
          disabled={disabled}
          onClick={() => {
            if (currentSelection !== null) onOpenTimeline(currentSelection)
          }}
        >
          タイムラインの該当箇所へ移動
        </button>
      ) : null}

      <div className="preview-mode-controls">
        <fieldset className="preview-orientation-options" disabled={disabled}>
          <legend>表示枠</legend>
          {orientations.map((orientation) => (
            <label key={orientation.value}>
              <input
                type="radio"
                name="preview-orientation"
                value={orientation.value}
                disabled={disabled}
                checked={state.orientation === orientation.value}
                onChange={() =>
                  dispatch({
                    type: 'orientationChanged',
                    orientation: orientation.value,
                  })
                }
              />
              <span>{orientation.label}</span>
            </label>
          ))}
          {context === undefined ? (
            <p className="field-help">
              未選択では代理媒体が持つ比率をそのまま使用します。
            </p>
          ) : null}
        </fieldset>

        <fieldset className="preview-toggle-options" disabled={disabled}>
          <legend>表示内容</legend>
          <PreviewToggle
            label="字幕を表示"
            disabled={disabled}
            checked={state.subtitlesVisible}
            onChange={(visible) =>
              dispatch({ type: 'subtitlesVisibilityChanged', visible })
            }
          />
          <PreviewToggle
            label={context === undefined ? '汎用 UI mask' : '汎用 UI mask（配信先未検証）'}
            disabled={disabled}
            checked={state.genericUiMaskVisible}
            onChange={(visible) =>
              dispatch({ type: 'genericUiMaskVisibilityChanged', visible })
            }
          />
          <PreviewToggle
            label="サムネイル縮小"
            disabled={disabled}
            checked={state.thumbnailReduced}
            onChange={(reduced) =>
              dispatch({ type: 'thumbnailReductionChanged', reduced })
            }
          />
          <PreviewToggle
            label="mobile 配置"
            disabled={disabled}
            checked={state.mobilePreview}
            onChange={(enabled) =>
              dispatch({ type: 'mobilePreviewChanged', enabled })
            }
          />
          <PreviewToggle
            label="音声のみ"
            disabled={disabled}
            checked={state.audioOnly}
            onChange={(enabled) =>
              dispatch({ type: 'audioOnlyChanged', enabled })
            }
          />
        </fieldset>
      </div>

      {context === undefined ? (
        <div className="preview-mode-notes" role="note">
          <p>
            モード変更は既存の代理媒体と layout evidence の表示だけを変えます。timeline
            時刻、render、保存、承認を変更せず、切替のための seek も発行しません。
          </p>
          <p>
            横長・縦型は媒体を引き伸ばさない確認枠です。汎用 UI mask はローカルで決定的に重ねるだけで、実在する配信先 UI や safe zone の忠実性を主張しません。mobile
            配置も端末寸法を模倣しません。
          </p>
          {state.audioOnly ? (
            <p>
              音声のみでは映像を隠します。再生とミュートはプレイヤーで引き続き利用者が操作します。
            </p>
          ) : null}
        </div>
      ) : (
        <p
          className="preview-mode-fidelity"
          role="note"
          data-platform-fidelity="not-claimed"
          data-output-verification="not-verified"
        >
          表示のみの切替です。最終出力の画質・コーデック、配信先 UI・セーフゾーンは未検証です。
        </p>
      )}
    </section>
  )
}

function PreviewToggle({
  label,
  disabled,
  checked,
  onChange,
}: Readonly<{
  label: string
  disabled: boolean
  checked: boolean
  onChange: (checked: boolean) => void
}>) {
  return (
    <label>
      <input
        type="checkbox"
        disabled={disabled}
        checked={checked}
        onChange={(event) => onChange(event.currentTarget.checked)}
      />
      <span>{label}</span>
    </label>
  )
}