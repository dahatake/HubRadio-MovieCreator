import {
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import {
  getRovingIndexAfterKey,
  layoutRectToText,
  manualLayoutModeLabels,
  manualLayoutModes,
  manualLayoutSourceCounts,
  parseNormalizedLayoutRect,
  type LayoutRectText,
} from './layoutForm'
import type {
  LayoutManualOperation,
  LayoutSnapshot,
  ManualLayoutMode,
} from './layoutProtocol'

export interface ManualLayoutEditorProps {
  readonly snapshot: LayoutSnapshot
  readonly disabled: boolean
  readonly applyOperation: (operation: LayoutManualOperation) => void
}

type StaticCropDecision = LayoutManualOperation['staticCropDecision']
type GeometryKind = 'crop' | 'subject' | 'destination'
type CoordinateKind = keyof LayoutRectText

interface SourceDraft {
  readonly sourceAssetId: string
  readonly crop: LayoutRectText
  readonly subject: LayoutRectText
  readonly destination: LayoutRectText
}

export function ManualLayoutEditor({
  snapshot,
  disabled,
  applyOperation,
}: ManualLayoutEditorProps) {
  const currentSelection = snapshot.manualSelection
  const [mode, setMode] = useState<ManualLayoutMode | null>(
    currentSelection?.mode ?? null,
  )
  const [staticCropDecision, setStaticCropDecision] =
    useState<StaticCropDecision | null>(null)
  const [sources, setSources] = useState<readonly SourceDraft[]>(() =>
    currentSelection === null
      ? Object.freeze([])
      : draftsFromCurrentSelection(snapshot),
  )
  const [templateId, setTemplateId] = useState(
    currentSelection?.template?.templateId ?? '',
  )
  const [validationMessage, setValidationMessage] = useState<string | null>(null)
  const modeButtons = useRef<Array<HTMLButtonElement | null>>([])

  const chooseMode = (nextMode: ManualLayoutMode): void => {
    setMode(nextMode)
    setStaticCropDecision(null)
    setValidationMessage(null)
    if (currentSelection?.mode === nextMode) {
      setSources(draftsFromCurrentSelection(snapshot))
      setTemplateId(currentSelection.template?.templateId ?? '')
      return
    }
    setSources(Object.freeze(
      Array.from(
        { length: manualLayoutSourceCounts[nextMode] },
        () => emptySourceDraft(),
      ),
    ))
    setTemplateId('')
  }

  const onModeKeyDown = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ): void => {
    const nextIndex = getRovingIndexAfterKey(
      index,
      event.key,
      manualLayoutModes.length,
    )
    if (nextIndex === null) {
      return
    }
    event.preventDefault()
    const nextMode = manualLayoutModes[nextIndex]!
    chooseMode(nextMode)
    modeButtons.current[nextIndex]?.focus()
  }

  const updateSourceId = (index: number, sourceAssetId: string): void => {
    setSources((current) => Object.freeze(current.map((source, sourceIndex) =>
      sourceIndex === index ? { ...source, sourceAssetId } : source,
    )))
    setValidationMessage(null)
  }

  const updateCoordinate = (
    index: number,
    geometry: GeometryKind,
    coordinate: CoordinateKind,
    value: string,
  ): void => {
    setSources((current) => Object.freeze(current.map((source, sourceIndex) => {
      if (sourceIndex !== index) {
        return source
      }
      return {
        ...source,
        [geometry]: {
          ...source[geometry],
          [coordinate]: value,
        },
      }
    })))
    setValidationMessage(null)
  }

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (mode === null || staticCropDecision === null) {
      setValidationMessage('モードと static crop 判断を明示的に選択してください。')
      return
    }
    const normalizedSources = sources.map((source) => ({
      sourceAssetId: source.sourceAssetId,
      crop: parseNormalizedLayoutRect(source.crop),
      subject: parseNormalizedLayoutRect(source.subject),
      destination: parseNormalizedLayoutRect(source.destination),
    }))
    if (normalizedSources.some((source) =>
      source.sourceAssetId.length === 0 ||
      source.crop === null ||
      source.subject === null ||
      source.destination === null)) {
      setValidationMessage('素材と、約分済み n/d の frame 内座標をすべて指定してください。')
      return
    }
    if (mode === 'fixedTemplatePlacement' && templateId.length === 0) {
      setValidationMessage('固定テンプレート配置には既存 template を選択してください。')
      return
    }

    setValidationMessage(null)
    applyOperation({
      kind: 'setManualLayout',
      mode,
      staticCropDecision,
      sources: normalizedSources.map((source) => ({
        sourceAssetId: source.sourceAssetId,
        crop: source.crop!,
        subject: source.subject!,
        destination: source.destination!,
      })),
      templateId: mode === 'fixedTemplatePlacement' ? templateId : null,
    })
  }

  return (
    <section className="layout-editor-card" aria-labelledby="manual-layout-title">
      <div className="layout-card-heading">
        <div>
          <p className="section-kicker">Explicit manual composition</p>
          <h3 id="manual-layout-title">手動被写体・crop・配置</h3>
        </div>
        <span className="layout-evidence-badge">
          {currentSelection === null
            ? '現行 selection なし'
            : `現行: ${manualLayoutModeLabels[currentSelection.mode]}`}
        </span>
      </div>
      <p className="layout-card-note">
        face candidate、confidence、provider は受け取りません。素材と exact normalized rectangle を人が指定します。
      </p>

      <form className="manual-layout-form" onSubmit={submit}>
        <fieldset disabled={disabled}>
          <legend>1. レイアウトモード</legend>
          <div
            className="manual-mode-grid"
            role="radiogroup"
            aria-label="手動レイアウトモード"
          >
            {manualLayoutModes.map((candidate, index) => (
              <button
                key={candidate}
                ref={(element) => {
                  modeButtons.current[index] = element
                }}
                type="button"
                role="radio"
                aria-checked={mode === candidate}
                tabIndex={mode === candidate || (mode === null && index === 0) ? 0 : -1}
                onClick={() => chooseMode(candidate)}
                onKeyDown={(event) => onModeKeyDown(event, index)}
              >
                {manualLayoutModeLabels[candidate]}
                <small>{manualLayoutSourceCounts[candidate]} 素材</small>
              </button>
            ))}
          </div>
        </fieldset>

        {mode !== null && (
          <>
            <fieldset disabled={disabled}>
              <legend>2. static crop 判断</legend>
              <div className="layout-radio-row">
                <label>
                  <input
                    type="radio"
                    name="static-crop-decision"
                    checked={staticCropDecision === 'singleStaticCropSufficient'}
                    onChange={() => setStaticCropDecision('singleStaticCropSufficient')}
                  />
                  単一 static crop で十分
                </label>
                <label>
                  <input
                    type="radio"
                    name="static-crop-decision"
                    checked={staticCropDecision === 'manualKeyframesRequired'}
                    onChange={() => setStaticCropDecision('manualKeyframesRequired')}
                  />
                  手動 keyframe が必要
                </label>
              </div>
            </fieldset>

            <fieldset disabled={disabled}>
              <legend>3. 素材と normalized coordinates</legend>
              <p className="field-help">
                各値は約分済みの n/d 形式です。crop 内に subject を含め、destination は 9:16 output frame 内へ指定します。
              </p>
              <div className="manual-source-list">
                {sources.map((source, index) => (
                  <SourceGeometryEditor
                    key={index}
                    index={index}
                    source={source}
                    sourceOptions={snapshot.sources}
                    updateSourceId={updateSourceId}
                    updateCoordinate={updateCoordinate}
                  />
                ))}
              </div>
            </fieldset>

            {mode === 'fixedTemplatePlacement' && (
              <label className="layout-template-field">
                <span>既存 template evidence</span>
                <select
                  value={templateId}
                  disabled={disabled}
                  onChange={(event) => setTemplateId(event.currentTarget.value)}
                >
                  <option value="">選択してください</option>
                  {snapshot.templates.map((template) => (
                    <option key={template.templateId} value={template.templateId}>
                      {template.name}
                    </option>
                  ))}
                </select>
              </label>
            )}

            {validationMessage !== null && (
              <p className="layout-input-error" role="alert">
                {validationMessage}
              </p>
            )}
            <button className="primary-button" type="submit" disabled={disabled}>
              手動レイアウトを検証して適用
            </button>
          </>
        )}
      </form>
    </section>
  )
}

