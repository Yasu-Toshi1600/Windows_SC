# スタート連動機能 保守ガイド

ランチャーSurfaceの移動量、時間、イージング、Composition実装、途中反転の視覚仕様は[モーション仕様書](MOTION_SPECIFICATION.md)を参照する。

更新日: 2026-07-21
対象: Windows 11 25H2 / Windows App SDK 1.8 / Phase 4.5実装
目的: スタートメニュー検出、Windowsキー監視、ランチャー状態、フォーカス、モーションの保守判断を一か所に集約する。

## 1. 複雑になる理由

Windowsには、外部アプリからスタートメニューの開閉、表示矩形、アニメーション進行率を一括取得する安定した公開APIがない。Windows_SCはOSのスタートを制御せず、次の観測結果を組み合わせてランチャーを同期させる。

- Windowsキー単体操作
- `StartMenuExperienceHost` / `SearchHost` のWin32ウィンドウ状態
- UI Automationのフォーカス要素とその祖先
- ランチャー自身の表示、フォーカス、操作状態
- 短時間の有界フォールバック確認

一方式だけに依存すると、次の問題が起きる。

- Windowsキーだけでは、OS側でスタートが開かなかった場合にも誤表示する。
- UI Automationだけでは、イベント欠落や1秒前後のCOM待機が発生する場合がある。
- Win32だけでは、Windows更新でプロセスやクローキング方法が変わる可能性がある。
- 単純な表示トグルでは、進入中の終了、退出中の再表示、古い完了通知が競合する。

このため、検出、状態管理、配置、モーションを分離し、`MainWindow` で統合している。

## 2. 現在保証する動作

### スタートを開く

- Windowsキー単体でスタートが開いた場合、ランチャーを非アクティブで表示する。
- タスクバーのスタートボタンをクリックした場合も表示する。
- Windowsキーを含むショートカットでは表示しない。
- `Ctrl+Alt+Space` はスタートと無関係な手動トグルとして扱う。

### スタートを閉じる

- スタート連動状態でWindowsキーを押した場合、OSの非表示検出を待たず退出を開始する。
- スタートボタンの再クリックやスタート外側のクリックでは、Snapshotが非表示になった時点で退出する。
- ランチャーを一度操作した後は、スタートが閉じてもランチャーを維持する。

### ランチャーを閉じる

- 対話状態での画面外クリック、Escape、アクション成功で退出する。
- 退出はCompositionの下方向Translation 125msとOpacity 83msで行う。
- 退出中にWindowsキーでスタートを開き直した場合は、現在値から進入へ反転する。

## 3. コンポーネント責務

| ファイル | 責務 |
|---|---|
| `GlobalWindowsKeyMonitor.cs` | 低レベルキーボードフック。左右Windowsキー、修飾キー、他キーとの組み合わせを分類する。入力は遮断しない。 |
| `Services/GlobalInputService.cs` | Windowsキー単体イベントと `Ctrl+Alt+Space` の登録・公開。 |
| `StartMenuWindowInspector.cs` | Win32による軽量検出。対象プロセス、可視状態、DWMクローキング、矩形を確認する。 |
| `StartMenuBoundsValidator.cs` | SearchHost等のモニター全面サーフェスをスタートメニュー矩形から除外する。 |
| `UiAutomationStartMenuInspector.cs` | スキャン用STAとイベント登録用STAを分離し、フォーカス中のUI Automation矩形を優先し、取得不能時だけWin32検出へフォールバックしてSnapshotを更新する。 |
| `Services/HybridStartMenuMonitor.cs` | イベント監視と50/250msの有界フォールバック、監視の開始・停止。 |
| `StartMenuSnapshot.cs` | 可視状態、スタート矩形、スマートフォン連携パネル情報の不変Snapshot。 |
| `Services/LauncherMotionCoordinator.cs` | ランチャー表示の論理状態機械。 |
| `Services/CompositionLauncherMotionService.cs` | SurfaceのTranslation / Opacity、完了通知、途中反転。 |
| `MainWindow.xaml.cs` | 入力、Snapshot、フォーカス、状態機械、配置、モーションの統合。 |
| `Services/LauncherPlacementService.cs` | Snapshot、モニター、DPI、作業領域から最終配置を計算する。 |

OS依存の検出条件は `StartMenuWindowInspector` と `UiAutomationStartMenuInspector` の外へ広げない。

## 4. スタート検出の優先順位

### 4.1 第1段: UI Automationフォーカス検出

