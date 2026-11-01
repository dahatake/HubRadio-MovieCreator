import { addTopic, bandText, maximumTopics, nameIsShown, removeTopic, titleState, titleWarning, topicIsShown, updateSpeaker, updateTopic, type TalkShowInputValue } from './talkShowInput'

/**
 * FR-141: input and confirmation of the talk-show side telop (topic) and each speaker's name band. Drafts are labelled as drafts
 * and are not shown in the product until confirmed (the state is written in text, not only by color).
 */
export function TalkShowInputPanel({ value, onChange, disabled = false }: Readonly<{
  value: TalkShowInputValue
  onChange: (next: TalkShowInputValue) => void
  disabled?: boolean
}>) {
  const today = new Date().toISOString().slice(0, 10)
  return <fieldset className="talk-show-input" disabled={disabled}>
    <legend>対談番組の話題と名前・肩書き</legend>
    <p>映像に出るのは、入力した値と、確認済みにした値だけです。下書きや推測は出しません。</p>
    {value.topics.map((topic, index) => <div key={topic.id} className="talk-show-topic">
      <label>{`話題${index + 1}のサイドテロップ`}
        <input type="text" aria-label={`話題${index + 1}のサイドテロップ`} value={topic.text} onChange={event => onChange(updateTopic(value, topic.id, { text: event.target.value, origin: 'user' }))} />
      </label>
      {topic.origin === 'draft' && <label>
        <input type="checkbox" checked={topic.confirmed} onChange={event => onChange(updateTopic(value, topic.id, { confirmed: event.target.checked }))} />話題の下書きを確認した
      </label>}
      <span role="status">{topic.text.trim() === '' ? '未入力（映像には出しません）' : topicIsShown(topic) ? (topic.origin === 'draft' ? '確認済み' : '入力済み') : '下書き（未確認のため映像には出しません）'}</span>
      {value.topics.length > 1 && <button type="button" onClick={() => onChange(removeTopic(value, topic.id))}>{`話題${index + 1}を削除`}</button>}
    </div>)}
    {value.topics.length < maximumTopics && <button type="button" onClick={() => onChange(addTopic(value))}>話題を追加</button>}
    {value.speakers.map((speaker, index) => {
      const n = index + 1
      const warning = titleWarning(speaker)
      return <fieldset key={speaker.id} className="talk-show-speaker">
        <legend>{`話者${n}`}</legend>
        <label>{`話者${n}の名前`}
          <input type="text" aria-label={`話者${n}の名前`} value={speaker.name} onChange={event => onChange(updateSpeaker(value, speaker.id, { name: event.target.value, nameOrigin: 'user' }))} />
        </label>
        <label>{`話者${n}の肩書き`}
          <input type="text" aria-label={`話者${n}の肩書き`} value={speaker.title} onChange={event => onChange(updateSpeaker(value, speaker.id, { title: event.target.value }))} />
        </label>
        <label>
          <input type="checkbox" aria-label="肩書きを確認した" checked={speaker.titleConfirmed}
            onChange={event => onChange(updateSpeaker(value, speaker.id, { titleConfirmed: event.target.checked, titleConfirmedOn: event.target.checked && speaker.titleConfirmedOn === '' ? today : speaker.titleConfirmedOn }))} />
          肩書きを確認した
        </label>
        <label>{`話者${n}の肩書きの確認日（または収録時点）`}
          <input type="text" aria-label={`話者${n}の肩書きの確認日`} value={speaker.titleConfirmedOn} placeholder="2026-10-30 または 収録時点"
            onChange={event => onChange(updateSpeaker(value, speaker.id, { titleConfirmedOn: event.target.value }))} />
        </label>
        <p role="status">{`名前: ${speaker.name.trim() === '' ? '未入力（帯は出しません）' : nameIsShown(speaker) ? '入力済み' : '映像には出しません（匿名の呼び名・未確認）'} ／ 肩書き: ${titleState(speaker) === 'shown' ? '確認済み' : titleState(speaker) === 'none' ? '未入力' : '未確認'}`}</p>
        {warning !== null && <p role="alert">{warning}</p>}
        <p>{`帯の表示: ${bandText(speaker) ?? '出さない'}`}</p>
      </fieldset>
    })}
  </fieldset>
}
