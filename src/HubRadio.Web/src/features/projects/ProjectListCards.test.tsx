// @vitest-environment jsdom

import { within } from '@testing-library/dom'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ProjectListItem } from '../../bridge'
import { ProjectListCards } from './ProjectListCards'

const timestamp = '2026-09-07T01:02:00.0000000+00:00'
const finalizedProjectId = '99990000-aaaa-4bbb-8ccc-ddddeeeeffff'
const actEnvironment = globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
let previousActEnvironment: boolean | undefined
let container: HTMLDivElement
let root: Root

beforeEach(() => {
  previousActEnvironment = actEnvironment.IS_REACT_ACT_ENVIRONMENT
  actEnvironment.IS_REACT_ACT_ENVIRONMENT = true
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
  if (previousActEnvironment === undefined) {
    delete actEnvironment.IS_REACT_ACT_ENVIRONMENT
  } else {
    actEnvironment.IS_REACT_ACT_ENVIRONMENT = previousActEnvironment
  }
})

describe('ProjectListCards', () => {
  it('renders an empty state without a table, invented projects or paging controls', () => {
    renderCards([])

    expect(container.querySelector('.project-list-cards')).not.toBeNull()
    expect(within(container).getByRole('status').textContent).toContain('案件はまだありません')
    expect(within(container).queryAllByRole('button')).toHaveLength(0)
    expect(within(container).queryByRole('list')).toBeNull()
    expect(within(container).queryByRole('navigation')).toBeNull()
    expect(container.querySelector('table')).toBeNull()
  })

  it.each([1, 6])('shows %i projects on one page with both bounds disabled', (count) => {
    renderCards(projectsOf(count))

    expect(within(projectList()).getAllByRole('listitem')).toHaveLength(count)
    expect(pageStatus().textContent).toBe(`1 / 1 ページ（1–${count} / ${count} 件）`)
    expect(pageButton('前のページ').hasAttribute('disabled')).toBe(true)
    expect(pageButton('次のページ').hasAttribute('disabled')).toBe(true)
    expect(openButton('案件 1').hasAttribute('disabled')).toBe(false)
    expect(container.querySelector('table, .table-scroll')).toBeNull()
  })

  it('keeps real state, UTC update and all exact source labels without generated claims', () => {
    for (const [state, stateLabel] of [
      ['draft', '下書き'],
      ['finalized', '作成済み'],
    ] as const) {
      for (const [sourceStatus, sourceLabel] of [
        ['notSelected', '未選択'],
        ['available', '選択済み'],
        ['reselectionRequired', '再選択が必要'],
      ] as const) {
        renderCards([projectListItem(0, {
          state,
          projectId: state === 'finalized' ? finalizedProjectId : null,
          sourceStatus,
        })])

        expect(container.querySelector('.state-badge')?.textContent).toBe(stateLabel)
        expect(container.querySelector('.source-status')?.textContent).toBe(sourceLabel)
        expect(container.querySelector('time')?.getAttribute('datetime')).toBe(timestamp)
        expect(container.querySelector('time')?.textContent).toBe('2026/09/07 01:02 UTC')
        expect(container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true')
        expect(container.querySelector('svg')?.getAttribute('focusable')).toBe('false')
        for (const label of ['エピソード番号', '担当者', '編集状態', 'レビュー状態', '生成済み']) {
          expect(container.textContent).not.toContain(label)
        }
      }
    }
  })

  it('retains the existing unnamed fallback without adding null-field placeholders', () => {
    renderCards([projectListItem(0, { contentName: null, sourceStatus: 'notSelected' })])

    expect(openButton('名称未入力').getAttribute('title')).toBe('名称未入力')
    expect(container.querySelectorAll('.project-card-metadata dt')).toHaveLength(2)
    expect(container.textContent).not.toContain('未設定')
  })

  it('keeps a long name intact in the title and accessible name instead of slicing text', () => {
    const name = `${'とても長い案件名・'.repeat(80)}最後まで読める名前`
    renderCards([projectListItem(0, { contentName: name })])

    const button = openButton(name)
    expect(button.getAttribute('title')).toBe(name)
    expect(button.querySelector('.project-card-name')?.textContent).toBe(name)
    expect(button.getAttribute('aria-label')).toBe(`${name}を開く`)
  })

  it('escapes the display name and never exposes private paths or internal IDs as metadata', () => {
    const name = '<script>globalThis.compromised=true</script><img src=x onerror=alert(1)>&tail'
    const privatePath = 'C:\\Users\\private\\Videos\\source.mp4'
    const project = {
      ...projectListItem(0, {
        contentName: name,
        state: 'finalized',
        projectId: finalizedProjectId,
        outputFormats: [privatePath],
      }),
      sourcePath: privatePath,
    }
    renderCards([project])

    expect(openButton(name).getAttribute('title')).toBe(name)
    expect(container.querySelector('script, img')).toBeNull()
    expect(container.innerHTML).not.toContain(privatePath)
    expect(container.innerHTML).not.toContain(project.draftId)
    expect(container.innerHTML).not.toContain(finalizedProjectId)
  })

  it.each(['[Enter]', '[Space]'])('opens with %s using the draft ID, even for a finalized project', async (key) => {
    const project = projectListItem(0, { state: 'finalized', projectId: finalizedProjectId })
    const onOpen = renderCards([project])
    const user = userEvent.setup({ document })

    await act(async () => user.tab())
    expect(document.activeElement).toBe(openButton('案件 1'))
    expect(openButton('案件 1').getAttribute('type')).toBe('button')
    await act(async () => user.keyboard(key))

    expect(onOpen).toHaveBeenCalledExactlyOnceWith(project.draftId)
  })

  it('pages 13 projects locally in input order and focuses the first new card after keyboard paging', async () => {
    const projects = projectsOf(13)
    const onOpen = renderCards(projects)
    const user = userEvent.setup({ document })
    const seenNames = [...visibleNames()]
    expect(seenNames).toEqual(projects.slice(0, 6).map((project) => project.contentName))
    expect(pageStatus().getAttribute('aria-live')).toBe('polite')
    expect(pageStatus().getAttribute('aria-atomic')).toBe('true')
    expect(pageButton('次のページ').getAttribute('aria-controls')).toBe(projectList().id)

    act(() => pageButton('次のページ').focus())
    await act(async () => user.keyboard('[Enter]'))
    expect(pageStatus().textContent).toBe('2 / 3 ページ（7–12 / 13 件）')
    expect(visibleNames()).toEqual(projects.slice(6, 12).map((project) => project.contentName))
    expect(document.activeElement).toBe(openButton('案件 7'))
    seenNames.push(...visibleNames())

    act(() => pageButton('次のページ').focus())
    await act(async () => user.keyboard('[Space]'))
    expect(pageStatus().textContent).toBe('3 / 3 ページ（13–13 / 13 件）')
    expect(visibleNames()).toEqual(['案件 13'])
    expect(document.activeElement).toBe(openButton('案件 13'))
    expect(pageButton('次のページ').hasAttribute('disabled')).toBe(true)
    seenNames.push(...visibleNames())
    expect(seenNames).toEqual(projects.map((project) => project.contentName))

    act(() => pageButton('前のページ').focus())
    await act(async () => user.keyboard('[Enter]'))
    expect(document.activeElement).toBe(openButton('案件 7'))
    act(() => pageButton('前のページ').focus())
    await act(async () => user.keyboard('[Space]'))
    expect(document.activeElement).toBe(openButton('案件 1'))
    expect(pageButton('前のページ').hasAttribute('disabled')).toBe(true)
    expect(onOpen).not.toHaveBeenCalled()
  })

  it('clamps a shrinking list, retains the clamp on growth and resets after an empty list', async () => {
    const projects = projectsOf(13)
    const onOpen = renderCards(projects)
    const user = userEvent.setup({ document })
    await act(async () => user.click(pageButton('次のページ')))
    await act(async () => user.click(pageButton('次のページ')))
    expect(pageStatus().textContent).toContain('3 / 3 ページ')

    renderCards(projects.slice(0, 8), false, onOpen)
    expect(pageStatus().textContent).toBe('2 / 2 ページ（7–8 / 8 件）')
    expect(visibleNames()).toEqual(['案件 7', '案件 8'])
    expect(pageButton('次のページ').hasAttribute('disabled')).toBe(true)

    renderCards(projects, false, onOpen)
    expect(pageStatus().textContent).toContain('2 / 3 ページ')
    expect(visibleNames()).toHaveLength(6)

    renderCards([], false, onOpen)
    expect(within(container).queryByRole('navigation')).toBeNull()
    renderCards(projects, false, onOpen)
    expect(pageStatus().textContent).toBe('1 / 3 ページ（1–6 / 13 件）')
    expect(visibleNames()[0]).toBe('案件 1')
    expect(onOpen).not.toHaveBeenCalled()
  })

  it('preserves the current page and focused draft key when refreshed items are renamed and reordered', async () => {
    const projects = projectsOf(13)
    const onOpen = renderCards(projects)
    const user = userEvent.setup({ document })
    await act(async () => user.click(pageButton('次のページ')))
    const focusedButton = openButton('案件 7')
    expect(document.activeElement).toBe(focusedButton)

    const renamed = { ...projects[6]!, contentName: '更新した案件名' }
    renderCards([
      ...projects.slice(0, 6),
      projects[7]!,
      renamed,
      ...projects.slice(8),
    ], false, onOpen)

    expect(pageStatus().textContent).toContain('2 / 3 ページ')
    expect(openButton('更新した案件名')).toBe(focusedButton)
    expect(document.activeElement).toBe(focusedButton)
    expect(focusedButton.getAttribute('title')).toBe('更新した案件名')
    await act(async () => user.keyboard('[Enter]'))
    expect(onOpen).toHaveBeenCalledExactlyOnceWith(renamed.draftId)
  })

  it.each(['pending request', 'unsaved edits'])('blocks opening and paging during %s without resetting the page', async () => {
    const projects = projectsOf(13)
    const onOpen = renderCards(projects)
    const user = userEvent.setup({ document })
    await act(async () => user.click(pageButton('次のページ')))

    // ProjectWorkspace supplies disabled for either pending work or dirty input.
    renderCards(projects, true, onOpen)
    for (const button of within(container).getAllByRole('button')) {
      expect(button.hasAttribute('disabled')).toBe(true)
      await act(async () => user.click(button))
    }
    await act(async () => user.keyboard('[Enter][Space]'))
    expect(onOpen).not.toHaveBeenCalled()
    expect(pageStatus().textContent).toBe('2 / 3 ページ（7–12 / 13 件）')

    renderCards(projects, false, onOpen)
    expect(pageStatus().textContent).toContain('2 / 3 ページ')
    expect(openButton('案件 7').hasAttribute('disabled')).toBe(false)
    expect(pageButton('前のページ').hasAttribute('disabled')).toBe(false)
    expect(pageButton('次のページ').hasAttribute('disabled')).toBe(false)
    await act(async () => user.click(openButton('案件 7')))
    expect(onOpen).toHaveBeenCalledExactlyOnceWith(projects[6]!.draftId)
  })

  it('shows one delete button per card only when deletion is offered and reports the exact target', async () => {
    const projects = projectsOf(3)
    renderCards(projects)
    expect(within(container).queryAllByRole('button', { name: /を削除$/ })).toHaveLength(0)

    const onDelete = vi.fn<(draftId: string, name: string) => void>()
    renderCards(projects, false, undefined, onDelete)
    expect(within(container).getAllByRole('button', { name: /を削除$/ })).toHaveLength(3)
    const user = userEvent.setup({ document })
    await act(async () => user.click(within(container).getByRole('button', { name: '案件 2を削除' })))
    expect(onDelete).toHaveBeenCalledExactlyOnceWith(projects[1]!.draftId, '案件 2')

    onDelete.mockClear()
    renderCards(projects, true, undefined, onDelete)
    const disabledDelete = within(container).getByRole('button', { name: '案件 2を削除' })
    expect(disabledDelete.hasAttribute('disabled')).toBe(true)
    await act(async () => user.click(disabledDelete))
    expect(onDelete).not.toHaveBeenCalled()
  })

  it('uses the unnamed fallback when deleting a project without a name', async () => {
    const onDelete = vi.fn<(draftId: string, name: string) => void>()
    renderCards([projectListItem(0, { contentName: null })], false, undefined, onDelete)
    const user = userEvent.setup({ document })
    await act(async () => user.click(within(container).getByRole('button', { name: '名称未入力を削除' })))
    expect(onDelete).toHaveBeenCalledExactlyOnceWith(expect.any(String), '名称未入力')
  })
})

function renderCards(
  projects: readonly ProjectListItem[],
  disabled = false,
  onOpen = vi.fn<(draftId: string) => void>(),
  onDelete?: (draftId: string, name: string) => void,
) {
  act(() => {
    root.render(
      <ProjectListCards projects={projects} disabled={disabled} onOpen={onOpen} onDelete={onDelete} />,
    )
  })
  return onOpen
}

function projectList() {
  return within(container).getByRole('list', { name: '閲覧を許可された案件の一覧' })
}

function openButton(name: string) {
  return within(container).getByRole('button', { name: `${name}を開く` })
}

function pageButton(name: '前のページ' | '次のページ') {
  return within(container).getByRole('button', { name })
}

function pageStatus() {
  return within(container).getByRole('status')
}

function visibleNames() {
  return [...container.querySelectorAll('.project-card-name')].map((node) => node.textContent)
}

function projectsOf(count: number): readonly ProjectListItem[] {
  return Object.freeze(Array.from({ length: count }, (_, index) => projectListItem(index)))
}

function projectListItem(index: number, overrides: Partial<ProjectListItem> = {}): ProjectListItem {
  return Object.freeze({
    draftId: `00000000-0000-4000-8000-${(index + 1).toString().padStart(12, '0')}`,
    projectId: null,
    state: 'draft',
    contentName: `案件 ${index + 1}`,
    episodeNumber: null,
    sourceStatus: 'available',
    owner: null,
    publicationScope: 'internalOnly',
    editState: null,
    reviewState: null,
    outputFormats: Object.freeze(['long-form-1280x720']),
    updatedAt: timestamp,
    ...overrides,
  })
}