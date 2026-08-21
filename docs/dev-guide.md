# 開発手順ガイド（学生向け）

## 目的
このアプリと**同等の機能**を一から作る前提で、開発手順と主要フレームワーク/関数の役割を整理する。

## 1. 進め方（開発の流れ）

1. **要件整理**
   - 入力: バーコード（5桁/10桁）を受付
   - 出力: 履歴の保存、CSV/BIN出力、検索、削除、締切表示、場所設定
2. **データ設計**
   - `history.json`（全履歴）
   - `scans/ids_<場所>.bin`（下5桁の連続バイナリ）
   - `locations.json`（場所一覧）
   - `settings.json`（現在の場所）
   - `time.json`（締切候補）
3. **UI設計**
   - 入力欄、履歴テーブル、検索、エクスポート、完了/退避、設定モーダル
4. **実装**
   - WPF + MVVMで分離（View / ViewModel / Service）
5. **動作確認**
   - 入力→保存→表示→削除/退避→再起動で復元
6. **配布**
   - `dotnet publish -c Release` で単一実行ファイル出力

## 2. フレームワークと構成

### .NET 8 + WPF
- **WPF**: Windows向けデスクトップUI
- **XAML**: UI定義（`MainWindow.xaml`）

### MVVM構成
- **View**: `MainWindow.xaml`
- **ViewModel**: `ViewModels/MainViewModel.cs`
- **Service**: `Services/*`（保存・通知・学生データ）

## 3. 主要機能と関数対応

### 3.1 入力 → 履歴保存
- **MainViewModel.SubmitManualInput()**
  - 入力バリデーション（数字のみ、5桁/10桁）
  - 重複チェック（同一場所×下5桁）
- **MainViewModel.ProcessScan()**
  - `ScanRecord` を生成して履歴へ追加
  - `HistoryService.SaveHistory()` で `history.json` 保存
  - `ScanFileService.AppendLast5()` で `ids_<場所>.bin` 追記

### 3.2 履歴の読み込み
- **HistoryService.LoadHistory()**
  - `data/history.json` を復元
- **MainViewModel.LoadHistory()**
  - 読み込んだ履歴に氏名/出席番号を付与

### 3.3 場所の管理
- **SettingsService.LoadLocations()**
  - `data/locations.json` を読み込み、なければ既定値を生成
- **SettingsService.Location**
  - 現在の場所を `settings.json` に保存

### 3.4 既存データの退避
- **ScanFileService.Exists()**
  - `ids_<場所>.bin` の存在/サイズ確認
- **ScanFileService.RenameBin()**
  - `ids_<場所>_<suffix>.bin` に退避
- **MainViewModel.RenameBin()**
  - 退避後に現在場所の履歴をリセット

### 3.5 検索
- **MainViewModel.RefreshHistoryView()**
  - 検索語と場所で絞り込み

### 3.6 出力
- **MainViewModel.ExportCsv()**
  - `scan_<場所>_yyyyMMddHHmm.csv` を出力
- **MainViewModel.ExportBin()**
  - `ids_<場所>_yyyyMMddHHmm.bin` を出力

### 3.7 締切表示
- **MainWindow.LoadDeadline()**
  - `data/time.json` を解析し、最短の未来日時を表示
- **FileSystemWatcher**
  - `time.json` 変更を監視して自動更新

### 3.8 学生データ復号
- **StudentService.LoadStudents()**
  - `data/students.enc` を読み込み
  - **EmbeddedStudentsPassphrase.GetPassphrase()** で埋め込み鍵を取得

## 4. 重要ファイル一覧

| 目的 | ファイル |
| --- | --- |
| UI | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| 入力/履歴/検索 | `ViewModels/MainViewModel.cs` |
| 履歴保存 | `Services/HistoryService.cs` |
| BIN保存 | `Services/ScanFileService.cs` |
| 場所設定 | `Services/SettingsService.cs` |
| 学生データ | `Services/StudentService.cs` |

## 5. 最小動作の確認チェック

1. 起動 → 場所選択
2. 入力 → 履歴追加
3. 再起動 → 履歴復元
4. 退避 → 新規履歴で再開
5. time.json変更 → 画面反映
