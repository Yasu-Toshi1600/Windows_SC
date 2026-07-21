# Windows_SC モーション仕様書

更新日: 2026-07-22  
対象: Windows_SC `0.5.2-dev` / Windows App SDK 1.8 / x64

## 1. 目的と文書境界

ランチャーの進入、退出、途中反転、視覚素材、アクセシビリティ、性能計測の現行仕様を定義する。

- スタートメニューの検出条件、監視スレッド、Snapshot、Windowsキーとの同期は[スタート連動機能 保守ガイド](START_MENU_INTEGRATION_MAINTENANCE.md)を正とする。
- アプリ全体の構成は[Windows_SC 設計書](DESIGN.md)を正とする。
- 通常の回帰確認は[簡易テストチェックリスト](TEST_CHECKLIST.md)、網羅的な合否判定は[Phase 5～6 詳細テスト手順書](PHASE5_6_TEST_PROCEDURE.md)を使用する。
- 未完了課題は[現行修正課題・ドキュメント整理表](REMAINING_WORK_AND_DOCUMENTATION_AUDIT.md)で管理する。

本書は旧`MOTION_REDESIGN_PROCEDURE.md`と`MOTION_BASELINE.md`のうち、現在も有効な仕様と計測値を統合したものである。再設計の作業手順と当時の検討過程は`archive`内へ残す。

## 2. 設計原則

1. ランチャーはスタートメニューに付随する一時的な面として動かす。
2. スタート連動表示はスタートと同じ下方向の起点から進入し、別の浮遊ウィンドウに見える横移動は行わない。
3. 手動表示はスタートと同期しないため、短いDirect Entranceを使用する。
4. 退出は進入より短くし、操作完了後に待たされる感覚を抑える。
5. HWNDを毎フレーム移動せず、最終位置に置いたXAML SurfaceのTranslationとOpacityをCompositorで動かす。
6. 退出途中の再表示は完了待ちせず、現在の見た目から反転する。
7. Windowsのアニメーション設定を尊重し、無効時も同じ最終状態と入力規則を保つ。
8. 表示経路で同期I/O、音声デバイス列挙、UI Automation全ツリー走査、強制レイアウト、同期再描画を行わない。

## 3. Visual構造と素材

```text
MainWindow（最終位置・最終サイズの透明な枠なしウィンドウ）
└─ MotionViewport（透明、クリップ領域）
   └─ LauncherSurface（Compositionの移動対象）
      └─ RootBorder（角丸、境界、背景、入力面）
         └─ ランチャー内容
```

- `MainWindow`は最終矩形へ一度だけ配置し、アニメーション中に`SetWindowPos`を反復しない。
- `LauncherSurface`へCompositionプロパティセットの`Translation`と`Opacity`を接続する。
- Windows App SDK 1.8ではSurface単位の`SystemBackdropElement`を利用できないため、`RootBorder`へ`AcrylicInAppFillColorDefaultBrush`を適用する。
- Window全体のDesktop Acrylicは、Surfaceが画面外にある間もウィンドウ矩形全体を塗るため使用しない。
- 透明効果、コントラストテーマ、RDP等でAcrylicを利用できない場合は、テーマが提供する単色フォールバックを受け入れる。
- カード個別の位置アニメーションは行わない。現行ログ上の`card-animation=disabled`を正とする。

Windows App SDK 2.0以降へ更新するときだけ、`SystemBackdropElement`とSurface単位のDesktop Acrylicを再評価する。

## 4. モーション値

| 動作 | Translation開始／終了 | Opacity | 時間 | イージング |
|---|---|---|---:|---|
| スタート連動進入 | Surface全高相当の下位置 → `0` | 原則`1 → 1` | 250ms | `cubic-bezier(0, 0, 0, 1)`相当 |
| 手動進入 | `Y=24` → `0` | `0 → 1` | 167ms | `cubic-bezier(0, 0, 0, 1)`相当 |
| 退出 | `0` → 表示経路に応じた開始位置 | `1 → 0` | 125ms | `cubic-bezier(1, 0, 1, 1)`相当 |
| Opacity変化 | 上記と同時 | 目標値まで | 最大83ms | 方向と同じ |

スタート連動の移動量は`RootBorder.ActualHeight`を使用し、未計測時は配置された物理高さをDPIに応じてeffective pixelへ変換する。手動表示は固定24 effective pixelとする。

実装ではCompositionのCubic Bezierと同等になるよう、途中反転時の現在値計算にも方向別のイージング関数を使用する。時間を変更する場合はコード、本書、性能試験の期待値を同じ変更で更新する。

