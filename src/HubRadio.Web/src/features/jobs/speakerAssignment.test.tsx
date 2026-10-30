// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SpeakerAssignmentPanel } from './SpeakerAssignmentPanel'
import { PortraitCropForm } from './PortraitCropForm'
import { StyleTemplatePicker } from './StyleTemplatePicker'
import {
  createSpeakerAssignRequest, createSpeakerConfirmRequest, createSpeakerGetRequest, createSpeakerService, parseSpeakerResponse,
  type SpeakerOutcome, type SpeakerRequest, type SpeakerResult, type SpeakerService, type SpeakerTransport,
} from './speakerProtocol'
import { createPortraitCropRequest, createPortraitService, type PortraitRequest, type PortraitTransport } from './portraitProtocol'

const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const project = id(9)
const speaker = 'char-0123456789ab'
const result = (over: Partial<SpeakerResult> = {}): SpeakerResult => ({ status: 'ok', state: 'none', label: '未割り当て', estimated: false, assignments: [], ...over })
const envelope = (r: unknown, method = 'speakers.get', requestId = id(1)) => ({ version: 1, requestId, method, result: r })

function fakeTransport() {
  const sent: SpeakerRequest[] = []
  let listener: ((event: { readonly data: unknown }) => void) | null = null
  const transport: SpeakerTransport = {
    postMessage: message => { sent.push(message) },
    addEventListener: (_t, l) => { listener = l },
    removeEventListener: () => { listener = null },
  }
  return { transport, sent, deliver: (data: unknown) => listener?.({ data }) }
}

// FR-140 AC-164 AC-166: the speaker-assignment bridge (speakers.get / speakers.assign / speakers.confirm).
describe('speakerProtocol', () => {
  it('builds closed requests and refuses invalid ones', () => {
    expect(createSpeakerGetRequest(id(1), project).params).toEqual({ projectId: project })
    expect(createSpeakerConfirmRequest(id(1), project).method).toBe('speakers.confirm')
    expect(createSpeakerAssignRequest(id(1), project, null, null).params).toEqual({ projectId: project, cueIndex: null, speakerId: null })
    expect(createSpeakerAssignRequest(id(1), project, 3, speaker).params).toEqual({ projectId: project, cueIndex: 3, speakerId: speaker })
    expect(() => createSpeakerAssignRequest(id(1), project, null, speaker)).toThrow(TypeError)
    expect(() => createSpeakerAssignRequest(id(1), project, -1, null)).toThrow(TypeError)
    expect(() => createSpeakerAssignRequest(id(1), project, 10_000, null)).toThrow(TypeError)
    expect(() => createSpeakerAssignRequest(id(1), project, 1.5, null)).toThrow(TypeError)
    expect(() => createSpeakerAssignRequest(id(1), project, 1, '../x')).toThrow(TypeError)
    expect(() => createSpeakerGetRequest('nope', project)).toThrow(TypeError)
    expect(() => createSpeakerGetRequest(id(1), 'nope')).toThrow(TypeError)
  })

  it('parses only the closed result and never mistakes a draft for a confirmed assignment', () => {
    const draft = parseSpeakerResponse(envelope(result({ state: 'draft', label: '下書き（未確認）', estimated: true, assignments: [{ cueIndex: 0, speakerId: speaker }] })))
    expect(draft?.result).toMatchObject({ state: 'draft', label: '下書き（未確認）' })
    expect(parseSpeakerResponse(envelope(result(), 'results.export'))).toBeNull()
    expect(parseSpeakerResponse(envelope({ ...result(), extra: 1 }))).toBeNull()
    expect(parseSpeakerResponse(envelope(result({ state: 'maybe' as never })))).toBeNull()
    expect(parseSpeakerResponse(envelope(result({ assignments: [{ cueIndex: -1, speakerId: speaker }] })))).toBeNull()
    expect(parseSpeakerResponse(envelope(result({ assignments: [{ cueIndex: 0, speakerId: 'C:\\x' }] })))).toBeNull()
  })

  it('sends each request and resolves with its response; a closed or silent host reports disconnected / timeout', async () => {
    const h = fakeTransport()
    const service = createSpeakerService(h.transport, { createRequestId: () => id(1) })
    const estimate = service.estimate(project)
    expect(h.sent[0]).toMatchObject({ method: 'speakers.assign', params: { cueIndex: null, speakerId: null } })
    h.deliver(envelope(result({ state: 'draft', estimated: true }), 'speakers.assign'))
    expect(await estimate).toMatchObject({ status: 'ok', state: 'draft' })
    const confirm = service.confirm(project)
    h.deliver(envelope(result({ state: 'confirmed', label: '確定済み' }), 'speakers.confirm'))
    expect(await confirm).toMatchObject({ state: 'confirmed' })
    service.dispose()
    expect(await service.get(project)).toEqual({ status: 'disconnected' })
    const slow = createSpeakerService(fakeTransport().transport, { createRequestId: () => id(3), timeoutMilliseconds: 20 })
    expect(await slow.get(project)).toEqual({ status: 'timeout' })
  })
})

