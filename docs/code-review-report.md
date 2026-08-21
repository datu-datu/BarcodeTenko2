# プロジェクト品質・コード精査レポート

- **対象プロジェクト**: BarcodeTenko2
- **検証日時**: 2026-08-21
- **対象フレームワーク**: .NET 8.0 (WPF / win-x64)

---

## 1. プロジェクトのテスト結果

単体テストスイート (`tests/Tenko.Tests/UnitTest1.cs`) を追加・実行し、すべての主要ロジックが正常に動作することを確認しました（**全6項目 合格**）。

| テスト対象 | 検証内容 | 結果 |
| :--- | :--- | :--- |
| **学生データ復号・検索** | `students.enc` (AES-CBC + HMAC) の復号および学籍番号下5桁による氏名・出席番号の紐付け | **合格** |
| **スキャン入力バリデーション** | 数字以外の除外、5桁/10桁判定、同一場所での重複防止、他場所での重複許可 | **合格** |
| **バイナリ操作 (`ScanFileService`)** | UInt16 Little Endian (2バイト) の追記、末尾削除、ファイル名変更、破損ファイル耐性 | **合格** |
| **検索・フィルタリング** | 氏名・出席番号・学籍番号・バーコードによるインクリメンタル絞り込み | **合格** |
| **データ出力** | 履歴のCSV出力、BIN出力の整合性 | **合格** |
| **設定永続化** | `settings.json` / `locations.json` のロード・セーブ | **合格** |

> [!NOTE]
> **発見・修正した不具合**:  
> `src/Tenko.Native/Tenko.Native.csproj` にて `<None Update="..\..\data\students.enc">` と記述されていたため、MSBuildの仕様上ビルド出力先へ暗号化学生データがコピーされず、実行時に学生情報が取得できない状態になっていました。`<None Include="...">` に修正し、正常にコピー・復号されるよう対応済みです。

---

## 2. 余計なDLLの利用チェック

**結論: 余計なサードパーティ製DLLや外部パッケージは一切利用されていません。**

- **NuGetパッケージ**: 外部パッケージへの依存は **0件** です。.NET 8標準のBase Class Library (BCL) およびWPFの標準ランタイムのみで構成されています。
- **配布構成**: `Tenko.Native.csproj` にて `PublishSingleFile` および `SelfContained` が有効化されており、`dotnet publish -c Release` 実行時には単一の実行ファイル（.exe）に集約されるため、不要なDLLが配布先に散らかる心配はありません。
- **プロジェクト間参照**: `ScanViewer.csproj` が `Tenko.Native.csproj` を直接参照してサービス層（`StudentService` 等）を共有しています。小規模ツールとしては実用上問題ありませんが、将来的に規模が拡大する場合は共通ロジックをクラスライブラリ（Class Library）に切り出す構成が推奨されます。

---

## 3. 人が読みやすいコードになっているかの精査

全体として、責務が綺麗に整理されており **非常に読みやすく見通しの良いコード** です。

### 良い点
1. **シンプルなMVVM構成**: View（UI定義）、ViewModel（状態・ビジネスロジック）、Service（データIO・通知）が明確に分かれており、コードの流れが把握しやすいです。
2. **適切な日本語コメント**: 各メソッドに目的を端的に説明する1行コメントが付与されており、初見の開発者でも役割を即座に把握できます。
3. **ボイラープレートの集約**:
   - `JsonHelper.cs`: 日本語の文字化け・エスケープを防ぐ設定を一元管理。
   - `MainViewModel.ExecuteWithNotify`: 例外捕捉とユーザー通知を1箇所に集約。

### 改善・リファクタリングの余地がある点
1. **名前空間の不整合**:
   - `Common/RelayCommand.cs` と `Common/ViewModelBase.cs` は `Common` フォルダ配下にありますが、`namespace Tenko.Native.ViewModels` となっています。フォルダ構成と一致させるとより分かりやすくなります。
2. **ViewModel内でのUIダイアログ直接呼び出し**:
   - `MainViewModel.cs` の `DeleteRecord` や `DeleteAll` で `MessageBox.Show` を直接呼んでいます。ViewModelがUIモーダルに直接依存しているため、自動テスト時に画面ブロックの要因となります。
3. **例外の握りつぶし**:
   - `StorageService.LoadJson` で `catch { return default; }` となっています。JSONファイル破損時にログや通知が出ずに空データとして上書きされる可能性があるため、警告ログを出力するか通知を促すとより安全です。

---

## 4. `src` 配下のファイル数・分割粒度の精査

**結論: 全体として適切な粒度であり、過剰なファイル分け（Over-engineering）は起きていません。**

現在のファイル構成：
- `Tenko.Native`: 14ファイル
- `ScanViewer`: 6ファイル

### ファイルごとの評価
| ファイル / ディレクトリ | 行数 | 評価 |
| :--- | :---: | :--- |
| `ScanFileService.cs` | 62行 | **適切**（バイナリ入出力・削除の独自ロジックが集約） |
| `StudentService.cs` | 221行 | **適切**（暗号化CSVのパース・復号アルゴリズムが集約） |
| `StorageService.cs` | 77行 | **適切**（ディレクトリ保証、JSON・バイトIOを集約） |
| `HistoryService.cs` | 21行 | **統合可能（軽量）**: `LoadJson` / `SaveJson` を呼ぶだけの薄いラッパーのため、`StorageService` または `MainViewModel` に直接持たせることで1ファイル削減可能 |
| `Common/RelayCommand.cs`, `ViewModelBase.cs` | 50行 / 24行 | **統合可能（軽量）**: ファイル数を減らしたい場合は `MvvmCore.cs` など1ファイルに統合可能 |

### 総括
現在のファイル分割は「開発手順ガイド」としても各ファイルの役割が一目で分かる適正なサイズ感（1ファイルあたり20〜200行程度）に収まっており、無駄に複雑な階層構造や不要なインターフェース（`IHistoryService` 等）を作っていないため、**現在の設計が学習・運用のバランスとして最適**です。

---

## 5. 実施した改善内容

1. **暗号化学生データのコピー不具合の修正**:
   - `Tenko.Native.csproj` および `ScanViewer.csproj` にて、`<None Include="..\..\data\students.enc">` を設定し、ビルド・パブリッシュ時に確実に `data/students.enc` が同梱されるよう修正。
2. **名前空間の整理**:
   - `Common/RelayCommand.cs` と `Common/ViewModelBase.cs` の名前空間を `Tenko.Native.Common` に統一。
3. **ViewModelとUIモーダルの疎結合化**:
   - `MainViewModel` に差し替え可能な確認ダイアログデリゲート `ConfirmDialog` を導入。単体テスト時に UI ダイアログによるブロッキングを防ぎ、自動テストによる削除機能の検証を可能化。
4. **StorageService のテスト容易性と堅牢性向上**:
   - `StorageService(string? baseDir = null)` により任意の基準ディレクトリを注入可能に改修。
   - `LoadJson` でのパース失敗時に `Debug.WriteLine` によるエラーログ出力を追加。
5. **単体テストスイートの正式導入**:
   - `tests/Tenko.Tests` プロジェクトをソリューション `Tenko.sln` に統合し、CI/CD やローカルで `dotnet test` が常時実行可能な環境を構築。

