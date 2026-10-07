// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ProjectListItem } from '../../bridge/projectProtocol'
import { createPipelineSnapshot, fixtureId, pipelineResponse } from '../workflow/pipelineTestFixture'
import type { PipelinePhase, PipelineRequest, PipelineResponse, PipelineSnapshot, PipelineVersionEntry, PipelineVersionsResponse } from '../workflow/pipelineProtocol'
import { pipelinePhaseLabels } from '../workflow/pipelinePresentation'
import { AllProjectJobsPanel } from './AllProjectJobsPanel'

type Status = PipelineVersionsResponse['result']['status']
type ReadReply = PipelineSnapshot | null | 'manual'

const project = (n: number, name: string, state: 'draft' | 'finalized' = 'finalized'): ProjectListItem => ({
  draftId: `draft-${n}`, projectId: state === 'finalized' ? fixtureId(n) : null, state, contentName: name, episodeNumber: null,
  sourceStatus: 'ready', owner: null, publicationScope: null, editState: null, reviewState: null, outputFormats: [], updatedAt: '2026-01-01T00:00:00Z',
} as unknown as ProjectListItem)

const entry = (n: number, createdAt: string, phase: PipelineVersionEntry['phase'] = 'Completed'): PipelineVersionEntry => ({
  versionId: fixtureId(n), versionDigest: 'a'.repeat(64), parentVersionId: null, executionId: fixtureId(100 + n),
  phase, hasResult: true, isCurrent: false, createdAt, settings: [], outputs: [],
})

// A version-less execution of project n (FR-061.10).
const snapshotOf = (n: number, executionN: number, phase: PipelinePhase, revision = 3): PipelineSnapshot =>
  ({ ...createPipelineSnapshot(phase, fixtureId(n)), executionId: fixtureId(executionN), revision }) as PipelineSnapshot

/** pipeline.read is answered at once with `read(projectId)` unless that returns 'manual'. */
function fakeClient(read: (projectId: string) => ReadReply = () => null) {
  let listener: ((r: PipelineVersionsResponse, projectId: string) => void) | null = null
  let pipelineListener: ((r: PipelineResponse) => void) | null = null
  const sent: { requestId: string, projectId: string }[] = []
  const sentPipeline: PipelineRequest[] = []
  const client = {
    sendPipelineVersions: vi.fn((requestId: string, projectId: string) => { sent.push({ requestId, projectId }) }),
    onPipelineVersionsResponse: vi.fn((l: typeof listener) => { listener = l; return vi.fn() }),
    sendPipeline: vi.fn((request: PipelineRequest) => {
      sentPipeline.push(request)
      if (request.method !== 'pipeline.read') return
      const reply = read(request.params.projectId)
      if (reply !== 'manual') pipelineListener!(pipelineResponse(reply, request.requestId))
    }),
    onPipelineResponse: vi.fn((l: typeof pipelineListener) => { pipelineListener = l; return vi.fn() }),
    dispose: vi.fn(),
  }
  function respond(index: number, status: Status, versions: PipelineVersionEntry[] = [], requestId = sent[index].requestId) {
    act(() => {
      listener!({ version: 1, requestId, method: 'pipeline.versions', result: { status, workingCopyRevision: 0, versions } } as unknown as PipelineVersionsResponse, sent[index].projectId)
    })
  }
  function respondPipeline(response: unknown) { act(() => { pipelineListener!(response as PipelineResponse) }) }
  return { client, sent, sentPipeline, respond, respondPipeline }
}

