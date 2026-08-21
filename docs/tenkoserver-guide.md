# TenkoServer 実行・デプロイガイド & Dockerfile 解説

本ドキュメントでは、点呼データ集約・管理サーバー **TenkoServer** の Dockerfile の設計背景（`Tenko.Native` が含まれる理由）および、サーバーへのデプロイ・実行手順について解説します。

---

## 📑 目次
1. [TenkoServer の概要](#1-tenkoserver-の概要)
2. [Dockerfile の解説（なぜ Tenko.Native が含まれるのか）](#2-dockerfile-の解説なぜ-tenkonative-が含まれるのか)
3. [事前準備（暗号化データの生成）](#3-事前準備暗号化データの生成)
4. [サーバーの実行手順](#4-サーバーの実行手順)
   - [方法 A: Docker Compose（本番運用・HTTPS自動対応・推奨）](#方法-a-docker-compose本番運用https自動対応推奨)
   - [方法 B: Docker 単体実行](#方法-b-docker-単体実行)
   - [方法 C: dotnet CLI による直接実行（開発・検証用）](#方法-c-dotnet-cli-による直接実行開発検証用)
5. [環境変数・設定一覧](#5-環境変数設定一覧)
6. [クライアント（Tenko.Native）との接続設定](#6-クライアントtenkonativeとの接続設定)
7. [トラブルシューティング](#7-トラブルシューティング)

---

## 1. TenkoServer の概要

TenkoServer は、各拠点の点呼端末（`Tenko.Native`）から送信される点呼スキャンデータを一元集約し、ブラウザでリアルタイム確認できる ASP.NET Core 製の Web サーバーです。

- **Web API**: 端末からのスキャン結果受信（`POST /api/v1/scans`）
- **Web ダッシュボード**: 全拠点集約状況・未点呼者一覧の確認、CSV/BIN 出力
- **通知機能**: Power Automate 連携による学生への点呼完了メール送信（管理画面から「自動送信 / 手動送信」の切り替え、および一括/個別送信が可能）

---

## 2. Dockerfile の解説（なぜ Tenko.Native が含まれるのか）

`src/TenkoServer/Dockerfile` のビルドステージ（前半）には、以下のように `Tenko.Native` や他のプロジェクトファイルが記述されています。

```dockerfile
# Copy solution and project files
COPY ["Tenko.sln", "./"]
COPY ["src/TenkoServer/TenkoServer.csproj", "src/TenkoServer/"]
COPY ["src/Tenko.Native/Tenko.Native.csproj", "src/Tenko.Native/"]
COPY ["src/ScanViewer/ScanViewer.csproj", "src/ScanViewer/"]
COPY ["tests/Tenko.Tests/Tenko.Tests.csproj", "tests/Tenko.Tests/"]

# Restore dependencies
RUN dotnet restore "src/TenkoServer/TenkoServer.csproj"
```

これには以下の技術的理由があります。

### ① Docker ビルドキャッシュの最適化（NuGet Restore キャッシュ）
Docker ビルドでは、ファイルの変更がないレイヤーをキャッシュしてビルド時間を大幅に短縮します。  
ソースコード全体（`.cs` ファイル等）をコピーする前に、**ソリューションファイルと各 `.csproj` だけを先行コピーして `dotnet restore` を実行** することで、ソースコードを編集してもパッケージ復元レイヤーのキャッシュが無効化されず、高速なビルドが可能になります。

### ② ソリューション（Tenko.sln）構造の整合性
リポジトリルートにある `Tenko.sln` には、`TenkoServer`, `Tenko.Native`, `ScanViewer`, `Tenko.Tests` の4プロジェクトが登録されています。ビルドコンテキストをリポジトリルートとし、ソリューション全体の一貫性を保ったまま MSBuild や restore を正しく処理させるために各 `.csproj` が配置されています。

> [!NOTE]
> **本番ランタイムイメージには Tenko.Native は含まれません**  
> マルチステージビルドの `final` ステージ（実行環境）では、ビルドステージで発行された `TenkoServer.dll` とその依存ライブラリのみが抽出されるため、WPF クライアントのバイナリや余分なファイルはコンテナ内に残りません。
> 
> また、セキュリティベストプラクティスに基づき、暗号化パスフレーズは **ビルド時に Docker イメージへ焼き込まず、実行時に環境変数（`TENKO_STUDENTS_PASSPHRASE`）またはボリュームマウント（`data/students.passphrase`）から安全に注入** されます。

---

## 3. 事前準備（暗号化データの生成）

TenkoServer を実行する前に、学生マスタと復号用パスフレーズを準備します。

1. **パスフレーズの準備**:
   `data/students.passphrase` を作成し、暗号化パスフレーズを1行で記述します（または起動時に環境変数 `TENKO_STUDENTS_PASSPHRASE` で渡すことも可能です）。
   ```text
   MySecurePassphrase2026!
   ```

2. **学生元データ CSV の作成**:
   `data/students.csv` を作成します（ヘッダー: `student_number,name,code`）。
   ```csv
   student_number,name,code
   21021,太郎 花子,4D23
   23213,次郎 美咲,2M15
   ```

3. **学生データの暗号化（`students.enc` の生成）**:
   PowerShell で暗号化スクリプトを実行します。
   ```powershell
   .\tools\Encrypt-StudentsCsv.ps1
   ```
   これにより `data/students.enc` が生成されます。

---

## 4. サーバーの実行手順

### 方法 A: Docker Compose（本番運用・HTTPS自動対応・推奨）

リバースプロキシ（Caddy）が内蔵されており、Let's Encrypt による SSL/TLS 証明書の自動取得・更新（HTTPS化）が可能です。

1. **ディレクトリの移動**:
   ```bash
   cd src/TenkoServer
   ```

2. **環境変数の指定とコンテナ起動**:
   ```bash
   # 本番環境での起動例（TENKO_API_KEY / TENKO_ADMIN_PASSWORD は必須。未指定時は起動に失敗します）
   TENKO_API_KEY="your-secure-api-key" \
   TENKO_ADMIN_PASSWORD="your-secure-admin-password" \
   SERVER_DOMAIN="tenko.example.com" \
   docker compose up -d --build
   ```
   ※ ローカルテストの場合も `TENKO_API_KEY` と `TENKO_ADMIN_PASSWORD` の指定が必要です（ドメインは `localhost` 既定）。

3. **ステータス確認 & ログ表示**:
   ```bash
   # コンテナの状態確認
   docker compose ps

   # ログの確認
   docker compose logs -f tenkoserver
   ```

4. **コンテナの停止**:
   ```bash
   docker compose down
   ```

---

### 方法 B: Docker 単体実行

Docker Compose を使わず、TenkoServer 単体を Docker コンテナとして実行する場合の手順です。

1. **イメージのビルド（リポジトリルートで実行）**:
   ```bash
   # 必ずリポジトリルート（Tenko.sln がある階層）で実行してください
   docker build -t tenkoserver -f src/TenkoServer/Dockerfile .
   ```

2. **コンテナの起動**:
   ```bash
   docker run -d \
     -p 8080:8080 \
     -e ASPNETCORE_ENVIRONMENT=Production \
     -e TenkoServer__ApiKey="your-secure-api-key" \
     -e TenkoServer__AdminPassword="your-secure-admin-password" \
     -v tenko_data:/app/data \
     --name tenkoserver_app \
     tenkoserver
   ```
   - Web ダッシュボード: `http://<サーバーIP>:8080`

---

### 方法 C: dotnet CLI による直接実行（開発・検証用）

.NET 8 SDK がインストールされている環境で、Docker を使わずに直接起動する場合の手順です。

```bash
# リポジトリルートから直接起動（AdminPassword / ApiKey は必須）
TenkoServer__AdminPassword="your-secure-admin-password" \
TenkoServer__ApiKey="your-secure-api-key" \
dotnet run --project src/TenkoServer
```

または Release 発行して実行:
```bash
# 発行
dotnet publish src/TenkoServer/TenkoServer.csproj -c Release -o ./publish /p:UseAppHost=false

# 実行
cd publish
dotnet TenkoServer.dll
```
- デフォルト URL: `http://localhost:5000` または `http://localhost:8080`

---

## 5. 環境変数・設定一覧

`docker-compose.yml` またはコンテナ起動時に以下の環境変数を設定できます。

| 環境変数名 | デフォルト値 | 説明 |
|---|---|---|
| `SERVER_DOMAIN` | `localhost` | 公開ドメイン名（Caddy が自動で HTTPS 証明書を取得） |
| `TENKO_API_KEY` | **(必須・既定値なし)** | 端末（クライアント）認証用 API キー（ヘッダー: `X-API-Key`）。ランダムな 256bit 相合の文字列を推奨 |
| `TENKO_ADMIN_PASSWORD` | **(必須・既定値なし)** | Web 管理ダッシュボードのログインパスワード。推測困難な文字列を設定すること |
| `TENKO_STUDENTS_PASSPHRASE` | *(空)* | 学生マスタ復号用パスフレーズ（`data/students.passphrase` または環境変数で設定） |
| `TENKO_POWER_AUTOMATE_WEBHOOK_URL` | *(空)* | Power Automate の HTTP 要求受信トリガー URL。宛先メールアドレスは Power Automate 側で学籍番号から解決する |

> [!CAUTION]
> 以前のドキュメントに記載されていた具体的なキー・パスワードの値は漏洩済みとみなし、必ず新しい値へローテーションしてください。

---

## 6. クライアント（Tenko.Native）との接続設定

TenkoServer を構築したら、各クライアント端末側で `data/server.json` を設定してビルドすることで、スキャン時にデータがサーバーへ自動同期されます。

```json
{
  "serverUrl": "https://tenko.example.com",
  "apiKey": "your-secure-api-key",
  "clientId": "terminal-entrance-01",
  "enabled": true
}
```

---

## 7. トラブルシューティング

### Q1. サーバー起動ログに `Students passphrase is empty` という警告が出る
- **原因**: パスフレーズが設定されていません。
- **対処**: `data/students.passphrase` ファイルを配置するか、環境変数 `TENKO_STUDENTS_PASSPHRASE` を設定してください。

### Q2. サーバー起動時に学生マスタが読み込まれない（未点呼者が0名になる）
- **原因**: `data/students.enc` が配置されていないか、パスフレーズと暗号化時のパスフレーズが一致していません。
- **対処**: `.\tools\Encrypt-StudentsCsv.ps1` を実行して `data/students.enc` を生成・配置し、正しいパスフレーズを設定してください。

### Q3. Docker ビルド時に `COPY failed: file not found in build context` が出る
- **原因**: ビルドコンテキストがリポジトリルート以外になっています。
- **対処**: `docker build` を実行する際はリポジトリルートに移動し、`-f src/TenkoServer/Dockerfile .` を指定してください（Docker Compose を使用する場合は `src/TenkoServer/docker-compose.yml` 内で `context: ../../` が設定されています）。

### Q4. `permission denied while trying to connect to the docker API at unix:///var/run/docker.sock` と怒られる
- **原因**: 現在の Linux ユーザーが `docker` グループに所属していないため、Docker デーモンへのアクセス権限がありません。
- **対処**:
  以下のコマンドでユーザーを `docker` グループに追加し、反映させてください。
  ```bash
  sudo usermod -aG docker $USER
  newgrp docker
  ```
  ※ 一時的に実行したい場合は `sudo docker compose up -d --build` のように `sudo` を付けて実行することも可能です。

### Q5. `WARN: the attribute version is obsolete, it will be ignored` という警告が出る
- **原因**: Docker Compose V2 では `docker-compose.yml` の `version: '3.8'` などの記述が不要（非推奨）になりました。
- **対処**: `docker-compose.yml` から `version:` の行を削除してください（動作には影響ありません）。
