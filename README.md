# 魔道書 / MagicLogger

魔道書は、自分のChatGPT会話ExportをWindows PCに取り込み、会話の原文と断章をローカルで保存・検索・閲覧するアプリです。この公開準備用ツリーにはCoreだけを収録しています。蒐集手帖は含みません。

## 対応環境

- Windows 11 x64
- [.NET 10 Desktop Runtime x64](https://learn.microsoft.com/dotnet/core/install/windows)（配布候補はフレームワーク依存。利用者にSDKは不要）

Windows 10とArm64は今回の配布対象として検証していません。

## ChatGPT Exportの取り込み

1. [ChatGPTのデータExport](https://help.openai.com/en/articles/7260999-exporting-your-chatgpt-history-and-data)を取得し、安全な場所に保存します。
2. 魔道書を起動し、「記録 ▼」からExportのZIP、`conversations.json`、`conversations-*.json`、または展開済みExportフォルダを選びます。分割JSONは複数選択できます。
3. 巨大ZIPを読み込めない場合はZIPを展開し、フォルダまたは分割JSONを選んでください。失敗したファイルは画面の「再試行」から読み直せます。

取り込みはテキスト中心です。`chat.html`、添付画像などの実体、画像のみの発言は完全には取り込みません。元のExportは取り込み後も元の場所に残ります。

## 基本操作

年月の目次から会話を開き、原文と断章を読み返せます。検索欄ではタイトル・要約・原文・Category・Tagなどを検索できます。複数語はAND検索です。断章には手動Tag、しおり、資料への移動、しおり／資料フォルダがあります。断章を非表示にしたり資料へ移したりしても原文は削除されません。

## 保存先とバックアップ

会話の原文は`%LOCALAPPDATA%\MagicLogger\magic-logger.db`に保存します。通常バックアップとschema移行前バックアップは同じデータ領域の`backups`フォルダに作られます。画面の「バックアップ」で手動作成でき、通常は24時間ごとに最大5世代の自動バックアップも作ります。バックアップはSQLiteのオンライン方式で作成し、整合性を検査した単独の`.db`です。

復旧する場合はアプリを終了し、データ領域全体を別の場所へ保全してから、復旧したい単独バックアップを`magic-logger.db`として戻します。稼働中DBの`.db`だけをコピーしてバックアップの代わりにしないでください。別のドライブにもバックアップを保管すると、ドライブ故障への備えになります。

## ローカル動作

現在の解析Providerはローカル処理だけで動作します。基本機能に外部AIサービスやAPIキーは不要で、アプリから会話を外部AI/APIへ送信しません。ChatGPT Exportを取得する際の通信はChatGPT側の操作です。

## ソースからビルド

Windows上で.NET 10 SDKを使用します。

```powershell
dotnet restore MagicLogger.slnx
dotnet build MagicLogger.slnx -c Release --no-restore
dotnet run --project MagicLogger.Tests/MagicLogger.Tests.csproj -c Release --no-restore
dotnet publish MagicLogger/MagicLogger.csproj -c Release -r win-x64 --self-contained false -p:DebugType=none -o publish
powershell -NoProfile -File tools/New-DistributionCandidate.ps1 -ApplicationOutputDirectory publish -CandidateDirectory candidate
```

テストは合成データだけで完結するコンソール形式です。`dotnet test`ではなく上記の`dotnet run`で実行します。配布候補は`distribution-allowlist.txt`に列挙したファイルだけを含め、作成時に検査スクリプトが走ります。

## ライセンスと第三者ソフトウェア

魔道書Coreは無料のオープンソースソフトウェアとして、MIT Licenseで公開する方針です。著作権表示と利用条件はルートの`LICENSE`を参照してください。

同梱ライブラリの著作権表示と利用条件は`THIRD-PARTY-NOTICES.txt`と`THIRD-PARTY-LICENSES/`を参照してください。アプリはOpenAI / ChatGPTの公式製品ではありません。
