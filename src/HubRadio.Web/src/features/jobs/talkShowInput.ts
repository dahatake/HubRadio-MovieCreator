/**
 * FR-141 (NFR-BR-002): the talk-show template's side telops and name bands are made only from values the user entered or
 * confirmed. A system draft is shown as a draft until confirmed; a title needs its own confirmation and date.
 */
export type TalkShowOrigin = 'user' | 'draft'

export interface TalkShowTopicEntry {
  readonly id: string
  readonly text: string
  readonly origin: TalkShowOrigin
  readonly confirmed: boolean
}

export interface TalkShowSpeakerEntry {
  readonly id: string
  readonly name: string
  readonly nameOrigin: TalkShowOrigin
  readonly nameConfirmed: boolean
  readonly title: string
  readonly titleConfirmed: boolean
  /** The date (yyyy-MM-dd) or recording point the title was confirmed at. */
  readonly titleConfirmedOn: string
}

export interface TalkShowInputValue {
  readonly topics: readonly TalkShowTopicEntry[]
  readonly speakers: readonly TalkShowSpeakerEntry[]
}

export const emptyTalkShowInput: TalkShowInputValue = Object.freeze({
  topics: Object.freeze([{ id: 'topic-1', text: '', origin: 'user', confirmed: false }]),
  speakers: Object.freeze([
    { id: 'speaker-1', name: '', nameOrigin: 'user', nameConfirmed: false, title: '', titleConfirmed: false, titleConfirmedOn: '' },
    { id: 'speaker-2', name: '', nameOrigin: 'user', nameConfirmed: false, title: '', titleConfirmed: false, titleConfirmedOn: '' },
  ]),
} satisfies TalkShowInputValue)

const anonymousLabel = /^\s*(話者|発言者|Speaker|SPEAKER|speaker)\s*[0-9０-９A-Za-zＡ-Ｚ]{1,3}\s*$/

export const topicIsShown = (topic: TalkShowTopicEntry): boolean =>
  topic.text.trim() !== '' && (topic.origin === 'user' || topic.confirmed)

export const nameIsShown = (speaker: TalkShowSpeakerEntry): boolean =>
  speaker.name.trim() !== '' && !anonymousLabel.test(speaker.name) && (speaker.nameOrigin === 'user' || speaker.nameConfirmed)

export type TitleState = 'none' | 'shown' | 'unconfirmed' | 'date-missing'

/** A title is shown only when it is confirmed with a date; typing alone does not show it. */
export function titleState(speaker: TalkShowSpeakerEntry): TitleState {
  if (speaker.title.trim() === '') return 'none'
  if (!speaker.titleConfirmed) return 'unconfirmed'
  return speaker.titleConfirmedOn.trim() === '' ? 'date-missing' : 'shown'
}

/** The text of the name band, or null when nothing may be shown. */
export function bandText(speaker: TalkShowSpeakerEntry): string | null {
  if (!nameIsShown(speaker)) return null
  return titleState(speaker) === 'shown' ? `${speaker.name.trim()}　${speaker.title.trim()}` : speaker.name.trim()
}

export function titleWarning(speaker: TalkShowSpeakerEntry): string | null {
  const state = titleState(speaker)
  if (state === 'unconfirmed') return '肩書きが未確認のため、映像には出しません（名前だけを出します）。'
  if (state === 'date-missing') return '肩書きの確認日（または収録時点）が未入力のため、映像には出しません。'
  return null
}

export function updateSpeaker(value: TalkShowInputValue, id: string, patch: Partial<TalkShowSpeakerEntry>): TalkShowInputValue {
  return { ...value, speakers: value.speakers.map(item => item.id === id ? { ...item, ...patch } : item) }
}

export function updateTopic(value: TalkShowInputValue, id: string, patch: Partial<TalkShowTopicEntry>): TalkShowInputValue {
  return { ...value, topics: value.topics.map(item => item.id === id ? { ...item, ...patch } : item) }
}

/** FR-141: the most topics one Job may hold (the same limit the app applies when it reads the input). */
export const maximumTopics = 20

/** FR-141 / FR-138 (4): adds an empty topic entry after the last one (each topic is shown while it continues). */
export function addTopic(value: TalkShowInputValue): TalkShowInputValue {
  if (value.topics.length >= maximumTopics) return value
  let number = value.topics.length + 1
  while (value.topics.some(item => item.id === `topic-${number}`)) number += 1
  return { ...value, topics: [...value.topics, { id: `topic-${number}`, text: '', origin: 'user', confirmed: false }] }
}

/** Removes a topic entry; at least one entry always stays so the field is never lost. */
export function removeTopic(value: TalkShowInputValue, id: string): TalkShowInputValue {
  if (value.topics.length <= 1) return value
  return { ...value, topics: value.topics.filter(item => item.id !== id) }
}
