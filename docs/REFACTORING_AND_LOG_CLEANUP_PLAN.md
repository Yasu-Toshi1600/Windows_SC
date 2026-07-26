# リファクタリング・ログ整理準備

作成日: 2026-07-26
対象: `0.6.2-beta.1`から`1.0.0-rc.1`まで

## 1. 目的

正式版前の回帰を増やさず、現在の外部動作、設定JSON、配布方式を維持したまま、
保守しにくい箇所とログ表記のばらつきを段階的に整理する。

この期間は新機能を追加しない。リファクタリングは小さい単位で実施し、各変更後に
簡易テストAを行う。スタート検出、表示モーション、音声COM制御等の動作を変更する
場合は、本書の整理作業とは別の不具合修正として扱う。

## 2. コード再確認の結果

### 2.1 改修対象

| ID | 対象 | 現在の状態 | 実施する整理 | リスク |
|---|---|---|---|---|
| R-01 | `SettingsViewModel.cs` | 約1460行。設定画面本体に、編集用ViewModel、選択肢record、検証、保存、診断操作が同居している。 | まず編集用ViewModelと選択肢型を別ファイルへ移す。型名、名前空間、公開範囲、バインディング名は変更しない。 | 低 |
| R-02 | `SettingsViewModel.SaveAsync` | 入力検証、設定生成、JSON保存、自動起動反映、ロールバック、UI状態更新を1メソッドで行う。 | 保存前検証と`LauncherSettings`生成を分離する。検証結果は対象項目ID、必要なら操作ID、利用者向けメッセージを返し、ViewModelが選択状態を更新する。 | 中 |
| R-03 | 設定保存トランザクション | JSON保存後に自動起動反映が失敗した場合、設定と自動起動を個別に戻している。処理自体は正しいがViewModelへ密結合している。 | R-02の安定後に、保存・自動起動反映・ロールバックを結果オブジェクトで返す調整役へ抽出する。現行の処理順とメッセージは変えない。 | 中 |
| R-04 | `WindowsAudioOutputService.cs` | 約690行。前半のサービス処理と、後半約230行のCOM GUID、構造体、インターフェース定義が同居している。 | COM定義だけを`CoreAudioInterop.cs`へ移す。列挙、既定変更、音量操作、COM解放順は変更しない。 | 低 |
| R-05 | ログ用文字列整形 | `App`、`ActionExecutionService`、`WindowsAudioOutputService`、`UiAutomationStartMenuInspector`、`StartMenuWindowInspector`等に似た改行・引用符除去処理がある。 | 1フィールド分の値を整形する共通`LogValue`ヘルパーへ統合する。パス匿名化は引き続き`DiagnosticLogger`境界で行う。 | 低 |
| R-06 | `MainWindow.xaml.cs` | 約850行。表示、退出、アクティブ化再試行、仮想デスクトップ、環境変更、モーション完了が同居している。 | 先にログ整理だけを行う。分割する場合もpartialファイルへの機械的移動だけとし、フィールド、タイマー、イベント購読順は変えない。 | 中 |

### 2.2 現時点で変更しない部分

次は回帰時の影響が大きいため、再現可能な不具合がない限り`1.0.0`までは構造変更
しない。

- `HybridStartMenuMonitor`と`UiAutomationStartMenuInspector`のスレッド、待機、
  再試行、Snapshot更新
- `StartMenuWindowInspector`の候補選択と複数モニター判定
- `LauncherMotionCoordinator`と`CompositionLauncherMotionService`の状態遷移、
  途中反転、完了通知
- `MainWindow`のアクティブ化再試行、light-dismiss、仮想デスクトップ移動
- `WindowsAudioOutputService`のCOM生成、列挙、既定デバイス変更、解放順
- `App.OnLaunched`の起動順と同期初期化
- 設定スキーマ、既存JSONのプロパティ名、既定値

起動処理の非同期化、UI Automationワーカーの再設計、音声サービスの内部クラス分割は
正式版後の候補とする。

## 3. 推奨するファイル構成

R-01とR-04では、処理内容を変更せず次の構成へ分ける。

```text
ViewModels/
├─ SettingsViewModel.cs
├─ LauncherItemEditorViewModel.cs
├─ CommandCycleStepEditorViewModel.cs
├─ RegisteredAudioDeviceEditorViewModel.cs
└─ SettingsOptions.cs

Services/
├─ WindowsAudioOutputService.cs
└─ CoreAudioInterop.cs
```

`SettingsOptions.cs`には`ActionKindOption`、`AudioOutputDeviceOption`、
`CycleKindOption`、`PostExecutionBehaviorOption`、`LayoutModeOption`を置く。
ファイル分割時に型の名前、コンストラクター、プロパティ、XAMLバインディングを
同時に変更しない。

