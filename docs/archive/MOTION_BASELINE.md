# Phase 4.5 モーション基準計測

> アーカイブ: この資料は再設計前後の履歴計測であり、現在の性能合格値を示すものではない。統合後の仕様と履歴値は[モーション仕様書](../MOTION_SPECIFICATION.md)、現行試験は[Phase 5～6 テスト手順書](../PHASE5_6_TEST_PROCEDURE.md)を参照する。

採取日: 2026-07-20  
環境: Windows 11 / WQHD / 125% / x64 Debug  
入力: 既存の `window-diagnostics.log` に記録された直近30回のWindowsキー単体操作

## 再設計前

| 区間 | 最小 | 中央値 | 最大 |
|---|---:|---:|---:|
| Windowsキー解放 → UI Automationによるスタート確認 | 36ms | 52ms | 2518ms |
| スタート確認 → ランチャー表示要求 | 14ms | 21ms | 175ms |

- 通常時はスタート確認から表示要求まで50ms以内だったが、同期音声取得や強制レイアウトと重なる回に100ms超の外れ値があった。
- UI Automationは非待機時のフォーカス変更でもデスクトップツリーを走査し、詳細なルート一覧を同期ファイルログへ大量出力していた。
- 進入・退出は16msの `DispatcherQueueTimer` と毎フレームの `SetWindowPos` で実行していた。
- 退出ログの実測はおおむね166～192msだったが、整数座標のHWND移動でありリフレッシュレートとは同期していなかった。
- 進入完了時刻は旧実装で記録していなかったため、再設計後から `LaunchTiming` と `Motion` ログで比較する。

## 再設計後に記録する値

- `key-to-start-ms`: Windowsキー解放からスタート確認
- `start-to-request-ms`: スタート確認から表示要求
- `request-to-loaded-ms`: 表示要求からXAML SurfaceのLoaded
- `request-to-complete-ms`: 表示要求からComposition進入完了
- `motion-ms`: Composition ScopedBatchの実完了時間

フレームごとのログは記録せず、状態遷移と開始・完了だけを非同期ログライターへ渡す。

## 再設計後の暫定実測

2026-07-20のx64 Debug起動確認中に、スタート連動の進入・退出を1回採取した。

| 区間 | 実測 |
|---|---:|
| Windowsキー解放 → スタート確認 | 62.3ms |
| スタート確認 → 表示要求 | 4.0ms |
| 表示要求 → Composition進入完了 | 264.8ms |
| Composition進入 | 256.5ms |
| Composition退出 | 158.4ms |

- スタート確認から表示要求まで50ms以内という初期目標を、この試行では満たした。
- Compositionの完了通知、`VisibleWithStart`、スタート非表示後の `Exiting`、`Hidden` への遷移をログで確認した。
- これは機能確認用の単一サンプルであり、性能合否は60 / 120 / 144Hz、各DPI、複数モニターで反復測定して確定する。
