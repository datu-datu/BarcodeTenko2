# Tenko.Lite 改善指示書（実装エージェント向け）

- **対象リポジトリ**: `BarcodeTenko2`（.NET 8 / WPF / win-x64）
- **対象ブランチ**: `refactor/tenko-native-redesign`
- **対象スコープ**: `src/Tenko.Lite` およびテスト `tests/Tenko.Tests`
- **目的**: 「削除機能」の堅牢化、メモリ／リソースリーク対策、UI・操作性改善、軽微なバグ修正
- **作成時点の状態**: Debug/Release ビルド成功、既存テストすべて合格（件数は §1 の手順で各自が確認すること。本書では件数を固定しない）

---

## 0. 最初にやること（作業プロトコル）

1. §5「対応済み」を読み、**同じ修正を繰り返さない**こと。
2. §7「変更禁止」を読む。ここに挙げた形式は外部互換があるため変更不可。
3. §1 の検証コマンドでベースラインを確認し、**合格件数を記録**する（以降の比較基準にする）。
4. **タスクは 1 件ずつ実施する。**
   - ユーザーがタスク ID（例: 「A-1 と C-4 をやって」）を指定した場合は、**そのタスクだけ**を実施する。
   - 指定がなければ §4 の優先度表に従い **P0 → P1 → P2** の順で 1 件ずつ進める。
   - **全タスクを一度に実施しない。** 1 タスク完了 → 報告、の繰り返しとする。
5. **変更前に必ず対象ファイルを読むこと。** 本書の記述とコードが異なる場合は、**コードを正**とし、差異を報告に含める。
6. 各タスク完了ごとにビルドとテストを実行し、ベースラインから壊れていないことを確認してから次へ進む。
7. 各タスクの完了時に、以下の形式で簡潔に報告する:

```
- 実施タスク: [ID] [タイトル]
- 変更ファイル: [パスの箇条書き]
- 追加/更新テスト: [テスト名と件数]
- 検証結果: ビルド [成功/失敗] / テスト [合格 n 件・失敗 n 件]
- 見送り・補足: [あれば理由付きで]
```

---

## 1. 前提環境と検証手順

- Windows + .NET 8 SDK（開発機は SDK 9.0.313 でビルド確認済み）。WPF のため Windows 以外ではビルド不可。
- ビルド時に PowerShell スクリプトが実行され、`data/locations.json` と `data/server.json` が埋め込みコードとして生成される（`tools/Generate-EmbeddedLocations.ps1` / `tools/Generate-EmbeddedServerConfig.ps1`）。`data/` が無いとビルドは失敗する。
- 検証コマンド（リポジトリルートで実行）:

```powershell
# ビルド
dotnet build src/Tenko.Lite/Tenko.Lite.csproj -c Debug

# テスト（既存テストがすべて合格すること。初回実行の合格件数をベースラインとして記録する）
dotnet test tests/Tenko.Tests/Tenko.Tests.csproj -c Debug

# UI の目視確認が必要な場合
dotnet run --project src/Tenko.Lite/Tenko.Lite.csproj -c Debug
```

- ビルド時の `[Generate-Embedded*] ... loaded and embedded` 警告 4 件は**正常**（既存の仕様）。エラーではない。消そうとしない。
- テストプロジェクトは **Lite 以外のテストも含む**（`UnitTest1.cs` = Native/Lite、`ScanViewerTests.cs`、`TenkoServerTests.cs`）。`dotnet test` はこれら全件を実行する。Lite の変更で他プロジェクトのテストが失敗した場合は意図しない影響が出ているため、変更を見直すこと。

---

## 2. アーキテクチャ早見表

| 役割 | ファイル |
| --- | --- |
| アプリ起動・DI 登録 | `src/Tenko.Lite/App.xaml.cs` |
| 色/スタイルリソース | `src/Tenko.Lite/App.xaml` |
| UI 定義 | `src/Tenko.Lite/Views/MainWindow.xaml` |
| UI コードビハインド（フォーカス、キー、アニメ） | `src/Tenko.Lite/Views/MainWindow.xaml.cs` |
| 状態・コマンド・検索・削除・出力 | `src/Tenko.Lite/ViewModels/MainViewModel.cs` |
| スキャン検証・重複判定・デバウンス | `src/Tenko.Lite/Services/ScanProcessor.cs` |
| スキャン結果 DTO / インターフェース | `src/Tenko.Lite/Services/IScanProcessor.cs` |
| `scans/ids_<場所>.bin` の入出力 | `src/Tenko.Lite/Services/ScanFileService.cs` |
| `data/*.json` の読み書き（アトミック書き込み） | `src/Tenko.Lite/Services/StorageService.cs` |
| `data/history.json` | `src/Tenko.Lite/Services/HistoryService.cs` |
| `data/settings.json` / `data/locations.json` | `src/Tenko.Lite/Services/SettingsService.cs` |
| サーバー同期（キュー、削除、再送） | `src/Tenko.Lite/Services/ServerSyncService.cs` |
| CSV / BIN 出力 | `src/Tenko.Lite/Services/ExportService.cs` |
| ステータスバー通知（イベント） | `src/Tenko.Lite/Services/NotificationService.cs` |
| 1 秒ごとの時刻通知 | `src/Tenko.Lite/Services/ClockService.cs` |
| 確認ダイアログ | `src/Tenko.Lite/Services/DialogService.cs` |
| JSON 設定の一元化 | `src/Tenko.Lite/Infrastructure/JsonHelper.cs` |
| MVVM 基盤（`RelayCommand` / `ViewModelBase`） | `src/Tenko.Lite/Common/*.cs` |
| 履歴 1 件のモデル | `src/Tenko.Lite/Models/ScanRecord.cs` |
| テスト（Native/Lite 用） | `tests/Tenko.Tests/UnitTest1.cs` |

補足: Lite 版には **`StudentService`（氏名・出席番号の紐付け）が無い**。これは意図的な仕様であり、追加しないこと。

保存先の実パス: 実行時データ（`data/` と `scans/`）は、実行フォルダ直下の **`kunugidasainotenko/`** フォルダ配下に作成される（`StorageService.RootFolderName` 定数）。本ドキュメント中の `data/...` / `scans/...` は、すべてこの親フォルダを基準とした相対パスである。

---

## 3. 現行のデータフロー

