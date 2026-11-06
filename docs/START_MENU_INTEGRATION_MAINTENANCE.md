# スタート連動機能 保守ガイド

ランチャーSurfaceの移動量、時間、イージング、Composition実装、途中反転の視覚仕様は[モーション仕様書](MOTION_SPECIFICATION.md)を参照する。

更新日: 2026-11-06
対象: Windows_SC `0.9.10` / Windows 11 25H2 / Windows App SDK 1.8
目的: スタートメニュー検出、Windowsキー監視、ランチャー状態、フォーカス、モーションの保守判断を一か所に集約する。

## 1. 複雑になる理由

Windowsには、外部アプリからスタートメニューの開閉、表示矩形、アニメーション進行率を一括取得する安定した公開APIがない。Windows_SCはOSのスタートを制御せず、次の観測結果を組み合わせてランチャーを同期させる。

- Windowsキー単体操作
- `StartMenuExperienceHost` / `SearchHost` のWin32ウィンドウ状態
- UIAオン時に使用するUI Automationのフォーカス要素とその祖先（既定では停止）
- ランチャー自身の表示、フォーカス、操作状態
- 短時間の有界フォールバック確認

一方式だけに依存すると、次の問題が起きる。

- Windowsキーだけでは、OS側でスタートが開かなかった場合にも誤表示する。
- UI Automationだけでは、イベント欠落や1秒前後のCOM待機が発生する場合がある。
- Win32だけでは、Windows更新でプロセスやクローキング方法が変わる可能性がある。
- 単純な表示トグルでは、進入中の終了、退出中の再表示、古い完了通知が競合する。

このため、検出、状態管理、配置、モーションを分離し、`MainWindow` で統合している。

## 2. 現行実装の動作

### スタートを開く

- 表示検証でHWNDが非最前面の場合、最初の再試行でPresenterの`IsAlwaysOnTop`をfalse→trueに再適用し、非アクティブの`SetWindowPos`で復旧する。`Activate`や`Show(true)`でスタートのフォーカスを奪わない。
- 各再試行後は次のUIタイマーで実状態を再確認する。最後の再試行前の状態で失敗を確定しない。上限後は既存のStart非表示待ちガードと退出処理を維持する。

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
| `Windows_SC/GlobalWindowsKeyMonitor.cs` | 低レベルキーボードフック。左右Windowsキー、修飾キー、他キーとの組み合わせを分類する。入力は遮断しない。 |
| `Windows_SC/Services/GlobalInputService.cs` | Windowsキー単体イベントと`Ctrl+Alt+Space`の登録・公開。休止状態／スリープ復帰時はフックと固定ホットキーを再登録する。 |
| `Windows_SC/StartMenuWindowInspector.cs` | Win32による軽量検出。対象プロセス、可視状態、DWMクローキング、矩形を確認する。 |
| `Windows_SC/StartMenuBoundsValidator.cs` | SearchHost等のモニター全面サーフェスをスタートメニュー矩形から除外する。 |
| `Windows_SC/UiAutomationStartMenuInspector.cs` | 既定はWin32のみのMTAスキャンでSnapshotを更新する。設定でUIAをオンにした場合だけUIAフォーカス確認とイベント専用MTAを起動する。 |
| `Windows_SC/Services/HybridStartMenuMonitor.cs` | Windowsキーと50/125msの有界確認、監視の開始・停止。復帰時は旧世代の停止を待って同じ既定モードのインスペクターを作り直す。 |
| `Windows_SC/StartMenuSnapshot.cs` | 可視状態、スタート矩形、スマートフォン連携パネル情報の不変Snapshot。 |
| `Windows_SC/Services/LauncherMotionCoordinator.cs` | ランチャー表示の論理状態機械。 |
| `Windows_SC/Services/CompositionLauncherMotionService.cs` | SurfaceのTranslation / Opacity、完了通知、途中反転。 |
| `Windows_SC/MainWindow.xaml.cs` | 入力をUIスレッドへ非同期転送し、Snapshot、フォーカス、状態機械、配置、モーションを統合する。 |
| `Windows_SC/Services/LauncherPlacementService.cs` | Snapshot、モニター、DPI、作業領域から最終配置を計算する。 |

