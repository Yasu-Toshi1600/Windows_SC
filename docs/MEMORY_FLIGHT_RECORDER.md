# プライベートワーキングセットの継続計測

## 目的と起動

RAM常駐量の急変と、コミット・GC・システムの空きRAM・画面状態を同じ時系列で比較するための調査機能。
通常は無効で、起動時の環境変数 `WINDOWS_SC_MEMORY_FLIGHT=1` の場合だけ有効になる。
設定ファイルへの保存や管理者権限は不要。Visual Studioを閉じても、アプリ自体が動いていれば記録は続く。

1. 起動中のWindows_SCをトレイ等から終了する。単一起動制御があるため、既存プロセスへ後から有効化はできない。
2. Visual Studioでx64を選び、起動プロファイルを **Windows_SC (Memory Flight)** にする。
3. 通常利用に近い測定はRelease・Ctrl+F5で起動する。ヒープスナップショットも調べる場合はF5を使う。
4. `%LOCALAPPDATA%\Windows_SC\Logs\MemoryFlight\samples.csv` が毎秒更新されることを確認する。
5. 停止はアプリの終了。次回、通常の起動プロファイルを使うと無効に戻る。

VS以外では、起動するシェルに環境変数を設定して、このブランチをビルドしたEXEを起動する。
既存の配布ZIPにはこの計測機能は含まれない。診断のために既存ZIPを同じ版で差し替えない。

## 記録と保持上限

| ファイル | 内容 |
|---|---|
| `samples.csv` | 約1秒ごとの全サンプル |
| `events.csv` | 急変前の最大30秒、急変サンプル、急変後の約30秒。`event_id`と`phase`で区別 |
| `trigger.txt` | 最後の急変通知。PID、イベント番号、UTC時刻。WPR連携用 |

CSVは各8 MiB、現行＋`.1.csv`～`.3.csv`の4本ずつ、合計最大64 MiB。
実行中にローテーションし、有効状態で再起動したときも現行ファイルを世代移動する。
古い記録は上書きされるため、調査対象の急変を見つけたらファイルをコピーして保管する。
イベントがファイル境界をまたぐ場合は世代ファイルも併せて読む。`trigger.txt`は前回起動分が残る場合がある。

プライベートワーキングセットが、前回または最大30秒前との比較で次のどちらかを満たすと急変とする。

- 増減の絶対値が16 MiB以上。
- 増減の絶対値が4 MiB以上、かつ比較元の20%以上。

増加・減少の両方が対象。再検出は60秒間抑制するが、通常サンプルは継続する。
採取間隔が5秒を超えた場合、時刻が巻き戻った場合、値が欠測した場合は比較履歴をリセットする。
スリープをまたぐ差分を「1秒での急変」と扱わない。タイマー遅延、終了、書込み失敗により前後30秒が揃わないこともある。

## 指標の読み方

- `utc`、`pid`、`sequence`、`elapsed_ms`、`interval_ms`: UTC時刻、PID、連番、計測開始からの経過時間、実際の採取間隔。
- `private_ws_bytes`: プライベートワーキングセット。共有分を含む`working_set_bytes`と区別する。
- `private_commit_bytes`: プライベートコミット。ページファイルの実使用量ではない。
- `page_faults_total` / `page_faults_delta`: 累積ページフォルトと前回との差。32bitの折返しを処理する。ソフト／ハードの合計であり、ディスク読み込み回数とは限らない。
- `gen0` / `gen1` / `gen2`、`managed_bytes`、`gc_committed_bytes`、`allocated_total_bytes`: GC回数、強制GCなしの管理メモリ推定量、直近GCのコミット量、累積割当量。
- `system_available_bytes` / `system_physical_bytes` / `system_commit_bytes` / `system_commit_limit_bytes`: PC全体の利用可能な物理メモリ、物理メモリ総量、コミット量、コミット上限。
- `cpu_total_ms`、`handles`: 自プロセスの累積CPU時間とハンドル数。
- `launcher_visible` / `launcher_cloaked` / `launcher_foreground`: 実HWNDから取得した表示・cloaking・前面状態。1/0、取得不能は空欄。設定画面の前面状態とは異なる。
- `settings_open`、`last_marker`、`marker_elapsed_ms`: 設定画面の状態、最後の起動／設定開閉等の通知とその時刻。毎秒時点の最新状態であり、1秒以内のすべての操作を記録するものではない。
- `sample_us`: 主にネイティブ情報の採取時間。CSV文字列生成とファイル書込み時間は含まない。