## 5. 状態機械

| 状態 | 意味 | 主な遷移 |
|---|---|---|
| `Hidden` | HWND非表示 | Windowsキーで待機、スタートクリックまたは手動操作で進入 |
| `AwaitingStartConfirmation` | Windowsキー単体入力後、OSのスタート表示を確認中 | Visible Snapshotで`EnteringWithStart`、期限切れで`Hidden` |
| `EnteringWithStart` | スタート連動進入中 | 完了で`VisibleWithStart`、終了要求で`Exiting` |
| `EnteringManual` | 手動進入中 | 完了で`VisibleInteractive`、終了要求で`Exiting` |
| `VisibleWithStart` | スタートに追従して表示中、非アクティブ可 | ランチャー操作で`VisibleInteractive`、スタート終了で`Exiting` |
| `VisibleInteractive` | ランチャー操作中 | 外側クリック、Escape、成功後に閉じる操作で`Exiting` |
| `Exiting` | 退出中 | 完了で`Hidden`、新しい表示要求で進入へ反転 |

状態は`LauncherMotionCoordinator`だけで遷移させる。`AppWindow`の可視状態や単純なboolを論理状態の代わりにしない。

## 6. 操作別の動作

### Windowsキーで開く

1. Windowsキー単体の解放を受け、`AwaitingStartConfirmation`へ進む。
2. スタートのVisible Snapshotを確認して配置を確定する。
3. ウィンドウをアクティブ化せず表示し、250msの進入を開始する。
4. スタート側の検索・キー入力フォーカスを奪わない。

スタートが開かなかった場合は確認期限後に`Hidden`へ戻し、ランチャーだけを表示しない。

### Windowsキーで閉じる

`EnteringWithStart`または`VisibleWithStart`でWindowsキー単体を受けた場合は、UI AutomationのHidden確認を待たず125msの退出を開始する。

### スタートボタンクリック

Windowsキー待機中でなくても、新しいスタートVisible Snapshotを受けた場合はスタート連動進入を開始する。クリック直後の検出とモニター選択の詳細はスタート保守ガイドに従う。

### 手動ホットキーとトレイ

- `Ctrl+Alt+Space`は表示／退出のトグルである。
- トレイの「ランチャーを表示」は非表示なら表示し、表示済みなら多重表示しない。
- 手動表示はウィンドウをアクティブ化し、167ms、24 effective pixelの進入を使う。
- 退出中の手動表示要求は現在値から手動進入へ反転する。

### ランチャー操作

- ランチャーがフォーカスを得た時点で`VisibleInteractive`へ移り、スタート非表示だけでは閉じない。
- 外側クリック、Escape、設定表示、成功後に閉じるアクションで退出する。
- 実行失敗時はエラー確認のため表示を維持する。

## 7. 途中反転と完了通知

進入・退出を切り替えるときは、経過時間、開始値、終了値、イージングから現在のTranslationとOpacityを計算し、その値から新しいアニメーションを開始する。

- アニメーションごとに世代IDを増やす。
- `CompositionScopedBatch.Completed`はUI Dispatcherへ戻す。
- 完了通知の世代IDが現在と一致しない場合は無視する。
- 退出完了前に新しいWindowsキー、手動表示、トレイ表示を受けた場合は即時反転する。
- スタート非表示を確認して退出した後、新しいVisible Snapshotを受けた場合も連続クリックによる再表示として反転する。
- 退出前から残っていた古いVisible Snapshotだけでは反転しない。

これにより、古い退出完了が再表示後のウィンドウを隠すことと、SnapshotがVisibleのまま通知が止まり次回クリックで起動しなくなることを防ぐ。

## 8. アニメーション無効時

`UISettings.AnimationsEnabled`を起動時に読み、変更イベントにも追従する。

- 無効なら進入はTranslation `0`、Opacity `1`へ即時遷移する。
- 退出中に無効化された場合は即時Hiddenまで完了する。
- 表示中に無効化された場合は即時Visibleの最終値へ合わせる。
- 状態機械、フォーカス、スタート同期、終了理由はアニメーション有効時と変えない。

## 9. スレッドと性能上の制約

