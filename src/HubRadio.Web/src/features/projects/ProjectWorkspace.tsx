import { useEffect, useRef, useState, type ChangeEvent } from 'react'

import {
  PROJECT_CREATE_DRAFT_METHOD,
  PROJECT_FINALIZE_DRAFT_METHOD,
  PROJECT_LIST_METHOD,
  PROJECT_LOAD_DRAFT_METHOD,
  PROJECT_PICK_SOURCE_METHOD,
  PROJECT_SAVE_DRAFT_METHOD,
  isProjectDraftInputWithinRequestLimit,
  type ProjectBridgeMethod,
  type ProjectCreationMissingField,
  type ProjectDeadlineInput,
  type ProjectDraftInput,
  type ProjectListItem,
  type PublicationScope,
  type SourceStatus,
} from '../../bridge'
import {
  getMissingProjectFields,
  getProjectInputSourceStatus,
  isProjectInputFinalizable,
  isProjectInputDirty,
  isProjectInputSaveable,
} from './projectState'
import {
  useProjectState,
  type ProjectStateContextValue,
} from './ProjectStateProvider'

const wizardSteps = [
  '元動画と名前',
  '目的と公開範囲',
  '出力とブランド',
  '出演者と期限',
  '確認',
] as const

const publicationScopes: readonly Readonly<{
  value: PublicationScope
  label: string
  description: string
}>[] = [
  {
    value: 'internalOnly',
    label: '社内限定',
    description: '組織内でのみ利用します。',
  },
  {
    value: 'specificGroupOnly',
    label: '特定グループ限定',
    description: '指定された関係者だけで利用します。',
  },
  {
    value: 'externalCandidate',
    label: '外部公開候補',
    description: '外部公開を検討します。承認済みではありません。',
  },
  {
    value: 'externalPublicationApproved',
    label: '外部公開承認済み',
    description: '外部公開の承認を得た案件です。',
  },
  {
    value: 'publicationForbidden',
    label: '公開不可',
    description: '外部へ公開できません。',
  },
  {
    value: 'pendingDecision',
    label: '判定待ち',
    description: '公開範囲の確認が必要です。',
  },
]

const missingFieldLabels: Readonly<Record<ProjectCreationMissingField, string>> = {
  sourceMedia: '元動画ファイル',
  contentName: 'コンテンツ名',
  purpose: '動画の目的',
  audience: '想定視聴者',
  publicationScope: '公開範囲',
  outputFormats: '希望する出力形式',
  language: '使用言語',
  performerNames: '出演者名',
  deadline: '公開期限の要否',
  brandTemplate: 'ブランドテンプレート',
}

const publicationScopeLabels = Object.freeze(
  Object.fromEntries(
    publicationScopes.map((scope) => [scope.value, scope.label]),
  ) as Record<PublicationScope, string>,
)

const sourceStatusLabels: Readonly<Record<SourceStatus, string>> = {
  notSelected: '未選択',
  available: '選択済み',
  reselectionRequired: '再選択が必要',
}

const requestLabels: Readonly<Record<ProjectBridgeMethod, string>> = {
  [PROJECT_LIST_METHOD]: '案件一覧を更新',
  [PROJECT_CREATE_DRAFT_METHOD]: '下書きを作成',
  [PROJECT_LOAD_DRAFT_METHOD]: '下書きを読み込み',
  [PROJECT_SAVE_DRAFT_METHOD]: '下書きを保存',
  [PROJECT_PICK_SOURCE_METHOD]: '元動画を選択',
  [PROJECT_FINALIZE_DRAFT_METHOD]: '案件を作成',
}

export function ProjectWorkspace() {
  return <ProjectWorkspaceView {...useProjectState()} />
}

