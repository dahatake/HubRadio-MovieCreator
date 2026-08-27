function App() {
  return (
    <main className="app-shell" aria-labelledby="app-title">
      <section className="welcome-card" aria-describedby="app-description">
        <p className="eyebrow">Hubらじお</p>
        <h1 id="app-title">Movie Creator</h1>
        <p className="status" role="status">
          <span className="status-indicator" aria-hidden="true" />
          UI の準備ができました
        </p>
        <p id="app-description" className="description">
          編集機能は今後のステップで追加されます。
        </p>
      </section>
    </main>
  )
}

export default App
