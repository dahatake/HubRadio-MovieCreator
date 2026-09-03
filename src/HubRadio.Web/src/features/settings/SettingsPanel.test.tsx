// @vitest-environment jsdom

import { fireEvent } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { SettingsPanel } from './SettingsPanel'
import { SettingsStateProvider } from './SettingsStateProvider'
import type { SettingsBridgeClient } from './settingsBridgeSession'
import type { SettingsRequest, SettingsResponse, SettingsSnapshot } from './settingsProtocol'

const projectId = '11112222-3333-4444-8555-666677778888'
const digest = 'a'.repeat(64)
const snapshot: SettingsSnapshot = {
  projectId, profileId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', profileName: 'test', profileVersion: '1.0.0', profileDigest: digest, initialSnapshotDigest: digest, snapshotDigest: digest, revision: 0, updatedAt: '2026-09-03T00:00:00.0000000+00:00', definitionCount: 1,
  definitions: [{ key: 'subtitles.enabled', group: 'subtitlesAndGraphics', isAdvanced: true, displayName: 'Subtitles', description: 'Enable subtitles.', unit: 'none', defaultValue: { kind: 'boolean', booleanValue: false }, minimum: null, maximum: null, allowedTextValues: [], evidence: [{ kind: 'productDecision', sourceId: 'productDecision' }] }],
  initialValues: [{ key: 'subtitles.enabled', value: { kind: 'boolean', booleanValue: false } }],
  values: [{ key: 'subtitles.enabled', value: { kind: 'boolean', booleanValue: true } }],
}

