namespace HubRadio.App.Projects;

/// <summary>
/// Fixed, path-free explanations shown to the user when "編集を開始" cannot start the native
/// source-only pipeline: what is wrong, whether the app already tried to repair it, and what to do.
/// </summary>
internal static class SourceOnlyDependencyGuidance
{
    private const string PackageAdvice =
        "配布パッケージ（ポータブル版）を使用するか、開発ビルドの場合は Release 構成でビルドし直してください（docs/dev/troubleshooting.md）。";

    internal static string Describe(SourceOnlyApplicationDependency dependency, bool repairAttempted)
    {
        string cause = dependency switch
        {
            SourceOnlyApplicationDependency.Ffmpeg =>
                "動画処理ツール（Tools\\ffmpeg.exe）が見つからないか、検証に失敗しました。",
            SourceOnlyApplicationDependency.Ffprobe =>
                "動画解析ツール（Tools\\ffprobe.exe）が見つからないか、検証に失敗しました。",
            SourceOnlyApplicationDependency.Worker =>
                "文字起こしワーカー（Worker フォルダー）が見つからないか、検証に失敗しました。",
            SourceOnlyApplicationDependency.Model =>
                "文字起こしモデル（ModelCatalog フォルダー）が見つからないか、検証に失敗しました。",
            SourceOnlyApplicationDependency.Font =>
                "字幕用フォント（Fonts フォルダー）が見つからないか、検証に失敗しました。",
            SourceOnlyApplicationDependency.ApplicationEvidence =>
                "アプリのビルド証跡（Evidence フォルダー）が見つからないか、検証に失敗しました。",
            _ =>
                "編集の準備（初期化または前回の処理の復旧）が完了していません。",
        };
        string remedy = dependency switch
        {
            SourceOnlyApplicationDependency.Initialization =>
                "しばらくしてから「編集を開始」をもう一度押してください。直らない場合はアプリを再起動してください。",
            _ => PackageAdvice + "ファイルを補ったあと、「編集を開始」をもう一度押すと自動で再確認します。",
        };
        string repair = repairAttempted
            ? "アプリは自動修復（再初期化と復旧の再実行）を試みましたが、回復できませんでした。"
            : string.Empty;
        return string.Concat("編集を開始できません。", cause, repair, remedy);
    }
}
