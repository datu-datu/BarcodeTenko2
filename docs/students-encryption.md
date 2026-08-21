# students.enc / students.passphrase 運用手順

## 前提
- 学生マスタは **実行時** に `data\students.enc` を復号して読み込む。
- 復号鍵は **ビルド時** に `data\students.passphrase` から読み込み、実行ファイルへ埋め込む。
- 実行時に `students.passphrase` は参照しない。
- 基準パスは `AppDomain.CurrentDomain.BaseDirectory`。

## どのファイルが使われるか

| ファイル | 役割 | 参照タイミング |
| --- | --- | --- |
| `data\students.csv` | 暗号化の元データ | 暗号化スクリプト実行時のみ |
| `data\students.enc` | 暗号化済みマスタ | アプリ起動時に読み込む |
| `data\students.passphrase` | 復号鍵 | **ビルド時**に読み込んで埋め込む |

## 実行時の参照先（フォルダ階層）

### `dotnet run` / VSデバッグ時
実行ファイルの場所は `bin\Debug\net8.0-windows\win-x64\`。

```
<repo>\bin\Debug\net8.0-windows\win-x64\
  Tenko.Native.exe
  data\
    students.enc
```

### `dotnet publish -c Release` 時
出力先は `bin\Release\net8.0-windows\win-x64\publish\`。

```
<repo>\bin\Release\net8.0-windows\win-x64\publish\
  Tenko.Native.exe
  data\
    students.enc
```

> 注意: `students.enc` は **ビルド/パブリッシュ時に自動コピー**される。  
> `students.passphrase` は **埋め込みのみ**で、実行フォルダへ配置しない。

## 手順

### 1. 鍵ファイルを作成
`data\students.passphrase` に1行で鍵を書く。

```
<repo>\data\students.passphrase
```

### 2. 暗号化ファイルを生成
プロジェクトルートで実行。

```
.\tools\Encrypt-StudentsCsv.ps1
```

生成物:
- `<repo>\data\students.enc`

### 3. ビルド/パブリッシュ
ビルド時に `students.passphrase` が埋め込まれる。  
出力先に `students.enc` が自動コピーされる。

## 典型的な誤解

- **誤**: 実行時に `students.passphrase` が必要  
  **正**: ビルド時に埋め込まれるため、配布不要

- **誤**: `data\students.csv` がそのまま使われる  
  **正**: 実行時は `students.enc` のみ読む


 - ビルド時埋め込み: Tenko.Native.csproj の Target GenerateEmbeddedStudentsPassphrase   - tools\Generate-EmbeddedPassphrase.ps1 を実行し、EmbeddedStudentsPassphrase.g.cs を生成
 - 実行時の参照: Services\StudentService.cs の LoadStudents()   - EmbeddedStudentsPassphrase.GetPassphrase() を呼び出して埋め込み済み鍵を取得 