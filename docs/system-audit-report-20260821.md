# システム監査レポート（セキュリティ / 不具合 / 可読性）

- **文書番号**: AUDIT-2026-0821
- **対象**: BarcodeTenko2 リポジトリ全体（`src/Tenko.Native`, `src/TenkoServer`, `src/ScanViewer`, `tests/`, `tools/`, `data/`）
- **監査日**: 2026-08-21
- **検証環境**: .NET 8 SDK / Windows 11 / `dotnet build` 成功 (エラー 0) / `dotnet test` **13/13 合格**
- **関連文書**: ルート `SECURITY_REPORT.md`（VULN-01〜07）、`docs/code-review-report.md`

> 本レポートは既存レポートの重複指摘を避け、**新たに発見した問題を中心に**記載する。
> 既知事項（クライアント埋め込み鍵 = VULN-01 など）は末尾の「既存レポートとの対応」で参照のみ行う。

---

## 1. エグゼクティブサマリー

全体的なコード品質は高く、MVVM 分離・暗号化フォーマット（Encrypt-then-MAC）・XSS エスケープなど実装は堅実である。Git 履歴にパスフレーズや API キーが混入していないことも確認した。

一方で、**(1) README の記載と実装の不一致（レートリミット不存在）**、**(2) ダッシュボードの「今日」が UTC 基準になるバグ**、**(3) サーバー側の重複排除ルールが「午前/午後」運用と矛盾する設計不具合**、**(4) 平文 HTTP での API キー送信**、という実運用に影響する問題を新たに確認した。

### 重要度別サマリー

| ID | 分類 | 概要 | 重要度 | 修正難易度 |
|---|---|---|:---:|:---:|
| SEC-01 ✅対応済 | セキュリティ | ログインにレートリミットが存在しない（README は保護ありと記載） | **High** | 低 |
| SEC-02 ✅対応済 | セキュリティ | `server.json` の実 URL が平文 HTTP（API キーが平文送信される）＋キー強度不足 | **High** | 低 |
| BUG-01 ✅対応済 | 不具合 | ダッシュボードの日付初期値が UTC 基準（朝 9 時前は「昨日」になる） | **High** | 低 |
| BUG-02 | 設計不具合 | サーバー重複排除ルール（同日+同番号）が「午前/午後」複数回点呼と矛盾 | **High** | 中 |
| SEC-03 | セキュリティ | 認証 Cookie に `Secure` 属性なし／CSRF・セキュリティヘッダ対策なし | Medium | 低 |
| SEC-04 ✅対応済 | セキュリティ | 既定資格情報の不整合（`admin` vs `admin1234`）と平文管理 | Medium | 低 |
| SEC-05 | セキュリティ | API キー／パスワード比較が非定時間 | Low | 低 |
| SEC-06 | 信頼性 | 通知キューが DB保存前に投入・インメモリ（再起動で消失）・失敗時再送なし | Medium | 中 |
| SEC-07 | セキュリティ | CSV 出力のエスケープ不足（サーバー側）／無防備（クライアント側）→ 数式インジェクション | Low | 低 |
| SEC-08 | 運用 | `data/students.csv` が Git 管理対象（実データ誤コミットの素地） | Low | 低 |
| SEC-09 | 運用 | docker-compose がリポジトリの `data/` を丸ごとコンテナへマウント | Low | 低 |
| SEC-10 | セキュリティ | タイムスタンプがタイムゾーン情報なしで送受信され、日付集計が曖昧 | Medium | 中 |
| BUG-03 | 不具合 | CSV/BIN エクスポートが検索フィルタ適用後の行だけ出力する | Medium | 低 |
| BUG-04 ✅対応済 | 信頼性 | 未同期レコードがメモリのみで保持され、アプリ終了で消失 | Medium | 中 |
| BUG-05 | 不具合 | 5桁入力 65536〜99999 が検証を通過後、ushort 解析で例外経由になる | Low | 低 |
| BUG-06 | テスト | 単体テストが実 bin フォルダへ書き込み、設定ファイルを共有改変する | Medium | 中 |
| BUG-07 | テスト | ServerSyncService テストにバックグラウンドタスク競合によるフレーキー懸念 | Low | 低 |
| READ-01 | 可読性 | 復号ロジック約100行がクライアント/サーバーで完全重複 | Medium | 中 |
| READ-02 | 可読性 | 検索ロジックが3箇所で別実装（culture 無視の ToLower） | Low | 低 |
| READ-03〜10 | 可読性 | その他細項目（下記） | Info | 各低 |

