import type { LayoutSubtitleDisplay } from '../layout/layoutProtocol'

export interface SubtitleDisplaySelectorProps {
  readonly displays: readonly LayoutSubtitleDisplay[]
  readonly activeDigest: string | null
  readonly disabled: boolean
  readonly onChange: (digest: string) => void
}

export function SubtitleDisplaySelector({
  displays,
  activeDigest,
  disabled,
  onChange,
}: SubtitleDisplaySelectorProps) {
  return (
    <section
      className="layout-editor-card subtitle-display-selector"
      aria-labelledby="subtitle-display-title"
    >
      <p className="section-kicker">Native display-policy evidence</p>
      <h3 id="subtitle-display-title">字幕表示</h3>
      {displays.length === 0 ? (
        <div className="layout-variant-unavailable" role="note">
          <strong>表示 profile は供給されていません</strong>
          <p>画面側で全文・選択 Cue・句読点の組合せを捏造しません。</p>
        </div>
      ) : (
        <>
          {activeDigest === null && (
            <p className="layout-card-note" role="status">
              preview へ表示する profile を明示的に選択してください。
            </p>
          )}
          <fieldset className="layout-variant-options" disabled={disabled}>
            <legend>preview に投影する既存 profile</legend>
            {displays.map((display) => (
              <label key={display.profileVersionDigest}>
                <input
                  type="radio"
                  name="subtitle-display-profile"
                  value={display.profileVersionDigest}
                  checked={activeDigest === display.profileVersionDigest}
                  onChange={() => onChange(display.profileVersionDigest)}
                />
                <span>
                  <strong>
                    {display.cueMode === 'full' ? '全文' : '選択 Cue'}・
                    {display.punctuationDisplay === 'preserve'
                      ? '句読点あり'
                      : '句読点なし'}
                  </strong>
                  <small>
                    表示 {display.displayedCues.length} / 元 Cue {display.sourceCueCount}
                    {display.omittedCueIds.length > 0
                      ? `・空表示を ${display.omittedCueIds.length} 件除外`
                      : ''}
                  </small>
                  <code>{display.profileVersionDigest}</code>
                </span>
              </label>
            ))}
          </fieldset>
          <p className="layout-card-note">
            この選択は native の SubtitleDisplayPolicy 結果を切り替える preview 専用状態です。原文、保存、render 設定は変更しません。
          </p>
        </>
      )}
    </section>
  )
}
