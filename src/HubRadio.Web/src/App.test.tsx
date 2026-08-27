import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

import App from './App'

describe('App', () => {
  it('renders the application-ready status', () => {
    const markup = renderToStaticMarkup(<App />)

    expect(markup).toContain('Movie Creator')
    expect(markup).toContain('UI の準備ができました')
  })
})
