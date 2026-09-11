// @vitest-environment jsdom

import userEvent from '@testing-library/user-event'
import { act, StrictMode } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  parseDeliveryReadResponse,
  type DeliveryReadRequest,
  type DeliveryReadResponse,
  type DeliveryReadStatus,
  type DeliverySnapshot,
  type DeliveryTrace,
} from './deliveryProtocol'
import {
  DeliveryPanel,
  DeliveryPanelView,
  getDeliveryEvidenceSectionAfterKey,
  type DeliveryPanelProps,
} from './DeliveryPanel'
import {
  DeliveryStateProvider,
  type DeliveryStateContextValue,
} from './DeliveryStateProvider'
import type { DeliveryBridgeClient } from './deliveryBridgeSession'
import {
  initialDeliveryState,
  type DeliveryUiState,
} from './deliveryState'
import {
  createDeliveryFailureValue,
  createDeliveryResponseValue,
  deliveryApprovalEvidenceDigest,
  deliveryApprovalId,
  deliveryAssetId,
  deliveryEvidenceDigest,
  deliveryFfmpegDigest,
  deliveryProfileDigest,
  deliveryRequestId,
  deliverySecondVersionId,
  deliverySourceDigest,
  deliverySourceSpanId,
  deliverySubtitleDigest,
  deliveryUsedSourceSpansDigest,
  deliveryVersionDigest,
  deliveryVersionId,
  deliveryVideoDigest,
} from './deliveryTestFixture'

const adversarialText =
  '<script>globalThis.compromised=true</script><img src=x onerror=alert(1)>&tail'

