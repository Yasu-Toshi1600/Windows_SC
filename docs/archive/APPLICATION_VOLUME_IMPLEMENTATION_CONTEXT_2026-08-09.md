# アプリ別音量 実装引継ぎコンテキスト（履歴）

作成元: `docs/CODEX_CONTEXT.md`
アーカイブ日: 2026-08-09

> 本書はPhase 3実装時の検討と引継ぎの記録である。現行仕様は
> `../FEATURE_MAINTENANCE.md`と`../DESIGN.md`を正とし、
> 実装計画の履歴は`FEATURE_EXPANSION_IMPLEMENTATION_PLAN_2026-08-09.md`を参照する。

## 対象

Windows_SC の機能拡張計画における **Phase 3: アプリごとの音量** に関する、ChatGPTとの検討内容のみを引き継ぐためのコンテキスト。

この文書は実装時の補助資料であり、現行仕様の正本ではない。

---

## 1. Phase 3 の目的

Windows_SC の既存音量スライダーに、現在のマスター音量だけでなく **アプリごとの音量制御** を追加する。

対象は Windows 標準の Core Audio / WASAPI の公開 API を使ったセッション音量制御とする。

初回実装では次を扱う。

- 現在の既定 Render / Multimedia 出力上のオーディオセッション列挙
- アプリ単位へのセッション集約
- アプリ別音量変更
- 新規セッションの追跡
- Windows 標準音量ミキサーや EarTrumpet 等からの外部音量変更への追従
- アプリ再起動後の再検出
- 出力デバイスごとの音量状態保存・復元
- 既定出力切替時の再バインド

初回実装から外すもの。

- アプリ別ミュート
- 音量メーター
- 入力デバイス
- Communications ロール
- プロセス単位での個別分離
- 非公開 Audio Policy API を使ったアプリ別出力先変更
- 排他モード等、`ISimpleAudioVolume` で扱えないケースへの対応

---

## 2. Windows のアプリ別音量の基本理解

Windows の「アプリ別音量」は、厳密にはプロセス音量ではなく **オーディオセッション音量**。

構造は概ね次のようになる。

```text
Audio Endpoint
  └─ IAudioSessionManager2
       ├─ Discord session
       ├─ Chrome session 1
       ├─ Chrome session 2
       └─ Game session
```

主に使う COM インターフェイス。

- `IAudioSessionManager2`
  - 既存セッション列挙
  - 新規セッション通知登録
- `IAudioSessionControl2`
  - PID
  - Session Identifier
  - Session Instance Identifier
- `ISimpleAudioVolume`
  - セッション音量変更
- `IAudioSessionEvents`
  - 外部音量変更
  - 状態変更
  - セッション切断
- `IAudioSessionNotification`
  - 新規セッション作成通知
- `IMMDeviceEnumerator`
  - 既定出力取得
  - 既定出力変更通知
- `IMMNotificationClient`
  - Audio Endpoint の追加・削除・既定変更通知

`ISimpleAudioVolume.SetMasterVolume()` の `MasterVolume` は、システム全体のマスター音量ではなく **そのセッション内のマスター音量** を意味する。

---

## 3. 出力先ごとの音量保持

Windows のオーディオセッションは Audio Endpoint ごとに存在するため、Windows_SC 側でもアプリ別音量状態は **出力先ごとに分けて保持する** 方針とする。

例:

```text
Speakers
  Discord = 30%

USB DAC
  Discord = 70%
```

状態保存のキーは次の複合キーを使う。

```text
Audio Endpoint ID + Application ID
```

PID、表示名、セッション名は永続キーに使わない。

アプリを再起動すると PID は変わるため、PID 永続化は禁止。

通常アプリは正規化した実行ファイルパスを主な識別子候補とする。

パッケージアプリは、取得可能なら AUMID / Package Identity 等を使う。

ただし初回実装では、まず通常 Win32 exe の識別を完成させ、その後パッケージアプリ対応を追加してもよい。

---

## 4. サービス境界

既存の `WindowsAudioOutputService` にアプリ音量処理を詰め込まない。

新規に次を追加する。

