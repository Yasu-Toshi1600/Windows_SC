# 次に行う作業と判断事項

更新日: 2026-08-15
対象ソースバージョン: `0.9.1`

## 1. 文書の位置付け

本書は現在地点、未完了作業、配布判断だけを管理する。仕様や保守手順を重複して持たない。

- 現行設計: [DESIGN.md](DESIGN.md)
- 機能保守: [FEATURE_MAINTENANCE.md](FEATURE_MAINTENANCE.md)
- UI・MVVMの実施順: [UI_MVVM_REFACTORING_PLAN.md](UI_MVVM_REFACTORING_PLAN.md)
- Core自動テスト: [CORE_TESTING.md](CORE_TESTING.md)
- GUI・実機確認: [TEST_CHECKLIST.md](TEST_CHECKLIST.md)
- 配布: [DISTRIBUTION.md](DISTRIBUTION.md)

## 2. 現在地点

| 領域 | 状態 |
|---|---|
| バージョン | アプリ`0.9.1`、MSIXメタデータ`0.9.1.0` |
| 製品構成 | `Windows_SC.Core`とWinUI本体`Windows_SC`の2プロジェクト化済み |
| ビルド | Debug／Release x64、警告0・エラー0 |
| 自動テスト | `net8.0`のCoreテスト70件成功 |
| UI・MVVM Phase 5 | 診断ViewModel／View分離済み。残る設定エディター分割は未完了 |
| UI・MVVM Phase 6 | 実行、音声循環、モニター履歴を分離済み。音量表示とXAML重複整理は未完了 |
| UI・MVVM Phase 7～8 | 依存境界とCore移行のコード完了。GUI／ZIP確認待ち |
| GUI確認 | ShortcutKey、マクロ、アプリ別音量、システムモニター、設定分割後の表示は未確認 |
| 配布確認 | 現在版ZIP、別フォルダー、別PC、公開Releaseは未確認 |
| 公開状態 | 最新公開版は`v0.6.2-beta.1`。`0.9.1`タグ／Releaseは未作成 |

ビルドとCoreテスト成功は、WinUI表示、Windowsキー、スタートボタン、Core Audio、
自動起動、ZIP起動の確認を代替しない。

## 3. 次に行う作業

### 3.1 コード整理

1. UI・MVVM Phase 5を続行し、Button／Cycle／Volume／Monitorエディターと
   音声・アプリ候補ViewModelを、既存Binding名と保存順を維持して分割する。
2. Phase 6を続行し、音量表示状態と標準／コンパクトXAMLの重複を整理する。
3. Phase 9で構成ルート、所有権、不要コード、文書、配布スクリプトを最終整理する。
4. 各単位でCoreテスト、Debug／Release x64、対象GUIを確認する。

スタート検出、Composition、Core Audio COMを同じ変更で大きく作り替えない。

### 3.2 GUI・実機確認

1. 設定画面の表示、選択変更、保存、再起動後復元、失敗項目の再選択を確認する。
2. ShortcutKeyとマクロの設定、方式保存、実行、途中失敗、再押下拒否を確認する。
3. 音声循環の「次: デバイス名」と、2出力でのアプリ別音量30%／70%復元を確認する。
4. 外部ミキサー追従、通常終了時のCOM解放、`worker-timeout`不発生を確認する。
5. システムモニターの標準／コンパクト、非表示時停止、長時間常駐を確認する。
6. Windowsキーとスタートボタンの両経路、仮想デスクトップ、複数モニターを回帰確認する。

### 3.3 配布確認

1. 簡易テストA／Bの対象項目を完了する。
2. `Build-Distribution.ps1`で`Windows_SC-0.9.1-x64.zip`を生成する。
3. 別フォルダーで初回起動、設定保存、終了、自動起動、版表示を確認する。
4. 可能ならVisual Studio／.NET SDKのない別PCで確認する。
5. README、CHANGELOG、版、成果物名、SHA-256を照合する。
6. 結果後に`v0.9.1`タグとGitHub Releaseを作成するか判断する。

## 4. 現在の判断

| ID | 方針 |
|---|---|
| D-01 | 公開配布は通常ZIPを優先する。MSIXは公開対象外とする。 |
| D-02 | 単一EXEは`0x80040111`の起動障害を対象PCで解消・確認するまで再採用しない。 |
| D-03 | `0.9.1`配布判断はGUI、現在版ZIP、別PCの結果後に行う。 |
| D-04 | Windows 11 25H2・ビルド26200以降、x64、下端タスクバーを維持する。 |
| D-05 | 1080p未満とスマートフォン連携パネルの配置は未検証範囲として明示する。 |
| D-06 | 起動処理の追加非同期化と自動更新は初回正式版の必須範囲に含めない。 |

## 5. `1.0.0`へ進む条件

- 利用不能、データ破損、終了不能、意図しない実行につながる既知不具合がない。
- 選択したZIPを手元と別PCで確認し、自動起動を含む主要機能が動作する。
- `1.0.0-rc.1`で簡易テストA／Bと必要な詳細試験を完了する。
- 未検証環境を利用者向け文書へ明記する。
- README、CHANGELOG、版、タグ、配布物、Releaseアセットが一致する。
