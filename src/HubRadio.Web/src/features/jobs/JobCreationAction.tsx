import { useEffect, useLayoutEffect, useId, useRef, useState } from 'react'
import type { PipelineRegenerationExtras, PipelineRegenerationParams } from '../workflow/pipelineProtocol'
import { StyleTemplatePicker } from './StyleTemplatePicker'
import { TalkShowInputPanel } from './TalkShowInputPanel'
import { emptyTalkShowInput, type TalkShowInputValue } from './talkShowInput'
import { CharacterPortraitRegistration, type PortraitRegistration, type RegisteredPortrait } from './CharacterPortraitRegistration'
import { createHostPortraitService, portraitFileToParams, type PortraitCropParams, type PortraitOutcome, type PortraitService } from './portraitProtocol'
import { PortraitCropForm } from './PortraitCropForm'
import { SpeakerAssignmentPanel } from './SpeakerAssignmentPanel'
import { createHostSpeakerService, type SpeakerService } from './speakerProtocol'
import { createHostTemplateAutoService, type TemplateAutoResult, type TemplateAutoService } from './templateAutoProtocol'
import { styleTemplateOptions } from './styleTemplates'

const portraitMessages = Object.freeze({
  registered: '立ち絵を登録しました。使用権の確認状態も保存しました。',
  invalid: '立ち絵を登録できませんでした。同じ名前がないか、PNG・JPEG・WebP の 4 MiB 以下の画像かを確認してください。',
  forbidden: '立ち絵を登録する権限がありません。',
  unavailable: '立ち絵の登録を利用できません。',
  tooLarge: '画像は PNG・JPEG・WebP の 4 MiB 以下にしてください。',
  cropped: '立ち絵を切り出して登録しました。使用権の確認状態は元の画像から引き継ぎました。',
  cropInvalid: '切り出せませんでした。範囲が画像の内側か、名前が重複していないかを確認してください。',
})

function toRegistered(outcome: PortraitOutcome): readonly RegisteredPortrait[] | null {
  return 'portraits' in outcome && outcome.status === 'ok' ? outcome.portraits : null
}

