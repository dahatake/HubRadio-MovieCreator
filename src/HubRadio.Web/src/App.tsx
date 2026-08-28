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

  return (
    <main className="app-shell" aria-labelledby="app-title">
      <section className="welcome-card" aria-describedby="app-description">
        <p className="eyebrow">Hubらじお</p>
        <h1 id="app-title">Movie Creator</h1>
        <p className="status" role="status" data-sync-status={syncStatus}>
          <span className="status-indicator" aria-hidden="true" />
          {statusMessages[syncStatus]}
        </p>
        <button
          className="bridge-button"
          type="button"
          disabled={syncStatus === 'pending'}
          onClick={synchronize}
        >
          ホスト接続を確認
        </button>
        <p id="app-description" className="description">
          編集機能は今後のステップで追加されます。
        </p>
      </section>
    </main>
  )
}

export default App