// FR-140 AC-167: the crop request of the portrait bridge.
describe('portraits.crop', () => {
  const crop = { sourceId: speaker, name: '左の子', x: 0, y: 0, width: 20, height: 20 }
  it('builds a closed request and refuses a range the host would refuse', () => {
    expect(createPortraitCropRequest(id(1), crop)).toMatchObject({ method: 'portraits.crop', params: crop })
    expect(() => createPortraitCropRequest(id(1), { ...crop, width: 7 })).toThrow(TypeError)
    expect(() => createPortraitCropRequest(id(1), { ...crop, x: -1 })).toThrow(TypeError)
    expect(() => createPortraitCropRequest(id(1), { ...crop, y: 1.5 })).toThrow(TypeError)
    expect(() => createPortraitCropRequest(id(1), { ...crop, sourceId: 'C:\\a.png' })).toThrow(TypeError)
    expect(() => createPortraitCropRequest(id(1), { ...crop, name: ' ' })).toThrow(TypeError)
  })

  it('sends the crop and resolves with the portrait list', async () => {
    const sent: PortraitRequest[] = []
    const held: { listener: ((event: { readonly data: unknown }) => void) | null } = { listener: null }
    const transport: PortraitTransport = {
      postMessage: m => { sent.push(m) }, addEventListener: (_t, l) => { held.listener = l }, removeEventListener: () => { held.listener = null },
    }
    const service = createPortraitService(transport, { createRequestId: () => id(1) })
    const pending = service.crop(crop)
    expect(sent[0]).toMatchObject({ method: 'portraits.crop' })
    const entry = { id: speaker, name: '左の子', rights: 'unconfirmed', subtitleColor: '#FFD400', rightsNote: null }
    held.listener?.({ data: { version: 1, requestId: id(1), method: 'portraits.crop', result: { status: 'ok', portraitCount: 1, portraits: [entry] } } })
    expect(await pending).toMatchObject({ status: 'ok', portraitCount: 1 })
    service.dispose()
  })
})

// FR-136 / FR-140: each template radio is named by the template name alone, and 演出テンプレート is a control that never changes the choice.
describe('StyleTemplatePicker accessible names', () => {
  it('names every template radio by the template name and keeps the choice when 演出テンプレート is pressed', () => {
    const container = document.createElement('div'); document.body.append(container)
    const root = createRoot(container)
    const onSelect = vi.fn()
    act(() => root.render(<StyleTemplatePicker selectedId="explainer" onSelect={onSelect} portraitCount={1} />))
    const names = [...container.querySelectorAll('input[type="radio"]')].map(radio => radio.getAttribute('aria-label'))
    expect(names).toContain('キャラクター対話')
    expect(names).toContain('ショート飛び出し')
    const jump = [...container.querySelectorAll('button')].find(item => item.textContent === '演出テンプレート') as HTMLButtonElement
    act(() => { jump.click() })
    expect(onSelect).not.toHaveBeenCalled()
    act(() => root.unmount()); container.remove()
  })
})

