# MKV Bitrate Changer

MKV ファイルの映像ビットレートを変更する Windows 向け WPF アプリです。

FFmpeg を使って映像を再エンコードし、音声と字幕はそのままコピーします。複数の MKV ファイルをキューへ追加し、一覧の上から順に変換できます。

## 主な機能

- 複数の MKV ファイルを選択して順次変換
- ファイル選択とドラッグ＆ドロップによるキューへの追加
- キュー内の選択項目削除と全項目削除
- 出力フォルダーの指定
- CRF 方式による可変ビットレート変換
- kbps 指定によるビットレート変換
- H.264 / H.265 の選択
- `fast` / `medium` / `slow` / `veryslow` プリセットの選択
- 変換速度を抑えて他のアプリを優先する低負荷モード
- ファイルごとの進捗、変換速度、ファイルサイズの表示
- FFmpeg 実行ログの表示
- FFmpeg が見つからない場合の winget インストール案内

## 動作環境

- Windows 10 以降
- FFmpeg

配布用 exe は self-contained single-file publish で作成するため、通常は .NET Runtime を別途インストールする必要はありません。

FFmpeg は exe に同梱していません。PC に FFmpeg がない場合、アプリから winget によるインストールを実行できます。

## 使い方

1. `ChangeBitRateFroMkv.exe` を起動します。
2. `ファイルを追加` から、変換する `.mkv` ファイルを1つ以上選択します。
3. 必要に応じて、追加の MKV ファイルを一覧へドラッグ＆ドロップします。
4. `出力フォルダー` の `選択` から保存先を指定します。
5. 変換モードを選択します。
6. 必要に応じて H.265 または低負荷モードを有効にします。
7. `順次変換を開始` を押します。

ファイルは入力一覧の上から順に変換されます。途中のファイルで変換に失敗した場合も、残りのファイルは続けて処理されます。

### 変換モード

- `可変ビットレート / CRF方式`: CRF 値を品質基準として変換します。値が小さいほど高品質・大容量になります。指定範囲は 0～51 です。
- `固定ビットレート方式`: 映像ビットレートを kbps 単位の正の整数で指定します。
- `H.265 / HEVCで圧縮する`: 通常の H.264 の代わりに H.265 で変換します。圧縮効率が高い一方、変換時間が長くなることがあります。

### 低負荷モード

`低負荷モード（変換速度を落として他のアプリを優先）` を有効にすると、次の設定で FFmpeg を実行します。

- エンコードに使うCPUスレッド数を、論理プロセッサ数のおよそ4分の1に制限
- FFmpeg プロセスの優先度を `通常以下` に設定

このアプリの H.264 / H.265 変換はCPUエンコーダーを使用します。低負荷モードはGPU使用量を直接制限する機能ではなく、ゲームやVRアプリなどへCPU時間を譲り、同時使用時の負荷を抑えるための設定です。

## 出力ファイル

出力ファイル名は、入力ファイル名を基に自動生成されます。

~~~text
入力: example.mkv
出力: example_converted.mkv
~~~

異なるフォルダーから同名の入力ファイルを追加した場合は、同じ処理内で出力が重ならないよう連番が付きます。

~~~text
example_converted.mkv
example_converted_2.mkv
~~~

既存の出力ファイルと同名になった場合は、FFmpeg の `-y` オプションによって上書きされます。

## exe 単体で配布する

プロジェクトルートで次のコマンドを実行します。

~~~powershell
dotnet publish .\ChangeBitRateFroMkv\ChangeBitRateFroMkv.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish-single
~~~

生成される配布用 exe:

~~~text
publish-single\ChangeBitRateFroMkv.exe
~~~

`publish-single` フォルダー内の `ChangeBitRateFroMkv.exe` だけで起動できます。

## 開発

### ビルド

~~~powershell
dotnet build .\ChangeBitRateFroMkv\ChangeBitRateFroMkv.csproj -c Release
~~~

### 実行

~~~powershell
dotnet run --project .\ChangeBitRateFroMkv\ChangeBitRateFroMkv.csproj
~~~

## 注意事項

- 入力ファイルは `.mkv` のみ選択できます。
- 入力一覧へ同じファイルを重複して追加することはできません。
- 音声と字幕は再エンコードせず、そのままコピーします。
- H.265 は H.264 より変換に時間がかかることがあります。
- 低負荷モードでは通常より変換時間が長くなります。
- FFmpeg のインストール後に検出できない場合は、アプリを再起動してください。