```text
IApplicationVolumeService
WindowsApplicationVolumeService
IApplicationVolumeStateStore
CoreAudioSessionInterop.cs
```

推奨構造:

```text
Launcher Slider
      │
      ▼
IApplicationVolumeService
      │
      ▼
WindowsApplicationVolumeService
      │
      ├── AudioSessionThread (MTA)
      │      ├── IMMDeviceEnumerator
      │      ├── IAudioSessionManager2
      │      ├── IAudioSessionNotification
      │      ├── IAudioSessionEvents
      │      └── ISimpleAudioVolume
      │
      ├── ApplicationIdentityResolver
      │      └── PID -> exe / AUMID
      │
      └── IApplicationVolumeStateStore
             └── application-volume-state.json
```

UI は COM インターフェイスを直接触らない。

---

## 5. COM スレッド方針

音声セッション COM 処理は **MTA で初期化した専用の非 UI スレッド** に集約する。

そのスレッド上で行うこと。

- 既定 Endpoint 取得
- セッション列挙
- Notification 登録
- `ISimpleAudioVolume` 操作
- Notification 解除
- COM 参照解放

コールバック内で重い処理を行わない。

特に次をコールバック内で直接行わない。

- 待機
- Notification の登録解除
- 最後の COM 参照解放
- 大量の UI 更新
- ファイル保存

必要な処理は専用 MTA スレッドのキューへ渡す。

例:

```csharp
OnDefaultDeviceChanged(...)
{
    _audioThreadQueue.Enqueue(
        () => RebindDefaultEndpoint(deviceId));
}
```

---

## 6. 新規セッション通知の重要事項

`IAudioSessionManager2.RegisterSessionNotification()` を登録した後、既存セッションを取得する際は必ず `GetSessionEnumerator()` と `GetCount()` を呼ぶ。

その後は列挙結果だけに依存せず、`IAudioSessionNotification.OnSessionCreated` で新しいセッションを内部一覧へ追加する。

アプリが起動していても、一度も音声を出していない場合はオーディオセッションが存在しないことがある。

そのため、設定済みアプリが未再生の場合は「まだセッションなし」として扱い、後から `OnSessionCreated` でセッションが作成された時点で接続・保存値適用を行う。

---

## 7. セッションとアプリを分離する

「1アプリ = 1セッション」ではない。

Chrome、Discord 等では同じアプリが複数の音声セッションを持つ場合がある。

そのためランタイム内部では、個々のオーディオセッションとアプリ単位のグループを分離する。

例:

```csharp
internal sealed class ApplicationSessionGroup
{
    public required ApplicationKey Key { get; init; }
    public required string DisplayName { get; init; }

    public List<AudioSessionHandle> Sessions { get; } = [];
}
```

```csharp
internal sealed class AudioSessionHandle
{
    public required string InstanceId { get; init; }
    public required uint ProcessId { get; init; }

    public required IAudioSessionControl2 Control { get; init; }
    public required ISimpleAudioVolume Volume { get; init; }
}
```

アプリスライダーを変更した場合は、グループ内の全セッションへ同じ値を適用する。

```csharp
foreach (var session in group.Sessions)
{
    session.Volume.SetMasterVolume(value, ref eventContext);
}
```

`Session Instance Identifier` は実行中一覧の重複排除には使えるが、永続化しない。

`GroupingParam` も Windows の UI 上の集約判断には参考になるが、アプリの永続 ID としては使わない。

---

## 8. PID とアプリ識別

`IAudioSessionControl2.GetProcessId()` から PID を取得する。

注意点として `AUDCLNT_S_NO_SINGLE_PROCESS` は単純な失敗ではない。

複数プロセスが1セッションを共有している場合に返る成功系コードであり、返された PID だけを根拠に「このセッションは必ずこの1プロセス」と断定しない。

通常 Win32 アプリでは概ね次の流れ。

```text
PID
 ↓
Executable Path
 ↓
Path normalization
 ↓
Application ID
```

例:

```text
C:\Program Files\Discord\Discord.exe
 ↓
c:\program files\discord\discord.exe
```

表示名だけを識別子にしない。

保護プロセス、終了済みプロセス、パス取得不可等は安全にスキップする。