export function ProjectWorkspaceView({
  state,
  refresh,
  createDraft,
  loadDraft,
  updateInput,
  saveDraft,
  pickSource,
  finalizeDraft,
  closeDraft,
  clearError,
}: ProjectStateContextValue) {
  const [wizardStep, setWizardStep] = useState(0)
  const wizardHeadingRef = useRef<HTMLHeadingElement>(null)
  const errorRef = useRef<HTMLDivElement>(null)
  const draftId = state.draft?.draftId ?? null

  useEffect(() => {
    if (draftId !== null) {
      setWizardStep(0)
      wizardHeadingRef.current?.focus()
    }
  }, [draftId])

  useEffect(() => {
    if (state.error !== null) {
      errorRef.current?.focus()
    }
  }, [state.error])

  const pending = state.pending !== null
  const finalized = state.draft?.isFinalized === true
  const dirty = isProjectInputDirty(state.input, state.draft)
  const saveable = isProjectInputSaveable(state.input)
  const withinRequestLimit = isProjectDraftInputWithinRequestLimit(state.input)
  const activeSourceStatus = getProjectInputSourceStatus(state.input, state.draft)
  const complete = isProjectInputFinalizable(state.input, state.draft)
  const missingFields = getMissingProjectFields(state.input, activeSourceStatus)

  const changeInput = <K extends keyof ProjectDraftInput>(
    key: K,
    value: ProjectDraftInput[K],
  ): void => {
    updateInput({ ...state.input, [key]: value })
  }

  const changeOptionalText = (
    key: 'contentName' | 'purpose' | 'audience' | 'language',
    value: string,
  ): void => {
    changeInput(key, value.length === 0 ? null : value)
  }

  const trimOptionalText = (
    key: 'contentName' | 'purpose' | 'audience' | 'language',
    value: string,
  ): void => {
    const trimmed = value.trim()
    changeInput(key, trimmed.length === 0 ? null : trimmed)
  }

  const toggleOutputProfile = (profileId: string, checked: boolean): void => {
    const selected = new Set(state.input.outputProfileIds)
    if (checked) {
      selected.add(profileId)
    } else {
      selected.delete(profileId)
    }

    changeInput(
      'outputProfileIds',
      state.catalog.outputProfiles
        .map((profile) => profile.id)
        .filter((id) => selected.has(id)),
    )
  }

  const addPerformer = (): void => {
    changeInput('performerNames', [...state.input.performerNames, ''])
  }

  const updatePerformer = (index: number, value: string): void => {
    changeInput(
      'performerNames',
      state.input.performerNames.map((current, currentIndex) =>
        currentIndex === index ? value : current,
      ),
    )
  }

  const removePerformer = (index: number): void => {
    changeInput(
      'performerNames',
      state.input.performerNames.filter((_, currentIndex) => currentIndex !== index),
    )
  }

  const changeDeadline = (event: ChangeEvent<HTMLInputElement>): void => {
    const value = event.currentTarget.value
    const utcValue = value.length === 0
      ? null
      : new Date(`${value}:00.000Z`).toISOString()
    changeInput(
      'deadlineSelection',
      utcValue === null
        ? null
        : { hasDeadline: true, value: utcValue },
    )
  }

  return (
    <div className="project-workspace">
      {state.error !== null && (
        <div
          className="error-banner"
          ref={errorRef}
          role="alert"
          tabIndex={-1}
        >
          <div>
            <strong>操作を完了できませんでした</strong>
            <p>{state.error.message}</p>
          </div>
          <div className="error-actions">
            {state.error.code === 'project.revision_conflict' && draftId !== null && (
              <button type="button" onClick={() => loadDraft(draftId)}>
                最新版を再読み込み
              </button>
            )}
            {state.error.code === 'project.source_reselection_required' && (
              <button type="button" onClick={pickSource}>
                元動画を再選択
              </button>
            )}
            <button className="quiet-button" type="button" onClick={clearError}>
              閉じる
            </button>
          </div>
        </div>
      )}

      <section className="project-panel project-list-panel" aria-labelledby="projects-title">
        <div className="section-heading-row">
          <div>
            <p className="section-kicker">Projects</p>
            <h2 id="projects-title">案件一覧</h2>
            <p>下書きと作成済み案件を、更新が新しい順に表示します。</p>
          </div>
          <div className="section-actions">
            <button
              className="secondary-button"
              type="button"
              disabled={pending}
              onClick={refresh}
            >
              一覧を更新
            </button>
            <button
              className="primary-button"
              type="button"
              disabled={pending || !state.canCreate || state.draft !== null}
              onClick={createDraft}
            >
              新しい案件
            </button>
          </div>
        </div>

        {!state.canCreate && !pending && state.catalog.outputProfiles.length > 0 && (
          <p className="permission-note">
            案件を作成する権限がありません。一覧の閲覧権限は案件ごとに適用されます。
          </p>
        )}

        <ProjectList
          projects={state.projects}
          disabled={pending || dirty}
          onOpen={loadDraft}
        />
      </section>

      {state.draft !== null && (
        <section className="project-panel wizard-panel" aria-labelledby="wizard-title">
          <div className="wizard-heading">
            <div>
              <p className="section-kicker">Creation wizard</p>
              <h2 id="wizard-title" ref={wizardHeadingRef} tabIndex={-1}>
                {finalized ? '案件の内容' : '案件作成ウィザード'}
              </h2>
            </div>
            <span className={`state-badge ${finalized ? 'finalized' : 'draft'}`}>
              {finalized ? '作成済み' : '下書き'}
            </span>
          </div>

          <nav className="wizard-progress" aria-label="案件作成の手順">
            <ol>
              {wizardSteps.map((label, index) => (
                <li key={label}>
                  <button
                    type="button"
                    aria-current={wizardStep === index ? 'step' : undefined}
                    onClick={() => setWizardStep(index)}
                  >
                    <span aria-hidden="true">{index + 1}</span>
                    {label}
                  </button>
                </li>
              ))}
            </ol>
          </nav>

          <form
            className="project-form"
            onSubmit={(event) => {
              event.preventDefault()
              saveDraft()
            }}
          >
            <fieldset disabled={pending || finalized} hidden={wizardStep !== 0}>
              <legend>元動画とコンテンツ名</legend>
              <p className="step-description">
                元動画は Windows の選択画面で指定します。ファイルパスは Web 画面へ渡されません。
              </p>

              <div className="field-group">
                <span className="field-label">元動画ファイル <RequiredMark /></span>
                <div className="source-picker-row">
                  <span className={`source-status ${activeSourceStatus}`}>
                    {sourceStatusLabels[activeSourceStatus]}
                  </span>
                  <button className="secondary-button" type="button" onClick={pickSource}>
                    {activeSourceStatus === 'notSelected' ? '元動画を選択' : '元動画を選び直す'}
                  </button>
                </div>
                {activeSourceStatus === 'reselectionRequired' && (
                  <p className="field-warning">
                    保存済みの選択情報はこの起動では利用できません。元動画を再選択してください。
                  </p>
                )}
                {state.sourcePickerCanceled && (
                  <p className="field-note" role="status">
                    選択をキャンセルしました。以前の選択は変更していません。
                  </p>
                )}
              </div>

              <TextField
                id="project-content-name"
                label="コンテンツ名"
                value={state.input.contentName}
                placeholder="例: Hubらじお 第12回"
                onChange={(value) => changeOptionalText('contentName', value)}
                onBlur={(value) => trimOptionalText('contentName', value)}
              />
            </fieldset>

            <fieldset disabled={pending || finalized} hidden={wizardStep !== 1}>
              <legend>目的・視聴者・公開範囲</legend>
              <p className="step-description">
                自動編集の判断基準と、情報を扱える範囲を指定します。
              </p>

              <TextAreaField
                id="project-purpose"
                label="動画の目的"
                value={state.input.purpose}
                placeholder="この動画で達成したいこと"
                onChange={(value) => changeOptionalText('purpose', value)}
                onBlur={(value) => trimOptionalText('purpose', value)}
              />
              <TextAreaField
                id="project-audience"
                label="想定視聴者"
                value={state.input.audience}
                placeholder="視聴する人やチーム"
                onChange={(value) => changeOptionalText('audience', value)}
                onBlur={(value) => trimOptionalText('audience', value)}
              />

              <fieldset className="nested-fieldset">
                <legend>公開範囲 <RequiredMark /></legend>
                <div className="scope-grid">
                  {publicationScopes.map((scope) => (
                    <label className="scope-option" key={scope.value}>
                      <input
                        type="radio"
                        name="publication-scope"
                        value={scope.value}
                        checked={state.input.publicationScope === scope.value}
                        onChange={() => changeInput('publicationScope', scope.value)}
                      />
                      <span>
                        <strong>{scope.label}</strong>
                        <small>{scope.description}</small>
                      </span>
                    </label>
                  ))}
                </div>
              </fieldset>
            </fieldset>

            <fieldset disabled={pending || finalized} hidden={wizardStep !== 2}>
              <legend>出力形式・言語・ブランド</legend>
              <p className="step-description">
                承認済みの出力プロファイルとブランドテンプレートから選択します。
              </p>

              <fieldset className="nested-fieldset">
                <legend>希望する出力形式 <RequiredMark /></legend>
                {state.catalog.outputProfiles.length === 0 ? (
                  <p className="field-warning">利用可能な出力形式を読み込めませんでした。</p>
                ) : (
                  <div className="checkbox-grid">
                    {state.catalog.outputProfiles.map((profile) => (
                      <label className="checkbox-option" key={profile.id}>
                        <input
                          type="checkbox"
                          checked={state.input.outputProfileIds.includes(profile.id)}
                          onChange={(event) =>
                            toggleOutputProfile(profile.id, event.currentTarget.checked)
                          }
                        />
                        <span>{profile.name}</span>
                      </label>
                    ))}
                  </div>
                )}
              </fieldset>

              <TextField
                id="project-language"
                label="使用言語"
                value={state.input.language}
                placeholder="例: ja-JP"
                onChange={(value) => changeOptionalText('language', value)}
                onBlur={(value) => trimOptionalText('language', value)}
              />

              <div className="field-group">
                <label className="field-label" htmlFor="project-brand-template">
                  ブランドテンプレート <RequiredMark />
                </label>
                <select
                  id="project-brand-template"
                  value={state.input.brandTemplateId ?? ''}
                  onChange={(event) =>
                    changeInput(
                      'brandTemplateId',
                      event.currentTarget.value.length === 0
                        ? null
                        : event.currentTarget.value,
                    )
                  }
                >
                  <option value="">選択してください</option>
                  {state.catalog.brandTemplates.map((template) => (
                    <option key={template.id} value={template.id}>
                      {template.name}
                    </option>
                  ))}
                </select>
              </div>
            </fieldset>

            <fieldset disabled={pending || finalized} hidden={wizardStep !== 3}>
              <legend>出演者と公開期限</legend>
              <p className="step-description">
                出演者は複数登録できます。公開期限がない場合も明示してください。
              </p>

              <fieldset className="nested-fieldset">
                <legend>出演者名 <RequiredMark /></legend>
                <div className="performer-list">
                  {state.input.performerNames.map((performer, index) => (
                    <div className="performer-row" key={index}>
                      <label className="visually-hidden" htmlFor={`project-performer-${index}`}>
                        出演者 {index + 1}
                      </label>
                      <input
                        id={`project-performer-${index}`}
                        type="text"
                        value={performer}
                        placeholder={`出演者 ${index + 1}`}
                        onChange={(event) => updatePerformer(index, event.currentTarget.value)}
                        onBlur={(event) =>
                          updatePerformer(index, event.currentTarget.value.trim())
                        }
                      />
                      <button
                        className="icon-button"
                        type="button"
                        aria-label={`出演者 ${index + 1} を削除`}
                        onClick={() => removePerformer(index)}
                      >
                        削除
                      </button>
                    </div>
                  ))}
                </div>
                <button className="secondary-button" type="button" onClick={addPerformer}>
                  出演者を追加
                </button>
              </fieldset>

              <fieldset className="nested-fieldset deadline-fieldset">
                <legend>必要な公開期限 <RequiredMark /></legend>
                <label className="checkbox-option no-deadline-option">
                  <input
                    type="checkbox"
                    checked={state.input.deadlineSelection?.hasDeadline === false}
                    onChange={(event) =>
                      changeInput(
                        'deadlineSelection',
                        event.currentTarget.checked
                          ? { hasDeadline: false, value: null }
                          : null,
                      )
                    }
                  />
                  <span>公開期限なし</span>
                </label>
                <div className="field-group inline-field">
                  <label className="field-label" htmlFor="project-deadline">
                    公開期限（UTC）
                  </label>
                  <input
                    id="project-deadline"
                    type="datetime-local"
                    value={deadlineInputValue(state.input.deadlineSelection)}
                    disabled={state.input.deadlineSelection?.hasDeadline === false}
                    onChange={changeDeadline}
                  />
                </div>
                <p className="field-note">
                  「公開期限なし」または日時のどちらかを指定してください。
                </p>
              </fieldset>
            </fieldset>

            <fieldset disabled={pending || finalized} hidden={wizardStep !== 4}>
              <legend>入力内容の確認</legend>
              <p className="step-description">
                下書きは未完成のまま保存できます。案件作成には10項目すべてが必要です。
              </p>
              <ProjectReview input={state.input} context={state} />
            </fieldset>

            <div className="completion-summary" aria-live="polite">
              {finalized ? (
                <p>この案件は作成済みです。</p>
              ) : complete ? (
                <p className="complete-message">必須項目がすべて入力されています。</p>
              ) : missingFields.length === 0 ? (
                <p className="field-warning">入力内容を修正してから案件を作成してください。</p>
              ) : (
                <details open={wizardStep === wizardSteps.length - 1}>
                  <summary>案件作成まで残り {missingFields.length} 項目</summary>
                  <ul>
                    {missingFields.map((field) => (
                      <li key={field}>{missingFieldLabels[field]}</li>
                    ))}
                  </ul>
                </details>
              )}
              {!withinRequestLimit ? (
                <p className="field-warning">
                  入力内容が大きすぎます。文章または出演者数を減らしてください。
                </p>
              ) : !saveable ? (
                <p className="field-warning">
                  入力中の項目に空欄、前後の空白、または重複があります。修正するか行を削除してください。
                </p>
              ) : null}
            </div>

            <div className="wizard-actions">
              <button
                className="quiet-button"
                type="button"
                disabled={pending}
                onClick={closeDraft}
              >
                {dirty ? '変更を破棄して一覧へ' : '一覧へ戻る'}
              </button>
              <div className="wizard-navigation">
                <button
                  className="secondary-button"
                  type="button"
                  disabled={wizardStep === 0}
                  onClick={() => setWizardStep((step) => Math.max(0, step - 1))}
                >
                  前へ
                </button>
                {wizardStep < wizardSteps.length - 1 && (
                  <button
                    className="secondary-button"
                    type="button"
                    onClick={() =>
                      setWizardStep((step) => Math.min(wizardSteps.length - 1, step + 1))
                    }
                  >
                    次へ
                  </button>
                )}
                <button
                  className="secondary-button"
                  type="submit"
                  disabled={pending || finalized || !saveable || !dirty}
                >
                  下書きを保存
                </button>
                <button
                  className="primary-button"
                  type="button"
                  disabled={pending || finalized || !complete}
                  onClick={finalizeDraft}
                >
                  案件を作成
                </button>
              </div>
            </div>
          </form>

          <p className="operation-status" role="status" aria-live="polite">
            {state.pending === null
              ? dirty
                ? '未保存の変更があります。'
                : '下書きは保存済みです。'
              : `${requestLabels[state.pending.method]}しています。`}
          </p>
        </section>
      )}
    </div>
  )
}

