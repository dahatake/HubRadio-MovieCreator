import { createHash, randomUUID } from 'node:crypto'
import {
  lstat,
  readFile,
  readdir,
  rename,
  rm,
  writeFile,
} from 'node:fs/promises'
import { dirname, extname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const manifestName = 'asset-manifest.json'
const modulePath = fileURLToPath(import.meta.url)
const scriptDirectory = dirname(modulePath)
const webRoot = resolve(scriptDirectory, '..')
const defaultDistRoot = join(webRoot, 'dist')
const allowedAssetExtensions = new Set([
  '.css',
  '.js',
  '.jpeg',
  '.jpg',
  '.png',
  '.svg',
  '.webp',
  '.woff',
  '.woff2',
])

const compareOrdinal = (left, right) => left < right ? -1 : left > right ? 1 : 0

const fail = (message) => {
  throw new Error(`Production Web asset verification failed: ${message}`)
}

const toPortablePath = (path) => path.split(sep).join('/')

const decodeUtf8 = (bytes, context) => {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch {
    return fail(`${context} is not valid UTF-8.`)
  }
}

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

const enumerateFiles = async (directory) => {
  const result = []
  const entries = await readdir(directory, { withFileTypes: true })
  entries.sort((left, right) => compareOrdinal(left.name, right.name))

  for (const entry of entries) {
    const fullPath = join(directory, entry.name)
    const information = await lstat(fullPath)
    if (information.isSymbolicLink()) {
      fail('symbolic links are not allowed in the production bundle.')
    }

    if (information.isDirectory()) {
      result.push(...await enumerateFiles(fullPath))
      continue
    }

    if (!information.isFile()) {
      fail('only regular files and directories are allowed in the production bundle.')
    }

    result.push(fullPath)
  }

  return result
}

const validatePortableRelativePath = (value) => {
  if (
    value.length === 0 ||
    value.startsWith('/') ||
    value.includes('\\') ||
    value.includes(':') ||
    value.split('/').some((segment) =>
      segment.length === 0 || segment === '.' || segment === '..')
  ) {
    fail('an asset path is not a safe portable relative path.')
  }
}

const readClosedCsp = (indexHtml) => {
  const match = indexHtml.match(
    /<meta\s+[^>]*http-equiv="Content-Security-Policy"[^>]*content="([^"]+)"[^>]*>/u,
  )
  if (match === null) {
    fail('index.html does not contain the required Content-Security-Policy meta element.')
  }

  const actual = new Map()
  for (const rawDirective of match[1].split(';')) {
    const directive = rawDirective.trim()
    if (directive.length === 0) {
      continue
    }

    const [name, ...values] = directive.split(/\s+/u)
    if (actual.has(name)) {
      fail('the Content-Security-Policy contains a duplicate directive.')
    }

    actual.set(name, values.join(' '))
  }

  const expected = new Map([
    ['default-src', "'none'"],
    ['script-src', "'self'"],
    ['style-src', "'self'"],
    ['img-src', "'self' data:"],
    ['connect-src', "'none'"],
    ['font-src', "'self'"],
    ['media-src', "'none'"],
    ['object-src', "'none'"],
    ['base-uri', "'none'"],
    ['form-action', "'none'"],
    ['frame-src', "'none'"],
    ['worker-src', "'none'"],
  ])
  if (
    actual.size !== expected.size ||
    [...expected].some(([name, value]) => actual.get(name) !== value)
  ) {
    fail('the Content-Security-Policy is not the closed bundled-asset policy.')
  }
}

const validateEntryPoint = (indexHtml, assetPaths) => {
  if (/<base\b/iu.test(indexHtml)) {
    fail('index.html must not override its relative URL base.')
  }

  readClosedCsp(indexHtml)
  for (const match of indexHtml.matchAll(/\b(?:src|href)="([^"]+)"/giu)) {
    if (!/^\.\/assets\/[A-Za-z0-9._-]+\.(?:css|js)$/u.test(match[1])) {
      fail('index.html contains a non-local or unsupported resource reference.')
    }
  }

  const scripts = [...indexHtml.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/giu)]
  if (scripts.length !== 1) {
    fail('index.html must contain exactly one external module script.')
  }

  const scriptAttributes = scripts[0][1]
  if (scripts[0][2].trim().length !== 0 || !/\btype="module"/u.test(scriptAttributes)) {
    fail('inline or non-module scripts are not allowed.')
  }

  const scriptSource = scriptAttributes.match(/\bsrc="([^"]+)"/u)?.[1]
  const styleReferences = [...indexHtml.matchAll(
    /<link\b(?=[^>]*\brel="stylesheet")(?=[^>]*\bhref="([^"]+)")[^>]*>/giu,
  )].map((match) => match[1])
  const references = [scriptSource, ...styleReferences]
  if (scriptSource === undefined || styleReferences.length === 0) {
    fail('index.html must reference a script and stylesheet.')
  }

  for (const reference of references) {
    if (!/^\.\/assets\/[A-Za-z0-9._-]+\.(?:css|js)$/u.test(reference)) {
      fail('index.html contains a non-local or unsupported asset reference.')
    }

    const target = reference.slice(2)
    if (!assetPaths.has(target)) {
      fail('index.html references a missing asset.')
    }
  }
}

