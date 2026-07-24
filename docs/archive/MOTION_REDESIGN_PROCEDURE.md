# Windows_SC モーション再設計手順書

> アーカイブ: この資料はPhase 4.5の設計・実装履歴である。現在のモーション仕様は[モーション仕様書](../MOTION_SPECIFICATION.md)、スタート検出順序と回帰対策は[スタート連動機能 保守ガイド](../START_MENU_INTEGRATION_MAINTENANCE.md)、現在の作業順は[次に行う作業と判断が必要な内容](../NEXT_STEPS_AND_DECISIONS.md)を参照する。

スタート検出とランチャー状態機械の現行保守仕様は[スタート連動機能 保守ガイド](../START_MENU_INTEGRATION_MAINTENANCE.md)を参照する。

作成日: 2026-07-20  
最終改訂日: 2026-07-21
対象: WinUI 3 / Windows App SDK 1.8 / Windows 11 25H2 x64  
状態: 実装中。基準計測とComposition方式への置換を開始済み。

## 1. 目的

ランチャーの表示・非表示を Microsoft の Windows/Fluent Motion 指針に沿って再設計し、次を実現する。

- スタートメニューと同じタスクバー上端を起点に、ランチャーが画面下から滑らかに現れる。
- スタートメニューとランチャーが別プロセスであっても、方向、タイミング、素材、配置を揃え、一つの操作から現れた一体の UI に見せる。
- UI スレッドの負荷に左右されにくい Composition アニメーションへ移行する。
- スタートメニューからフォーカスを奪わず、ランチャー操作へ移った場合は自然に対話状態へ遷移する。
- 「アニメーション効果」を無効にした利用者、ハイコントラスト、透明効果無効、RDPなどにも安全に対応する。

## 2. Microsoft ガイドラインから採用する原則

Microsoft の Windows Motion は、Connected、Consistent、Responsive、Delightful、Resourceful を原則としている。特に、タスクバーから呼び出されるフライアウトは同じ方向で表示・終了し、同じ入口を持つ UI は共通のタイミング、イージング、方向を使うことが推奨されている。

本アプリでは次のように解釈する。

| 原則 | Windows_SC での適用 |
|---|---|
| Connected | スタート矩形の右側にランチャーを固定し、両者の下端と出現方向を揃える。 |
| Consistent | スタート連動表示は常にタスクバー上端から上方向、終了は下方向とする。 |
| Responsive | Windows キー入力そのものでは表示せず、スタート表示確認後すぐに最初のフレームを提示する。 |
| Delightful | バウンドや過剰なカード演出は避け、短く直接的な一回の面移動にする。 |
| Resourceful | 独自タイマーではなく、WinUI/Windows Composition の標準機構と標準時間を使う。 |

Microsoft の標準時間は 83ms、167ms、250ms が中心で、直接的な進入には 167/250/333ms が示されている。進入の基準イージングは `cubic-bezier(0, 0, 0, 1)`、退出の基準は `cubic-bezier(1, 0, 1, 1)` である。

参考:

