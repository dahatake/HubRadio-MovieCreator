import { useEffect, useLayoutEffect, useId, useRef, useState } from 'react'
import type { PipelineRegenerationParams } from '../workflow/pipelineProtocol'
import { StyleTemplatePicker } from './StyleTemplatePicker'
import { TalkShowInputPanel } from './TalkShowInputPanel'
import { emptyTalkShowInput, type TalkShowInputValue } from './talkShowInput'
import { CharacterPortraitRegistration, type PortraitRegistration, type RegisteredPortrait } from './CharacterPortraitRegistration'
import { createHostPortraitService, portraitFileToParams, type PortraitOutcome, type PortraitService } from './portraitProtocol'

const portraitMessages = Object.freeze({
  registered: '立ち絵を登録しました。使用権の確認状態も保存しました。',
  invalid: '立ち絵を登録できませんでした。同じ名前がないか、PNG・JPEG・WebP の 4 MiB 以下の画像かを確認してください。',
  forbidden: '立ち絵を登録する権限がありません。',
  unavailable: '立ち絵の登録を利用できません。',
  tooLarge: '画像は PNG・JPEG・WebP の 4 MiB 以下にしてください。',
})

function toRegistered(outcome: PortraitOutcome): readonly RegisteredPortrait[] | null {
  return 'portraits' in outcome && outcome.status === 'ok' ? outcome.portraits : null
}

/** FR-061: creates a new Job from the saved "next Job" settings after an explicit confirmation. */
export function JobCreationAction({ nextJobNumber, params, disabledReason, pending, active, onOpenSettings, openSettingsDisabled = false, onConfirm, styleTemplateId = null, onStyleTemplateChange, portraitService: injectedPortraits }: Readonly<{
  nextJobNumber: number | null
  params: PipelineRegenerationParams | null
  disabledReason: string | null
  pending: boolean
  active: boolean
  onOpenSettings: () => void
  openSettingsDisabled?: boolean
  onConfirm: (params: PipelineRegenerationParams, styleTemplateId?: string | null) => boolean
  /** FR-136: the selected production template (null = none). The picker is shown only when a change handler is given. */
  styleTemplateId?: string | null
  onStyleTemplateChange?: (id: string | null) => void
  /** FR-137 (6): the standing-picture registration service (the WebView2 host one is used when omitted; null hides the form). */
  portraitService?: PortraitService | null
}>) {
  const headingId = useId()
  const [confirmation, setConfirmation] = useState<PipelineRegenerationParams | null>(null)
  const [rejected, setRejected] = useState(false)
  const [portraitService] = useState<PortraitService | null>(() => injectedPortraits === undefined ? createHostPortraitService() : injectedPortraits)
  const [portraits, setPortraits] = useState<readonly RegisteredPortrait[]>([])
  const [portraitNote, setPortraitNote] = useState<string | null>(null)
  const [portraitBusy, setPortraitBusy] = useState(false)
  const [talkShow, setTalkShow] = useState<TalkShowInputValue>(emptyTalkShowInput)
  useEffect(() => {
    if (portraitService === null || onStyleTemplateChange === undefined) return
    let alive = true
    void portraitService.list().then(outcome => {
      const listed = toRegistered(outcome)
      if (alive && listed !== null) setPortraits(listed)
    })
    return () => { alive = false }
  }, [portraitService, onStyleTemplateChange])
  async function registerPortrait(registration: PortraitRegistration) {
    if (portraitService === null) return
    setPortraitBusy(true)
    setPortraitNote(null)
    try {
      const request = await portraitFileToParams(registration.file, registration.name, registration.rights)
      if (request === null) { setPortraitNote(portraitMessages.tooLarge); return }
      const outcome = await portraitService.register(request)
      const listed = toRegistered(outcome)
      if (listed !== null) { setPortraits(listed); setPortraitNote(portraitMessages.registered) }
      else setPortraitNote(outcome.status === 'forbidden' ? portraitMessages.forbidden : outcome.status === 'invalid' ? portraitMessages.invalid : portraitMessages.unavailable)
    } finally { setPortraitBusy(false) }
  }
  const cancel = useRef<HTMLButtonElement>(null)
  const opener = useRef<HTMLButtonElement>(null)
  const sending = useRef(false)
  const restoreFocus = useRef(false)
  const available = active && params !== null && disabledReason === null && !pending
  const same = confirmation !== null && params !== null && JSON.stringify(confirmation) === JSON.stringify(params)
  useLayoutEffect(() => {
    if (!active || opener.current?.closest('[hidden], [inert]')) return
    if (confirmation !== null) cancel.current?.focus()
    else if (restoreFocus.current) { restoreFocus.current = false; opener.current?.focus() }
  }, [confirmation, active])
  function close() { restoreFocus.current = true; setConfirmation(null); setRejected(false); sending.current = false }
  const target = nextJobNumber === null ? '新しいJob' : `Job ${nextJobNumber}`
  return <section aria-labelledby={headingId}>
    <h2 id={headingId}>新しいJobを作成</h2>
    <p>1つの元動画から、設定を変えた複数のJobを作れます。「次に作成するJobの設定を開く」で設定を変えて保存し、「新しいJobを作成」を押してください。今までのJobと仕上がりは消えません。Jobは1つずつ順番に実行します。</p>
    <button type="button" disabled={!active || openSettingsDisabled} onClick={onOpenSettings}>次に作成するJobの設定を開く</button>
    {onStyleTemplateChange !== undefined && <StyleTemplatePicker selectedId={styleTemplateId} onSelect={onStyleTemplateChange} disabled={!active || pending} portraitCount={portraits.length} />}
    {onStyleTemplateChange !== undefined && styleTemplateId === 'talk-show' && <TalkShowInputPanel value={talkShow} onChange={setTalkShow} disabled={!active || pending} />}
    {onStyleTemplateChange !== undefined && portraitService !== null && <>
      <CharacterPortraitRegistration portraits={portraits} onRegister={registration => { void registerPortrait(registration) }} disabled={!active || pending || portraitBusy} />
      {portraitNote !== null && <p role="status">{portraitNote}</p>}
    </>}
    <button ref={opener} type="button" disabled={!available || confirmation !== null} onClick={() => {
      if (available) { sending.current = false; setRejected(false); setConfirmation(Object.freeze({ ...params })) }
    }}>新しいJobを作成</button>
    {disabledReason !== null && <p role="status">{disabledReason}</p>}
    {pending && <p role="status">Jobの作成の受付を確認しています。</p>}
    {confirmation !== null && <section role="alertdialog" aria-label="Jobの作成の確認" onKeyDown={event => {
      if (event.key === 'Escape') { event.preventDefault(); close() }
    }}>
      <p>保存済みの「次に作成するJob」の設定で、{target}を作成しますか。今までのJobと仕上がりは残ります。</p>
      {(!same || !available || rejected) && <p role="status">確認中に設定または状態が変わりました。取り消して状態を確認してください。</p>}
      <button type="button" disabled={!available || !same || rejected} onClick={() => {
        if (!available || !same || sending.current) return
        sending.current = true
        if (onConfirm(confirmation, styleTemplateId)) setConfirmation(null)
        else { sending.current = false; setRejected(true) }
      }}>作成する</button>
      <button ref={cancel} type="button" onClick={close}>取消</button>
    </section>}
  </section>
}