describe('DeliveryPanel', () => {
  it('renders complete hashes, review/approval evidence, rights, QC, and exact trace', () => {
    const parsed = availableEvidence()
    const markup = renderEvidence(parsed.snapshot, parsed.trace)

    for (const digest of [
      deliveryVersionDigest,
      deliveryProfileDigest,
      deliveryEvidenceDigest,
      deliveryUsedSourceSpansDigest,
      deliveryApprovalEvidenceDigest,
      deliveryVideoDigest,
      deliverySubtitleDigest,
      deliverySourceDigest,
      deliveryFfmpegDigest,
    ]) {
      expect(markup).toContain(digest)
      expect(markup).not.toContain(`${digest.slice(0, 12)}…`)
    }
    expect(markup).toContain(deliveryVersionId)
    expect(markup).toContain(deliveryApprovalId)
    expect(markup).toContain(deliveryAssetId)
    expect(markup).toContain(deliverySourceSpanId)
    expect(markup).toContain('出力時刻 value')
    expect(markup).toContain('Output time')
    expect(markup).toContain('1/2')
    expect(markup).toContain('Source range')
    expect(markup).toContain('[1/1, 2/1)')
  })

  it('keeps internal-only publication distinct from per-asset rights and offers no file action', () => {
    const parsed = availableEvidence()
    const markup = renderEvidence(parsed.snapshot, parsed.trace)

    expect(markup).toContain('プロジェクト公開範囲: 内部限定')
    expect(markup).toContain('外部公開の許可を示すものではありません。')
    expect(markup).toContain('素材ごとの権利状態とも別の evidence')
    expect(markup).toContain('承認済み')
    expect(markup).not.toMatch(/download|ダウンロード|storageReference|file:\/\//i)
    expect(markup).not.toContain('href=')
  })

  it('states every QC limitation without inventing thresholds or readiness', () => {
    const parsed = availableEvidence()
    const markup = renderEvidence(parsed.snapshot, parsed.trace)

    expect(markup).toContain('機械チェック通過は配信許可ではありません')
    expect(markup).toContain('人による確認が必要です。')
    expect(markup).toContain('承認済み数値閾値は適用されておらず')
    expect(markup).toContain('この QC 単独では配信へ進めません。')
    expect(markup).toContain('尺')
    expect(markup).toContain('音声・映像同期')
    expect(markup).toContain('字幕表示')
    expect(markup).toContain('画面輝度')
    expect(markup).toContain('番組ラウドネス')
    expect(markup).toContain('トゥルーピーク')
    expect(markup).not.toContain('閾値以内')
    expect(markup).not.toContain('配信可能')
  })

  it('renders all closed failures without retaining partial evidence', () => {
    const cases: ReadonlyArray<readonly [
      Exclude<DeliveryReadStatus, 'available'>,
      string,
    ]> = [
      ['forbidden', '閲覧する権限がありません'],
      ['unavailable', 'production data provider は利用できません'],
      ['incomplete', '必要な成果物または承認根拠が不足しています'],
      ['mismatched', '結合が一致しません'],
      ['qualityControlFailed', '品質確認が失敗したため'],
    ]
    for (const [status, message] of cases) {
      const markup = renderContext({
        ...initialDeliveryState,
        versionIdInput: deliveryVersionId,
        status,
        lastCompletedOperation: 'snapshot',
      })
      expect(markup).toContain(message)
      expect(markup).toContain('証跡を利用可能として確認できません')
      expect(markup).not.toContain(deliveryVideoDigest)
    }
  })

  it('renders an explicit half-open trace miss without inventing an origin', () => {
    const parsed = availableEvidence()
    const trace: DeliveryTrace = {
      status: 'notFound',
      outputTime: { value: '10', rate: '1' },
      originKind: null,
      originId: null,
      sourceTime: null,
      sourceSpan: null,
    }
    const markup = renderEvidence(parsed.snapshot, trace)

    expect(markup).toContain('対応する由来はありません')
    expect(markup).toContain('10/1 は timeline の半開区間に含まれません')
    expect(markup).toContain('由来を推測していません')
    expect(markup).not.toContain(deliverySourceSpanId)
  })

  it('escapes hostile native text and exposes no identity, path, or arbitrary URL', () => {
    const parsed = availableEvidence()
    const snapshot: DeliverySnapshot = {
      ...parsed.snapshot,
      rights: [{ ...parsed.snapshot.rights[0]!, allowedUse: adversarialText }],
    }
    const markup = renderEvidence(snapshot, parsed.trace)

    expect(markup).toContain('&lt;script&gt;')
    expect(markup).toContain('&lt;img')
    expect(markup).toContain('&amp;tail')
    expect(markup).not.toContain('<script')
    expect(markup).not.toContain('<img')
    expect(markup).not.toContain('S-1-5-')
    expect(markup).not.toContain('C:\\')
    expect(markup).not.toContain('https://')
  })

  it('provides bounded roving keyboard navigation for every evidence section', () => {
    expect(getDeliveryEvidenceSectionAfterKey(0, 'ArrowRight')).toBe(1)
    expect(getDeliveryEvidenceSectionAfterKey(5, 'ArrowRight')).toBe(0)
    expect(getDeliveryEvidenceSectionAfterKey(0, 'ArrowLeft')).toBe(5)
    expect(getDeliveryEvidenceSectionAfterKey(3, 'ArrowDown')).toBe(4)
    expect(getDeliveryEvidenceSectionAfterKey(3, 'ArrowUp')).toBe(2)
    expect(getDeliveryEvidenceSectionAfterKey(4, 'Home')).toBe(0)
    expect(getDeliveryEvidenceSectionAfterKey(1, 'End')).toBe(5)
    expect(getDeliveryEvidenceSectionAfterKey(0, 'Tab')).toBeNull()
    expect(getDeliveryEvidenceSectionAfterKey(-1, 'ArrowRight')).toBeNull()
    expect(getDeliveryEvidenceSectionAfterKey(6, 'ArrowLeft')).toBeNull()

    const parsed = availableEvidence()
    const markup = renderEvidence(parsed.snapshot, parsed.trace)
    expect(markup).toContain('role="tablist"')
    expect(markup.match(/role="tab"/g)).toHaveLength(6)
    expect(markup.match(/role="tabpanel"/g)).toHaveLength(6)
    expect(markup.match(/tabindex="-1"/g)).toHaveLength(5)
  })

  it('requires a canonical version ID and a reduced trace time', () => {
    const invalidVersion = renderContext({
      ...initialDeliveryState,
      versionIdInput: deliveryRequestId.toUpperCase(),
    })
    expect(invalidVersion).toMatch(
      /<button type="submit" disabled="">証跡を開く<\/button>/,
    )

    const parsed = availableEvidence()
    const invalidTime = renderContext({
      ...initialDeliveryState,
      versionIdInput: deliveryVersionId,
      traceValueInput: '2',
      traceRateInput: '4',
      snapshot: parsed.snapshot,
      status: 'available',
      lastCompletedOperation: 'snapshot',
    })
    expect(invalidTime).toMatch(
      /<button class="primary-button" type="submit" disabled="">出力時刻を照合<\/button>/,
    )
  })
})

describe('DeliveryPanel workflow context', () => {
  let container: HTMLDivElement
  let root: Root
  let client: FakeDeliveryClient
  let createClient: () => DeliveryBridgeClient

  beforeEach(() => {
    ;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
    client = new FakeDeliveryClient()
    createClient = () => client
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
    vi.restoreAllMocks()
    delete (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT
  })

  it.each([
    null,
    '',
    '00000000-0000-0000-0000-000000000000',
    deliveryRequestId.toUpperCase(),
    adversarialText,
  ])('shows no form, fake table, or prior evidence for invalid selection %j', async (versionId) => {
    const parsed = availableEvidence()
    const props = { context: { versionId } }
    const markup = renderContext(evidenceState(parsed.snapshot, parsed.trace), props)
    expect(markup).toContain('保存対象の版が選択されていません。')
    expect(markup).not.toContain('<form')
    expect(markup).not.toContain('<table')
    expect(markup).not.toContain('<details')
    expect(markup).not.toContain('再読み込み')
    expect(markup).not.toContain(deliveryVersionDigest)
    expect(markup).not.toContain('承認済み')
    expect(markup).not.toContain(adversarialText)

    await renderPanel(props)
    expect(client.sent).toHaveLength(0)
  })

  it('keeps undefined context standalone, with manual loading and no auto-read', async () => {
    await renderPanel({ context: undefined })
    expect(requiredElement<HTMLInputElement>('#delivery-version-id').value).toBe('')
    expect(client.sent).toHaveLength(0)
    setControlValue(requiredElement<HTMLInputElement>('#delivery-version-id'), deliveryVersionId)
    expect(client.sent).toHaveLength(0)
    submit(requiredElement<HTMLFormElement>('.delivery-load-form'))
    expect(requiredSent(0).params.versionId).toBe(deliveryVersionId)
  })

  it.each([null, 'unavailable'] as const)(
    'uses the short disconnected status for %s without stale evidence or empty tables',
    (status) => {
      const parsed = availableEvidence()
      const markup = renderContext({
        ...evidenceState(parsed.snapshot, parsed.trace),
        status,
      }, { context: { versionId: deliveryVersionId } })

      expect(markup).toContain('成果物の保存処理は未接続です')
      expect(markup).not.toContain('production data provider')
      expect(markup).not.toContain('<form')
      expect(markup).not.toContain('<table')
      expect(markup).not.toContain('<details')
      expect(markup).not.toContain(deliveryVideoDigest)
      expect(markup).not.toContain('ローカル保存済み')
    },
  )

  it('does not infer saved output from a missing snapshot or an old exported review record', () => {
    const parsed = availableEvidence()
    const snapshot: DeliverySnapshot = {
      ...parsed.snapshot,
      review: {
        ...parsed.snapshot.review,
        records: [{ ...parsed.snapshot.review.records[0]!, state: 'exported' }],
      },
    }
    for (const state of [
      { ...evidenceState(snapshot, null), status: 'unavailable' as const },
      { ...evidenceState(snapshot, null), snapshot: null },
      {
        ...evidenceState(snapshot, null),
        error: { code: 'delivery.operation_failed' as const, message: adversarialText },
      },
    ]) {
      const markup = renderContext(state, { context: { versionId: deliveryVersionId } })
      expect(markup).toContain('成果物の保存処理は未接続です')
      expect(markup).not.toContain('ローカル保存済み')
      expect(markup).not.toContain('エクスポート済み')
      expect(markup).not.toContain('<table')
      expect(markup).not.toContain(adversarialText)
    }
  })

  it('shows the short disconnected status before a missing provider snapshot is loaded', () => {
    const markup = renderContext(initialDeliveryState, { context: { versionId: deliveryVersionId } })
    expect(markup).toContain('成果物の保存処理は未接続です')
    expect(markup).not.toContain('<form')
    expect(markup).not.toContain('<table')
  })

  it.each(['forbidden', 'incomplete', 'mismatched', 'qualityControlFailed'] as const)(
    'hides the retained snapshot and trace on %s',
    (status) => {
      const parsed = availableEvidence()
      const markup = renderContext({
        ...evidenceState(parsed.snapshot, parsed.trace),
        status,
      }, { context: { versionId: deliveryVersionId } })
      expect(markup).not.toContain(deliveryVersionDigest)
      expect(markup).not.toContain(deliverySourceSpanId)
      expect(markup).not.toContain('<table')
      expect(markup).not.toContain('ローカル保存済み')
    },
  )

  it.each(['input', 'snapshot'] as const)(
    'requires both selected/input and snapshot version identity (%s mismatch)',
    (mismatch) => {
      const parsed = availableEvidence()
      const state = evidenceState(parsed.snapshot, parsed.trace)
      const markup = renderContext({
        ...state,
        versionIdInput: mismatch === 'input' ? deliverySecondVersionId : deliveryVersionId,
        snapshot: mismatch === 'snapshot'
          ? { ...parsed.snapshot, versionId: deliverySecondVersionId }
          : parsed.snapshot,
      }, { context: { versionId: deliveryVersionId } })
      expect(markup).not.toContain(deliveryVersionDigest)
      expect(markup).not.toContain(deliverySourceSpanId)
      expect(markup).not.toContain('由来を解決しました')
      expect(markup).not.toContain('<form')
      expect(markup).not.toContain('<table')
    },
  )

  it('summarizes only registered types/counts and keeps every full digest and old record in details', async () => {
    const parsed = availableEvidence()
    const snapshot: DeliverySnapshot = {
      ...parsed.snapshot,
      artifacts: [
        ...parsed.snapshot.artifacts,
        { ...parsed.snapshot.artifacts[0]!, artifactId: deliverySecondVersionId },
      ],
    }
    act(() => root.render(
      <DeliveryPanelView
        {...contextFor(evidenceState(snapshot, parsed.trace))}
        context={{ versionId: deliveryVersionId }}
      />,
    ))

    const details = requiredElement<HTMLDetailsElement>('.delivery-details')
    expect(details.open).toBe(false)
    expect(requiredElement('#delivery-overview-title').textContent).toBe('登録された成果物（3 件）')
    expect(requiredElement('[aria-label="保存済み成果物の種類と件数"]').textContent)
      .toBe('master: 2 件subtitle: 1 件')
    expect(container.textContent).toContain('生成済み（登録成果物）')
    expect(container.textContent).toContain('ローカル保存済み（delivery 登録）')
    expect(container.textContent).toContain('新しい自動評価や人の確認の合格を示しません')
    expect(container.textContent).toContain('V2 の自動昇格の完了とも別')
    expect(container.textContent).not.toContain('PASS')
    expect(container.textContent).not.toContain('利用者確認済み')
    expect(container.querySelector('.delivery-load-form')).toBeNull()

    const overview = container.cloneNode(true) as HTMLElement
    overview.querySelector('.delivery-details')!.remove()
    const digests = new Set(JSON.stringify(parsed).match(/[0-9a-f]{64}/g) ?? [])
    expect(digests.size).toBeGreaterThan(9)
    for (const digest of digests) {
      expect(details.textContent).toContain(digest)
      expect(details.textContent).not.toContain(`${digest.slice(0, 12)}…`)
      expect(overview.textContent).not.toContain(digest)
    }
    expect(details.textContent).toContain(deliveryApprovalId)
    expect(details.textContent).toContain(deliverySourceSpanId)
    expect(overview.textContent).not.toContain(deliveryApprovalId)
    expect(overview.textContent).not.toContain(deliveryVersionId)

    const user = userEvent.setup({ document })
    await act(async () => user.click(requiredElement<HTMLElement>('.delivery-details > summary')))
    expect(details.open).toBe(true)
    for (const kind of ['identity', 'artifacts', 'review', 'rights', 'qualityControl', 'trace']) {
      await act(async () => user.click(requiredElement<HTMLButtonElement>(`#delivery-evidence-tab-${kind}`)))
      expect(requiredElement<HTMLElement>(`#delivery-evidence-panel-${kind}`).hidden).toBe(false)
    }
    expect(container.querySelector('a, img, [download]')).toBeNull()
  })

  it('treats hostile artifact types and detail text as content, never markup or file actions', () => {
    const value = createDeliveryResponseValue()
    value.result.snapshot.artifacts[0]!.artifactType = adversarialText
    value.result.snapshot.rights[0]!.allowedUse = adversarialText
    const response = parseRequired(value)
    if (response.result.status !== 'available') throw new Error('Expected available evidence.')
    const markup = renderContext(
      evidenceState(response.result.snapshot, response.result.trace),
      { context: { versionId: deliveryVersionId } },
    )
    expect(markup).toContain('&lt;script&gt;')
    expect(markup).toContain('&lt;img')
    expect(markup).toContain('&amp;tail')
    expect(markup).not.toMatch(/<script|<img|href=|download=|file:\/\//i)
    expect(markup).not.toContain('C:\\')
    expect(markup).not.toContain('https://')
  })

  it('reads once after provider input synchronization, even with StrictMode and fresh context objects', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(1)
    expect(requiredSent(0).params).toEqual({ versionId: deliveryVersionId, outputTime: null })
    expect(container.querySelector('.delivery-load-form')).toBeNull()
    await completeAvailable(requiredSent(0))
    expect(container.textContent).toContain('ローカル保存済み')

    await renderPanel({ context: { versionId: deliveryVersionId } })
    await renderPanel({ context: { versionId: deliveryVersionId }, active: false })
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(1)

    act(() => {
      requiredElement<HTMLElement>('.delivery-details > summary').click()
      requiredElement<HTMLButtonElement>('#delivery-evidence-tab-trace').click()
    })
    setControlValue(requiredElement<HTMLInputElement>('#delivery-trace-value'), '1')
    setControlValue(requiredElement<HTMLInputElement>('#delivery-trace-rate'), '2')
    expect(client.sent).toHaveLength(1)
    submit(requiredElement<HTMLFormElement>('.delivery-trace-form'))
    expect(requiredSent(1).params).toEqual({
      versionId: deliveryVersionId,
      outputTime: { value: '1', rate: '2' },
    })
    await completeAvailable(requiredSent(1))
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(2)
    expect(container.textContent).toContain('由来を解決しました')
  })

  it('reads an unavailable context only once and does not retry on hide/show', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    await act(async () => client.emit(parseRequired(
      createDeliveryFailureValue('unavailable', requiredSent(0).requestId),
    )))
    await renderPanel({ context: { versionId: deliveryVersionId }, active: false })
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(1)
    expect(requiredElement('.delivery-operation-status').textContent)
      .toBe('成果物の保存処理は未接続です')
    expect(container.querySelector('form, table, details')).toBeNull()
  })

  it.each([
    ['incomplete', '必要な成果物または承認根拠が不足しています。'],
    ['unavailable', '成果物の保存処理は未接続です'],
    ['forbidden', '閲覧する権限がありません。'],
    ['mismatched', '結合が一致しません。'],
    ['qualityControlFailed', '品質確認が失敗したため'],
  ] as const)('offers a same-version manual retry after %s without a snapshot or automatic retries', async (status, message) => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    const reload = requiredElement<HTMLButtonElement>('.delivery-panel > .secondary-button')
    expect(reload.tagName).toBe('BUTTON')
    expect(reload.type).toBe('button')
    expect(reload.textContent).toBe('再読み込み')
    expect(reload.disabled).toBe(true)
    act(() => reload.click())
    expect(client.sent).toHaveLength(1)

    const first = requiredSent(0)
    await act(async () => client.emit(parseRequired(
      createDeliveryFailureValue(status, first.requestId),
    )))
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(requiredElement('.delivery-operation-status').textContent).toContain(message)
    expect(container.querySelector('form, table, details')).toBeNull()
    expect(container.textContent).not.toContain('ローカル保存済み')
    expect(reload.disabled).toBe(false)
    expect(client.sent).toHaveLength(1)

    await renderPanel({ context: { versionId: deliveryVersionId }, active: false })
    expect(requiredElement<HTMLElement>('.delivery-panel').hidden).toBe(true)
    expect(requiredElement('.delivery-panel').hasAttribute('inert')).toBe(true)
    expect(reload.disabled).toBe(true)
    act(() => reload.click())
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(reload.disabled).toBe(false)
    expect(client.sent).toHaveLength(1)

    const user = userEvent.setup({ document })
    await act(async () => user.click(reload))
    expect(client.sent).toHaveLength(2)
    const retry = requiredSent(1)
    expect(retry.requestId).not.toBe(first.requestId)
    expect(retry.params).toEqual({ versionId: deliveryVersionId, outputTime: null })
    expect(reload.disabled).toBe(true)
    act(() => reload.click())
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(2)

    await completeAvailable(retry)
    expect(container.textContent).toContain('ローカル保存済み（delivery 登録）')
    expect(container.textContent).toContain(deliveryVersionDigest)
    expect(requiredElement<HTMLDetailsElement>('.delivery-details').open).toBe(false)
    expect(container.querySelectorAll('.secondary-button')).toHaveLength(1)
    expect(reload.disabled).toBe(false)
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(2)
  })

  it('does not manually load or refresh an old provider input before selection synchronization', () => {
    const parsed = availableEvidence()
    const changeVersionId = vi.fn()
    const loadSnapshot = vi.fn()
    const refreshSnapshot = vi.fn()
    act(() => root.render(
      <DeliveryPanelView
        {...contextFor(evidenceState(parsed.snapshot, parsed.trace))}
        changeVersionId={changeVersionId}
        loadSnapshot={loadSnapshot}
        refreshSnapshot={refreshSnapshot}
        context={{ versionId: deliverySecondVersionId }}
      />,
    ))
    const reload = requiredElement<HTMLButtonElement>('.delivery-panel > .secondary-button')
    expect(changeVersionId).toHaveBeenCalledWith(deliverySecondVersionId)
    expect(reload.disabled).toBe(true)
    act(() => reload.click())
    expect(loadSnapshot).not.toHaveBeenCalled()
    expect(refreshSnapshot).not.toHaveBeenCalled()
    expect(container.querySelector('.delivery-workspace')).toBeNull()
  })

  it('retries the current selection after forbidden and ignores superseded responses', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    const first = requiredSent(0)
    await renderPanel({ context: { versionId: deliverySecondVersionId } })
    const reload = requiredElement<HTMLButtonElement>('.delivery-panel > .secondary-button')
    expect(reload.disabled).toBe(true)
    act(() => reload.click())
    expect(client.sent).toHaveLength(1)

    await completeAvailable(first)
    const second = requiredSent(1)
    expect(second.params.versionId).toBe(deliverySecondVersionId)
    await act(async () => client.emit(parseRequired(
      createDeliveryFailureValue('forbidden', second.requestId),
    )))
    await completeAvailable(first)
    expect(requiredElement('.delivery-operation-status').textContent)
      .toContain('閲覧する権限がありません。')
    expect(container.querySelector('.delivery-workspace')).toBeNull()
    expect(container.textContent).not.toContain(deliveryVersionDigest)
    expect(reload.disabled).toBe(false)
    expect(client.sent).toHaveLength(2)

    const user = userEvent.setup({ document })
    act(() => reload.focus())
    await act(async () => user.keyboard('{Enter}'))
    const retry = requiredSent(2)
    expect(retry.params).toEqual({ versionId: deliverySecondVersionId, outputTime: null })
    await completeAvailable(first)
    await act(async () => client.emit(parseRequired(
      createDeliveryFailureValue('forbidden', second.requestId),
    )))
    await completeAvailable(retry, deliveryVersionId)
    expect(reload.disabled).toBe(true)
    expect(requiredElement('.delivery-operation-status').textContent).toBe('保存証跡を確認しています。')
    expect(container.querySelector('.delivery-workspace')).toBeNull()

    await completeAvailable(retry)
    expect(container.textContent).toContain(deliverySecondVersionId)
    expect(container.textContent).not.toContain(deliveryVersionId)
    expect(container.textContent).toContain('ローカル保存済み（delivery 登録）')
    expect(reload.disabled).toBe(false)
    expect(client.sent).toHaveLength(3)
  })

  it('waits for an old pending request, rejects wrong version/request pairs, and reads the new version once', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    const first = requiredSent(0)
    await renderPanel({ context: { versionId: deliverySecondVersionId } })
    await completeAvailable(first, deliverySecondVersionId)
    expect(client.sent).toHaveLength(1)
    expect(container.querySelector('.delivery-workspace')).toBeNull()

    await completeAvailable(first)
    expect(client.sent).toHaveLength(2)
    const second = requiredSent(1)
    expect(second.params).toEqual({ versionId: deliverySecondVersionId, outputTime: null })
    expect(container.textContent).not.toContain(deliveryVersionDigest)
    expect(container.querySelector('.delivery-workspace')).toBeNull()

    await completeAvailable(first)
    await completeAvailable(second, deliveryVersionId)
    expect(container.querySelector('.delivery-workspace')).toBeNull()
    expect(client.sent).toHaveLength(2)
    await completeAvailable(second)
    expect(container.textContent).toContain(deliverySecondVersionId)
    expect(container.textContent).not.toContain(deliveryVersionId)
    await renderPanel({ context: { versionId: deliverySecondVersionId } })
    expect(client.sent).toHaveLength(2)
  })

  it('does not read an inactive selection or start the next read when a pending response completes hidden', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId }, active: false })
    expect(client.sent).toHaveLength(0)
    await renderPanel({ context: { versionId: deliveryVersionId } })
    const first = requiredSent(0)
    await renderPanel({ context: { versionId: deliverySecondVersionId }, active: false })
    await completeAvailable(first)
    expect(client.sent).toHaveLength(1)
    expect(container.querySelector('.delivery-workspace')).toBeNull()
    await renderPanel({ context: { versionId: deliverySecondVersionId } })
    expect(client.sent).toHaveLength(2)
    expect(requiredSent(1).params.versionId).toBe(deliverySecondVersionId)
  })

  it('cancels a deferred read when hidden before it can be sent', async () => {
    act(() => root.render(panelTree({ context: { versionId: deliveryVersionId } })))
    act(() => root.render(panelTree({ context: { versionId: deliveryVersionId }, active: false })))
    await act(async () => { await Promise.resolve() })
    expect(client.sent).toHaveLength(0)
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(1)
    expect(requiredSent(0).params.versionId).toBe(deliveryVersionId)
  })

  it('reloads a discarded snapshot if a version switch is reversed before the next read is sent', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    await completeAvailable(requiredSent(0))
    act(() => root.render(panelTree({ context: { versionId: deliverySecondVersionId } })))
    act(() => root.render(panelTree({ context: { versionId: deliveryVersionId } })))
    await act(async () => { await Promise.resolve() })
    expect(client.sent.map((request) => request.params.versionId))
      .toEqual([deliveryVersionId, deliveryVersionId])
    await completeAvailable(requiredSent(1))
    expect(container.textContent).toContain(deliveryVersionDigest)
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(client.sent).toHaveLength(2)
  })

  it('hides pending tables, disables controls and focus, preserves state, and never refreshes on return', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    await completeAvailable(requiredSent(0))
    const details = requiredElement<HTMLDetailsElement>('.delivery-details')
    act(() => {
      requiredElement<HTMLElement>('.delivery-details > summary').click()
      requiredElement<HTMLButtonElement>('#delivery-evidence-tab-trace').click()
    })
    const input = requiredElement<HTMLInputElement>('#delivery-trace-value')
    setControlValue(input, '3')
    setControlValue(requiredElement<HTMLInputElement>('#delivery-trace-rate'), '2')
    act(() => requiredElement<HTMLButtonElement>('.delivery-panel > .secondary-button').click())
    expect(client.sent).toHaveLength(2)
    await renderPanel({ context: { versionId: deliveryVersionId }, active: false })

    const panel = requiredElement<HTMLElement>('.delivery-panel')
    expect(panel.hidden).toBe(true)
    expect(panel.hasAttribute('inert')).toBe(true)
    expect(container.querySelectorAll('table')).toHaveLength(2)
    expect(container.querySelector('[tabindex="0"]')).toBeNull()
    for (const control of container.querySelectorAll('button, input, fieldset')) {
      expect(control.matches(':disabled')).toBe(true)
    }
    const traceTab = requiredElement<HTMLButtonElement>('#delivery-evidence-tab-trace')
    act(() => {
      traceTab.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }))
      requiredElement<HTMLElement>('.delivery-details > summary').click()
    })
    submit(requiredElement<HTMLFormElement>('.delivery-trace-form'))
    expect(traceTab.getAttribute('aria-selected')).toBe('true')
    expect(details.open).toBe(true)
    expect(client.sent).toHaveLength(2)

    await completeAvailable(requiredSent(1))
    expect(client.sent).toHaveLength(2)
    expect(requiredElement<HTMLElement>('.delivery-table-scroll').tabIndex).toBe(-1)
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(requiredElement<HTMLDetailsElement>('.delivery-details')).toBe(details)
    expect(input.value).toBe('3')
    expect(requiredElement<HTMLInputElement>('#delivery-trace-rate').value).toBe('2')
    expect(traceTab.getAttribute('aria-selected')).toBe('true')
    expect(client.sent).toHaveLength(2)

    act(() => input.focus())
    const blur = vi.spyOn(input, 'blur')
    await renderPanel({ context: { versionId: deliveryVersionId }, active: false })
    // jsdom does not reliably remove focus after a focused input becomes disabled.
    // Check blur invocation and outside-focus preservation separately from real-browser focus.
    expect(blur).toHaveBeenCalled()
    const outside = document.createElement('button')
    document.body.append(outside)
    try {
      outside.focus()
      await renderPanel({ context: { versionId: deliveryVersionId } })
      expect(document.activeElement).toBe(outside)
      expect(client.sent).toHaveLength(2)
    } finally {
      outside.remove()
    }
  })

  it('hides retained evidence immediately when the context becomes null without clearing provider data', async () => {
    await renderPanel({ context: { versionId: deliveryVersionId } })
    await completeAvailable(requiredSent(0))
    await renderPanel({ context: { versionId: null } })
    expect(container.querySelector('form, table, details')).toBeNull()
    expect(container.textContent).not.toContain(deliveryVersionDigest)
    await renderPanel({ context: { versionId: deliveryVersionId } })
    expect(container.textContent).toContain(deliveryVersionDigest)
    expect(client.sent).toHaveLength(1)
  })

  async function renderPanel(props: DeliveryPanelProps): Promise<void> {
    await act(async () => root.render(panelTree(props)))
  }

  function panelTree(props: DeliveryPanelProps) {
    return (
      <StrictMode>
        <DeliveryStateProvider createClient={createClient}>
          <DeliveryPanel {...props} />
        </DeliveryStateProvider>
      </StrictMode>
    )
  }

  async function completeAvailable(
    request: DeliveryReadRequest,
    versionId = request.params.versionId,
  ): Promise<void> {
    const value = createDeliveryResponseValue(request.requestId, request.params.outputTime !== null)
    value.result.snapshot.versionId = versionId
    if (value.result.trace !== null && request.params.outputTime !== null) {
      value.result.trace.outputTime = { ...request.params.outputTime }
    }
    await act(async () => client.emit(parseRequired(value)))
  }

  function requiredSent(index: number): DeliveryReadRequest {
    const request = client.sent[index]
    if (request === undefined) throw new Error(`Expected delivery request ${index}.`)
    return request
  }

  function requiredElement<T extends Element = HTMLElement>(selector: string): T {
    const element = container.querySelector(selector)
    if (element === null) throw new Error(`Expected element matching ${selector}.`)
    return element as T
  }
})