- Compositionの進行はCompositorへ任せ、UIスレッドの16msタイマーを使用しない。
- UIスレッドは開始、状態遷移、完了処理だけを行う。
- 完了イベントからUIを操作する前にDispatcherへ戻す。
- ログはバックグラウンドライターへ渡し、フレームごとの値を記録しない。
- 表示直前に音声デバイスを同期列挙しない。キャッシュ値を利用する。
- UI Automationの子孫全走査を表示ホットパスへ戻さない。
- `UpdateLayout`、同期`RedrawWindow`、毎フレームのHWND移動へ依存しない。

## 10. 診断ログ

記録する主な値:

- `key-to-start-ms`: Windowsキー解放からスタート確認
- `start-to-request-ms`: スタート確認から表示要求
- `request-to-complete-ms`: 表示要求から進入完了
- `motion-ms`: Compositionの実完了時間
- `MotionState`: 遷移前、遷移後、理由
- `Motion`: 方向、完了、経過時間、理由

通常の目安:

- `start-to-request-ms`: 50ms以内
- スタート連動進入: 約250ms
- 手動進入: 約167ms
- 退出: 約125ms、継続的に200msを超えない

OS側のスタート表示待ちを含む`key-to-start-ms`は別区間として評価し、ランチャーのComposition時間と混同しない。

## 11. 回帰試験

最低限、次を確認する。

1. Windowsキーによる開閉を30回行い、表示漏れ、二重表示、フォーカス奪取がない。
2. スタートボタンの通常クリックと連続クリックで、退出途中から正しく再表示できる。
3. `Ctrl+Alt+Space`とトレイ表示で、退出途中の反転が現在位置から連続する。
4. 外側クリックとEscapeで一度だけ退出する。
5. 75Hz／180Hzデュアル環境で確認済みの挙動を維持する。
6. 60Hz、120Hz以上、100～200% DPI、異なるDPIの複数モニター、負座標で確認する。
7. Windowsのアニメーションと透明効果を無効にして最終状態が正しい。
8. 途中反転100回で点滅、位置飛び、透明な入力面、操作不能がない。
9. 非表示時に連続スキャンや継続的なCPU使用がない。

普段は簡易テストチェックリストを使い、正式なテストIDと合格条件が必要な場合だけPhase 5～6詳細テスト手順書を使用する。

## 12. 統合した履歴計測

2026-07-20、Windows 11、WQHD、125%、x64 Debugで採取した値。性能合格値ではなく、再設計前後を比較する履歴サンプルである。

### 再設計前

| 区間 | 最小 | 中央値 | 最大 |
|---|---:|---:|---:|
| Windowsキー解放 → UI Automationによるスタート確認 | 36ms | 52ms | 2518ms |
| スタート確認 → ランチャー表示要求 | 14ms | 21ms | 175ms |

旧実装は16msのDispatcherタイマーと毎フレームの`SetWindowPos`を使用し、UI Automation全走査、同期音声取得、強制レイアウト、同期ログが外れ値の原因になっていた。退出は約166～192msだった。

### 再設計直後の単一サンプル

| 区間 | 実測 |
|---|---:|
| Windowsキー解放 → スタート確認 | 62.3ms |
| スタート確認 → 表示要求 | 4.0ms |
| 表示要求 → Composition進入完了 | 264.8ms |
| Composition進入 | 256.5ms |
| Composition退出 | 158.4ms |

このサンプルでは`start-to-request-ms <= 50`を満たし、状態遷移とComposition完了通知が動作した。現在の正式判定には複数回のRelease計測を使用する。

## 13. 既知の制約と将来検討

- スタートとランチャーは別プロセスであり、同じ進行率やComposition Timelineを共有できない。
- Windows更新によってスタートの表示速度、矩形、UI Automation要素が変わる可能性がある。
- 複数`SearchHost`環境におけるクリック直後のモニター確定には残存リスクがある。
- 60／120／144Hz、主要DPI、RDP、途中反転100回の正式な合否記録は未完了である。
- Surface単位のDesktop AcrylicはWindows App SDK 2.0以降への更新時に再検討する。
- カード個別アニメーションは、全体モーションの安定性を崩さないことを確認できるまで追加しない。

## 14. 参考資料

- [Motion in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)
- [Timing and easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing)
- [Directionality and gravity](https://learn.microsoft.com/en-us/windows/apps/design/motion/directionality-and-gravity)
- [Acrylic material](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
- [System backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)
- [XAML and Composition interoperability](https://learn.microsoft.com/en-us/windows/apps/develop/composition/xaml-comp-interop)
- [Time-based animations](https://learn.microsoft.com/en-us/windows/apps/develop/composition/time-animations)
- [CompositionScopedBatch](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionscopedbatch)