複数モニターでは、最初にフォーカス要素から現在表示中のスタート矩形を確認する。

1. `StartMenuExperienceHost` と `SearchHost` の現在PIDを取得する。
2. `AutomationElement.FocusedElement` のPIDが対象PIDか確認する。
3. フォーカス要素からRawViewの親方向だけをたどる。
4. Window候補を優先して最小の有効矩形を選ぶ。

`TreeScope.Descendants` による子孫全走査は禁止する。実機で1回1.0～1.2秒ブロックし、Windowsキー起動、画面外クリック、Composition完了通知を遅らせたためである。

フォーカスされたスタート矩形をWin32列挙結果より優先する。`SearchHost`は複数モニターに可視ウィンドウを持つ場合があり、列挙順で選ぶとスタートとランチャーが別モニターへ分離するためである。

### 4.2 第2段: Win32ウィンドウ補助検出

UI Automationで有効なフォーカス矩形を取れない場合のみ、`StartMenuWindowInspector.TryGetStartMenuBounds` を実行する。

1. 前景ウィンドウのプロセスが `StartMenuExperienceHost` または `SearchHost` なら矩形を確認する。
2. フォーカスがExplorerの`StartButton`に残っている場合は、その矩形が属するモニターをクリック元として保持する。
3. 取れない場合はトップレベルウィンドウだけを列挙する。
4. `IsWindowVisible == true` を要求する。
5. `DWMWA_CLOAKED` でクローキングされていないことを要求する。
6. 幅200px、高さ100px未満の矩形を除外する。
7. モニター領域または作業領域の縦横90%以上を覆う矩形を除外する。
8. クリック元が分かる場合は同じモニターの候補だけを採用する。一致候補がなければ別モニターの候補へフォールバックせず、その回は未検出とする。

`SearchHost`は環境によってスタートメニュー本体とは別にモニター全面のホストウィンドウを持つ。この矩形を採用すると、配置処理がスタートの右側に空きがないと誤判定するため、プロセス名と最小サイズだけで矩形を採用してはいけない。

### 4.3 スタートボタンクリック

アイドル時は連続ポーリングしない。グローバルフォーカス変更時にWin32でスタートウィンドウが可視かだけを確認する。可視ならSTAワーカーへスキャンを要求し、`Hidden` から `EnteringWithStart` へ遷移する。

## 5. 監視期間と負荷制御

`HybridStartMenuMonitor` は次の期間だけフォールバックTimerを動かす。

| 状態 | 間隔 | 目的 |
|---|---:|---|
| Windowsキー解放後 | 50ms、最大1500ms | イベント欠落を補い、スタート表示を確認する。 |
| スタート連動表示中・未操作 | 250ms | スタート終了イベントの欠落を補う。 |
| UI Automationイベント登録待ち | 250ms | Win32の軽量確認だけを行い、登録が外部プロバイダーで停止しても起動経路を維持する。 |
| ランチャー対話状態 | 停止 | 外側クリックやアプリ操作と監視を競合させない。 |
| 非表示アイドル | 停止 | CPUとCOM呼び出しを抑える。クリック起動はフォーカスイベントで拾う。 |

対話状態へ移った時点でSnapshotを `Hidden` に確定する。停止前の `Visible` がアクション終了後まで残り、退出中のランチャーを誤って再進入させることを防ぐためである。

## 6. 状態機械

| 状態 | 意味 | 主な遷移先 |
|---|---|---|
| `Hidden` | HWNDは非表示。 | Windowsキーで `AwaitingStartConfirmation`、スタートボタンクリックで `EnteringWithStart`、手動ホットキーで `EnteringManual`。 |
| `AwaitingStartConfirmation` | Windowsキー単体を確認し、スタートの実表示を待つ。 | 表示確認で `EnteringWithStart`、1500msタイムアウトで `Hidden`。 |
| `EnteringWithStart` | スタート連動の250ms進入中。 | 完了で `VisibleWithStart`、操作で `VisibleInteractive`、Windowsキーで `Exiting`。 |
| `EnteringManual` | 手動ホットキーの167ms進入中。 | 完了または操作で `VisibleInteractive`。 |
| `VisibleWithStart` | スタートと連動し、まだランチャー未操作。 | 操作で `VisibleInteractive`、スタート終了で `Exiting`。 |
| `VisibleInteractive` | ランチャーが操作対象。スタート状態から独立。 | 外側クリック、Escape、アクション成功で `Exiting`。 |
| `Exiting` | 125ms退出中。 | 完了で `Hidden`、最後に確認した要求がVisibleなら現在値から進入へ反転。 |

