import { useRef, useState } from 'react'
import { AppIcon, type AppIconName } from '../../components/AppIcons'
import {
  getWorkflowTabFocusStep,
  workflowStepLabels,
  workflowSteps,
  type WorkflowStep,
} from './workflowNavigation'
import './workflow.css'

const stepIcons: Readonly<Record<WorkflowStep, AppIconName>> = {
  source: 'video',
  processing: 'processing',
  results: 'play',
  delivery: 'save',
}

export interface WorkflowTabsProps {
  readonly selectedStep: WorkflowStep
  readonly onSelect: (step: WorkflowStep) => void
  /** Display-only progress supplied by the caller; never selects or gates a tab. */
  readonly processingStep?: WorkflowStep | null
  /** FR-061.1: the tabs to show, in order; defaults to all four project tabs. */
  readonly steps?: readonly WorkflowStep[]
  /** FR-061.1: per-step label overrides (the project list names its source tab 「Project一覧」). */
  readonly labels?: Partial<Readonly<Record<WorkflowStep, string>>>
}

/**
 * Manual-activation tabs. The caller owns workflow-panel-{step} tabpanels,
 * their aria-labelledby="workflow-tab-{step}" and their hidden state.
 */
export function WorkflowTabs({ selectedStep, onSelect, processingStep, steps = workflowSteps, labels }: WorkflowTabsProps) {
  const tabs = useRef<Partial<Record<WorkflowStep, HTMLButtonElement | null>>>({})
  // Outside the tablist, use the controlled selection as the next entry stop.
  const [focusedStep, setFocusedStep] = useState<WorkflowStep | null>(null)

  return (
    <div
      className="workflow-tabs"
      role="tablist"
      aria-label="動画の作業"
      aria-orientation="horizontal"
      onBlur={event => {
        if (!event.currentTarget.contains(event.relatedTarget)) setFocusedStep(null)
      }}
    >
      {steps.map(step => (
        <button
          key={step}
          ref={element => { tabs.current[step] = element }}
          type="button"
          className="workflow-tab"
          role="tab"
          id={`workflow-tab-${step}`}
          aria-controls={`workflow-panel-${step}`}
          aria-selected={selectedStep === step}
          tabIndex={(focusedStep !== null && steps.includes(focusedStep) ? focusedStep : selectedStep) === step ? 0 : -1}
          onFocus={() => setFocusedStep(step)}
          onClick={() => onSelect(step)}
          onKeyDown={event => {
            const nextStep = getWorkflowTabFocusStep(step, event.key, steps)
            if (nextStep === null) return
            event.preventDefault()
            tabs.current[nextStep]?.focus()
            // Native button clicks handle Enter/Space, without double activation.
          }}
        >
          <AppIcon name={stepIcons[step]} />
          <span className="workflow-tab-label">{labels?.[step] ?? workflowStepLabels[step]}</span>
          {processingStep === step && <span className="workflow-tab-status">処理中</span>}
        </button>
      ))}
    </div>
  )
}