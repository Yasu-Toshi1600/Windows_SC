# バージョン管理

現在の版はルートの `Version.props` を正とする。検証・公開状況は
[現在の状況](NEXT_STEPS_AND_DECISIONS.md)で管理する。

## 更新手順

1. `VersionPrefix`をメジャー・マイナー・パッチ形式で変更する。
2. 安定版では`VersionSuffix`を空にする。開発版は`dev`、`beta.N`、`rc.N`。
3. `Windows_SC/Package.appxmanifest`のIdentity Versionを数値4区切りで同期する。
4. CHANGELOG、README、現行docs、配布用READMEを照合する。
   過去の調査結果やarchive内の歴史的な版番号は書き換えない。
5. Debug／Release x64と関連テストを確認する。配布する場合は[配布手順](DISTRIBUTION.md)へ。

版番号更新・ビルド・ZIP生成・公開は別の状態である。公開前はCHANGELOGの「未リリース」で管理し、
公開時に版見出しと公開日を付ける。同じ版の成果物を内容だけ差し替えず、再配布時は増版する。
MSIXメタデータの保守はMSIX公開を意味しない。

パッチは修正、マイナーは後方互換の機能追加、メジャーは互換性変更や製品の大きな区切りを目安とする。
初回正式候補は`1.0.0-rc.1`、正式版は`1.0.0`。接尾辞が空でも公開済みとは限らない。