### 最終要求状態への収束

`_startLinkedVisibilityRequested`は、未操作のスタート連動中に最後に確認したSnapshotがVisibleかHiddenかを保持する。Hiddenなら退出方向、Visibleなら表示方向へ進める。退出完了とVisible通知が競合した場合も、Hide完了後に最新Snapshotを再確認し、Visibleなら直ちに進入へ戻す。

ランチャーがフォーカスを得て`VisibleInteractive`へ移った時点では、この要求を破棄してSnapshotをHiddenへ確定する。これにより、アクション成功後に古いVisibleへ戻ることを防ぐ。

連続クリックでは、退出アニメーション中に`Hidden → Visible`が約100～150msで到着する場合がある。このVisibleを最後の要求として受け入れ、必要なら新しいStart矩形へ再配置してから反転する。状態遷移を単純な`_isVisible`トグルへ戻してはいけない。

## 7. 操作別シーケンス

### Windowsキーで開く

```text
GlobalWindowsKeyMonitor
  → Windowsキー単体を分類
  → MainWindow: AwaitingStartConfirmation
  → HybridStartMenuMonitor: 50ms監視を最大1500ms開始
  → Win32可視ウィンドウ確認
  → 必要時のみUI Automationフォーカス確認
  → Snapshot.Visible + Bounds
  → 配置確定
  → AppWindow.Show(activate: false)
  → Composition進入
```

### Windowsキーで閉じる

```text
Windowsキー単体
  → EnteringWithStart / VisibleWithStart
  → Snapshotを即時Hiddenへ確定
  → UI Automationの非表示確認を待たない
  → Composition退出を即時開始
```

OS側の非表示検出を待つ方式へ戻すと、フォーカスやCOM応答によって約1秒遅れる場合がある。

### スタートボタンで開く

```text
OSのフォーカス変更イベント
  → Win32でStart/Searchウィンドウの可視状態を確認
  → STAワーカーへスキャン要求
  → Snapshot.Visible
  → Hiddenから直接EnteringWithStart
```

### ランチャー操作から画面外クリック

```text
ランチャーのPointer / Activated
  → VisibleInteractive
  → Start監視停止 + Snapshot.Hidden
画面外クリック
  → Window.Deactivated
  → Exiting
  → Composition完了
  → AppWindow.Hide
```

## 8. スレッド境界

| 処理 | 実行場所 |
|---|---|
| WinUI、AppWindow、Coordinator、Composition開始 | UIスレッド |
| 低レベルキーボードフック | 登録スレッド。コールバック処理は短時間に限定する。 |
| UI Automationスキャン | スキャン専用STAスレッド |
| UI Automationフォーカスイベント登録 | イベント専用STAスレッド。外部プロバイダーによる停止をスキャンへ伝播させない。 |
| 診断ログのファイル書き込み | バックグラウンドライター |
| Compositionアニメーション進行 | Compositor |

SnapshotChangedは `HybridStartMenuMonitor` が `DispatcherQueue` へ戻してから `MainWindow` に通知する。UI AutomationワーカーからWinUI要素を直接操作してはいけない。

## 9. Snapshotの扱い

`StartMenuSnapshot` は次を含む。

- `IsVisible`
- `Bounds`
- `IsPhonePanelVisible`
- `PhonePanelBounds`

表示条件は `IsVisible == true` かつ `Bounds != null`。Snapshotは現在の観測結果であり、イベント履歴ではない。

重要なリセット点:

- Windowsキーで閉じるとき: `NotifyStartMenuClosing`
- ランチャーが対話状態へ移るとき: `SetLauncherInteractive(true)`
- 退出完了でランチャーが非表示になるとき: `SetLauncherVisible(false)`

スマートフォン連携パネルの子孫UI Automation走査は性能問題のため現在無効。`AssumePhonePanelVisible` による予約幅を使用する。

## 10. ログの読み方

ログ: `%LOCALAPPDATA%\Windows_SC\Logs\window-diagnostics.log`

### 正常なWindowsキー起動

```text
[WindowsKey] key-up; classification=standalone
[MotionState] from=Hidden to=AwaitingStartConfirmation
[UIAutomation] start-menu-state=visible-win32-window
[LaunchTiming] event=start-confirmed key-to-start-ms=...
[Launcher] action=show-request ...
[Motion] direction=Entrance completed=true ...
```