```
[手入力/スキャナ] → ManualInputBox(Enter)
  → MainViewModel.SubmitManualInput()
    → ScanProcessor.ProcessScan(barcode, location, _allHistory)
       ① location 空チェック → LocationNotSet
       ② 空文字 → ValidationError
       ③ 数字以外 → ValidationError
       ④ 5桁/10桁以外 → ValidationError
       ⑤ 下5桁を ushort 化（オーバーフロー確認）
       ⑥ デバウンス判定（同一番号を 3 秒以内）→ IgnoredDebounce
       ⑦ 同一ロケーション内の重複判定 → DuplicateWarning
       ⑧ ScanRecord 生成 → allHistory.Insert(0, ...)
       ⑨ HistoryService.SaveHistory()  … data/history.json 全件書き直し
       ⑩ ScanFileService.AppendLast5() … scans/ids_<場所>.bin へ 2byte 追記
       ⑪ ServerSyncService.EnqueueRecord() … 送信キューへ
    → VM が History(表示用) を更新し IsRecentlyAdded=true
  → XAML の DataGridRow トリガでフェードアニメ
```

---

## 4. タスク優先度一覧

凡例: ✅ = 完了、⬜ = 未着手

| ID | 分類 | タイトル | 優先度 | 状態 |
| --- | --- | --- | --- | --- |
| A-1 | 削除 | 参照等価依存の削除を Id ベースに | P0 | ✅ 完了 |
| A-2 | 削除 | bin と history の不整合検出（`RemoveLast5` の戻り値化） | P0 | ✅ 完了 |
| A-3 | 削除 | サーバー削除伝播のバッチ化（ファイル I/O 削減） | P0 | ✅ 完了 |
| A-4 | 削除 | 複数選択削除 | P1 | ⬜ 未着手 |
| A-5 | 削除 | 削除コマンドの CanExecute とフィードバック | P1 | ⬜ 未着手 |
| A-6 | 削除 | 削除後の `History` 更新方針の統一 | P1 | ⬜ 未着手 |
| A-7 | 削除 | 削除のアンドゥ | P2 | ⬜ 未着手 |
| B-1 | メモリ | `MainWindow` の無条件 `Dispatcher.Invoke` をガード | P0 | ✅ 完了 |
| B-2 | メモリ | `RunOnUi` にシャットダウンガードを追加 | P0 | ✅ 完了 |
| B-3 | メモリ | 送信キュー `_pendingRecords` の上限設定 | P0 | ✅ 完了 |
| B-4 | メモリ | `_allHistory` の全件常駐と毎回全走査の改善 | P1 | ⬜ 未着手 |
| B-5 | メモリ | `IsRecentlyAdded` のリセット（Storyboard 再再生の抑止） | P1 | ⬜ 未着手 |
| B-6 | メモリ | Dispose の回帰テスト追加 | P1 | ✅ 完了 |
| B-7 | メモリ | `SaveHistory` の O(n²) 書き込み対策 | P2 | ⬜ 未着手 |
| C-1 | UI | 「累計」が絞り込みに追随してしまう | P0 | ✅ 完了 |
| C-2 | UI | 通知の色分け（`TypeToBrush`）が未配線 | P0 | ✅ 完了 |
| C-3 | UI | 出力先がカレントディレクトリ（書込不可の恐れ） | P0 | ✅ 完了 |
| C-4 | UI | CSV ヘッダ誤り（`Timestamp,ID` → 実体は Last5） | P0 | ✅ 完了 |
| C-5 | UI | 出力対象が絞り込み結果になっている／無言 return | P1 | ⬜ 未着手 |
| C-6 | UI | モーダルを Esc/Enter で操作できない | P1 | ⬜ 未着手 |
| C-7 | UI | 設定モーダル表示中に場所変更で警告モーダルが裏に隠れる | P1 | ⬜ 未着手 |
| C-8 | UI | 最小サイズ未指定・フォント小さめ・起動位置 | P1 | ⬜ 未着手 |
| C-9 | UI | 文言・エラーメッセージの改善 | P1 | ⬜ 未着手 |
| C-10 | UI | 重複スキャン時の明示 | P1 | ⬜ 未着手 |
| C-11 | UI | 場所の追加・編集 | P2 | ⬜ 未着手 |
| C-12 | UI | 空状態表示・並べ替え | P2 | ⬜ 未着手 |
| D-1 | 不具合 | `ProcessScan` の保存順序（history 先行で不整合） | P0 | ✅ 完了 |
| D-2 | 不具合 | `SubmitManualInput` が例外を捕捉していない | P0 | ✅ 完了 |
| D-3 | 不具合 | デバウンスが単一スロット（Native と非対称） | P1 | ⬜ 未着手 |
| D-4 | 不具合 | `RenameLocationBin` のサニタイズ・同名衝突 | P1 | ⬜ 未着手 |
| D-5 | 不具合 | 保存場所が `Locations` に無いと ComboBox が空表示 | P1 | ⬜ 未着手 |
| D-6 | 不具合 | 30 秒タイマーの空キュー時早期 return | P2 | ⬜ 未着手 |
| D-7 | 不具合 | 例外メッセージが技術的すぎる | P2 | ⬜ 未着手 |

進捗: **P0 全 12 タスク完了**（P0 は A-1〜A-3 / B-1〜B-3 / C-1〜C-4 / D-1〜D-2。B-6 も完了）。P1・P2 は未着手。

---

## 5. 対応済み（同じ修正を繰り返さないこと）

以下は本ブランチで対応済み。**やり直さないこと。**

- `MainViewModel` は `IDisposable` を実装済み。`ClockService.OnTick` / `ServerSyncService.OnStatusChanged` / `NotificationService.OnNotification` の購読解除と `_notificationTimer` の停止を `Dispose()` で実施済み。
- 購読はラムダではなくメソッド参照（`OnClockTick` / `OnSyncStatusChanged` / `OnNotificationReceived`）に変更済み。
- `MainWindow.Closed` から `_viewModel.Dispose()` を呼ぶように接続済み。
- UI スレッド委譲は `MainViewModel.RunOnUi(Action)` に集約済み（`private static`）。
- `MainViewModel.RefreshHistoryView` は `ToLower()` を 1 回だけ計算し、LINQ と中間 `List` を排除済み。
- `ScanProcessor` の下 5 桁解析は `Substring` ではなく `barcode.AsSpan(...)` + `ushort.TryParse` に変更済み。
- `ScanFileService.RemoveLast5` はバイト配列を直接操作（`List<ushort>` と LINQ を排除）済み。
- `StorageService.SaveJson` は一時ファイル + `File.Move(overwrite:true)` のアトミック書き込み、かつストリーム直列化に変更済み（`JsonHelper.SerializeToStream` を追加済み）。
- `ServerSyncService` は注入された `HttpClient` を `Dispose` しないよう所有権を修正済み（`_ownsHttpClient`）。
- 未使用の `StorageService.GetScanFiles`、`SettingsService` の重複初期代入、不要な `using` は削除済み。

### 今回対応済み（P0・2026-09-15）

