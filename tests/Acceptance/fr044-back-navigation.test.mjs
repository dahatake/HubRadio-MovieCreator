// FR-044 AC-019: Project 一覧へ戻る導線は共通ヘッダー 1 か所だけで、画面ごとの重複した「戻る」ボタンがない。
// NFR-UX-001 AC-113: 通常画面は 3 つの工程（元動画・ジョブの状態の確認・仕上がり）に限られる（詳細な操作検証は src/HubRadio.Web の *.test.tsx）。
import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import test from 'node:test'
import { join } from 'node:path'

const webSrc = new URL('../../src/HubRadio.Web/src/', import.meta.url)

function productFiles(dir) {
  const out = []
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) out.push(...productFiles(full))
    else if (/\.(tsx|ts)$/.test(entry.name) && !/\.test\.(tsx|ts)$/.test(entry.name)) out.push(full)
  }
  return out
}

test('FR-044 AC-019: 「Project一覧へ戻る」ボタンは WorkflowWorkspace の 1 か所だけに存在する', () => {
  const hits = []
  for (const file of productFiles(webSrc.pathname.replace(/^\/([A-Za-z]:)/, '$1'))) {
    const text = readFileSync(file, 'utf8')
    const count = (text.match(/<button[^>]*>\s*Project一覧へ戻る\s*<\/button>/g) ?? []).length
    if (count > 0) hits.push({ file: file.replace(/\\/g, '/').split('/src/').pop(), count })
  }
  assert.deepEqual(hits, [{ file: 'features/workflow/WorkflowWorkspace.tsx', count: 1 }])
})

test('NFR-UX-001 AC-113: 通常画面の工程は 元動画・ジョブの状態の確認・仕上がり の 3 つだけで、保存画面は無い', () => {
  const text = readFileSync(new URL('features/workflow/workflowNavigation.ts', webSrc), 'utf8')
  const labels = text.slice(0, text.indexOf('}', text.indexOf("source: '元動画'")))
  assert.match(labels, /source: '元動画'/)
  assert.match(labels, /processing: 'ジョブの状態の確認'/)
  assert.match(labels, /results: '仕上がり'/)
  assert.equal((labels.match(/^\s+\w+: '/gm) ?? []).length, 3)
  assert.doesNotMatch(labels, /保存/)
})

test('FR-044 AC-019: 他の画面に「戻る」「一覧へ戻る」系のボタンを置かない', () => {
  const offenders = []
  for (const file of productFiles(webSrc.pathname.replace(/^\/([A-Za-z]:)/, '$1'))) {
    if (/WorkflowWorkspace\.tsx$/.test(file)) continue
    const text = readFileSync(file, 'utf8')
    if (/<button[^>]*>\s*(?:Project一覧へ戻る|一覧へ戻る|← ?戻る)\s*<\/button>/.test(text)) offenders.push(file)
  }
  assert.deepEqual(offenders, [])
})
