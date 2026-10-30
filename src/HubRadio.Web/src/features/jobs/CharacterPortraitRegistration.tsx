import { useId, useState, type FormEvent } from 'react'

/** NFR-BR-004: whether the user confirmed the right to use the image. */
export type PortraitRights = 'confirmed' | 'unconfirmed'

export interface RegisteredPortrait {
  readonly id: string
  readonly name: string
  readonly rights: PortraitRights
  readonly subtitleColor: string
}

export interface PortraitRegistration {
  readonly name: string
  readonly file: File
  readonly rights: PortraitRights
}

const rightsLabel: Record<PortraitRights, string> = { confirmed: '使用権を確認済み', unconfirmed: '使用権は未確認' }

/**
 * FR-137 (6): the user registers a standing picture (立ち絵) of their own for the character dialogue template. The app bundles
 * none. The state of the right to use the image must be chosen explicitly (確認済み / 未確認) and is recorded (NFR-BR-004).
 * The list shows the state and the subtitle color by text as well as by color.
 */
export function CharacterPortraitRegistration({ portraits, onRegister, disabled = false }: Readonly<{
  portraits: readonly RegisteredPortrait[]
  onRegister: (registration: PortraitRegistration) => void
  disabled?: boolean
}>) {
  const group = useId()
  const [name, setName] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [rights, setRights] = useState<PortraitRights | null>(null)
  const ready = name.trim().length > 0 && name.trim().length <= 40 && file !== null && rights !== null

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!ready || file === null || rights === null) return
    onRegister({ name: name.trim(), file, rights })
    setName('')
    setFile(null)
    setRights(null)
  }

  return <fieldset className="character-portrait-registration" disabled={disabled}>
    <legend>立ち絵の登録（キャラクター対話用）</legend>
    <p>アプリは立ち絵を同梱していません。使ってよい自分の画像だけを登録してください。1 つも登録しないと「キャラクター対話」は選べません。</p>
    <form onSubmit={submit}>
      <label>キャラクター名
        <input type="text" value={name} maxLength={40} onChange={event => setName(event.target.value)} />
      </label>
      <label>画像ファイル（PNG・JPEG・WebP）
        <input type="file" accept="image/png,image/jpeg,image/webp" onChange={event => setFile(event.target.files?.[0] ?? null)} />
      </label>
      <fieldset>
        <legend>画像の使用権</legend>
        <label><input type="radio" name={group} checked={rights === 'confirmed'} onChange={() => setRights('confirmed')} />{rightsLabel.confirmed}</label>
        <label><input type="radio" name={group} checked={rights === 'unconfirmed'} onChange={() => setRights('unconfirmed')} />{rightsLabel.unconfirmed}</label>
      </fieldset>
      <button type="submit" disabled={!ready}>登録する</button>
    </form>
    {portraits.length === 0
      ? <p role="status">登録された立ち絵はありません。</p>
      : <ul className="character-portrait-list">
        {portraits.map(portrait => <li key={portrait.id}>
          <span className="character-portrait-name">{portrait.name}</span>
          <span className="character-portrait-rights">{rightsLabel[portrait.rights]}</span>
          <span className="character-portrait-color" style={{ color: portrait.subtitleColor }}>字幕の色 {portrait.subtitleColor}</span>
        </li>)}
      </ul>}
  </fieldset>
}
