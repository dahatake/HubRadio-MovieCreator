// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import { act, StrictMode } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import App from './App'
import type { BridgeMessageEvent, BridgeTransport } from './bridge'
import { ComparisonStateProvider } from './features/compare'
import { DeliveryStateProvider } from './features/delivery'
import { LayoutStateProvider } from './features/layout'
import { PlayerNavigationProvider } from './features/player'
import { PreviewModeProvider } from './features/preview-modes'
import { ProjectStateProvider } from './features/projects/ProjectStateProvider'
import { ReviewStateProvider } from './features/review'
import { SettingsWorkspaceStateProvider } from './features/settings'
import { AppStateProvider } from './state'

describe('App', () => {
  it('renders a compact host status and one of four workflow screens without raw ID forms', () => {
    const markup = renderToStaticMarkup(appTree())
    const container = document.createElement('div')
    container.innerHTML = markup
    document.body.append(container)
    try {
      expect(markup).toContain('Movie Creator')
      expect(markup).toContain('ホスト接続は未確認です')
      expect(within(container).getAllByRole('tab').map(tab => tab.textContent))
        .toEqual(['元動画', '自動編集', '仕上がり', '保存'])
      expect(within(container).getAllByRole('tabpanel')).toHaveLength(1)
      expect(within(container).getByRole('tabpanel', { name: '元動画' }).id)
        .toBe('workflow-panel-source')
      expect(within(container).getByRole('heading', { name: '案件一覧' })).toBeTruthy()
      expect(within(container).getByRole('button', { name: '新しい案件を作成' })).toBeTruthy()
      expect(within(container).getByRole('button', { name: '設定' })).toBeTruthy()
      expect(within(container).queryByRole('heading', { name: 'ジョブの進行状況' })).toBeNull()
      expect(within(container).queryByRole('heading', { name: '文字起こし編集' })).toBeNull()
      expect(container.querySelector('.workflow-connection button')).toBeNull()
      expect(container.querySelector('form, #job-id, #player-job-id, #timeline-project-id, #review-version-id, #delivery-version-id')).toBeNull()
      expect(markup).toContain(
        '<a class="skip-link" href="#project-content">メインコンテンツへ移動</a>',
      )
      expect(container.querySelector<HTMLElement>('#project-content')?.tabIndex).toBe(-1)
    } finally {
      container.remove()
    }
  })

  it('requires the application state provider', () => {
    expect(() => renderToStaticMarkup(<App />)).toThrow(
      'useAppState must be used within an AppStateProvider.',
    )
  })

  it('requires the project state provider', () => {
    expect(() =>
      renderToStaticMarkup(
        <AppStateProvider>
          <PreviewModeProvider>
            <App />
          </PreviewModeProvider>
        </AppStateProvider>,
      ),
    ).toThrow('useProjectState must be used within a ProjectStateProvider.')
  })

  it('requires the review state provider', () => {
    expect(() =>
      renderToStaticMarkup(
        <AppStateProvider>
          <ProjectStateProvider>
            <PreviewModeProvider>
              <ComparisonStateProvider>
                <DeliveryStateProvider>
                  <LayoutStateProvider>
                    <App />
                  </LayoutStateProvider>
                </DeliveryStateProvider>
              </ComparisonStateProvider>
            </PreviewModeProvider>
          </ProjectStateProvider>
        </AppStateProvider>,
      ),
    ).toThrow('useReviewState must be used within a ReviewStateProvider.')
  })

  it('requires the comparison state provider', () => {
    expect(() =>
      renderToStaticMarkup(
        <AppStateProvider>
          <ProjectStateProvider>
            <PreviewModeProvider>
              <ReviewStateProvider>
                <DeliveryStateProvider>
                  <LayoutStateProvider>
                    <App />
                  </LayoutStateProvider>
                </DeliveryStateProvider>
              </ReviewStateProvider>
            </PreviewModeProvider>
          </ProjectStateProvider>
        </AppStateProvider>,
      ),
    ).toThrow(
      'useComparisonState must be used within a ComparisonStateProvider.',
    )
  })

  it('requires the delivery state provider', () => {
    expect(() =>
      renderToStaticMarkup(
        <AppStateProvider>
          <ProjectStateProvider>
            <PreviewModeProvider>
              <ComparisonStateProvider>
                <ReviewStateProvider>
                  <LayoutStateProvider>
                    <App />
                  </LayoutStateProvider>
                </ReviewStateProvider>
              </ComparisonStateProvider>
            </PreviewModeProvider>
          </ProjectStateProvider>
        </AppStateProvider>,
      ),
    ).toThrow('useDeliveryState must be used within a DeliveryStateProvider.')
  })

  it('requires the layout state provider', () => {
    expect(() =>
      renderToStaticMarkup(
        <AppStateProvider>
          <ProjectStateProvider>
            <PreviewModeProvider>
              <ComparisonStateProvider>
                <ReviewStateProvider>
                  <DeliveryStateProvider>
                    <App />
                  </DeliveryStateProvider>
                </ReviewStateProvider>
              </ComparisonStateProvider>
            </PreviewModeProvider>
          </ProjectStateProvider>
        </AppStateProvider>,
      ),
    ).toThrow('useLayoutState must be used within a LayoutStateProvider.')
  })

  it('requires the preview mode provider', () => {
    expect(() =>
      renderToStaticMarkup(
        <AppStateProvider>
          <App />
        </AppStateProvider>,
      ),
    ).toThrow(
      'usePreviewModes must be used within a PreviewModeProvider.',
    )
  })

  it('requires the workspace settings provider, not the legacy settings provider', () => {
    expect(() => renderToStaticMarkup(
      <AppStateProvider>
        <ProjectStateProvider>
          <PreviewModeProvider>
            <PlayerNavigationProvider>
              <ComparisonStateProvider>
                <ReviewStateProvider>
                  <DeliveryStateProvider>
                    <LayoutStateProvider><App /></LayoutStateProvider>
                  </DeliveryStateProvider>
                </ReviewStateProvider>
              </ComparisonStateProvider>
            </PlayerNavigationProvider>
          </PreviewModeProvider>
        </ProjectStateProvider>
      </AppStateProvider>,
    )).toThrow('useSettingsWorkspace must be used within SettingsWorkspaceStateProvider.')
  })

  describe('startup handshake', () => {
    let container: HTMLDivElement
    let root: Root
    let transport: BridgeTransport
    let requests: Array<Parameters<BridgeTransport['postMessage']>[0]>
    let listeners: Set<(event: BridgeMessageEvent) => void>

    beforeEach(() => {
      vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
      container = document.createElement('div')
      document.body.append(container)
      root = createRoot(container)
      requests = []
      listeners = new Set()
      transport = {
        postMessage(message) {
          if (message.method === 'bridge.ping') {
            expect(container.querySelector('#app-title')?.textContent)
              .toBe('Movie Creator')
            expect(listeners.size).toBeGreaterThan(0)
          }
          requests.push(message)
        },
        addEventListener(_type, listener) { listeners.add(listener) },
        removeEventListener(_type, listener) { listeners.delete(listener) },
      }
      vi.stubGlobal('chrome', { webview: transport })
      vi.spyOn(window.crypto, 'randomUUID')
        .mockReturnValue('00112233-4455-4677-8899-aabbccddeeff')
    })

    afterEach(() => {
      act(() => root.unmount())
      container.remove()
      vi.restoreAllMocks()
      vi.unstubAllGlobals()
    })

    it.each([false, true])(
      'automatically pings once after mount and waits for a correlated response (StrictMode=%s)',
      async (strictMode) => {
        await act(async () => {
          root.render(strictMode ? <StrictMode>{appTree()}</StrictMode> : appTree())
        })

        const pings = requests.filter(request => request.method === 'bridge.ping')
        expect(pings).toHaveLength(1)
        expect(pings[0]?.params).toEqual({})
        const status = () => container.querySelector('[data-sync-status]')
          ?.getAttribute('data-sync-status')
        expect(status()).toBe('pending')

        const response = {
          version: 1,
          method: 'bridge.ping',
          requestId: pings[0]?.requestId,
          result: { protocolVersion: 1 },
        }
        act(() => {
          for (const listener of listeners) {
            listener({ data: { ...response, requestId: '11111111-1111-4111-8111-111111111111' } })
          }
        })
        expect(status()).toBe('pending')
        act(() => {
          for (const listener of listeners) listener({ data: response })
        })
        expect(status()).toBe('acknowledged')
        expect(requests.filter(request => request.method === 'bridge.ping'))
          .toHaveLength(1)
      },
    )

    it('cancels the queued automatic ping on unmount', async () => {
      act(() => root.render(appTree()))
      act(() => root.unmount())
      await act(async () => { await Promise.resolve() })

      expect(requests.filter(request => request.method === 'bridge.ping'))
        .toHaveLength(0)
      expect(listeners.size).toBe(0)
    })

    it('does not ping when React fails before the initial commit', async () => {
      await expect(act(async () => {
        root.render(<AppStateProvider><App /></AppStateProvider>)
      })).rejects.toThrow('usePreviewModes must be used within a PreviewModeProvider.')

      expect(container.querySelector('main')).toBeNull()
      expect(requests).toHaveLength(0)
    })

    it('reports automatic ping send failure without acknowledging', async () => {
      vi.spyOn(transport, 'postMessage').mockImplementation(() => {
        throw new Error('Web messages unavailable.')
      })
      await act(async () => root.render(appTree()))

      expect(container.querySelector('[data-sync-status]')
        ?.getAttribute('data-sync-status')).toBe('error')
    })

    it('offers retry only after failure and still requires a correlated acknowledgement', async () => {
      const send = vi.spyOn(transport, 'postMessage').mockImplementation(() => {
        throw new Error('Web messages unavailable.')
      })
      await act(async () => root.render(appTree()))
      const retry = within(container).getByRole('button', { name: '接続を再試行' })
      expect(retry.closest('.workflow-header')).not.toBeNull()

      send.mockImplementation(message => { requests.push(message) })
      await act(async () => { retry.click() })
      expect(container.querySelector('[data-sync-status]')?.getAttribute('data-sync-status')).toBe('pending')
      expect(within(container).queryByRole('button', { name: '接続を再試行' })).toBeNull()
      const ping = requests.filter(request => request.method === 'bridge.ping').at(-1)!
      act(() => {
        for (const listener of listeners) listener({ data: {
          version: 1, method: 'bridge.ping', requestId: ping.requestId, result: { protocolVersion: 1 },
        } })
      })
      expect(container.querySelector('[data-sync-status]')?.getAttribute('data-sync-status')).toBe('acknowledged')
      expect(container.querySelector('.workflow-connection button')).toBeNull()
    })
  })
})

function appTree() {
  return (
    <AppStateProvider>
      <ProjectStateProvider>
        <PreviewModeProvider>
          <ComparisonStateProvider>
            <ReviewStateProvider>
              <DeliveryStateProvider>
                <LayoutStateProvider>
                  <PlayerNavigationProvider>
                    <SettingsWorkspaceStateProvider>
                      <App />
                    </SettingsWorkspaceStateProvider>
                  </PlayerNavigationProvider>
                </LayoutStateProvider>
              </DeliveryStateProvider>
            </ReviewStateProvider>
          </ComparisonStateProvider>
        </PreviewModeProvider>
      </ProjectStateProvider>
    </AppStateProvider>
  )
}