OS依存の検出条件は `StartMenuWindowInspector` と `UiAutomationStartMenuInspector` の外へ広げない。

## 4. スタート検出の優先順位

### 4.0a Win32イベントとフォーカス補助（2026-11-06）

UIAオン・オフの双方でWin32イベント通知を併用する。`Win32StartMenuEventMonitor`は
UIスレッドで前景変更、FOCUS、SHOW/HIDE、LOCATIONCHANGE、CLOAKED/UNCLOAKEDの
7種類をOUTOFCONTEXT・SKIPOWNPROCESSで登録し、同じスレッドで解除する。
前景変更は全体を契機とし、それ以外はStart/Search/ExplorerのPIDを確認してから要求する。
状態イベントはOBJID_WINDOW・CHILDID_SELFに限定し、フォーカス通知は部品IDを解決しない。
対象PID一覧は125msタイマー内で最大2秒ごとに更新する。AccessibleObjectFromEvent等は呼ばない。

コールバックはスキャン要求をUIキューへ保留1件に集約する。同期COMや列挙は実行しない。
対話中・破棄後・旧世代の通知は捨てる。フック登録失敗時も125ms確認で動作し、
復帰時は再登録する。解除失敗時は無効化したコールバックを保持し、新しいフックを重ねない。

Win32スキャンではGetGUIThreadInfoのhwndFocusをGetAncestor(GA_ROOT)でたどり、
Start/Searchの検証済みルート矩形を前景候補とともに優先する。
どちらも可視・非クローキング・サイズ条件を適用する。起動元がExplorerの
Shell_TrayWnd／Shell_SecondaryTrayWndならタスクバー矩形からモニターを選ぶ。
これはStartButton自体を識別する処理ではなく、専用HWNDがない部品階層も復元しない。
前景・フォーカスが取得不能なら既存の候補列挙へ戻る。UIAから明示された起動元はタスクバー推定で上書きしない。ただし有効なStart/Searchのフォーカス・前景候補は先に採用し、起動元モニターによる絞り込みは候補列挙時に適用する。

125ms確認とWindowsキー後の50ms確認は通知欠落対策として維持する。
現行ビルドのStartイベント受信と複数モニターのGUI確認は未完了。

### 4.0 既定経路（2026-10-30）

LockAppへのIAccessible解放がファイナライザーを待たせることをダンプで確認したため、
既定は`win32-only`。グローバルUIAフォーカス通知の登録とFocusedElement取得を両方停止する。
`UiAutomationStartMenuInspector`は既存の名前を保つが、既定ではWin32スキャン用MTAだけを開始し、
イベント用MTAは開始しない。通常起動・スリープ復帰とも同じモードを維持する。

既存の`StartMenuWindowInspector`でStart/Searchの前景ウィンドウを優先し、
可視・非クローキング・サイズ検証を通るトップレベル候補からSnapshotを更新する。
UIAのStartButton矩形によるモニター補助は既定では停止するが、Win32のフォーカス・
タスクバーHWNDによる補助を追加した。複数候補がある場合の選択は実機確認が必要。
ボタン起動はWin32通知と125ms周期の可視確認で検出する。

以下4.1〜4.3と5章のUIAイベントに関する説明は、コンストラクターへ
`enableUiAutomation: true`を渡すUIAオンの経路に限る。設定画面の「次回起動時のUIA監視」を
明示保存し、再起動すると適用される。保存前の切り替えと保存直後は実行中のモードを変更しない。
JSONの`EnableUiAutomation`がない既存設定はfalse。通常の保存・診断ログ設定の適用でも値を保持する。
起動時に選択した値を監視サービスで保持し、スリープ復帰による再生成にも同じ値を渡す。
統合後の確認状況は[現在の状況](NEXT_STEPS_AND_DECISIONS.md)を参照。