以下は本指示書に沿って実装済み。**やり直さないこと。** テストは `tests/Tenko.Tests/UnitTest1.cs` に追加済み（ビルド成功・全 44 件合格）。

- **A-1**: `IScanProcessor.DeleteRecord` を `bool DeleteRecord(ScanRecord, List<ScanRecord>, out bool binMismatch)` に変更。`Id` 一致で 1 件削除し、VM 側の `History` も Id 一致で除去。見つからない場合は表示を再構築して警告。
- **A-2**: `ScanFileService.RemoveLast5` を `bool` 返却化し、`CountLast5` を追加。BIN から除去できない場合／BIN と履歴の件数が食い違う場合に `binMismatch` を立て、VM が警告。
- **A-3**: `ServerSyncService` に `RemovePendingRecords(IEnumerable<string>)` / `EnqueueDeletions(IEnumerable<string>)` を追加。保存とステータス更新は各 1 回だけ。単発の `RemovePendingRecord` / `EnqueueDeletion` はバッチへ委譲。`ScanProcessor.PropagateServerDeletions` が ID 群をまとめて伝播。
- **B-1**: `MainWindow` に `RunOnUi`（`CheckAccess` + `HasShutdownStarted/Finished` ガード）を追加し、通知フラッシュとフォーカス移動の両方で使用。
- **B-2**: `MainViewModel.RunOnUi` にディスパッチャ停止ガードを追加（停止中は何もしない）。
- **B-3**: `ServerSyncService` に `MaxPendingRecords` / `MaxPendingDeletions`（各 5000）を追加。超過時は古いものから破棄／追加を打ち切り、ステータスに「(上限超過)」を表示。
- **B-6**: `Dispose` の回帰テストを追加（`TenkoLite_MainViewModel_DisposeStopsEventSubscriptions`）。
- **C-1**: `MainViewModel.CurrentLocationCount`（現在場所の総件数）と `FilteredCount` を追加。「累計」は `CurrentLocationCount` にバインドし、絞り込み件数はバッジに「絞り込み中 n 件」として分離。
- **C-2**: 未配線だった `TypeToBrush` をステータスバーの `Foreground` に接続（成功=緑 / 警告=橙 / エラー=赤）。
- **C-3**: `MainViewModel.ExportDirectory`（既定: マイドキュメント配下の `Tenko出力`）を追加し、出力先を必ず作成。通知は絶対パス表示。テストから差し替え可能。
- **C-4**: `ExportService.ExportCsv` のヘッダを `時刻,学籍番号` に修正し、BOM 付き UTF-8 で出力。
- **D-1**: `ProcessScan` を「BIN 追記 → 履歴保存」の順に変更。失敗時は追記済み BIN をロールバックして例外を再送出（VM が通知）。
- **D-2**: `SubmitManualInput` を try/catch し、入力値を保持したまま汎用の日本語メッセージでエラー通知。

補足（未対応・申し送り）:

- `Tenko.Native` 側の `ExportService` は旧ヘッダ `Timestamp,ID` のまま（スコープ外・未対応）。
- UI 変更（C-1 / C-2）は XAML のコンパイル成功までの確認。実機での目視確認は未実施。

---

## 6. タスク詳細

### A. 削除機能の改善

状態: **A-1 / A-2 / A-3 = ✅ 完了**、A-4 〜 A-7 = ⬜ 未着手

#### A-1 [P0] 参照等価依存の削除を Id ベースに
- **現状**: `ScanProcessor.DeleteRecord` は `allHistory.Remove(record)`、`MainViewModel.DeleteRecord` は `History.Remove(record)` を使用。`ScanRecord` は `Equals` をオーバーライドしていないため**参照等価**で削除される。
- **問題**: 参照が一致しない Record（例: 再ロード後のインスタンス、将来のスナップショット比較）では削除が黙って失敗する。成否も戻り値で分からない。
- **対応方針**:
  - `ScanRecord.Id` で一致する要素を削除するよう変更する。
  - `IScanProcessor.DeleteRecord` の戻り値を `bool`（削除できたか）に変更し、VM 側で失敗時に `_notificationService.Warning` を出す。
  - `ViewModel` の `History.Remove` も同様に Id ベースへ統一する。
- **変更候補**: `Services/IScanProcessor.cs`, `Services/ScanProcessor.cs`, `ViewModels/MainViewModel.cs`
- **受入基準**: 同一 Id を持つ別インスタンスを渡しても 1 件だけ削除される。存在しない Id では削除されず警告が出る。テストで担保する。
- **注意**: `IScanProcessor` は Lite 専用だが、`tests/Tenko.Tests` から呼ばれているためシグネチャ変更時はテストも更新すること。

#### A-2 [P0] bin と history の不整合検出
- **現状**: `ScanFileService.RemoveLast5` は `data.Length == 0 || data.Length % 2 != 0` のとき黙って return し、対象値が無い場合も黙って何もしない。戻り値は `void`。
- **問題**: `scans/ids_<場所>.bin` に同じ値が複数含まれる場合（「そのまま追記」で既存 bin に継続追記した場合など）、**最後に一致した 2 バイト**を消すため、削除対象の行と bin の対応がずれる可能性がある。また bin と history の件数がずれても検知できない。
- **対応方針**:
  - `RemoveLast5` を `bool` 返却に変更（実際に除去できたか）。
  - `ScanProcessor.DeleteRecord` は、bin から除去できなかった場合に `MainViewModel` へ警告を伝える（例: 履歴と BIN の不整合）。
  - 可能なら削除前に「bin 内の該当値の個数」と「history 内の該当値の件数」を比較し、不一致なら警告する。
- **変更候補**: `Services/ScanFileService.cs`, `Services/ScanProcessor.cs`, `ViewModels/MainViewModel.cs`
- **受入基準**: 対象が無い／壊れた bin でも例外を出さず false を返す（既存テスト `ScanFileService_HandlesCorruptedOrEmptyFileGracefully` を壊さない。`void` → `bool` の変更は戻り値を無視する既存呼び出しを壊さない）。不整合時に警告が出る。
- **禁止**: bin のバイト形式（リトルエンディアン UInt16 の連結）は変更しない（§7）。

#### A-3 [P0] サーバー削除伝播のバッチ化
- **現状**: `ScanProcessor.DeleteAllForLocation` は対象件数分ループして `PropagateServerDeletion(id)` を呼ぶ。`PropagateServerDeletion` は `ServerSyncService.RemovePendingRecord(id)` → 成功時に `SavePendingRecords()`（JSON 全件書き出し）、失敗時に `EnqueueDeletion(id)` → `SavePendingDeletions()`（JSON 全件書き出し）を**1 件ごとに**実行する。
- **問題**: N 件削除で最大 2N 回のファイル書き込みが発生する。ロケーションの全削除時に顕著な遅延とディスク負荷になる。
- **対応方針**:
  - `ServerSyncService` にバッチ API を追加する（例: `RemovePendingRecords(IEnumerable<string> ids)` と `EnqueueDeletions(IEnumerable<string> ids)`）。内部で `_lock` を 1 回取り、保存も最後に 1 回だけ行う。
  - `ScanProcessor.DeleteAllForLocation` は ID リストをまとめて 1 回で伝播する。
  - ステータス更新（`UpdateStatus`）も最後に 1 回にまとめる。
