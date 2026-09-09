# AGENTS.md — 开发说明（RealtimeSubtitle）

面向贡献者与 AI 编码代理。产品说明见 [README.md](README.md)。

## 项目是什么

Windows 11 **本地**实时双语字幕：

```
系统音频 (WASAPI loopback)
  → ASR (OpenVINO: Whisper / SenseVoice / Qwen3)
  → MarianMT 翻译 (OpenVINO)
  → WPF 逐像素透明置顶覆盖层
```

- 全本地推理，无云依赖
- 默认不使用独立 GPU 做 AI 推理（把 GPU 留给游戏）
- Intel NPU：编码器可选；解码器因 KV-cache 动态形状固定在 CPU

## 仓库布局

| 路径 | 职责 |
|---|---|
| `RealtimeSubtitle/RealtimeSubtitle.Core` | 平台无关：音频环/VAD/重采样、`ISpeechRecognizer`、`ITranslator`、字幕、配置、模型查找 |
| `RealtimeSubtitle/RealtimeSubtitle.Windows` | WASAPI、OpenVINO ASR/翻译、窗口互操作 |
| `RealtimeSubtitle/RealtimeSubtitle.Overlay.Wpf` | 透明字幕窗（独立 STA 线程） |
| `RealtimeSubtitle/RealtimeSubtitle.App` | WinUI 3 控制面板 + 组合根 `AppServices` |
| `RealtimeSubtitle/RealtimeSubtitle.Tools` | `WavDumpTool` 诊断/基准 |
| `RealtimeSubtitle/RealtimeSubtitle.Tests` | xUnit |
| `RealtimeSubtitle/tools/model-convert` | Python 模型导出（optimum/OpenVINO） |
| `RealtimeSubtitle/docs` | decisions.md、acceptance-checklist.md |
| `RealtimeSubtitle/tools/publish-portable.ps1` | 便携包发布脚本 |

解决方案：`RealtimeSubtitle/RealtimeSubtitle.slnx`（.NET 8 / x64）。

## 构建

```powershell
cd RealtimeSubtitle
# 沙箱/受限网络建议（见 docs/decisions.md）
$env:TMP = $env:TEMP = "$PWD\.tmp"
$env:NUGET_PACKAGES = "$PWD\.nuget-packages"
Remove-Item Env:SSL_CERT_FILE -ErrorAction SilentlyContinue

dotnet build RealtimeSubtitle.slnx -c Debug
dotnet test RealtimeSubtitle.Tests -c Debug -p:Platform=x64 --no-restore
# 联调非打包
dotnet build RealtimeSubtitle.App -c Debug -p:WindowsPackageType=None -p:Platform=x64
```

- App：`net8.0-windows10.0.26100.0`，WinUI 3 + WindowsAppSDK **2.4.1-experimental**
- Core：`net8.0`
- OpenVINO：`JYPPX.OpenVINO.CSharp.API 3.3.1` + `JYPPX.OpenVINO.GenAI.runtime.win 2026.3.0`

## 模型

运行时查找顺序（`RealtimeSubtitle.Core/Models/ModelPaths.cs`）：

1. `%LOCALAPPDATA%\RealtimeSubtitle\models\<id>`
2. 可执行文件旁 `models\<id>`（便携包）
3. 仓库 `tools/model-convert/out/<id>`
4. 离线 zip `tools/model-convert/dist/<id>.zip`
5. 可选网络源 `Models.BaseUrl`（`config.json`，无内置公网默认）

主要模型 ID：`whisper-*-int8`、`whisper-en-*-int8`、`sensevoice-small-int8`、`qwen3-asr-1.7b-int8`、`opus-mt-en-zh-int8`、`opus-mt-ja-zh-int8`。

导出见 `tools/model-convert/convert_models.py`（需 Python 3.10+ venv + optimum-intel）。  
**大体积模型目录默认不进 Git**（见 `.gitignore`）。

## 运行与诊断

```powershell
# GUI
RealtimeSubtitle.App.exe

# CLI 探针
WavDumpTool.exe --asr testdata\speech_zh.wav --lang auto
WavDumpTool.exe --translate "Where are you going?"
WavDumpTool.exe --bench
WavDumpTool.exe --npu-concurrent-probe   # 若已编入 Tools
```

日志：`%LOCALAPPDATA%\RealtimeSubtitle\logs\`

## 架构要点（改代码前必读）

- **ASR 路由**（`AppServices.BuildRecognizer`）：`zh`→Qwen3，`ja`→SenseVoice，`en`/`auto`→Whisper
- **翻译设备**：`auto`→CPU（P6-16，避免与 ASR 抢 NPU）；显式 `NPU`→encoder NPU + decoder CPU
- **NPU 并发**：本机实测双 Infer **串行**，不要为“占用率”做双 NPU 并行
- **解码器上 NPU**：KV-cache 动态形状，当前导出图无法编译；需静态化重导出（有损，暂缓）
- **字幕**：sticky——常驻直到下一句替换；junk 不清屏（`AppServices` + `SubtitleManager`）
- **覆盖层**：必须是 WPF `UpdateLayeredWindow`（WinUI 无逐像素 alpha，见 decisions P5-7）
- **junk 过滤**：`AsrJunkFilter` 拦 `[Music]`/`*outro*`/`[BLANK_AUDIO]` 等，勿把词表匹配当通用垃圾检测

## 环境注意

- 构建需 Windows 11 SDK / WinAppSDK；仅 x64
- NuGet restore 在受限网络可能失败：可 `--no-restore` 使用已有 `obj/project.assets.json`
- 勿提交：`bin/`、`obj/`、`.venv/`、`tools/model-convert/out|dist`、`dist/`、`*.pfx`、日志与探针输出

## 相关文档

- 产品与使用：`README.md`
- 开源组件：`THIRD_PARTY_NOTICES.md`
- 许可证：`LICENSE`（MIT）
- 阶段决策：`RealtimeSubtitle/docs/decisions.md`
- 验收清单：`RealtimeSubtitle/docs/acceptance-checklist.md`
- 设计稿：`windows11_realtime_translation_subtitle_design_v2.md`
