export const BRIDGE_PROTOCOL_VERSION = 1 as const
export const BRIDGE_PING_METHOD = 'bridge.ping' as const

const requestIdPattern =
  /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/

export interface BridgePingRequest {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof BRIDGE_PING_METHOD
  readonly params: Readonly<Record<string, never>>
}

export interface BridgePingResponse {
  readonly version: typeof BRIDGE_PROTOCOL_VERSION
  readonly requestId: string
  readonly method: typeof BRIDGE_PING_METHOD
  readonly result: Readonly<{
    protocolVersion: typeof BRIDGE_PROTOCOL_VERSION
  }>
}

export function createPingRequest(requestId: string): BridgePingRequest {
  if (!requestIdPattern.test(requestId)) {
    throw new Error('A bridge request ID must be a non-empty lowercase UUID.')
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId,
    method: BRIDGE_PING_METHOD,
    params: Object.freeze({}),
  })
}

export function parsePingResponse(value: unknown): BridgePingResponse | null {
  if (!isRecordWithExactKeys(value, ['version', 'requestId', 'method', 'result'])) {
    return null
  }

  const result = value.result
  if (
    value.version !== BRIDGE_PROTOCOL_VERSION ||
    typeof value.requestId !== 'string' ||
    !requestIdPattern.test(value.requestId) ||
    value.method !== BRIDGE_PING_METHOD ||
    !isRecordWithExactKeys(result, ['protocolVersion']) ||
    result.protocolVersion !== BRIDGE_PROTOCOL_VERSION
  ) {
    return null
  }

  return Object.freeze({
    version: BRIDGE_PROTOCOL_VERSION,
    requestId: value.requestId,
    method: BRIDGE_PING_METHOD,
    result: Object.freeze({ protocolVersion: BRIDGE_PROTOCOL_VERSION }),
  })
}

function isRecordWithExactKeys(
  value: unknown,
  expectedKeys: readonly string[],
): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false
  }

  const keys = Object.keys(value)
  return (
    keys.length === expectedKeys.length &&
    expectedKeys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
  )
}