- **変更候補**: `Services/ServerSyncService.cs`, `Services/ScanProcessor.cs`
- **受入基準**: 既存の `sync_queue.json` / `sync_deletes.json` の形式を維持したまま、N 件削除時の保存呼び出しが定数回になる。
- **検証方法**: `SavePendingRecords` は private で回数を直接数えられないため、テストでは**バッチ API 呼び出し後の `sync_queue.json` / `sync_deletes.json` の内容**（対象 ID が除去／追加されていること）で検証する。保存回数を厳密に数えたい場合は、保存処理を `protected virtual` にする等の最小限のフック追加は許可する。
- **注意**: `SyncPendingAsync` / `FlushDeletionsAsync` の `SemaphoreSlim` による直列化は維持すること。`ServerSyncService` のテストには §8 の「実 HTTP 回避」の手順に従うこと。

#### A-4 [P1] 複数選択削除
- **現状**: `DataGrid` は `SelectionMode` 未指定（既定 = Single）。削除は行ごとの「削除」ボタンのみ。
- **対応方針**: `SelectionMode="Extended"` にし、選択行を一括削除するコマンドを追加する。`DataGrid.SelectedItems` はバインディング不可のため、`MainWindow.xaml.cs` のクリックハンドラから VM のコマンドへ `IList` を渡すか、添付ビヘイビアを実装する。
- **受入基準**: 複数行を選択して削除でき、削除件数の通知が出る。単一削除の既存挙動は維持。
- **注意**: 一括削除でも bin からの除去は 1 件ずつ必要（`RemoveLast5` は値を 1 つ消す仕様）。

#### A-5 [P1] 削除コマンドの CanExecute とフィードバック
- **現状**: `DeleteRecordCommand` / `DeleteAllCommand` は `RelayCommand` の `CanExecute` を渡しておらず常に活性。`DeleteAll` は `IsLocationSet` を手動チェックして警告を出すだけ。
- **対応方針**: `CanExecute` を使って活性制御し、実行時の無言 return を減らす。削除失敗時は `History` を変更しない（部分更新を避ける）。`RelayCommand` / `RelayCommand<T>` は既に省略可能な `canExecute` コンストラクタ引数を持つ（`Common/RelayCommand.cs`）ため、そのまま利用できる。
- **受入基準**: 履歴が空／場所未設定のとき削除が実行できない、または理由が通知される。

#### A-6 [P1] 削除後の `History` 更新方針の統一
- **現状**: `DeleteRecord` は `History.Remove(record)`（個別更新）、`DeleteAll` は `History.Clear()`、`CompleteRename` / `ExecuteRenameWarning` も `History.Clear()` と、方針が混在している。
- **対応方針**: 「削除後に `RefreshHistoryView()` で再構築」に統一するか、個別更新を維持するかを決めて統一する。`RefreshHistoryView` はフィルタ・場所を考慮するため、再構築のほうが事故が少ない。
- **受入基準**: 検索中・場所切替後でも表示と `_allHistory` が一致する。

#### A-7 [P2] 削除のアンドゥ
- **対応方針**: 直前 1 操作分の削除を取り消せるようにする（bin へ再追記、サーバー削除キューから除去）。設計難度が高い場合は見送り、その旨を報告に記載する。
- **受入基準**: 誤削除を 1 操作だけ戻せる。戻せない場合は仕様として明記。

---

### B. メモリ／リソースリーク対策

状態: **B-1 / B-2 / B-3 / B-6 = ✅ 完了**、B-4 / B-5 / B-7 = ⬜ 未着手（B-6 は P1 だが今回あわせて完了）

#### B-1 [P0] `MainWindow` の無条件 `Dispatcher.Invoke` をガード
- **現状**: `MainWindow.OnViewModelPropertyChanged` は `Dispatcher.Invoke(TryFocusManualInput)` を**無条件**で呼ぶ。`TryFocusManualInput` 自体も `_viewModel.PropertyChanged` 経由で呼ばれる。
- **問題**: 既に UI スレッド上でも毎回 Invoke を通る。ウィンドウ終了処理中に発火すると `TaskCanceledException` / `InvalidOperationException` になり得る。
- **対応方針**: `Dispatcher.CheckAccess()` と `Dispatcher.HasShutdownStarted` / `HasShutdownFinished` を確認してから実行する。`MainViewModel.RunOnUi` と同等のヘルパを共通化してよい。なお `RunOnUi` は `private static`（`MainViewModel.cs`）のため、共通化する場合は `internal static` ヘルパクラスに移すなど、`MainWindow.xaml.cs` から呼べる形にする。
- **受入基準**: 起動→モーダル開閉→終了が例外なく完了する。

#### B-2 [P0] `RunOnUi` にシャットダウンガードを追加
- **現状**: `MainViewModel.RunOnUi` は `Application.Current?.Dispatcher` を取得し、`CheckAccess()` でなければ `Invoke` する。ディスパッチャの停止状態は見ていない。
- **問題**: `ServerSyncService.OnStatusChanged` は `Task.Run` 側（バックグラウンド）から発火するため、アプリ終了と競合すると `Invoke` が例外になる。`Dispose()` で購読解除しているが、解除と発火の競合はゼロにはならない。
- **対応方針**: `HasShutdownStarted` 等を確認し、停止中は何もしない（または `BeginInvoke` を使い、戻り値を待たない）。※`BeginInvoke` は順序保証がないため、表示用途では許容。
- **受入基準**: 同期処理中にウィンドウを閉じても例外が出ない。

#### B-3 [P0] 送信キュー `_pendingRecords` の上限設定
- **現状**: `ServerSyncService._pendingRecords` / `_pendingDeletions` は無制限に増える。`sync_queue.json` も同様に増大する。
- **問題**: 学内ネットワーク不通時にスキャンし続けると、メモリと JSON 書き込みコストが増大し続ける。
- **対応方針**: 上限（例: 5000 件）を設ける。超過時は古いものから破棄する（あるいは新規受付を止める）方針を決め、`UpdateStatus` に「未送信（上限超過）」を表示する。破棄する場合は `Debug.WriteLine` で記録する。
- **受入基準**: 上限を超えてもアプリが落ちず、ステータスに状態が出る。復旧時に上限内のレコードが送信される。
- **注意**: `sync_queue.json` の形式（`List<ScanRecord>`）は変更しない（§7）。テストは §8 の「実 HTTP 回避」に従うこと。