interface ProjectListProps {
  readonly projects: readonly ProjectListItem[]
  readonly disabled: boolean
  readonly onOpen: (draftId: string) => void
}

export function ProjectList({ projects, disabled, onOpen }: ProjectListProps) {
  if (projects.length === 0) {
    return (
      <div className="empty-state">
        <strong>案件はまだありません</strong>
        <p>作成権限がある場合は「新しい案件」から始められます。</p>
      </div>
    )
  }

  return (
    <div className="table-scroll" tabIndex={0} aria-label="案件一覧（横スクロール可能）">
      <table>
        <caption className="visually-hidden">閲覧を許可された案件の一覧</caption>
        <thead>
          <tr>
            <th scope="col">コンテンツ名</th>
            <th scope="col">エピソード番号</th>
            <th scope="col">元動画</th>
            <th scope="col">担当者</th>
            <th scope="col">公開区分</th>
            <th scope="col">編集状態</th>
            <th scope="col">レビュー状態</th>
            <th scope="col">出力形式</th>
            <th scope="col">更新日時</th>
          </tr>
        </thead>
        <tbody>
          {projects.map((project) => {
            const name = project.contentName ?? '名称未入力'
            return (
              <tr key={project.draftId}>
                <th scope="row">
                  <button
                    className="project-link"
                    type="button"
                    disabled={disabled}
                    aria-label={`${name}を開く`}
                    onClick={() => onOpen(project.draftId)}
                  >
                    {name}
                  </button>
                  <span className={`state-badge ${project.state}`}>
                    {project.state === 'finalized' ? '作成済み' : '下書き'}
                  </span>
                </th>
                <td>{project.episodeNumber ?? '—'}</td>
                <td>{sourceStatusLabels[project.sourceStatus]}</td>
                <td>{project.owner ?? '未設定'}</td>
                <td>
                  {project.publicationScope === null
                    ? '未設定'
                    : publicationScopeLabels[project.publicationScope]}
                </td>
                <td>{project.editState ?? '未設定'}</td>
                <td>{project.reviewState ?? '未設定'}</td>
                <td>
                  {project.outputFormats.length === 0 ? (
                    '未選択'
                  ) : (
                    <ul className="format-list">
                      {project.outputFormats.map((format) => (
                        <li key={format}>{format}</li>
                      ))}
                    </ul>
                  )}
                </td>
                <td>
                  <time dateTime={project.updatedAt}>{formatUtc(project.updatedAt)}</time>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

interface TextFieldProps {
  readonly id: string
  readonly label: string
  readonly value: string | null
  readonly placeholder: string
  readonly onChange: (value: string) => void
  readonly onBlur: (value: string) => void
}

function TextField({
  id,
  label,
  value,
  placeholder,
  onChange,
  onBlur,
}: TextFieldProps) {
  return (
    <div className="field-group">
      <label className="field-label" htmlFor={id}>
        {label} <RequiredMark />
      </label>
      <input
        id={id}
        type="text"
        value={value ?? ''}
        placeholder={placeholder}
        onChange={(event) => onChange(event.currentTarget.value)}
        onBlur={(event) => onBlur(event.currentTarget.value)}
      />
    </div>
  )
}

function TextAreaField(props: TextFieldProps) {
  return (
    <div className="field-group">
      <label className="field-label" htmlFor={props.id}>
        {props.label} <RequiredMark />
      </label>
      <textarea
        id={props.id}
        rows={4}
        value={props.value ?? ''}
        placeholder={props.placeholder}
        onChange={(event) => props.onChange(event.currentTarget.value)}
        onBlur={(event) => props.onBlur(event.currentTarget.value)}
      />
    </div>
  )
}

function RequiredMark() {
  return <span className="required-mark">必須</span>
}

interface ProjectReviewProps {
  readonly input: ProjectDraftInput
  readonly context: ProjectStateContextValue['state']
}

function ProjectReview({ input, context }: ProjectReviewProps) {
  const selectedProfiles = context.catalog.outputProfiles
    .filter((profile) => input.outputProfileIds.includes(profile.id))
    .map((profile) => profile.name)
  const selectedTemplate = context.catalog.brandTemplates.find(
    (template) => template.id === input.brandTemplateId,
  )

  return (
    <dl className="review-list">
      <ReviewItem
        term="元動画ファイル"
        value={sourceStatusLabels[getActiveSourceStatus(
          input,
          context.draft,
        )]}
      />
      <ReviewItem term="コンテンツ名" value={input.contentName} />
      <ReviewItem term="動画の目的" value={input.purpose} />
      <ReviewItem term="想定視聴者" value={input.audience} />
      <ReviewItem
        term="公開範囲"
        value={
          input.publicationScope === null
            ? null
            : publicationScopeLabels[input.publicationScope]
        }
      />
      <ReviewItem term="希望する出力形式" value={joinValues(selectedProfiles)} />
      <ReviewItem term="使用言語" value={input.language} />
      <ReviewItem term="出演者名" value={joinValues(input.performerNames)} />
      <ReviewItem term="必要な公開期限" value={formatDeadline(input.deadlineSelection)} />
      <ReviewItem
        term="ブランドテンプレート"
        value={selectedTemplate?.name ?? null}
      />
    </dl>
  )
}

function ReviewItem({ term, value }: { readonly term: string; readonly value: string | null }) {
  return (
    <div>
      <dt>{term}</dt>
      <dd>{value ?? '未入力'}</dd>
    </div>
  )
}

function getActiveSourceStatus(
  input: ProjectDraftInput,
  draft: ProjectStateContextValue['state']['draft'],
): SourceStatus {
  return getProjectInputSourceStatus(input, draft)
}

function deadlineInputValue(deadline: ProjectDeadlineInput): string {
  return deadline?.hasDeadline === true ? deadline.value.slice(0, 16) : ''
}

function formatDeadline(deadline: ProjectDeadlineInput): string | null {
  if (deadline === null) {
    return null
  }

  return deadline.hasDeadline ? formatUtc(deadline.value) : '公開期限なし'
}

function joinValues(values: readonly string[]): string | null {
  return values.length === 0 ? null : values.join('、')
}

function formatUtc(value: string): string {
  const date = new Date(value)
  const year = date.getUTCFullYear().toString().padStart(4, '0')
  const month = (date.getUTCMonth() + 1).toString().padStart(2, '0')
  const day = date.getUTCDate().toString().padStart(2, '0')
  const hour = date.getUTCHours().toString().padStart(2, '0')
  const minute = date.getUTCMinutes().toString().padStart(2, '0')
  return `${year}/${month}/${day} ${hour}:${minute} UTC`
}