// FR-061 AC-036
describe('AllProjectJobsPanel', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { vi.useFakeTimers(); container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove(); vi.useRealTimers() })

  type Props = Parameters<typeof AllProjectJobsPanel>[0]
  const render = (props: Props) => act(() => root.render(<AllProjectJobsPanel {...props} />))
  const flush = () => act(async () => { await Promise.resolve(); await Promise.resolve() })
  const refresh = () => container.querySelector('button')! as HTMLButtonElement
  const rowsText = () => [...container.querySelectorAll('tbody tr')].map(tr => [...tr.querySelectorAll('td')].slice(0, 3).map(td => td.textContent).join('|'))

  it('says there are no jobs when no finalized Project exists and sends nothing', async () => {
    const fake = fakeClient()
    render({ active: true, projects: [project(1, '下書き', 'draft')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    expect(container.textContent).toContain('ジョブはまだありません。Project一覧で元動画を選んで「編集を開始」すると、ジョブが作られます。')
    expect(fake.client.sendPipelineVersions).not.toHaveBeenCalled()
  })

  it('does not read while inactive, and reads when it becomes active', async () => {
    const fake = fakeClient()
    const props: Props = { active: false, projects: [project(1, 'A')], createClient: () => fake.client, onOpenJob: vi.fn() }
    render(props)
    await flush()
    expect(fake.sent).toHaveLength(0)
    render({ ...props, active: true })
    await flush()
    expect(fake.sent).toHaveLength(1)
  })

  it('reads one Project at a time, then lists jobs newest first with numbers and labels', async () => {
    const fake = fakeClient()
    render({ active: true, projects: [project(1, 'A'), project(2, 'B'), project(3, '下書き', 'draft')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    expect(fake.sent.map(s => s.projectId)).toEqual([fixtureId(1)])
    expect(container.querySelector('[role="status"]')?.textContent).toBe('ジョブの状態を取得しています（0 / 2 Project）。')
    expect(refresh().disabled).toBe(true)
    fake.respond(0, 'available', [entry(2, '2026-02-01T00:00:00Z', 'Failed'), entry(1, '2026-01-01T00:00:00Z')])
    await flush()
    expect(fake.sent.map(s => s.projectId)).toEqual([fixtureId(1), fixtureId(2)])
    expect(fake.client.onPipelineVersionsResponse).toHaveBeenCalledOnce()
    fake.respond(1, 'available', [entry(3, '2026-03-01T00:00:00Z')])
    await flush()
    expect(fake.client.dispose).toHaveBeenCalled()
    expect(refresh().disabled).toBe(false)
    expect(rowsText()).toEqual(['B|Job 1（最新）|' + pipelinePhaseLabels.Completed, 'A|Job 2（最新）|' + pipelinePhaseLabels.Failed, 'A|Job 1|' + pipelinePhaseLabels.Completed])
    expect(container.querySelector('caption')?.textContent).toBe('ジョブの一覧（3件）')
    expect([...container.querySelectorAll('tbody tr')].map(tr => tr.getAttribute('data-job-phase'))).toEqual(['Completed', 'Failed', 'Completed'])
    expect(container.textContent).not.toContain(fixtureId(1))
  })

  it('ignores responses with an unexpected requestId', async () => {
    const fake = fakeClient()
    render({ active: true, projects: [project(1, 'A'), project(2, 'B')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    fake.respond(0, 'available', [entry(1, '2026-01-01T00:00:00Z')], fixtureId(9999))
    await flush()
    expect(fake.sent).toHaveLength(1)
    expect(container.querySelector('tbody')).toBeNull()
  })

  it('treats notFound as zero jobs and other statuses as a failure', async () => {
    const fake = fakeClient()
    render({ active: true, projects: [project(1, 'A'), project(2, 'B')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    fake.respond(0, 'notFound')
    await flush()
    fake.respond(1, 'unavailable')
    await flush()
    expect(container.textContent).toContain('「B」のジョブを取得できませんでした。')
    expect(container.textContent).not.toContain('「A」のジョブを取得できませんでした。')
    expect(container.textContent).not.toContain('ジョブはまだありません。')
  })

  it('shows the empty message when every read succeeds with no jobs', async () => {
    const fake = fakeClient()
    render({ active: true, projects: [project(1, 'A')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    fake.respond(0, 'notFound')
    await flush()
    expect(container.textContent).toContain('ジョブはまだありません。')
    expect(container.querySelector('table')).toBeNull()
  })

  it('records a timed-out Project as failed and continues with the next one', async () => {
    const fake = fakeClient()
    render({ active: true, projects: [project(1, 'A'), project(2, 'B')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    await act(async () => { vi.advanceTimersByTime(15_000); await Promise.resolve() })
    await flush()
    expect(fake.sent.map(s => s.projectId)).toEqual([fixtureId(1), fixtureId(2)])
    fake.respond(1, 'available', [entry(1, '2026-01-01T00:00:00Z')])
    await flush()
    expect(container.textContent).toContain('「A」のジョブを取得できませんでした。')
    expect(rowsText()).toEqual(['B|Job 1（最新）|' + pipelinePhaseLabels.Completed])
  })

  it('marks remaining Projects failed when the client cannot be created or send throws', async () => {
    render({ active: true, projects: [project(1, 'A'), project(2, 'B')], createClient: () => { throw new Error('no host') }, onOpenJob: vi.fn() })
    await flush()
    expect(container.textContent).toContain('「A」のジョブを取得できませんでした。')
    expect(container.textContent).toContain('「B」のジョブを取得できませんでした。')
    const fake = fakeClient()
    fake.client.sendPipelineVersions.mockImplementation(() => { throw new Error('busy') })
    render({ active: false, projects: [project(1, 'A'), project(2, 'B')], createClient: () => fake.client, onOpenJob: vi.fn() })
    render({ active: true, projects: [project(1, 'A'), project(2, 'B')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    expect(fake.client.sendPipelineVersions).toHaveBeenCalledOnce()
    expect(container.textContent).toContain('「B」のジョブを取得できませんでした。')
  })

  it('lets a refresh supersede a run, keeping old rows visible meanwhile', async () => {
    const first = fakeClient()
    const second = fakeClient()
    const clients = [first, second]
    const props: Props = { active: true, projects: [project(1, 'A'), project(2, 'B')], createClient: () => clients.shift()!.client, onOpenJob: vi.fn() }
    render(props)
    await flush()
    first.respond(0, 'available', [entry(1, '2026-01-01T00:00:00Z')])
    await flush()
    first.respond(1, 'notFound')
    await flush()
    expect(rowsText()).toHaveLength(1)
    // Second read begins; keep the first result visible and disable the button.
    await act(async () => { refresh().click() })
    await flush()
    expect(rowsText()).toHaveLength(1)
    expect(refresh().disabled).toBe(true)
    // Re-activation supersedes the in-flight run.
    render({ ...props, active: false })
    expect(second.client.dispose).not.toHaveBeenCalled()
  })

  it('discards a superseded run so only the newest generation publishes', async () => {
    const clients = [fakeClient(), fakeClient()]
    let next = 0
    const props: Props = { active: false, projects: [project(1, 'A')], createClient: () => clients[next++].client, onOpenJob: vi.fn() }
    render(props)
    render({ ...props, active: true })
    await flush()
    render({ ...props, active: false })
    render({ ...props, active: true })
    await flush()
    expect(next).toBe(2)
    expect(clients[0].client.dispose).toHaveBeenCalled()
    clients[0].respond(0, 'available', [entry(1, '2026-01-01T00:00:00Z')])
    clients[1].respond(0, 'available', [entry(2, '2026-02-01T00:00:00Z'), entry(1, '2026-01-01T00:00:00Z')])
    await flush()
    expect(rowsText()).toHaveLength(2)
  })

  it('opens a job by draft id, or disables the button with one shared reason', async () => {
    const fake = fakeClient()
    const onOpenJob = vi.fn()
    const props: Props = { active: true, projects: [project(1, 'A')], createClient: () => fake.client, onOpenJob }
    render(props)
    await flush()
    fake.respond(0, 'available', [entry(2, '2026-02-01T00:00:00Z'), entry(1, '2026-01-01T00:00:00Z')])
    await flush()
    const open = container.querySelector('button[aria-label="「A」のJob 2を開く"]') as HTMLButtonElement
    expect(open.textContent).toBe('開く')
    act(() => { open.click() })
    expect(onOpenJob).toHaveBeenCalledWith('draft-1')
    render({ ...props, openDisabledReason: '編集中のため開けません。' })
    expect([...container.querySelectorAll('tbody button')].every(b => (b as HTMLButtonElement).disabled)).toBe(true)
    expect(container.textContent?.split('編集中のため開けません。')).toHaveLength(2)
    act(() => { (container.querySelector('tbody button') as HTMLButtonElement).click() })
    expect(onOpenJob).toHaveBeenCalledOnce()
  })

  it('disposes the client and clears timers on unmount', async () => {
    const fake = fakeClient()
    render({ active: true, projects: [project(1, 'A')], createClient: () => fake.client, onOpenJob: vi.fn() })
    await flush()
    act(() => root.unmount())
    expect(fake.client.dispose).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(0)
    root = createRoot(container)
  })
})

/**
 * FR-061.10 (latest Job before any version), FR-061.11 (cancel / retry) and
 * FR-061.12 (automatic re-read while a Job is live).
 */
// FR-061 AC-036
describe('AllProjectJobsPanel latest Job, operations and auto refresh', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { vi.useFakeTimers(); container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove(); vi.useRealTimers() })

  type Props = Parameters<typeof AllProjectJobsPanel>[0]
  const flush = async () => { for (let i = 0; i < 6; i += 1) await act(async () => { await Promise.resolve() }) }
  const rowsText = () => [...container.querySelectorAll('tbody tr')].map(tr => [...tr.querySelectorAll('td')].slice(0, 3).map(td => td.textContent).join('|'))
  const button = (label: string) => container.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement | null
  const textButton = (text: string) => [...container.querySelectorAll('button')].find(b => b.textContent === text) as HTMLButtonElement
  const dialog = () => container.querySelector('[role="alertdialog"]')
  const click = (element: HTMLElement) => act(() => { element.click() })

  /** Renders project A (n=1) and completes the first read; every createClient call yields a new fake. */
  async function openTable(options: { snapshot: () => ReadReply, versions?: PipelineVersionEntry[], extra?: Partial<Props> }) {
    const fakes: ReturnType<typeof fakeClient>[] = []
    const props: Props = {
      active: true, projects: [project(1, 'A')], onOpenJob: vi.fn(), ...options.extra,
      createClient: () => { const fake = fakeClient(() => options.snapshot()); fakes.push(fake); return fake.client },
    }
    act(() => root.render(<AllProjectJobsPanel {...props} />))
    await flush()
    fakes[0].respond(0, options.versions === undefined ? 'notFound' : 'available', options.versions ?? [])
    await flush()
    return { fakes, props }
  }

  const versionsOne = [entry(1, '2026-01-01T00:00:00Z')]

  it('adds a version-less latest Job first with the next Job number and keeps the older row not latest', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Analyzing'), versions: versionsOne })
    expect(fakes[0].sentPipeline.map(r => r.method)).toEqual(['pipeline.read'])
    expect(fakes[0].sentPipeline[0].params.projectId).toBe(fixtureId(1))
    expect(rowsText()).toEqual(['A|Job 2（最新）|' + pipelinePhaseLabels.Analyzing, 'A|Job 1|' + pipelinePhaseLabels.Completed])
    expect(container.querySelectorAll('tbody tr')[0].querySelectorAll('td')[3].textContent).toBe('不明')
  })

  it('shows Job 1 for a Project that has only a version-less Job (versions notFound)', async () => {
    await openTable({ snapshot: () => snapshotOf(1, 50, 'Queued') })
    expect(rowsText()).toEqual(['A|Job 1（最新）|' + pipelinePhaseLabels.Queued])
  })

  it('sorts the version-less latest row before dated rows of other Projects', async () => {
    const fakes: ReturnType<typeof fakeClient>[] = []
    act(() => root.render(<AllProjectJobsPanel active projects={[project(1, 'A'), project(2, 'B')]} onOpenJob={vi.fn()}
      createClient={() => { const f = fakeClient(id => id === fixtureId(2) ? snapshotOf(2, 60, 'Rendering') : null); fakes.push(f); return f.client }} />))
    await flush()
    fakes[0].respond(0, 'available', [entry(1, '2026-05-01T00:00:00Z')])
    await flush()
    fakes[0].respond(1, 'available', [entry(2, '2026-01-01T00:00:00Z')])
    await flush()
    expect(rowsText()).toEqual(['B|Job 2（最新）|' + pipelinePhaseLabels.Rendering, 'A|Job 1（最新）|' + pipelinePhaseLabels.Completed, 'B|Job 1|' + pipelinePhaseLabels.Completed])
    expect(fakes[0].sentPipeline.map(r => r.params.projectId)).toEqual([fixtureId(1), fixtureId(2)])
  })

  it('takes the latest phase from the snapshot when its execution is already a versions row', async () => {
    await openTable({ snapshot: () => snapshotOf(1, 101, 'Failed'), versions: versionsOne })
    expect(rowsText()).toEqual(['A|Job 1（最新）|' + pipelinePhaseLabels.Failed])
  })

  it('keeps the versions rows when pipeline.read times out, without marking the Project failed', async () => {
    const { fakes } = await openTable({ snapshot: () => 'manual', versions: versionsOne })
    expect(rowsText()).toEqual([])
    await act(async () => { vi.advanceTimersByTime(15_000); await Promise.resolve() })
    await flush()
    expect(rowsText()).toEqual(['A|Job 1（最新）|' + pipelinePhaseLabels.Completed])
    expect(container.textContent).not.toContain('取得できませんでした')
    expect(fakes[0].client.dispose).toHaveBeenCalled()
  })

  it('ignores a pipeline.read response with another requestId', async () => {
    const { fakes } = await openTable({ snapshot: () => 'manual', versions: versionsOne })
    fakes[0].respondPipeline(pipelineResponse(snapshotOf(1, 50, 'Queued'), fixtureId(9999)))
    await flush()
    expect(container.querySelector('tbody')).toBeNull()
    fakes[0].respondPipeline(pipelineResponse(snapshotOf(1, 50, 'Queued'), fakes[0].sentPipeline[0].requestId))
    await flush()
    expect(rowsText()).toHaveLength(2)
  })

  it('cancels the live latest Job after confirmation, then rereads', async () => {
    let current: ReadReply = snapshotOf(1, 50, 'Composing', 7)
    const { fakes } = await openTable({ snapshot: () => current, versions: versionsOne })
    expect(button('「A」のJob 2を開く')).not.toBeNull()
    expect(button('「A」のJob 1を取り消す')).toBeNull()
    const cancel = button('「A」のJob 2を取り消す')!
    expect(cancel.textContent).toBe('取消')
    expect(button('「A」のJob 2を再試行する')).toBeNull()
    click(cancel)
    expect(dialog()?.getAttribute('aria-label')).toBe('ジョブの操作の確認')
    expect(dialog()?.textContent).toContain('「A」のJob 2を取り消しますか。取り消すと、このJobの処理は止まります。')
    expect(document.activeElement).toBe(textButton('やめる'))
    expect(fakes).toHaveLength(1)
    current = snapshotOf(1, 50, 'Canceled', 8)
    click(textButton('実行する'))
    await flush()
    expect(dialog()).toBeNull()
    expect(document.activeElement).toBe(cancel)
    expect(fakes).toHaveLength(2)
    expect(fakes[1].sentPipeline).toHaveLength(1)
    expect(fakes[1].sentPipeline[0]).toMatchObject({ method: 'pipeline.cancel', params: { projectId: fixtureId(1), executionId: fixtureId(50), expectedRevision: 7 } })
    fakes[1].respondPipeline(pipelineResponse(snapshotOf(1, 50, 'Canceled', 8), fakes[1].sentPipeline[0].requestId, 'pipeline.cancel'))
    await flush()
    expect([...container.querySelectorAll('p[role="status"]')].map(p => p.textContent)).toContain('「A」のJob 2の取り消しを受け付けました。')
    expect(fakes[1].client.dispose).toHaveBeenCalled()
    expect(fakes).toHaveLength(3)
    expect(fakes[2].sent).toHaveLength(1)
    fakes[2].respond(0, 'available', versionsOne)
    await flush()
    expect(rowsText()[0]).toBe('A|Job 2（最新）|' + pipelinePhaseLabels.Canceled)
  })

  it('shows retry only for a failed or canceled latest Job without version or result', async () => {
    await openTable({ snapshot: () => snapshotOf(1, 50, 'Failed'), versions: versionsOne })
    expect(button('「A」のJob 2を再試行する')?.textContent).toBe('再試行')
    expect(button('「A」のJob 2を取り消す')).toBeNull()
    expect(button('「A」のJob 1を再試行する')).toBeNull()
    act(() => root.unmount())
    root = createRoot(container)
    await openTable({ snapshot: () => snapshotOf(1, 50, 'Canceled'), versions: versionsOne })
    expect(button('「A」のJob 2を再試行する')).not.toBeNull()
    act(() => root.unmount())
    root = createRoot(container)
    // The failed execution is itself a version: not retryable.
    await openTable({ snapshot: () => ({ ...snapshotOf(1, 101, 'Failed'), versionId: fixtureId(1), versionDigest: 'a'.repeat(64) }), versions: versionsOne })
    expect(button('「A」のJob 1を再試行する')).toBeNull()
    act(() => root.unmount())
    root = createRoot(container)
    await openTable({ snapshot: () => snapshotOf(1, 50, 'Completed'), versions: versionsOne })
    expect(container.querySelector('button[aria-label*="再試行"]')).toBeNull()
    expect(container.querySelector('button[aria-label*="取り消す"]')).toBeNull()
  })

  it('retries after confirmation and reports the acceptance', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Failed', 4), versions: versionsOne })
    click(button('「A」のJob 2を再試行する')!)
    expect(dialog()?.textContent).toContain('「A」のJob 2を再試行しますか。')
    click(textButton('実行する'))
    await flush()
    expect(fakes[1].sentPipeline[0]).toMatchObject({ method: 'pipeline.retry', params: { projectId: fixtureId(1), executionId: fixtureId(50), expectedRevision: 4 } })
    fakes[1].respondPipeline(pipelineResponse(snapshotOf(1, 51, 'Queued', 0), fakes[1].sentPipeline[0].requestId, 'pipeline.retry'))
    await flush()
    expect(container.textContent).toContain('「A」のJob 2の再試行を受け付けました。')
    expect(fakes).toHaveLength(3)
  })

  it('sends nothing when the confirmation is dismissed with やめる or Escape', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Composing'), versions: versionsOne })
    const cancel = button('「A」のJob 2を取り消す')!
    click(cancel)
    click(textButton('やめる'))
    expect(dialog()).toBeNull()
    expect(document.activeElement).toBe(cancel)
    click(cancel)
    act(() => { textButton('やめる').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(dialog()).toBeNull()
    expect(document.activeElement).toBe(cancel)
    await flush()
    expect(fakes).toHaveLength(1)
    expect(container.textContent).not.toContain('受け付けました')
  })

  it('reports a failure for a non-available status and rereads', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Composing'), versions: versionsOne })
    click(button('「A」のJob 2を取り消す')!)
    click(textButton('実行する'))
    await flush()
    fakes[1].respondPipeline({ version: 1, requestId: fakes[1].sentPipeline[0].requestId, method: 'pipeline.cancel', result: { status: 'unavailable', snapshot: null } })
    await flush()
    expect(container.textContent).toContain('「A」のJob 2を操作できませんでした。状態が変わった可能性があります。')
    expect(container.textContent).not.toContain('受け付けました')
    expect(fakes).toHaveLength(3)
  })

  it('reports a failure when the operation times out, and rereads', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Composing'), versions: versionsOne })
    click(button('「A」のJob 2を取り消す')!)
    click(textButton('実行する'))
    await flush()
    expect(textButton('最新の状態に更新').disabled).toBe(true)
    await act(async () => { vi.advanceTimersByTime(15_000); await Promise.resolve() })
    await flush()
    expect(container.textContent).toContain('「A」のJob 2を操作できませんでした。状態が変わった可能性があります。')
    expect(fakes[1].client.dispose).toHaveBeenCalled()
    expect(fakes[1].sentPipeline).toHaveLength(1)
    expect(fakes).toHaveLength(3)
  })

  it('reports a failure when sending throws', async () => {
    const { fakes, props } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Composing'), versions: versionsOne })
    const make = props.createClient!
    act(() => root.render(<AllProjectJobsPanel {...props} createClient={() => {
      const fake = make() as unknown as ReturnType<typeof fakeClient>['client']
      fake.sendPipeline.mockImplementation(() => { throw new Error('busy') })
      return fake
    }} />))
    click(button('「A」のJob 2を取り消す')!)
    click(textButton('実行する'))
    await flush()
    expect(container.textContent).toContain('「A」のJob 2を操作できませんでした。状態が変わった可能性があります。')
    expect(fakes[1].sentPipeline).toHaveLength(0)
  })

  it('disables operation buttons while a read run is in flight', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Composing'), versions: versionsOne })
    expect(button('「A」のJob 2を取り消す')!.disabled).toBe(false)
    click(textButton('最新の状態に更新'))
    await flush()
    expect(fakes).toHaveLength(2)
    expect(button('「A」のJob 2を取り消す')!.disabled).toBe(true)
  })

  it('rereads 10 seconds after a run when a latest Job is live, and says so', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Rendering'), versions: versionsOne })
    expect(container.textContent).toContain('実行中のジョブがあるあいだは、10秒ごとに自動で更新します。')
    await act(async () => { vi.advanceTimersByTime(9_999) })
    expect(fakes).toHaveLength(1)
    await act(async () => { vi.advanceTimersByTime(1) })
    await flush()
    expect(fakes).toHaveLength(2)
    expect(fakes[1].sent).toHaveLength(1)
    expect(container.textContent).not.toContain('10秒ごとに自動で更新します')
    fakes[1].respond(0, 'available', versionsOne)
    await flush()
    await act(async () => { vi.advanceTimersByTime(10_000) })
    await flush()
    expect(fakes).toHaveLength(3)
  })

  it('does not reread automatically when no latest Job is live', async () => {
    const { fakes } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Failed'), versions: versionsOne })
    expect(container.textContent).not.toContain('10秒ごとに自動で更新します')
    await act(async () => { vi.advanceTimersByTime(120_000) })
    expect(fakes).toHaveLength(1)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('clears the auto refresh timer when inactive, on a manual refresh and on unmount', async () => {
    const { fakes, props } = await openTable({ snapshot: () => snapshotOf(1, 50, 'Rendering'), versions: versionsOne })
    expect(vi.getTimerCount()).toBe(1)
    click(textButton('最新の状態に更新'))
    await flush()
    expect(fakes).toHaveLength(2)
    expect(vi.getTimerCount()).toBe(1) // the request timeout, not the auto refresh
    await act(async () => { vi.advanceTimersByTime(14_999) })
    fakes[1].respond(0, 'available', versionsOne)
    await flush()
    expect(vi.getTimerCount()).toBe(1)
    act(() => root.render(<AllProjectJobsPanel {...props} active={false} />))
    expect(vi.getTimerCount()).toBe(0)
    await act(async () => { vi.advanceTimersByTime(60_000) })
    expect(fakes).toHaveLength(2)
    act(() => root.render(<AllProjectJobsPanel {...props} active />))
    await flush()
    fakes[2].respond(0, 'available', versionsOne)
    await flush()
    expect(vi.getTimerCount()).toBe(1)
    act(() => root.unmount())
    expect(vi.getTimerCount()).toBe(0)
    root = createRoot(container)
  })
})