/** FR-061: creates a new Job from the saved "next Job" settings after an explicit confirmation. */
export function JobCreationAction({ nextJobNumber, params, disabledReason, pending, active, onOpenSettings, openSettingsDisabled = false, onConfirm, styleTemplateId = null, onStyleTemplateChange, portraitService: injectedPortraits, speakerService: injectedSpeakers, templateAutoService: injectedAuto, projectId = null }: Readonly<{
  nextJobNumber: number | null
  params: PipelineRegenerationParams | null
  disabledReason: string | null
  pending: boolean
  active: boolean
  onOpenSettings: () => void
  openSettingsDisabled?: boolean
  onConfirm: (params: PipelineRegenerationParams, styleTemplateId?: string | null, extras?: PipelineRegenerationExtras) => boolean
  /** FR-136: the selected production template (null = none). The picker is shown only when a change handler is given. */
  styleTemplateId?: string | null
  onStyleTemplateChange?: (id: string | null) => void
  /** FR-137 (6): the standing-picture registration service (the WebView2 host one is used when omitted; null hides the form). */
  portraitService?: PortraitService | null
  /** FR-140: the speaker-assignment service (the WebView2 host one is used when omitted; null hides it) and the Project it applies to. */
  speakerService?: SpeakerService | null
  /** FR-145: the automatic template selection service (the WebView2 host one is used when omitted; null turns the automatic selection off). */
  templateAutoService?: TemplateAutoService | null
  projectId?: string | null
}>) {
  const headingId = useId()
  const [templateAutoService] = useState<TemplateAutoService | null>(() => injectedAuto === undefined ? createHostTemplateAutoService() : injectedAuto)
  const [auto, setAuto] = useState<TemplateAutoResult | null>(null)
  const manualChoice = useRef(false)
  const [manualShown, setManualShown] = useState(false)
  const changeTemplate = useRef(onStyleTemplateChange)
  changeTemplate.current = onStyleTemplateChange
  // FR-145: reads the automatic selection of the Project without making the screen wait. While it analyzes, the screen stays usable;
  // when it selects a template (and the user has not chosen one themself) that template becomes the initial selection.
  useEffect(() => {
    manualChoice.current = false
    setManualShown(false)
    setAuto(null)
    if (templateAutoService === null || projectId === null || changeTemplate.current === undefined) return
    let alive = true
    let timer: ReturnType<typeof setTimeout> | undefined
    let attempts = 0
    const poll = async () => {
      const outcome = await templateAutoService.get(projectId)
      if (!alive) return
      if ('state' in outcome) {
        setAuto(outcome)
        if (outcome.state === 'selected' && outcome.templateId !== null && !manualChoice.current) changeTemplate.current?.(outcome.templateId)
        if ((outcome.state === 'waiting' || outcome.state === 'analyzing') && attempts++ < 90) timer = setTimeout(() => { void poll() }, 1000)
      } else if (attempts++ < 3) timer = setTimeout(() => { void poll() }, 2000)
    }
    void poll()
    return () => { alive = false; if (timer !== undefined) clearTimeout(timer) }
  }, [templateAutoService, projectId])
  // FR-145: a template the user chooses themself cancels an automatic selection that is still running and is never overwritten by it.
  function chooseTemplate(id: string | null) {
    manualChoice.current = true
    setManualShown(true)
    if (templateAutoService !== null && projectId !== null) {
      void templateAutoService.touch(projectId).then(outcome => { if ('state' in outcome) setAuto(outcome) })
    }
    onStyleTemplateChange?.(id)
  }
  const autoName = auto?.templateId == null ? null : styleTemplateOptions.find(option => option.id === auto.templateId)?.name ?? null
  const autoNote = auto === null ? null
    : auto.state === 'waiting' || auto.state === 'analyzing' ? '元動画を解析して、最適な演出テンプレートを選んでいます。解析の完了を待たずに、他の操作やテンプレートの選択ができます。'
    : auto.state === 'selected' && autoName !== null ? `自動選択: ${autoName}。理由: ${auto.reason}${manualShown ? '（今は手動で選んだテンプレートを使います）' : '（別のテンプレートや「テンプレートを使わない」にいつでも変えられます）'}`
    : auto.state === 'cancelled' || auto.state === 'skipped' ? auto.reason
    : null
  const [speakerService] = useState<SpeakerService | null>(() => injectedSpeakers === undefined ? createHostSpeakerService() : injectedSpeakers)
  const [confirmation, setConfirmation] = useState<PipelineRegenerationParams | null>(null)
  const [rejected, setRejected] = useState(false)
  const [portraitService] = useState<PortraitService | null>(() => injectedPortraits === undefined ? createHostPortraitService() : injectedPortraits)
  const [portraits, setPortraits] = useState<readonly RegisteredPortrait[]>([])
  const [portraitNote, setPortraitNote] = useState<string | null>(null)
  const [portraitBusy, setPortraitBusy] = useState(false)
  const [talkShow, setTalkShow] = useState<TalkShowInputValue>(emptyTalkShowInput)
  const [subtitleIntoMargin, setSubtitleIntoMargin] = useState(false)
  const verticalTemplate = styleTemplateId === 'vertical-3-tier' || styleTemplateId === 'short-pop'
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
  async function cropPortrait(crop: PortraitCropParams) {
    if (portraitService === null) return
    setPortraitBusy(true)
    setPortraitNote(null)
    try {
      const outcome = await portraitService.crop(crop)
      const listed = toRegistered(outcome)
      if (listed !== null) { setPortraits(listed); setPortraitNote(portraitMessages.cropped) }
      else setPortraitNote(outcome.status === 'forbidden' ? portraitMessages.forbidden : outcome.status === 'invalid' ? portraitMessages.cropInvalid : portraitMessages.unavailable)
    } finally { setPortraitBusy(false) }
  }
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
    {onStyleTemplateChange !== undefined && <StyleTemplatePicker selectedId={styleTemplateId} onSelect={chooseTemplate} disabled={!active || pending} portraitCount={portraits.length} />}
    {onStyleTemplateChange !== undefined && autoNote !== null && <p role="status" className="style-template-auto">{autoNote}</p>}
    {onStyleTemplateChange !== undefined && styleTemplateId === 'talk-show' && <TalkShowInputPanel value={talkShow} onChange={setTalkShow} disabled={!active || pending} />}
    {onStyleTemplateChange !== undefined && portraitService !== null && <>
      <CharacterPortraitRegistration portraits={portraits} onRegister={registration => { void registerPortrait(registration) }} disabled={!active || pending || portraitBusy} />
      {portraitNote !== null && <p role="status">{portraitNote}</p>}
      <PortraitCropForm portraits={portraits} onCrop={crop => { void cropPortrait(crop) }} disabled={!active || pending || portraitBusy} />
    </>}
    {onStyleTemplateChange !== undefined && speakerService !== null && styleTemplateId === 'character-dialogue' &&
      <SpeakerAssignmentPanel projectId={projectId} service={speakerService} portraitCount={portraits.length} disabled={!active || pending} />}
    {onStyleTemplateChange !== undefined && verticalTemplate && <div>
      <button type="button" aria-pressed={subtitleIntoMargin} disabled={!active || pending} onClick={() => setSubtitleIntoMargin(value => !value)}>字幕位置を余白内に動かす</button>
      <p role="status">{subtitleIntoMargin
        ? '字幕を上下左右の余白の内側（置かない領域）へ動かして作成します。出力検査は不合格になります。'
        : '字幕は余白と字幕領域の内側に置きます（既定）。'}</p>
    </div>}
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
        const entered = styleTemplateId === 'talk-show' && (talkShow.topics.some(item => item.text.trim() !== '') || talkShow.speakers.some(item => item.name.trim() !== ''))
        const extras: PipelineRegenerationExtras = styleTemplateId === 'talk-show' ? (entered ? { talkShowInput: JSON.stringify(talkShow) } : {}) : verticalTemplate && subtitleIntoMargin ? { subtitleIntoMargin: true } : {}
        if (Object.keys(extras).length === 0 ? onConfirm(confirmation, styleTemplateId) : onConfirm(confirmation, styleTemplateId, extras)) setConfirmation(null)
        else { sending.current = false; setRejected(true) }
      }}>作成する</button>
      <button ref={cancel} type="button" onClick={close}>取消</button>
    </section>}
  </section>
}
