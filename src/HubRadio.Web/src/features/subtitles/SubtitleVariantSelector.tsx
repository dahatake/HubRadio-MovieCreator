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
      <p className="section-kicker">Existing delivery evidence only</p>
      <h3 id="subtitle-variant-title">字幕焼き込み variant</h3>
      {variantPair === null ? (
        <div className="layout-variant-unavailable" role="note">
          <strong>対応する variant pair はありません</strong>
          <p>画面側で manifest、render graph、sidecar の有無を推測しません。</p>
        </div>
      ) : (
        <>
          <fieldset className="layout-variant-options" disabled={disabled}>
            <legend>preview で照合する既存 evidence</legend>
            <label>
              <input
                type="radio"
                name="subtitle-variant"
                checked={choice === 'burnedIn'}
                onChange={() => onChange('burnedIn')}
              />
              <span>
                <strong>字幕焼き込みあり</strong>
                <code>{variantPair.burnedIn.deliveryManifestId}</code>
                <code>{variantPair.burnedIn.renderGraphId}</code>
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
                <code>{variantPair.subtitleFree.deliveryManifestId}</code>
                <code>{variantPair.subtitleFree.renderGraphId}</code>
                <small>Sidecar: {variantPair.subtitleFree.hasSidecar ? 'あり' : 'なし'}</small>
              </span>
            </label>
          </fieldset>
          <dl className="layout-variant-evidence">
            <div><dt>Composition digest</dt><dd><code>{variantPair.compositionVersionDigest}</code></dd></div>
            <div><dt>Display profile digest</dt><dd><code>{variantPair.displayProfileDigest}</code></dd></div>
            <div><dt>Subtitle evidence digest</dt><dd><code>{variantPair.subtitleEvidenceDigest}</code></dd></div>
          </dl>
          <p className="layout-card-note">
            この選択は preview 表示だけの session-local 状態です。render、保存、承認、variant 生成は実行しません。
          </p>
        </>
      )}
    </section>
  )
}
