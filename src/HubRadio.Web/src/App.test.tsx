// @vitest-environment jsdom

import { act, StrictMode } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import App from './App'
import type { BridgeMessageEvent, BridgeTransport } from './bridge'
import { ComparisonStateProvider } from './features/compare'
import { DeliveryStateProvider } from './features/delivery'
import { LayoutStateProvider } from './features/layout'
import { PreviewModeProvider } from './features/preview-modes'
import { ProjectStateProvider } from './features/projects/ProjectStateProvider'
import { ReviewStateProvider } from './features/review'
import { SettingsStateProvider } from './features/settings'
import { AppStateProvider } from './state'

describe('App', () => {
  it('renders the initial host synchronization status and action', () => {
    const markup = renderToStaticMarkup(appTree())

    expect(markup).toContain('Movie Creator')
    expect(markup).toContain('ホスト接続は未確認です')
    expect(markup).toContain('ホスト接続を確認')
    expect(markup).toContain('案件一覧')
    expect(markup).toContain('新しい案件')
    expect(markup).toContain('ジョブの進行状況')
    expect(markup).toContain('プレビューモード')
    expect(markup).toContain('動画・媒体のプレビュー根拠がありません')
    expect(markup).toContain('文字起こし編集')
    expect(markup).toContain('編集タイムライン')
    expect(markup).toContain('Canvas は表示専用')
    expect(markup).toContain('縦型レイアウト・字幕・テロップ')
    expect(markup).toContain('レイアウトは未選択です')
    expect(markup).toContain('構成版の比較')
    expect(markup).toContain('比較結果はまだありません')
    expect(markup).toContain('レビュー・コメント・承認')
    expect(markup).toContain('レビュー対象は未選択です')
    expect(markup).toContain('手動サムネイル生成')
    expect(markup).toContain('案件設定')
    expect(markup).toContain('プレイヤーで正確なフレームを選択してください')
    expect(markup).toContain('<form')
    expect(markup).toContain('for="job-id"')
    expect(markup).toContain('id="job-id"')
    expect(markup).toContain('type="submit"')
    expect(markup).toContain(
      '<a class="skip-link" href="#project-content">メインコンテンツへ移動</a>',
    )
    expect(markup).toContain(
      '<div id="project-content" tabindex="-1">',
    )
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
                  <SettingsStateProvider>
                    <App />
                  </SettingsStateProvider>
                </LayoutStateProvider>
              </DeliveryStateProvider>
            </ReviewStateProvider>
          </ComparisonStateProvider>
        </PreviewModeProvider>
      </ProjectStateProvider>
    </AppStateProvider>
  )
}
