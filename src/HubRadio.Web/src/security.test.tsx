import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

describe('web text security boundary', () => {
  it('renders adversarial markup as escaped text', () => {
    const payload =
      '<script>globalThis.compromised=true</script><img src="x" onerror="alert(1)">&tail'

    const markup = renderToStaticMarkup(<p>{payload}</p>)

    expect(markup).toContain('&lt;script&gt;')
    expect(markup).toContain('&lt;img')
    expect(markup).toContain('&amp;tail')
    expect(markup).not.toContain('<script')
    expect(markup).not.toContain('<img')
  })
})