# アーカイブ文書

このフォルダーには、設計・検証・修正作業の過去記録を保存する。
現行仕様、次の作業、現在の合格条件を判断する資料としては使用しない。

現行資料:

- [Windows_SC 設計書](../DESIGN.md)
- [機能保守ガイド](../FEATURE_MAINTENANCE.md)
- [UI・MVVMリファクタリング計画](../UI_MVVM_REFACTORING_PLAN.md)
- [次に行う作業と判断が必要な内容](../NEXT_STEPS_AND_DECISIONS.md)
- [簡易テストチェックリスト](../TEST_CHECKLIST.md)

## 収録資料

2026-11-06に現行仕様と履歴を整理した。以下は移動・退避時点の記録であり、現在の合否や実装予定の根拠にしない。
音声クラッシュとUI・MVVMの未完了項目は現行docsへ引き継いでいる。

| 退避資料 | 内容 |
|---|---|
| [PROJECT_REVIEW_2026-09-05.md](PROJECT_REVIEW_2026-09-05.md) | 旧0.9.3に対するレビュー・対応経緯 |
| [SHUTDOWN_INVESTIGATION_2026-09-06.md](SHUTDOWN_INVESTIGATION_2026-09-06.md) | 終了処理の旧調査・版ごとの結果 |
| [CODE_SIMPLIFICATION_REVIEW_2026-11-06.md](CODE_SIMPLIFICATION_REVIEW_2026-11-06.md) | 不要処理・テスト整理の調査と実施記録 |
| [STATUS_HISTORY_2026-11-06.md](STATUS_HISTORY_2026-11-06.md) | 旧版のビルド・テスト件数、ZIPハッシュ、main統合・版更新の記録 |
| [UI_MVVM_PLAN_PROGRESS_2026-11-06.md](UI_MVVM_PLAN_PROGRESS_2026-11-06.md) | UI・MVVM計画の過去Phaseと完了記録。残計画は現行docsで継続 |
| [AUDIO_DEVICE_INVESTIGATION_2026-09-08.md](AUDIO_DEVICE_INVESTIGATION_2026-09-08.md) | 音声削除クラッシュの当時のログ・ダンプ解析・試験結果 |
| [DESIGN_BEFORE_DOCUMENTATION_CLEANUP_2026-11-06.md](DESIGN_BEFORE_DOCUMENTATION_CLEANUP_2026-11-06.md) | 重複・旧進捗を含む整理前の設計書 |
| [START_MENU_MAINTENANCE_HISTORY_2026-11-06.md](START_MENU_MAINTENANCE_HISTORY_2026-11-06.md) | スタート保守の旧回帰事例・実装記録 |
| [MOTION_SPECIFICATION_HISTORY_2026-11-06.md](MOTION_SPECIFICATION_HISTORY_2026-11-06.md) | 過去Debug版のモーション計測例 |

| 文書 | 内容 |
|---|---|
| `INITIAL_DESIGN.md` | 初期要件、Phase進捗、当時の設計案 |
| `PHASE0_1_STATUS_2026-07-17.md` | Phase 0～1の技術検証記録 |
| `MOTION_BASELINE.md` | モーション再設計前後の計測記録 |
| `MOTION_REDESIGN_PROCEDURE.md` | Phase 4.5の再設計手順と実装履歴 |
| `REMAINING_WORK_AND_DOCUMENTATION_AUDIT_2026-07-23.md` | `0.5.9-dev`までの修正、検証、文書整理履歴 |
| `UI_ADJUSTMENT_V0.6_2026-07-24.md` | `0.6.0-dev`のUI調整に使用した決定・実装・確認記録 |
| `PHONE_PANEL_DETECTION_EXPERIMENT_2026-07-25.md` | スマートフォン連携パネル検出の試行と不採用理由 |
| `REFACTORING_AND_LOG_CLEANUP_PLAN_2026-08-09.md` | 設定保存分離、Core Audio宣言分離、ログ整理の計画と完了記録 |
| `FEATURE_EXPANSION_IMPLEMENTATION_PLAN_2026-08-09.md` | ShortcutKey、マクロ、アプリ別音量、システムモニターの段階的な実装計画とコード実装記録 |
| `APPLICATION_VOLUME_IMPLEMENTATION_CONTEXT_2026-08-09.md` | Phase 3のCore Audio設計検討と実装引継ぎ記録 |
| `ACTION_AND_MACRO_MAINTENANCE_2026-08-09.md` | 機能保守ガイドへ統合する前のアクション／マクロ保守仕様 |
| `APPLICATION_VOLUME_MAINTENANCE_2026-08-09.md` | 機能保守ガイドへ統合する前のアプリ別音量保守仕様 |
| `SYSTEM_MONITOR_WIDGET_MAINTENANCE_2026-08-09.md` | 機能保守ガイドへ統合する前のシステムモニター保守仕様 |

履歴資料の記述が現行資料と食い違う場合は、現行資料を優先する。
