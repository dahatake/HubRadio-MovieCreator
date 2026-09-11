import {
  useCallback,
  useEffect,
  useMemo,
  useReducer,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
  type PointerEvent,
  type WheelEvent,
} from 'react'

import { createHostBridgeClient, type BridgeClient } from '../../bridge'
import {
  exactTimeToDerivedSeconds,
  formatExactRationalTime,
} from './rationalTime'
import {
  TIMELINE_CANVAS_HEIGHT,
  clampTimelineScroll,
  computeMoveTargetIndex,
  createTimelineLayout,
  fitTimelineZoom,
  hitTestTimelineElement,
  syncCanvasBackingStore,
  totalTimelineWidth,
  zoomTimelineAroundPointer,
} from './timelineGeometry'
import {
  createSplitOperationAtOutputTime,
  createTrimOperations,
  resolveTimelinePlayhead,
  type TimelinePlayerPosition,
  type TrimEdge,
} from './timelineModel'
import {
  createTimelineApplyRequest,
  createTimelineReadRequest,
  isCanonicalTimelineId,
  type TimelineApplyResponse,
  type TimelineOperation,
  type TimelineReadResponse,
} from './timelineProtocol'
import {
  renderTimelineCanvas,
  type TimelineCanvasPalette,
} from './timelineRenderer'
import {
  canRedoTimeline,
  canUndoTimeline,
  initialTimelineState,
  selectPendingTimelineOperations,
  timelineStateReducer,
  type TimelineIssue,
} from './timelineState'

export type TimelineBridgeClient = Pick<
  BridgeClient,
  | 'sendTimelineRead'
  | 'sendTimelineApply'
  | 'abandonTimelineRequest'
  | 'onTimelineReadResponse'
  | 'onTimelineApplyResponse'
  | 'dispose'
>

export interface TimelineEditorProps {
  readonly createClient?: () => TimelineBridgeClient
  readonly createRequestId?: () => string
  readonly initialProjectId?: string
  readonly playerPosition?: TimelinePlayerPosition | null
  readonly context?: Readonly<{ projectId: string | null }>
  readonly active?: boolean
  readonly onDirtyChange?: (dirty: boolean) => void
}

type PointerInteraction =
  | Readonly<{
      kind: 'move'
      pointerId: number
      elementId: string
      startX: number
      currentX: number
    }>
  | Readonly<{
      kind: 'pan'
      pointerId: number
      startX: number
      startScroll: number
    }>

const issueMessages: Readonly<Record<TimelineIssue, string>> = {
  bridgeUnavailable: 'ネイティブホストへ接続できません。',
  sendFailed: 'タイムライン要求を送信できませんでした。',
  forbidden: 'このタイムラインを操作する権限がありません。',
  notFound: 'この案件の編集タイムラインはまだありません。',
  unavailable: 'タイムラインを現在利用できません。',
  invalidOperation: '未送信操作を適用できませんでした。',
  conflict: '別の編集が保存されました。未送信操作を保持したまま再読み込みしてください。',
  intentInvalidAfterReload: '再読み込み後の最新版には未送信操作を再生できません。内容を確認して破棄してください。',
  invalidResponse: 'ホスト応答の対応関係を確認できませんでした。',
}

export function TimelineEditor(props: TimelineEditorProps) {
  // Only a workflow project change resets the editor; visibility never does.
  const projectKey = props.context === undefined
    ? 'standalone'
    : `workflow:${props.context.projectId ?? ''}`
  return <TimelineEditorSession key={projectKey} {...props} />
}

