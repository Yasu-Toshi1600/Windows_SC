# 開閉に伴うメモリ増加の類似事例調査

調査日: 2026-10-30。対象: `diagnostics/private-working-set-recorder`。
Microsoftの公開Issue、修正PR、リリースノートとローカル実装を照合した。
製品コード・依存パッケージの変更、再起動、外部の再現プログラムの実行は行っていない。

## 現在の観測と環境

- .NET 8、`Microsoft.WindowsAppSDK.WinUI` **1.8.260224000**。自己完結型の設定。
- ランチャーは同じMainWindowをShow/Hideする。設定画面は閉じた後、次回に新規作成する。
- 修正版PID 17716で、10/30 09:13の8回の表示前後にprivate WSが149.7→151.7 MiB、
  private commitが228.9→231.0 MiBとなった。GC committedは17.3 MiBのまま。
- 09:14の設定画面2回の開閉を含む区間ではprivate WSが151.6→194.4 MiBとなり、
  閉じて約1分後も194.4 MiBだった。これは相関であり、保持元やリークを確定する記録ではない。
- 記録は毎秒。高速な開閉をすべてCSVだけで数えることはできない。
  上記8回は通常ログのHidden→Enteringの遷移で確認した。
- 観測のコピー: `artifacts/open-close-observation-20261030-091753/`。

## 類似事例と今回への適用

### 1. UI Automation使用時の参照残留

[dotnet/wpf #11337](https://github.com/dotnet/wpf/issues/11337)（2025-12-23報告、調査時Open）。
UI Automationの反復走査でWPF側のElementProxyのCOM参照が残り、
UI要素が保持される再現報告。報告にはクライアント側も増えるとの記載がある。
対象として.NET Framework 4.8から.NET 10が挙げられている。

Windows_SCも`System.Windows.Automation`でフォーカス要素と祖先を調べるため、
UIAクライアント処理を分離する根拠になる。ただし、報告の対象UIはWPFであり、
Windows_SCのUIはWinUI、監視先はStartである。再現コードの全子孫走査も現行実装とは異なる。
このIssueだけで同一不具合とは判断できない。

参考として、[Windows Terminal #19710](https://github.com/microsoft/terminal/issues/19710)にも
スクリーンリーダー使用中にXAML側がUIAテキスト範囲を保持する調査がある。
こちらは提供側のTextRange固有の経路であり、Windows_SCのフォーカス取得とは一致しない。

### 2. Windowのイベント購読による終了後の保持

[WinUI #9960](https://github.com/microsoft/microsoft-ui-xaml/issues/9960)と
[#7282](https://github.com/microsoft/microsoft-ui-xaml/issues/7282)。
Windowに対するイベント購読や、Window直下のx:Bindから生成されるActivated購読により、
閉じたWindowが参照され続ける事例。
[修正PR #11734](https://github.com/microsoft/microsoft-ui-xaml/pull/11734/files)は2026-09-04にmainへマージ済み。
修正はネイティブのWindow終了処理でイベント登録をまとめて解放するもの。
PRのマージと、利用中パッケージへの収録は別である。

ローカルのRelease生成コードには、MainWindowの`Activated += bindings.Activated`と
空の`StopTracking()`がある。ただしMainWindowは1個を再表示するため、
これだけでは開閉回数に比例する増加を説明できない。
SettingsWindowのx:BindはDataTemplate内で、生成コードには同じWindow.Activated購読がない。
また、設定画面自身とAppのClosed購読は、前回の修正で明示解除している。
したがって、設定画面の増加も、この既知不具合と即断せず残留インスタンスを確認する必要がある。

### 3. 複数Windowを閉じても使用量が下がらない報告

[WinUI #9063](https://github.com/microsoft/microsoft-ui-xaml/issues/9063)（2023-11-09報告、調査時Open）。
Windows App SDK 1.4.2のC++・unpackagedアプリで、新しいWindowを作るごとに増え、
閉じても減らないとの報告。別の検証者はパッケージ更新後、50個の反復開閉で増加が1 MB未満になり、
XAML Hot Reload有効時は計測値が大きく見えるとも報告している。

新規作成する設定画面とは操作が近い。ただし古い版・デバッガ条件の違いがあり、
現在のRelease単独起動で同じ原因が起きている証拠にはならない。

### 4. 似ているが、現行コードへ直接当てはまらない例

- [WinUI #11239](https://github.com/microsoft/microsoft-ui-xaml/issues/11239):
  ConnectedAnimationを繰り返すと増える報告。現行はCompositionのKeyFrameAnimationを使用し、
  ConnectedAnimationは使用しないため、同じ不具合として扱わない。
- [dotnet/runtime #129191](https://github.com/dotnet/runtime/issues/129191):
  .NET 10のCOM wrapper関連の増加報告。現行の対象フレームワークは.NET 8である。
- [Windows App SDK 1.8リリースノート](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8):
  1.8.9にはItemsRepeaterのRecyclePool参照循環の修正がある。
  アプリのXAMLでItemsRepeaterを直接使っていないため、更新だけで今回が直るとは判断しない。

## 次の切り分け

1. 設定画面を開かず、同じ条件・回数でランチャーの表示と非表示を繰り返す。
   UIA監視、モーション、表示時の音声デバイス再取得を、それぞれ個別に外した診断構成と比較する。
   UIAを外す比較でもWindowsキーとStartボタンのWin32検出経路は維持する。
2. 設定画面だけを反復作成・終了し、閉じたSettingsWindowとViewModelが弱参照で残るか調べる。
   残る場合は保持元、残らない場合はネイティブ側の確保元を追う。
3. パッケージ更新を試す場合も、コード修正と同時に混ぜず、同じ再現操作で比較する。

長時間放置の追加だけでは開閉由来の処理を特定できない。
公開事例から調査対象は絞れるが、現時点で原因となる既知不具合は特定できていない。
