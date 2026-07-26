# Windows_SC 配布手順

更新日: 2026-07-25
対象バージョン: `0.6.2-beta.1`

## 1. 配布形式

Windows_SCはWindows 11 25H2以降、x64、下端タスクバー向けの自己完結版として
配布する。利用先にVisual Studio、.NET SDK、Windows App SDKを別途導入する
必要はない。

成果物は`Windows_SC-<version>-x64.zip`だけとする。展開後、フォルダー内の
`Windows_SC.exe`を実行する。単一EXE版はWinUIの初期化に失敗する環境が
確認されたため、配布しない。

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

スクリプトは`Version.props`から版番号を読み、`artifacts\distribution`を作り直す。
完了時にファイル数、容量、SHA-256を表示する。

## 4. 配布前確認

1. Release x64が警告・エラーなしで発行できる。
2. 通常ZIP版に`README.txt`が入り、展開後のEXEが起動する。
3. 診断情報が今回の版番号を表示する。
4. AI/ML、Widgets関連の未使用ファイルが通常ZIP版に含まれない。
5. 別PCで初回起動、設定保存、再起動、自動起動を確認する。
6. Windowsの未署名アプリ警告が出る可能性を利用者へ伝える。

## 5. 自動起動と配置

自動起動は実行ファイルの現在位置を登録する。登録後に展開フォルダーを移動・改名
した場合は、設定画面で自動起動をいったん無効にし、新しい位置から有効にし直す。
フォルダー内のDLL等は削除・移動しない。

## 6. β版の扱い

`0.6.2-beta.1`は知人向けの最初のβ版であり、署名付きの正式版ではない。
`0.6.2-beta.1`では通常ZIP版と単一EXE版を公開したが、単一EXE版は起動障害が
確認されたため使用しない。以後の配布は通常ZIP版だけとする。
重大な問題が出た場合は通常ZIP版でも再現するか確認し、通常ログと必要に応じて
詳細ログを添えて調査する。同じ版番号の成果物を内容だけ変えて再配布せず、
作り直す場合は版番号を更新する。
