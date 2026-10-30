import { useId, useState, type FormEvent } from 'react'
import type { RegisteredPortrait } from './CharacterPortraitRegistration'
import { PORTRAIT_CROP_MAX, PORTRAIT_CROP_MIN_SIDE, type PortraitCropParams } from './portraitProtocol'

/**
 * FR-140: when a registered image holds several characters on one sheet (for example angles side by side), the user gives a pixel
 * range (left, top, width, height) and cuts one character out. The cut-out keeps the rights confirmation of the source image
 * (NFR-BR-004) and the source image is not changed.
 */
export function PortraitCropForm({ portraits, onCrop, disabled = false }: Readonly<{
  portraits: readonly RegisteredPortrait[]
  onCrop: (crop: PortraitCropParams) => void
  disabled?: boolean
}>) {
  const id = useId()
  const [sourceId, setSourceId] = useState('')
  const [name, setName] = useState('')
  const [range, setRange] = useState({ x: '0', y: '0', width: '', height: '' })
  const number = (text: string) => (/^\d{1,5}$/.test(text) ? Number(text) : Number.NaN)
  const x = number(range.x); const y = number(range.y); const width = number(range.width); const height = number(range.height)
  const rangeOk = [x, y, width, height].every(value => Number.isInteger(value) && value >= 0 && value <= PORTRAIT_CROP_MAX) &&
    width >= PORTRAIT_CROP_MIN_SIDE && height >= PORTRAIT_CROP_MIN_SIDE
  const ready = sourceId !== '' && name.trim().length > 0 && name.trim().length <= 40 && rangeOk
  if (portraits.length === 0) return null

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!ready) return
    onCrop({ sourceId, name: name.trim(), x, y, width, height })
    setName('')
  }
  const field = (key: keyof typeof range, label: string) => <label>{label}
    <input type="text" inputMode="numeric" value={range[key]} onChange={event => setRange({ ...range, [key]: event.target.value })} />
  </label>

  return <fieldset className="portrait-crop" disabled={disabled}>
    <legend>立ち絵の切り出し（1 枚に複数のキャラクターがいる画像用）</legend>
    <p>登録済みの画像から範囲（画素）を指定して 1 体を切り出し、新しい立ち絵として登録します。使用権の確認状態は元の画像から引き継ぎ、元の画像は変更しません。</p>
    <form onSubmit={submit}>
      <label htmlFor={id}>切り出す元の画像</label>
      <select id={id} value={sourceId} onChange={event => setSourceId(event.target.value)}>
        <option value="">選んでください</option>
        {portraits.map(portrait => <option key={portrait.id} value={portrait.id}>{portrait.name}</option>)}
      </select>
      {field('x', '範囲の左（画素）')}
      {field('y', '範囲の上（画素）')}
      {field('width', '範囲の幅（画素）')}
      {field('height', '範囲の高さ（画素）')}
      <label>切り出した立ち絵の名前
        <input type="text" value={name} maxLength={40} onChange={event => setName(event.target.value)} />
      </label>
      <button type="submit" disabled={!ready}>切り出して登録</button>
    </form>
  </fieldset>
}
