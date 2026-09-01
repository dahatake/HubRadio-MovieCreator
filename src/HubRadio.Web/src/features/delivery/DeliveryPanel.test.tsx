import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'

import {
  parseDeliveryReadResponse,
  type DeliveryReadStatus,
  type DeliverySnapshot,
  type DeliveryTrace,
} from './deliveryProtocol'
import {
  DeliveryPanelView,
  getDeliveryEvidenceSectionAfterKey,
} from './DeliveryPanel'
import type { DeliveryStateContextValue } from './DeliveryStateProvider'
import {
  initialDeliveryState,
  type DeliveryUiState,
} from './deliveryState'
import {
  createDeliveryResponseValue,
  deliveryApprovalEvidenceDigest,
  deliveryApprovalId,
  deliveryAssetId,
  deliveryEvidenceDigest,
  deliveryFfmpegDigest,
  deliveryProfileDigest,
  deliveryRequestId,
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

function renderContext(state: DeliveryUiState): string {
  return renderToStaticMarkup(
    <DeliveryPanelView {...contextFor(state)} />,
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