アプリ識別処理は別クラスへ分離してよい。

```csharp
internal interface IApplicationAudioIdentityResolver
{
    ApplicationAudioIdentity? Resolve(uint processId);
}
```

---

## 9. 音量変更

UI 上は 0～100%。

Core Audio へ渡す際は 0.0～1.0 に変換する。

```csharp
float value = volumePercent / 100f;
```

対象アプリに複数セッションがある場合は全セッションへ適用する。

同じアプリの複数セッションで現在値が異なる場合は、内部的には「mixed / 混在状態」として扱う。

利用者がスライダーを動かした時点で、全セッションを選択値へ揃える。

---

## 10. EventContext による通知ループ防止

Windows_SC 自身の変更と、Windows 音量ミキサーや EarTrumpet 等による外部変更を区別するために専用 GUID を使う。

```csharp
private readonly Guid _eventContext = Guid.NewGuid();
```

Windows_SC から変更する場合:

```csharp
simpleVolume.SetMasterVolume(value, ref _eventContext);
```

`IAudioSessionEvents.OnSimpleVolumeChanged` 側では EventContext を比較する。

```csharp
if (eventContext == _eventContext)
{
    // Windows_SC 自身による変更
    return;
}

// 外部変更として処理
```

外部変更を受けた場合、現在出力先 + アプリの組み合わせがすでに状態保存対象なら最新値へ更新する。

ただし、外部通知だけを理由に未保存の組み合わせを新規作成しない。

---

## 11. 実行状態ファイル

ランチャー設定ファイルとは別に、実際のアプリ別音量を保存する。

ファイル名案:

```text
application-volume-state.json
```

ランチャー設定側には「どのアプリを操作するスライダーか」を保存する。

現在音量はランチャー設定へ書き戻さない。

理由:

- Windows_SC の既存方針では設定内容は設定画面で明示的に保存する
- スライダーを触るたびに Launcher 設定そのものが変更される設計は既存仕様と衝突する

状態ファイル例:

```json
{
  "schemaVersion": 1,
  "entries": [
    {
      "deviceId": "{endpoint-id-1}",
      "targetKind": "ExecutablePath",
      "targetId": "c:\\program files\\discord\\discord.exe",
      "volume": 30
    },
    {
      "deviceId": "{endpoint-id-2}",
      "targetKind": "ExecutablePath",
      "targetId": "c:\\program files\\discord\\discord.exe",
      "volume": 70
    }
  ]
}
```

`Dictionary` の JSON キーへ複合情報を直接押し込むより `entries` 配列の方が将来拡張しやすい。

状態ファイルは独自 `schemaVersion` を持つ。

保存時は一時ファイルへ書いてから置換する。

保存失敗時は直前の正常ファイルを保持する。

音量変更そのものは成功済みならロールバックしない。

スライダー連続操作による保存は 300～500ms 程度の debounce を検討する。

Core Audio への音量適用自体は即時実行する。

---

## 12. 出力先切替時の動作

対象は初回版では既定 `eRender / eMultimedia` Endpoint のみ。

`IMMNotificationClient.OnDefaultDeviceChanged` で既定出力変更を検知する。

処理順序:

```text
旧 Endpoint
 ↓
Session Events 解除
 ↓
Session Notification 解除
 ↓
COM 参照解放
 ↓
新 Endpoint 取得
 ↓
Session Notification 登録
 ↓
GetSessionEnumerator
 ↓
GetCount
 ↓
既存 Session 列挙
 ↓
アプリごとに集約
 ↓
保存値検索
 ↓
保存値がある組み合わせのみ適用
 ↓
UI 更新
```

重要:

保存値がない出力先へ初めて切り替えた場合、Windows_SC はそのアプリ音量を上書きしない。

Core Audio から現在値を読み、表示するだけにする。

その出力先で利用者が Windows_SC のスライダーを操作した時点で、初めて

```text
Endpoint ID + Application ID
```

の状態を作成する。

例:

