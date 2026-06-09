# MKV Bitrate Changer

MKV ファイルの映像ビットレートを変更する Windows 向け WPF アプリです。

FFmpeg を使って映像を再エンコードし、音声と字幕はそのままコピーします。

## 主な機能

- MKV ファイルの入力と出力先の選択
- CRF 方式による可変ビットレート変換
- kbps 指定による固定ビットレート変換
- H.264 / H.265 の選択
- `fast` / `medium` / `slow` / `veryslow` プリセットの選択
- 変換前後のファイルサイズ表示
- FFmpeg 実行ログの表示
- FFmpeg が見つからない場合の winget インストール案内

## 動作環境

- Windows 10 以降
- FFmpeg

配布用 exe は self-contained single-file publish で作成するため、通常は .NET Runtime を別途インストールする必要はありません。

FFmpeg は exe に同梱していません。PC に FFmpeg がない場合、アプリ起動後に winget でのインストールを案内します。

## 使い方

1. `ChangeBitRateFroMkv.exe` を起動します。
2. `入力MKV` の `選択` から変換したい `.mkv` ファイルを選びます。
3. `出力MKV` の `保存先` から出力ファイル名を指定します。
4. 変換モードを選びます。
   - `可変ビットレート / CRF方式`: CRF 値で品質基準の変換をします。小さい値ほど高品質・大容量になります。
   - `固定ビットレート方式`: 映像ビットレートを kbps で指定します。
5. 必要に応じて `H.265 / HEVCで圧縮する` を有効にします。
6. `変換開始` を押します。

## exe 単体で配布する

プロジェクトルートで次のコマンドを実行します。

```powershell
dotnet publish .\ChangeBitRateFroMkv\ChangeBitRateFroMkv.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish-single
```

生成される配布用 exe:

```text
publish-single\ChangeBitRateFroMkv.exe
```

`publish-single` フォルダ内の `ChangeBitRateFroMkv.exe` だけで起動できます。

## 開発

### ビルド

```powershell
dotnet build .\ChangeBitRateFroMkv\ChangeBitRateFroMkv.csproj -c Release
```

### 実行

```powershell
dotnet run --project .\ChangeBitRateFroMkv\ChangeBitRateFroMkv.csproj
```

## 注意事項

- 入力ファイルは `.mkv` のみ選択できます。
- 既存の出力ファイルがある場合は FFmpeg の `-y` により上書きされます。
- H.265 を使う場合、H.264 より変換に時間がかかることがあります。
- FFmpeg のインストール後に検出できない場合は、アプリを再起動してください。
