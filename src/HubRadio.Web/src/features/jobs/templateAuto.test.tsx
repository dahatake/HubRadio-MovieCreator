// @vitest-environment jsdom
import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { JobCreationAction } from './JobCreationAction'
import {
  createTemplateAutoRequest, createTemplateAutoService, parseTemplateAutoResponse,
  type TemplateAutoOutcome, type TemplateAutoRequest, type TemplateAutoResult, type TemplateAutoService, type TemplateAutoTransport,
} from './templateAutoProtocol'
import { styleTemplateOptions } from './styleTemplates'

const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const project = id(9)
const result = (over: Partial<TemplateAutoResult> = {}): TemplateAutoResult => ({ status: 'ok', state: 'none', templateId: null, reason: '', jobOrigin: null, ...over })
const envelope = (r: unknown, method = 'templateAuto.get', requestId = id(1)) => ({ version: 1, requestId, method, result: r })

// FR-145 / NFR-REL-002: the templateAuto.get / templateAuto.touch bridge protocol.
describe('templateAutoProtocol', () => {
  it('builds closed requests and refuses invalid ones', () => {
    expect(createTemplateAutoRequest(id(1), 'templateAuto.get', project).params).toEqual({ projectId: project })
    expect(createTemplateAutoRequest(id(1), 'templateAuto.touch', project).method).toBe('templateAuto.touch')
    expect(() => createTemplateAutoRequest('nope', 'templateAuto.get', project)).toThrow(TypeError)
    expect(() => createTemplateAutoRequest(id(1), 'templateAuto.get', 'nope')).toThrow(TypeError)
  })

  it('parses only the closed result', () => {
    const ok = parseTemplateAutoResponse(envelope(result({ state: 'selected', templateId: 'explainer', reason: '解説を選びました。' })))
    expect(ok?.result).toMatchObject({ state: 'selected', templateId: 'explainer' })
    expect(parseTemplateAutoResponse(envelope(result(), 'speakers.get'))).toBeNull()
    expect(parseTemplateAutoResponse(envelope({ ...result(), extra: 1 }))).toBeNull()
    expect(parseTemplateAutoResponse(envelope(result({ state: 'maybe' as never })))).toBeNull()
    expect(parseTemplateAutoResponse(envelope(result({ templateId: 'C:\\x' })))).toBeNull()
  })

  it('resolves a request with its response and reports a silent or closed host', async () => {
    const held: { listener: ((event: { readonly data: unknown }) => void) | null } = { listener: null }
    const sent: TemplateAutoRequest[] = []
    const transport: TemplateAutoTransport = {
      postMessage: message => { sent.push(message) },
      addEventListener: (_t, l) => { held.listener = l },
      removeEventListener: () => { held.listener = null },
    }
    const service = createTemplateAutoService(transport, { createRequestId: () => id(1), timeoutMilliseconds: 20 })
    const pending = service.get(project)
    held.listener?.({ data: envelope(result({ state: 'analyzing', reason: '解析中' })) })
    expect(await pending).toMatchObject({ state: 'analyzing' })
    expect(await service.touch(project)).toEqual({ status: 'timeout' })
    service.dispose()
    expect(await service.get(project)).toEqual({ status: 'disconnected' })
  })
})

describe('JobCreationAction automatic template selection', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { vi.useFakeTimers(); container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove(); vi.useRealTimers() })
  const flush = () => act(async () => { await Promise.resolve(); await Promise.resolve() })

  function Host({ service }: Readonly<{ service: TemplateAutoService }>) {
    const [selected, setSelected] = useState<string | null>(null)
    return <JobCreationAction nextJobNumber={2} params={null} disabledReason={null} pending={false} active onOpenSettings={() => {}}
      onConfirm={() => true} styleTemplateId={selected} onStyleTemplateChange={setSelected} portraitService={null} speakerService={null}
      templateAutoService={service} projectId={project} />
  }
  const checked = () => { const label = (container.querySelector('input[type="radio"]:checked') as HTMLInputElement | null)?.getAttribute('aria-label'); return label === null || label === undefined || label === 'テンプレートを使わない' ? 'none' : label }
  const service = (queue: TemplateAutoResult[]) => {
    const fake: TemplateAutoService = {
      get: vi.fn(async () => (queue.length > 1 ? queue.shift()! : queue[0]!) as TemplateAutoOutcome),
      touch: vi.fn(async () => result({ state: 'cancelled', reason: 'テンプレートを手動で選んだため、自動選択を取り消しました。' }) as TemplateAutoOutcome),
      dispose: vi.fn(),
    }
    return fake
  }

  // AC-179 AC-181: while analyzing the screen is usable and says so; when the analysis selects a template it becomes the initial selection with its reason.
  it('shows the analysis, then the selected template as the initial selection with the reason', async () => {
    const fake = service([result({ state: 'analyzing', reason: '解析中' }),
      result({ state: 'selected', templateId: 'short-pop', reason: '縦型ショート向けです。' })])
    act(() => root.render(<Host service={fake} />))
    await flush()
    expect(container.textContent).toContain('解析の完了を待たずに')
    expect(checked()).toBe('none')
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(checked()).toBe('ショート飛び出し')
    expect(container.textContent).toContain('自動選択: ショート飛び出し。理由: 縦型ショート向けです。')
  })

  // AC-180 AC-181: choosing a template themself tells the host (the automatic selection is cancelled) and is never overwritten later.
  it('cancels the automatic selection on a manual choice and never overwrites it', async () => {
    const fake = service([result({ state: 'analyzing', reason: '解析中' }),
      result({ state: 'selected', templateId: 'short-pop', reason: '縦型ショート向けです。' })])
    act(() => root.render(<Host service={fake} />))
    await flush()
    const other = container.querySelector(`input[aria-label="${styleTemplateOptions[0]!.name.replace(/\s+/gu, '')}"]`) as HTMLInputElement
    act(() => { other.click() })
    await flush()
    expect(fake.touch).toHaveBeenCalledWith(project)
    await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
    expect(checked()).toBe('解説')
  })

  // AC-182: no template is chosen (skipped) and the reason is shown; nothing blocks the screen.
  it('shows the reason when no template was chosen', async () => {
    act(() => root.render(<Host service={service([result({ state: 'skipped', reason: '解析が時間内に終わらなかったため、テンプレートは選びませんでした。' })])} />))
    await flush()
    expect(checked()).toBe('none')
    expect(container.textContent).toContain('解析が時間内に終わらなかった')
    expect(container.querySelector('.style-template-origin')).toBeNull()
  })

  // AC-181 AC-180 AC-182: the origin shown is the one the host recorded for the latest Job: 自動選択, 手動 or なし (never another value).
  it.each([['auto', '自動選択'], ['manual', '手動'], ['none', 'なし']] as const)('shows the recorded origin %s as %s', async (recorded, label) => {
    const fake = service([result({ state: 'selected', templateId: 'short-pop', reason: '縦型ショート向けです。', jobOrigin: recorded })])
    act(() => root.render(<Host service={fake} />))
    await flush()
    expect(container.querySelector('.style-template-origin')?.textContent).toBe(`直近の Job のテンプレートの出自: ${label}`)
  })

  it('rejects an origin that is not one of the three values', () => {
    expect(parseTemplateAutoResponse(envelope(result({ jobOrigin: '自動選択' as never })))).toBeNull()
    expect(parseTemplateAutoResponse(envelope(result({ jobOrigin: '' as never })))).toBeNull()
  })
})
