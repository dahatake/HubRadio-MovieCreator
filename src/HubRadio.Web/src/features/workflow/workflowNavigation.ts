import { isCanonicalBridgeId } from '../../bridge/protocol'
import { isCanonicalComparisonDigest } from '../compare/compareProtocol'
import type { DeliveryArtifact, DeliverySnapshot } from '../delivery/deliveryProtocol'
import type { RationalTime } from '../player/playerProtocol'
import type { SettingDefinition, SettingsSnapshot } from '../settings/settingsProtocol'
import {
  compareExactRationalTime,
  exactZero,
  parseExactRationalTime,
} from '../timeline/rationalTime'

export const workflowSteps = Object.freeze([
  'source', 'processing', 'results', 'delivery',
] as const)
export type WorkflowStep = (typeof workflowSteps)[number]
export const workflowStepLabels: Readonly<Record<WorkflowStep, string>> = Object.freeze({
  source: '元動画',
  processing: '自動編集',
  results: '仕上がり',
  delivery: '保存',
})

export const workflowTools = Object.freeze([
  'analysis', 'transcript', 'timeline', 'layout',
  'thumbnails', 'compare', 'review', 'evidence',
] as const)
export type WorkflowTool = (typeof workflowTools)[number]

type WarningBinding = Pick<DeliverySnapshot, 'projectId' | 'versionId' | 'versionDigest'>

/** A local display intent, not a bridge DTO or a copy of the current selection. */
export type WorkflowWarningTarget = WarningBinding & Readonly<{ artifactId?: string }> & (
  | Readonly<{ kind: 'preview'; timeBasis: 'source' | 'output'; time: RationalTime }>
  | Readonly<{ kind: 'settings'; key: SettingDefinition['key'] }>
  | Readonly<{ kind: 'evidence' }>
  | Readonly<{ kind: 'editor'; tool: WorkflowTool }>
)

/** Read from existing providers on each attempt; never retained in navigation state. */
export interface WorkflowWarningContext {
  readonly projectId: string | null
  readonly versionId: string | null
  readonly versionDigest: string | null
  /** Availability of this exact destination, not merely bridge/job readiness. */
  readonly available: boolean
  /** Validated artifacts belonging to the context's project/version. */
  readonly artifacts?: readonly Pick<DeliveryArtifact, 'artifactId'>[]
  readonly settings?: Pick<SettingsSnapshot, 'projectId' | 'definitions'> | null
}

export type WorkflowWarningBlockReason =
  | 'invalidTarget' | 'missingContext' | 'projectMismatch' | 'versionMismatch'
  | 'digestMismatch' | 'artifactUnavailable' | 'settingUnavailable' | 'unavailable'

export const workflowWarningReasonLabels: Readonly<Record<WorkflowWarningBlockReason, string>> = Object.freeze({
  invalidTarget: '移動先の参照が不足しているか、不正です。',
  missingContext: '現在の案件・版を確認できません。',
  projectMismatch: '警告の対象案件が現在の案件と一致しません。',
  versionMismatch: '警告の対象版が現在の版と一致しません。',
  digestMismatch: '警告の対象内容が現在の版と一致しません。',
  artifactUnavailable: '対象の成果物を確認できません。',
  settingUnavailable: 'この案件の対象設定を確認できません。',
  unavailable: '移動先の機能は未接続または利用できません。',
})

export type WorkflowWarningResolution =
  | Readonly<{ status: 'ready'; target: WorkflowWarningTarget }>
  | Readonly<{ status: 'blocked'; reason: WorkflowWarningBlockReason }>

interface WorkflowView {
  readonly step: WorkflowStep
  /** Non-null means settings is open; settings is never a fifth workflow step. */
  readonly returnToStep: WorkflowStep | null
  /** Remembered across tabs; only displayed in results. */
  readonly activeTool: WorkflowTool | null
}

export interface WorkflowNavigationState extends WorkflowView {
  /** One visit, no history stack. The target binding also guards the return selection. */
  readonly warningVisit: Readonly<{
    target: WorkflowWarningTarget
    pending: boolean
    returnTo: WorkflowView & Readonly<{ warningId: string | null }>
  }> | null
}

export type WorkflowNavigationAction =
  | Readonly<{ type: 'selectStep'; step: WorkflowStep }>
  | Readonly<{ type: 'selectTool'; tool: WorkflowTool | null }>
  | Readonly<{ type: 'openSettings' | 'closeSettings' | 'returnFromWarning' }>
  | Readonly<{
      type: 'followWarning'
      target: unknown
      context: WorkflowWarningContext
      /** Opaque ID from the real warning record; do not manufacture a UUID. */
      warningId?: string | null
    }>
  | Readonly<{ type: 'warningDestinationHandled'; target: WorkflowWarningTarget }>
  | Readonly<{ type: 'warningDestinationFailed'; target: WorkflowWarningTarget }>

