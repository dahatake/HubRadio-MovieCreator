import { useId, useState, type FormEvent } from 'react'

import {
  PROJECT_DISPLAY_NAME_MAX_LENGTH,
  normalizeProjectDisplayName,
  type ProjectDraft,
} from '../../bridge'
import { getProjectDisplayName } from './projectState'
import './projects.css'

export interface ProjectNameEditorProps {
  readonly draft: ProjectDraft
  readonly disabled: boolean
  readonly onRename: (displayName: string) => void
}

const invalidNameMessage =
  `案件名は 1〜${PROJECT_DISPLAY_NAME_MAX_LENGTH} 文字で入力してください。改行などの制御文字と、文字の向きを変える記号は使えません。`

/** Edits only the name shown in the app; the source file and generated files keep their names. */
export function ProjectNameEditor({ draft, disabled, onRename }: ProjectNameEditorProps) {
  const id = useId()
  const savedName = getProjectDisplayName(draft) ?? ''
  const [edit, setEdit] = useState<{ readonly basedOn: string; readonly value: string }>({
    basedOn: savedName,
    value: savedName,
  })
  // A saved or reloaded name replaces the text box; edits made against the old name are dropped.
  const value = edit.basedOn === savedName ? edit.value : savedName
  const normalized = normalizeProjectDisplayName(value)
  const changed = normalized !== savedName
  const invalid = normalized === null
  const originalName = draft.contentName
  const canRestore = originalName !== null && originalName !== savedName && normalizeProjectDisplayName(originalName) !== null

  const submit = (event: FormEvent): void => {
    event.preventDefault()
    if (!disabled && normalized !== null && changed) {
      onRename(normalized)
    }
  }

  return (
    <form className="project-name-editor" aria-labelledby={`${id}-title`} onSubmit={submit} noValidate>
      <h3 id={`${id}-title`}>案件名</h3>
      <div className="field-group">
        <label htmlFor={`${id}-input`}>案件名（この画面と一覧に表示される名前）</label>
        <input
          id={`${id}-input`}
          type="text"
          value={value}
          disabled={disabled}
          aria-invalid={invalid}
          aria-describedby={`${id}-note${invalid ? ` ${id}-error` : ''}`}
          autoComplete="off"
          onChange={(event) => setEdit({ basedOn: savedName, value: event.currentTarget.value })}
        />
        {invalid && <p id={`${id}-error`} className="field-error" role="alert">{invalidNameMessage}</p>}
        <p id={`${id}-note`} className="project-name-note">
          名前を変えても、元動画のファイル名と作成済みの成果物は変わりません。
          {originalName !== null && originalName !== savedName && `元の名前：${originalName}`}
        </p>
      </div>
      <div className="section-actions">
        <button className="secondary-button" type="submit" disabled={disabled || invalid || !changed}>
          案件名を保存
        </button>
        {canRestore && originalName !== null && (
          <button
            className="quiet-button"
            type="button"
            disabled={disabled}
            onClick={() => onRename(originalName)}
          >
            元の名前に戻す
          </button>
        )}
      </div>
    </form>
  )
}