## 4. 設定保存処理の整理方針

### 4.1 保存前検証

現在`SaveAsync`が直接行っている次の検証を、一つの検証処理へまとめる。

- 表示名が空でない
- ボタンの起動対象またはコマンドが空でない
- 音声循環に異なるデバイスが2台以上ある
- コマンド循環に操作が2件以上ある
- 各コマンド操作の表示名と実行対象が空でない

検証結果は文字列だけでなく、該当するランチャー項目IDとコマンド操作IDを保持する。
ViewModelはその結果を使って`SelectedItem`と`SelectedCommandStep`を設定する。
これにより、現在の「問題のある入力欄を選択して警告する」動作を維持する。

### 4.2 設定生成

検証合格後に、編集用ViewModelから`LauncherSettings`を一度だけ生成する。
生成後は既存の`LauncherSettingsValidator.Validate`も通し、編集画面の検証と
保存ファイルの検証が食い違わないことを確認する。

### 4.3 保存とロールバック

処理順は次を維持する。

1. 保存前の`LauncherSettings`と自動起動状態を保持する。
2. 新しいJSONを保存する。
3. 自動起動を反映する。
4. 自動起動に失敗した場合は、自動起動とJSONを保存前へ戻す。
5. 成功時だけ`MainWindowViewModel`へ反映し、未保存状態を解除する。

この順序はデータ破損を避けるための現行仕様であり、リファクタリング時に入れ替えない。

## 5. ログの統一仕様

### 5.1 基本形

新しいログと変更するログは、次の順序を基本とする。

```text
[領域] action=<操作> result=<結果> reason=<理由> <追加フィールド>
```

- 領域とキーは英語、値は原則として小文字のkebab-caseとする。
- `action`を最初、`result`を次に置く。
- 成功、失敗、取消し、処理不要は`success`、`failed`、`cancelled`、`skipped`を使う。
- 表示状態等は`state=visible|hidden`、対応状態は
  `state=supported|unsupported`のように別フィールドへ置く。
- 例外は`exception`、`hresult`、Win32エラーは`win32-error`を使う。
- 時間は`elapsed-ms`、件数は複数形、真偽値は`true|false`を使う。
- ログ行へ説明文だけを置かず、検索できるキーと値へ変換する。

例:

```text
[Application] action=start result=success
[Compatibility] action=check result=success state=supported os-build=26200
[SystemTray] action=remove result=success
[StartMenu] action=scan result=skipped reason=no-candidates candidates=0
[Motion] action=transition result=success from=Hidden to=Entering reason=start-opened
[AudioOutput] action=cycle result=failed exception=COMException hresult=0x80040111
```

### 5.2 領域名の集約

詳細ログであることを領域名へ付けず、`Write`と`WriteDetailed`の呼出し先で区別する。

| 現在 | 統一後 |
|---|---|
| `ActionDetail` | `Action` |
| `AudioOutputDetail` | `AudioOutput` |
| `UIAutomationFocus`、`UIAutomationCandidate` | `UIAutomation` |
| `StartPanelScan`、`StartPanelCandidate`、`StartPanelSelection` | `StartMenu` |
| `MotionState` | `Motion` |
| `EnvironmentMonitor` | `Environment` |
| `LaunchTiming`、`LauncherVisual`、`VisibilityState` | `Launcher` |

通常ログと詳細ログで同じ出来事を記録する場合は、同じ領域名と`action`を使い、
詳細ログ側に`target`、デバイス情報、座標等を追加する。

### 5.3 通常ログと詳細ログの境界

通常ログへ残す:

- 起動、終了、対応環境判定
- 設定の読込、保存、復旧、自動起動反映
- スタート監視とランチャー状態の主要な変化
- 操作の成否
- 例外型、HRESULT、Win32エラー
- 主要な所要時間と件数

詳細ログだけへ残す:

- アクションの実行対象、引数、作業フォルダー
- 音声デバイスIDと表示名
- UI AutomationのAutomationId、コントロール種別、HWND、矩形
- ウィンドウ位置、モニター座標等の調査値
- 回復可能な例外の詳細メッセージ

現在の`DiagnosticLogger`境界でのパス匿名化は維持する。共通`LogValue`は改行、
NULL文字、引用符等でログ構造が壊れないようにするためのものであり、匿名化の
代わりにはしない。

## 6. 具体的なログ変更点

### 6.1 書式を変更するもの

