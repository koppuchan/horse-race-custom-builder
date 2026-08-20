# JvDataSdk

`JVData_Struct.cs` は JRA-VAN Data Lab. SDK（Ver4.9.0.2、`https://jra-van.jp/dlb/sdv/sdk.html` より
登録済み開発者アカウントでダウンロード）に同梱されている「JV-Data構造体（C#版）」をそのまま配置したもの。

- ロジック・バイトオフセットは無編集。プロジェクトの名前空間規約に合わせて `namespace` 宣言のみ追加。
- 著作権表記の通り `(C) Copyright JRA SYSTEM SERVICE CO.,LTD.` に帰属する。登録済み開発者が自身の
  ソフトウェアをビルドするために利用する分には問題ない扱い（他のJV-Link連携OSSプロジェクトも同様に
  各自でこのファイルを配置する運用になっている）。
- SDK配布物一式（インストーラ、ドキュメント、サンプルコード等）はリポジトリには含めていない
  （`.gitignore` 参照）。ビルドに必要なこのファイルのみをコミット対象にしている。
