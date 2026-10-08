# Windows_SC 配布手順

更新日: 2026-10-08
対象ソースバージョン: `0.9.8`

## 1. 配布形式

Windows_SCはWindows 11 25H2以降、x64、下端タスクバー向けの自己完結版として
配布する。利用先にVisual Studio、.NET SDK、Windows App SDKを別途導入する
必要はない。1080p以上を推奨し、接続中のディスプレイに1080p未満の解像度が
ある場合は、未検証であることを警告してから起動を続ける。

現行スクリプトの成果物は`Windows_SC-<version>-x64.zip`である。展開後、
フォルダー内の`Windows_SC.exe`を実行する。MSIXは公開配布の対象にしない。

公開配布は通常ZIPまたは単一EXEを目的とする。ただし、単一EXEは一部PCで
WinUI初期化時に`0x80040111`のCOM例外が発生したため、現時点では生成しない。
再採用する場合は発行方法を実装し直し、問題が発生したPCを含む対象環境で
起動を確認してから配布対象へ加える。

## 2. 構成の軽量化

- Windows App SDKは一括パッケージではなく、WinUIコンポーネントとその必須依存だけを使う。
- 未使用のAI/ML、Widgets関連ランタイムを配布物へ含めない。
- 管理対象のサテライトリソース言語は日本語と英語に限定する。配布生成時は
  WinUIのネイティブMUIも整理し、`ja`、`ja-JP`、`en-US`と英語フォールバックだけを残す。
- JSON、COM音声制御、WPF UI Automationの互換性を優先し、トリミングは行わない。
- Release版はReadyToRunを使用する。

## 3. 作成方法

リポジトリのルートで実行する。

```powershell
.\scripts\Build-Distribution.ps1
```

PowerShellの実行ポリシーでスクリプトが止められるPCでは、システム設定を変更せず
次のように今回のプロセスだけ許可する。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Distribution.ps1
```

スクリプトは`Version.props`から版番号を読み、`artifacts\distribution`内の旧成果物を
保持したまま作業用`staging`だけを作り直す。同じ版番号のZIPが既にある場合は上書きせず
停止する。完了時にファイル数、容量、SHA-256を表示する。

## 4. 配布前確認

1. `Version.props`、`Package.appxmanifest`、`CHANGELOG.md`、README、関連仕様の版と内容が一致する。
2. Release x64が警告・エラーなしで発行できる。
3. 通常ZIP版に`README.txt`が入り、別フォルダーへ展開したEXEが起動する。
4. 診断情報とDLLのファイル／製品バージョンが今回の版番号を表示する。
5. AI/ML、Widgets関連の未使用ファイルが通常ZIP版に含まれない。
6. 別PCで初回起動、設定保存、再起動、自動起動を確認する。
7. Windowsの未署名アプリ警告が出る可能性を利用者へ伝える。
8. 1080p未満の警告動作を確認できていなければ、未検証範囲として明記する。
9. Gitタグ、Release本文、公開アセットの版とSHA-256を確認する。
10. 起動後と設定画面の開閉後に通常ログの`[Memory]`行が記録され、メモリ・CPU・I/O・GPU診断を確認できる。GPU取得不可時は`unavailable`を許容する。指標の意味は[ログ仕様](LOGGING_AND_PRIVACY.md#メモリ診断)を参照する。

## 5. 自動起動と配置

自動起動は実行ファイルの現在位置を登録する。登録後に展開フォルダーを移動・改名
した場合は、設定画面で自動起動をいったん無効にし、新しい位置から有効にし直す。
フォルダー内のDLL等は削除・移動しない。

## 6. 公開状況と再配布

版ごとの検証・公開状況は[現在の状況](NEXT_STEPS_AND_DECISIONS.md)に集約する。
同じ版の成果物を内容だけ差し替えず、再配布時は増版する。

## 7. 公開しない形式

- MSIX用のマニフェストやプロジェクト設定は開発互換性のため残るが、Releaseアセットへ加えない。
- x86／ARM64のプロジェクト定義は製品サポートまたは配布保証を意味しない。
- 単一EXEは、起動障害の解消と対象PCでの再確認が完了するまでReleaseアセットへ加えない。
