> 履歴資料（2026-11-06退避）。現行仕様・残作業は[現在の状況](../NEXT_STEPS_AND_DECISIONS.md)と現行保守ガイドを参照。未完了項目は現行文書へ引き継ぎ済み。

# 重複チェック・関数分割の調査

対象: main `383ac60` / Windows_SC 0.9.10
調査日: 2026-11-06

## 範囲と方法

製品C#コード96ファイル・17,357行を対象に、検証・正規化・関数参照を横断検索した。
設定読込／編集／保存／実行の経路、システムモニター、音声ID、Start監視、非同期通知の候補箇所を精読し、
XAML・テストの参照も照合した。全行の逐語レビューや動的プロファイルではない。
関数参照の検索は文字列ベースの候補抽出であり、未使用の断定にはXAML等の確認を併用した。

本調査は製品コードを変更していない。ビルド・テスト・GUI・ZIP・別PC・公開Release確認は実施していない。
以下の行番号は調査時点のもの。

## 整理の実施結果（2026-11-06）

以下は調査後の実装結果。調査時の記載と行番号は元の根拠として残す。

- 1～5を実施。未使用表示3種類とFormatBytes、グラフの一回用2関数、起動設定の一回用3関数を削除。
  ショートカットエラーは一度の検証結果を使用し、修飾キーは未知ビットだけを判定する。
- 6は同じモデル検証の重複分を削除し、JsonSettingsRepository.SaveAsync内の検証へ集約。
  UIの対象項目・ステップ再選択用検証と、JSON読込時の検証はそれぞれの入口として維持した。
- 7～8は現在の可変・nullable入力型では前提を保証できないため、必要な処理として維持。
  検証済み型の全面導入は本整理に含めていない。実行系の空定義への補完も今回は変更していない。
- 呼び出し元が非null／現在のリビジョンを渡す内部APIのnull・範囲ガードと、
  本番で使われていない設定保存コーディネーターのキャンセルトークン引数を削除。
  非同期処理の破棄・世代チェック、排他制御、終了時の保存待機は維持した。

ユーザー指定のDesktop/test_review.mdを現行mainと照合し、次を実施した。

| レビュー項目 | 対応 |
|---|---|
| A-1 | 状態ファイルのNextIndexを負数／件数以上に編集して再読込するテストと、正常インデックスでの保存失敗テストへ分離 |
| A-2 | モデル検証に到達しないNaNのDataRowを削除。製品の数値検証とJSON読込設定は維持 |
| A-3 / B-3 | Memory Flightはmainに存在しないため対象外。特別版には変更なし |
| A-4 | 空白付きStableIdのJSON往復を削除し、StableIdのない旧設定からID順に解決できることを確認 |
| A-5 | 正規化前IDの内部Set呼出しから、旧形式IDを含むファイルの読込へ変更 |
| A-6 | Attach一回→実行→Detach→実行されない、という寿命に変更 |
| A-7 / B-1 | 内部ガードだけのテストと未使用キャンセル経路のテストを削除。保存失敗後も次の保存と終了待機が進むテストを追加 |
| B-2 | DictionaryのJSON往復だけのテストを削除。同名デバイスの編集・差し替えはGUIチェックリストへ明記 |
| B-4 | 同一StableIdの大文字小文字だけを変えたassertを削除。衝突・曖昧一致・ID変更・順序のテストは維持 |
| B-5 | Stopのテスト名を戻り値と抑止解除という実際の検証内容へ修正 |
| B-6 | 結果ファクトリーの単純な代入確認テストを削除 |

未知の修飾キービットを含む外部定義の拒否も追加確認した。
Coreテスト103件成功、Release x64ビルド警告・エラー0。出力は`artifacts/simplification-build/`。
GUI、長時間メモリ、ZIP、別PC、公開済みRelease確認は未実施。コミット・push・再起動は未実施。

## 小さく整理できる箇所

### 1. 同一ショートカットを連続して二度検証する

`Windows_SC/ViewModels/SettingsSaveValidator.cs:51–60`

FirstOrDefaultの条件でShortcutKeyValidator.Validateを呼び、不正な項目を見つけた後に
エラーメッセージ取得のため同じ値を再検証している。
ループ内で結果を変数へ一度格納し、その結果と項目IDを返せばよい。
エラーの優先順位と対象項目の再選択は維持する。

### 2. 画面で使わない3種類の表示テキストを生成する

`Windows_SC/ViewModels/LauncherItemViewModel.cs:132–148, 415–424, 518–522`

CpuUsageText / GpuUsageText / MemoryUsageTextは、製品コードとXAMLを検索した範囲では
宣言と自身への代入しか参照がない。実画面はPrimaryMonitorText / SecondaryMonitorTextを使用する。
表示中の更新ごとに余分な文字列を作り、変更通知も発生し得る。
3プロパティ・対応フィールド・代入・FormatBytesを削除する候補。
長時間メモリ増加の原因と断定する材料ではなく、不要な一時割り当ての指摘である。

### 3. グラフの一度しか使わない処理を二段階で包んでいる

`Windows_SC/ViewModels/LauncherItemViewModel.cs:431–434, 495–505`

RefreshSystemMetrics → UpdateMonitorGraphPoints → BuildMonitorGraphPointsとたどるが、
後ろの2関数はいずれも1か所からしか呼ばれない。内容は履歴取得・Point変換・2つの表示への反映。
1項目表示の分岐内へまとめる候補。2か所で使用するReplacePointsは残す。

