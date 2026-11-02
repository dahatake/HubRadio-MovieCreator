import { useEffect, useId, useRef, useState } from 'react'
import {
  createHostDisclosureService,
  DISCLOSURE_MAX_DESTINATION_LENGTH,
  DISCLOSURE_MAX_LABEL_LENGTH,
  type DisclosureElementKind,
  type DisclosureOutcome,
  type DisclosureResult,
  type DisclosureService,
} from './disclosureProtocol'

/** NFR-BR-005: what AI did, in words (never a colour or an ID). */
export const disclosureKindLabels: Readonly<Record<DisclosureElementKind, string>> = {
  SpeakerEstimate: '話者の自動推定',
  AutoSubtitle: '自動字幕',
  TopicSummaryDraft: '話題の要約の下書き',
  SubstantialEdit: 'AI による大幅な編集',
}

export const disclosureMessages = {
  forbidden: 'この操作の権限がないため、ラベルを追加しませんでした。',
  unavailable: 'AI生成表示を利用できません。',
  unavailableEnvironment: 'この環境では利用できません。',
  invalid: 'ラベルの内容を確認できません。公開先とラベルの文字を確かめてください。',
  timeout: '応答がありません。もう一度お試しください。',
  emptyDestination: '公開先を入力してください。',
  emptyLabel: 'ラベルの文字を入力してください。',
  added: 'ラベルを追加しました。納品物の ai-disclosure.txt に反映されます。',
  noElements: 'AI で生成または大幅に編集した要素は記録されていません。',
  noLabels: '公開先のラベルはまだ追加されていません。',
} as const

export function describeDisclosureFailure(outcome: DisclosureOutcome): string | null {
  switch (outcome.status) {
    case 'listed':
    case 'added': return null
    case 'forbidden': return disclosureMessages.forbidden
    case 'invalid': return disclosureMessages.invalid
    case 'timeout': return disclosureMessages.timeout
    default: return disclosureMessages.unavailable
  }
}

export interface DisclosurePanelProps {
  readonly projectId: string | null
  /** undefined: use the WebView2 host bridge; null: unavailable. */
  readonly service?: DisclosureService | null
}

/**
 * NFR-BR-005 AC-126: lists the AI-generated or substantially edited elements and the labels per publication
 * destination, and adds a label. The labels travel with the delivered files as ai-disclosure.txt.
 */
export function DisclosurePanel({ projectId, service: injected }: DisclosurePanelProps) {
  const id = useId()
  const [service, setService] = useState<DisclosureService | null>(injected ?? null)
  const [result, setResult] = useState<DisclosureResult | null>(null)
  const [destination, setDestination] = useState('')
  const [labelText, setLabelText] = useState('')
  const [pending, setPending] = useState(false)
  const [message, setMessage] = useState('')
  const mounted = useRef(true)

  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  useEffect(() => {
    if (injected !== undefined) { setService(injected); return undefined }
    const host = createHostDisclosureService()
    setService(host)
    return () => { host?.dispose() }
  }, [injected])

  useEffect(() => {
    if (service === null || projectId === null) { setResult(null); return }
    let current = true
    void service.list(projectId).then(outcome => {
      if (!current || !mounted.current) return
      const failure = describeDisclosureFailure(outcome)
      if (failure === null) setResult(outcome as DisclosureResult)
      else setMessage(failure)
    }).catch(() => { if (current && mounted.current) setMessage(disclosureMessages.unavailable) })
    return () => { current = false }
  }, [service, projectId])

  async function add(): Promise<void> {
    if (service === null || projectId === null || pending) return
    const target = destination.trim()
    const text = labelText.trim()
    if (target.length === 0) { setMessage(disclosureMessages.emptyDestination); return }
    if (text.length === 0) { setMessage(disclosureMessages.emptyLabel); return }
    setPending(true)
    setMessage('')
    let outcome: DisclosureOutcome
    try { outcome = await service.addLabel(projectId, target, text) }
    catch { outcome = { status: 'disconnected' } }
    if (!mounted.current) return
    const failure = describeDisclosureFailure(outcome)
    if (failure === null) {
      setResult(outcome as DisclosureResult)
      setLabelText('')
      setMessage(disclosureMessages.added)
    } else setMessage(failure)
    setPending(false)
  }

  const available = service !== null && projectId !== null
  const headingId = `${id}-title`
  const statusId = `${id}-status`

  return (
    <section className="job-export ai-disclosure" aria-labelledby={headingId} aria-busy={pending} data-ai-disclosure="">
      <h3 id={headingId}>AI生成の表示</h3>
      <p className="result-gallery-note">
        AI で生成または大幅に編集した要素の一覧と、公開先ごとのラベルです。追加したラベルは、納品するファイルの ai-disclosure.txt に入ります。
      </p>
      {!available && <p>{disclosureMessages.unavailableEnvironment}</p>}
      {result !== null && (
        <>
          {result.elements.length === 0
            ? <p>{disclosureMessages.noElements}</p>
            : (
              <ul aria-label="AIで生成した要素">
                {result.elements.map((element, index) => (
                  <li key={`${index}:${element.elementRef}`}>
                    {disclosureKindLabels[element.kind]}（{element.elementRef} / {element.modelIdentifier} {element.modelVersion}）
                  </li>
                ))}
              </ul>
            )}
          {result.labels.length === 0
            ? <p>{disclosureMessages.noLabels}</p>
            : (
              <ul aria-label="公開先のラベル">
                {result.labels.map((label, index) => <li key={`${index}:${label.destination}`}>{label.destination}: {label.labelText}</li>)}
              </ul>
            )}
        </>
      )}
      <div className="job-export-field">
        <label htmlFor={`${id}-destination`}>公開先</label>
        <input id={`${id}-destination`} type="text" value={destination} maxLength={DISCLOSURE_MAX_DESTINATION_LENGTH}
          spellCheck={false} autoComplete="off" disabled={!available || pending} aria-describedby={statusId}
          onChange={event => setDestination(event.target.value)} />
        <label htmlFor={`${id}-label`}>ラベルの文字</label>
        <div className="job-export-input">
          <input id={`${id}-label`} type="text" value={labelText} maxLength={DISCLOSURE_MAX_LABEL_LENGTH}
            autoComplete="off" disabled={!available || pending} aria-describedby={statusId}
            onChange={event => setLabelText(event.target.value)}
            onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); void add() } }} />
          <button type="button" disabled={!available || pending} onClick={() => { void add() }}>ラベルを追加</button>
        </div>
      </div>
      <div id={statusId} role="status" aria-live="polite" aria-atomic="true" data-ai-disclosure-message="">
        {pending && <p>処理しています…</p>}
        {message !== '' && <p>{message}</p>}
      </div>
    </section>
  )
}
