// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ResultGallery } from '../results/ResultGallery'
import { deriveResultPresentation } from '../results/resultPresentation'
import { verifiedPipelineResponse } from '../workflow/pipelineProtocol'
import { createCompletedPipeline, createPipelineArtifact, fixtureId, pipelineResponse } from '../workflow/pipelineTestFixture'
import { stitchFailureMessages, stitchMessages } from './BookendStitchPanel'
import { STITCH_FAILURE_CODES, type BookendStitchService, type StitchOutcome, type StitchPickClipResult, type StitchRunParams, type StitchRunResult } from './stitchProtocol'

const none = { code: null, outputFileName: null, manifestFileName: null, durationSeconds: null } as const

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(r => { resolve = r })
  return { promise, resolve }
}

function fakeService() {
  const picks: Array<ReturnType<typeof deferred<StitchOutcome<StitchPickClipResult>>>> = []
  const runs: Array<ReturnType<typeof deferred<StitchOutcome<StitchRunResult>>>> = []
  const runParams: StitchRunParams[] = []
  const service: BookendStitchService = {
    pickClip: vi.fn(() => { const d = deferred<StitchOutcome<StitchPickClipResult>>(); picks.push(d); return d.promise }),
    run: vi.fn((params: StitchRunParams) => { runParams.push(params); const d = deferred<StitchOutcome<StitchRunResult>>(); runs.push(d); return d.promise }),
    dispose: vi.fn(),
  }
  return { service, picks, runs, runParams }
}