UI Automationフォーカス経由では `visible-focused-element` になる。

### 正常なWindowsキー終了

```text
[LaunchTiming] event=windows-key-close action=exit-immediate
[StartMenuMonitor] state=hidden source=windows-key-close
[MotionState] ... to=Exiting reason=windows-key-close
[Motion] direction=Exit completed=true elapsed-ms=...
```

### 正常なスタートボタンクリック

`show-request` のreasonが `start-menu-click-detected` になり、`key-to-start-ms=-1.0` になる。キー入力がないため `-1.0` は異常ではない。

### 主要時間

| ログ値 | 意味 | 目安 |
|---|---|---:|
| `key-to-start-ms` | Windowsキー解放からスタート確認 | 通常10～100ms。コールド準備は分離する。 |
| `start-to-request-ms` | スタート確認から表示要求 | 50ms以内。 |
| `request-to-complete-ms` | 表示要求から進入完了 | 約250ms＋数ms。 |
| Entrance `elapsed-ms` | Composition進入 | 約250ms。 |
| Exit `elapsed-ms` | Composition退出 | 約125ms。 |
| UIA `scan=slow` | 1回の確認が50ms超 | 継続発生する場合は回帰。 |

### 異常パターン

- `WindowsKey standalone` の後に `start-confirmation=expired`: OSがスタートを開かなかったか検出漏れ。Win32候補とSearchHost PIDを確認する。
- `scan=slow` が1秒前後で連続: 子孫ツリー走査の再導入、COM停止、対象プロセス応答停止を疑う。
- `focus-event=registered elapsed-ms` が大きい: 外部UI Automationプロバイダーによる登録停止。スキャン用STAが独立しているため、Windowsキー起動は継続できなければならない。
- `WindowPlacement result=failed` の `start` がモニター作業領域と一致: モニター全面サーフェス除外の回帰。
- アクション成功直後に `Exiting → EnteringWithStart`: Snapshotリセットまたは反転ガードの回帰。
- Exitが200ms超: UIスレッド処理、設定画面生成、DispatcherQueue遅延を確認する。
- `compiled-bindings=initialized items=0`: 設定適用順または初回 `Bindings.Update()` の位置を確認する。

## 11. 過去の回帰と防止策

| 症状 | 原因 | 現在の防止策 |
|---|---|---|
| 初回表示で静的な「設定」だけ見え、登録項目が出ない | 表示ホットパス整理時に初回のcompiled binding初期化まで削除した | ウィンドウ生成時に `Bindings.Update()` を一度だけ実行し、表示処理では実行しない。 |
| Windowsキーで閉じると約1秒遅れる | StartのHiddenをUI Automationで確認してから退出していた | `EnteringWithStart` / `VisibleWithStart` ではWindowsキー解放時に即退出する。 |
| 画面外クリックや設定画面表示が遅れる | Start/Searchの子孫ツリー全走査が1.0～1.2秒ブロックした | 子孫走査を削除し、Win32トップレベル＋UIA祖先方向だけに限定。対話開始時に監視停止。 |
| Windowsキーで開かない回がある | UIAフォーカスだけに絞り、Startがフォーカスとして公開されない回を検出できなかった | 可視・非クローキングWin32ウィンドウを50msフォールバックで確認する。 |
| スタートボタンクリックで起動しない | Windowsキー後だけ監視を有効にし、アイドル中のクリック経路を失った | フォーカス変更時にWin32可視状態を軽く確認し、クリック由来のSnapshotを許可する。 |
| アクション成功後に一瞬再表示する | 対話開始前の `Visible` Snapshotが残り、退出中の再開条件を満たした | 対話開始時にSnapshotをHiddenへ確定し、スタート連動の最終要求を破棄する。 |
| 連続クリック後に起動しなくなる | 退出中の新しいVisibleを無視し、退出完了後に状態変更通知が残らなかった | 未操作のスタート連動中はVisible／Hiddenの最後の要求へ収束し、Hide完了時にも最新Snapshotを再確認する。 |
| 複数モニターで別画面へ表示される | ExplorerのStartButtonにフォーカスが残る間、複数SearchHost候補を列挙順で選んだ | StartButton矩形のモニターと候補を対応付け、不一致候補へフォールバックしない。 |
| 退出が体感上遅い | 167ms退出と同時間のOpacityが残った | Translation 125ms、Opacity 83msへ短縮。 |

この表の防止策を削除・一般化する場合は、対応する回帰試験を先に自動化または実機実施する。