export const initialWorkflowNavigation: WorkflowNavigationState = Object.freeze({
  step: 'source',
  returnToStep: null,
  activeTool: null,
  warningVisit: null,
})

/** Display changes only: no job commands, selection snapshots, resets, reads or seeks. */
export function workflowNavigationReducer(
  state: WorkflowNavigationState,
  action: WorkflowNavigationAction,
): WorkflowNavigationState {
  switch (action.type) {
    case 'selectStep':
      return isWorkflowStep(action.step)
        ? { ...state, step: action.step, returnToStep: null, warningVisit: null }
        : state
    case 'selectTool':
      return action.tool === null || isWorkflowTool(action.tool)
        ? { ...state, step: 'results', activeTool: action.tool, returnToStep: null, warningVisit: null }
        : state
    case 'openSettings':
      return state.returnToStep === null
        ? { ...state, returnToStep: state.step }
        : state
    case 'closeSettings':
      if (state.returnToStep === null) return state
      return {
        ...state,
        step: state.returnToStep,
        returnToStep: null,
        warningVisit: state.warningVisit?.target.kind === 'settings' ? null : state.warningVisit,
      }
    case 'followWarning': {
      const resolution = resolveWorkflowWarningTarget(action.target, action.context)
      if (resolution.status === 'blocked') return state
      const target = resolution.target
      return {
        step: target.kind === 'settings' ? state.step : 'results',
        returnToStep: target.kind === 'settings' ? state.returnToStep ?? state.step : null,
        activeTool: target.kind === 'editor' ? target.tool
          : target.kind === 'evidence' ? 'evidence'
          : target.kind === 'preview' ? null : state.activeTool,
        warningVisit: {
          target,
          pending: true,
          returnTo: {
            step: state.step,
            returnToStep: state.returnToStep,
            activeTool: state.activeTool,
            warningId: typeof action.warningId === 'string' && action.warningId.length > 0
              ? action.warningId : null,
          },
        },
      }
    }
    case 'warningDestinationHandled':
      // Use the target returned by selectPendingWorkflowWarning, not a stale async result.
      return state.warningVisit?.pending && state.warningVisit.target === action.target
        ? { ...state, warningVisit: { ...state.warningVisit, pending: false } }
        : state
    case 'warningDestinationFailed':
      // A failed/expired destination is not a successful visit or a return association.
      return state.warningVisit?.target === action.target ? { ...state, warningVisit: null } : state
    case 'returnFromWarning':
      return returnFromWarning(state)
    default:
      return state
  }
}

export function selectWorkflowScreen(state: WorkflowNavigationState): WorkflowStep | 'settings' {
  return state.returnToStep === null ? state.step : 'settings'
}

/**
 * Call before offering a warning link. Ready validates a display intent, not a seek
 * or a resolved warning. The consumer must resolve the actual frame/record using
 * existing providers; missing matches must never fall back to the first frame.
 */
export function resolveWorkflowWarningTarget(
  value: unknown,
  context: WorkflowWarningContext,
): WorkflowWarningResolution {
  const target = parseWarningTarget(value)
  if (target === null) return { status: 'blocked', reason: 'invalidTarget' }
  const reason = bindingIssue(target, context)
  if (reason !== null) return { status: 'blocked', reason }
  if (context.available !== true) return { status: 'blocked', reason: 'unavailable' }
  if (target.artifactId !== undefined && !context.artifacts?.some(item => item.artifactId === target.artifactId)) {
    return { status: 'blocked', reason: 'artifactUnavailable' }
  }
  if (target.kind === 'settings' && (
    context.settings?.projectId !== target.projectId ||
    !context.settings.definitions.some(definition => definition.key === target.key)
  )) {
    return { status: 'blocked', reason: 'settingUnavailable' }
  }
  return { status: 'ready', target }
}

/** Recheck live identity after loading, immediately before consumer focus/seek. No frame-0 fallback. */
export function selectPendingWorkflowWarning(
  state: WorkflowNavigationState,
  context: WorkflowWarningContext,
): WorkflowWarningResolution | null {
  const visit = state.warningVisit
  if (!visit?.pending || (selectWorkflowScreen(state) === 'settings') !== (visit.target.kind === 'settings')) {
    return null
  }
  const resolution = resolveWorkflowWarningTarget(visit.target, context)
  return resolution.status === 'ready'
    ? { status: 'ready', target: visit.target }
    : resolution
}