- [Motion in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)
- [Timing and easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing)
- [Directionality and gravity](https://learn.microsoft.com/en-us/windows/apps/design/motion/directionality-and-gravity)

## 3. 技術上の境界

Windows_SC からスタートメニューの Visual やアニメーション進行率を取得し、同一の ConnectedAnimation として接続する公開 API は使えない。Windows Composition が直接操作できるのは基本的に自アプリが描画する Visual であり、ConnectedAnimation もアプリ内のビュー間で共有要素を継続して見せる仕組みである。

したがって本設計の「一体化」は、OSのスタートメニューを制御することではなく、次の観測可能な情報を使った知覚上の同期とする。

- スタートメニューの表示開始イベント
- スタートメニューの矩形
- スタートメニューが属するモニターと作業領域
- タスクバー上端に相当する作業領域の下端
- スタートメニューの表示・非表示状態
- ランチャー自身のフォーカス状態

フレーム単位の完全同期は保証せず、「同じ起点、同じ方向、近い開始時刻、Fluent標準の速度感」を保証対象とする。

参考:

- [Composition animations for WinUI](https://learn.microsoft.com/en-us/windows/apps/develop/composition/composition-animation)
- [Visual layer overview](https://learn.microsoft.com/en-us/windows/apps/develop/composition/visual-layer)
- [Connected animation for Windows apps](https://learn.microsoft.com/en-us/windows/apps/develop/motion/connected-animation)

## 4. 現行方式の廃止対象

再実装時には、次をアニメーション経路から外す。

1. `DispatcherQueueTimer` を16ms間隔で動かすフレームループ。
2. 各フレームの `SetWindowPos`。
3. 24 effective pixelだけ上下させる短距離の整数座標補間。
4. 表示直前または表示中の同期音声デバイス列挙。
5. 表示開始時の重複した `Bindings.Update()`、`UpdateLayout()`、同期 `RedrawWindow()`。
6. ランチャー面の移動と同時に走るカード単位の位置アニメーション。
7. アニメーション中の大量なファイルログ出力。

トップレベルウィンドウの位置決定は表示前の一回だけとし、動き自体は Composition Visual に任せる。

## 5. 採用するモーションモデル

### 5.1 空間モデル

画面の物理的な下端ではなく、スタートメニューと同じ「タスクバー上端＝対象モニターの作業領域下端」をモーションの起点とする。下端タスクバー以外は引き続き非対応とする。

```text
DisplayArea.WorkArea.Bottom
          │
          │  進入時はここから上へ
          ▼
┌──────────────┐  ┌──────────────┐
│ Start menu   │  │ Launcher     │
│              │  │ surface      │
└──────────────┘  └──────────────┘
       共通の下端基準・共通の上向きモーション
```

最終位置は現在の配置ロジックを引き継ぎ、次を満たす。

- ランチャーはスタートメニューの右側。
- スマートフォン連携パネルがある場合は、その右端を基準とする。
- スタートとランチャーの下端の視覚的な余白を統一する。
- モニター、DPI、負座標を配置確定時に解決し、アニメーション中は再計算しない。

### 5.2 Visual構造

実装時は概念的に次の構造へ分ける。

```text
LauncherWindow / MotionHost
└─ MotionViewport（透明、画面外部分をクリップ）
   └─ LauncherSurface（角丸、Acrylic、影、入力面）
      └─ LauncherContent（ヘッダー、カード、スライダー）
```

- `MotionHost` は最終表示位置から作業領域下端までの移動路を保持する。
- `LauncherSurface` 全体を一つの Visual として移動する。
- `LauncherSurface` の下端がタスクバー上端より下にある部分はクリップする。
- ウィンドウレベルの背景が先に四角く表示されないよう、Hostは透明に保つ。
- 背景素材は Surface の範囲だけに適用する。

技術スパイクの結果、特定のXAML要素へSystemBackdropを適用する `SystemBackdropElement` はWindows App SDK 2.0で追加されるAPIであり、対象の1.8では利用できなかった。1.8向けの初期実装は、透明なHost内で `LauncherSurface` にテーマの `AcrylicInAppFillColorDefaultBrush` を適用する。2.0以降へ更新する際に、角丸、クリップ、Translation、透明Hostとの組み合わせを再評価する。

### 5.3 Material

現在の `MicaBackdrop Kind="BaseAlt"` は常設アプリウィンドウ寄りの素材である。MicrosoftはMicaをアプリの基礎レイヤー、Desktop Acrylicをフライアウトやコンテキストメニューなどの一時的・light-dismiss UIに推奨している。

ランチャーは一時的なサーフェスなので、再設計では次の順で評価する。

1. Windows App SDK 1.8ではSurfaceへテーマの `AcrylicInAppFillColorDefaultBrush` を適用
2. Windows App SDK 2.0以降への更新時は `SystemBackdropElement` + `DesktopAcrylicBackdrop` を再評価
3. Acrylic非対応、透明効果無効、バッテリー節約、RDPでは標準の単色フォールバック

Window全体の `DesktopAcrylicBackdrop` は、Surfaceの移動中にも最終ウィンドウ矩形全体を先に塗るため採用しない。

独自Tintの調整は初期実装では行わず、標準テーマとコントラストを優先する。

参考:

- [Acrylic material](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
- [System backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)
- [Materials in Windows apps](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials)

## 6. モーション仕様

以下は最初の実装値であり、実機の高速度撮影とフレーム計測後に167/250/333msの標準値の範囲で調整する。

### 6.1 スタート連動の進入

| 項目 | 初期仕様 |
|---|---|
| 起点 | 対象モニターの作業領域下端（タスクバー上端） |
| 終点 | 配置サービスが決定したランチャー最終位置 |
| 方向 | 下から上 |
| 対象 | `LauncherSurface` 一枚 |
| Translation | 移動路下端から `0` |
| Opacity | 原則1を維持。必要な場合のみ開始83ms以内で0から1 |
| Duration | 250msを第一候補。長距離で急に見える場合のみ333msを比較 |
| Easing | `cubic-bezier(0, 0, 0, 1)` |
| Activation | `activate: false`。スタートからフォーカスを奪わない |

バウンド、スプリング、オーバーシュート、回転、拡大縮小は使用しない。スタートメニューの隣に現れる道具面であり、強い祝祭的モーションは目的に合わない。

### 6.2 スタート連動の退出

| 項目 | 初期仕様 |
|---|---|
| 起点 | 現在の表示位置 |
| 終点 | タスクバー上端より下 |
| 方向 | 上から下 |
| Translation | `0` から移動路下端 |
| Opacity | 1から0を83msで完了 |
| Duration | 125ms（初期実装の167msから、実機フィードバックにより短縮） |
| Easing | `cubic-bezier(1, 0, 1, 1)` |

終了は進入より短くし、利用者の操作を待たせない。

### 6.3 ランチャー操作へ移った場合

ランチャーがフォーカスまたはポインター操作を得た時点で `VisibleInteractive` とする。

- 進入中なら最終位置まで進入を完了する。
- スタートメニューが閉じてもランチャーを退出させない。
- 面を動かし直さない。
- カードを個別に再進入させない。
- Escape、外側クリック、アクション正常終了で退出する。

### 6.4 手動ホットキー

`Ctrl+Alt+Space` はスタートとは異なる入口なので、スタートとの同期を装わない。

- 配置は現行どおり対象モニター中央、または将来の設定値とする。
- 167msのDirect Entranceまたは83msのFadeを比較する。
- スタート連動用の長い画面下モーションをそのまま流用しない。
- 同じランチャー面として、角丸、Material、退出時間は共通化する。

## 7. 状態機械

モーション、スタート監視、フォーカスを一つのCoordinatorが管理する。

```text
Hidden
  ├─ Windowsキー単体 ─> AwaitingStartConfirmation
  └─ 手動ホットキー ─> EnteringManual

AwaitingStartConfirmation
  ├─ Start visible + bounds valid ─> EnteringWithStart
  └─ timeout / shortcut / false positive ─> Hidden

EnteringWithStart
  ├─ entrance complete ─> VisibleWithStart
  ├─ launcher interaction ─> VisibleInteractive
  └─ Start hidden before interaction ─> Exiting

VisibleWithStart
  ├─ launcher interaction ─> VisibleInteractive
  └─ Start hidden ─> Exiting

VisibleInteractive
  ├─ Escape / outside click / action success ─> Exiting
  └─ Start hidden ─> VisibleInteractive

Exiting
  ├─ exit complete ─> Hidden
  ├─ Start reopens ─> EnteringWithStart（現在の表示値から反転）
  └─ manual toggle ─> EnteringManual（現在の表示値から反転）
```

禁止事項:

- `ToggleWindow`だけで状態を反転しない。
- 進入中に別の進入を重ねない。
- 退出中に即座に座標を初期値へ戻さない。
- 完了通知をUIタイマーの経過時間だけで判定しない。

## 8. 実装手順

### Step 1: ベースラインを記録する

変更前に次を採取し、再設計後と比較できるようにする。

1. Windowsキーを押してからスタート検出までの時間。
2. スタート検出からランチャー最初の描画までの時間。
3. 進入開始から完了までの実時間。
4. UIスレッドの長時間処理。
5. 60/120/144Hzでの表示フレームと目視上の停止。
6. 初回表示と2回目以降の差。
7. Debugとx64 Releaseの差。

既存ログは状態と時刻の記録にだけ使う。フレームごとのファイル書き込みは計測そのものを乱すので行わない。

### Step 2: 表示ホットパスを分離する

進入開始前後から次を退避する設計にする。

- 音声デバイス列挙と既定デバイス取得はキャッシュを表示し、非同期更新する。
- 設定読込とViewModel構築は常駐開始時に完了させる。
- ItemsControlのコンテナー生成と初回レイアウトを非表示状態で完了させる。
- `UpdateLayout`と同期 `RedrawWindow` に依存しない準備完了条件を定義する。
- UI Automationの詳細診断はアニメーション完了後、または別スレッドで行う。
- スタート連動表示中のWindowsキー単体操作は、UI Automationの非表示確認を待たず即座に退出を開始する。
- 待機中のフォールバック確認ではStart/Searchプロセスのトップレベル要素だけを調べ、子孫ツリーの全走査を行わない。
- 通常ログは状態遷移、開始、完了、キャンセルだけに限定する。

完了条件: アニメーション開始から完了まで、UIスレッドで同期I/O、音声列挙、強制再描画を行わない。

### Step 3: MotionHostの技術スパイクを作る

本実装へ入る前に、小さい検証ブランチで以下を確認する。

1. 透明なHost上で `LauncherSurface` だけを表示できる。
2. SurfaceをHost下端の外側からTranslationして、ウィンドウ境界で正しくクリップできる。
3. 1.8ではSurfaceに適用したテーマ背景がSurfaceと一緒に移動する。2.0移行時は `SystemBackdropElement` のDesktop Acrylicを再検証する。
4. Surface外の透明部分がクリックを不必要に奪わない。
5. 角丸と影が移動中も欠けない。
6. 非アクティブ表示でもCompositionが開始される。
7. Acrylicの単色フォールバックが移動中に点滅しない。

スパイクでSurface単位のBackdropが安定しない場合は、次の順で代案を評価する。

1. 透明Host + Compositionで描く独立Surface
2. 最終サイズのWindow + 短いTranslation/Clip Reveal
3. HWND移動を残す案は最後の手段とし、DWM同期手段を別途技術検証する

### Step 4: Compositionアニメーションを構築する

WinUI 3ではXAML要素に `UIElement.StartAnimation` を使える。`Translation` と `Opacity` を同時に開始し、UIスレッドではなくCompositorに進行を任せる。

実装上の要件:

- `LauncherSurface` 一つだけを基本ターゲットにする。
- TranslationとOpacityは同じCompositorから生成する。
- 複数プロパティはAnimationGroupまたはScopedBatchでまとめる。
- 完了は `CompositionScopedBatch.Completed` で受け取る。
- 終了時に必ず最終値を確定し、アニメーションを停止・破棄する。
- 再表示や反転時は現在のPresentation値から新しいアニメーションを開始する。
- DPI変更は次回表示時に再構築し、実行中の座標系を途中変更しない。

参考:

- [XAML and Composition interoperability](https://learn.microsoft.com/en-us/windows/apps/develop/composition/xaml-comp-interop)
- [Time-based animations](https://learn.microsoft.com/en-us/windows/apps/develop/composition/time-animations)
- [CompositionScopedBatch](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionscopedbatch)

### Step 5: スタート監視と開始タイミングを接続する

1. Windowsキー単体は `AwaitingStartConfirmation` への遷移だけに使う。
2. UI Automationのフォーカス変更イベントを主トリガーにする。
3. `SearchHost`、スタート親要素、矩形条件が揃った最初の通知で配置を確定する。
4. 配置成功後、同一Dispatcherターン内でHostを表示し、Compositionを開始する。
5. 50msポーリングはイベント欠落時の短期間フォールバックに限定する。
6. スタート未確認のままタイムアウトした場合は何も表示しない。
7. `Win+D` 等のショートカットでは待機状態へ入らない。

開始遅延はOS内部アニメーションの固定値を推測して補正しない。イベント到着後に人工的な待機を足すと端末差が拡大するため、確認でき次第開始する。

### Step 6: 退出と割り込みを統合する

次のすべてを同じ `RequestExit(reason)` 経路へ集約する。

- スタート終了かつランチャー非対話
- Escape
- 外側クリック
- アクション正常終了
- 手動ホットキーによる閉鎖
- セッションロックや安全に表示を維持できない状態

退出中の再表示では、古いアニメーションを完了待ちにせず、現在値から進入へ反転する。完了イベントには世代IDを持たせ、キャンセル済みアニメーションの遅延完了が現在状態を上書きしないようにする。

### Step 7: カードアニメーションを整理する

スタートとの一体感を優先し、最初の版ではカード単位の位置アニメーションを無効にする。ランチャー面全体が安定して動くことを先に完成させる。

必要性が確認できた場合だけ、次の条件で再導入する。

- 面の進入完了後に開始する。
- 位置を再度動かさず、83ms程度のOpacityだけを基本とする。
- 最大遅延を100ms以内に抑える。
- スクロールで後から実体化した項目へ入口アニメーションを付けない。

### Step 8: アクセシビリティとフォールバックを実装する

`UISettings.AnimationsEnabled` がfalseの場合は、Translation、Opacity、カード演出を行わず、最終状態へ即時遷移する。起動時の一回読み取りだけでなく、設定変更イベントへの追従も検討する。

- 透明効果無効: 単色背景へ自動フォールバック。
- ハイコントラスト: Materialと影に頼らず、境界線とシステム色で面を識別可能にする。
- RDP/VM: Acrylicのフォールバックを受け入れ、モーション自体はCompositionで継続する。
- バッテリー節約: Acrylicが無効になる前提で単色時の外観を確認する。
- アニメーション無効: 即時表示・即時非表示でも状態機械とフォーカス動作を変えない。

参考:

- [UISettings.AnimationsEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.animationsenabled)

## 9. 検証手順

### 9.1 機能検証

- Windowsキー単体でスタートが開いた場合だけ進入する。
- Windowsキーショートカットでは進入しない。
- スタート表示中はランチャーが非アクティブでも残る。
- ランチャーをクリックした後、スタートが閉じてもランチャーは残る。
- スタート終了時はランチャーが下方向へ退出する。
- 進入中のEscape、退出中の再表示、キー連打で点滅しない。
- 退出完了後は透明Hostが入力を遮らない。

### 9.2 表示環境

- 60Hz、120Hz、144Hz
- 100%、125%、150%、200%、300% DPI
- 中央揃え、左揃えタスクバー
- 1～4モニター、主画面以外、負座標、異なるDPI間
- ライト、ダーク、ハイコントラスト
- 透明効果オン・オフ
- アニメーション効果オン・オフ
- RDP、スリープ復帰、Explorer再起動

### 9.3 性能目標

以下はMicrosoftの保証値ではなく、本プロジェクトの初期受け入れ目標とする。

- `SetWindowPos` をアニメーションフレームごとに呼ばない。
- 進入・退出中のUIスレッドに16msを超える同期処理を置かない。
- 60Hz環境で目視できる停止、逆戻り、位置飛びを発生させない。
- スタート表示確認からランチャー初回表示要求まで50ms以内を目標とする。
- 進入の実測時間を指定値の±1フレーム程度に収める。
- 100回連続表示で最終位置のずれ、透明Host残留、入力遮断を発生させない。
- 8時間常駐後もAnimation、Visual、ScopedBatchの継続的な増加を起こさない。

## 10. 実装の分割単位

一度に置き換えず、次の単位で進める。

1. 計測と状態遷移ログだけを追加する。
2. 音声取得と強制レイアウトを表示ホットパスから外す。
3. MotionHost + 単色Surfaceの技術スパイクを行う。
4. Compositionによる進入だけを実装する。
5. Compositionによる退出と割り込み反転を実装する。
6. スタート監視Coordinatorへ状態機械を統合する。
7. Desktop Acrylic / SystemBackdropElementを適用する。
8. アクセシビリティとフォールバックを実装する。
9. 実機マトリクスで時間とイージングを確定する。
10. 旧 `WindowAnimationService` のタイマーベース実装を削除する。

各段階でx64 DebugとReleaseをビルドし、前段の動作を維持したまま進める。

## 11. 完了条件

次をすべて満たした時点でモーション再設計を完了とする。

- スタートと同じタスクバー上端から、ランチャー面が上方向へ一続きに進入する。
- スタート右側の最終位置へ、点滅、瞬間移動、終端の段付きなしで停止する。
- 進入はCompositionで実行され、UIスレッドのタイマーと毎フレームのHWND移動を使わない。
- スタート連動、対話状態、退出、途中反転が明示的な状態機械で管理される。
- スタート表示中はフォーカスを奪わず、ランチャー操作後はスタート終了に巻き込まれない。
- transient UIとしてDesktop Acrylicを基本にし、非対応環境では読みやすい単色へ戻る。
- アニメーション効果無効時は、動きなしで同じ機能と状態遷移を維持する。
- 60/120/144Hz、主要DPI、複数モニターでカクつきと位置ずれがない。
- 旧方式より入力から表示までの遅延が増えていない。

## 12. 変更対象候補（実装時）

本手順書の作成時点では変更しない。実装時の主な対象候補は次のとおり。

- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Services/WindowAnimationService.cs`
- `Services/IWindowAnimationService.cs`
- `Services/WindowInteropService.cs`
- `Services/HybridStartMenuMonitor.cs`
- `Services/LauncherPlacementService.cs`
- 新規 `LauncherMotionCoordinator` または同等の状態管理サービス
- モーション、配置、状態遷移のテストプロジェクト

## 13. 実装時の技術判断

- `SystemBackdropElement` はWindows App SDK 2.0で追加されるAPIであり、対象の1.8には含まれない。このため初期実装は透明な `MotionViewport` と、移動する `LauncherSurface` に `AcrylicInAppFillColorDefaultBrush` を適用する構成とした。これによりSurface外を塗らず、透明効果・ハイコントラスト時はテーマの単色へフォールバックする。
- Window全体の `DesktopAcrylicBackdrop` はSurface移動中も最終ウィンドウ矩形全体を塗ってしまうため採用しない。Surface単位のDesktop Acrylicは対応APIを利用できるWindows App SDKへ更新する際に再検証する。
- モーションは `UIElement.StartAnimation` でSurfaceの `Translation` と `Opacity` をCompositionプロパティセットへ接続し、`CompositionScopedBatch.Completed` で完了させる。途中反転は世代IDで古い完了通知を無効化し、経過時間から求めた現在値を開始値として再構築する。
- 診断ログはUIスレッドからファイルを書かず、バックグラウンドライターへキューイングする。
- スタート監視はUI Automationでフォーカス中のStartMenuExperienceHost／SearchHostとその祖先から有効な矩形を取得できる場合はそれを優先し、取得できない場合だけ可視・非クローキングWin32ウィンドウへフォールバックする。Start/Searchプロセスやスマートフォン連携パネルの子孫ツリー全走査は1秒前後ブロックする実測があったため表示経路から除外し、ランチャーが操作状態へ移った時点で監視も停止する。スマートフォン連携パネルの余白は利用者設定による予約を使用する。
- 操作状態へ移った時点で監視Snapshotを非表示へ確定する。退出中に新しいWindowsキー入力または新しいスタート表示Snapshotを受けた場合は、現在位置から進入へ反転する。退出開始前の古いSnapshotでは再表示しない。
- Windowsキー待機中でなくても、フォーカス変更時にWin32でStartMenuExperienceHost／SearchHostの可視ウィンドウを確認できた場合は、スタートボタンクリックによる表示としてランチャーを進入させる。

## 14. 実装・検証状況

- Step 1～9のコード置換は完了した。
- 16msのUIタイマーと毎フレームのHWND移動、表示時の強制レイアウト・同期再描画を削除した。
- 当時の技術検証ではx64／ARM64のDebugとReleaseをビルドした。現在の製品検証・配布対象はx64だけとし、ReleaseはReadyToRun有効、トリミング無効を正とする。
- 60 / 120 / 144Hz、100 / 125 / 150 / 200%、複数モニター、RDP、モーション無効、途中反転100回は実機検証待ちとする。