class FakeDeliveryClient implements DeliveryBridgeClient {
  readonly sent: DeliveryReadRequest[] = []
  private listener: ((response: DeliveryReadResponse) => void) | null = null

  sendDelivery(request: DeliveryReadRequest): void {
    this.sent.push(request)
  }

  abandonDeliveryRequest(): void {}

  onDeliveryResponse(listener: (response: DeliveryReadResponse) => void): () => void {
    this.listener = listener
    return () => { this.listener = null }
  }

  dispose(): void {
    this.listener = null
  }

  emit(response: DeliveryReadResponse): void {
    this.listener?.(response)
  }
}

function parseRequired(value: unknown): DeliveryReadResponse {
  const parsed = parseDeliveryReadResponse(value)
  if (parsed === null) throw new Error('Expected a valid delivery response fixture.')
  return parsed
}

function setControlValue(element: HTMLInputElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set
  if (setter === undefined) throw new Error('Expected the native input value setter.')
  act(() => {
    setter.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

function submit(form: HTMLFormElement): void {
  act(() => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
}

function evidenceState(snapshot: DeliverySnapshot, trace: DeliveryTrace | null): DeliveryUiState {
  return {
    ...initialDeliveryState,
    versionIdInput: snapshot.versionId,
    snapshot,
    trace,
    status: 'available',
    lastCompletedOperation: trace === null ? 'snapshot' : 'trace',
  }
}

function availableEvidence() {
  const parsed = parseDeliveryReadResponse(createDeliveryResponseValue())
  if (parsed === null || parsed.result.status !== 'available') {
    throw new Error('Expected valid available delivery evidence.')
  }
  return parsed.result
}

function renderEvidence(
  snapshot: DeliverySnapshot,
  trace: DeliveryTrace | null,
): string {
  return renderContext({
    ...initialDeliveryState,
    versionIdInput: snapshot.versionId,
    snapshot,
    trace,
    status: 'available',
    lastCompletedOperation: trace === null ? 'snapshot' : 'trace',
  })
}

function renderContext(state: DeliveryUiState, props: DeliveryPanelProps = {}): string {
  return renderToStaticMarkup(
    <DeliveryPanelView {...contextFor(state)} {...props} />,
  )
}

function contextFor(state: DeliveryUiState): DeliveryStateContextValue {
  return {
    state,
    changeVersionId: () => undefined,
    changeTraceValue: () => undefined,
    changeTraceRate: () => undefined,
    loadSnapshot: () => undefined,
    refreshSnapshot: () => undefined,
    lookupTrace: () => undefined,
    clearError: () => undefined,
  }
}
