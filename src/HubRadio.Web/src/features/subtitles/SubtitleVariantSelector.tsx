import type { LayoutVariantPair } from '../layout/layoutProtocol'
import type { LayoutVariantChoice } from '../layout/LayoutPreview'

export interface SubtitleVariantSelectorProps {
  readonly variantPair: LayoutVariantPair | null
  readonly choice: LayoutVariantChoice
  readonly disabled: boolean
  readonly onChange: (choice: Exclude<LayoutVariantChoice, null>) => void
}

export function SubtitleVariantSelector({
  variantPair,
  choice,
  disabled,
  onChange,
}: SubtitleVariantSelectorProps) {
  return (
    <section className="layout-editor-card" aria-labelledby="subtitle-variant-title">
      <p className="section-kicker">作成済みの動画</p>
      <h3 id="subtitle-variant-title">字幕あり・なしの切り替え</h3>
      {variantPair === null ? (
        <div className="layout-variant-unavailable" role="note">
          <strong>字幕あり・なしの両方の動画はまだありません</strong>
          <p>自動編集が完了すると選べるようになります。</p>
        </div>
      ) : (
        <>
          <fieldset className="layout-variant-options" disabled={disabled}>
            <legend>プレビューで見る動画</legend>
            <label>
              <input
                type="radio"
                name="subtitle-variant"
                checked={choice === 'burnedIn'}
                onChange={() => onChange('burnedIn')}
              />
              <span>
                <strong>字幕焼き込みあり</strong>
              </span>
            </label>
            <label>
              <input
                type="radio"
                name="subtitle-variant"
                checked={choice === 'subtitleFree'}
                onChange={() => onChange('subtitleFree')}
              />
              <span>
                <strong>字幕なし</strong>
                <small>字幕ファイル（SRT など）: {variantPair.subtitleFree.hasSidecar ? 'あり' : 'なし'}</small>
              </span>
            </label>
          </fieldset>
          <p className="layout-card-note">
            この選択はプレビューの表示だけを切り替えます。動画の作り直しや保存は行いません。
          </p>
        </>
      )}
    </section>
  )
}