### 4.1 UIAオンの経路の第1段: フォーカス検出

複数モニターでは、最初にフォーカス要素から現在表示中のスタート矩形を確認する。

1. `StartMenuExperienceHost` と `SearchHost` の現在PIDを取得する。
2. `AutomationElement.FocusedElement` のPIDが対象PIDか確認する。
3. フォーカス要素からRawViewの親方向だけをたどる。
4. Window候補を優先して最小の有効矩形を選ぶ。

`TreeScope.Descendants` による子孫全走査は禁止する。実機で1回1.0～1.2秒ブロックし、Windowsキー起動、画面外クリック、Composition完了通知を遅らせたためである。

フォーカスされたスタート矩形をWin32列挙結果より優先する。`SearchHost`は複数モニターに可視ウィンドウを持つ場合があり、列挙順で選ぶとスタートとランチャーが別モニターへ分離するためである。

### 4.2 Win32検出（UIAオンの経路では第2段）

既定経路では常に`StartMenuWindowInspector.TryGetStartMenuBounds`を実行する。
UIAオンの経路では有効なフォーカス矩形を取れない場合に実行する。以下の2はUIAオンの場合だけ適用し、8はWin32のタスクバー補助またはUIAで起動元を得た場合に適用する。

1. 前景ウィンドウのプロセスが `StartMenuExperienceHost` または `SearchHost` なら矩形を確認する。
2. フォーカスがExplorerの`StartButton`に残っている場合は、その矩形が属するモニターをクリック元として保持する。
3. 取れない場合はトップレベルウィンドウだけを列挙する。
4. `IsWindowVisible == true` を要求する。
5. `DWMWA_CLOAKED` でクローキングされていないことを要求する。
6. 幅200px、高さ100px未満の矩形を除外する。
7. モニター領域または作業領域の縦横90%以上を覆う矩形を除外する。
8. クリック元が分かる場合は同じモニターの候補だけを採用する。一致候補がなければ別モニターの候補へフォールバックせず、その回は未検出とする。

`SearchHost`は環境によってスタートメニュー本体とは別にモニター全面のホストウィンドウを持つ。この矩形を採用すると、配置処理がスタートの右側に空きがないと誤判定するため、プロセス名と最小サイズだけで矩形を採用してはいけない。

### 4.3 UIAオンの経路のスタートボタンクリック

グローバルフォーカス変更時にWin32でスタートウィンドウが可視か確認する。可視なら
MTAワーカーへスキャンを要求し、`Hidden`から`EnteringWithStart`へ遷移する。
さらに、UI Automationのフォーカス通知が長時間稼働後に無言で途絶える場合に備え、
非表示・非対話中も125ms間隔で同じ軽量なWin32可視確認を行う。スタート系
ウィンドウが可視の場合だけUI Automationスキャンを要求する。

## 5. 監視期間と負荷制御

`HybridStartMenuMonitor` は次の期間だけフォールバックTimerを動かす。
既定構成では、このTimerとWindowsキーの即時要求がWin32スキャンを起動する。
UIAイベントの登録待ち・再試行についての記述は、UIAオンの経路だけに適用する。

| 状態 | 間隔 | 目的 |
|---|---:|---|
| Windowsキー解放後 | 50ms、最大1500ms | イベント欠落を補い、スタート表示を確認する。 |
| スタート連動表示中・未操作 | 125ms | スタート終了イベントの欠落を補う。 |
| UI Automationイベント登録待ち | 125ms | Win32の軽量確認だけを行い、登録が外部プロバイダーで停止しても起動経路を維持する。 |
| ランチャー対話状態 | 停止 | 外側クリックやアプリ操作と監視を競合させない。 |
| 非表示アイドル | 125ms | Win32で可視候補がある場合だけスキャンする。フォーカス通知の無言停止を補う。 |

