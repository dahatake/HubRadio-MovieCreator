import {
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react'

import {
  getRovingIndexAfterKey,
  layoutRectToText,
  parseNormalizedLayoutRect,
  type LayoutRectText,
} from './layoutForm'
import { ExactRectEditor } from './ManualLayoutEditor'
import type {
  LayoutInterpolation,
  LayoutKeyframeOperation,
  LayoutKeyframeTrack,
  LayoutSnapshot,
} from './layoutProtocol'

export interface KeyframeEditorProps {
  readonly snapshot: LayoutSnapshot
  readonly disabled: boolean
  readonly applyOperation: (operation: LayoutKeyframeOperation) => void
}

interface KeyframeDraft {
  readonly timeValue: string
  readonly timeRate: string
  readonly sourceCrop: LayoutRectText
  readonly destination: LayoutRectText
  readonly interpolation: LayoutInterpolation
}

interface SegmentDraft {
  readonly range: LayoutKeyframeTrack['segments'][number]['range']
  readonly keyframes: readonly KeyframeDraft[]
}

export function KeyframeEditor({
  snapshot,
  disabled,
  applyOperation,
}: KeyframeEditorProps) {
  const [selectedIndex, setSelectedIndex] = useState(0)
  const [segments, setSegments] = useState<readonly SegmentDraft[]>(() =>
    draftSegments(snapshot.keyframeTracks[0]),
  )
  const [validationMessage, setValidationMessage] = useState<string | null>(null)
  const trackButtons = useRef<Array<HTMLButtonElement | null>>([])
  const track = snapshot.keyframeTracks[selectedIndex]

  if (track === undefined) {
    return (
      <section className="layout-editor-card" aria-labelledby="keyframe-layout-title">
        <p className="section-kicker">Native keyframe evidence</p>
        <h3 id="keyframe-layout-title">手動 keyframe</h3>
        <div
          className="layout-no-keyframe"
          role={snapshot.manualSelection?.status === 'manualKeyframesRequired' ? 'alert' : 'note'}
        >
          <strong>編集可能な keyframe track はありません</strong>
          <p>
            native snapshot に存在しない source range、destination slot、補間値は画面側で補完しません。
          </p>
        </div>
      </section>
    )
  }

  const chooseTrack = (index: number): void => {
    const next = snapshot.keyframeTracks[index]
    if (next === undefined) {
      return
    }
    setSelectedIndex(index)
    setSegments(draftSegments(next))
    setValidationMessage(null)
  }

  const onTrackKeyDown = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ): void => {
    const nextIndex = getRovingIndexAfterKey(
      index,
      event.key,
      snapshot.keyframeTracks.length,
    )
    if (nextIndex === null) {
      return
    }
    event.preventDefault()
    chooseTrack(nextIndex)
    trackButtons.current[nextIndex]?.focus()
  }

  const updateKeyframe = (
    segmentIndex: number,
    keyframeIndex: number,
    update: (draft: KeyframeDraft) => KeyframeDraft,
  ): void => {
    setSegments((current) => Object.freeze(current.map((segment, currentSegment) =>
      currentSegment === segmentIndex
        ? {
            ...segment,
            keyframes: Object.freeze(segment.keyframes.map((keyframe, currentKeyframe) =>
              currentKeyframe === keyframeIndex ? update(keyframe) : keyframe,
            )),
          }
        : segment,
    )))
    setValidationMessage(null)
  }

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    const normalized = segments.map((segment) => ({
      range: segment.range,
      keyframes: segment.keyframes.map((keyframe) => ({
        time: { value: keyframe.timeValue, rate: keyframe.timeRate },
        sourceCrop: parseNormalizedLayoutRect(keyframe.sourceCrop),
        destination: parseNormalizedLayoutRect(keyframe.destination),
        interpolation: keyframe.interpolation,
      })),
    }))
    if (normalized.some((segment) => segment.keyframes.some((keyframe) =>
      keyframe.time.value.length === 0 ||
      keyframe.time.rate.length === 0 ||
      keyframe.sourceCrop === null ||
      keyframe.destination === null))) {
      setValidationMessage('時刻とすべての rectangle を約分済み exact 値で指定してください。')
      return
    }

    setValidationMessage(null)
    applyOperation({
      kind: 'setKeyframes',
      sourceAssetId: track.sourceAssetId,
      destinationSlot: track.destinationSlot,
      segments: normalized.map((segment) => ({
        range: segment.range,
        keyframes: segment.keyframes.map((keyframe) => ({
          time: keyframe.time,
          sourceCrop: keyframe.sourceCrop!,
          destination: keyframe.destination!,
          interpolation: keyframe.interpolation,
        })),
      })),
      evaluationTime: track.evaluation.requestedTime,
    })
  }

  return (
    <section className="layout-editor-card" aria-labelledby="keyframe-layout-title">
      <div className="layout-card-heading">
        <div>
          <p className="section-kicker">Native keyframe evidence</p>
          <h3 id="keyframe-layout-title">手動 keyframe</h3>
        </div>
        <span className="layout-evidence-badge">
          評価 {track.evaluation.requestedTime.value}/{track.evaluation.requestedTime.rate}
        </span>
      </div>
      <p className="layout-card-note">
        native track の segment・source・slot を維持し、exact time と rectangle だけを編集します。preview は応答の評価済み整数 pixels を使います。
      </p>

      <div className="keyframe-track-tabs" role="tablist" aria-label="Keyframe tracks">
        {snapshot.keyframeTracks.map((candidate, index) => (
          <button
            key={`${candidate.sourceAssetId}-${candidate.destinationSlot}`}
            ref={(element) => {
              trackButtons.current[index] = element
            }}
            type="button"
            role="tab"
            aria-selected={selectedIndex === index}
            tabIndex={selectedIndex === index ? 0 : -1}
            onClick={() => chooseTrack(index)}
            onKeyDown={(event) => onTrackKeyDown(event, index)}
          >
            {candidate.destinationSlot}
          </button>
        ))}
      </div>

      <form className="keyframe-form" onSubmit={submit}>
        <fieldset disabled={disabled}>
          <legend>{track.destinationSlot} track</legend>
          <dl className="layout-keyframe-evidence">
            <div><dt>Source asset</dt><dd><code>{track.sourceAssetId}</code></dd></div>
            <div><dt>Source pixels</dt><dd>{track.sourceWidth} × {track.sourceHeight}</dd></div>
            <div><dt>Output pixels</dt><dd>{track.outputWidth} × {track.outputHeight}</dd></div>
            <div><dt>評価 segment</dt><dd>{track.evaluation.segmentIndex}</dd></div>
          </dl>
          <div className="keyframe-segment-list">
            {segments.map((segment, segmentIndex) => (
              <fieldset className="keyframe-segment" key={segmentIndex}>
                <legend>
                  Segment {segmentIndex + 1}: {formatTime(segment.range.start)} + {formatTime(segment.range.duration)}
                </legend>
                {segment.keyframes.map((keyframe, keyframeIndex) => (
                  <fieldset className="keyframe-card" key={keyframeIndex}>
                    <legend>Keyframe {keyframeIndex + 1}</legend>
                    <div className="keyframe-time-row">
                      <label>
                        <span>time value</span>
                        <input
                          type="text"
                          inputMode="numeric"
                          value={keyframe.timeValue}
                          onChange={(event) => updateKeyframe(
                            segmentIndex,
                            keyframeIndex,
                            (current) => ({ ...current, timeValue: event.currentTarget.value }),
                          )}
                        />
                      </label>
                      <label>
                        <span>time rate</span>
                        <input
                          type="text"
                          inputMode="numeric"
                          value={keyframe.timeRate}
                          onChange={(event) => updateKeyframe(
                            segmentIndex,
                            keyframeIndex,
                            (current) => ({ ...current, timeRate: event.currentTarget.value }),
                          )}
                        />
                      </label>
                      <label>
                        <span>interpolation</span>
                        <select
                          value={keyframe.interpolation}
                          onChange={(event) => updateKeyframe(
                            segmentIndex,
                            keyframeIndex,
                            (current) => ({
                              ...current,
                              interpolation: event.currentTarget.value as LayoutInterpolation,
                            }),
                          )}
                        >
                          <option value="hold">hold</option>
                          <option value="linear">linear</option>
                        </select>
                      </label>
                    </div>
                    <div className="keyframe-rect-grid">
                      <ExactRectEditor
                        id={`keyframe-${segmentIndex}-${keyframeIndex}-source`}
                        legend="Source crop"
                        value={keyframe.sourceCrop}
                        onChange={(coordinate, value) => updateKeyframe(
                          segmentIndex,
                          keyframeIndex,
                          (current) => ({
                            ...current,
                            sourceCrop: { ...current.sourceCrop, [coordinate]: value },
                          }),
                        )}
                      />
                      <ExactRectEditor
                        id={`keyframe-${segmentIndex}-${keyframeIndex}-destination`}
                        legend="9:16 destination"
                        value={keyframe.destination}
                        onChange={(coordinate, value) => updateKeyframe(
                          segmentIndex,
                          keyframeIndex,
                          (current) => ({
                            ...current,
                            destination: { ...current.destination, [coordinate]: value },
                          }),
                        )}
                      />
                    </div>
                  </fieldset>
                ))}
              </fieldset>
            ))}
          </div>
          {validationMessage !== null && (
            <p className="layout-input-error" role="alert">{validationMessage}</p>
          )}
          <button className="primary-button" type="submit" disabled={disabled}>
            Keyframe track を検証して適用
          </button>
        </fieldset>
      </form>
    </section>
  )
}

function draftSegments(track: LayoutKeyframeTrack | undefined): readonly SegmentDraft[] {
  if (track === undefined) {
    return Object.freeze([])
  }
  return Object.freeze(track.segments.map((segment) => ({
    range: segment.range,
    keyframes: Object.freeze(segment.keyframes.map((keyframe) => ({
      timeValue: keyframe.time.value,
      timeRate: keyframe.time.rate,
      sourceCrop: layoutRectToText(keyframe.sourceCrop),
      destination: layoutRectToText(keyframe.destination),
      interpolation: keyframe.interpolation,
    }))),
  })))
}

function formatTime(value: LayoutKeyframeTrack['range']['start']): string {
  return `${value.value}/${value.rate}`
}
