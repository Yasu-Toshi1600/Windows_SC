# Windows_SC

Windows 11のスタートメニューと連動する常駐ランチャーです。現在のソース版は **0.9.8**。
配布・検証の状況は[現在の状況](docs/NEXT_STEPS_AND_DECISIONS.md)を参照してください。

## 対応環境と起動

Windows 11 25H2以降（ビルド26200以降）、x64、下端タスクバーが対象です。
1080p以上を推奨します。対象外のOS・構成では起動を中止し、1080p未満は警告して続行します。

通常ZIPを展開し、フォルダー内の `Windows_SC.exe` を実行します。
.NETやWindows App SDKの別途導入は不要です。DLLを取り除かないでください。
MSIXは公開対象外、単一EXEは起動障害があるため現在は生成しません。

## 使い方

Windowsキー、スタートボタン、`Ctrl+Alt+Space`、通知領域アイコンから表示できます。
通知領域から設定・終了も選べます。

- **ボタン**：アプリ・ファイル・URL、コマンド／bat、ShortcutKey、最大50ステップのマクロ。
- **循環切り替え**：音声出力またはコマンドを登録順に実行し、「次」の対象を表示。
  コマンドの位置は再起動後も保持します。内容・順序の変更時は先頭へ戻ります。
- **音量**：マスター音量とアプリ別音量。アプリ別音量は出力先ごとに復元します。
- **モニター**：CPU・GPU・メモリから最大2項目。1項目なら60秒の履歴グラフを表示します。

各項目は、設定画面で`#D3E3FD`を含む8色の基準色から枠線と淡い背景を選べます。
現行ソースでは外側ウィンドウの背景を透明にする処理を追加しています。内側パネルの見た目は維持し、角の透過表示は実機確認待ちです。既存ZIPへの反映状況は[現在の状況](docs/NEXT_STEPS_AND_DECISIONS.md)を参照してください。

設定の編集は「保存」で確定します。旧設定は移行し、破損時は退避します。
将来版の設定は上書きしません。自動起動を設定した後で展開先を移動した場合は登録し直してください。

音声出力は、Windowsが提供するStableIdを保存してドライバー更新後の追跡に利用します。
既存登録は接続中に一度設定を保存してください。追跡できない場合は登録行と新しい出力先を選び、
「選択した登録先を差し替え」で順序を維持して復旧できます。保存した名前は未接続時も表示します。

設定・実行位置・音量状態は `%LOCALAPPDATA%\Windows_SC\Settings`、
ログは同じ場所の `Logs` に保存します。設定画面の「データフォルダーを開く」から確認できます。
通常ログの`[Memory]`行に起動時・1分ごと・設定画面の開閉時のメモリ、CPU、I/O、GPU診断を記録します。長時間稼働で増える場合は[診断値の読み方](docs/LOGGING_AND_PRIVACY.md#メモリ診断)を参照してください。
外部へ自動送信しません。詳細ログにはコマンドやデバイス名が含まれ得るため、共有前に確認してください。

## 制約と開発

音声行の削除時クラッシュは[継続調査中](docs/AUDIO_DEVICE_INVESTIGATION_2026-09-08.md)です。
1080p未満とスマートフォン連携パネル使用時には、配置がずれる可能性があります。

```powershell
dotnet build Windows_SC.slnx -c Debug -p:Platform=x64 --no-restore
dotnet build Windows_SC.slnx -c Release -p:Platform=x64 --no-restore
dotnet test Windows_SC.Tests/Windows_SC.Tests.csproj -c Debug -p:Platform=x64 --no-restore
```

初回や依存変更時は先にrestoreします。配布物の生成は[配布手順](docs/DISTRIBUTION.md)を参照してください。

## 文書

- [現在の状況・残作業](docs/NEXT_STEPS_AND_DECISIONS.md)／[変更履歴](CHANGELOG.md)
- [設計](docs/DESIGN.md)／[機能保守](docs/FEATURE_MAINTENANCE.md)
- [スタート連動](docs/START_MENU_INTEGRATION_MAINTENANCE.md)／[モーション](docs/MOTION_SPECIFICATION.md)
- [ログとプライバシー](docs/LOGGING_AND_PRIVACY.md)
- [実機試験](docs/TEST_CHECKLIST.md)／[自動テスト](docs/CORE_TESTING.md)
- [配布](docs/DISTRIBUTION.md)／[版の更新](docs/VERSIONING.md)
- [リファクタリング計画](docs/UI_MVVM_REFACTORING_PLAN.md)／[過去の記録](docs/archive/README.md)

`docs/archive`は履歴です。現行仕様の根拠にはしません。