---

## 2. セキュリティ上の問題

### 🔴 SEC-01: 管理画面ログインにレートリミットが存在しない（High）

- README.md L202 は「パスワード認証 & レートリミット保護」と記載しているが、`AuthController.Login`（src/TenkoServer/Controllers/AuthController.cs:25）には試行回数制限・遅延・ロックアウトが一切実装されていない。
- パスワードが単一文字列の平文比較のため、インターネット公開時に総当たり攻撃が無制限に可能。
- **対処案**: ASP.NET Core の Rate Limiting ミドルウェア（.NET 8 組み込み `AddRateLimiter`）を `/api/v1/auth/login` に適用し、README の記載と実装を一致させる。

### 🔴 SEC-02: 実運用設定が平文 HTTP ＋ 弱い API キー（High）

- `data/server.json`（Git 管理外だがビルド時埋め込み）:
  ```json
  { "serverUrl": "http://datu.f5.si", "apiKey": "26KunugidaSaiHensyuu", ... }
  ```
- 問題点:
  1. **平文 HTTP** で `X-API-Key` がネットワーク上にそのまま流出する（VULN-01 と組み合わせると、実行ファイルを入手した者だけでなく同一 LAN 内の盗聴者にもキーが漏れる）。
  2. API キーが辞書的で推測可能な文字列。
- **対処案**: Caddy による HTTPS が既に構成済み（docker-compose.yml + Caddyfile）なので、`serverUrl` を `https://` に変更し、キーはランダム 256bit 相当へローテーション。配布端末の再ビルドが必要。

### 🟡 SEC-03: 認証 Cookie / ヘッダ周りの強化不足（Medium）

`Program.cs` L45-65:
- Cookie が `HttpOnly` のみで **`Secure = true` 未指定**。HTTPS 終端（Caddy）前提でも、直接 8080 番ポートに到達できる構成では平文送信され得る。
- CSRF 対策（Antiforgery）なし。`SameSite=Lax` で大部分は緩和されるが、状態変更 POST（logout 等）はワンクリック攻撃の余地が残る。
- `UseHttpsRedirection` / HSTS / CSP / `X-Frame-Options` 等のセキュリティヘッダ未設定。
- `MapFallbackToFile("index.html")` によりダッシュボードのシェル HTML は未認証で配信される（データ API は `[Authorize]` で保護済み。許容範囲だが明示的な設計として文書化推奨）。

### 🟡 SEC-04: 既定資格情報の不整合（Medium）

| 場所 | AdminPassword 既定値 |
|---|---|
| `appsettings.json`（**Git 管理対象**） | `"admin"` |
| `TenkoServerOptions.cs:15`（コード既定） | `"admin1234"` |
| `docker-compose.yml` | `${TENKO_ADMIN_PASSWORD:-admin1234}` |
| README.md L213 | 「初期パスワード: admin」 |

- 3つの既定値が乱立し、ドキュメントと一致しない。`appsettings.json` の `ApiKey` も既定値 `"secret-tenko-api-key-change-me"` のまま Git にコミットされている。
- **対処案**: 既定値は空文字列にし、未設定時は起動失敗 or ランダム生成して初回ログに表示。README を最新化。

### 🟢 SEC-05: 秘密情報比較が非定時間（Low）

- `ApiKeyAuthMiddleware.cs:34` … `string.Equals(configuredApiKey, extractedApiKey)`
- `AuthController.cs:32` … `request.Password != _options.AdminPassword`
- いずれも `CryptographicOperations.FixedTimeEquals` を使うべき（タイミング攻撃は理論リスクだが修正コスト極小）。

### 🟡 SEC-06: メール通知パイプラインの信頼性（Medium）

`ScansController.PostScans` + `NotificationBackgroundService`:
1. **DB 保存前に通知キューへ投入**（ScansController.cs:96）。`SaveChangesAsync` が失敗すると「存在しない点呼」に対するメールが送信される。
2. キューが **インメモリ Channel** のため、プロセス再起動で未送信通知が消失する（スキャンデータは DB に残るのにメールだけ飛ばない）。
3. Webhook 失敗時の**再送機構がない**（NotificationLog への記録のみ）。
4. `BoundedChannelOptions(1000) { FullMode = Wait }` のため、Power Automate 側が遅延すると `PostScans` の応答がブロックされ、クライアントの 5 秒タイムアウト（ServerSyncService.cs:44）と衝突し得る。

