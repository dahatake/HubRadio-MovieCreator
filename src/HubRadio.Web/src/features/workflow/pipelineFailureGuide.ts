import type { PipelinePhase } from './pipelineProtocol'

/** FR-047.3: what went wrong in 自動編集, why, and what the user can do about it. */
export interface PipelineFailureGuide {
  readonly title: string
  readonly cause: string
  readonly remedies: readonly string[]
}

const retry = '「自動編集を再試行」を押してください。'
const contact = '同じエラーが続く場合は、このエラーコードと「実行の参照」の内容を添えて管理者に連絡してください。'
const newProject = '「案件一覧へ戻る」から新しい案件を作成し、その動画を選び直してください。'
const convert = '動画編集ソフトや変換ツールで、映像1本・音声1本、固定フレームレート（例: 30fps）、映像と音声の開始が0秒の MP4（H.264／AAC）に書き出し直してください。'

const sourceTiming: PipelineFailureGuide = {
  title: '元動画の映像と音声の時間情報を扱えません',
  cause: '元動画の映像と音声の時間の並びが、自動編集で扱える形ではありません。例: フレームレートが一定でない（可変フレームレート）、映像と音声の開始が0秒でない、音声が映像より0.25秒を超えて早く終わる、映像または音声が2本以上ある、フレームの間隔が欠けている。',
  remedies: [convert, newProject],
}

const sourceFile: PipelineFailureGuide = {
  title: '元動画のファイルを読み込めません',
  cause: '案件を作成したときの元動画のファイルが、見つからない、移動・名前の変更・上書きされた、または読み取りを許可されていません。',
  remedies: ['元動画のファイルを、案件を作成したときの場所と名前に戻し、内容を変更しないでください。', retry, `ファイルを戻せない場合は、${newProject}`],
}

const exact: Readonly<Record<string, PipelineFailureGuide>> = {
  'source_only.unsupported_source_timing': sourceTiming,
  'source_only.source_frame_rate_unavailable': {
    title: '元動画のフレームレートを確認できません',
    cause: '元動画の映像に、1秒あたりのフレーム数（フレームレート）の情報がありません。',
    remedies: [convert, newProject],
  },
  'source_only.source_geometry_unavailable': {
    title: '元動画の画面の大きさを確認できません',
    cause: '元動画の映像の幅・高さ・画素の縦横比を読み取れませんでした。',
    remedies: [convert, newProject],
  },
  'source_only.audio_stream_absent': {
    title: '元動画に音声がありません',
    cause: '元動画に音声トラックが含まれていないため、文字起こしと字幕を作れません。',
    remedies: ['音声が入っている動画を書き出し直してください。', newProject],
  },
  'source_only.no_speech_detected': {
    title: '元動画から話し声を検出できません',
    cause: '音声はありますが、話し声として認識できる部分が見つかりませんでした（無音、音量が極端に小さい、音楽だけ等）。',
    remedies: ['元動画を再生し、話し声が聞こえることを確認してください。', '話し声が入った動画を選び直してください。'],
  },
  'source_only.no_speech_recognized': {
    title: '話し声を文字にできません',
    cause: '話し声の部分はありましたが、文字起こしの結果が得られませんでした。',
    remedies: ['元動画を再生し、話し声がはっきり聞こえるか（雑音や音楽に埋もれていないか）確認してください。', retry, contact],
  },
  'source_only.decode_failed': {
    title: '元動画を最後まで読み込めません',
    cause: '元動画のファイルが途中で壊れている、または途中までしか保存されていません。',
    remedies: ['元動画を最後まで再生できるか確認してください。', convert, newProject],
  },
  'source_only.unsupported_source_type': {
    title: 'この形式の動画は扱えません',
    cause: '元動画のファイル形式（コンテナ・コーデック）が自動編集の対象外です。',
    remedies: [convert, newProject],
  },
  'source_only.generation_failed': {
    title: '動画の書き出しに失敗しました',
    cause: '編集結果の動画・字幕・サムネイルを書き出す途中で失敗しました。保存先の空き容量の不足や、ほかのアプリによるファイルの使用が考えられます。',
    remedies: ['保存先のドライブに十分な空き容量（元動画の大きさの5倍以上が目安）があるか確認してください。', retry, contact],
  },
  'source_only.state_persistence_failed': {
    title: '処理の状態を保存できません',
    cause: 'アプリの作業フォルダーへ処理の記録を書き込めませんでした。空き容量の不足やアクセス権の問題が考えられます。',
    remedies: ['保存先のドライブの空き容量を確認してください。', 'アプリを再起動してから、もう一度「自動編集を再試行」を押してください。', contact],
  },
  'source_only.execution_failed': {
    title: '自動編集中に予期しないエラーが発生しました',
    cause: '分類できないエラーで自動編集が止まりました。保存済みの元動画と案件は変更されていません。',
    remedies: [retry, 'アプリを再起動してからもう一度試してください。', contact],
  },
  'source_only.app_restarted': {
    title: 'アプリの終了で自動編集が中断しました',
    cause: '自動編集の途中でアプリが終了または再起動されました。',
    remedies: [retry],
  },
  'job.app_restarted': {
    title: 'アプリの終了で自動編集が中断しました',
    cause: '自動編集の途中でアプリが終了または再起動されました。',
    remedies: [retry],
  },
  'job.execution_timed_out': {
    title: '自動編集が制限時間内に終わりませんでした',
    cause: '処理に時間がかかりすぎたため、自動編集を止めました。長い動画や、ほかの重い処理と同時に実行した場合に起こります。',
    remedies: ['ほかのアプリを終了してから、「自動編集を再試行」を押してください。', contact],
  },
  'task.timeout': {
    title: '文字起こしが制限時間内に終わりませんでした',
    cause: '音声の解析・文字起こしに時間がかかりすぎたため、処理を止めました。',
    remedies: ['ほかのアプリを終了してから、「自動編集を再試行」を押してください。', contact],
  },
  'source_only.subtitle_source_mapping_unsupported': {
    title: '字幕を元動画の時間に合わせられません',
    cause: '文字起こしの結果の一部が、元動画の時間位置に対応付けられませんでした。',
    remedies: [retry, '「設定」の「横長の字幕の1行の最大の文字数」「字幕の最大の行数」などの字幕の項目を初期値に戻してから再試行してください。', contact],
  },
}

