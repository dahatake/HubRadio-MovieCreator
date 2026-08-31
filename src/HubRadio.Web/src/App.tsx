import { JobProgressPanel } from './features/jobs/JobProgressPanel'
import { PlayerPanel } from './features/player'
import { selectSyncStatus, useAppState, type SyncStatus } from './state'
import { ProjectWorkspace } from './features/projects/ProjectWorkspace'

const statusMessages: Readonly<Record<SyncStatus, string>> = {
  unsaved: 'ホスト接続は未確認です',
  pending: 'ホスト接続を確認しています',
  acknowledged: 'ホスト接続を確認しました',
  error: 'ホスト接続を確認できませんでした',
}

function App() {
  const { state, synchronize } = useAppState()
  const syncStatus = selectSyncStatus(state)

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
      <div id="project-content">
        <ProjectWorkspace />
        <JobProgressPanel />
        <PlayerPanel />
      </div>
    </main>
  )
}

export default App