#### B-4 [P1] `_allHistory` の全件常駐と毎回全走査の改善
- **現状**: `MainViewModel.LoadHistory` は `history.json` を**全場所分**メモリに読み込む。`RefreshHistoryView` は毎回 `_allHistory` を全走査して `CurrentLocation` と検索語でフィルタする。`ScanProcessor.ProcessScan` の重複判定も `allHistory.Any(...)` で O(n) 全走査。
- **問題**: 履歴が増えると起動・検索・スキャンが線形に遅くなる。
- **対応方針（いずれか、安全な順）**:
  1. `CurrentLocation` ごとのインデックス（`Dictionary<string, List<ScanRecord>>`）を保持し、`RefreshHistoryView` の全走査をなくす。
  2. 重複判定を「ロケーション別の `HashSet<ushort>`」で行う（ただし `_allHistory` は VM が `Insert`/`RemoveAll` するため、インデックス同期の責務を `ScanProcessor` 側に寄せるか、`allHistory` を渡す設計をやめる必要がある。設計変更の範囲を小さく保つこと）。
- **禁止**: `history.json` の形式・分割は変更しない（§7）。**メモリ上のみ**の最適化にすること。
- **受入基準**: 既存テスト合格。10,000 件程度の履歴でも検索が体感的に遅くならない。

#### B-5 [P1] `IsRecentlyAdded` のリセット
- **現状**: `MainViewModel.SubmitManualInput` は `result.Record.IsRecentlyAdded = true;` を設定するが、**false に戻す処理が無い**。XAML の `DataGridRow` の `DataTrigger`（`IsRecentlyAdded == True`）でフェードアニメを起動する。
- **問題**: 検索入力や場所変更で `RefreshHistoryView` が `History` を作り直すたびに、過去に追加された全行で `BeginStoryboard` が**再再生**される（見た目のバグ + Storyboard の再確保）。
- **対応方針**: アニメ完了時にフラグを戻す。実装例:
  - `MainWindow.xaml.cs` で行のアニメ `Completed` を拾って `ScanRecord.IsRecentlyAdded = false` に戻す。
  - あるいは `MainViewModel` 側で一定時間後に戻す（`IClockService` 注入など、テスト可能な形が望ましい）。
- **受入基準**: 新規行のみが一度だけハイライトされる。検索しても再ハイライトされない。

#### B-6 [P1] Dispose の回帰テスト追加
- **対応方針**: `MainViewModel.Dispose()` が購読解除とタイマー停止を行うことをテストで保証する。例:
  - `Dispose()` 後に `MockClockService.TriggerTick()` を呼んでも `CurrentTimeString` が変化しないこと。
  - `Dispose()` 後に `notificationService.Success("x")` を呼んでも `NotificationMessage` が変化しないこと。
  - `Dispose()` を 2 回呼んでも例外にならないこと。
- **ファイル**: `tests/Tenko.Tests/UnitTest1.cs`（既存の `MockClockService` / `MockDialogService` を再利用）。
- **注意**: テストスレッドでは `Application.Current` が null のため、`RunOnUi` は即時実行される（`dispatcher == null` 分岐）。この前提で上記テストは成立する。
- **受入基準**: 追加テストが合格し、`Dispose` を外すと失敗する（= 実効性がある）。

#### B-7 [P2] `SaveHistory` の O(n²) 書き込み対策
- **現状**: `ScanProcessor.ProcessScan` は 1 スキャンごとに `HistoryService.SaveHistory` で**履歴全件**を JSON 書き出しする（1 セッションで O(n²)）。
- **対応方針**: 形式を維持したまま、デバウンス保存（例: 500ms まとめ書き）や追記ジャーナル + 定期コンパクト化を検討する。クラッシュ時のデータ保全とトレードオフになるため、実装するなら「終了時・操作確定時に必ずフラッシュ」を保証すること。
- **禁止**: `history.json` の最終形式は変更しない（§7）。見送る場合は報告に理由を書く。

---

### C. UI・操作性の改善

状態: **C-1 〜 C-4 = ✅ 完了**、C-5 〜 C-12 = ⬜ 未着手

#### C-1 [P0] 「累計」が絞り込みに追随してしまう
- **現状**: `MainWindow.xaml` の累計は `{Binding History.Count}`。`History` は**フィルタ結果**のコレクション（`RefreshHistoryView` が検索語で絞り込む）。
- **問題**: 検索すると「累計」の数字が減り、点呼の進捗として誤読される。
- **対応方針**: `MainViewModel` に現在場所の総件数 `CurrentLocationCount`（例: `_allHistory.Count(h => h.Location == CurrentLocation)`）を追加し、累計はそれを表示する。絞り込み中の件数は別ラベル（例: `絞り込み中 n 件`）に分離する。`History` 変更時に `OnPropertyChanged(nameof(CurrentLocationCount))` を発火させること。
- **受入基準**: 検索しても累計が変わらない。絞り込み件数は別途確認できる。テストで担保する。

#### C-2 [P0] 通知の色分けが未配線
- **現状**: `MainWindow.xaml` は `<local:NotificationTypeToBrushConverter x:Key="TypeToBrush" />` を定義しているが、**どこからも参照されていない**（死にリソース）。ステータスバーのメッセージは
  `<TextBlock Text="{Binding NotificationMessage}" VerticalAlignment="Center"/>` で色指定なし。
- **問題**: `Success` / `Warning` / `Error` の区別が視覚的に分からない。
- **対応方針**: ステータスバーの `NotificationMessage` の `Foreground` に `TypeToBrush` を適用する（`NotificationType` にバインド）。`MainViewModel.NotificationType` は `SetProperty` 経由で変更通知される。
- **受入基準**: 成功は緑、警告はオレンジ、エラーは赤で表示される。`TypeToBrush` が未使用のままにならない。

#### C-3 [P0] 出力先がカレントディレクトリ
- **現状**: `MainViewModel.ExportCsv/ExportBin` は `_exportService.ExportCsv(CurrentLocation, History)` と `outputDir` を渡さない。`ExportService` は `outputDir` が null のとき `fileName` のみを使うため、**プロセスのカレントディレクトリ**に書き込む。
- **問題**: 単一ファイル配布で `Program Files` 配下や書込不可の作業ディレクトリで起動した場合、例外になる（あるいは利用者がファイルを見つけられない）。
- **対応方針**: ユーザー書込可能な既定出力先（例: `%USERPROFILE%\Documents\Tenko出力`、または `AppContext.BaseDirectory\exports`）を導入し、通知に**絶対パス**を表示する。ディレクトリが無ければ作成する。なお `ExportService.ExportCsv/ExportBin` は既に省略可能な `outputDir` 引数を持つ（VM から渡されていないだけ）。
- **受入基準**: 出力が必ず成功し、通知から保存先が分かる。テストは `new ExportService().ExportCsv("場所", records, 一時ディレクトリ)` のように一時ディレクトリを渡して直接検証できる。