describe('SettingsPanel', () => {
  it('renders an accessible project loader and private-safe initial state', () => {
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn(), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn(() => vi.fn()),
    }
    const markup = renderToStaticMarkup(<SettingsStateProvider createClient={() => client}><SettingsPanel /></SettingsStateProvider>)
    expect(markup).toContain('<h2 id="settings-title">案件設定</h2>')
    expect(markup).toContain('for="settings-project-id"')
    expect(markup).toContain('id="settings-project-id"')
    expect(markup).toContain('type="submit"')
    expect(markup).not.toContain('actorSid')
    expect(markup).not.toContain('sourcePath')
  })

  it('shows fixed groups and values, supports group keys, and expands advanced settings', async () => {
    const user = userEvent.setup()
    let sent: SettingsRequest | null = null
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn((request) => { sent = request }), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    await act(async () => { root.render(<SettingsStateProvider createClient={() => client}><SettingsPanel /></SettingsStateProvider>) })
    const input = container.querySelector<HTMLInputElement>('#settings-project-id')!
    await act(async () => { fireEvent.change(input, { target: { value: projectId } }) })
    await act(async () => { fireEvent.submit(input.closest('form')!) })
    expect(sent).not.toBeNull()
    const request = sent as unknown as SettingsRequest
    const response: SettingsResponse = { version: 1, requestId: request.requestId, method: 'settings.read', result: { status: 'available', snapshot } }
    await act(async () => { listener!(response) })

    const tabs = Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]'))
    expect(tabs.map(tab => tab.textContent)).toEqual(['案件と出力', '編集', '字幕とグラフィック', '音声と素材', 'プライバシーと保存'])
    tabs[0]!.focus()
    await user.keyboard('{End}')
    expect(document.activeElement).toBe(tabs[4])
    await user.keyboard('{Home}')
    expect(document.activeElement).toBe(tabs[0])
    await user.keyboard('{ArrowLeft}')
    expect(document.activeElement).toBe(tabs[4])
    await user.keyboard('{ArrowDown}')
    expect(document.activeElement).toBe(tabs[0])
    await user.keyboard('{ArrowUp}')
    expect(document.activeElement).toBe(tabs[4])
    await user.keyboard('{ArrowRight}')
    expect(document.activeElement).toBe(tabs[0])
    input.focus()
    await user.tab()
    expect(document.activeElement).toBe(input.closest('form')!.querySelector<HTMLButtonElement>('button[type="submit"]'))
    await user.tab({ shift: true })
    expect(document.activeElement).toBe(input)
    const advanced = Array.from(container.querySelectorAll('button')).find(button => button.textContent === '詳細設定を表示')!
    expect(advanced.getAttribute('aria-expanded')).toBe('false')
    advanced.focus()
    await user.keyboard('{Enter}')
    expect(advanced.getAttribute('aria-expanded')).toBe('true')
    await user.keyboard(' ')
    expect(advanced.getAttribute('aria-expanded')).toBe('false')
    await user.click(advanced)
    await user.click(tabs[2]!)
    expect(tabs[2]!.getAttribute('aria-selected')).toBe('true')
    expect(container.textContent).toContain('現在値')
    expect(container.textContent).toContain('案件開始時の初期値')
    expect(container.textContent).toContain('プロファイル既定値')
    expect(Array.from(container.querySelectorAll('.setting-value-summary dd')).map(item => item.textContent)).toEqual(['有効', '無効', '無効'])
    expect(container.textContent).toContain('Subtitles')
    expect(container.textContent).toContain('設定を読み込みました。')
    const evidence = container.querySelector<HTMLDetailsElement>('details')!
    const evidenceSummary = evidence.querySelector<HTMLElement>('summary')!
    evidence.open = true
    evidenceSummary.focus()
    await act(async () => { fireEvent.keyDown(evidenceSummary, { key: 'Escape' }) })
    expect(evidence.open).toBe(false)
    expect(document.activeElement).toBe(evidenceSummary)
    await act(async () => { root.unmount() })
    container.remove()
  })

  it('sends update, reset, and reset-all with the current snapshot identity', async () => {
    const user = userEvent.setup()
    let sent: SettingsRequest | null = null
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn((request) => { sent = request }), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    await act(async () => { root.render(<SettingsStateProvider createClient={() => client}><SettingsPanel /></SettingsStateProvider>) })
    const input = container.querySelector<HTMLInputElement>('#settings-project-id')!
    await user.type(input, projectId)
    await user.click(container.querySelector<HTMLButtonElement>('button[type="submit"]')!)
    const loadRequest = sent as unknown as SettingsRequest
    await act(async () => { listener!({ version: 1, requestId: loadRequest.requestId, method: 'settings.read', result: { status: 'available', snapshot } }) })
    await user.click(Array.from(container.querySelectorAll<HTMLButtonElement>('[role="tab"]'))[2]!)
    await user.click(Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === '詳細設定を表示')!)

    const resetButton = Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === '案件開始時の値へ戻す')!
    await user.click(resetButton)
    const resetRequest = sent as unknown as SettingsRequest
    expect(resetRequest.method).toBe('settings.reset')
    expect(resetRequest.params).toMatchObject({ projectId, expectedRevision: 0, key: 'subtitles.enabled' })
    const resetSnapshot = withCurrent(false, 1, 'b')
    await act(async () => { listener!({ version: 1, requestId: resetRequest.requestId, method: 'settings.reset', result: { status: 'available', snapshot: resetSnapshot } }) })
    expect(container.querySelector('[role="status"]')!.textContent).toContain('案件開始時の値へ戻しました。')

    const valueControl = container.querySelector<HTMLSelectElement>('#setting-subtitles-enabled')!
    await user.selectOptions(valueControl, 'true')
    await user.click(Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === '保存')!)
    const updateRequest = sent as unknown as SettingsRequest
    expect(updateRequest.method).toBe('settings.update')
    expect(updateRequest.params).toMatchObject({ projectId, expectedRevision: 1, key: 'subtitles.enabled', value: { kind: 'boolean', booleanValue: true } })
    const updatedSnapshot = withCurrent(true, 2, 'c')
    await act(async () => { listener!({ version: 1, requestId: updateRequest.requestId, method: 'settings.update', result: { status: 'available', snapshot: updatedSnapshot } }) })
    expect(container.querySelector('[role="status"]')!.textContent).toContain('現在値を保存しました。')

    await user.click(Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === '案件開始時の値へすべて戻す')!)
    const resetAllRequest = sent as unknown as SettingsRequest
    expect(resetAllRequest.method).toBe('settings.resetAll')
    expect(resetAllRequest.params).toMatchObject({ projectId, expectedRevision: 2, expectedSnapshotDigest: updatedSnapshot.snapshotDigest })
    await act(async () => { listener!({ version: 1, requestId: resetAllRequest.requestId, method: 'settings.resetAll', result: { status: 'available', snapshot: withCurrent(false, 3, 'a') } }) })
    expect(container.querySelector('[role="status"]')!.textContent).toContain('すべての設定を案件開始時の値へ戻しました。')
    await act(async () => { root.unmount() })
    container.remove()
  })

  it('announces a conflict as an alert and requires refresh', async () => {
    const user = userEvent.setup()
    let sent: SettingsRequest | null = null
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn((request) => { sent = request }), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    await act(async () => { root.render(<SettingsStateProvider createClient={() => client}><SettingsPanel /></SettingsStateProvider>) })
    const input = container.querySelector<HTMLInputElement>('#settings-project-id')!
    await user.type(input, projectId)
    await user.click(container.querySelector<HTMLButtonElement>('button[type="submit"]')!)
    const request = sent as unknown as SettingsRequest
    await act(async () => { listener!({ version: 1, requestId: request.requestId, method: 'settings.read', result: { status: 'conflict', snapshot: null } }) })

    expect(container.querySelector('[role="alert"]')!.textContent).toContain('再読み込みしてください。')
    expect(container.querySelector('[role="status"]')!.textContent).toContain('変更を続けるには設定を再読み込みしてください。')
    await act(async () => { root.unmount() })
    container.remove()
  })

  it('keeps a newly entered project when the abandoned project responds late', async () => {
    let sent: SettingsRequest | null = null
    let listener: ((response: SettingsResponse) => void) | null = null
    const client: SettingsBridgeClient = {
      sendSettings: vi.fn((request) => { sent = request }), abandonSettingsRequest: vi.fn(), dispose: vi.fn(),
      onSettingsResponse: vi.fn((next) => { listener = next; return vi.fn() }),
    }
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    await act(async () => { root.render(<SettingsStateProvider createClient={() => client}><SettingsPanel /></SettingsStateProvider>) })
    const input = container.querySelector<HTMLInputElement>('#settings-project-id')!
    await act(async () => { fireEvent.change(input, { target: { value: projectId } }); fireEvent.submit(input.closest('form')!) })
    const abandoned = sent as unknown as SettingsRequest
    const nextProject = '22223333-4444-4555-8666-777788889999'
    await act(async () => { fireEvent.change(input, { target: { value: nextProject } }) })
    await act(async () => { listener!({ version: 1, requestId: abandoned.requestId, method: 'settings.read', result: { status: 'available', snapshot } }) })

    expect(client.abandonSettingsRequest).toHaveBeenCalledWith(abandoned.requestId)
    expect(input.value).toBe(nextProject)
    expect(container.textContent).not.toContain('1 項目')
    await act(async () => { root.unmount() })
    container.remove()
  })
})

function withCurrent(value: boolean, revision: number, digestPrefix: string): SettingsSnapshot {
  return {
    ...snapshot,
    revision,
    snapshotDigest: digestPrefix.repeat(64),
    values: [{ key: 'subtitles.enabled', value: { kind: 'boolean', booleanValue: value } }],
  }
}
