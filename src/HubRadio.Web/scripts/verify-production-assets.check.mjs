import assert from 'node:assert/strict'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'

import { verifyProductionAssets } from './verify-production-assets.mjs'
import { verifyRuntimeDependencies } from './verify-runtime-dependencies.mjs'

const csp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'none'; font-src 'self'; media-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; worker-src 'none'"

const createFixture = async () => {
  const root = await mkdtemp(join(tmpdir(), 'HubRadio.WebAssets.'))
  await mkdir(join(root, 'assets'))
  await writeFile(join(root, 'assets', 'app-123.js'), 'export const value = 1\n')
  await writeFile(join(root, 'assets', 'app-123.css'), ':root { color: #111; }\n')
  await writeFile(
    join(root, 'index.html'),
    `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="${csp}"><script type="module" src="./assets/app-123.js"></script><link rel="stylesheet" href="./assets/app-123.css"></head><body></body></html>`,
  )
  return root
}

const withFixture = async (action) => {
  const root = await createFixture()
  try {
    await action(root)
  } finally {
    await rm(root, { force: true, recursive: true })
  }
}

test('writes a closed manifest for a valid production bundle', async () => {
  await withFixture(async (root) => {
    await verifyProductionAssets(root)
    const manifest = JSON.parse(await readFile(join(root, 'asset-manifest.json'), 'utf8'))
    assert.equal(manifest.schemaVersion, '1.0.0')
    assert.equal(manifest.sourceMapsIncluded, false)
    assert.equal(manifest.remoteAssetsAllowed, false)
    assert.deepEqual(
      manifest.assets.map((asset) => asset.path),
      ['assets/app-123.css', 'assets/app-123.js', 'index.html'],
    )
  })
})

test('rejects a remote entry script', async () => {
  await withFixture(async (root) => {
    const indexPath = join(root, 'index.html')
    const index = await readFile(indexPath, 'utf8')
    await writeFile(
      indexPath,
      index.replace('./assets/app-123.js', 'https://cdn.example/app.js'),
    )
    await assert.rejects(
      verifyProductionAssets(root),
      /non-local or unsupported resource reference/u,
    )
  })
})

test('rejects inline script content', async () => {
  await withFixture(async (root) => {
    const indexPath = join(root, 'index.html')
    const index = await readFile(indexPath, 'utf8')
    await writeFile(indexPath, index.replace(
      '<script type="module" src="./assets/app-123.js"></script>',
      '<script type="module">alert(1)</script>',
    ))
    await assert.rejects(
      verifyProductionAssets(root),
      /inline or non-module scripts/u,
    )
  })
})

test('rejects source maps and unsupported root files', async () => {
  await withFixture(async (root) => {
    await writeFile(join(root, 'assets', 'app-123.js.map'), '{}')
    await assert.rejects(
      verifyProductionAssets(root),
      /unsupported file|source maps/u,
    )
  })

  await withFixture(async (root) => {
    await writeFile(join(root, 'unexpected.txt'), 'stale')
    await assert.rejects(
      verifyProductionAssets(root),
      /unsupported file/u,
    )
  })
})

test('rejects remote CSS and source map references', async () => {
  await withFixture(async (root) => {
    await writeFile(
      join(root, 'assets', 'app-123.css'),
      '@import url("https://cdn.example/theme.css");\n',
    )
    await assert.rejects(
      verifyProductionAssets(root),
      /CSS must not import or reference remote assets/u,
    )
  })

  await withFixture(async (root) => {
    await writeFile(
      join(root, 'assets', 'app-123.js'),
      'export const value = 1\n//# sourceMappingURL=https://cdn.example/app.js.map\n',
    )
    await assert.rejects(
      verifyProductionAssets(root),
      /source map references/u,
    )
  })
})

test('runtime dependency evidence matches the locked production closure', async () => {
  await verifyRuntimeDependencies()
})