#### C-4 [P0] CSV ヘッダ誤り
- **現状**: `ExportService.ExportCsv` はヘッダ `"Timestamp,ID"` を書き、データ行は `$"{r.FormattedTimestamp},{r.Last5:D5}"`。
- **問題**: ヘッダの `ID` は実際の `Last5`（学籍番号下 5 桁）と異なる。提出物として誤解を招く。
- **対応方針**: ヘッダを実データに合わせる（例: `時刻,学籍番号`）。Excel で日本語ヘッダを正しく開けるよう UTF-8 BOM の付与も検討する。
- **受入基準**: ヘッダとデータの意味が一致する。既存テスト `ExportService_ExportsCsvAndBin` を更新して検証する。

#### C-5 [P1] 出力対象が絞り込み結果／無言 return
- **現状**: `ExportCsv/ExportBin` は `if (History.Count == 0) return;` で無言終了し、渡すコレクションは**フィルタ済み**の `History`。ファイル名は `CurrentLocation` 基準。
- **問題**: 絞り込み中に出力すると一部だけが出る／空のとき押しても何も起きず理由が分からない。
- **対応方針**: 方針を決めて統一する。推奨は「現在場所の全件を出力」。絞り込み中に押した場合は「絞り込み中でも全件出力されます」と通知する。空の場合は `CanExecute` で無効化するか通知する。なお `ExportService` は空コレクションを渡されると `InvalidOperationException` を投げるため、VM 側の空チェックは必ず先に行うこと。
- **受入基準**: 出力件数が期待どおりで、無反応にならない。

#### C-6 [P1] モーダルを Esc/Enter で操作できない
- **現状**: モーダルは `Window` ではなく `Border` のオーバーレイ（`Grid.RowSpan="3"`）。Esc で閉じられない。
- **対応方針**: `MainWindow` の `PreviewKeyDown` で Esc → 開いているモーダルを閉じる、Enter → 主ボタン（点呼を始める／保存して完了 など）を実行する。フォーカスがモーダル内にある場合のみ作用させる。
- **受入基準**: キーボードだけで点呼が完結する（スキャナ運用で重要）。

#### C-7 [P1] 設定モーダル表示中に場所変更で警告モーダルが裏に隠れる
- **現状**: `MainViewModel.CurrentLocation` セッターが `CheckBinFile()` を呼び、既存 bin があれば `ShowBinWarning = true` にする。設定モーダルにも `CurrentLocation` にバインドした `ComboBox` がある。`Panel.ZIndex` は 警告=2000、設定=3000。
- **問題**: 設定モーダルを開いたまま場所を変更すると、警告モーダルが設定モーダルの**背後**に生成され、操作できずフォーカス管理も乱れる。
- **対応方針**: 設定モーダル表示中は `CheckBinFile()` を抑止して、設定を閉じた後に判定する。あるいは警告の ZIndex を設定より前にする。どちらでもよいが、状態遷移が破綻しないことを確認する。
- **受入基準**: どの順序で操作してもモーダルが取り残されない。

#### C-8 [P1] ウィンドウ／視認性
- **現状**: `Window` は `Height="600" Width="800"` のみで `MinWidth`/`MinHeight`/`WindowStartupLocation` 未指定。`App.xaml` の既定フォントサイズは 12（一部 10〜11）。
- **対応方針**: 最小サイズを設定し、起動位置を中央にする。点呼用 PC での視認性のため、主要ラベル・通知のフォントサイズを見直す。`DataGrid` の交互行色（`AlternatingRowBackground` 等）を検討。ウィンドウサイズの記憶は任意（settings.json 形式を変える場合は §7 に注意）。
- **受入基準**: 小さい画面でも操作要素が欠けない。

#### C-9 [P1] 文言・エラーメッセージ
- **現状**: `_notificationService.Error($"削除失敗: {ex.Message}")` のように技術的な例外メッセージをそのまま表示する箇所がある。
- **対応方針**: 利用者向けの日本語メッセージを表示し、詳細は `Debug.WriteLine` に出す。文言の表記ゆれ（「累計」「絞り込み中」など）を統一する。
- **受入基準**: 例外の生メッセージが UI に直接出ない。

#### C-10 [P1] 重複スキャン時の明示
- **現状**: 重複はステータスバーの警告（2 秒で消える）と入力欄の赤フラッシュのみ。
- **対応方針**: 「重複」であることが判別できる表示（入力欄の背景色、重複バッジ、より長い表示時間など）にする。`NotificationService.Warning` を利用する場合は §C-2 の色分けと合わせる。
- **受入基準**: 重複スキャンが一目で分かる。

#### C-11 [P2] 場所の追加・編集
- **現状**: `Locations` は `data/locations.json` か埋め込み値のみ。実行時に追加・編集できない。
- **対応方針**: 設定モーダルに追加／削除 UI を設け、`locations.json`（`string[]`）へ保存する。形式は維持する（§7）。
- **受入基準**: 追加した場所が `ComboBox` に出て、再起動後も残る。

#### C-12 [P2] 空状態表示・並べ替え
- **対応方針**: 履歴が 0 件のときのプレースホルダ表示、`DataGrid` の列ソート許可、現在の絞り込み件数表示。
- **受入基準**: 空でも画面が分かりやすい。

---

### D. 不具合・堅牢性

状態: **D-1 / D-2 = ✅ 完了**、D-3 〜 D-7 = ⬜ 未着手

#### D-1 [P0] `ProcessScan` の保存順序
- **現状**: `allHistory.Insert` → `SaveHistory()` → `AppendLast5()` の順。
- **問題**: `AppendLast5` が失敗すると `history.json` だけ更新され、bin と不整合になる（逆も同様に回避したい）。
- **対応方針**: 失敗時にロールバックする（`allHistory` から除去し `SaveHistory` をやり直す）か、bin 追記を先にして失敗時に履歴へ入れない。どちらでもよいが結果の整合を保証し、ユーザーへ通知する。
- **受入基準**: 片方の書き込みが失敗しても不整合が残らない、または不整合が明示される。