describe('BookendStitchPanel in the result gallery', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.unstubAllGlobals()
  })

  function verifiedSnapshot() {
    const snapshot = createCompletedPipeline()
    snapshot.result!.artifacts.push(createPipelineArtifact(snapshot.result!, 'LandscapeThumbnail', 20))
    snapshot.result!.artifacts.push(createPipelineArtifact(snapshot.result!, 'SubRip', 21))
    return verifiedPipelineResponse(pipelineResponse(snapshot))!.result.snapshot!
  }

  function render(service: BookendStitchService | null | undefined, pipeline = verifiedSnapshot()) {
    act(() => root.render(<ResultGallery presentation={deriveResultPresentation({ pipeline })} bookendStitch={service} />))
    return pipeline
  }

  const button = (name: string | RegExp) => within(container).getByRole<HTMLButtonElement>('button', { name })
  const status = () => container.querySelector('[data-stitch-message]')!.textContent

  async function settle<T>(d: { resolve: (value: T) => void }, value: T) {
    await act(async () => { d.resolve(value); await Promise.resolve() })
  }

  it('appears only for media-verified video outputs, never for images, subtitles or unverified results', () => {
    const pipeline = render(fakeService().service)
    const panels = container.querySelectorAll('[data-stitch-artifact]')
    expect(panels).toHaveLength(1)
    expect(panels[0]!.getAttribute('data-stitch-artifact')).toBe(pipeline.result!.artifacts[0]!.artifact.artifactId)
    expect(within(container).getByText('オープニング・クロージングを付けて保存').tagName).toBe('SUMMARY')

    const unverified = createCompletedPipeline()
    act(() => root.render(<ResultGallery presentation={deriveResultPresentation({ pipeline: unverified })} bookendStitch={fakeService().service} />))
    expect(container.querySelectorAll('[data-stitch-artifact]')).toHaveLength(0)
  })

  it('shows the action as unavailable without a host bridge', () => {
    render(undefined)
    expect(container.textContent).toContain(stitchMessages.unavailableEnvironment)
    for (const name of ['オープニングを選ぶ', 'クロージングを選ぶ', '結合して保存']) expect(button(name).disabled).toBe(true)
  })

  it('enables saving only after a selection and sends the exact output identity', async () => {
    const { service, picks, runs, runParams } = fakeService()
    const pipeline = render(service)
    const artifact = pipeline.result!.artifacts[0]!
    expect(button('結合して保存').disabled).toBe(true)

    act(() => button('オープニングを選ぶ').click())
    expect(button('選択中…').disabled).toBe(true)
    expect(button('結合して保存').disabled).toBe(true)
    await settle(picks[0]!, { status: 'selected', clipId: fixtureId(400), fileName: 'jingle.mp4' })
    expect(container.querySelector('[data-stitch-selection="opening"]')!.textContent).toBe('jingle.mp4')
    expect(button('結合して保存').disabled).toBe(false)

    act(() => button('結合して保存').click())
    expect(button('結合中…').disabled).toBe(true)
    expect(button('オープニングを選ぶ').disabled).toBe(true)
    expect(runParams).toEqual([{ projectId: pipeline.projectId, executionId: pipeline.executionId, artifactId: artifact.artifact.artifactId,
      artifactDigest: artifact.artifact.contentDigest, openingClipId: fixtureId(400), closingClipId: null }])
    await settle(runs[0]!, { status: 'completed', code: null, outputFileName: 'final.mp4', manifestFileName: 'final.stitch.json', durationSeconds: 5.5 })
    expect(status()).toBe('保存しました: final.mp4（5.5秒）。同じ場所に final.stitch.json を作成しました。')
    expect(container.querySelector('[data-stitch-message]')!.getAttribute('role')).toBe('status')
    expect(button('結合して保存').disabled).toBe(false)

    act(() => button('オープニングの選択を外す').click())
    expect(container.querySelector('[data-stitch-selection="opening"]')!.textContent).toBe('未選択')
    expect(button('結合して保存').disabled).toBe(true)
  })

  it('shows every run status and failure code in Japanese without paths', async () => {
    const { service, picks, runs } = fakeService()
    render(service)
    act(() => button('クロージングを選ぶ').click())
    await settle(picks[0]!, { status: 'selected', clipId: fixtureId(401), fileName: 'outro.mov' })
    const cases: Array<[StitchOutcome<StitchRunResult>, string]> = [
      [{ status: 'cancelled', ...none }, stitchMessages.cancelled],
      [{ status: 'notFound', ...none }, stitchMessages.notFound],
      [{ status: 'forbidden', ...none }, stitchMessages.forbidden],
      [{ status: 'busy', ...none }, stitchMessages.busy],
      [{ status: 'unavailable', ...none }, stitchMessages.unavailable],
      [{ status: 'timeout' }, stitchMessages.timeout],
      [{ status: 'disconnected' }, stitchMessages.unavailable],
      ...STITCH_FAILURE_CODES.map(code => [{ status: 'failed', ...none, code }, `${stitchFailureMessages[code]}（${code}）`] as [StitchOutcome<StitchRunResult>, string]),
    ]
    for (const [index, [outcome, text]] of cases.entries()) {
      act(() => button('結合して保存').click())
      await settle(runs[index]!, outcome)
      expect(status()).toBe(text)
    }
    expect(container.textContent).not.toMatch(/[A-Za-z]:\\|\\\\/)

    act(() => button('結合して保存').click())
    await settle(runs[cases.length]!, { status: 'invalid', ...none })
    expect(status()).toBe(stitchMessages.invalid)
    expect(container.querySelector('[data-stitch-selection="closing"]')!.textContent).toBe('未選択')
    expect(button('結合して保存').disabled).toBe(true)
  })

  it('reports picker outcomes and keeps the previous selection on cancel', async () => {
    const { service, picks } = fakeService()
    render(service)
    const outcomes: Array<[StitchOutcome<StitchPickClipResult>, string]> = [
      [{ status: 'selected', clipId: fixtureId(402), fileName: 'a.mp4' }, ''],
      [{ status: 'cancelled', clipId: null, fileName: null }, ''],
      [{ status: 'unsupported', clipId: null, fileName: null }, stitchMessages.unsupported],
      [{ status: 'forbidden', clipId: null, fileName: null }, stitchMessages.forbidden],
      [{ status: 'unavailable', clipId: null, fileName: null }, stitchMessages.unavailable],
      [{ status: 'timeout' }, stitchMessages.timeout],
    ]
    for (const [index, [outcome, text]] of outcomes.entries()) {
      act(() => button('オープニングを選ぶ').click())
      await settle(picks[index]!, outcome)
      expect(status()).toBe(text)
      expect(container.querySelector('[data-stitch-selection="opening"]')!.textContent).toBe('a.mp4')
    }
  })
})