```text
Speakers: Discord 30% を保存済み
USB DAC: 未保存

Speakers -> USB DAC

USB DAC 上の Discord が 80% なら、そのまま 80% 表示
Windows_SC は 30% に勝手に変更しない

利用者が USB DAC 上で 50% に変更
 ↓
USB DAC + Discord = 50% を保存

Speakers に戻る
 ↓
Discord 30% を復元
```

現在出力先での変更は他 Endpoint の保存値を書き換えない。

---

## 13. アプリ再起動時の動作

例:

```text
Discord.exe = 30%
```

が保存済みでも、Discord 終了時にセッション COM オブジェクトは消える。

Discord 再起動後、音声再生を開始して新規セッションが生成されたら `OnSessionCreated` が発生する。

その時点で:

```text
新規 Session
 ↓
PID取得
 ↓
Application ID解決
 ↓
Discord と判定
 ↓
現在 Endpoint + Discord の保存値検索
 ↓
保存値があれば適用
```

とする。

アプリが起動済みでもまだ音を一度も再生していない場合はセッションが存在しないことがあるため、その状態では操作不能 / 利用不能表示とする。

---

## 14. 推奨 IApplicationVolumeService API

初期案:

```csharp
public interface IApplicationVolumeService : IDisposable
{
    event EventHandler<ApplicationVolumeChangedEventArgs>? VolumeChanged;
    event EventHandler<ApplicationAvailabilityChangedEventArgs>? AvailabilityChanged;

    Task<IReadOnlyList<ApplicationAudioInfo>> GetAvailableApplicationsAsync();

    Task<ApplicationVolumeState?> GetStateAsync(
        ApplicationAudioTarget target);

    Task SetVolumeAsync(
        ApplicationAudioTarget target,
        int volume);

    Task RefreshAsync();
}
```

UI は COM / PID / Session Identifier 等を直接扱わない。

必要なら実装時に API は調整してよいが、サービス境界は維持する。

---

## 15. Launcher 設定モデル

既存 `VolumeSliderDefinition` に `Master / Application` を表す列挙値を末尾追加する。

既存 v1 スライダーは移行時すべて `Master` として扱い、従来動作を変更しない。

Application の場合は少なくとも次を保持する。

- Identifier type
- Normalized identifier
- Display name

概念例:

```json
{
  "kind": "Slider",
  "volume": {
    "type": "Application",
    "application": {
      "identifierType": "ExecutablePath",
      "identifier": "c:\\program files\\discord\\discord.exe",
      "displayName": "Discord"
    }
  }
}
```

現在音量 30% 等はこの設定には保存しない。

---

## 16. 設定 UI

既存 Volume Slider 設定へ次を追加する。

```text
音量種類
  ○ マスター音量
  ○ アプリ別音量

対象アプリ
  [ Discord ▼ ]
```

アプリ候補は現在音声セッションを持つアプリから取得する。

表示候補には少なくとも次を出す。

- 表示名
- 実行中 / 利用不能状態

更新操作も用意する。

アプリが終了している場合でも、既存設定の対象アプリ識別子は失わない。

---

## 17. ログ方針

通常ログには次を直接出さない。

- アプリ名
- 実行ファイルフルパス
- セッション名

通常ログ例:

```text
[ApplicationVolume] action=set result=success itemId=... sessionCount=2
```

必要に応じて HRESULT / Win32 error / session count 等を残す。

詳細ログでパスを扱う場合も既存の匿名化方針を通す。

---

## 18. 推奨実装順

Phase 3 を一度に完成させず、以下の小さい段階へ分ける。

### Phase 3A: Core Audio セッション列挙

まず UI・状態保存なしで次を動かす。

```text
IMMDeviceEnumerator
 ↓
Default eRender/eMultimedia Endpoint
 ↓
IAudioSessionManager2
 ↓
RegisterSessionNotification
 ↓
GetSessionEnumerator
 ↓
GetCount
 ↓
既存 Session 列挙
 ↓
PID / exe path / current volume 取得
```

成功条件:

- Discord 等の現在音声を出しているアプリを検出できる
- PID が取れる
- exe path が取れる
- 現在音量を読める

### Phase 3B: アプリ集約 + 音量変更

