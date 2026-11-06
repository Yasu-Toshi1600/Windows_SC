# Windows_SC 設計書

更新日: 2026-11-06。対象: Windows_SC 0.9.10の作業ツリー。
本書は現在のコード構造とデータの流れを示す。実機での確認状況は[現在の状況](NEXT_STEPS_AND_DECISIONS.md)を参照する。

## 文書の役割

| 分野 | 文書 |
|---|---|
| Start検出・入力・Snapshot・復帰 | [スタート保守](START_MENU_INTEGRATION_MAINTENANCE.md) |
| 表示状態・Composition・時間 | [モーション仕様](MOTION_SPECIFICATION.md) |
| 操作・音声・モニター | [機能保守](FEATURE_MAINTENANCE.md) |
| ログ・保存先・プライバシー | [ログ仕様](LOGGING_AND_PRIVACY.md) |
| 自動テスト／GUI確認 | [Coreテスト](CORE_TESTING.md)／[チェックリスト](TEST_CHECKLIST.md)／[詳細手順](PHASE5_6_TEST_PROCEDURE.md) |
| 版・配布 | [バージョン管理](VERSIONING.md)／[配布手順](DISTRIBUTION.md) |
| 未完了の構造整理 | [UI・MVVM計画](UI_MVVM_REFACTORING_PLAN.md) |
| 過去の実装・検証 | [アーカイブ](archive/README.md) |

## 製品と対応範囲

Windows 11のStartと連動する常駐ランチャー。アプリ・ファイル・URL・コマンド・ShortcutKey・マクロ、
音声循環・音量・システムモニターを扱う。表示・設定UIは1ページを対象とする。
Windows 11 25H2（ビルド26200）以降、x64、下端タスクバー向け。
StartupCompatibilityCheckerはOS・アーキテクチャ・タスクバーを検査し、対象外は起動を中止する。
1080p未満は未検証として警告し、確認後に続行する。日本語は強制条件ではない。
複数モニター・DPI・仮想デスクトップは実装対象だが、現行版でのGUI確認は別途必要。

## 構成と所有権

| 領域 | 実装と責務 |
|---|---|
| 構成ルート | Appがサービス・Window・設定を生成し、イベント接続・終了を管理 |
| UI | MainWindowが入力、配置、表示検証、light-dismissを統合。SettingsWindowが編集UI・Picker・Dialogの寿命を管理 |
| VM | MainWindowViewModelが項目を構築。LauncherItemViewModelが操作と表示を担当。SettingsViewModelが編集・保存・再選択を調整 |
| Start | HybridStartMenuMonitor、Win32StartMenuEventMonitor、UiAutomationStartMenuInspector、StartMenuWindowInspector |
| 表示 | LauncherMotionCoordinator、CompositionLauncherMotionService、LauncherPlacementService |
| 操作 | ActionExecutionService、ShortcutKeyExecutionService、MacroExecutionService |
| 音声 | WindowsAudioOutputService、WindowsApplicationVolumeService、ApplicationVolumeStateStore |
| モニター | WindowsSystemMetricsServiceが採取、SystemMetricsStateがキャッシュ・履歴、VisibleUiUpdateCoordinatorがUI投入を管理 |
| 設定 | JsonSettingsRepository、LauncherSettingsMigrator、LauncherSettingsValidator、SettingsPersistenceCoordinator |
| OS・診断 | GlobalInputService、WindowInteropService、RegistryStartupService、WindowsSystemTrayService、SingleInstanceService、DiagnosticLogger、ProcessMemoryDiagnostics |

製品はWindows_SC（WinUI・Windows固有実装）とWindows_SC.Core（net8.0）の2プロジェクト。
依存はWindows_SCからCoreへの一方向。Windows_SC.TestsはCore成果物を参照する。
scripts/UiLifecycleChecksは例外的に製品ソースをリンクして外部依存を代替する分離検証であり、GUI試験ではない。

## 起動・表示・終了

1. 保存場所・ログ・単一起動制御を初期化し、対応環境と設定を確認する。
2. サービス・Windowを生成し、通常は非表示でトレイへ常駐する。
3. Windowsキー単体／Startボタン／Ctrl+Alt+Space／トレイから表示する。
4. Start連動は非アクティブ表示。手動表示は独立して動作する。
5. 通常のランチャー退出は非表示常駐へ戻る。メインWindowへのWM_CLOSEはアプリ終了要求として扱う。

