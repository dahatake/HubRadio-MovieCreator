import { useId } from 'react'
import { orientationLabel, styleTemplateOptions, type StyleTemplateOption } from './styleTemplates'

/**
 * FR-136: lists the production templates (name, purpose, vertical or landscape, a preview) and lets the user pick
 * one or none. Choosing none keeps the default output. The selection is shown by a radio button and by text, not by color alone.
 */
export function StyleTemplatePicker({ selectedId, onSelect, options = styleTemplateOptions, disabled = false, portraitCount = 0 }: Readonly<{
  selectedId: string | null
  onSelect: (id: string | null) => void
  options?: readonly StyleTemplateOption[]
  disabled?: boolean
  /** FR-137 (6): how many standing pictures the user registered; a template that needs them cannot be chosen at 0. */
  portraitCount?: number
}>) {
  const name = useId()
  return <fieldset className="style-template-picker" disabled={disabled}>
    <legend>演出テンプレート（任意）</legend>
    <p className="style-template-hint">選ぶだけで、字幕・強調テロップ・動き・効果音・配置・書体がまとめて適用されます。選ばない場合は、いつもの仕上がりになります。</p>
    {options.length === 0 && <p role="status">利用できる演出テンプレートはありません。</p>}
    <ul className="style-template-list">
      <li>
        <label className="style-template-card">
          <input type="radio" name={name} checked={selectedId === null} onChange={() => onSelect(null)} />
          <span className="style-template-name">テンプレートを使わない</span>
          <span className="style-template-purpose">いつもの既定の仕上がりにします。</span>
        </label>
      </li>
      {options.map(option => {
        const locked = option.requiresPortraits === true && portraitCount === 0
        return <li key={option.id}>
        <label className="style-template-card" data-selected={selectedId === option.id} data-locked={locked}>
          <input type="radio" name={name} checked={selectedId === option.id} disabled={locked} onChange={() => onSelect(option.id)} />
          <span className="style-template-name">{option.name}<span className="style-template-orientation">{orientationLabel(option.orientation)}</span></span>
          <span className="style-template-purpose">{option.purpose}</span>
          <span className="style-template-sample" role="img" aria-label={`見た目の例: ${option.previewDescription}`}
            style={{ color: option.textColor, textShadow: `0 0 2px ${option.outlineColor}, 1px 1px 0 ${option.outlineColor}, -1px -1px 0 ${option.outlineColor}`, borderColor: option.emphasisColor }}>
            {option.sampleText}
          </span>
          <span className="style-template-description">{option.previewDescription}</span>
          {locked && <span className="style-template-locked">立ち絵が 1 つも登録されていないため、まだ選べません。下の「立ち絵の登録」で自分の画像を登録してください。</span>}
        </label>
      </li>})}
    </ul>
    {selectedId !== null && <p role="status">選択中: {options.find(option => option.id === selectedId)?.name ?? '不明なテンプレート'}</p>}
  </fieldset>
}
