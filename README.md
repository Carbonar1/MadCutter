# MadCutter

音MAD 素材切り出しツール。動画/音声 → 任意区間を切り出して MP3/WAV/AAC/OGG で保存。

## 構成
- C# WPF (.NET 8) + WebView2
- フロント: wavesurfer.js v7 (ESM) + Regions / Timeline プラグイン
- デコード/エンコード: ffmpeg.exe (同梱, gyan.dev essentials v9.0.2)

## セットアップ

1. ffmpeg は `ffmpeg/ffmpeg.zip` として同梱 (サイズ対策)。初回起動時に自動で `ffmpeg/ffmpeg.exe` へ展開される。手動で配置してもよい (gyan.dev essentials build)
2. wavesurfer v7 ESM ファイルを `web/` に配置 (同梱済)
   - `web/wavesurfer.esm.js`
   - `web/plugins/regions.esm.js`
   - `web/plugins/timeline.esm.js`
3. ビルド
   ```
   dotnet build -c Release
   ```
4. 起動: `bin/Release/net8.0-windows/MadCutter.exe`

## 操作

### マウス
- ドラッグ&ドロップ or 「ファイルを開く」でメディア読込
- 波形クリック: シーク
- Shift+クリック: IN セット
- Alt+クリック: OUT セット
- 波形上ホイール: ズーム
- 区間の端ドラッグ: IN/OUT リサイズ

### キーボード (既定, ⚙設定で変更可)
- Space: 再生/停止
- I: IN マーカー
- O: OUT マーカー
- L: ループ再生 ON/OFF
- E: 書き出し
- ←/→: 5秒シーク

### UI
- 形式: MP3 / WAV / AAC (m4a) / OGG
- 品質: 形式に応じて切替 (CBR / VBR / 無圧縮)
- 速度: 0.25〜2.0x
- 音量: 0〜100%
- 履歴: 最近開いたファイル (最大15)
- ⚙設定: ショートカット再割当 / ファイル名プレビュー欄表示 ON/OFF
- IN/OUT 数値欄: 直接秒数入力可
- ファイル名プレビュー欄 (任意表示): ダブルクリックで自動名に戻る

## エンコード設定

| 形式 | コーデック | 品質 |
|------|-----------|------|
| MP3 | libmp3lame | CBR 96〜320k / VBR V0,V2,V4 |
| WAV | pcm_s16le | 44.1kHz 16bit |
| AAC | aac (builtin) | CBR 96〜320k |
| OGG | libvorbis | VBR q1〜q9 |

## 永続データ

- `%APPDATA%\MadCutter\settings.json` — 最後の出力先, ファイル履歴
- ブラウザ localStorage — キーバインド, 表示オプション
- `%TEMP%\MadCutter\cache\<sha1>.wav` — 波形キャッシュ (2GB 上限 LRU trim, SHA1(フルパス + mtime + サイズ) をキー)

## 未実装 / 次候補

- 複数区間連続書き出し (キュー)
- 直前区間 undo
- 自動連番書き出し (`_cut_01`, `_cut_02` ...)
- フェードイン/アウト
- ノーマライズ
- ステレオ/モノ切替
- サンプルレート選択
- 波形振幅ゲイン表示調整

## プロジェクト構成

```
MadCutter/
  MadCutter.csproj
  App.xaml / App.xaml.cs
  MainWindow.xaml / MainWindow.xaml.cs   — WPF ホスト, ffmpeg 制御, 設定/履歴/キャッシュ管理
  web/
    index.html
    app.js                                — wavesurfer 制御, キーバインド, UI 状態
    wavesurfer.esm.js
    plugins/
      regions.esm.js
      timeline.esm.js
  ffmpeg/
    ffmpeg.exe                            — 同梱バイナリ
```