| 現在の例 | 変更後の例 |
|---|---|
| `[Application] ===== diagnostic session started =====` | `[Application] action=start result=success` |
| `[Application] input-monitor=stopped; window=closed` | `[Application] action=window-close result=success`と`[InputMonitor] action=stop result=success` |
| `[Application] shutdown=requested source=...` | `[Application] action=shutdown-request result=success source=...` |
| `[Compatibility] result=supported ...` | `[Compatibility] action=check result=success state=supported ...` |
| `[Action] result=success kind=...` | `[Action] action=execute result=success kind=...` |
| `[SystemTray] action=remove` | `[SystemTray] action=remove result=success` |
| `[StartPanelScan] スタートメニュー...取得できませんでした。` | `[StartMenu] action=scan result=skipped reason=no-candidates candidates=0` |
| `[UIAutomation] scan=slow ...` | `[UIAutomation] action=scan result=success slow=true elapsed-ms=...` |
| `[MotionState] from=... to=... reason=...` | `[Motion] action=transition result=success from=... to=... reason=...` |

### 6.2 追加する失敗ログ

現在は利用者向けメッセージだけで通常ログへ残らない経路があるため、次を追加する。

- `SettingsViewModel.SaveAsync`の外側の保存失敗
- 詳細診断設定の保存失敗
- データフォルダーを開けなかった場合
- ログ削除に失敗した場合
- 起動対象のファイル／フォルダー選択に失敗した場合

通常ログには操作、結果、例外型、HRESULTだけを残す。例外メッセージに対象パスや
デバイス名が入り得る場合は詳細ログだけへ記録する。

### 6.3 見直す通常ログ

次は通常ログに詳細メッセージを含めているため、例外型とHRESULTだけで原因を
判別できるか確認し、必要ならメッセージを詳細ログへ移す。

- `WindowsAudioOutputService`の列挙、既定取得、音量取得・変更、循環失敗
- `ActionExecutionService`の失敗詳細
- `JsonSettingsRepository`の不正設定メッセージ
- UI Automation登録・解除失敗の例外メッセージ

未処理例外は終了直前の調査に必要なため、通常ログへどこまでメッセージを残すかを
別扱いで判断する。少なくともパス匿名化と一行化は必須とする。

## 7. 実施順

変更は次の単位に分け、別コミットにする。

1. 現在の簡易テストAと代表ログを基準として保存する。
2. R-01: 設定編集用ViewModelと選択肢型をファイル分割する。
3. R-04: Core AudioのCOM定義だけを別ファイルへ移す。
4. R-05: 共通`LogValue`を追加し、重複する値整形を置換する。
5. ログ領域名、`action`、`result`、例外フィールドを小さい領域ごとに統一する。
6. 欠落している失敗ログを追加し、通常／詳細の境界を確認する。
7. R-02: 保存前検証と設定生成を分離する。
8. 必要性が残る場合だけR-03とR-06を行う。

ログの全面置換とクラス分割を同じコミットで行わない。問題が出た場合に、構造変更と
ログ変更のどちらが原因かを判別できる単位を保つ。

## 8. 確認方法

現時点では自動テストプロジェクトがないため、ファイル移動だけの段階はビルドと
簡易テストAで確認する。R-02を実施する場合は、少なくとも
`LauncherSettingsValidator`、保存前検証、`LogValue`、
`LogPrivacySanitizer`を対象にした小さい単体テストを追加すると、以後の変更を
安全に進めやすい。テスト基盤の追加自体はファイル分割の必須条件にはしない。

各コミット後:

- Debug x64またはRelease x64をエラーなしでビルドする。
- 簡易テストAを実施する。
- 設定画面を開閉し、項目選択、追加、削除、並べ替え、未保存表示を確認する。
- 通常ログへ新しい未処理例外がないことを確認する。

設定保存分離後:

- 各入力エラーで該当項目と操作が選択される。
- 正常保存、再起動後の復元、自動起動有効／無効が従来どおり動く。
- 自動起動反映失敗時のロールバック結果を通常ログで追える。

ログ整理後:

- 通常ログに実行対象、音声デバイスID・名称、AutomationId、HWND、矩形がない。
- 詳細ログ有効時だけ上記の調査値が記録される。
- 全行が一行で、領域、`action`、`result`から検索できる。
- ユーザー名と実パスが匿名化される。
- 主要イベントの時系列がログ整理前と同じである。

配布前:

- 簡易テストA／Bを行う。
- x64自己完結の通常ZIP版を生成し、別フォルダーまたは別PCで起動する。

## 9. 完了条件

- `SettingsViewModel.cs`から編集用型が分離され、保存処理の責務が明確になっている。
- `WindowsAudioOutputService.cs`からCOM宣言が分離されている。
- 重複するログ値整形が共通化されている。
- 主要ログが統一形式で検索でき、通常／詳細の情報境界が仕様と一致している。
- 設定JSON、画面操作、スタート連動、音声操作、配布物の動作が変更前と同じである。
- 簡易テストA／Bで新しいFAILがない。