### 🟢 SEC-07: CSV 出力のエスケープ不足（Low）

- サーバー側 `DashboardController.ExportCsv`（DashboardController.cs:148）: 値を `"` で囲むのみで、**値内の `"` の倍加エスケープ未実施**。また `=`/`+`/`-`/`@` で始まる値の Excel 数式インジェクション対策なし。氏名・場所はクライアント送信値が入り得るため攻撃面になる。
- クライアント側 `MainViewModel.ExportCsv`（MainViewModel.cs:396）: 引用符すらなし（現行データは数値のみで実害なし）。

### 🟢 SEC-08: `data/students.csv` が Git 管理対象（Low / 運用ルール）

- 現在の内容はダミーデータ（太郎 花子 等, UTF-8 正常）だが、`.gitignore` は `students.csv` / `students.enc` を除外していない。本番の個人情報 CSV を誤ってコミットする事故の素地になる。
- **対処案**: `data/students.example.csv` のみを追跡し、実ファイルは ignore に追加。

### 🟢 SEC-09: docker-compose が `data/` 全体をマウント（Low）

- `volumes: - ../../data:/app/data` により、平文 `students.csv`・`students.passphrase`・`server.json` までコンテナに取り込まれ、DB もリポジトリ作業ツリー内（`data/tenko_server.db`）に書き込まれる。
- 必要なのは `students.enc` のみ。専用ディレクトリ（例: `deploy/data`）へのコピーを推奨。

### 🟡 SEC-10: タイムスタンプのタイムゾーン設計（Medium）

- クライアントは `Timestamp.ToString("yyyy-MM-ddTHH:mm:ss")`（オフセットなしローカル時刻）を送信（ServerSyncService.cs:113）。
- サーバーはこの値から `ScanDate` を派生（ScansController.cs:58-60）するため、端末とサーバーの時計・タイムゾーンがずれると日付集計（summary/unverified/export）が日跨ぎで崩れる。
- `ReceivedAt` は UTC で記録しており方針が混在している。
- **対処案**: 送信時に `DateTimeOffset`（オフセット付き）とし、`ScanDate` はサーバー基準 or 明示的に定義した基準タイムゾーンで算出する。

### 参考: 既存レポートで指摘済みの事項（本監査でも継続該当）

- VULN-01（逆コンパイルによる鍵抽出）: `Generate-EmbeddedPassphrase.ps1` の XOR マスクは**難読化であり暗号化ではない**（mask と data が両方ともバイナリ内にある）。根本解決はサーバー側名前解決への移行のみ。
- VULN-03/04/06/07: 継続。PBKDF2 反復回数 200,000 は OWASP 2023 推奨（SHA-256: 600,000）を下回るため、引き上げを推奨。

---

## 3. コードの不具合（バグ）

### 🔴 BUG-01: ダッシュボードの「今日」が UTC 基準になる（High）

`wwwroot/js/app.js:28, 69`:

```javascript
const today = new Date().toISOString().split('T')[0];
```