#### D-2 [P0] `SubmitManualInput` が例外を捕捉していない
- **現状**: `MainViewModel.SubmitManualInput` は `_scanProcessor.ProcessScan` を try/catch なしで呼ぶ。`ProcessScan` 内部の `SaveHistory` / `AppendLast5` はファイル I/O を行い、例外を投げ得る。他の操作（削除・出力・リネーム）は try/catch 済みで不整合。
- **問題**: ディスク満杯・権限不足などで UI スレッドの未処理例外となりアプリが落ちる。
- **対応方針**: `SubmitManualInput` を try/catch し、`_notificationService.Error` で通知する。入力値（`ManualInput`）は失敗時に消さない（再試行できるように）。
- **受入基準**: 書き込み失敗時もアプリが落ちず、原因が通知される。
- **テスト方法**: Windows ではディレクトリの読み取り専用属性だけでは書込失敗を安定して再現できない。`IScanProcessor` を実装した「`ProcessScan` で例外を投げるスタブ」を作り、`MainViewModel` に注入して検証する（`MainViewModel` は `IScanProcessor` 経由なので容易）。

#### D-3 [P1] デバウンスが単一スロット
- **現状**: Lite の `ScanProcessor` は `_lastScannedNumber` / `_lastScannedAt` の**単一スロット**で判定。`Tenko.Native` 側は `Dictionary<ushort, DateTime>` + 古いキーのクリーンアップで**番号ごと**に判定。
- **問題**: A→B→A の順でスキャンすると、A はデバウンスされず重複警告（`DuplicateWarning`）になる。挙動が Native と非対称。
- **対応方針**: Lite も番号単位のデバウンスに統一するか、単一スロットのままで意図をコメントに明記する。統一する場合は辞書の肥大化対策（古いキーの削除）も Native と同様に実装する。
- **受入基準**: 仕様が一貫し、コメントで説明されている。テストで挙動を固定する。

#### D-4 [P1] `RenameLocationBin` のサニタイズ・同名衝突
- **現状**: `newName` の不正文字を `_` に置換するのみ。空白のみの入力や、末尾が空白・ドットの名前は Windows で問題になり得る。`StorageService.Move` は `File.Move(source, dest)`（overwrite なし）なので、同名ファイルが既存だと例外。
- **対応方針**: 空・空白のみは既定名にフォールバックする。末尾の空白・ドットを除去する。既存と衝突する場合は連番を付与する（または上書き確認をする）。
- **受入基準**: どのような入力でも例外にならず、意図した名前で保存される。

#### D-5 [P1] 保存場所が `Locations` に無い場合
- **現状**: `MainViewModel` コンストラクタは `_settingsService.Location` をそのまま `_currentLocation` に入れ、`Locations` は `settingsService.Locations` から作る。両者が不一致だと `ComboBox` の `SelectedItem` が一致せず空表示になる。
- **対応方針**: 読み込み時に `Location` が `Locations` に無ければ追加する、または先頭にフォールバックする。
- **受入基準**: 設定ファイル不整合でも現在場所が表示される。

#### D-6 [P2] 30 秒タイマーの空キュー時早期 return
- **現状**: `ServerSyncService` の `_retryTimer`（30 秒）は毎回 `SyncPendingAsync` と `FlushDeletionsAsync` を呼ぶ。両者はキューが空なら内部で早期 return するが、`SemaphoreSlim` の取得とロックは発生する。
- **対応方針**: タイマー側で `PendingCount == 0 && PendingDeletionCount == 0` なら何もしない。
- **受入基準**: 空キュー時に無駄な処理が走らない。

#### D-7 [P2] 例外メッセージの統一
- §C-9 と同一。詳細は `Debug.WriteLine`、UI は日本語の要約。

---

## 7. 変更禁止（互換性クリティカル）

以下は外部ツール・提出物・サーバーと共有しているため、**事前合意なしに変更しないこと**。

1. **`kunugidasainotenko/scans/ids_<場所>.bin`**
   - リトルエンディアン `UInt16` を連結したバイナリ。**提出用フォーマット**（README に「できた `ids_` から始まるファイルを提出」と記載）。並び・エンディアン・要素サイズを変更しない。`students.enc` の同梱先（csproj の `<Link>`）もこの親フォルダ配下である。
2. **`data/history.json`**
   - `[{ "Id": string, "Timestamp": ISO 日時, "Barcode": string, "Last5": ushort, "Location": string }]`
   - デシリアライズは `PropertyNameCaseInsensitive = true`（`JsonHelper`）。**既存プロパティの改名・削除は不可**（追加は可）。
   - `ScanRecord.IsRecentlyAdded` は `[JsonIgnore]`。永続化しない。
3. **`data/settings.json`**: `{ "Location": string }`
4. **`data/locations.json`**: `string[]`
5. **埋め込み生成スクリプトの契約**
   - `tools/Generate-EmbeddedLocations.ps1`（入力 `data/locations.json` → `Tenko.Lite.Generated.EmbeddedLocations`）
   - `tools/Generate-EmbeddedServerConfig.ps1`（入力 `data/server.json` → `Tenko.Lite.Generated.EmbeddedServerConfig`）
   - 生成物の API（`EmbeddedLocations.GetLocations()` / `EmbeddedServerConfig.IsEnabled`, `ServerUrl`, `ClientId`, `ApiKey`）を変更しない。
6. **サーバー同期プロトコル**
   - `POST {ServerUrl}/api/v1/scans`
     body: `{ "clientId": string, "records": [ { "id", "timestamp": "yyyy-MM-ddTHH:mm:ss", "barcode", "last5", "location" } ] }`
     header: `X-API-Key`（空でなければ付与）
   - `POST {ServerUrl}/api/v1/scans/delete`
     body: `{ "clientId": string, "ids": [string] }`
   - サーバー実装は `src/TenkoServer`。**サーバー側と同時に変更しない限り不可**。
   - タイムスタンプ書式 `yyyy-MM-ddTHH:mm:ss` は固定。
7. **`data/sync_queue.json`**（`List<ScanRecord>`）/ **`data/sync_deletes.json`**（`List<string>`）
   - 形式を変更しない（B-3 で上限を設けても形式は維持）。
8. **親フォルダ名 `kunugidasainotenko`**
   - `StorageService.RootFolderName`。実行時データの配置先であり、変更すると既存端末の `data/` `scans/` が見えなくなる（移行処理は行わない方針）。変更しない。
   - 同梱の `students.enc` の配置先は `Tenko.Native.csproj` / `ScanViewer.csproj` / `tests/Tenko.Tests/Tenko.Tests.csproj` の `<Link>` と連動するため、片方だけ変更しないこと。

---

## 8. テスト方針

### テストファイルの構成

