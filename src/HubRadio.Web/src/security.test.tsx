import { readFileSync } from 'node:fs'
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

  it('keeps the job UI free of dynamic HTML and script sinks', () => {
    const source = readFileSync(
      new URL('./features/jobs/JobProgressPanel.tsx', import.meta.url),
      'utf8',
    )

    expect(source).not.toContain('dangerouslySetInnerHTML')
    expect(source).not.toContain('.innerHTML')
    expect(source).not.toMatch(/\beval\s*\(/)
    expect(source).not.toContain('new Function')
  })
})