/**
 * Read before return/close. warningIds must belong to the currently bound provider;
 * that provider, not navigation, owns the restored selection.
 */
export function selectReturningWorkflowWarningId(
  state: WorkflowNavigationState,
  context: WorkflowWarningContext,
  warningIds: readonly string[],
): string | null {
  const visit = state.warningVisit
  if (visit === null || bindingIssue(visit.target, context) !== null) return null
  const warningId = visit.returnTo.warningId
  return warningId !== null && warningIds.includes(warningId) ? warningId : null
}

export function isWorkflowStep(value: unknown): value is WorkflowStep {
  return typeof value === 'string' && workflowSteps.includes(value as WorkflowStep)
}

/** Horizontal manual tabs: move focus only; UX-018 owns DOM focus and activation. */
export function getWorkflowTabFocusStep(step: WorkflowStep, key: string): WorkflowStep | null {
  if (!isWorkflowStep(step)) return null
  const index = workflowSteps.indexOf(step)
  switch (key) {
    case 'ArrowLeft': return workflowSteps[(index + workflowSteps.length - 1) % workflowSteps.length]!
    case 'ArrowRight': return workflowSteps[(index + 1) % workflowSteps.length]!
    case 'Home': return workflowSteps[0]
    case 'End': return workflowSteps[workflowSteps.length - 1]!
    default: return null
  }
}

export function isWorkflowTabActivationKey(key: string): boolean {
  return key === 'Enter' || key === ' '
}

function returnFromWarning(state: WorkflowNavigationState): WorkflowNavigationState {
  const origin = state.warningVisit?.returnTo
  return origin === undefined ? state : {
    step: origin.step,
    returnToStep: origin.returnToStep,
    activeTool: origin.activeTool,
    warningVisit: null,
  }
}

function bindingIssue(target: WarningBinding, context: WorkflowWarningContext): WorkflowWarningBlockReason | null {
  if (!isId(context.projectId) || !isId(context.versionId) || !isDigest(context.versionDigest)) return 'missingContext'
  if (target.projectId !== context.projectId) return 'projectMismatch'
  if (target.versionId !== context.versionId) return 'versionMismatch'
  if (target.versionDigest !== context.versionDigest) return 'digestMismatch'
  return null
}

function parseWarningTarget(value: unknown): WorkflowWarningTarget | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
  const candidate = value as Record<string, unknown>
  const kind = candidate.kind
  if (kind !== 'preview' && kind !== 'settings' && kind !== 'evidence' && kind !== 'editor') return null
  const payloadKeys = kind === 'preview' ? ['timeBasis', 'time']
    : kind === 'settings' ? ['key'] : kind === 'editor' ? ['tool'] : []
  const hasArtifact = Object.hasOwn(candidate, 'artifactId')
  const keys = ['kind', 'projectId', 'versionId', 'versionDigest', ...payloadKeys, ...(hasArtifact ? ['artifactId'] : [])]
  if (
    Object.keys(candidate).length !== keys.length || !keys.every(key => Object.hasOwn(candidate, key)) ||
    !isId(candidate.projectId) || !isId(candidate.versionId) || !isDigest(candidate.versionDigest) ||
    (hasArtifact && !isId(candidate.artifactId))
  ) return null
  const binding: WarningBinding & Readonly<{ artifactId?: string }> = {
    projectId: candidate.projectId,
    versionId: candidate.versionId,
    versionDigest: candidate.versionDigest,
    ...(hasArtifact ? { artifactId: candidate.artifactId as string } : {}),
  }
  switch (kind) {
    case 'preview': {
      const time = parseExactRationalTime(candidate.time)
      if (time === null || (candidate.timeBasis !== 'source' && candidate.timeBasis !== 'output')) return null
      if (candidate.timeBasis === 'output' && compareExactRationalTime(time, exactZero) < 0) return null
      return Object.freeze({ ...binding, kind, timeBasis: candidate.timeBasis, time })
    }
    case 'settings':
      return typeof candidate.key === 'string' && candidate.key.length > 0
        ? Object.freeze({ ...binding, kind, key: candidate.key }) : null
    case 'editor':
      return isWorkflowTool(candidate.tool) ? Object.freeze({ ...binding, kind, tool: candidate.tool }) : null
    case 'evidence':
      return Object.freeze({ ...binding, kind })
  }
}

function isWorkflowTool(value: unknown): value is WorkflowTool {
  return typeof value === 'string' && workflowTools.includes(value as WorkflowTool)
}

function isId(value: unknown): value is string {
  return typeof value === 'string' && isCanonicalBridgeId(value)
}

function isDigest(value: unknown): value is string {
  return typeof value === 'string' && value.length === 64 && isCanonicalComparisonDigest(value)
}