interface GroupRule { readonly test: (code: string) => boolean, readonly guide: PipelineFailureGuide }

const groups: readonly GroupRule[] = [
  {
    test: code => /^source_only\.(source_(unavailable|changed|empty|invalid)|path_(unavailable|invalid)|locator_[a-z_]+|file_identity_unavailable|project_source_mismatch|non_video_bytes_invalid)$/.test(code),
    guide: sourceFile,
  },
  {
    test: code => /^source_only\.(runtime_[a-z_]+|model_directory_mismatch|process_lease_unavailable)$/.test(code),
    guide: {
      title: 'アプリに同梱の部品を使えません',
      cause: '動画処理ツール（ffmpeg／ffprobe）、文字起こしワーカー、モデル、フォントのいずれかが見つからないか、検証に失敗しました。',
      remedies: ['配布パッケージ（ポータブル版）のフォルダーを丸ごと使っているか確認してください（docs/dev/troubleshooting.md）。', 'アプリを再起動してから「自動編集を再試行」を押してください。', contact],
    },
  },
  {
    test: code => code.startsWith('asr.'),
    guide: {
      title: '文字起こしに失敗しました',
      cause: '音声を文字にする処理（文字起こしワーカー）がエラーで終了しました。',
      remedies: [retry, 'アプリを再起動してからもう一度試してください。', contact],
    },
  },
  {
    test: code => code.startsWith('task.'),
    guide: {
      title: '音声の解析に失敗しました',
      cause: '音声の解析を行う処理（解析ワーカー）が、正しい結果を返さずに終了しました。',
      remedies: [retry, contact],
    },
  },
  {
    test: code => /^source_only\.(required_setting_missing_or_invalid|settings_[a-z_]+)$/.test(code),
    guide: {
      title: '設定の内容を使えません',
      cause: '案件の設定に、足りない項目、または自動編集の開始後に変わった項目があります。',
      remedies: ['「設定」で「この動画」の設定を確認し、「保存」してください。', retry],
    },
  },
  {
    test: code => /^source_only\.(unsupported_loudness_profile|loudness_[a-z_]+|true_peak_[a-z_]+)$/.test(code),
    guide: {
      title: '音量を基準に合わせられません',
      cause: '書き出した動画の音量（ラウドネス・ピーク）が、設定の基準に収まりませんでした。元動画の音量が極端に小さい・大きい、または音割れしている場合に起こります。',
      remedies: ['元動画の音量を確認し、必要なら音量を整えて書き出し直してください。', '「設定」の音の大きさ（LUFS）の目標値を確認してから再試行してください。'],
    },
  },
  {
    test: code => /^source_only\.subtitle[a-z_]*$/.test(code),
    guide: {
      title: '字幕を作成できません',
      cause: '字幕の時間・位置・警告の記録が、規則を満たしませんでした。',
      remedies: [retry, '「設定」の字幕の項目を初期値に戻してから再試行してください。', contact],
    },
  },
  {
    test: code => /^source_only\.[a-z_]*(reconciliation|conflict|changed|mismatch|checkpoint|invalid|missing|incomplete)[a-z_]*$/.test(code),
    guide: {
      title: '処理の記録の整合を確認できません',
      cause: '途中まで保存した処理の記録（解析結果・編集の版・成果物）が、予定と一致しませんでした。別の操作との競合、またはアプリの途中終了が考えられます。',
      remedies: ['ほかの画面で同じ案件を操作していないか確認してください。', retry, contact],
    },
  },
]

const fallback: PipelineFailureGuide = {
  title: '自動編集を完了できませんでした',
  cause: 'このエラーコードの詳しい説明は登録されていません。',
  remedies: [retry, contact],
}

const reselection: PipelineFailureGuide = {
  title: '元動画の選び直しが必要です',
  cause: '元動画のファイルが、案件を作成したときから見つからない、または内容が変わっています。',
  remedies: sourceFile.remedies,
}

const reconciliation: PipelineFailureGuide = {
  title: '保存状態の確認が必要です',
  cause: '処理の途中で保存された記録と、実際のファイルの状態が一致するか、自動では判断できません。誤って成果物を消さないよう、自動の再試行と削除は行いません。',
  remedies: ['作業フォルダーのファイルを手動で変更・削除していないか確認してください。', contact],
}

/** Returns null while nothing has failed (no code and no failure-like phase). */
export function describePipelineFailure(code: string | null, phase: PipelinePhase | null): PipelineFailureGuide | null {
  if (phase === 'Canceled' || phase === 'Completed') return null
  if (phase === 'ReconciliationRequired') return reconciliation
  if (code !== null) {
    const known = exact[code] ?? groups.find(rule => rule.test(code))?.guide
    if (known !== undefined) return known
  }
  if (phase === 'ReselectionRequired') return reselection
  return code === null && phase !== 'Failed' ? null : fallback
}
