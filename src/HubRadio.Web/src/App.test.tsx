import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

import App from './App'
import { ProjectStateProvider } from './features/projects/ProjectStateProvider'
import { AppStateProvider } from './state'

describe('App', () => {
  it('renders the initial host synchronization status and action', () => {
    const markup = renderToStaticMarkup(
      <AppStateProvider>
        <ProjectStateProvider>
          <App />
        </ProjectStateProvider>
      </AppStateProvider>,
    )

    expect(markup).toContain('Movie Creator')
    expect(markup).toContain('ホスト接続は未確認です')
    expect(markup).toContain('ホスト接続を確認')
    expect(markup).toContain('案件一覧')
    expect(markup).toContain('新しい案件')
    expect(markup).toContain('ジョブの進行状況')
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
          <App />
        </AppStateProvider>,
      ),
    ).toThrow('useProjectState must be used within a ProjectStateProvider.')
  })
})
