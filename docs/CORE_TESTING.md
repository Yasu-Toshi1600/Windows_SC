# Windows_SC.Core 自動テスト方針

更新日: 2026-08-09
対象: `Windows_SC.Core`、`Windows_SC.Tests`

## 1. 目的

`Windows_SC.Core`へ移したUI非依存コードは、WinUI、Windows App SDK、COM、レジストリ、
実ウィンドウを生成せずに自動確認する。`Windows_SC.Tests`は`net8.0`を対象とし、
製品ソースのリンクではなくCore成果物を`ProjectReference`で参照する。

```powershell
dotnet test Windows_SC.Tests/Windows_SC.Tests.csproj -c Debug --no-restore
dotnet test Windows_SC.Tests/Windows_SC.Tests.csproj -c Release --no-restore
```

## 2. 自動確認する領域

| 領域 | 主な確認内容 |
|---|---|
| 設定モデル | 既定設定、ID、登録順、旧音声切り替え形式との互換性 |
| スキーマ移行 | v1／版番号なしからv2への移行、将来版の拒否 |
| 設定検証 | ページ・項目ID、操作種類、マクロ、音量範囲、アプリ対象、循環設定、Widget選択数 |
| 設定保存調整 | 編集リビジョン、保存の直列化、取消し、引数検証 |
| ShortcutKey | 検証、表示、ScanCode／VirtualKey入力列、記録対象と抑止解除 |
| マクロ | 登録順、失敗時停止、同時実行拒否、破棄後の実行拒否 |
| 非同期状態 | 最新値優先、逐次キュー、例外通知失敗後の継続、idle完了 |
| 音声状態 | デバイスID正規化、次候補と折返し、出力先別アプリ音量、範囲補正、破損状態隔離 |
| システムモニター | 履歴上限、0～100への補正、欠損値、グラフ座標 |
| ログ値 | 改行等の除去、ユーザー名・既知パス・パス末尾の匿名化 |
| 結果契約 | 成功／失敗ファクトリーの値と件数 |

Coreの振る舞いを変更するときは、正常系だけでなく境界値、取消し、例外、重複、
大小文字差、破損入力のいずれかを再現するテストを同じ変更へ含める。

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

## 4. 2026-08-09確認結果

- Coreテスト: 69件成功、失敗0
- テストTFM: `net8.0`
- CoreのWinUI／Windows App SDK／Windows API参照: なし
- 製品ソースの`Compile Include`: なし
- 自動テスト追加時に、アプリ音量状態のデバイスID比較とユーザープロファイル配下の
  ログ匿名化で不足を検出し、Core実装と回帰テストを修正した。