UI Automationのフォーカスイベント登録に失敗した場合は、1秒、2秒、5秒、10秒、
30秒の順で待って再試行し、それ以降も30秒間隔で再試行する。再試行中も125msの
Win32ゲート付きフォールバック監視を継続する。登録に成功するとイベント監視へ
自動復帰するが、無言停止を検出できないため非表示アイドル時の125msゲート付き
フォールバックは維持する。

初回の登録失敗は通常ログへ記録し、以降の連続失敗は詳細ログだけへ記録する。
復旧成功は通常ログへ試行回数とともに記録する。トラブルシューティング画面には
IsReadyに従い「スタートメニュー監視: 正常」または「スタートメニュー監視: 準備中」と表示する。Win32のみでも準備済みなら正常であり、WinEvent登録成功そのものを示す表示ではない。

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
Win32イベント通知（または非表示・非対話中の125msタイマー）
  → Win32でStart/Searchウィンドウの可視状態を確認
  → MTAワーカーへスキャン要求
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
| 低レベルキーボードフック | 登録スレッド。コールバックではキー分類と`DispatcherQueue`への登録だけを行い、短時間で復帰する。 |
| StartのWin32スキャン（UIAオン時はUIAも使用） | スキャン専用MTAスレッド |
| UI Automationフォーカスイベント登録 | 既定では停止。UIAオン時だけイベント専用MTAを開始する。 |
| 診断ログのファイル書き込み | バックグラウンドライター |
| Compositionアニメーション進行 | Compositor |

停止・スキャン要求・WaitHandle解放は同じロックで同期する。UIスレッドはネイティブ呼び出しの終了を待たず、最後のワーカー処理完了時に待機ハンドルを解放する。イベント解除が失敗した場合も新世代を重ねずWin32検出を継続する。UI予約通知は破棄フラグと通知元の世代を実行時に確認する。

SnapshotChangedは `HybridStartMenuMonitor` が `DispatcherQueue` へ戻してから `MainWindow` に通知する。UI AutomationワーカーからWinUI要素を直接操作してはいけない。

Windowsキー単体イベントも `MainWindow` が `DispatcherQueue` へ積み直してから処理する。低レベルキーボードフックのコールバック内で仮想デスクトップCOM、Start確認、状態遷移、`AppWindow`操作を実行してはいけない。入力同期呼び出し中にCOMを実行すると`RPC_E_CANTCALLOUT_ININPUTSYNCCALL (0x8001010D)`となり、ランチャーが別の仮想デスクトップに残る場合がある。

Windowsキーとスタートボタンからの表示はStartへフォーカスを残すため、`AppWindow.Show(false)`の後に`SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)`相当を適用する。`OverlappedPresenter.IsAlwaysOnTop`の初期設定だけに依存すると、非表示からの再表示後にZ順が復元されず、内部では表示済みでも他のウィンドウの背後に残る場合がある。

表示APIの戻りやComposition状態だけを実表示成功の根拠にしない。表示直後にHWNDの`IsWindowVisible`、`WS_EX_TOPMOST`、`DWMWA_CLOAKED`を確認し、不一致時は短時間の再試行を行う。再試行後も一致しない場合は`verify-presentation result=failed`を記録して非表示状態へ戻し、内部だけが表示済みの状態を残さない。非表示完了時も`IsWindowVisible`を確認する。

最前面状態の検証に失敗した場合、StartのVisible Snapshotを根拠にHide直後の再進入を繰り返してはいけない。StartがHiddenになったことを125msフォールバックで確認するまでStart連動表示を遮断する。`Ctrl+Alt+Space`またはトレイからの手動表示はこの遮断を解除できる。

休止状態／スリープ復帰の`WM_POWERBROADCAST`を受けた場合は、ExplorerとDWMの復帰を待つため1秒デバウンスした後、次をUIスレッドで行う。