既定のStart監視はWin32。イベント通知＋125ms確認、Windowsキー後は50ms・最大1500ms確認。
UIA設定は明示保存して再起動後だけ反映する。UIAオンでは登録・スキャンを別々のMTAで実行する。
スリープ復帰時は旧ワーカーの停止を待って同じモードで再生成し、待機中はWin32で検出する。

終了要求では新しい操作を抑止し、設定Windowを閉じて明示保存キューの完了を待つ。
MainWindowのUI・Composition・監視資源を接続中に解放してからWindowを閉じる。
Appは診断、VM、トレイ、アプリ音量、音声出力、音量状態、システム計測、マクロ、単一起動制御等を解放し、
最後にロガーを停止してExitする。解放ログはプロセス消滅の証拠ではない。
ネイティブ処理中のハンドルを並行破棄せず、ワーカー完了時まで保留する場合がある。

配置は検出したStartの右側、手動表示はカーソルのモニター中央。
高さの基準は600 physical pixel。幅・列数・DPI・スマートフォン用予約幅はLauncherPlacementServiceが決める。
外側背景は透明で標準フレームを除去。見た目・反転・複数モニターはGUIチェックで確認する。

## 入力と設定の境界

設定はスキーマ2のJSON。読込時は旧スキーマ移行→デシリアライズ→モデル検証。
不正設定はBackupへ退避して既定値へ復旧を試み、将来スキーマは上書きせず警告して終了する。
既定項目はメモ帳、電卓、エクスプローラー、Windows設定。

保存の流れは次のとおり。

1. 編集VMをSettingsSaveValidatorで確認し、失敗した項目・ステップを再選択する。
2. 保存用モデルを構築し、アプリ共通のSettingsPersistenceCoordinatorへ投入する。
3. JsonSettingsRepository.SaveAsync入口でモデル全体を検証し、一時ファイルから設定を置換する。
4. 自動起動を反映する。失敗時はレジストリ・JSONの復元を試み、実際の状態を通知する。
5. 成功したモデルをランチャーへ適用し、保存対象リビジョンだけを保存済みにする。

一般設定保存は詳細ログ期限・常時有効フラグを保持する。診断設定の専用適用も同じ保存キューを使う。
UIA設定の保存は実行中モードを切り替えず、次回起動へ反映する。
現在のDTOは可変で、操作種別に応じたnullableプロパティと旧音声設定の互換変換を持つ。
「検証済みの型が全内部経路で保証される」構造にはまだなっていない。

## データと外部処理

設定・実行位置・音量状態は %LOCALAPPDATA%\Windows_SC\Settings、ログは同じルートのLogsへ保存する。
settings.json、command-cycle-state.json、application-volume-state.jsonは別のデータ。
後ろの2つの自動保存は、編集途中の一般設定を確定しない。旧保存場所はApplicationDataPathsが移行する。
レジストリ自動起動はHKCUのRunへ現在のEXEを登録し、直後に読み戻す。

Application／File／UrlはCommandと別の操作種別。Shell／cmdの起動要求を行い、子プロセスの終了は待たない。
ShortcutKeyはランチャー退出・フォーカス復帰後に送信時点の前面へSendInputする。UIAは使用しない。
音声循環は登録順の2台以上をIDで管理し、表示は次の切替先。表示名で同定しない。
システムモニターは選択項目を毎秒採取し、非表示中も最大60点の履歴を保持する。UIだけを非表示中に停止する。
通常診断は1分間隔等で採取し、GPUはモニターで選択されている場合だけ計測する。

## 配布と未確認事項

通常の自己完結型x64 ZIPが検証済みの配布形式。MSIXは公開対象外。
単一EXEは過去のWinUI起動障害が解消・実機確認されるまで生成しない。
版番号・ビルド成功はGUI・ZIP・別PC・公開済みReleaseの確認とは別。
未解決不具合と優先順位は[現在の状況](NEXT_STEPS_AND_DECISIONS.md)で管理し、本書へ旧版の試験結果を混在させない。
