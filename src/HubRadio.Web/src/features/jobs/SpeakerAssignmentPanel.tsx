import { useEffect, useId, useState } from 'react'
import type { SpeakerOutcome, SpeakerResult, SpeakerService } from './speakerProtocol'

const stateText: Readonly<Record<SpeakerResult['state'], string>> = Object.freeze({
  none: '話者は未割り当てです。未割り当ての字幕には立ち絵を出さず、既定の字幕色を使います。',
  draft: '下書き（未確認）です。確認して確定するまで、成果物には反映しません。',
  confirmed: '確定済みです。確定した割り当てだけが成果物に反映されます。',
})

const failureText = Object.freeze({
  forbidden: '話者を割り当てる権限がありません。',
  invalid: '話者を割り当てられませんでした。立ち絵を登録してから、もう一度お試しください。',
  unavailable: '話者の割り当てを利用できません。',
  timeout: '話者の割り当ての応答がありませんでした。',
  disconnected: '話者の割り当てを利用できません。',
})

function resultOf(outcome: SpeakerOutcome): SpeakerResult | null {
  return 'state' in outcome && outcome.status === 'ok' ? outcome : null
}

/**
 * FR-140: the user assigns a speaker (a registered character) to the subtitle cues of the character dialogue template. The system's
 * estimate is only a draft ("下書き（未確認）") until the user confirms it ("確認して確定"); only a confirmed assignment reaches the
 * output. The state is shown by text, not by color alone.
 */
export function SpeakerAssignmentPanel({ projectId, service, portraitCount, disabled = false }: Readonly<{
  projectId: string | null
  service: SpeakerService | null
  portraitCount: number
  disabled?: boolean
}>) {
  const statusId = useId()
  const [result, setResult] = useState<SpeakerResult | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    if (service === null || projectId === null) return
    let alive = true
    void service.get(projectId).then(outcome => {
      const read = resultOf(outcome)
      if (alive && read !== null) setResult(read)
    })
    return () => { alive = false }
  }, [service, projectId])
  if (service === null || projectId === null) return null

  async function run(action: () => Promise<SpeakerOutcome>) {
    setBusy(true)
    setNote(null)
    try {
      const outcome = await action()
      const read = resultOf(outcome)
      if (read !== null) setResult(read)
      else setNote(failureText[outcome.status === 'ok' ? 'invalid' : outcome.status])
    } finally { setBusy(false) }
  }

  const state = result?.state ?? 'none'
  const blocked = disabled || busy
  return <fieldset className="speaker-assignment" disabled={disabled}>
    <legend>字幕の話者（キャラクター対話用）</legend>
    <p id={statusId} role="status">{stateText[state]}</p>
    <p>状態: <span className="speaker-assignment-state" data-state={state}>{result?.label ?? '未割り当て'}</span></p>
    <button type="button" disabled={blocked || portraitCount === 0}
      onClick={() => { void run(() => service.estimate(projectId)) }}>話者を割り当てる</button>
    <button type="button" aria-describedby={statusId} disabled={blocked || state !== 'draft'}
      onClick={() => { void run(() => service.confirm(projectId)) }}>確認して確定</button>
    {note !== null && <p role="alert">{note}</p>}
  </fieldset>
}