1. Composition、アクティブ化、表示検証の途中状態を破棄し、実HWNDを非表示へ戻す。
2. Windowsキーの低レベルフックと`Ctrl+Alt+Space`を解除して再登録する。
3. 旧MTAワーカーへ停止を要求し、処理完了後に起動時に選択した同じモードで再生成する。待機中も125msのWin32検出でWindowsキー・スタートボタン経路を維持する。UIAオン時はUIAイベント解除の成功も待つ。
4. `[Recovery] action=resume`で各領域の成功／失敗を個別に記録する。

アプリプロセス、設定画面、トレイ、音声COMサービスはこの復帰処理では作り直さない。設定の未保存状態を壊さず、起動経路と表示状態だけを小さく回復させる。

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
2026-07-25に`ShowHideCompanion`のToggle状態、ボタン矩形、トップレベル
ウィンドウ矩形を使う自動検出を試したが、正確なパネル外周を得られず不採用とした。
観測値と不採用理由は
[スマートフォン連携パネル検出 実験記録](archive/PHONE_PANEL_DETECTION_EXPERIMENT_2026-07-25.md)
を参照する。

## 10. ログの読み方

ログ: `%LOCALAPPDATA%\Windows_SC\Logs\window-diagnostics.log`

### 正常なWindowsキー起動

```text
[InputMonitor] action=classify-windows-key result=success classification=standalone
[Motion] action=transition result=success from=Hidden to=AwaitingStartConfirmation
[UIAutomation] action=update-start-menu-state result=success state=visible-win32-window
[Launcher] action=confirm-start result=success key-to-start-ms=...
[Launcher] action=show-request ...
[Motion] action=complete result=success direction=Entrance ...
```

既定の起動ログは`[StartMenu] action=start-monitor result=success mode=win32-only`。
`visible-focused-element`やUIAイベント登録はUIAオンの経路だけであり、既定で出た場合は
保存済みUIA設定と再起動の有無を確認する。

### 正常なWindowsキー終了

```text
[Launcher] action=exit-request result=success reason=windows-key-close mode=immediate
[StartMenu] action=assume-hidden result=success source=windows-key-close
[Motion] action=transition result=success ... to=Exiting reason=windows-key-close
[Motion] action=complete result=success direction=Exit elapsed-ms=...
```

### 正常なスタートボタンクリック

`show-request` のreasonが `start-menu-click-detected` になり、`key-to-start-ms=-1.0` になる。キー入力がないため `-1.0` は異常ではない。

### 主要時間

| ログ値 | 意味 | 目安 |
|---|---|---:|
| `key-to-start-ms` | Windowsキー解放からスタート確認 | 通常10～100ms。コールド準備は分離する。 |
| `start-to-request-ms` | スタート確認から表示要求 | 50ms以内。 |
| `request-to-detect-ms` | スキャン要求からスタート検出（既定Win32、UIAオン時はUIA併用） | 検出元ごとに比較する。 |
| `scan-ms` | スタート検出に成功したスキャン | 50ms超が継続する場合は調査する。 |
| `request-to-complete-ms` | 表示要求から進入完了 | 約250ms＋数ms。 |
| Entrance `elapsed-ms` | Composition進入 | 約250ms。 |
| Exit `elapsed-ms` | Composition退出 | 約125ms。 |
| 検出 `action=scan slow=true` | 1回の確認が50ms超 | 継続発生する場合は回帰。 |

### 異常パターン

