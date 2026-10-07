import { useEffect, useState } from 'react'

import type { ProjectBridgeMethod } from '../../bridge'

/** Seconds of waiting after which the notice says the wait is longer than usual. */
export const ACTIVITY_SLOW_AFTER_SECONDS = 30

export interface ProjectActivityDescription {
  readonly title: string
  readonly detail: string
  readonly userAction: string
  readonly next: string | null
}

const activityDescriptions: Readonly<Partial<Record<ProjectBridgeMethod, ProjectActivityDescription>>> = {
  'project.finalizeDraft': {
    title: 'いま行っていること：編集の受付',
    detail: '選んだ元動画の確認、既定設定の固定、Projectの保存を行っています。',
    userAction: 'あなたの操作は不要です。完了するまでこの画面のままお待ちください。処理中は「取消」と「編集を開始」を押せません。',
    next: '完了するとProjectが作成され、「ジョブの状態の確認」画面で処理状況を確認できます。',
  },
  'project.pickSource': {
    title: 'いま行っていること：元動画の選択',
    detail: '元動画を選ぶ画面を開いています。',
    userAction: '別に開いた選択画面で動画を1本選んでください。選ばずに閉じても、Projectは変更されません。',
    next: null,
  },
  'project.saveDraft': {
    title: 'いま行っていること：下書きの保存',
    detail: '入力した内容を下書きとして保存しています。',
    userAction: 'あなたの操作は不要です。完了するまでお待ちください。',
    next: null,
  },
}

export function describeProjectActivity(
  method: ProjectBridgeMethod,
): ProjectActivityDescription | null {
  return activityDescriptions[method] ?? null
}

export function ProjectActivityNotice({
  method,
}: {
  readonly method: ProjectBridgeMethod
}) {
  const description = describeProjectActivity(method)
  const [elapsedSeconds, setElapsedSeconds] = useState(0)

  useEffect(() => {
    const startedAt = Date.now()
    const timer = window.setInterval(() => {
      setElapsedSeconds(Math.floor((Date.now() - startedAt) / 1000))
    }, 1000)
    return () => window.clearInterval(timer)
  }, [])

  if (description === null) {
    return null
  }

  return (
    <section className="operation-activity" aria-labelledby="operation-activity-title" data-activity-method={method}>
      <h3 id="operation-activity-title">{description.title}</h3>
      <p>{description.detail}</p>
      <p className="operation-activity-elapsed">経過時間：{elapsedSeconds} 秒</p>
      <p>{description.userAction}</p>
      {description.next !== null && <p>{description.next}</p>}
      {method === 'project.finalizeDraft' && (
        <p className="operation-activity-note">
          処理の細かい進み具合（％）は取得できないため、表示していません。
        </p>
      )}
      {method === 'project.finalizeDraft' && elapsedSeconds >= ACTIVITY_SLOW_AFTER_SECONDS && (
        <p className="operation-activity-slow" role="status">
          {ACTIVITY_SLOW_AFTER_SECONDS} 秒以上かかっています。処理は続いている可能性があるため、もう少しお待ちください。
          長く変わらない場合は、アプリを再起動して元動画を選び直してください（docs/dev/troubleshooting.md）。
        </p>
      )}
    </section>
  )
}
