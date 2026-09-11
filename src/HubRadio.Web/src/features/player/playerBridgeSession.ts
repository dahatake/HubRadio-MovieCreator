export const PLAYER_SEEK_TIMEOUT_MS = 30_000

/** Local request ownership only; abandoning a seek never cancels native work. */
export interface PlayerBridgeSession {
  trackSeek(requestId: string): boolean
  releaseSeek(requestId: string): void
  abandonSeek(requestId: string): void
  dispose(): void
}

export function createPlayerBridgeSession(
  client: Readonly<{ abandonPlayerRequest?: (requestId: string) => void }>,
  timedOut: (requestId: string) => void,
): PlayerBridgeSession {
  let pending: Readonly<{ requestId: string; timer: ReturnType<typeof setTimeout> }> | null = null
  let disposed = false
  function releaseSeek(requestId: string): void {
    if (pending?.requestId !== requestId) return
    clearTimeout(pending.timer)
    pending = null
  }
  function abandonSeek(requestId: string): void {
    if (pending?.requestId !== requestId) return
    releaseSeek(requestId)
    try { client.abandonPlayerRequest?.(requestId) } catch { /* Local release must still finish. */ }
  }
  return {
    trackSeek(requestId) {
      if (disposed || pending !== null) return false
      const timer = setTimeout(() => {
        if (disposed || pending?.requestId !== requestId) return
        abandonSeek(requestId)
        timedOut(requestId)
      }, PLAYER_SEEK_TIMEOUT_MS)
      pending = { requestId, timer }
      return true
    },
    releaseSeek,
    abandonSeek,
    dispose() {
      if (disposed) return
      disposed = true
      if (pending !== null) abandonSeek(pending.requestId)
    },
  }
}