### 4. 起動設定を作る小さな一回用関数

`Windows_SC/Services/ActionExecutionService.cs:99–111, 134–147`

CreateShellStartInfo（単純なオブジェクト初期化）、BuildCommandLine（条件付き文字列結合）、
IsCommandScript（拡張子2つの判定）は各1か所からしか呼ばれない。呼び出し位置への直接記述を検討できる。
CreateStartInfo全体は環境変数展開や起動方式の分岐を含むため、単に一回使用という理由で統合しない。
複数経路で使用するCreateCommandStartInfo / BuildCommandScriptLineは共有する意義がある。

### 5. 修飾キーの二重判定

`Windows_SC.Core/Application/ShortcutKeyValidator.cs:20–25`

Enum.IsDefinedと未対応ビットのマスク判定をANDで組み合わせている。
現在のFlags定義では、定義済みの値はすべて対応ビット内なので、未対応ビットの判定だけで同じ拒否条件になる。
変更時は既存のShortcutKeyテストで、複合修飾キーと未知ビットの結果を確認する。

## 境界と型を整えてから減らす箇所

### 6. 保存前の検証が重なる

`Windows_SC/ViewModels/SettingsViewModel.cs:1516, 1640`
`Windows_SC/Services/JsonSettingsRepository.cs:93, 127–134`

UI向けSettingsSaveValidator → CreateSettingsForSave内のLauncherSettingsValidator →
Repository.SaveAsync内の同じLauncherSettingsValidatorという経路になっている。
最初の検証は項目選択用IDを返す一方、モデル検証はスキーマ・列挙値・ID等も扱い、完全に同一ではない。
後ろの2回は同じモデルを再検証するが、保存キュー内で診断ログ設定を補完する処理もある。

推奨は、JSON読込とUI保存をそれぞれ外部入力の境界として検証し、
共通の検証結果に項目・ステップIDを持たせること。保存・実行側は検証済みスナップショットを受け取る。
現状の設定モデルはset可能なListとnullableプロパティを持ち、検証済みであることを型で保証していない。
単なるラッパーに可変モデルを格納するだけでは不十分。まずコピーと変更権限を決める必要がある。
保存失敗時の旧設定保護・自動起動ロールバック・エラー項目の再選択を保つ。

### 7. 検証済み設定を内部で再解釈する

`Windows_SC/ViewModels/LauncherItemEditorViewModel.cs:470–484`
`Windows_SC/ViewModels/LauncherItemViewModel.cs:437–464`
`Windows_SC.Core/Application/SystemMetricsState.cs:24`
`Windows_SC.Core/Models/LauncherSettings.cs:91–108`

Widgetの表示項目はモデル検証済みでも、編集VM・表示VMがそれぞれ不正値をCPU＋メモリへ戻し、
計測側も対応ビットだけにマスクする。意味の異なる処理だが、不正値の扱いが内部へ分散している。
旧音声設定のGetEffectiveCycleActionも、検証・編集VM・表示VMで互換変換を繰り返す。
読込境界で互換変換と検証を完了し、内部の選択済み項目と操作を確定させる設計が合う。
非Widget項目のnullは正常なので、一律にすべてのnullチェックを削除してはいけない。

### 8. 必須データの欠落を空オブジェクトで埋めて実行側へ渡す

`Windows_SC/Services/ActionExecutionService.cs:24`
`Windows_SC.Core/Application/MacroExecutionService.cs:63`
`Windows_SC/ViewModels/LauncherItemViewModel.cs:378`

ShortcutKey / Macro / MacroステップActionの欠落をnewの空定義で補っている。
その後に実行側が再判定したり、空マクロが実行成功として扱われ得る構造である。
通常経路では設定検証が欠落を拒否するため、常に発生するバグという指摘ではない。
検証後は「ショートカット操作ならShortcutKeyが必須」「待機ステップなら時間が必須」と
型で表し、欠落の補完と再判定をなくす候補。編集途中の未完成データとは型を分ける。
現状の実行サービスの入力型を変えずに末端の防御だけを削除することは勧めない。

## 維持すべき確認・分割

- UI投入前と実行時の破棄・世代・対話状態の確認。HybridStartMenuMonitor.cs:278–287等では、
  キュー待機の間に状態が変わるため、入力データの重複検証ではない。
- 音声デバイスの接続・現在出力・利用可能性の実行直前確認。登録値の形式検証とは異なる。
  登録IDのNormalize / Distinctは入口へ寄せる候補だが、OSから来るIDも別途正規化が必要。
- HWNDの可視・クローキング・矩形・プロセス確認。別時点のOS観測であり、一度の検証で永続保証できない。
- ICommand.Executeの型確認と実行可能性確認。CanExecuteが必ず直前に呼ばれるとは内部実装上仮定できない。
- FocusEventLoop、ワーカー停止、Compositionの解放、保存ロールバック等の関数。
  1回使用でも、処理・寿命・失敗時の責任を分ける必要がある。
- XAMLイベントハンドラーとネイティブコールバック。C#内の直接呼び出し数だけで未使用扱いしない。

## 整理の順序

まず1～5を小さな変更として扱い、次に6～8の設定境界・検証済み型の設計を行う。
UIA・Composition・音声COMを同時に大きく変更しない。
本調査は設計・保守性のレビューであり、性能測定やメモリリーク原因の特定ではない。
