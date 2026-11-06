# Windows_SC.Core 自動テスト方針

更新日: 2026-11-06
対象: `Windows_SC.Core`、`Windows_SC.Tests`

## 1. 目的

`Windows_SC.Core`へ移したUI非依存コードは、WinUI、Windows App SDK、COM、レジストリ、
実ウィンドウを生成せずに自動確認する。`Windows_SC.Tests`は`net8.0`を対象とし、
製品ソースのリンクではなくCore成果物を`ProjectReference`で参照する。

```powershell
dotnet test Windows_SC.Tests/Windows_SC.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet test Windows_SC.Tests/Windows_SC.Tests.csproj -c Release -p:Platform=x64 --no-restore
```

## 2. 自動確認する領域

| 領域 | 主な確認内容 |
|---|---|
| 設定モデル | 既定設定、ID、登録順、旧音声切り替え形式との互換性 |
| スキーマ移行 | v1／版番号なしからv2への移行、将来版の拒否 |
| 設定検証 | ページ・項目ID、操作種類、マクロ、音量範囲、アプリ対象、循環設定、Widget選択数 |
| 設定保存調整 | 編集中保存のリビジョン、保存の直列化、保存失敗後の継続、終了時のidle待機 |
| ShortcutKey | 検証、表示、ScanCode／VirtualKey入力列、記録対象と抑止解除 |
| マクロ | 登録順、失敗時停止、同時実行拒否、破棄後の実行拒否 |
| 非同期状態 | 最新値優先、逐次キュー、例外通知失敗後の継続、idle完了 |
| 音声状態 | デバイスID正規化、次候補と折返し、出力先別アプリ音量、範囲補正、破損状態隔離 |
| システムモニター | 履歴上限、0～100への補正、欠損値、グラフ座標 |
| システム計測とUI反映 | 選択項目のキャッシュ・履歴、設定変更中の旧結果破棄、非表示中のUI投入抑止、通知集約、再表示と終了後の抑止 |
| ログ値 | 改行等の除去、ユーザー名・既知パス・パス末尾の匿名化 |

Coreの振る舞いを変更するときは、正常系だけでなく境界値、取消し、例外、重複、
大小文字差、破損入力のいずれかを再現するテストを同じ変更へ含める。
入力条件は実際の入口に合わせる。状態ファイルの破損はファイルを編集してから読み込み、
内部の正常系APIへ到達しない値を渡して代用しない。単純なJSON往復や結果生成だけの確認、
未使用のキャンセル経路、内部APIのnullガードだけの確認は増やさない。
デバイス表示名が同一の場合の編集UI動作はCoreのDictionary往復では確認できないため、GUI確認とする。

## 3. Core単体では確認しない領域

次はWindows固有実装またはGUI手動確認として扱う。

- WinUI XAMLの表示、Binding／`x:Bind`警告、DPI、スクロール、フォーカス、ContentDialog
- Windowsキー、スタートボタン、UI Automation、仮想デスクトップ、light-dismiss
- Compositionモーション、実ウィンドウ位置、複数モニター
- Core Audio COMの列挙・通知・解放、実デバイス切り替え、外部ミキサー追従
- `SendInput`による実アプリへのShortcutKey送信
- レジストリ自動起動、トレイ、単一インスタンス、Picker、実ファイル起動
- 通常ZIPの生成、ローカル起動、別PC、公開Release

これらは`TEST_CHECKLIST.md`と`PHASE5_6_TEST_PROCEDURE.md`を正とし、
ビルドやCoreテスト成功だけで動作確認済みとはしない。

## 4. 検証結果

件数と直近結果は[現在の状況](NEXT_STEPS_AND_DECISIONS.md)に集約する。

## 5. UI寿命管理の分離検証

`scripts/UiLifecycleChecks`はCoreテストと別のコンソール検証で、製品ソースをリンクし、
外部UI／ネイティブ依存を代替して順序と所有資源を確認する。WorkersはWindowsで実行する。

```powershell
dotnet run --project scripts/UiLifecycleChecks/Monitor/Monitor.csproj -c Release
dotnet run --project scripts/UiLifecycleChecks/Workers/Workers.csproj -c Release
dotnet run --project scripts/UiLifecycleChecks/Motion/Motion.csproj -c Release
```

Monitorは破棄後・旧世代の通知、停止未完了時の復帰100回、Win32代替検出を確認する。
Workersは実WaitHandleと要求・破棄・ワーカー終了処理で競合100回と遅延解放を確認する。
既定のWin32経路では、実際のStart／ワーカーを実行し、UIAイベント用スレッドが開始されず、
Windowsキー・アイドル検出・非表示化・停止がWin32代替実装で処理されることも確認する。
Motionは完了・反転500回、中断・無効化・終了時の所有資源解放を確認する。
これは実UIAプロバイダー、ネイティブComposition、GUIや長時間メモリ改善の確認ではない。

Win32イベントの検証:

```powershell
dotnet run --project scripts/UiLifecycleChecks/WinEvents/WinEvents.csproj -c Release
dotnet run --project scripts/UiLifecycleChecks/WinEvents/WinEvents.csproj -c Release -- --native-smoke
```

通常実行は製品のイベント管理コードをリンクし、ネイティブAPIを代替して、フィルター、PID更新、
部分登録失敗、解除失敗、旧コールバック抑止、登録スレッド制約を確認する。
native-smokeは実Win32で7本のフックを登録し、2秒のメッセージ処理後に同一スレッドで解除する。
通知数0でも登録・解除の確認は成功となるため、Start操作のGUI受信試験の代替にはならない。
Monitor検証には通知100件の集約と対話中・復帰後・破棄後の通知抑止も含む。