describe('SpeakerAssignmentPanel', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })
  const flush = () => act(async () => { await Promise.resolve(); await Promise.resolve() })
  const button = (name: string) => [...container.querySelectorAll('button')].find(item => item.textContent === name) as HTMLButtonElement

  function service(initial: SpeakerResult) {
    const calls: string[] = []
    const fake: SpeakerService = {
      get: vi.fn(async () => initial as SpeakerOutcome),
      estimate: vi.fn(async () => { calls.push('estimate'); return result({ state: 'draft', label: '下書き（未確認）', estimated: true }) as SpeakerOutcome }),
      assign: vi.fn(async () => result({ state: 'draft', label: '下書き（未確認）' }) as SpeakerOutcome),
      confirm: vi.fn(async () => { calls.push('confirm'); return result({ state: 'confirmed', label: '確定済み' }) as SpeakerOutcome }),
      dispose: vi.fn(),
    }
    return { fake, calls }
  }

  // AC-166: the estimate is shown as a draft, and only the user's "確認して確定" makes it confirmed.
  it('shows the estimate as a draft until the user confirms it', async () => {
    const { fake, calls } = service(result())
    act(() => root.render(<SpeakerAssignmentPanel projectId={project} service={fake} portraitCount={2} />))
    await flush()
    expect(button('確認して確定').disabled).toBe(true)
    expect(container.textContent).toContain('未割り当て')
    await act(async () => { button('話者を割り当てる').click() })
    await flush()
    expect(container.textContent).toContain('下書き（未確認）')
    expect(container.textContent).not.toContain('確定済みです')
    expect(button('確認して確定').disabled).toBe(false)
    await act(async () => { button('確認して確定').click() })
    await flush()
    expect(calls).toEqual(['estimate', 'confirm'])
    expect(container.textContent).toContain('確定済み')
    expect(button('確認して確定').disabled).toBe(true)
  })

  it('cannot assign without a registered portrait and says so when the host refuses', async () => {
    const { fake } = service(result())
    act(() => root.render(<SpeakerAssignmentPanel projectId={project} service={fake} portraitCount={0} />))
    await flush()
    expect(button('話者を割り当てる').disabled).toBe(true)
    const refusing = { ...fake, estimate: vi.fn(async () => result({ status: 'forbidden' }) as SpeakerOutcome) }
    act(() => root.render(<SpeakerAssignmentPanel projectId={project} service={refusing} portraitCount={1} />))
    await flush()
    await act(async () => { button('話者を割り当てる').click() })
    await flush()
    expect(container.querySelector('[role="alert"]')?.textContent).toContain('権限')
  })

  it('renders nothing without a host service or a Project', () => {
    act(() => root.render(<SpeakerAssignmentPanel projectId={null} service={service(result()).fake} portraitCount={1} />))
    expect(container.textContent).toBe('')
    act(() => root.render(<SpeakerAssignmentPanel projectId={project} service={null} portraitCount={1} />))
    expect(container.textContent).toBe('')
  })
})

// FR-140 AC-167: the user gives a range and cuts one character out.
describe('PortraitCropForm', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })
  const setValue = (input: HTMLInputElement | HTMLSelectElement, value: string) => {
    const proto = input instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype
    Object.getOwnPropertyDescriptor(proto, 'value')?.set?.call(input, value)
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }))
  }

  it('is hidden with no registered image, and sends the range only when it is valid', () => {
    act(() => root.render(<PortraitCropForm portraits={[]} onCrop={vi.fn()} />))
    expect(container.textContent).toBe('')
    const onCrop = vi.fn()
    act(() => root.render(<PortraitCropForm onCrop={onCrop} portraits={[{ id: speaker, name: '元絵', rights: 'unconfirmed', subtitleColor: '#FFD400' }]} />))
    const submit = container.querySelector('button[type="submit"]') as HTMLButtonElement
    expect(submit.disabled).toBe(true)
    const inputs = [...container.querySelectorAll('input[type="text"]')] as HTMLInputElement[]
    act(() => {
      setValue(container.querySelector('select') as HTMLSelectElement, speaker)
      setValue(inputs[2]!, '20'); setValue(inputs[3]!, '4')   // height below the minimum side
      setValue(inputs[4]!, '左の子')
    })
    expect(submit.disabled).toBe(true)
    act(() => { setValue(inputs[3]!, '20') })
    expect(submit.disabled).toBe(false)
    act(() => { submit.click() })
    expect(onCrop).toHaveBeenCalledWith({ sourceId: speaker, name: '左の子', x: 0, y: 0, width: 20, height: 20 })
  })
})
