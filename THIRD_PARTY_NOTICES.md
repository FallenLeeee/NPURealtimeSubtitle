# 第三方开源组件公示（Third-Party Notices）

本软件（RealtimeSubtitle）源代码采用 **MIT License**。  
下列组件/模型**未包含在本仓库源码中**，或仅以 NuGet 依赖形式在构建时引入；使用时请遵守其各自许可证。

> 公示日期：2026-09-09。许可证全文以各上游仓库/包内文件为准。

---

## 运行时 / 开发依赖（NuGet）

| 组件 | 用途 | 许可证（常见） |
|---|---|---|
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | WinUI 3 / Windows 应用 SDK | MIT |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) | Windows SDK 构建工具 |  Microsoft 软件许可 |
| [JYPPX.OpenVINO.CSharp.API](https://www.nuget.org/packages/JYPPX.OpenVINO.CSharp.API) | OpenVINO C# API 绑定 | 见包页 / Apache-2.0（OpenVINO 生态） |
| [JYPPX.OpenVINO.GenAI.runtime.win](https://www.nuget.org/packages/JYPPX.OpenVINO.GenAI.runtime.win) | OpenVINO / GenAI 原生运行时（含 NPU 插件） | Apache-2.0（Intel OpenVINO） |
| [Intel® OpenVINO™ Toolkit](https://github.com/openvinotoolkit/openvino) | 本地推理引擎 | Apache-2.0 |
| [xunit](https://xunit.net/) / xunit.runner.visualstudio | 单元测试 | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | 测试宿主 | MIT |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 代码覆盖率 | MIT |
| [System.Speech](https://www.nuget.org/packages/System.Speech) | 语音相关 API（诊断工具） | MIT |

.NET 运行时与库：[MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)。

---

## 模型权重（**不随本仓库分发**）

应用可加载由下列权重转换得到的 OpenVINO IR；请遵守原始模型许可：

| 模型 | 来源 / 说明 | 许可证（常见） |
|---|---|---|
| Whisper (tiny/base/small, .en) | OpenAI Whisper | MIT |
| MarianMT / opus-mt en→zh、ja→zh | Helsinki-NLP | CC-BY-4.0 |
| SenseVoiceSmall | FunAudioLLM / SenseVoice | Apache-2.0 |
| Qwen3-ASR | Qwen / 通义 | Apache-2.0（以模型卡为准） |

转换脚本：`RealtimeSubtitle/tools/model-convert/`（依赖 optimum-intel、transformers、openvino 等 Python 包，许可见各 PyPI 项目）。

---

## 其他

| 项目 | 用途 | 许可证 |
|---|---|---|
| optimum-intel / transformers / tokenizers | 模型导出（开发期） | Apache-2.0 |
| huggingface_hub | 模型下载（开发期） | Apache-2.0 |

Windows、Visual Studio、VMware 等为各自厂商的专有软件，不在本公示范围。

---

## 合规提示

1. 分发**可执行文件 + 模型**时，须一并提供本文件及各模型的原始许可。  
2. 本仓库 **不** 再分发 Whisper/Marian/SenseVoice/Qwen 权重文件。  
3. 若你对许可证有疑问，请以上游官方 LICENSE / Model Card 为准。
