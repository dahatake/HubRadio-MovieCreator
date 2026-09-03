import { useState } from 'react'

import { ComparePanel } from './features/compare'
import { JobProgressPanel } from './features/jobs/JobProgressPanel'
import { DeliveryPanel } from './features/delivery'
import { LayoutPanel } from './features/layout'
import {
  PlayerPanel,
  type PlayerExactFrameSelection,
  type PlayerTimelinePosition,
} from './features/player'
import {
  PreviewModeControls,
  usePreviewModes,
} from './features/preview-modes'
import { ReviewPanel } from './features/review'
import { TimelineEditor } from './features/timeline'
import { ThumbnailPanel } from './features/thumbnails'
import { selectSyncStatus, useAppState, type SyncStatus } from './state'
import { ProjectWorkspace } from './features/projects/ProjectWorkspace'
import { TranscriptPanel } from './features/transcript/TranscriptPanel'
import { AnalysisPanel } from './features/analysis/AnalysisPanel'
import { SettingsPanel } from './features/settings'

const statusMessages: Readonly<Record<SyncStatus, string>> = {
  unsaved: 'ホスト接続は未確認です',
  pending: 'ホスト接続を確認しています',
  acknowledged: 'ホスト接続を確認しました',
  error: 'ホスト接続を確認できませんでした',
}

function App() {
  const { state, synchronize } = useAppState()
  const { state: previewModes } = usePreviewModes()
  const syncStatus = selectSyncStatus(state)
  const [playerPosition, setPlayerPosition] =
    useState<PlayerTimelinePosition | null>(null)
  const [playerSelection, setPlayerSelection] =
    useState<PlayerExactFrameSelection | null>(null)

  return (
    <main className="app-shell" aria-labelledby="app-title">
      <a className="skip-link" href="#project-content">
        メインコンテンツへ移動
      </a>
      <header className="app-header">
        <div className="brand-lockup">
          <span className="brand-mark" aria-hidden="true">H</span>
          <div>
            <p className="eyebrow">Hubらじお</p>
            <h1 id="app-title">Movie Creator</h1>
          </div>
        </div>
        <div className="connection-controls">
          <p className="status" role="status" data-sync-status={syncStatus}>
            <span className="status-indicator" aria-hidden="true" />
            {statusMessages[syncStatus]}
          </p>
          <button
            className="quiet-button"
            type="button"
            disabled={syncStatus === 'pending'}
            onClick={synchronize}
          >
            ホスト接続を確認
          </button>
        </div>
      </header>
      <div id="project-content" tabIndex={-1}>
        <ProjectWorkspace />
        <JobProgressPanel />
        <PreviewModeControls selection={playerSelection} />
        <PlayerPanel
          previewModes={previewModes}
          onPositionChange={setPlayerPosition}
          onSelectionChange={setPlayerSelection}
        />
        <ThumbnailPanel
          selection={playerSelection}
          previewReduced={previewModes.thumbnailReduced}
        />
        <AnalysisPanel />
        <TranscriptPanel />
        <TimelineEditor
          initialProjectId={playerPosition?.projectId}
          playerPosition={playerPosition}
        />
        <LayoutPanel previewModes={previewModes} />
        <ComparePanel />
        <ReviewPanel />
        <DeliveryPanel />
        <SettingsPanel />
      </div>
    </main>
  )
}

export default App
