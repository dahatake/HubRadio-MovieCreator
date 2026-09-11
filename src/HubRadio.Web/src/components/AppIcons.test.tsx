import { createHash } from 'node:crypto'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

import { AppIcon, type AppIconName } from './AppIcons'

// GitHub-reported original SVG blob SHA-1s at commit
// 5bae3fb7771054c252a54b1d9210e9c03439fa1b; not SHA-256s of our JSX.
const iconCases = [
  { name: 'video', label: '元動画', blobSha1: '04f29c5d88569dcc0efe39e555724d3a25e3acf1' },
  { name: 'processing', label: '自動編集', blobSha1: 'ba11497b00c12a394dc3d15092b4927b9cade4ba' },
  { name: 'play', label: '仕上がり', blobSha1: '05a0ec3f98ee72567238bfeee1ffa980553d8b0b' },
  { name: 'save', label: '保存', blobSha1: '4df2b9d636fdaa2635ebcb825acdde439043442c' },
  { name: 'settings', label: '設定', blobSha1: '54752c20fe6c380b1f2b5bcbef8738295e2cc1d0' },
  { name: 'arrowLeft', label: '戻る', blobSha1: '32618604e0ece64de2688e01ce14c2959a25c0c3' },
] as const satisfies ReadonlyArray<{
  name: AppIconName
  label: string
  blobSha1: string
}>

describe('AppIcon', () => {
  it.each(iconCases)('renders $name as a decorative currentColor SVG', ({ name }) => {
    const markup = renderToStaticMarkup(<AppIcon name={name} />)

    expect(markup).toMatch(/^<svg\b/)
    expect(markup).toContain('viewBox="0 0 20 20"')
    expect(markup).toContain('width="20"')
    expect(markup).toContain('height="20"')
    expect(markup).toContain('aria-hidden="true"')
    expect(markup).toContain('focusable="false"')
    expect(markup.match(/fill="[^"]*"/g)).toEqual(['fill="currentColor"'])
    expect(markup.match(/<path\b/g)).toHaveLength(1)
    expect(markup).not.toMatch(/<title|<desc|<text|role=|aria-label|tabindex=/i)
    expect(markup).not.toMatch(/<script|<foreignObject|<image|<use|href=|style=/i)
  })

  it.each(iconCases)('retains the pinned upstream geometry for $name', ({ name, blobSha1 }) => {
    const markup = renderToStaticMarkup(<AppIcon name={name} />)
    const path = /<path d="([^"]+)"><\/path>/.exec(markup)?.[1]
    expect(path).toBeDefined()

    // Reconstruct the fetched, three-line upstream SVG envelope only for hashing.
    // Restore its original fill and LF endings; never insert this string as HTML.
    const upstreamSvg =
      '<svg width="20" height="20" viewBox="0 0 20 20" fill="none" xmlns="http://www.w3.org/2000/svg">\n' +
      `<path d="${path}" fill="#212121"/>\n</svg>\n`
    const actualBlobSha1 = createHash('sha1')
      .update(`blob ${Buffer.byteLength(upstreamSvg, 'utf8')}\0`)
      .update(upstreamSvg, 'utf8')
      .digest('hex')

    expect(actualBlobSha1).toBe(blobSha1)
  })

  it.each([16, 24, 32])('uses size %i without changing the viewBox', (size) => {
    const markup = renderToStaticMarkup(<AppIcon name="play" size={size} />)

    expect(markup).toContain(`width="${size}"`)
    expect(markup).toContain(`height="${size}"`)
    expect(markup).toContain('viewBox="0 0 20 20"')
    expect(markup).toContain('aria-hidden="true"')
    expect(markup).toContain('focusable="false"')
  })

  it.each(iconCases)('leaves the visible $name label to the caller', ({ name, label }) => {
    const markup = renderToStaticMarkup(
      <button type="button">
        <AppIcon name={name} />
        <span>{label}</span>
      </button>,
    )

    expect(markup).toMatch(/^<button type="button"><svg\b/)
    expect(markup).toContain(`</svg><span>${label}</span></button>`)
    expect(markup).not.toContain('aria-label=')
  })
})