- `tests/Tenko.Tests/UnitTest1.cs` … Native / Lite 用（**新規テストはこのファイルの `TenkoTests` クラスに `[Fact]` で追加する。新しいテストファイルは作らない**）
- `tests/Tenko.Tests/ScanViewerTests.cs` … ScanViewer 用（触らない）
- `tests/Tenko.Tests/TenkoServerTests.cs` … サーバー用（触らない）
- モックは `MockClockService` / `MockDialogService` が **Native と Lite の両インターフェースを実装**しているので再利用できる。

### ストレージを使うテストの定型

`StorageService` は `new Tenko.Lite.Services.StorageService(baseDir)` で基準ディレクトリを注入できる（`<baseDir>/kunugidasainotenko/{data,scans}` が作られる）。新規テストでは**必ず一意の一時ディレクトリを渡し**、後始末する:

```csharp
string baseDir = Path.Combine(Path.GetTempPath(), "TenkoTests_" + Guid.NewGuid().ToString("N"));
try
{
    var storage = new Tenko.Lite.Services.StorageService(baseDir);
    // ... テスト本体 ...
}
finally
{
    if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
}
```

注意点（実コードとの食い違いに惑わされないこと）:

- **引数なしの `new StorageService()` は使わない。** テスト出力ディレクトリ直下に `kunugidasainotenko/` を作って実書き込みする。既存テストにこの使い方が残っているが、新規テストでは真似しない。
- `TenkoTests` クラスの `_testDir` は作成・削除されるだけで、**現在どのテストからも `StorageService` に渡されていない**。「既存クラスを踏襲」とは `[Fact]` の追加先と Dispose パターンのことであり、`_testDir` が自動的に使われるわけではない。

### MainViewModel のコンストラクタ（テストで使うシグネチャ）

```csharp
new Tenko.Lite.ViewModels.MainViewModel(
    IScanProcessor scanProcessor,            // new ScanProcessor(historyService, scanFileService) またはスタブ
    SettingsService settingsService,         // new SettingsService(storage)
    NotificationService notificationService, // new NotificationService()
    IClockService clockService,              // MockClockService
    IExportService exportService,            // new ExportService()
    IDialogService dialogService,            // MockDialogService
    ServerSyncService? serverSyncService = null) // 省略可
```

### ServerSyncService のテスト: 実 HTTP 回避（重要）

- `EmbeddedServerConfig.IsEnabled` / `ServerUrl` 等は**ビルド時に `data/server.json` から埋め込まれる**。現在の `data/server.json` は `enabled: true` で実 URL が設定されている。
- そのため実 `HttpClient` のまま `EnqueueRecord` / `EnqueueDeletion` を呼ぶと、**実サーバーへ POST を試行する**（5 秒タイムアウト。失敗は `Debug.WriteLine` に吸収されるが、テストが遅く・不安定になる）。
- テストでは必ず偽の `HttpMessageHandler` を注入する:

```csharp
private sealed class FakeHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
}

// 利用例
var sync = new Tenko.Lite.Services.ServerSyncService(new HttpClient(new FakeHttpHandler()), persistFilePath);
```

- `_retryTimer`（30 秒）はテストスレッドでは発火しない。同期の検証は `SyncPendingAsync()` / `FlushDeletionsAsync()` を直接 `await` して行う。

### 追加すべきテスト（対応するタスク）

- 削除が Id ベースで 1 件のみ（A-1）／存在しない Id（A-1）
- bin 不整合時に警告（A-2）／壊れた bin で false（A-2）
- サーバー削除バッチ後の `sync_queue.json` / `sync_deletes.json` の内容（A-3）
- `Dispose` 後にイベントが作用しない（B-6）
- 「累計」= 現在場所の全件で、絞り込みに影響されない（C-1）
- 出力先が書込可能ディレクトリで、CSV ヘッダが正しい（C-3 / C-4）
- 出力が場所の全件を対象にする（C-5）
- 例外を投げる `IScanProcessor` スタブで `SubmitManualInput` が落ちず通知する（D-2）
- デバウンス挙動の仕様固定（D-3）
- **UI 変更は自動テストが難しいため、可能なら `dotnet run --project src/Tenko.Lite/Tenko.Lite.csproj -c Debug` で起動して目視確認する。**

---

## 9. 実装上の注意

- **コメントは日本語**で、既存のコメントスタイル（1 行で目的を説明）に合わせる。
- **C# のスタイル**: `src/` 配下はブロックスコープ namespace（`namespace X { ... }`）、`tests/` はファイルスコープ namespace（`namespace Tenko.Tests;`）。編集するファイルの既存スタイルに合わせる。新規ファイルは同じディレクトリの既存ファイルに合わせる。
- `Nullable` 有効（`<Nullable>enable</Nullable>`）。null を取りうる引数・戻り値には `?` を付ける。
- 既存の設計方針（MVVM、Service 分割、`JsonHelper` 経由の JSON）を崩さない。
- 過剰な抽象化・将来の要件の先回り実装をしない。タスクに必要な最小の変更に留める。
- **新規 NuGet パッケージは追加しない**（既存: `Microsoft.Extensions.DependencyInjection` のみ）。
- `Tenko.Native` / `ScanViewer` は本指示書のスコープ外。ただし同じ不具合が存在し得るため、Lite の挙動を変えた場合は報告に「Native 未対応」である旨を明記する。
- ビルドの `[Generate-Embedded*]` 警告 4 件は正常。消そうとしない。

---

## 10. 参考: 既知の死にコード（任意で整理）

- `MainWindow.xaml` の `TypeToBrush`（未使用 → §C-2 で配線する）
- `MainWindow.xaml` の `FlashRedStoryboard` は `MainWindow.xaml.cs` から使用中（削除しない）
- `ScanResult.Failed`（Lite 内で未使用。`IScanProcessor` の公開 API なので削除は任意）
- `IDialogService.ShowMessage`（Lite 内で未使用。削除は任意）

---

## 11. コミット方針

- **commit / push は指示された場合のみ**行う。1 タスク 1 コミット程度の粒度とし、巨大なリファクタは避ける。
- プレフィックスの例: `fix:` / `perf:` / `refactor:` / `feat:` / `test:`
- メッセージは日本語。本文に変更点を箇条書きする。
- 末尾に以下の trailer を必ず付ける:

```
Co-authored-by: CommandCodeBot <noreply@commandcode.ai>
```

- 例:

```
fix: Tenko.Liteの削除処理をIdベース化しBIN不整合を検出

- ScanProcessor.DeleteRecordをId一致で1件削除し、成否を返すよう変更
- ScanFileService.RemoveLast5をbool返却に変更し、除去失敗時に警告
- サーバー削除伝播をバッチ化しJSON書き出しを定数回に削減
- 上記に対するxUnitテストを追加

Co-authored-by: CommandCodeBot <noreply@commandcode.ai>
```