function TimelineEditorSession({
  createClient = createHostBridgeClient,
  createRequestId = defaultRequestId,
  initialProjectId = '',
  playerPosition = null,
  context,
  active = true,
  onDirtyChange,
}: TimelineEditorProps) {
  const hasContext = context !== undefined
  const contextProjectId = context?.projectId ?? null
  const [state, dispatch] = useReducer(timelineStateReducer, {
    ...initialTimelineState,
    projectIdInput: hasContext ? contextProjectId ?? '' : initialProjectId,
  })
  const [pixelsPerSecond, setPixelsPerSecond] = useState(1)
  const [scrollOffset, setScrollOffset] = useState(0)
  const [viewportWidth, setViewportWidth] = useState(0)
  const [canvasRevision, setCanvasRevision] = useState(0)
  const [interaction, setInteraction] = useState<PointerInteraction | null>(null)
  const clientRef = useRef<TimelineBridgeClient | null>(null)
  const factoryRef = useRef(createClient)
  const onDirtyChangeRef = useRef(onDirtyChange)
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const fittedWorkingCopyRef = useRef<string | null>(null)
  const automaticLoadRef = useRef(false)

  // Inline prop identities must not restart the keyed session or notify again.
  useEffect(() => {
    factoryRef.current = createClient
    onDirtyChangeRef.current = onDirtyChange
  }, [createClient, onDirtyChange])

  useEffect(() => {
    if (
      !hasContext &&
      initialProjectId.length > 0 &&
      initialProjectId !== state.projectIdInput &&
      state.pendingRequest === null &&
      state.baseline === null
    ) {
      dispatch({ type: 'projectIdInputChanged', value: initialProjectId })
    }
  }, [hasContext, initialProjectId, state.baseline, state.pendingRequest, state.projectIdInput])

  useEffect(() => {
    if (hasContext && !isCanonicalTimelineId(contextProjectId ?? '')) return
    let client: TimelineBridgeClient
    try {
      client = factoryRef.current()
    } catch {
      dispatch({ type: 'bridgeUnavailable' })
      return
    }

    let unsubscribeRead: (() => void) | null = null
    let unsubscribeApply: (() => void) | null = null
    try {
      unsubscribeRead = client.onTimelineReadResponse(
        (response: TimelineReadResponse) => {
          dispatch({ type: 'readReceived', response })
        },
      )
      unsubscribeApply = client.onTimelineApplyResponse(
        (response: TimelineApplyResponse) => {
          dispatch({ type: 'applyReceived', response })
        },
      )
    } catch {
      unsubscribeRead?.()
      unsubscribeApply?.()
      client.dispose()
      dispatch({ type: 'bridgeUnavailable' })
      return
    }

    clientRef.current = client
    return () => {
      if (clientRef.current === client) clientRef.current = null
      try {
        unsubscribeRead?.()
        unsubscribeApply?.()
      } finally {
        client.dispose()
      }
    }
  }, [contextProjectId, hasContext])

  useEffect(() => {
    if (!active) setInteraction(null)
  }, [active])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!active || canvas === null) return
    const updateSize = (): void => {
      const width = measureVisibleCanvasWidth(canvas)
      if (!(width > 0)) return
      const ratio = typeof window === 'undefined' ? 1 : window.devicePixelRatio
      const previousWidth = canvas.width
      const previousHeight = canvas.height
      syncCanvasBackingStore(canvas, width, TIMELINE_CANVAS_HEIGHT, ratio)
      setViewportWidth(width)
      if (canvas.width !== previousWidth || canvas.height !== previousHeight) {
        setCanvasRevision((current) => current + 1)
      }
    }
    updateSize()
    window.addEventListener('resize', updateSize)
    const observer = typeof ResizeObserver === 'undefined'
      ? null
      : new ResizeObserver(updateSize)
    observer?.observe(canvas.parentElement ?? canvas)
    return () => {
      window.removeEventListener('resize', updateSize)
      observer?.disconnect()
    }
  }, [active, state.baseline?.workingCopyId])

  useEffect(() => {
    if (state.baseline === null) {
      fittedWorkingCopyRef.current = null
      return
    }
    if (
      !active ||
      viewportWidth <= 0 ||
      !(measureVisibleCanvasWidth(canvasRef.current) > 0)
    ) {
      return
    }
    if (fittedWorkingCopyRef.current === state.baseline.workingCopyId) return
    fittedWorkingCopyRef.current = state.baseline.workingCopyId
    setPixelsPerSecond(fitTimelineZoom(state.previewElements, viewportWidth))
    setScrollOffset(0)
  }, [active, state.baseline, state.previewElements, viewportWidth])

  useEffect(() => {
    if (
      !active ||
      viewportWidth <= 0 ||
      !(measureVisibleCanvasWidth(canvasRef.current) > 0)
    ) {
      return
    }
    setScrollOffset((current) =>
      clampTimelineScroll(
        current,
        totalTimelineWidth(state.previewElements, pixelsPerSecond),
        viewportWidth,
      ),
    )
  }, [active, pixelsPerSecond, state.previewElements, viewportWidth])

  const layout = useMemo(
    () =>
      createTimelineLayout(
        state.previewElements,
        pixelsPerSecond,
        scrollOffset,
      ),
    [pixelsPerSecond, scrollOffset, state.previewElements],
  )
  const selectedIds = useMemo(
    () => new Set(state.selectedElementIds),
    [state.selectedElementIds],
  )
  const playhead = useMemo(
    () =>
      state.baseline === null
        ? null
        : resolveTimelinePlayhead(
            state.previewElements,
            playerPosition,
            state.baseline.projectId,
            state.pendingGroups.length > 0,
          ),
    [
      playerPosition,
      state.baseline,
      state.pendingGroups.length,
      state.previewElements,
    ],
  )
  const dropTargetIndex =
    interaction?.kind === 'move'
      ? computeMoveTargetIndex(
          state.previewElements,
          interaction.elementId,
          interaction.currentX,
          viewportWidth,
          pixelsPerSecond,
          scrollOffset,
        )
      : null

  useEffect(() => {
    const canvas = canvasRef.current
    if (
      !active ||
      canvas === null ||
      viewportWidth <= 0 ||
      !(measureVisibleCanvasWidth(canvas) > 0) ||
      typeof CanvasRenderingContext2D === 'undefined'
    ) {
      return
    }
    const context = canvas.getContext('2d')
    if (context === null) return
    const scale = canvas.width / viewportWidth
    context.setTransform(scale, 0, 0, scale, 0, 0)
    renderTimelineCanvas(context, {
      width: viewportWidth,
      pixelsPerSecond,
      scrollOffset,
      layout,
      selectedIds,
      draggedElementId:
        interaction?.kind === 'move' ? interaction.elementId : null,
      dropTargetIndex,
      playheadSeconds:
        playhead === null ? null : exactTimeToDerivedSeconds(playhead),
      palette: readPalette(canvas),
    })
  }, [
    active,
    canvasRevision,
    dropTargetIndex,
    interaction,
    layout,
    pixelsPerSecond,
    playhead,
    scrollOffset,
    selectedIds,
    viewportWidth,
  ])

  const load = useCallback(
    (preservePending: boolean): void => {
      const projectId = preservePending
        ? state.baseline?.projectId ?? state.projectIdInput
        : state.projectIdInput
      if (
        state.pendingRequest !== null ||
        !isCanonicalTimelineId(projectId)
      ) {
        return
      }
      const client = clientRef.current
      if (client === null) {
        dispatch({ type: 'bridgeUnavailable' })
        return
      }
      const requestId = createRequestId().toLowerCase()
      const request = createTimelineReadRequest(
        requestId,
        projectId,
      )
      dispatch({
        type: 'readRequested',
        requestId,
        projectId,
        preservePending,
      })
      try {
        client.sendTimelineRead(request)
      } catch {
        client.abandonTimelineRequest(requestId)
        dispatch({ type: 'sendFailed', requestId })
      }
    },
    [
      createRequestId,
      state.baseline,
      state.pendingRequest,
      state.projectIdInput,
    ],
  )

  useEffect(() => {
    if (
      !active ||
      !hasContext ||
      !isCanonicalTimelineId(contextProjectId ?? '') ||
      automaticLoadRef.current
    ) {
      return
    }
    let superseded = false
    // Cancel StrictMode's discarded setup before sending through the live client.
    void Promise.resolve().then(() => {
      if (superseded || automaticLoadRef.current) return
      automaticLoadRef.current = true
      load(false)
    })
    return () => {
      superseded = true
    }
  }, [active, contextProjectId, hasContext, load])

  const applyPending = (): void => {
    const client = clientRef.current
    const baseline = state.baseline
    const operations = selectPendingTimelineOperations(state)
    if (
      !active ||
      client === null ||
      baseline === null ||
      operations.length === 0 ||
      state.pendingRequest !== null ||
      !state.intentValid
    ) {
      return
    }
    const requestId = createRequestId().toLowerCase()
    dispatch({ type: 'applyRequested', requestId })
    try {
      const request = createTimelineApplyRequest(
        requestId,
        baseline.projectId,
        baseline.revision,
        operations,
      )
      client.sendTimelineApply(request)
    } catch {
      try {
        client.abandonTimelineRequest(requestId)
      } catch {
        // The failed client may already have disposed its correlation state.
      }
      dispatch({ type: 'sendFailed', requestId })
    }
  }

  const queueSelected = (kind: 'keep' | 'delete'): void => {
    if (!active) return
    const selected = new Set(state.selectedElementIds)
    const operations = state.previewElements
      .filter((element) => selected.has(element.id))
      .map<TimelineOperation>((element) =>
        Object.freeze({ kind, elementId: element.id }),
      )
    dispatch({ type: 'operationsQueued', operations })
  }

  const splitAtPlayhead = (): void => {
    const elementId = state.selectedElementIds[0]
    if (
      !active ||
      playhead === null ||
      elementId === undefined ||
      state.selectedElementIds.length !== 1
    ) {
      return
    }
    const operation = createSplitOperationAtOutputTime(
      state.previewElements,
      elementId,
      playhead,
      createRequestId().toLowerCase(),
    )
    if (operation !== null) {
      dispatch({ type: 'operationsQueued', operations: [operation] })
    }
  }

  const trimAtPlayhead = (edge: TrimEdge): void => {
    if (!active || playhead === null) return
    const operations = createTrimOperations(
      state.previewElements,
      playhead,
      edge,
      () => createRequestId().toLowerCase(),
    )
    if (operations !== null) {
      dispatch({ type: 'operationsQueued', operations })
    }
  }

  const fit = (): void => {
    if (!active || viewportWidth <= 0 || !(measureVisibleCanvasWidth(canvasRef.current) > 0)) return
    setPixelsPerSecond(fitTimelineZoom(state.previewElements, viewportWidth))
    setScrollOffset(0)
  }

  const changeZoom = (factor: number, anchor = viewportWidth / 2): void => {
    if (!active || viewportWidth <= 0 || !(measureVisibleCanvasWidth(canvasRef.current) > 0)) return
    const next = zoomTimelineAroundPointer(
      pixelsPerSecond,
      pixelsPerSecond * factor,
      scrollOffset,
      anchor,
      viewportWidth,
      state.previewElements,
    )
    setPixelsPerSecond(next.zoom)
    setScrollOffset(next.scroll)
  }

  const onSubmit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault()
    if (active) load(false)
  }

  const onCanvasPointerDown = (
    event: PointerEvent<HTMLCanvasElement>,
  ): void => {
    if (!active || event.button !== 0 || state.pendingRequest !== null) return
    const point = canvasPoint(event)
    const hit = hitTestTimelineElement(point.x, point.y, layout)
    event.currentTarget.setPointerCapture?.(event.pointerId)
    if (hit === null) {
      dispatch({ type: 'selectionCleared' })
      setInteraction({
        kind: 'pan',
        pointerId: event.pointerId,
        startX: point.x,
        startScroll: scrollOffset,
      })
      return
    }
    dispatch({
      type: 'elementSelected',
      elementId: hit.elementId,
      toggle: event.ctrlKey || event.metaKey,
    })
    setInteraction({
      kind: 'move',
      pointerId: event.pointerId,
      elementId: hit.elementId,
      startX: point.x,
      currentX: point.x,
    })
  }

  const onCanvasPointerMove = (
    event: PointerEvent<HTMLCanvasElement>,
  ): void => {
    if (!active || interaction === null || interaction.pointerId !== event.pointerId) {
      return
    }
    const point = canvasPoint(event)
    if (interaction.kind === 'pan') {
      setScrollOffset(
        clampTimelineScroll(
          interaction.startScroll - (point.x - interaction.startX),
          totalTimelineWidth(state.previewElements, pixelsPerSecond),
          viewportWidth,
        ),
      )
    } else {
      setInteraction({ ...interaction, currentX: point.x })
    }
  }

  const finishPointerInteraction = (
    event: PointerEvent<HTMLCanvasElement>,
  ): void => {
    if (!active || interaction === null || interaction.pointerId !== event.pointerId) {
      return
    }
    event.currentTarget.releasePointerCapture?.(event.pointerId)
    if (interaction.kind === 'move') {
      const finalPoint = canvasPoint(event)
      const finalX = finalPoint.x
      const releasedInsideCanvas =
        finalX >= 0 &&
        finalX < viewportWidth &&
        finalPoint.y >= 0 &&
        finalPoint.y < TIMELINE_CANVAS_HEIGHT
      const sourceIndex = state.previewElements.findIndex(
        (element) => element.id === interaction.elementId,
      )
      const targetIndex = computeMoveTargetIndex(
        state.previewElements,
        interaction.elementId,
        finalX,
        viewportWidth,
        pixelsPerSecond,
        scrollOffset,
      )
      if (
        sourceIndex >= 0 &&
        releasedInsideCanvas &&
        finalX !== interaction.startX &&
        targetIndex !== null &&
        targetIndex !== sourceIndex
      ) {
        dispatch({
          type: 'operationsQueued',
          operations: [
            {
              kind: 'move',
              elementId: interaction.elementId,
              targetIndex,
            },
          ],
        })
      }
    }
    setInteraction(null)
  }

  const onCanvasWheel = (event: WheelEvent<HTMLCanvasElement>): void => {
    if (!active) return
    event.preventDefault()
    const point = canvasPoint(event)
    if (event.ctrlKey) {
      if (event.deltaY === 0) return
      changeZoom(event.deltaY < 0 ? 1.2 : 1 / 1.2, point.x)
      return
    }
    const delta = event.deltaX !== 0 ? event.deltaX : event.deltaY
    setScrollOffset(
      clampTimelineScroll(
        scrollOffset + delta,
        totalTimelineWidth(state.previewElements, pixelsPerSecond),
        viewportWidth,
      ),
    )
  }

  const onCanvasKeyDown = (event: KeyboardEvent<HTMLCanvasElement>): void => {
    if (!active) return
    const modifier = event.ctrlKey || event.metaKey
    if (modifier && event.key.toLowerCase() === 'z') {
      event.preventDefault()
      dispatch({ type: event.shiftKey ? 'redo' : 'undo' })
      return
    }
    if (modifier && event.key.toLowerCase() === 'y') {
      event.preventDefault()
      dispatch({ type: 'redo' })
      return
    }
    switch (event.key) {
      case 'Delete':
      case 'Backspace':
        event.preventDefault()
        queueSelected('delete')
        break
      case '+':
      case '=':
        event.preventDefault()
        changeZoom(1.2)
        break
      case '-':
        event.preventDefault()
        changeZoom(1 / 1.2)
        break
      case 'z':
      case 'Z':
        event.preventDefault()
        fit()
        break
      case 's':
      case 'S':
        event.preventDefault()
        splitAtPlayhead()
        break
    }
  }

  const busy = state.pendingRequest !== null
  const selectedCount = state.selectedElementIds.length
  const pendingOperations = selectPendingTimelineOperations(state)
  const hasPending = pendingOperations.length > 0
  const canSplit = playhead !== null && selectedCount === 1 && !busy

  useEffect(() => {
    onDirtyChangeRef.current?.(hasPending)
  }, [hasPending])

  return (
    <section
      className="timeline-card"
      aria-labelledby="timeline-title"
      hidden={!active}
      inert={!active}
    >
      <div className="timeline-heading">
        <div>
          <p className="eyebrow">Exact composition</p>
          <h2 id="timeline-title">編集タイムライン</h2>
          <p>Canvas は表示専用。編集時刻は exact rational のまま保持します。</p>
        </div>
        <div className="timeline-phase" data-timeline-phase={state.phase} role="status">
          <span>{phaseLabel(state.phase)}</span>
          {state.baseline === null ? null : (
            <small>revision {state.baseline.revision}</small>
          )}
        </div>
      </div>

      {hasContext ? null : (
        <form className="timeline-load-form" onSubmit={onSubmit}>
          <label htmlFor="timeline-project-id">案件 ID</label>
          <div>
            <input
              id="timeline-project-id"
              type="text"
              autoComplete="off"
              spellCheck={false}
              minLength={36}
              maxLength={36}
              pattern="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
              value={state.projectIdInput}
              disabled={busy || hasPending}
              onChange={(event) =>
                dispatch({
                  type: 'projectIdInputChanged',
                  value: event.currentTarget.value,
                })
              }
            />
            <button
              className="primary-button"
              type="submit"
              disabled={
                busy ||
                hasPending ||
                !isCanonicalTimelineId(state.projectIdInput)
              }
            >
              タイムラインを開く
            </button>
          </div>
        </form>
      )}

      {state.issue === null ? null : (
        <div className="timeline-issue" role="alert">
          <p>{issueMessages[state.issue]}</p>
          <div>
            {hasContext && state.baseline === null ? (
              <button type="button" onClick={() => load(false)} disabled={!active || busy}>
                タイムラインの読み込みを再試行
              </button>
            ) : null}
            {state.phase === 'conflict' && state.intentValid ? (
              <button type="button" onClick={() => load(true)} disabled={busy}>
                未送信操作を保持して再読み込み
              </button>
            ) : null}
            {state.pendingGroups.length > 0 ? (
              <button
                className="quiet-button"
                type="button"
                onClick={() => dispatch({ type: 'pendingDiscarded' })}
                disabled={busy}
              >
                未送信操作を破棄
              </button>
            ) : null}
          </div>
        </div>
      )}

      {state.baseline === null ? (
        <div className="timeline-empty">
          <strong>タイムラインは未読み込みです</strong>
          <p>
            {hasContext
              ? '編集する案件を選択すると、権限確認後に working copy を表示します。'
              : '案件 ID を指定すると、権限確認後に working copy を表示します。'}
          </p>
        </div>
      ) : (
        <div className="timeline-workspace">
          <div className="timeline-toolbar" aria-label="タイムライン編集操作">
            <div className="toolbar-group">
              <button type="button" onClick={() => changeZoom(1 / 1.2)}>
                −
              </button>
              <button type="button" onClick={fit}>全体表示</button>
              <button type="button" onClick={() => changeZoom(1.2)}>
                ＋
              </button>
              <output aria-label="タイムライン拡大率">
                {Math.round(pixelsPerSecond)} px/s
              </output>
            </div>
            <div className="toolbar-group">
              <button
                type="button"
                disabled={busy || selectedCount === 0}
                onClick={() => queueSelected('keep')}
              >
                Keep
              </button>
              <button
                type="button"
                disabled={busy || selectedCount === 0}
                onClick={() => queueSelected('delete')}
              >
                Delete
              </button>
              <button type="button" disabled={!canSplit} onClick={splitAtPlayhead}>
                再生位置で分割
              </button>
              <button
                type="button"
                disabled={playhead === null || busy}
                onClick={() => trimAtPlayhead('start')}
              >
                左側をトリム
              </button>
              <button
                type="button"
                disabled={playhead === null || busy}
                onClick={() => trimAtPlayhead('end')}
              >
                右側をトリム
              </button>
            </div>
          </div>

          <div className="timeline-canvas-frame">
            <canvas
              ref={canvasRef}
              className="timeline-canvas"
              tabIndex={active ? 0 : -1}
              aria-label="編集タイムライン。クリックで選択、ドラッグで移動、空白ドラッグまたはホイールでスクロール、Ctrl+ホイールで拡大縮小"
              onPointerDown={onCanvasPointerDown}
              onPointerMove={onCanvasPointerMove}
              onPointerUp={finishPointerInteraction}
              onPointerCancel={() => setInteraction(null)}
              onWheel={onCanvasWheel}
              onKeyDown={onCanvasKeyDown}
            >
              Canvas を表示できない環境では下の要素一覧から選択できます。
            </canvas>
          </div>

          <div className="timeline-readout">
            <span>選択 {selectedCount}</span>
            <span>未送信 {pendingOperations.length}</span>
            <span>
              再生位置 {playhead === null ? '—' : formatExactRationalTime(playhead)}
            </span>
            <span>
              scroll {Math.round(scrollOffset)} px
            </span>
          </div>

          <ol className="timeline-element-list" aria-label="タイムライン要素">
            {state.previewElements.map((element, index) => (
              <li key={element.id}>
                <button
                  type="button"
                  aria-pressed={selectedIds.has(element.id)}
                  onClick={(event) =>
                    dispatch({
                      type: 'elementSelected',
                      elementId: element.id,
                      toggle: event.ctrlKey || event.metaKey,
                    })
                  }
                >
                  <span>{index + 1}</span>
                  <strong>{originLabel(element.origin.kind)}</strong>
                  <code>{element.id.slice(0, 8)}</code>
                  <small>{formatExactRationalTime(element.outputRange.duration)}</small>
                </button>
              </li>
            ))}
          </ol>

          <div className="timeline-history">
            <div>
              <button
                type="button"
                disabled={!canUndoTimeline(state)}
                onClick={() => dispatch({ type: 'undo' })}
              >
                未送信操作を元に戻す
              </button>
              <button
                type="button"
                disabled={!canRedoTimeline(state)}
                onClick={() => dispatch({ type: 'redo' })}
              >
                やり直す
              </button>
              <button
                className="quiet-button"
                type="button"
                disabled={busy || state.pendingGroups.length === 0}
                onClick={() => dispatch({ type: 'pendingDiscarded' })}
              >
                破棄
              </button>
            </div>
            <button
              className="primary-button"
              type="button"
              disabled={
                busy ||
                pendingOperations.length === 0 ||
                !state.intentValid ||
                state.phase === 'conflict'
              }
              onClick={applyPending}
            >
              {state.phase === 'applying'
                ? '適用しています…'
                : `${pendingOperations.length} 操作を適用`}
            </button>
          </div>
        </div>
      )}
    </section>
  )
}

