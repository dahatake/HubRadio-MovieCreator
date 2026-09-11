import { useEffect } from 'react'

import { WorkflowWorkspace } from './features/workflow/WorkflowWorkspace'
import { selectSyncStatus, useAppState, type SyncStatus } from './state'

const statusMessages: Readonly<Record<SyncStatus, string>> = {
  unsaved: 'ホスト接続は未確認です',
  pending: 'ホスト接続を確認しています',
  acknowledged: 'ホスト接続を確認しました',
  error: 'ホスト接続を確認できませんでした',
}

function App() {
  const { state, synchronize } = useAppState()
  const syncStatus = selectSyncStatus(state)

  useEffect(() => {
    let mounted = true
    // Child effects run before AppStateProvider installs its bridge listeners.
    queueMicrotask(() => {
      if (mounted) synchronize()
    })
    return () => {
      mounted = false
    }
  }, [synchronize])

  return (
    <WorkflowWorkspace
      connectionStatus={
        <div className="workflow-connection">
          <p className="status" role="status" data-sync-status={syncStatus}>
            <span className="status-indicator" aria-hidden="true" />
            {statusMessages[syncStatus]}
          </p>
          {syncStatus === 'error' && (
            <button className="quiet-button" type="button" onClick={synchronize}>
              接続を再試行
            </button>
          )}
        </div>
      }
    />
  )
}

export default App
