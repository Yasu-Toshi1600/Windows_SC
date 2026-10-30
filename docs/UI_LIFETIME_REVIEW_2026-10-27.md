# UIの終了・解放処理の再調査

調査日: 2026-10-27

対象: `diagnostics/private-working-set-recorder` / `c8e331e26de9b2a26b01f19554c73811a2dfa113`

## 修正の進捗（同日追記）

以下の調査本文・行番号・再現出力は上記コミットの修正前の記録である。
その後の依頼に基づき、特別版だけで次を修正した。mainには反映していない。

- 要求受付・停止通知・WaitHandle解放を同じロックで同期。停止前に開始した要求も破棄後は無視する。
- ワーカーをMTAへ変更。Disposeは停止を要求し、最後のワーカー処理完了時に両WaitHandleを解放する。
- 復帰時は旧ワーカー処理とイベント解除成功を待つ。待機中はWin32検出を使い、旧世代を増やさない。
- UIへ予約した通知に破棄・世代確認を追加。停止後のタイマー再開を防ぐ。
- Compositionの完了・反転・中断時に所有資源を解放。終了時はSurface側の式アニメーションも停止する。
- 通常のウィンドウ終了要求も保存完了待ちへ統一し、Window.Closeより前にUI資源を解放する。
- 閉じた設定画面の非同期更新を抑止し、Picker・Dialogを終了する。明示保存自体は完了させる。
- 終了ログ名を`shutdown-resources-released`に訂正し、プロセス終了確認と区別する。

