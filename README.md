# RealtimeSubtitle

Windows 11 **本地实时双语字幕**工具：采集系统声音 → 本地语音识别 → 本地翻译 → 屏幕底部透明字幕。  
**不上传音频、不依赖云端 API**，适合游戏、视频、会议等场景。

> **开发说明**：本项目由 **DeepSeek** 与 **MiMo** 协助开发完成。

## 功能

- WASAPI 回环捕获系统音频（游戏/视频/音乐），无需麦克风权限（默认引擎）
- 本地 ASR：Whisper（自动/英语）、SenseVoice（中/日，可 NPU）、Qwen3-ASR（中文高精度可选）
- 本地翻译：MarianMT（en→zh / ja→zh）
- 可选：Windows 语音识别（麦克风）
- 透明置顶字幕覆盖层（不抢焦点、可穿透鼠标）
- WinUI 3 控制面板：语言/模型/识别设备/翻译设备
- Intel NPU 可选加速（编码器）；解码器默认 CPU

## 系统要求

- Windows 11（Build 26100+）
- x64
- .NET 8 运行时（便携包可为自包含发布）
- 可选：Intel NPU 驱动

## 快速开始

### 方式一：便携包

1. 下载/解压便携 zip（或自行构建，见下）
2. 双击 `RealtimeSubtitle.App.exe`
3. 选择运行模式「真声直播」、识别语言，点「开始」
4. 播放带语音的内容，字幕出现在屏幕底部

### 方式二：从源码构建

```powershell
cd RealtimeSubtitle
dotnet build RealtimeSubtitle.slnx -c Debug -p:Platform=x64
dotnet build RealtimeSubtitle.App -c Debug -p:WindowsPackageType=None -p:Platform=x64
# 运行
.\RealtimeSubtitle.App\bin\x64\Debug\net8.0-windows10.0.26100.0\RealtimeSubtitle.App.exe
```

## 模型

应用会按顺序查找模型（安装目录 → exe 旁 `models/` → 仓库 out/ → 离线 zip → 可选 BaseUrl）。  
未内置公网下载源；可将模型放在：

```
%LOCALAPPDATA%\RealtimeSubtitle\models\
```

或与 exe 同级的 `models\` 目录。

常见模型：

| 用途 | 模型 ID |
|---|---|
| 英语识别 | `whisper-en-small-int8` 等 |
| 中文识别（默认，快/NPU） | `sensevoice-small-int8` |
| 中文识别（高精度，可选） | `qwen3-asr-1.7b-int8` |
| 日语识别 | `sensevoice-small-int8` |
| 英→中翻译 | `opus-mt-en-zh-int8` |
| 日→中翻译 | `opus-mt-ja-zh-int8` |

模型由社区权重转换而来（OpenVINO IR），**不随本仓库分发**；请自行下载或使用你生成的 `tools/model-convert/out` 产物。

### 构建便携包（可选）

```powershell
cd RealtimeSubtitle
powershell -File tools\publish-portable.ps1
# 输出 dist\RealtimeSubtitle-portable.zip
```

## 配置

`%LOCALAPPDATA%\RealtimeSubtitle\config.json`  
日志：`%LOCALAPPDATA%\RealtimeSubtitle\logs\`

## 已知限制

- 系统回环会捕获**所有**播放中的声音（含提示音）
- 默认渲染设备静音/无应用出声时，回环无数据
- DRM 受保护音频不进入 loopback
- 带伴奏歌声的歌词识别率受模型能力限制
- 翻译/识别解码器以 CPU 为主；NPU 主要加速编码器

## 开源许可

本项目源代码以 [MIT License](LICENSE) 发布。  
第三方组件与模型许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 致谢

- 开发协作：**DeepSeek**、**MiMo**
- 推理与模型：Intel OpenVINO、OpenAI Whisper、Helsinki-NLP Marian/opus-mt、FunAudioLLM SenseVoice、Qwen 等（详见第三方声明）
