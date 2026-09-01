// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { describe, expect, it } from 'vitest'

import type { BridgeClient } from '../../bridge'
import { AppStateProvider } from '../../state'
import { JobProgressPanel } from './JobProgressPanel'

const jobId = '11112222-3333-4444-8555-666677778888'

describe('JobProgressPanel keyboard flow', () => {
  it('submits a canonical job lookup with typed keyboard input and Enter', async () => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
      .IS_REACT_ACT_ENVIRONMENT = true
    const container = document.createElement('div')
    document.body.append(container)
    const root = createRoot(container)
    const client = new FakeJobClient()

    try {
      act(() => {
        root.render(
          <AppStateProvider
            createClient={() => client as unknown as BridgeClient}
          >
            <JobProgressPanel />
          </AppStateProvider>,
        )
      })
      const user = userEvent.setup({ document })
      const input = requiredElement<HTMLInputElement>(container, '#job-id')
      act(() => input.focus())

      await act(async () => user.keyboard(jobId))
      expect(input.value).toBe(jobId)
      await act(async () => user.keyboard('[Enter]'))

      expect(client.statusRequests).toHaveLength(1)
      expect(client.statusRequests[0]?.jobId).toBe(jobId)
      expect(
        requiredElement<HTMLButtonElement>(
          container,
          '.job-lookup button[type="submit"]',
        ).disabled,
      ).toBe(true)
    } finally {
      act(() => root.unmount())
      container.remove()
      delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean })
        .IS_REACT_ACT_ENVIRONMENT
    }

    expect(client.disposed).toBe(true)
    expect(client.abandoned).toHaveLength(1)
  })
})

class FakeJobClient {
  readonly statusRequests: Array<Readonly<{
    requestId: string
    jobId: string
  }>> = []
  readonly abandoned: string[] = []
  disposed = false

  onPingResponse(): () => void {
    return () => undefined
  }

  onJobResponse(): () => void {
    return () => undefined
  }

  sendJobStatus(requestId: string, requestedJobId: string): void {
    this.statusRequests.push({ requestId, jobId: requestedJobId })
  }

  abandonJobRequest(requestId: string): void {
    this.abandoned.push(requestId)
  }

  dispose(): void {
    this.disposed = true
  }
}

function requiredElement<T extends Element>(
  parent: ParentNode,
  selector: string,
): T {
  const element = parent.querySelector(selector)
  if (element === null) {
    throw new Error(`Expected element matching ${selector}.`)
  }
  return element as T
}
