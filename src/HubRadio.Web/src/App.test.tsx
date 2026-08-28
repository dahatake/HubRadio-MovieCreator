import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

import App from './App'
import { AppStateProvider } from './state'

describe('App', () => {
  it('renders the initial host synchronization status and action', () => {
    const markup = renderToStaticMarkup(
      <AppStateProvider>
        <App />
      </AppStateProvider>,
    )

    expect(markup).toContain('Movie Creator')
    expect(markup).toContain('ホスト接続は未確認です')
    expect(markup).toContain('ホスト接続を確認')
  })

  it('requires the application state provider', () => {
    expect(() => renderToStaticMarkup(<App />)).toThrow(
      'useAppState must be used within an AppStateProvider.',
    )
  })
})