function SourceGeometryEditor({
  index,
  source,
  sourceOptions,
  updateSourceId,
  updateCoordinate,
}: Readonly<{
  index: number
  source: SourceDraft
  sourceOptions: LayoutSnapshot['sources']
  updateSourceId: (index: number, value: string) => void
  updateCoordinate: (
    index: number,
    geometry: GeometryKind,
    coordinate: CoordinateKind,
    value: string,
  ) => void
}>) {
  return (
    <fieldset className="manual-source-card">
      <legend>素材 {index + 1}</legend>
      <label>
        <span>Source asset ID</span>
        <select
          value={source.sourceAssetId}
          onChange={(event) => updateSourceId(index, event.currentTarget.value)}
        >
          <option value="">選択してください</option>
          {sourceOptions.map((option) => (
            <option key={option.sourceAssetId} value={option.sourceAssetId}>
              {option.sourceAssetId} ({option.displayWidth}×{option.displayHeight})
            </option>
          ))}
        </select>
      </label>
      <ExactRectEditor
        id={`manual-source-${index}-crop`}
        legend="Source crop"
        value={source.crop}
        onChange={(coordinate, value) =>
          updateCoordinate(index, 'crop', coordinate, value)}
      />
      <ExactRectEditor
        id={`manual-source-${index}-subject`}
        legend="Subject"
        value={source.subject}
        onChange={(coordinate, value) =>
          updateCoordinate(index, 'subject', coordinate, value)}
      />
      <ExactRectEditor
        id={`manual-source-${index}-destination`}
        legend="9:16 destination"
        value={source.destination}
        onChange={(coordinate, value) =>
          updateCoordinate(index, 'destination', coordinate, value)}
      />
    </fieldset>
  )
}