- `WindowsKey standalone` の後に `start-confirmation=expired`: OSがスタートを開かなかったか検出漏れ。Win32候補とSearchHost PIDを確認する。
- `action=scan slow=true` が1秒前後で連続: 子孫ツリー走査の再導入、COM停止、対象プロセス応答停止を疑う。
- `detect-start-menu source=idle-fallback`: 既定構成では正常なボタン起動経路。125ms確認による体感遅延と時刻を照合する。
- UIAオンの構成の`action=register-focus-event elapsed-ms`が大きい: 外部プロバイダーによる登録停止。既定構成ではこの登録を行わない。
- `WindowPlacement result=failed` の `start` がモニター作業領域と一致: モニター全面サーフェス除外の回帰。
- アクション成功直後に `Exiting → EnteringWithStart`: Snapshotリセットまたは反転ガードの回帰。
- Exitが200ms超: UIスレッド処理、設定画面生成、DispatcherQueue遅延を確認する。
- `compiled-bindings=initialized items=0`: 設定適用順または初回 `Bindings.Update()` の位置を確認する。

## 11. 過去の回帰事例

旧版の事例表は[履歴](archive/START_MENU_MAINTENANCE_HISTORY_2026-11-06.md)へ退避した。現行の防止条件は本書の各節と禁止事項を参照する。

## 12. 変更時の禁止事項

1. `TreeScope.Descendants` でStart/Searchツリー全体を同期走査しない。
2. 非表示アイドル中に50ms監視または無条件のUI Automationスキャンを常時動かさない。125msのWin32可視ゲート付き確認は維持する。
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

- [ ] 初回起動直後と監視準備後。UIAオン／オフは保存・再起動して個別に確認する。
- [ ] 100 / 125 / 150 / 200% DPI。
- [ ] 複数モニター、負の画面座標、各モニターでのWindowsキー／クリック。
- [ ] 60 / 120 / 144Hz。
- [ ] Explorer、SearchHost、StartMenuExperienceHostの再起動後。
- [ ] スリープ復帰後。
- [ ] Windowsのアニメーション無効時。

### ログ合格条件

- [ ] `start-to-request-ms <= 50` が通常時に維持される。
- [ ] Exitが通常約125ms。
- [ ] `action=scan slow=true` が連続しない。
- [ ] `start-confirmation=expired` が通常のスタート表示で発生しない。
- [ ] アクション成功直後に意図しない `Exiting → EnteringWithStart` がない。

## 14. OS更新時の確認順

1. プロセス名が `StartMenuExperienceHost` / `SearchHost` のままか確認する。
2. 可視時にトップレベルウィンドウが列挙できるか確認する。
3. 非表示時に `IsWindowVisible` または `DWMWA_CLOAKED` で除外できるか確認する。
4. 取得矩形がスタート本体か、全画面透明Hostではないか確認する。
5. 複数候補がある場合、前景ウィンドウとモニター、候補の可視・クローキング状態を詳細ログで確認する。UIA調査が必要ならUIAオンの構成を分ける。
6. 回帰試験チェックリストを実行する。

検出条件を追加する場合は、表示中と非表示中の両方で候補を記録し、誤検出しないことを確認してから採用する。

## 15. 既知の制約

- Windowsの非公開実装に観測上依存するため、OS更新でプロセス名やウィンドウ構造が変わる可能性がある。
- `SearchHost` はタスクバー検索などでも使われる可能性がある。現在は可視で十分な大きさのSearchHostをスタート系Surfaceとして扱うため、タスクバー検索を利用する環境では誤表示の有無を確認する。
- 下端タスクバー以外は初期対応外。
- スマートフォン連携パネルの個別矩形検出は無効で、設定による予約幅を使用する。
- スタートとランチャーは別プロセスであり、アニメーション進行率を共有していない。
- セキュリティ製品や高負荷状態でキーボードフックや検出が遅延する可能性がある。既定ではボタン検出に最大約125msの確認待ちがある。
- 既定ではUIAのStartButton補助を停止し、Win32のフォーカス／タスクバー補助を使用する。両方取得できず、複数のSearchHost候補がある場合、列挙順で別モニターを選ぶ可能性が残る。複数モニターは実機確認が必要。

不具合時は、まず入力、検出、表示要求、Compositionのどの区間が遅いかログで分ける。検出精度を上げる目的で重い全ツリー走査へ戻さず、Win32条件、対象イベント、有界フォールバックの順に検討する。