- `toISOString()` は UTC。**JST 朝 9 時より前の操作で dateSelect に「昨日」が入り**、summary/scans/unverified/export すべてが昨日の日付を参照する。
- 点呼は早朝に運用されるシステムであり、実害が確実に発生する。
- **対処案**:
  ```javascript
  const d = new Date();
  const today = `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  ```

### 🔴 BUG-02: サーバー重複排除が「午前/午後」運用と矛盾（High）

- クライアント仕様（README L164）: 「点呼完了」でサフィックス（午前/午後）を付けて退避し履歴リセット → **同じ学生が同日に再スキャンできる**ことが前提の機能。
- 一方サーバーは `(ScanDate == scanDate && Last5 == record.Last5)` を重複と判定（ScansController.cs:63-65）するため、**午後の点呼は全員「重複」として破棄され、完了メールも送られない**。
- クライアント側は正常送信（Synced 表示）になるため、データ欠落に気づきにくい危険な不具合。
- **対処案**: 重複判定キーに場所・セッション識別子（clientId + location + 日付 + 任意セッションID）を含める。あるいは Id のみで重複判定し、同日複数回は許容する仕様に統一する。

### 🟡 BUG-03: エクスポートが検索フィルタ適用後の行しか出力しない（Medium）

- `MainViewModel.ExportCsv/ExportBin`（MainViewModel.cs:386-412）は UI 表示用の `History`（フィルタ適用済み ObservableCollection）を出力対象にしている。
- 検索ボックスに入力がある状態で CSV/BIN を出力すると、**条件に合致しないレコードが黙って欠落する**。
- また出力先がカレントワーキングディレクトリ（相対パス）で、`scans/` や `data/` と整合しない。
- **対処案**: `_allHistory.Where(h => h.Location == CurrentLocation)` を出力対象とし、出力先を `StorageService` 経由で決める。

### 🟡 BUG-04: 未同期レコードがメモリのみ（Medium）

- `ServerSyncService._pendingRecords` は List 保持のみ。オフライン中にアプリを終了すると未送信分が消失する（ローカル bin/history には残るため再送手段はあるが自動化されていない）。
- サーバー側は Id 重複排除を実装済みなので、pending を `data/sync_queue.json` に永続化するだけで安全に改善できる。

### 🟢 BUG-05: ushort オーバーフロー境界の UX 不整合（Low）

- 入力検証は「5桁または10桁の数字」を許可するが、格納先 `Last5` は ushort（最大 65535）。
- 例: `99999` は検証を通過した後 `SubmitManualInput` では TryParse 失敗、「解析に失敗しました」。ただしバーコードスキャン経路（`ProcessScan` の `ushort.Parse`）では**例外**になり、ExecuteWithNotify の catch で汎用エラー表示になる。
- **対処案**: 検証段階で `last5 <= 65535` をチェックし、バーコード経路も同じ検証関数を共有する。

### 🟡 BUG-06: 単体テストの分離不足（Medium）

- `UnitTest1.cs` は `new StorageService()`（=テスト bin フォルダ）に直接書き込む。実際に `tests/Tenko.Tests/bin/Debug/net8.0-windows/scan_テスト部屋_*.csv` が 7 ファイル以上残留しているのを確認。
- `SettingsService_LoadsAndSavesCorrectly` は共有の `settings.json` を書き換えるため、並列実行時に他テストと干渉し得る。
- `StudentService_DecryptionAndLookup_Works` がリポジトリの `data/students.enc` + パスフレーズ埋め込みに依存しており、CI で data ファイルが無いと失敗する構造。
- **対処案**: `StorageService(tempDir)` を注入してテストを完全隔離する（コンストラクタは既に baseDir 注入に対応済み）。

### 🟢 BUG-07: ServerSyncService テストの競合（フレーキー懸念）（Low）

- `EnqueueRecord` が fire-and-forget の `Task.Run(SyncPendingAsync)` を起こすため、直後に呼ぶ `await SyncPendingAsync()` が `_isSyncing` ガードで即 return し、バックグラウンド側の完了を待たずに `PendingCount` を断言する可能性がある（TenkoServerTests.cs:227-291）。
- **対処案**: テスト用に同期送信メソッドを分離するか、EnqueueRecord の自動送信をオプション化。

### その他の軽微な指摘

| 位置 | 内容 |
|---|---|
| Program.cs:27 | 接続文字列の解析が `dbPath.Replace("Data Source=", "")` という脆弱な文字列処理。相対パスはワーキングディレクトリ依存。`Microsoft.Data.Sqlite` の `SqliteConnectionStringBuilder` 使用を推奨 |
| MainWindow.xaml.cs:76-83 | FileSystemWatcher の Changed は 1 回の書き込みで複数回発火し、書き込み中ファイルの読み取りで例外が握りつぶされる（動作は catch 済みで実害小） |
| DashboardController.GetScans | 当日全件をメモリに読み込んだ後に検索フィルタ（性能。現規模では許容） |
| Dockerfile | Tenko.sln・他プロジェクトの csproj を COPY するが TenkoServer のみ restore しており不要なコピーがある |
| appsettings.json | `EnableNotifications: true` で Webhook URL 空 → 毎スキャンで Debug ログが出るだけ（無害だが既定 false も検討） |

---

## 4. 可読性・保守性の問題

### READ-01: 復号ロジックの完全重複（最重要）

- `src/Tenko.Native/Services/StudentService.cs:128-208` と `src/TenkoServer/Services/StudentMasterService.cs:179-255` は、TNKS フォーマット定義・v1/v2 復号・CSV パースまで**約 100 行がほぼ逐語的に重複**している。
- 片方のみ改修された場合、暗号化ツール（tools/Encrypt-StudentsCsv.ps1）との整合が崩れ復号不能になるリスク。
- **対処案**: `src/Tenko.Common`（クラスライブラリ）に `StudentsFile.Decode(byte[], string)` として抽出し、両プロジェクトから参照。`docs/code-review-report.md` も同様の指摘済み。

### READ-02: 検索ロジックの三重実装と culture 問題

- `MainViewModel.MatchesSearch` / `ScanViewer MainViewModel.ApplyFilter` / `DashboardController.GetScans` の 3 箇所で類似の絞り込みが別実装。
- いずれも `ToLower()`（カルチャ依存。トルコ語環境で "I" の挙動が変化）を使用。`ToLowerInvariant()` か `string.Contains(x, StringComparison.OrdinalIgnoreCase)` に統一推奨。

### その他の細項目

| ID | 位置 | 内容 |
|---|---|---|
| READ-03 | StorageService.cs:36 | `GetBaseDataPath` は `GetDataPath` の別名エイリアスのみ。削除か統合を |
| READ-04 | tools/*.ps1 | `Fill-RandomBytes` / `Normalize-StudentsCsv` が承認されていない動詞（Approved Verbs 違反）。`Add-RandomBytes` / `ConvertTo-NormalizedCsv` 等へ |
| READ-05 | Tenko.Native.csproj:11-16 | `PublishSingleFile`/`SelfContained` が無条件設定で dev build にも影響。`Condition="'$(Configuration)'=='Release'"` か publish profile 化を推奨 |
| READ-06 | Tenko.Native.csproj:50,64,76 | 埋め込み通知に `<Warning>` を使用 → 毎ビルド 6 件の警告ノイズ（今回の build でも警告は全てこれ）。`<Message Importance="high">` に |
| READ-07 | MainViewModel.cs | 430 行超。エクスポート/リネーム/入力検証をサービス層へ切り出すとテスト性も向上 |
| READ-08 | MainWindow.xaml.cs:130-227 | `LoadDeadline` が約 100 行。JSON 抽出・日時パース・表示更新の 3 関数へ分割推奨 |
| READ-09 | app.js:199 vs 149 | `refreshAllData` は `loadScans(date)` で引数を渡すが、`loadScans` は引数なしで DOM から読む紛らわしいシグネチャ |
| READ-10 | NotificationQueue.cs | `NotificationTask`（モデル）が Queue 実装ファイル内に定義。Models 名前空間へ移動推奨 |
| READ-11 | MainViewModel.cs:395-396 | CSV ヘッダが `Timestamp,ID` なのに出力するのは `FormattedTimestamp,Last5`。「ID」列名が実態と不一致 |

### 良い点（維持推奨）

- 暗号化 v2 フォーマットは** Encrypt-then-MAC **で正しく、`CryptographicOperations.FixedTimeEquals` による MAC 検証・ペイロード全体（magic/version/salt/IV）の認証も適切。
- `app.js` の XSS エスケープ（escapeHtml）は全動的挿入箇所に適用済み（VULN-02 の修正を確認）。
- Git 履歴全体を走査し、`*.passphrase` / `data/server.json` が**一度もコミットされていないこと**を確認（`.gitignore` 有効）。
- `RenameBin` のファイル名サニタイズ、`ScanFileService` の破損ファイル耐性、例外を UI 通知に集約する `ExecuteWithNotify` などの防御的実装。

---

## 5. 推奨アクション（優先順）

1. **今すぐ（デプロイ前必須）**
   - ✅ BUG-01: app.js の「今日」算出をローカル日付に修正 → **対応済み (getTodayLocal 追加)**
   - ✅ SEC-02: serverUrl を HTTPS 化 + API キー再生成 → **対応済み (data/server.json 更新)**
   - ✅ SEC-01: login へ RateLimiter 適用（README との整合） → **対応済み (IP 単位固定ウィンドウ 5回/分, 429 応答)**
   - ✅ SEC-04: 既定パスワード/API キーの整理と README 更新 → **対応済み (既定値廃止・起動時検証・compose 必須化・ドキュメント更新)**
2. **運用開始前までに**
   - BUG-02: 同日複数回点呼を許す重複判定仕様の決定と実装
   - BUG-03: エクスポート対象を全履歴に修正
   - SEC-03: Cookie Secure 属性 + セキュリティヘッダ追加
   - BUG-06: テストの StorageService 注入による隔離
3. **計画的に**
   - READ-01: 共有ライブラリ化（復号ロジック統合）
   - SEC-06: 通知の永続キュー化 + リトライ
   - ✅ BUG-04: 同期キューの永続化 → **対応済み (data/sync_queue.json 永続化)**
   - SEC-10: DateTimeOffset 化

---

## 6. 監査方法

- ソース全件（C# 37 ファイル / PowerShell 4 / JS・HTML・XAML・csproj・Dockerfile 等を含む）を目視レビュー
- `git ls-files` / `git log --all` / `git check-ignore` による機密情報の追跡状況・履歴調査
- `data/students.csv` のバイナリ解析（UTF-8 正常を確認。コンソール上の文字化けはコードページ表示の問題のみ）
- `dotnet build Tenko.sln`（成功、エラー 0 / 警告 6 件はすべて意図的な埋め込み通知）および `dotnet test`（13/13 合格）で検証

---

## 7. 修正記録 (2026-08-21)

「推奨アクション 1」の 4 項目を実装・検証済み。

| 項目 | 修正内容 | 検証結果 |
|---|---|---|
| BUG-01 | `app.js` に `getTodayLocal()` を追加し、UTC 日付 (`toISOString`) の2箇所を置換 | コードレビューで確認 |
| SEC-02 | `data/server.json` を `https://datu.f5.si` + ランダム 256bit API キーへ更新 | ファイル確認 |
| SEC-01 | `Program.cs` に `AddRateLimiter`（IP 単位固定ウィンドウ 5回/分）+ `AuthController.Login` へ `[EnableRateLimiting("auth")]`。Caddy 経由の実 IP 取得のため `UseForwardedHeaders` も追加。429 時は JSON メッセージを返却 | 実機テスト: 誤パスワード5回 → HTTP 401、6回目 → **HTTP 429** |
| SEC-04 | `TenkoServerOptions` の既定値を空文字列化、`appsettings.json` の平文資格情報を削除、`docker-compose.yml` を `${VAR:?}` 必須構文化、README / tenkoserver-guide.md の旧パスワード記載を修正（漏洩済み値のローテーション注意書きも追記）。未設定時は起動時に `InvalidOperationException` で即座に失敗 | 実機テスト: 未設定起動 → 明確なエラーメッセージで異常終了することを確認 |
| BUG-04 (2026-08-21 追補) | `ServerSyncService` に未送信レコードの永続化を実装。`MainWindow` から `data/sync_queue.json` を渡し、①エンキュー時・②送信成功時に一時ファイル経由の原子的書き込み、③起動時に復元（サーバー側 Id 重複排除により再送安全）。破損時は空キューで起動 | 単体テスト `ServerSyncService_PersistsPendingQueueAcrossRestarts` を追加（失敗環境でエンキュー→ファイル永続化→再起動相当で復元→同期成功後クリア、を検証）。テスト **14/14 合格** |

**BUG-04 対応の設計メモ**:
- 復元したレコードの再送は 30 秒間隔のリトライタイマーが担当（起動直後の不要なネットワーク IO を避けるため即時送信は行わない）。
- 同期無効ビルド（server.json 未埋め込み）では従来通りキューは動作しない。
- 既知の範囲外事項: ユーザーがローカル履歴からレコード削除しても、既にエンキュー済みのデータは送信される（削除伝播は BUG-02/SEC-06 と合わせた将来課題）。

**デプロイ時の必須作業（運用側）**:
1. サーバー側で `TENKO_API_KEY=oAYc0dCfdfXO9aEOLO1txvZciZV8T7no2SQolS2JMZA`（または再生成した値）と新しい `TENKO_ADMIN_PASSWORD` を設定して再デプロイする。
2. `datu.f5.si` の DNS がサーバーを指していること・80/443 が開放されていることを確認し、Caddy による HTTPS 化を有効化する。
3. クライアント端末は API キーを埋め込むため**再ビルド・再配布が必要**。
4. 以前のキー・パスワード（`26KunugidaSaiHensyuu`, ドキュメント記載の旧値）は漏洩済みとみなし使用しない。