## 12. 変更時の禁止事項

1. `TreeScope.Descendants` でStart/Searchツリー全体を同期走査しない。
2. 非表示アイドル中に50ms/250ms Timerを常時動かさない。
3. Windowsキーで閉じるとき、UI AutomationのHidden通知を待たない。
4. 表示ホットパスへ音声列挙、ファイルI/O、`UpdateLayout`、同期再描画を入れない。
5. `VisibleInteractive`へ移った後に、スタート由来の古いVisibleで進入反転しない。
6. `SearchHost` を検出対象から外さない。25H2実機ではスタートがSearchHostとして公開される。
7. DWMクローキング確認なしに、存在するだけのSearchHostを表示中と判定しない。
8. UI AutomationワーカーからWinUIやAppWindowを直接操作しない。
9. Windowsキーをブロック、置換、疑似入力しない。
10. Composition完了前にHWNDを瞬間移動しない。

## 13. 回帰試験チェックリスト

### 必須操作

- [ ] Windowsキーで10回連続して開閉できる。
- [ ] 300ms程度の連打でも点滅、取り残し、誤反転がない。
- [ ] タスクバーのスタートボタンをクリックして開閉できる。
- [ ] スタート検索へ文字入力してもランチャーがフォーカスを奪わない。
- [ ] ランチャーをクリックすると `VisibleInteractive` になり、スタートが閉じても残る。
- [ ] 画面外クリックで約125ms以内に退出を開始・完了する。
- [ ] Escapeで退出する。
- [ ] ボタン、音声切り替え、コマンド切り替え成功後に再表示しない。
- [ ] 退出中にWindowsキーを押すと現在位置から自然に反転する。
- [ ] `Ctrl+Alt+Space` がスタートと無関係に動作する。

### 環境

- [ ] 初回起動直後とUI Automation準備後。
- [ ] 100 / 125 / 150 / 200% DPI。
- [ ] 複数モニター、負の画面座標、各モニターでのWindowsキー／クリック。
- [ ] 60 / 120 / 144Hz。
- [ ] Explorer、SearchHost、StartMenuExperienceHostの再起動後。
- [ ] スリープ復帰後。
- [ ] Windowsのアニメーション無効時。

### ログ合格条件

- [ ] `start-to-request-ms <= 50` が通常時に維持される。
- [ ] Exitが通常約125ms。
- [ ] `scan=slow` が連続しない。
- [ ] `start-confirmation=expired` が通常のスタート表示で発生しない。
- [ ] アクション成功直後に意図しない `Exiting → EnteringWithStart` がない。

## 14. OS更新時の確認順

1. プロセス名が `StartMenuExperienceHost` / `SearchHost` のままか確認する。
2. 可視時にトップレベルウィンドウが列挙できるか確認する。
3. 非表示時に `IsWindowVisible` または `DWMWA_CLOAKED` で除外できるか確認する。
4. 取得矩形がスタート本体か、全画面透明Hostではないか確認する。
5. UI AutomationのフォーカスPID、AutomationId、親Windowを採取する。
6. 回帰試験チェックリストを実行する。

検出条件を追加する場合は、表示中と非表示中の両方で候補を記録し、誤検出しないことを確認してから採用する。

## 15. 既知の制約

- Windowsの非公開実装に観測上依存するため、OS更新でプロセス名やウィンドウ構造が変わる可能性がある。
- `SearchHost` はタスクバー検索などでも使われる可能性がある。現在は可視で十分な大きさのSearchHostをスタート系Surfaceとして扱うため、タスクバー検索を利用する環境では誤表示の有無を確認する。
- 下端タスクバー以外は初期対応外。
- スマートフォン連携パネルの個別矩形検出は無効で、設定による予約幅を使用する。
- スタートとランチャーは別プロセスであり、アニメーション進行率を共有していない。
- セキュリティ製品や高負荷状態でキーボードフック、UI Automationイベントが遅延する可能性がある。
- スタートボタンをクリックした直後にExplorerの`StartButton`を取得できる場合は、同じモニターのWin32候補だけを採用する。OS更新等で`StartButton`のAutomationIdもスタート本体のフォーカスも取得できない場合は、Win32候補だけではクリック元を確定できない可能性が残る。

不具合時は、まず入力、検出、表示要求、Compositionのどの区間が遅いかログで分ける。検出精度を上げる目的で重い全ツリー走査へ戻さず、Win32条件、対象イベント、有界フォールバックの順に検討する。
