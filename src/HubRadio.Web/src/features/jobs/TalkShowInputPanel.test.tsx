// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { TalkShowInputPanel } from './TalkShowInputPanel'
import { addTopic, bandText, emptyTalkShowInput, removeTopic, titleState, topicIsShown, updateSpeaker, updateTopic, type TalkShowInputValue } from './talkShowInput'

// FR-141 AC-168 / AC-170
describe('TalkShowInputPanel', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('div'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  const render = (value: TalkShowInputValue, onChange: (v: TalkShowInputValue) => void = () => {}) => act(() => root.render(<TalkShowInputPanel value={value} onChange={onChange} />))

  it('AC-168: shows inputs per speaker, keeps typed values, and tells a draft from a confirmed value in text', () => {
    let value = updateTopic(emptyTalkShowInput, 'topic-1', { text: '要約の下書き', origin: 'draft', confirmed: false })
    value = updateSpeaker(value, 'speaker-1', { name: '山田太郎', title: '代表取締役' })
    render(value)
    expect((container.querySelector('input[aria-label="話者1の名前"]') as HTMLInputElement).value).toBe('山田太郎')
    expect(container.querySelector('input[aria-label="話者2の肩書き"]')).not.toBeNull()
    expect(container.textContent).toContain('下書き（未確認のため映像には出しません）')
    expect(container.textContent).toContain('肩書きが未確認のため、映像には出しません')
    expect(container.textContent).toContain('帯の表示: 山田太郎')
    value = updateTopic(value, 'topic-1', { confirmed: true })
    render(value)
    expect(container.textContent).toContain('確認済み')
  })

  it('AC-168: checking "肩書きを確認した" records the confirmation date and shows the title in the band', () => {
    let value = updateSpeaker(emptyTalkShowInput, 'speaker-1', { name: '山田太郎', title: '代表取締役' })
    const changes: TalkShowInputValue[] = []
    render(value, next => { changes.push(next); value = next })
    const box = container.querySelector('input[aria-label="肩書きを確認した"]') as HTMLInputElement
    act(() => box.click())
    const next = changes.at(-1)!
    expect(next.speakers[0].titleConfirmed).toBe(true)
    expect(next.speakers[0].titleConfirmedOn).toMatch(/^\d{4}-\d{2}-\d{2}$/)
    render(next)
    expect(container.textContent).toContain('帯の表示: 山田太郎　代表取締役')
  })

  it('AC-170: a changed confirmed value replaces the old one and nothing of the old value stays', () => {
    const first = updateSpeaker(emptyTalkShowInput, 'speaker-1', { name: '山田太郎', title: '代表取締役', titleConfirmed: true, titleConfirmedOn: '2026-10-30' })
    expect(bandText(first.speakers[0])).toBe('山田太郎　代表取締役')
    const changed = updateSpeaker(first, 'speaker-1', { name: '山田次郎', title: '会長' })
    expect(bandText(changed.speakers[0])).toBe('山田次郎　会長')
    render(changed)
    expect(container.textContent).not.toContain('山田太郎')
    expect(container.textContent).not.toContain('代表取締役')
  })

  it('AC-157: topics can be added and removed; each topic keeps its own text and at least one entry stays', () => {
    let value = updateTopic(emptyTalkShowInput, 'topic-1', { text: '最初の話題' })
    const changes: TalkShowInputValue[] = []
    render(value, next => { changes.push(next); value = next })
    const add = Array.from(container.querySelectorAll('button')).find(item => item.textContent === '話題を追加')!
    act(() => add.click())
    value = changes.at(-1)!
    expect(value.topics.map(item => item.id)).toEqual(['topic-1', 'topic-2'])
    value = updateTopic(value, 'topic-2', { text: '次の話題' })
    render(value, next => { changes.push(next); value = next })
    expect((container.querySelector('input[aria-label="話題2のサイドテロップ"]') as HTMLInputElement).value).toBe('次の話題')
    expect((container.querySelector('input[aria-label="話題1のサイドテロップ"]') as HTMLInputElement).value).toBe('最初の話題')
    const remove = Array.from(container.querySelectorAll('button')).find(item => item.textContent === '話題1を削除')!
    act(() => remove.click())
    expect(changes.at(-1)!.topics.map(item => item.text)).toEqual(['次の話題'])
    expect(removeTopic(changes.at(-1)!, 'topic-2').topics).toHaveLength(1)
    let full = emptyTalkShowInput
    for (let i = 0; i < 30; i += 1) full = addTopic(full)
    expect(full.topics).toHaveLength(20)
  })

  it('never shows anonymous labels, a title without confirmation or date, or an unconfirmed draft topic', () => {
    expect(bandText(updateSpeaker(emptyTalkShowInput, 'speaker-1', { name: '話者1' }).speakers[0])).toBeNull()
    const s = updateSpeaker(emptyTalkShowInput, 'speaker-1', { name: '佐藤花子', title: '取締役', titleConfirmed: true }).speakers[0]
    expect(titleState(s)).toBe('date-missing')
    expect(bandText(s)).toBe('佐藤花子')
    expect(topicIsShown({ id: 't', text: '下書き', origin: 'draft', confirmed: false })).toBe(false)
  })
})
