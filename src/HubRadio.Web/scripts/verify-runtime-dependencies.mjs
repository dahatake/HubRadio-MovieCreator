import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const modulePath = fileURLToPath(import.meta.url)
const scriptDirectory = dirname(modulePath)
const defaultWebRoot = resolve(scriptDirectory, '..')
const defaultManifestPath = resolve(
  defaultWebRoot,
  '..',
  '..',
  'packaging',
  'web',
  'dependency-manifest.json',
)

const fail = (message) => {
  throw new Error(`Runtime dependency verification failed: ${message}`)
}

const exactKeys = (value, expected, context) => {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    fail(`${context} must be an object.`)
  }

  const actual = Object.keys(value).sort()
  const sortedExpected = [...expected].sort()
  if (actual.length !== sortedExpected.length ||
      actual.some((key, index) => key !== sortedExpected[index])) {
    fail(`${context} has missing or unsupported properties.`)
  }
}

const parseJson = async (path, context) => {
  const bytes = await readFile(path)
  let text
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch {
    return fail(`${context} is not valid UTF-8.`)
  }

  try {
    return JSON.parse(text)
  } catch {
    return fail(`${context} is not valid JSON.`)
  }
}

const sha256 = (value) => createHash('sha256').update(value).digest('hex')

const toSha1Hex = (integrity) => {
  if (typeof integrity !== 'string' || !integrity.startsWith('sha1-')) {
    fail('a production package lock entry does not have the recorded SHA-1 integrity form.')
  }

  const bytes = Buffer.from(integrity.slice(5), 'base64')
  if (bytes.length !== 20) {
    fail('a production package lock integrity value is malformed.')
  }

  return bytes.toString('hex')
}

const collectRuntimeClosure = (packageJson, packageLock) => {
  const rootDependencies = packageJson.dependencies
  if (rootDependencies === null || typeof rootDependencies !== 'object' ||
      Array.isArray(rootDependencies)) {
    fail('package.json dependencies are invalid.')
  }

  const packages = packageLock.packages
  if (packages === null || typeof packages !== 'object' || Array.isArray(packages)) {
    fail('package-lock.json packages are invalid.')
  }

  const pending = Object.keys(rootDependencies)
  const closure = new Set()
  while (pending.length > 0) {
    const name = pending.pop()
    if (closure.has(name)) {
      continue
    }

    const locked = packages[`node_modules/${name}`]
    if (locked === undefined || locked.dev === true) {
      fail('a production dependency is missing or marked development-only in package-lock.json.')
    }

    closure.add(name)
    if (locked.dependencies !== undefined) {
      for (const dependency of Object.keys(locked.dependencies)) {
        pending.push(dependency)
      }
    }
  }

  return [...closure].sort()
}

export const verifyRuntimeDependencies = async ({
  webRoot = defaultWebRoot,
  manifestPath = defaultManifestPath,
} = {}) => {
  const packageJson = await parseJson(resolve(webRoot, 'package.json'), 'package.json')
  const packageLock = await parseJson(resolve(webRoot, 'package-lock.json'), 'package-lock.json')
  const manifest = await parseJson(manifestPath, 'dependency-manifest.json')
  exactKeys(
    manifest,
    [
      'schemaVersion',
      'evidenceDate',
      'target',
      'packageManager',
      'releaseApproved',
      'artifactSetSha256',
      'packages',
      'conditions',
    ],
    'dependency-manifest.json',
  )
  if (manifest.schemaVersion !== '1.0.0' ||
      manifest.packageManager !== packageJson.packageManager ||
      manifest.releaseApproved !== false ||
      !Array.isArray(manifest.packages) ||
      !Array.isArray(manifest.conditions) ||
      manifest.conditions.length === 0) {
    fail('dependency-manifest.json metadata is inconsistent.')
  }

  const runtimeClosure = collectRuntimeClosure(packageJson, packageLock)
  const manifestNames = manifest.packages.map((entry) => entry.name)
  if (runtimeClosure.length !== manifestNames.length ||
      runtimeClosure.some((name, index) => name !== manifestNames[index])) {
    fail('dependency-manifest.json does not match the production dependency closure.')
  }

  const records = []
  for (const entry of manifest.packages) {
    exactKeys(
      entry,
      [
        'name',
        'version',
        'source',
        'fileName',
        'byteSize',
        'sha256',
        'registrySha1',
        'declaredLicense',
      ],
      'dependency package entry',
    )
    if (typeof entry.name !== 'string' ||
        typeof entry.version !== 'string' ||
        typeof entry.source !== 'string' ||
        typeof entry.fileName !== 'string' ||
        !Number.isSafeInteger(entry.byteSize) ||
        entry.byteSize <= 0 ||
        !/^[0-9a-f]{64}$/u.test(entry.sha256) ||
        !/^[0-9a-f]{40}$/u.test(entry.registrySha1) ||
        entry.declaredLicense !== 'MIT') {
      fail('a dependency package entry is invalid.')
    }

    const locked = packageLock.packages[`node_modules/${entry.name}`]
    if (locked.version !== entry.version ||
        locked.resolved !== entry.source ||
        locked.license !== entry.declaredLicense ||
        toSha1Hex(locked.integrity) !== entry.registrySha1 ||
        !entry.source.endsWith(`/${entry.fileName}`)) {
      fail('a dependency package entry is stale relative to package-lock.json.')
    }

    records.push(`${entry.name}\0${entry.version}\0${entry.byteSize}\0${entry.sha256}\n`)
  }

  if (sha256(records.join('')) !== manifest.artifactSetSha256) {
    fail('the dependency artifact-set digest is inconsistent.')
  }

  process.stdout.write(`Verified ${records.length} production runtime packages.\n`)
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === modulePath) {
  if (process.argv.length !== 2) {
    fail('command-line arguments are not supported.')
  }

  await verifyRuntimeDependencies()
}