export function ExactRectEditor({
  id,
  legend,
  value,
  onChange,
}: Readonly<{
  id: string
  legend: string
  value: LayoutRectText
  onChange: (coordinate: CoordinateKind, value: string) => void
}>) {
  return (
    <fieldset className="exact-rect-editor">
      <legend>{legend}</legend>
      {(['x', 'y', 'width', 'height'] as const).map((coordinate) => (
        <label key={coordinate} htmlFor={`${id}-${coordinate}`}>
          <span>{coordinate}</span>
          <input
            id={`${id}-${coordinate}`}
            type="text"
            inputMode="numeric"
            pattern="(?:0|[1-9][0-9]{0,18})/[1-9][0-9]{0,18}"
            placeholder="n/d"
            value={value[coordinate]}
            onChange={(event) => onChange(coordinate, event.currentTarget.value)}
          />
        </label>
      ))}
    </fieldset>
  )
}

function draftsFromCurrentSelection(snapshot: LayoutSnapshot): readonly SourceDraft[] {
  return Object.freeze((snapshot.manualSelection?.placements ?? []).map((placement) => ({
    sourceAssetId: placement.sourceAssetId,
    crop: layoutRectToText(placement.sourceCrop.rect),
    subject: layoutRectToText(placement.subject.rect),
    destination: layoutRectToText(placement.destination.rect),
  })))
}

function emptySourceDraft(): SourceDraft {
  return {
    sourceAssetId: '',
    crop: emptyRectText(),
    subject: emptyRectText(),
    destination: emptyRectText(),
  }
}

function emptyRectText(): LayoutRectText {
  return { x: '', y: '', width: '', height: '' }
}
