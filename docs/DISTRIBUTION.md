# Windows_SC 配布手順

更新日: 2026-07-24
対象バージョン: `0.6.2-beta.1`

## 1. 配布形式

Windows_SCはWindows 11 25H2以降、x64、下端タスクバー向けの自己完結版として
配布する。利用先にVisual Studio、.NET SDK、Windows App SDKを別途導入する
必要はない。

| 形式 | 成果物 | 用途と注意 |
|---|---|---|
| 通常ZIP版 | `Windows_SC-<version>-x64.zip` | 基準形式。展開してフォルダー内のEXEを実行する。起動と調査が比較的分かりやすい。 |
| 単一EXE版 | `Windows_SC-<version>-x64-single.exe` | 受け渡すファイルが1個。初回起動時に内部ファイルを一時領域へ展開するため、起動が遅くなる場合がある。 |

両形式の機能、設定保存先、ログ保存先は同じである。同時起動するための別製品では
ない。問題の切り分けでは通常ZIP版を優先する。

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

片方だけ作成する場合:

```powershell
.\scripts\Build-Distribution.ps1 -Format Folder
.\scripts\Build-Distribution.ps1 -Format SingleFile
```

スクリプトは`Version.props`から版番号を読み、`artifacts\distribution`を作り直す。
完了時に形式、ファイル数、容量、SHA-256を表示する。

## 4. 配布前確認

1. Release x64が警告・エラーなしで発行できる。
2. 通常ZIP版に`README.txt`が入り、展開後のEXEが起動する。
3. 単一EXE版の成果物が本当に1ファイルだけで、単独で起動する。
4. 両形式の診断情報が今回の版番号を表示する。
5. AI/ML、Widgets関連の未使用ファイルが通常ZIP版に含まれない。
6. 別PCで初回起動、設定保存、再起動、自動起動を確認する。
7. Windowsの未署名アプリ警告が出る可能性を利用者へ伝える。

## 5. 自動起動と配置

自動起動は実行ファイルの現在位置を登録する。登録後に通常ZIP版のフォルダーや
単一EXE版を移動・改名した場合は、設定画面で自動起動をいったん無効にし、
新しい位置から有効にし直す。

通常ZIP版はフォルダー内のDLL等を削除・移動しない。単一EXE版は起動時に一時展開
されるため、空き容量やセキュリティ製品の検査によって初回起動時間が変わり得る。

## 6. β版の扱い

`0.6.2-beta.1`は知人向けの最初のβ版であり、署名付きの正式版ではない。
重大な問題が出た場合は通常ZIP版でも再現するか確認し、通常ログと必要に応じて
詳細ログを添えて調査する。同じ版番号の成果物を内容だけ変えて再配布せず、
作り直す場合は版番号を更新する。
