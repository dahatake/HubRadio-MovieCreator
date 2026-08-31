import {
  createPingRequest,
  parsePingResponse,
  type BridgePingRequest,
  type BridgePingResponse,
} from './protocol'
import {
  parseProjectBridgeResponse,
  type ProjectBridgeRequest,
  type ProjectBridgeResponse,
} from './projectProtocol'

export * from './projectProtocol'

export interface BridgeMessageEvent {
  readonly data: unknown
}

export interface BridgeTransport {
  postMessage(message: BridgePingRequest | ProjectBridgeRequest): void
  addEventListener(type: 'message', listener: (event: BridgeMessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: BridgeMessageEvent) => void): void
}

export interface BridgeClient {
  sendPing(requestId: string): void
  onPingResponse(listener: (response: BridgePingResponse) => void): () => void
  sendProject(request: ProjectBridgeRequest): void
  onProjectResponse(listener: (response: ProjectBridgeResponse) => void): () => void
  dispose(): void
}

export function createBridgeClient(transport: BridgeTransport): BridgeClient {
  const listeners = new Set<(response: BridgePingResponse) => void>()
  const projectListeners = new Set<(response: ProjectBridgeResponse) => void>()
  let disposed = false

  const receive = (event: BridgeMessageEvent): void => {
    const response = parsePingResponse(event.data)
    if (response !== null) {
      for (const listener of listeners) {
        listener(response)
      }
    }

    const projectResponse = parseProjectBridgeResponse(event.data)
    if (projectResponse !== null) {
      for (const listener of projectListeners) {
        listener(projectResponse)
      }
    }
  }

  transport.addEventListener('message', receive)

  return Object.freeze({
    sendPing(requestId: string): void {
      throwIfDisposed(disposed)
      transport.postMessage(createPingRequest(requestId))
    },
    onPingResponse(listener: (response: BridgePingResponse) => void): () => void {
      throwIfDisposed(disposed)
      listeners.add(listener)
      return (): void => {
        listeners.delete(listener)
      }
    },
    sendProject(request: ProjectBridgeRequest): void {
      throwIfDisposed(disposed)
      transport.postMessage(request)
    },
    onProjectResponse(listener: (response: ProjectBridgeResponse) => void): () => void {
      throwIfDisposed(disposed)
      projectListeners.add(listener)
      return (): void => {
        projectListeners.delete(listener)
      }
    },
    dispose(): void {
      if (disposed) {
        return
      }

      disposed = true
      listeners.clear()
      projectListeners.clear()
      transport.removeEventListener('message', receive)
    },
  })
}

export function createHostBridgeClient(): BridgeClient {
  if (typeof window === 'undefined') {
    throw new Error('The WebView2 bridge is unavailable outside a browser window.')
  }

  const candidate = (window as WebViewWindow).chrome?.webview
  if (!isBridgeTransport(candidate)) {
    throw new Error('The WebView2 bridge transport is unavailable.')
  }

  return createBridgeClient(candidate)
}

interface WebViewWindow extends Window {
  readonly chrome?: {
    readonly webview?: unknown
  }
}

function isBridgeTransport(value: unknown): value is BridgeTransport {
  if (typeof value !== 'object' || value === null) {
    return false
  }

  const candidate = value as Partial<BridgeTransport>
  return (
    typeof candidate.postMessage === 'function' &&
    typeof candidate.addEventListener === 'function' &&
    typeof candidate.removeEventListener === 'function'
  )
}

function throwIfDisposed(disposed: boolean): void {
  if (disposed) {
    throw new Error('The WebView2 bridge client is disposed.')
  }
}