const validateTextAsset = (portablePath, bytes) => {
  const extension = extname(portablePath).toLowerCase()
  if (extension !== '.css' && extension !== '.js' && extension !== '.svg') {
    return
  }

  const text = decodeUtf8(bytes, portablePath)
  if (/sourceMappingURL\s*=/iu.test(text)) {
    fail('source map references must not be included in the production bundle.')
  }

  if (extension === '.css' &&
      (/@import\b/iu.test(text) || /url\(\s*["']?(?:https?:)?\/\//iu.test(text))) {
    fail('CSS must not import or reference remote assets.')
  }

  if (extension === '.svg' &&
      (/<(?:script|foreignObject)\b/iu.test(text) ||
       /\b(?:href|src)=["'](?:https?:)?\/\//iu.test(text))) {
    fail('SVG assets must not contain active content or remote references.')
  }
}

export const verifyProductionAssets = async (distRoot) => {
  const distInformation = await lstat(distRoot)
  if (!distInformation.isDirectory() || distInformation.isSymbolicLink()) {
    fail('dist must be a regular directory.')
  }

  await rm(join(distRoot, manifestName), { force: true })
  const files = await enumerateFiles(distRoot)
  const assets = []
  for (const fullPath of files) {
    const portablePath = toPortablePath(relative(distRoot, fullPath))
    validatePortableRelativePath(portablePath)
    if (portablePath !== 'index.html') {
      if (!portablePath.startsWith('assets/') ||
          !allowedAssetExtensions.has(extname(portablePath).toLowerCase())) {
        fail('the production bundle contains an unsupported file.')
      }
    }

    if (portablePath.toLowerCase().endsWith('.map')) {
      fail('source maps must not be included in the production bundle.')
    }

    const bytes = await readFile(fullPath)
  validateTextAsset(portablePath, bytes)
    assets.push({
      path: portablePath,
      byteSize: bytes.byteLength,
      sha256: sha256(bytes),
    })
  }

  assets.sort((left, right) => compareOrdinal(left.path, right.path))
  const assetPaths = new Set(assets.map((asset) => asset.path))
  if (!assetPaths.has('index.html') ||
      ![...assetPaths].some((path) => path.endsWith('.js')) ||
      ![...assetPaths].some((path) => path.endsWith('.css'))) {
    fail('the production bundle is incomplete.')
  }

  const indexBytes = await readFile(join(distRoot, 'index.html'))
  validateEntryPoint(decodeUtf8(indexBytes, 'index.html'), assetPaths)

  const manifest = {
    schemaVersion: '1.0.0',
    entryPoint: 'index.html',
    sourceMapsIncluded: false,
    remoteAssetsAllowed: false,
    assets,
  }
  const manifestPath = join(distRoot, manifestName)
  const temporaryManifestPath = join(
    distRoot,
    `.${manifestName}.${randomUUID()}.partial`,
  )
  try {
    await writeFile(
      temporaryManifestPath,
      `${JSON.stringify(manifest, null, 2)}\n`,
      { encoding: 'utf8', flag: 'wx' },
    )
    await rename(temporaryManifestPath, manifestPath)
  } finally {
    await rm(temporaryManifestPath, { force: true })
  }

  process.stdout.write(`Verified ${assets.length} production Web assets.\n`)
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === modulePath) {
  const argumentsAfterScript = process.argv.slice(2)
  if (argumentsAfterScript.length > 1) {
    fail('at most one explicit dist directory may be supplied.')
  }

  await verifyProductionAssets(
    argumentsAfterScript.length === 0
      ? defaultDistRoot
      : resolve(argumentsAfterScript[0]),
  )
}