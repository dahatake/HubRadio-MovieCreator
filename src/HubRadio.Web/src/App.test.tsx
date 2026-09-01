import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

import App from './App'
import { ComparisonStateProvider } from './features/compare'
import { DeliveryStateProvider } from './features/delivery'
import { LayoutStateProvider } from './features/layout'
import { PreviewModeProvider } from './features/preview-modes'
import { ProjectStateProvider } from './features/projects/ProjectStateProvider'
import { ReviewStateProvider } from './features/review'
import { AppStateProvider } from './state'

describe('App', () => {
  it('renders the initial host synchronization status and action', () => {
    const markup = renderToStaticMarkup(
      <AppStateProvider>
        <ProjectStateProvider>
          <PreviewModeProvider>
            <ComparisonStateProvider>
              <ReviewStateProvider>
                <DeliveryStateProvider>
                  <LayoutStateProvider>
                    <App />
                  </LayoutStateProvider>
                </DeliveryStateProvider>
              </ReviewStateProvider>
            </ComparisonStateProvider>
          </PreviewModeProvider>
        </ProjectStateProvider>
      </AppStateProvider>,
    )

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
    expect(markup).toContain('プレイヤーで正確なフレームを選択してください')
    expect(markup).toContain('<form')
    expect(markup).toContain('for="job-id"')
    expect(markup).toContain('id="job-id"')
    expect(markup).toContain('type="submit"')
    expect(markup).not.toContain('tabindex="-1"')
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
})
