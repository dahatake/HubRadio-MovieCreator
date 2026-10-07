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
      <p className="section-kicker">字幕の見せ方</p>
      <h3 id="subtitle-display-title">字幕表示</h3>
      {displays.length === 0 ? (
        <div className="layout-variant-unavailable" role="note">
          <strong>選べる字幕の見せ方はありません</strong>
          <p>字幕の作成後に選べるようになります。</p>
        </div>
      ) : (
        <>
          {activeDigest === null && (
            <p className="layout-card-note" role="status">
              プレビューに表示する字幕の見せ方を選んでください。
            </p>
          )}
          <fieldset className="layout-variant-options" disabled={disabled}>
            <legend>プレビューに表示する字幕</legend>
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
                    {display.cueMode === 'full' ? '全文' : '一部の字幕'}・
                    {display.punctuationDisplay === 'preserve'
                      ? '句読点あり'
                      : '句読点なし'}
                  </strong>
                  <small>
                    字幕 {display.displayedCues.length} 件を表示（全 {display.sourceCueCount} 件）
                    {display.omittedCueIds.length > 0
                      ? `・表示する文字がない ${display.omittedCueIds.length} 件を除外`
                      : ''}
                  </small>
                </span>
              </label>
            ))}
          </fieldset>
          <p className="layout-card-note">
            この選択はプレビューの表示だけを切り替えます。文字起こしや作成済みの動画は変わりません。
          </p>
        </>
      )}
    </section>
  )
}