追試用コードは[分離検証の実行方法](CORE_TESTING.md#5-ui寿命管理の分離検証)を参照。
Coreテスト110件と分離検証3本が成功した。最終ビルド・プロセス切替の結果は末尾を参照。
長時間のメモリ改善、スリープ復帰やDialogを含むGUI回帰、ZIP・別PC・公開Releaseは未確認。

## 修正前の調査: 結論と確認範囲

UIの終了・解放は完全ではない。終了と通知の競合を2件、製品ソースをリンクした
小さな検証プログラムで再現した。UI Automationの世代交代とCompositionの所有資源にも
寿命管理の不足がある。ただし、今回の検証だけでは、長時間稼働時のメモリ増加や
10月27日に旧プロセスが残留した原因までは確定しない。

App、MainWindow、SettingsWindow、関連ViewModel、診断パネル、Composition、透明背景、
Start監視、入力フック、トレイ、UI通知元の音声サービス、UI更新調整クラスを対象に、
生成・購読・UI投入・解除・終了の経路を確認した。XAMLと生成済みバインディングの
追跡方法も確認した。音声COM内部の全面再調査は今回の範囲ではない。

製品コードは変更していない。起動中の修正版（PID 32920、10:43:10起動）は停止・再起動せず、
今回の調査でGUI操作、設定変更、通常ログ削除は行っていない。

## 1. スキャン要求とDisposeが競合し、破棄済みハンドルへアクセスする【再現済み】

対象: [UiAutomationStartMenuInspector.cs](../Windows_SC/UiAutomationStartMenuInspector.cs)
`RequestScan`（96–114行）、`Dispose`（140–158行）。優先度: 高。

次の順序が成立する。

1. UIAイベント側の`RequestScan`が`_isDisposed == false`を確認する。
2. UI側の`Dispose`がフラグを立て、スキャンワーカー終了後に`_scanRequested.Dispose()`を呼ぶ。
3. 先に通過していた`RequestScan`が`_scanRequested.Set()`を呼ぶ。

フラグ確認・Set・Disposeが同じ同期境界にない。`_scanRequestLock`も要求内容の更新だけを
保護しているため、`ObjectDisposedException`になる。復帰時の旧Inspector破棄でも起こり得る。
イベント元まで例外が伝播した場合の実アプリの挙動は未確認であり、終了残留の原因とは断定しない。

検証では製品の`RequestScan`と`Dispose`、実際のWaitHandleを使い、スキャンワーカーだけを
停止通知で終了するスレッドに差し替えた。要求ロックで実行順序を固定して例外を確認した。
デスクトップのUIA走査やネイティブイベント登録は実行していない。

修正時は、受付停止と通知ハンドル利用の同期、および処理中コールバックの寿命をそろえる。
単にイベント解除を先に呼ぶだけでは、既に実行中のコールバックを排除できない。

## 2. Dispose前に予約したUI通知が、Dispose後にタイマーを再起動する【再現済み】

対象: [HybridStartMenuMonitor.cs](../Windows_SC/Services/HybridStartMenuMonitor.cs)
`Dispose`（194–207行）、`UpdateFallbackMonitoring`（248–294行）、通知（309–337行）。
優先度: 中。

`ReadyChanged`などのハンドラーはUI Dispatcherへラムダを予約する。
Disposeでイベント購読を解除しても、既に予約されたラムダは残る。
ラムダにも`UpdateFallbackMonitoring`にも破棄済み判定がないため、
予約済み通知 → Dispose → 予約処理の実行、の順で停止済みタイマーが再起動する。
`ReadyChanged`の外部通知も破棄後に発生した。

製品の`HybridStartMenuMonitor`を変更せずリンクし、Dispatcher、Timer、Inspectorを
テスト用実装に置き換えて順序を制御した。Ready通知と、表示回復待ち中のSnapshot通知の
両方で再起動を確認した。Tick購読自体は解除されているため、これだけでUIA走査が
継続すると解釈してはいけない。実WinUIの終了停止・メモリ保持は未検証。

同じ通知処理はInspectorの世代も確認していない。復帰直前に旧Inspectorが予約した通知が、
新Inspectorの状態に対して実行される。修正時は破棄フラグと通知元の世代を実行時にも確認し、
停止後のタイマー再開を監視の入口でも防ぐ。

## 3. 復帰時に旧UIAワーカーの終了・イベント解除を確認せず次の世代を開始する【コード確認】

対象: [HybridStartMenuMonitor.cs](../Windows_SC/Services/HybridStartMenuMonitor.cs)
`RecoverAfterResume`（64–100行）、
[UiAutomationStartMenuInspector.cs](../Windows_SC/UiAutomationStartMenuInspector.cs)
コンストラクター（45–60行）、`Dispose`、`FocusEventLoop`（213–304行）。優先度: 高。

スキャンワーカーは最大1秒待つが、タイムアウトしても次のInspectorへ置き換わる。
イベント登録ワーカーは終了を待たず、解除が成功したかを呼び出し側へ返さない。
外部UIA呼び出しが止まったまま復帰を繰り返すと、旧ワーカーとその参照するInspector、
イベント登録、COM資源が残った状態で新ワーカーを作れる。残留世代数の上限もない。
新旧世代によるイベントの登録・解除が重なる可能性がある。

さらに、両ワーカーはSTAである。MicrosoftはUIAクライアントにウィンドウを持たないMTAを
推奨し、イベントの登録・解除を同一スレッドで行い、複数スレッドで同時に行わないよう
説明している。現行コードは一世代内の登録・解除は同一スレッドだが、世代間の直列化がない。
根拠: [UI Automationのスレッド要件](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)。

`_disposeRequested`は明示的にDisposeされない。`_scanRequested`も1秒でワーカーが
終わらなかった場合は後から明示解放する経路がない。到達不能になればGC側の回収を期待できるが、
停止したワーカーやイベントが参照を持つ場合は回収待ちも進まない。
同期検証でも、Dispose後の`_disposeRequested.SafeWaitHandle.IsClosed`は`false`だった。

これはネイティブメモリ増加を調べる上で優先度の高い候補。ただし、今回稼働中の実プロセスで
旧ワーカーが増えていることは確認していない。Backgroundスレッドが残るという事実だけでも、
プロセス終了不能の証明にはならない。

修正時は、UIを無期限に待たせず、停止中世代の完了状態を管理し、登録・解除を直列化する。
停止が遅い場合もWin32によるスタート検出経路を維持し、世代を無制限に追加しない。
待機ハンドルの解放は、利用するワーカーとコールバックの完了後に行う。

## 4. Compositionの停止・所有資源の解放が一部明示されていない【コード確認、リーク未確定】

対象: [CompositionLauncherMotionService.cs](../Windows_SC/Services/CompositionLauncherMotionService.cs)
`Attach`（51–73行）、`Dispose`（110–125行）、`StartTransition`（128–192行）、
`CompleteTransition`（194–212行）。優先度: 中。

- Surfaceに接続したTranslation／OpacityのExpressionAnimationをフィールドへ保持しておらず、
  終了時に`UIElement.StopAnimation`で切り離していない。
- 解放しているPropertySet側のアニメーションと、Surface側のExpressionAnimationは別物。
  前者を止めても、後者を明示的に停止したことにはならない。
- 開閉ごとに作るKeyFrameAnimation 2個とEasingFunctionは明示Disposeせず、参照回収に任せている。
- Batchと匿名Completedハンドラーは完了時に解放せず、次回遷移またはサービスDisposeまで残る。

Batchフィールドは1個で、ExpressionAnimationの生成も通常は初期化時の2個だけ。
これらを「開閉のたびに必ず永久リークする」とは判断しない。
アニメーションの一時オブジェクトとネイティブ資源がいつ回収されるかを、開閉反復の計測で
調べる必要がある。自分で所有するオブジェクトの完了・中断・終了時の解放をそろえ、
XAMLから借りたCompositorは破棄しない。

API根拠: [UIElement.StopAnimation](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.stopanimation?view=windows-app-sdk-1.8)、
[CompositionObject.Close](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionobject.close?view=windows-app-sdk-1.8)。

## 5. 終了経路と非同期UI処理に残る確認事項

- [App.xaml.cs](../Windows_SC/App.xaml.cs)の`RequestShutdown`（328–342行）は設定画面を閉じ、
  保存完了を待ってからメイン画面を閉じる。一方、メイン画面へ直接WM_CLOSEが来る経路の
  `Window_Closed`（179–224行）には同じ保存待機や設定画面の先行クローズがない。
  保存中に直接閉じる場合の順序が統一されておらず、サービス解放が保存後のUI反映に先行し得る。
  実GUIでの再現・WinUIが残る画面を閉じる順序は未確認。
- `SettingsViewModel.Dispose`は主要イベントを解除するが、予約済みの音声候補更新
  （622–623行）と`RefreshApplicationsAsync`／`RefreshAudioDevicesAsync`のawait後には
  破棄済み判定がない。閉じた画面のVMを更新する余地があり、音声処理が完了しない間は
  Task側から参照が残る。保存処理は明示保存を完了させる必要があるため、UI更新の抑止と
  保存自体のキャンセルは区別する。
- MainWindowのWindowsキー・音声・アニメーション設定通知にも、予約後の終了を確認しない
  UIコールバックがある。SettingsWindowのPicker／ContentDialog、MainWindowのエラーDialogにも
  ウィンドウ終了時の明示キャンセルはない。画面を閉じた状態での実GUI検証が必要。
- `shutdown-complete`（App 219行）はLogger.DisposeとApplication.Exitより前に記録される。
  LoggerのJoinは最大2秒だが、後段のWinUI終了を含めて成功した証拠にはならない。
  解放ヘルパーの`phase=end`もDisposeが戻った証拠であり、バックグラウンド解放完了とは限らない。
  次回残留時は強制終了前に停止位置のスタックを取得する必要がある。

## 解放処理が存在し、今回明確な蓄積経路を確認しなかった箇所

- MainWindowの5本のDispatcherQueueTimer: 終了時にStopとTick解除がある。
- MainWindowの外部サービス購読、UISettings購読、Shortcut実行準備の接続: 解除経路がある。
- SettingsViewModel: 入力・音量・診断・編集コレクション購読を解除する。
- 透明背景: SystemBackdropをnullにし、切断時に自分で生成したBrushとCompositorをDisposeする。
- トレイメニュー: DestroyMenuをfinallyで実行。所有アイコンも終了時にDestroyIconする。
- DWM補助: 作成したリージョンとDCをfinallyで解放。仮想デスクトップCOMにもfinallyで解放がある。
- システムモニターUI: 非表示時の投入抑止、表示中1件への集約、破棄後の通知抑止、履歴60点上限がある。
- Slider補助状態: ConditionalWeakTableなので、Sliderを保持し続ける通常の辞書ではない。

キーボードフック解除、ホットキー解除、WndProc復元、トレイ削除のAPI呼び出しは存在するが、
一部は戻り値を確認していない。呼び出したこととOS側で解除に成功したことは区別する。
また、Window自身のイベントや画面内のイベントに解除がないことだけでは、循環参照や
恒久リークを断定しない。画面外の生きた所有者から保持される経路があるかが重要。

## 検証記録と次の順序

ローカル検証プログラムはgitignore対象の`artifacts`へ保存した。製品ソースをコピーして
修正するのではなくCompile Includeでリンクしている。

```powershell
dotnet run --project artifacts\ui-lifecycle-review\UiLifecycleReview.csproj -c Release
dotnet run --project artifacts\uia-lifecycle-review\UiAutomationLifecycleReview.csproj -c Release
```

実測出力:

```text
queued-ready-after-dispose: timer-running=True, tick-subscribers=0, ready-notifications=1
queued-snapshot-after-dispose: timer-running=True
after-dispose: scan-handle-closed=True, exit-handle-closed=False
in-flight-request-after-dispose: exception=ObjectDisposedException
```

両方とも終了コード0。これは既存不具合の再現成功であり、修正完了を示すテストではない。
実Dispatcher／UIAプロバイダー／Compositionのメモリ解放やプロセス終了を検証したものでもない。

| 区分 | 今回の結果 |
|---|---|
| 製品コード変更 | なし。調査記録と残作業へのリンクのみ追加 |
| 検証プログラム | 上記2本のビルド・実行成功。終了競合2件を再現 |
| 製品Debug／Release・既存テスト | 今回は再実行なし。製品コード未変更 |
| GUI手動確認・長時間改善 | 未確認 |
| ZIP確認・別PC確認・公開済みRelease確認 | 今回は未実施 |

次の修正は、まず1・2の通知と破棄の競合、次に3のUIA世代管理を独立した範囲で扱う。
その後4のCompositionを別に修正・比較する。トレイ／設定からの終了だけでなく、
直接クローズ、保存中、Dialog中、復帰を繰り返した後の終了について、
プロセス終了とワーカー・ハンドル・メモリの推移を確認する。

## 修正後の確認結果（2026-10-27 12:29）

| 区分 | 結果 |
|---|---|
| コード変更 | 特別版のみ。mainは変更なし。10/27の検証時点では未コミット・未push |
| ビルド | 最終Debug／Release x64とも警告0・エラー0 |
| 既存自動テスト | Debug x64の110件が成功 |
| 分離検証 | Monitor、Workers、Motionの3本が成功。上述の実行方法を参照 |
| プロセス切替 | 旧PID 32920は通常の終了要求で終了。強制終了なし |
| 修正版の終了試験 | PID 3424へ通常終了要求を送り、コマンド発行から終了確認まで603ms。手動GUI操作ではない |
| 終了時のログ | `source=window-close` → `wait-before-shutdown` → motion／backdropの解放完了 → `stop-workers result=success event-cleanup=complete` → `shutdown-resources-released`を確認 |
| 最終起動 | 12:28:42起動、PID 17716。修正版の出力先・DLLハッシュ・プロセス数1を確認 |
| 継続計測 | `WINDOWS_SC_MEMORY_FLIGHT=1`で起動。新PIDの1秒CSV記録を確認。WPRは開始していない |
| GUI手動確認 | 未確認。トレイ／設定からの操作、保存中、Dialog中、スリープ復帰、スタート両経路の回帰は残る |
| 長時間の改善 | 未確認。今回の修正で原因確定・リーク解消とはしない |
| ZIP確認 | 今回未実施・未生成 |
| 別PC確認 | 未実施 |
| 公開済みRelease確認 | 未実施・公開物の変更なし |

最初の修正版起動ではUIAの起動時スキャンにCOMException（0x80004005）が1回あり、
その後監視準備完了となった。最後の起動では起動時スキャンと監視準備完了の双方が成功した。
初回エラーの原因は未確定であり、今後も通常ログで追う。

Releaseは起動中の旧ファイルへの上書きを避け、次の追加プロパティで出力先を分けた。
`-p:BaseOutputPath=<worktree>/artifacts/ui-lifetime-build/`
実行ファイルはその下の`x64/Release/net8.0-windows10.0.19041.0/win-x64/Windows_SC.exe`。
DLLのSHA256は`C5C3DE01BE7DC0F4A79016C3DFCAB04FAAA4D397DD213B150CD6739C977D0F9A`。

切替前の通常ログ・MemoryFlight CSV、および新ビルドの終了ログは
ローカルの`artifacts/ui-lifetime-restart-20261027-122157`へ保存した。
