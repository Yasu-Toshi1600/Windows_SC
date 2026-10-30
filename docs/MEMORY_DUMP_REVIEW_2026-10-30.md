# メモリダンプによる残留オブジェクトの確認

調査日: 2026-10-30。対象: 特別版PID 17716（10/27 12:28:42起動）。
製品コード、設定、依存パッケージの変更と再起動は行っていない。

## 確認結果

**閉じたSettingsWindow 3個とDispose済みSettingsViewModel 3個が残り、
WinRTイベントの解放待ちから参照されていた。**
約2分18秒離した2回のダンプで同じ3個が残っていた。
GCが解放処理の対象として認識した後のファイナライズ待ちが増えているため、
解放処理の滞留が有力な調査対象となった。
ただし、ファイナライザースレッドを待たせているネイティブ関数、
それが待つ相手、全メモリ増加への寄与量までは特定していない。

| 項目 | 1回目 | 2回目 |
|---|---:|---:|
| ダンプファイルの保存完了時刻（JST） | 09:27:22 | 09:29:40 |
| SettingsWindow | 3 | 3 |
| SettingsViewModel | 3 | 3 |
| Ready for finalization | 15,169 | 15,485 |
| GC Allocated Heap Size | 14,382,960 bytes | 11,658,960 bytes |
| GC Committed Heap Size | 18,481,152 bytes | 18,481,152 bytes |
| ダンプファイルサイズ | 318,182,943 bytes | 318,854,797 bytes |

ダンプ全体のファイルサイズはアプリの使用量と同じ指標ではない。
`eeheap -gc`の値はダンプ時点のGC構成であり、CSVの`GC.GetGCMemoryInfo()`による
直近GC時点の値と必ずしも一致しない。

## 終了済み状態と参照経路

2回目の`dumpobj`で、3個すべてについて次を確認した。

- SettingsWindowの`_isClosed = 1`。
- SettingsViewModelの`_isDisposed = 1`。

`dumpheap -live`でも残存し、`gcroot`に次の経路が出た。

```text
Finalizer Queue
  -> WinRT._EventSource_...RoutedEventHandler+EventState
  -> Microsoft.UI.Xaml.RoutedEventHandler
  -> Windows_SC.SettingsWindow
```

設定画面自身が終了処理を実行していない、という説明では合わない。
WinRTイベントの後始末が完了せず、画面への参照も残る状態を確認した。
これはイベントの永久的な購読リークと同義ではなく、
ファイナライザーが進めば解除される資源の滞留も含む。
2回のスナップショットだけで、全期間にわたるデッドロックとは断定しない。

ファイナライザーはOS thread ID `0x5914`。
両ダンプの管理スタックでは`DebuggerU2MCatchHandlerFrame`だけが表示される。
この情報だけではネイティブの待機先を特定できない。
`dotnet-dump`はネイティブデバッガーではないため、次はWinDbg等でこのスレッドの
ネイティブスタックを調べる必要がある。

## 実施した解析

Microsoft製`dotnet-dump` 10.0.750501を、この作業ツリーの
`artifacts/diagnostics-tools/`へローカルインストールした。
NuGet接続と対象プロセスのメモリ読み取りはサンドボックス内では失敗し、
許可されたサンドボックス外実行で取得した。OS全体へのツール導入は行っていない。

`collect --type Heap`を2回実行し、ダンプをオフラインで解析した。

```text
eeheap -gc
dumpheap -stat
dumpheap -live -type Windows_SC.SettingsWindow
dumpheap -live -type Windows_SC.ViewModels.SettingsViewModel
gcroot <対象オブジェクトのアドレス>
dumpobj <対象オブジェクトのアドレス>
finalizequeue -stat
clrthreads
clrstack -all
```

強制GC、ワーキングセット縮小、デバッガーからの処理実行は行っていない。
総オブジェクト数には未回収のゴミも含まれるため、単純な個数だけでリークを判断せず、
`-live`、参照元、終了済みフラグ、時間を空けた再取得を組み合わせた。

採取後、同じPIDが応答中であることと、Memory Flightの毎秒記録継続を確認した。
ダンプ取得の前後ではprivate WSが約203→274 MiBへ上がった一方、
private commitは約286→287 MiBだった。
メモリを読み取る診断操作は常駐状態へ影響し得るため、この区間を
通常の開閉による増加として比較しない。

## 証跡と次の調査

ローカル証跡: `artifacts/memory-contents-20261030/`。

- `snapshot-0927.dmp` / `snapshot-second.dmp`
- `managed-summary.txt` / `threads-and-live-ui.txt`
- `settings-roots.txt` / `second-summary.txt`
- `closed-state-and-finalizers.txt`

ダンプはメモリ内容を含むローカル解析資料として保持し、リポジトリへ追加しない。

次に優先するのは、ファイナライザーのネイティブスタックとCOM解放先の確認。
現在のダンプから停止位置を追い、必要に応じてヒープ確保スタックのトレースを追加する。
既存MemoryFlight.wprpはVirtualAllocのスタックを採る構成で、
個々のネイティブヒープ確保をすべて記録する構成ではない。
過去に確保したネイティブメモリの呼び出し履歴が、ダンプだけで必ず復元できるわけではない。

資料: [dotnet-dumpの機能と制約](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump)、
[解放待ちとファイナライザースレッドの調査](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/performance)。

この作業は診断と文書化のみ。製品ビルド・自動テスト・GUI手動操作・配布確認は実施していない。
