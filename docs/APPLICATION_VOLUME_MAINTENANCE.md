# アプリ別音量 保守ガイド

更新日: 2026-08-09

## サービス境界

- `WindowsAudioOutputService`は既定出力切替とエンドポイントのマスター音量を担当する。
- `WindowsApplicationVolumeService`は既定Render／Multimediaエンドポイント上の音声セッションを担当する。
- Core Audioの列挙、登録、操作、解除、COM解放はMTA専用スレッドで直列化する。
- UIへPID、セッション識別子、COMオブジェクトを渡さない。

## 識別と状態保存

- 通常Win32アプリは正規化した実行ファイルパスで識別する。PID、表示名、Session Instance IDは
  永続キーにしない。取得不能な保護プロセス等はスキップする。
- 実行中の重複排除にはSession Instance IDを使い、同一アプリの複数セッションは集約する。
- 状態は`%LOCALAPPDATA%\Windows_SC\Settings\application-volume-state.json`へ保存する。
- 複合キーは`音声デバイスID＋識別子種類＋アプリ識別子`である。
- 利用者がWindows_SCで操作した組み合わせだけを遅延作成する。未保存の出力先へ切り替えた
  だけでは状態を作らず、現在値を上書きしない。
- 保存は400msのdebounce後に一時ファイルから置換する。不正ファイルは`corrupt`名へ隔離する。

## 通知と解放順

1. `RegisterSessionNotification`後に既存セッションを列挙する。
2. 各セッションへ`IAudioSessionEvents`を登録し、コールバックは処理キューへ渡す。
3. 既定出力変更時は各Session Events、Session Notificationの順に解除する。
4. セッション、Manager、EndpointのCOM参照を解放してから新しい出力へバインドする。
5. 終了時はアプリ別音量サービスのキューを完了し、MTAスレッド上で同じ解放順を実行してから、
   音声出力サービスを破棄する。
6. `MMDeviceEnumerator`は他の音声サービスとRCWを共有する可能性があるため、
   `FinalReleaseComObject`で強制解放しない。各処理が取得した参照だけを`ReleaseComObject`で解放する。
7. 通知解除済みまたは切断済みのCOM参照は`InvalidComObjectException`になり得る。
   解除失敗を記録しても、残りのCOM解放と`CoUninitialize`は継続する。

Windows_SC自身の変更は固有EventContextで識別する。外部ミキサー変更は、現在の組み合わせが
保存済みの場合だけ状態ストアを更新する。

## 既知制約と確認

- 初回は既定Render／Multimedia、通常Win32アプリ、音量のみを対象とする。
- アプリ別ミュート、メーター、入力、通信デバイス、アプリ固有の出力先指定は対象外である。
- 2出力の異なる値、未保存出力、複数セッション、外部ミキサー、アプリ再起動、切替連打、
  切断・再接続、スリープ復帰、保存失敗を実機で確認する。
- 通常ログへアプリ名、パス、セッション名を出さない。
