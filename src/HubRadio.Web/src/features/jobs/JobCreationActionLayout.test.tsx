// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fixtureId } from '../workflow/pipelineTestFixture'
import { JobCreationAction } from './JobCreationAction'

// FR-141 AC-168 (the entered talk-show values ride on the Job creation), FR-143 AC-174 (the subtitle-into-margin operation)
describe('JobCreationAction talk-show input and subtitle position', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })
  const params = { projectId: fixtureId(1), expectedVersionId: fixtureId(5), expectedVersionDigest: 'a'.repeat(64), expectedSettingsRevision: 3, expectedSettingsSnapshotDigest: 'b'.repeat(64) }
  const button = (label: string) => [...container.querySelectorAll('button')].find(item => item.textContent === label) as HTMLButtonElement
  function render(templateId: string, onConfirm: (...args: unknown[]) => boolean) {
    act(() => root.render(<JobCreationAction nextJobNumber={2} params={params} disabledReason={null} pending={false} active onOpenSettings={vi.fn()}
      onConfirm={onConfirm} styleTemplateId={templateId} onStyleTemplateChange={vi.fn()} portraitService={null} speakerService={null} />))
  }
  function type(label: string, value: string) {
    const input = container.querySelector(`input[aria-label="${label}"]`) as HTMLInputElement
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
    act(() => { setter.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) })
  }

  it('sends the entered names and titles as the talkShowInput JSON, with the confirmation state', () => {
    const onConfirm = vi.fn(() => true)
    render('talk-show', onConfirm)
    type('話者1の名前', '山田太郎')
    type('話者1の肩書き', '代表取締役')
    act(() => (container.querySelector('input[aria-label="肩書きを確認した"]') as HTMLInputElement).click())
    act(() => button('新しいJobを作成').click())
    act(() => button('作成する').click())
    expect(onConfirm).toHaveBeenCalledTimes(1)
    const [sent, template, extras] = onConfirm.mock.calls[0] as unknown as [unknown, string, { talkShowInput: string }]
    expect(sent).toEqual(params)
    expect(template).toBe('talk-show')
    expect(extras.talkShowInput.length).toBeLessThanOrEqual(8192)
    const parsed = JSON.parse(extras.talkShowInput) as { speakers: { name: string, title: string, titleConfirmed: boolean, titleConfirmedOn: string }[] }
    expect(parsed.speakers[0]).toMatchObject({ name: '山田太郎', title: '代表取締役', titleConfirmed: true })
    expect(parsed.speakers[0]!.titleConfirmedOn).toMatch(/^\d{4}-\d{2}-\d{2}$/)
    expect(parsed.speakers[1]).toMatchObject({ name: '', titleConfirmed: false })
  })

  it('moves the subtitle into the margin only after the explicit operation, for a vertical template only', () => {
    const onConfirm = vi.fn(() => true)
    render('vertical-3-tier', onConfirm)
    expect(button('字幕位置を余白内に動かす').getAttribute('aria-pressed')).toBe('false')
    act(() => button('新しいJobを作成').click())
    act(() => button('作成する').click())
    expect(onConfirm).toHaveBeenLastCalledWith(params, 'vertical-3-tier')
    render('vertical-3-tier', onConfirm)
    act(() => button('字幕位置を余白内に動かす').click())
    expect(button('字幕位置を余白内に動かす').getAttribute('aria-pressed')).toBe('true')
    act(() => button('新しいJobを作成').click())
    act(() => button('作成する').click())
    expect(onConfirm).toHaveBeenLastCalledWith(params, 'vertical-3-tier', { subtitleIntoMargin: true })
    render('explainer', onConfirm)
    expect(button('字幕位置を余白内に動かす')).toBeUndefined()
  })
})
