# BarcodeTenko2

バーコードリーダーを利用した学生点呼システムです。  
.NET 8 WPF によるネイティブ動作で、高速起動・安定したスキャン入力を実現しています。

---

## 📑 目次
1. [特徴](#-特徴)
2. [動作環境・必要ツール](#-動作環境必要ツール)
3. [ビルド前の準備（data/ フォルダの設定）](#-ビルド前の準備data-フォルダの設定)
4. [ビルド & 実行手順](#-ビルド--実行手順)
5. [配布パッケージの作成（本番用）](#-配布パッケージの作成本番用)
6. [アプリケーションの基本操作](#-アプリケーションの基本操作)
7. [データ構造とファイル仕様](#-データ構造とファイル仕様)
8. [サブツール（ScanViewer）](#-サブツールscanviewer)
9. [サーバー管理システム（TenkoServer）](#-サーバー管理システムtenkoserver)

---

## ✨ 特徴
- **高速・軽量**: .NET 8 WPF によるネイティブ動作（USBメモリ等でのポータブル運用が可能）。
- **入力の信頼性**: バーコードリーダーからの高速な一括入力を正確に処理し、重複スキャンを自動検知。
- **機密保護**: 学生の個人情報（氏名・出席番号）は AES 暗号化して管理。復号鍵と点呼場所はビルド時にバイナリへ直接埋め込み。
- **データ互換性**: 既存システムと互換性のあるバイナリ形式（`ids_<場所>.bin`）および CSV エクスポートに対応。

---

## 💻 動作環境・必要ツール

- **OS**: Windows 10 / 11 (64-bit)
- **.NET SDK**: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 以上
- **PowerShell**: Windows PowerShell 5.1 または PowerShell 7+

---

## 📁 ビルド前の準備（`data/` フォルダの設定）

本プロジェクトをビルド・実行するためには、リポジトリ直下の `data/` フォルダ内に以下のファイルを用意する必要があります。

```
BarcodeTenko2/
  ├── data/
  │   ├── students.csv         # [元データ] 学生マスタの平文CSV
  │   ├── students.passphrase  # [ビルド時埋め込み] 暗号化・復号用パスフレーズ
  │   ├── locations.json       # [ビルド時埋め込み] 点呼場所の初期リスト
  │   ├── server.json          # [ビルド時埋め込み] サーバー同期設定（URL・APIキー）
  │   └── students.enc         # [実行時読み込み] 暗号化された学生マスタ（スクリプトで生成）
```

### 各ファイルの役割と作成手順

#### 1. `data/students.csv`（学生マスタ元データ）
学生番号・氏名・出席番号を定義した CSV ファイルです。ヘッダーは `student_number,name,code` 必須です。

```csv
student_number,name,code
21021,太郎 花子,4D23
23213,次郎 美咲,2M15
```

#### 2. `data/students.passphrase`（暗号化パスフレーズ）
暗号化および復号に使用する任意のパスフレーズを **1行** で記述します。
※ このファイルの内容はビルド時にバイナリ内部へ自動的に埋め込まれます。

```
MySecurePassphrase2026!
```

#### 3. `data/locations.json`（点呼場所リスト）
点呼を行う場所の候補を JSON 配列形式で記述します。
※ この設定もビルド時にバイナリ内部へ初期値として自動的に埋め込まれます。

```json
[
  "2棟2階",
  "第一体育館前",
  "本部横"
]
```

#### 4. `data/server.json`（サーバー同期設定 ※任意）
TenkoServer と連携してリアルタイム点呼集約・自動メール送信を行う場合、サーバーの接続情報を記述します。
※ この設定もビルド時にバイナリ内部へ自動的に埋め込まれます。設定しない場合はサーバー同期なし（完全ローカル）で動作します。

```json
{
  "serverUrl": "https://tenko.example.com",
  "apiKey": "secret-tenko-api-key-change-me",
  "clientId": "terminal-main",
  "enabled": true
}
```

#### 5. 学生データの暗号化スクリプトを実行（`students.enc` の生成）
上記 1〜4 のファイルを準備したら、PowerShell を開いてプロジェクトルートで暗号化スクリプトを実行します。

```powershell
.\tools\Encrypt-StudentsCsv.ps1
```

> **成功時**: `data/students.enc` が生成されます。

---

## 🚀 ビルド & 実行手順

### 1. テストを実行して確認
単体テストを実行し、すべての機能が正常に動作することを確認します。

```powershell
dotnet test
```

### 2. 開発実行（デバッグ起動）
点呼アプリ（Tenko.Native）を起動します。

```powershell
dotnet run --project src/Tenko.Native
```

---

## 📦 配布パッケージの作成（本番用）

本番運用（USBメモリ配布など）用に、単一の実行可能ファイルとして出力します。

### 1. リリースビルドの実行

```powershell
dotnet publish src/Tenko.Native -c Release
```

出力先フォルダ:
`src/Tenko.Native/bin/Release/net8.0-windows/win-x64/publish/`

### 2. 配布フォルダの準備

出力先フォルダから、運用PCまたはUSBメモリに以下のファイルをコピーします。

| ファイル / フォルダ | 配布が必要か | 説明 |
|---|:---:|---|
| `Tenko.Native.exe` | **必須** | 単一実行ファイル（パスフレーズ・場所・サーバー設定埋め込み済み） |
| `data/students.enc` | **必須** | 暗号化済み学生データ（自動コピーされます） |
| `data/students.passphrase` | **配布禁止 ❌** | ビルド時に埋め込まれているため不要・漏洩防止 |
| `data/students.csv` | **配布禁止 ❌** | 平文の個人情報のため配布しない |
| `data/server.json` | **配布禁止 ❌** | ビルド時に埋め込まれているため不要・APIキー保護 |

> [!CAUTION]
> 個人情報およびAPIキー保護のため、**`students.csv`、`students.passphrase`、`server.json` は絶対に配布端末に含めないでください。**

---

## 🎮 アプリケーションの基本操作

1. **場所の選択**:
   - 初回起動時または場所を切り替えたいときは、右上の **「設定」** ボタンをクリックし、点呼場所を選択します。
2. **バーコード入力**:
   - 入力欄にフォーカスした状態でバーコードリーダーをスキャンします（手動で 5 桁または 10 桁の数値を入力して `Enter` でも可）。
   - スキャンが成功すると履歴テーブルに学生名が表示され、自動保存されます。
3. **データのエクスポート**:
   - 右上の **「設定」** ボタンをクリックし、**「CSV」** または **「BIN」** ボタンを押すことで、現在の点呼データをファイル出力できます。
4. **点呼完了・退避**:
   - 「点呼完了」ボタンを押してサフィックス（例: `午前`, `午後`）を指定すると、現在のバイナリデータを別名退避し、履歴をリセットして次の点呼を開始できます。
5. **締切時間の設定（任意）**:
   - 実行フォルダの `data/time.json` に `["2026-04-01-10-00", "2026-04-01-15-00"]` 形式で日時を記述すると、画面左下に締切時刻が表示されます。

---

## 📊 データ構造とファイル仕様

| 種類 | パス（実行フォルダ基準） | フォーマット / 説明 |
|---|---|---|
| スキャンバイナリ | `scans/ids_<場所>.bin` | 学籍番号下5桁を UInt16 Little Endian (2バイト) で連続記録 |
| 履歴JSON | `data/history.json` | 全場所のスキャン日時・バーコード・氏名等の履歴ログ |
| 場所設定 | `data/settings.json` | 最後に選択した点呼場所 |
| 場所リスト | `data/locations.json` | 実行時の点呼場所候補（配置しない場合は埋め込み値を使用） |
| 締切時間 | `data/time.json` | 締切時刻候補リスト |

---

## 🔍 サブツール（ScanViewer）

出力された `ids_<場所>.bin` などのバイナリファイルの内容を一覧表示・検索・確認するためのビューアツールです。

```powershell
dotnet run --project src/ScanViewer
```

- **「スキャンデータファイルを選択」** から `.bin` ファイルを選択すると、含まれる学生番号と氏名・出席番号が一覧表示されます。

---

## 🌐 サーバー管理システム（TenkoServer）

リモートの Linux サーバー（またはローカル）で稼働し、複数拠点からの点呼データを一元集約・管理できる Web API & 管理ダッシュボードです。

### ✨ 主な機能
1. **複数拠点の一元集約**: 端末から `POST /api/v1/scans`（ヘッダー: `X-API-Key`）で点呼データをリアルタイム受信・保存。
2. **Power Automate 連携による無料メール送信**: 点呼記録時に `s{last5}@tokyo.kosen-ac.jp` 宛てに完了通知メールを自動送信。
3. **Web 管理ビュー（ダッシュボード）**:
   - ブラウザから安全にログイン（パスワード認証 & レートリミット保護）。
   - リアルタイム点呼状況・完了率・拠点別集計の可視化。
   - **未点呼者リスト**: 学生マスタと当日の点呼データを突合し、未完了学生を瞬時に一覧表示。
   - 全拠点集約データの CSV / BIN ダウンロード。

### 🚀 起動方法

#### ローカル / 直接実行
```powershell
dotnet run --project src/TenkoServer
```
- 管理画面: `http://localhost:5000` (初期パスワード: `admin`)
- ログイン後、リアルタイムダッシュボードが表示されます。

#### Linux / Docker での本番デプロイ (HTTPS 自動対応)
```bash
cd src/TenkoServer
# 必要に応じて docker-compose.yml や appsettings.json の環境変数を設定
docker compose up -d --build
```
- Caddy リバースプロキシが自動的に Let's Encrypt 等で HTTPS 証明書を取得し、Port 443 で安全にアクセス可能になります。

### ⚙️ Power Automate の設定手順（メール自動送信）
1. Power Automate で新しいフローを作成し、トリガーに **「HTTP 要求の受信時」** を選択します。
2. 生成された **HTTP POST の URL** をコピーします。
3. `src/TenkoServer/appsettings.json` の `PowerAutomateWebhookUrl` に貼り付けます（または環境変数 `TenkoServer__PowerAutomateWebhookUrl` に設定）。
4. Power Automate 側で **「メールの送信 (V2)」(Office 365 Outlook)** アクションを追加し、宛先を `triggerBody()?['to']` に設定します。
### 📖 詳細ガイド
より詳しいデプロイ手順や Dockerfile の仕様解説については、[TenkoServer 実行・デプロイガイド](docs/tenkoserver-guide.md) を参照してください。