空欄は欠測で、0ではない。EX2対応OSが必要（Windows 10/11 22H2の2023年9月累積更新以降）。
古いAPIがEX部分だけ返す場合、private WSは空欄。API失敗や書込み失敗時は通常ログの`[MemoryFlight] result=failed`に型とHRESULTを記録し、追加計測を停止する。
定期採取はUIスレッドで行わず、実行中のタイマー処理と重なった呼び出しはスキップする。

### 判断できること・できないこと

private WSだけ減り、コミット量が維持されていれば、確保の解放よりも常駐ページの減少と整合する。
GC回数と管理ヒープが同時に変わる場合はGCとの対応を調べる。PCの空きRAMや表示状態も前後で比較する。
これらは相関の記録であり、OSがトリミングした理由、ページファイルへの退避量、リークを単独で断定するものではない。
private commitからWSを引いてページファイル使用量を計算しない。

## Windows側のトレースも自動保存する

急変に備えて先に起動する補助スクリプト。CSVだけなら管理者権限不要だが、WPRには管理者PowerShellが必要。
アプリのPIDはタスクマネージャーまたは`Get-Process Windows_SC`で確認する。

```powershell
.\scripts\Watch-MemoryFlight.ps1 -TargetProcessId 12345
```

- 専用の一意なWPRインスタンスで、ページフォルト、VirtualAlloc、プロセスのメモリ情報、.NET GCをメモリ循環記録する。
- バッファ指定はカーネル32 MiB＋イベント8 MiB。実際のOS負荷や保存時の追加処理は別途発生する。
- 新しい対象PIDの急変通知を検出すると、既定で15秒後に停止・ETL保存する。
- 既定の待機上限は1800秒。`-WatchSeconds`と`-AfterSeconds`で変更できる。タイムアウト・対象終了・Ctrl+Cでも、開始済みの自分の記録を保存して停止する。
- 保存先は`artifacts/memory-flight/<実行ごとのフォルダー>`。ETL、WPRの欠落イベント等の状態、CSVのコピーを残す。
- ETLは1回につき1本。繰り返し実行した成果物は自動削除しない。CSVコピーはETL停止後時点で、アプリ側のイベント窓より早く保存すると後半が未完になる場合がある。
- 循環バッファに残る時間はイベント量次第で、急変前30秒のETW保持は保証しない。CSVの時間幅とは別。
- `wpr -cancel`を他のセッションに対して実行しない。停止失敗時は表示される固有インスタンス名で状態を確認する。
- PowerShell自体を強制終了するとfinallyが実行されない場合がある。その場合は成果物の`status.txt`に記録されたインスタンス名で手動停止する。

WPAで対象PIDとCSVのUTC時刻を合わせ、ページフォルトの種類・VirtualAlloc/Free・GC・メモリ推移を見る。
ETLはシステム全体の情報やパス等を含み得るため、CSVと区別して扱う。外部へ自動送信しない。
このプロファイルはReferenceSetやワーキングセットのリセットを使わない。強制GC、ヒープダンプ採取も行わない。

## 実装の保守と確認

アプリの`ProcessMemoryDiagnostics`が計測の開始・設定通知・終了を管理する。
通常の1分診断とは別系統で、毎秒の経路では`Process.GetProcesses*`、PDH、UI Automationを使用しない。
CSV生成・書込み自身の割当やI/Oは測定値に含まれる。負荷がゼロとは扱わない。
UIのスタート検出・表示・音声制御のロジックは変更しない。

自動テストは急増／急減、30秒の累積差分、クールダウン、欠測・スリープ、長時間の履歴上限、CSVのローテーションを確認する。
実機では無効時の停止状態、有効時の記録、設定開閉、HWNDの表示・cloaked状態、アプリ終了後の書込み停止、WPR終了を確認する。
長時間の自然な変動、GUI、VS起動プロファイル、WPAでの原因分析はそれぞれ別の確認項目。

### このブランチでの検証（2026-10-14）

- Debug／Release x64ビルド成功、警告・エラー0。通常Debug出力は起動中アプリとVSによるロックがあったため、`artifacts/memory-flight-build`へ出力して確認した。
- 既存を含む自動テスト100件成功。
- 計測コード単体を別プロセスで実行し、Windows APIのprivate WS取得、無効時の未起動、約64 MiBの増加検出、41回のサンプル、急変前／検出時／後の記録、Dispose後の書込み停止を確認。
- WPRは専用テスト通知で自動停止・ETL保存に成功。カーネル／CLR両コレクターの欠落イベント0、停止コード0、専用セッションの終了を確認。
- 実アプリのGUI、VSの起動プロファイル選択、長時間の自然な急変、WPRのCtrl+C／時間切れ経路、ZIP・別PC・公開Releaseは未確認。

参考: [PROCESS_MEMORY_COUNTERS_EX2](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2)、
[WPRコマンド](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options)。