- 同一アプリの複数 Session をまとめる
- 1アプリの全 Session へ同じ音量を適用
- 0～100 -> 0.0～1.0 変換

成功条件:

- Windows_SC から Discord を50%にすると Windows 標準音量ミキサーも50%になる

### Phase 3C: Session Notification / Events

- `OnSessionCreated`
- `OnSimpleVolumeChanged`
- `OnStateChanged`
- `OnSessionDisconnected`
- EventContext による自己通知判定

成功条件:

- Windows_SC より後に音声開始したアプリを検出
- Windows 標準音量ミキサーから変えた値が Windows_SC に反映
- アプリ終了で Session が消える
- 再起動で新 Session を再検出

### Phase 3D: ApplicationVolumeStateStore

- `application-volume-state.json`
- Endpoint ID + Application ID 複合キー
- debounce 保存
- atomic replace

成功条件:

- 同じアプリでも出力先A/Bで別音量を保存できる

### Phase 3E: Default Endpoint 切替

- `IMMNotificationClient`
- 旧 Endpoint 解放
- 新 Endpoint 再列挙
- 保存済み組み合わせだけ復元
- 未保存 Endpoint では現在値を上書きしない

成功条件:

```text
Speakers Discord=30%
USB DAC Discord=70%
```

を保存後、両方向へ切り替えるたび対応値が復元される。

### Phase 3F: WinUI 3 接続

最後に Launcher Slider / Settings UI と接続する。

COM 層・状態保存・Endpoint 切替が安定するまでは UI 実装を最小限にする。

---

## 19. 最初のコミット目標

最初の Phase 3 コミットでは出力先別保存まで実装しなくてよい。

推奨コミット例:

```text
feat: add Core Audio application session service
```

この段階で確認したいこと:

```text
Windows_SC 起動

Discord で音を出す
 ↓
WindowsApplicationVolumeService が Discord を検出

Chrome で音を出す
 ↓
Chrome が追加される

Discord を Windows_SC から 50% にする
 ↓
Windows 標準音量ミキサーも 50%

Windows 標準音量ミキサーから 70% に変更
 ↓
Windows_SC 側も 70%

Discord 終了
 ↓
利用不能 / Session 削除

Discord 再起動 + 音声再生
 ↓
新 Session 再検出
```

ここまで安定してから StateStore と Endpoint 切替へ進む。

---

## 20. 実装時の重要な注意点

Phase 3 で最もバグりやすいのは UI ではなく **COM ライフタイムとイベント競合**。

特に注意すること。

- UI スレッドで Core Audio COM を長時間操作しない
- Session callback 内で COM 解放しない
- Endpoint callback 内で再バインドを完結させない
- 同一 Session を二重登録しない
- `Session Instance Identifier` でランタイム重複排除する
- PID を永続化しない
- `AUDCLNT_S_NO_SINGLE_PROCESS` を単純失敗扱いしない
- `OnSessionCreated` 取りこぼしを避ける
- 自己変更通知ループを EventContext で防ぐ
- 切断済み COM オブジェクトへアクセスしない
- アプリ終了と Session callback の競合を考慮する
- Windows_SC 終了時に Notification 登録解除と COM 解放を確実に行う
- スリープ復帰後は Endpoint / Session 再確認を行う

---

## 21. 実装方針の結論

Phase 3 の最終設計方針は次の通り。

```text
公開 Core Audio API のみ使用

Endpoint
 ↓
Audio Sessions
 ↓
Application Identity で集約
 ↓
ISimpleAudioVolume で変更
 ↓
IAudioSessionEvents で外部変更追従
 ↓
Endpoint ID + Application ID で状態保存
 ↓
Default Endpoint 切替時に保存値復元
```

初回からすべてを一気に実装せず、

```text
COM列挙
→ アプリ集約
→ 音量変更
→ Session通知
→ 状態保存
→ Endpoint切替
→ UI
```

の順で進める。

AUMID / Package Identity 対応は、通常 Win32 exe の流れが安定してから追加してよい。

既存 `WindowsAudioOutputService` の責務はマスター音量・既定出力切替のまま維持し、アプリ別音量は `WindowsApplicationVolumeService` へ分離する。