function measureVisibleCanvasWidth(canvas: HTMLCanvasElement | null): number {
  if (canvas === null || canvas.closest('[hidden], [inert]') !== null) return 0
  return Math.round(canvas.parentElement?.getBoundingClientRect().width ?? 0)
}

function canvasPoint(
  event: Pick<PointerEvent<HTMLCanvasElement> | WheelEvent<HTMLCanvasElement>, 'clientX' | 'clientY' | 'currentTarget'>,
): Readonly<{ x: number; y: number }> {
  const bounds = event.currentTarget.getBoundingClientRect()
  const width = bounds.width || event.currentTarget.clientWidth || 1
  const height = bounds.height || event.currentTarget.clientHeight || 1
  return Object.freeze({
    x: ((event.clientX - bounds.left) / width) *
      (Number.parseFloat(event.currentTarget.style.width) || width),
    y: ((event.clientY - bounds.top) / height) *
      (Number.parseFloat(event.currentTarget.style.height) || height),
  })
}

function readPalette(canvas: HTMLCanvasElement): TimelineCanvasPalette {
  const style = getComputedStyle(canvas)
  const color = (name: string, fallback: string): string =>
    style.getPropertyValue(name).trim() || fallback
  return Object.freeze({
    background: color('--cp-surface', '#ffffff'),
    ruler: color('--cp-surface-soft', '#f5f5f5'),
    text: color('--cp-text', '#242424'),
    mutedText: color('--cp-text-muted', '#5c5c5c'),
    border: color('--cp-border-strong', '#919191'),
    sourceFill: color('--timeline-source', '#b8dff2'),
    assetFill: color('--timeline-asset', '#d7c8ee'),
    editorialFill: color('--timeline-editorial', '#f5d6a3'),
    selected: color('--cp-accent', '#b11f4b'),
    playhead: color('--timeline-playhead', '#e5484d'),
    dropTarget: color('--cp-link', '#0078d4'),
  })
}

function phaseLabel(phase: string): string {
  switch (phase) {
    case 'idle':
      return '未読み込み'
    case 'loading':
      return '読み込み中'
    case 'ready':
      return '保存済み'
    case 'editing':
      return '未送信操作あり'
    case 'applying':
      return '適用中'
    case 'conflict':
      return '競合を確認してください'
    default:
      return '操作を確認してください'
  }
}

function originLabel(kind: 'sourceSpan' | 'asset' | 'editorialRecord'): string {
  switch (kind) {
    case 'sourceSpan':
      return 'Source span'
    case 'asset':
      return 'Asset'
    case 'editorialRecord':
      return 'Editorial'
  }
}

function defaultRequestId(): string {
  return window.crypto.randomUUID().toLowerCase()
}