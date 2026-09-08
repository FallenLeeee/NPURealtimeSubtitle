# 决策记录 (Decisions)

本文件记录每个阶段的"版本钉死项"与关键环境事实，供实施与复盘使用。格式：日期 | 决策 | 理由/来源。

## Phase 1（2026-09，实施首日）

| # | 决策 | 理由 / 来源 |
|---|---|---|
| P1-1 | 解决方案格式采用 `RealtimeSubtitle.slnx`（SDK 10 默认新 XML 格式） | 本机仅安装 .NET SDK 10.0.400；`dotnet new sln` 生成 slnx |
| P1-2 | 四个项目 TFM：Core = `net8.0`；Windows / Tools / App = `net8.0-windows10.0.26100.0` | v3.0 规划 §4；本机装有 .NET 8.0.30 运行时 |
| P1-3 | SDK 10 的 classlib 模板 `-f` 仅支持 net10.0/netstandard → 所有 csproj 手工钉 TFM 与属性 | `dotnet new classlib -f net8.0` 报 templating exit 127 |
| P1-4 | WindowsAppSDK：App 骨架钉 **1.8.260804001**（1.8 稳定线最新）；`Microsoft.Windows.SDK.BuildTools` 用浮动 `10.0.26100.*` | NuGet 版本清单（含 2.4.0 稳定版与 2.2.2-experimental9/2.4.1-experimental）；AI API 载体留待 Phase 2 任务 0 确认 |
| P1-5 | 单元测试项目 `RealtimeSubtitle.Tests`（xunit 2.9.3）加入解决方案 | 规划 §12 要求单元测试（布局之外的新增，记录备案） |
| P1-6 | COM 层使用 `[ComImport]` 手工互操作（不引 NAudio/第三方），仅实现 default 渲染端点；事件驱动 + 200ms WaitTimeout 兜底 | 规划 D5/F5/F6；设备枚举/通知客户端留待 Phase 4/5 |
| P1-7 | 环内数据为设备 mix format 原始交错 float（重采样器负责下混+转换） | 规划 §5.1 数据流 |
| P1-8 | `AudioResampler` 实现为"线性插值 + 一阶低通 + 绝对基准 `_pendingStart` 滑动窗口"，**每调用收缩待处理队列** | 初版按 `consumed>=64` 收缩会无界增长且索引错位（已修） |

## 环境事实（Phase 1 核查）

- OS：Windows 11 25H2，Build 26200（≥ 规划要求 26100）。
- CPU：Intel Core Ultra 7 255HX（Arrow Lake-HX）。
- **NPU：存在且驱动已装** — 设备 "Intel(R) AI Boost"，Class=ComputeAccelerator，PCI\VEN_8086&DEV_AD1D（NPU 3720），驱动 32.0.100.5540（≥ 文档基线 32.0.100.4724）。
- .NET：SDK 10.0.400；运行时有 5.0/6.0/8.0.30/9/10。
- NuGet 源：nuget.org + VS 离线包；公众号页导航经代理到 nuget.azure.cn（dotnet 命令走 nuget.org）。
- WinAppSDK 版本空间：稳定 1.7/1.8/2.0/2.1/2.2/2.3/2.4；实验 2.2.2-experimental9（`Microsoft.Windows.AI.Speech` 引入版本）、2.4.1-experimental。

## Phase 2 就绪核查记录（2026-09-08）

| # | 决策 | 理由 / 来源 |
|---|---|---|
| P2-1 | **锁定组合：Microsoft.WindowsAppSDK `2.4.1-experimental` + `TargetPlatformVersion 10.0.26226.0`**（TFM 仍 `net8.0-windows10.0.26100.0`） | 编译探测实证：`Microsoft.Windows.AI.Speech` 不在 2.4.0 稳定包、不在 2.2.2-experimental9（TPV 26100 下）、OS 投影单独（无 WinAppSDK）也不可用；仅在 2.4.1-experimental + TPV 26226 下编译通过。与 manifest `MaxVersionTested≥10.0.26226.0` 要求呼应 |
| P2-2 | **AI API 需要进程包标识**：官方 cs-wpf-sparse 示例 README 证实，无标识直接跑 exe 拿不到 AI 能力（需全量 MSIX 或 sparse 注册）→ Phase 2 验证载体改为 **App 内 `--ai-probe` 无头模式**（模型就绪→识别→写结果文件→退出），替代原计划的独立 AsrConsole（理论记录偏离，验证载体不变）；生产 UI 仍在 Phase 4 | [cs-wpf-sparse README](https://raw.githubusercontent.com/microsoft/WindowsAppSDK-Samples/release/experimental/Samples/WindowsAIFoundry/cs-wpf-sparse/README.md) |

## Phase 2/3 交叉进展（2026-09-08）

- **Phase 3 任务 0 关键验证通过**：`JYPPX.OpenVINO.CSharp.API 3.3.1` + `OpenVINO.runtime.win 2026.3.1` 在本机 net8.0 控制台可编译可运行；`Core.GetAvailableDevices()`（方法，返回 `IReadOnlyList<string>`）真机输出 `CPU / GPU.0 / GPU.1 / NPU` —— **NPU 对 OpenVINO 可见**（runtimes 包自带 NPU 插件，驱动 32.0.100.5540 匹配）。
- **ASR 后端决策（已裁决 → 见下方 P2-3）**：`Microsoft.Windows.AI.Speech` 在本机 `GetReadyState()` 持续抛 `0x8A1F022F`（上游 open issue microsoft/WindowsAppSDK#6561 同类：CPU 版引擎 `asrmodelapi.dll` 在部分 CPU 上非法指令 0xC000001D）。

## P2-3 最终定案（2026-09-08）：ASR 主后端 = Whisper via OpenVINO

**证据**（本机 Core Ultra 7 255HX / Win 25H2 26200 / 非 Copilot+）：
1. `--ai-setup` 探测：`GetReadyState` → `0x8A1F022F`；`EnsureReadyAsync` → 同 HRESULT 瞬时失败（**无下载发生**，排除"模型未装"解释）；`AFTER_STATE=Unknown`。
2. 上游 [microsoft/WindowsAppSDK#6561](https://github.com/microsoft/WindowsAppSDK/issues/6561)（open，2026-06）：CPU 版引擎 `asrmodelapi.dll` 在 i5-11400（含 AVX-512）上仍 `0xC000001D` 非法指令崩溃——该引擎要求更苛刻指令集，失败路径糟糕。
3. OpenVINO 真机枚举已确认 `NPU` 设备可用（同回合验证，见上）。

**决策**：
- ASR 主后端 = **Whisper（tiny/base，INT8）via OpenVINO，NPU 优先/CPU 兜底**，与翻译共用推理栈。Whisper on NPU 是 OpenVINO **官方支持**路径（2024.5+，stateful 默认开启）；~99 语言多语种自动覆盖 U2。
- `Microsoft.Windows.AI.Speech` 保留为**优先后端**：运行期 `GetReadyState()==Ready` 时优先（Copilot+/受支持 CPU 上体验更佳）；本类机器（0x8A1F022F/非法指令）自动走 Whisper。
- `LegacySpeechRecognizer`（Windows.Media）保留为三级兜底（麦克风/文件）。
- 架构影响：`ISpeechRecognizer` 缝隙不变；Phase 3 推理栈同时服务翻译与 ASR；Phase 4 UI 不变。
- 风险更新：Whisper on NPU 官方支持但需实测延迟（U4a）；tiny 质量低于大模型，字幕场景可接受（延迟优先原则）。

## P3-1 .. P3-4（Phase 3，2026-09-08）

| # | 决策/事实 | 证据 |
|---|---|---|
| P3-1 | OpenVINO 集成 = `JYPPX.OpenVINO.CSharp.API 3.3.1`（托管，含经典 Core+GenAI Whisper）+ `JYPPX.OpenVINO.GenAI.runtime.win 2026.3.0`（单原生包：openvino/genai/NPU 插件/tokenizers） | 真机枚举 `CPU/GPU.0/GPU.1/NPU`；Net8 console 实测 |
| P3-2 | **marian 翻译 = CPU（动态形状）**：导出解码器无 causal-mask 输入、无 KV 缓存、带 `beam_idx`——静态 NPU 填充破坏因果性，且 NPU 自回归慢于 CPU（F9/D10）。设备选择器实测 CPU 中位 **24ms/句**（50 句×3 轮），实时性远超目标 | shapes.txt（encoder: input_ids/attention_mask→last_hidden_state[1,?,512]；decoder: encoder_attention_mask/input_ids/encoder_hidden_states/beam_idx→logits[1,?,65001]） |
| P3-3 | marian special ids**以目标词表为准**（tgt[0]='</s>'=eos、tgt[2]=','）与源 spm 不同；decoder 起点 = generation_config 的 `decoder_start_token_id`(65000)，非 spm '</s>'；编码侧按 HF 惯例追加 eos | tokenizer.json+generation_config.json 本地解析；初版起错 token 生成螺旋（已修） |
| P3-4 | 翻译质量：opus-mt 偶发"重复句首"，已加重复后缀裁剪；尾缀"来"为模型固有意译，可接受 | 实测：请开门 / 我们现在需要行动了 / 你要去哪里? 来 |
| P3-5 | **whisper-via-GenAI 被上游契约失配阻塞（U4a）**：optimum-intel 2.0/2.1/1.25（连同 transformers 4.57.6/5.5.4、torch 久远组合）导出的 whisper 解码器**均无 `beam_idx` 输入**，而 GenAI 2026.3.x whisper 运行时 **无条件**喂 `beam_idx`（`is_beam_search` 只读、由模型 generation_config 派生，无法关闭）；1.25 还与新 torch 不兼容。**出路**：Phase 5 用经典 Core API 自实现 whisper 推理（mel 谱 ~100 行 + BPE tokenizer + 贪心解码，与 marian 同构）或等待上游修复；`ISpeechRecognizer` 缝隙已就位。venv 固定组合：optimum-intel 2.1.0 + transformers 4.57.6 + openvino 2026.3.1（whisper 导出本身 OK，仅与 GenAI 运行时契约不符） | 本回合多次实证（含只读 `is_beam_search`） |

## P4-1（Phase 4，2026-09-08）

- 覆盖层窗口组合实证：`WS_EX_TRANSPARENT|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW`（+LAYERED alpha 可选、无副作用）+ 去圆角 + `OverlappedPresenter.IsAlwaysOnTop` + `IsShownInSwitchers=false`；不透明度主要由 Border 背景 alpha 承担（避免 LAYERED 与 WinUI 合成器纠缠，LAYERED 仅作整窗 alpha 补充）。
- `Windows.Graphics.*` 与 `RealtimeSubtitle.Windows` 命名空间撞名 → 用 `global::` 限定。
- Phase 4 验收载体：App 内 `--overlay-probe` / `--demo`（录播源替代尚未就绪的 whisper ASR，U4a 后无缝换真声）；控制面板默认 UI 保留。
- 打包形态：默认 MSIX 构建保持绿色；UI 联调用 `-p:WindowsPackageType=None` 非打包变体（无需包标识，迭代快）。

## 环境根因与构建配方（2026-09-08，重要）

**根因**：会话沙箱曾把 `TMP/TEMP` 指向失效目录（`C:\Users\LuoLuo\AppData\Local\Temp\dsh-*`），导致 Python（tempfile/transformers）、NuGet、MSBuild 一切写临时文件的操作**静默失败**（dotnet 表现为"0 错误但生成失败"）；`%USERPROFILE%` 缓存（HF/.nuget）同样写不进。

**构建/转换配方（所有 dotnet/python 命令生效）**：
```
$env:TMP = $env:TEMP = <workspace>\RealtimeSubtitle\.tmp
$env:NUGET_PACKAGES = <workspace>\RealtimeSubtitle\.nuget-packages
HF_HOME/PYTHONUTF8 已内置在 tools/model-convert/convert_models.py
Remove-Item Env:SSL_CERT_FILE（变量指向不存在的 certifi 文件）
```
**机器固有问题**：本机 `C:\Program Files\dotnet\sdk\10.0.400\Sdks` 缺 `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator` / `WorkloadManifestTargetsLocator` 两目录；`$(MSBuildEnableWorkloadResolver)` 为 true 时会 MSB4276。配好 TMP 后实测不再触发（与沙箱环境共同作用）；`tools/msbuild-sdks/` 覆盖层保留备用但 **junction 覆盖会破坏 `[MSBuild]::Add` 求值，勿用**。

## Phase 1 收尾核查记录（2026-09-08）

- 首次实机采集（8s 窗口）：mix 格式识别正确（48000 Hz / 2ch / 32-bit float）；蜂鸣期间 VAD 触发、RMS≈0.35；静默段无数据。
- **平台行为记录**：默认渲染端点**空闲（无任何应用渲染音频）时不产生 loopback 数据包**；有声音（含游戏/视频）时正常实时输出。产品语义 OK（静默间隙交给 ASR 处理），Phase 5 如需要可挂 keep-alive 静音渲染流。
- 对照实验（15s 连续音）：写入 11.22s / 1120 块，消费全程实时无积压 —— 消费者/环形缓冲无瓶颈。
- WAV 头校验：RIFF/WAVE、PCM16、1ch、16000 Hz、数据 359040 B（11.22s）✓
- 单元测试 21/21 全绿；全解决方案 5 项目构建成功（含 WinUI App 骨架，WinAppSDK 1.8.260804001）。

## P5-1 / P5-2（Phase 5，2026-09-08）

- **P5-1：whisper 经典 API 自实现打通（U4a 解除）**。`OpenVinoWhisperClassicRecognizer`：C# log-mel（80×201 slaney 滤波矩阵由转换器导出、STFT 400/160、`log10→max-8→(x+4)/4`）→ 编码器（动态 [?,?,3000] 静态喂 [1,80,3000]）→ 解码器贪心（eos=50257、sot=50258、语言自动检测=对 98 个语言 token 取 argmax）→ GPT2 字节级 BPE 解码（`Ġ→0x20`、`Ċ→0x0A`、`ĉ→0x09`、byte_decoder 导出为 unicode→byte）。
  **实测（真机）**：en "Hello."/"Big brown fox jumps over the lazy dog."/"A test of the realtime subtitle system." ✓；zh "你好"/"中文语音识别正在进行" ✓；ja "こんにちは"/"これは字幕システムのテストです"/"日本語の音声認識をテストしています" ✓ —— **三语自动检测、单段 168-371ms**（U2/U3 达标）。
- **P5-2：whisper 编码器 NPU = 暂缓**。optimum 导出的编码器维度动态（?×?×3000），直接 `CompileModel(model,"NPU")` 触发 vpux-compiler 原生崩溃（LLVM ERROR，无法捕获）→ v1 全 CPU。出路：导出时固定静态形状或 C# 侧 `Reshape` 后再编译（后续）。
- 附带修正：whisper 语言 token 为 **added tokens**（`get_vocab()` 全量 51865）；`byte_decoder` 语义为 unicode→byte；mel 滤波矩阵实际 (bins×mels) 需转置。

## P5-3（2026-09-08）：覆盖层透明 = DWM 瞬态背景，LWA_COLORKEY 作废

- **失败记录**：初版用 `LWA_COLORKEY`（纯黑键色 + 根背景 `#FF000000`）整窗抠色 → 实测**仍是黑底**。
  根因：WinUI 3 内容由 DirectComposition 渲染，colorkey 只作用于 GDI 表面，对合成内容无效。
- **正确方案（已实施）**：`DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE=38, DWMSBT_TRANSIENTWINDOW=3)`
  + 根 Grid `Background="Transparent"`；在窗口呈现前设置、`Activate()` 后再补一次。
  字幕条自身保留半透明深色背景（#CC101010）保证可读性。
- **验证结论（本机）**：窗口存在且可见；DWM 属性读回 = 3；`PrintWindow(PW_RENDERFULLCONTENT)` 渲染出
  "透明根 + 深色条 + 白字"。**注意**：本机 GDI 屏幕捕获（CopyFromScreen/BitBlt，含 CAPTUREBLT）捕获不到
  DirectComposition 窗口（对照：WinForms 窗体可见、整窗品红 DirectComposition 窗口不可见），因此像素级
  自动验证不可用，最终以真实屏幕目视为准。
- 兜底预案（若个别系统仍黑）：`SetWindowCompositionAttribute(ACCENT_ENABLE_TRANSPARENTGRADIENT)` 或覆盖层
  改用 WPF（UpdateLayeredWindow 逐像素 alpha）。

## P5-4（2026-09-08）：模型自动补齐（目录 + 离线包 + 网络源）

- **问题**：模型路径原先硬编码 `tools/model-convert/out/<id>`（相对启动目录），从 bin 启动必报"未找到翻译模型"。
- **方案**：`RealtimeSubtitle.Core/Models/`（ModelCatalog/ModelPaths/ModelDownloader/ModelProvisioner）：
  1) 安装目录 `%LOCALAPPDATA%\RealtimeSubtitle\models\<id>`；2) 仓库检出 `tools/model-convert/out/<id>`
  （沿启动目录向上 ≤6 层查找）；3) 本地离线包 `tools/model-convert/dist/<id>.zip`（自动解压）；
  4) 网络源 `Models.BaseUrl`（`<BaseUrl>/<id>.zip`）。缺模型时 GUI"下载缺失模型"/点"开始"自动补齐并显示进度；
  `WavDumpTool --download-models [--dir <installRoot>]` 命令行等价。
- **网络事实（本机）**：huggingface.co 超时不可达，hf-mirror.com / raw.githubusercontent.com / gitee.com 可达 →
  未内置公网默认源，BaseUrl 由用户在 config.json 配置（如内网镜像）。离线 zip 已在本地实测端到端
  （解压 → 翻译/识别均可用）。

## P5-5（2026-09-08）：ASR 默认 = Windows 语音识别（Windows.Media.SpeechRecognition，流式）

- 用户要求"语言转文字用 Windows Speech Recognition 这个 API 的流式输出"。`Asr.PreferredBackend` 默认改为 `legacy`。
- `LegacySpeechRecognizer`（照 Rick Strahl 的 VoiceDictation 封装模式）：`SpeechRecognizer(Language)` + Dictation 主题约束 +
  `HypothesisGenerated`→Partial（边说边出）+ `ContinuousRecognitionSession.ResultGenerated`→Final。麦克风输入，无回环推送；
  whisper 后端（回环）经配置切换。
- 注意：legacy API 无自定义 PCM 推送/回环，直播模式下是**麦克风**识别（非系统音频）。

## P5-6（2026-09-08）：翻译 NPU = encoder 静态化混合（NPU(enc)+CPU(dec)）

- 用户要求翻译跑 NPU。实测（`--npu-probe`）：**encoder 静态 [1,16] reshape + NPU 编译成功（~2.3s）**；
  **decoder 4 输入全静态化后 NPU 编译仍失败**（`to_shape was called on a dynamic shape`，beam 相关动态节点，与 P3-2 判定一致）。
- 方案：encoder **一次编译固定 [1,64]**（真实句 padding 到 64 + attention_mask=0，BERT 式），全部句子复用；
  decoder 恒 CPU 动态。混合路径 device 报告 `NPU+CPU`；任一 NPU 失败自动回退全 CPU。
- 实测（本机）：混合中位 34ms/句 vs 纯 CPU 29ms/句（50 句×3 轮；NPU 编码器并未更快——decoder 是 CPU 大头、
  NPU 有往返开销），但 NPU 真实参与编码计算。单句翻译 ~100ms（含 NPU 首次 2.3s 编译在启动时一次完成）。

## P5-7（2026-09-08）：覆盖层 pivoted 到 WPF 逐像素透明（WinUI 两次方案均黑底）

- 用户两次反馈"底还是黑的"：LWA_COLORKEY（GDI 表面，对 DirectComposition 无效）与 DWMSBT_TRANSIENTWINDOW+透明根
  （属性确认生效=3、PrintWindow 渲染正确，但真实屏幕仍黑）均失败。
- **决定：覆盖层改用 WPF 窗口**（`RealtimeSubtitle.Overlay.Wpf`，独立 STA 线程 + Application，宿主在 WinUI 进程内）。
  WPF `AllowsTransparency=True` 走 UpdateLayeredWindow 逐像素 alpha——业界标准 overlay 机制，不存在"黑底"可能。
- 穿透/置顶/不抢焦点：`WS_EX_TRANSPARENT|NOACTIVATE|TOOLWINDOW`（probe 实测 `TRANSPARENT|NOACTIVATE|TOOLWINDOW|LAYERED`）。
- 跨线程：`SubtitleOverlayHost` 提供 `Start/PushSnapshot/Stop/Hwnd`（Hwnd 经 WPF Dispatcher 封送）。
- 遗留：本机 BitBlt/CopyFromScreen 捕获不到 layered/DirectComposition 窗口，像素级自动验证不可用 → 最终目视确认。

## P5-8（2026-09-08）：WPF 宿主崩溃修复 + ASR 默认回环 + config 迁移

- **崩溃修复**：VS 调试时 WPF 诊断注入（WpfTap）或重复 Start 会在 AppDomain 里已存在
  `System.Windows.Application`，`SubtitleOverlayHost` 再 `new Application()` 抛
  "不能在同一 AppDomain 中创建多个 System.Windows.Application 实例"。改为**不用 Application**，
  独立 STA 线程 `Dispatcher.Run()` 消息泵 + `InvokeShutdown()` 结束（WPF 独立窗口标准做法）。
- **ASR 默认回环**：用户明确"要啥麦克风权限，只需扬声器声音"。`Asr.PreferredBackend` 默认
  **whisper**（系统音频回环，无需麦克风权限）；legacy（Windows.Media.SpeechRecognition，麦克风）
  降为可选。Windows AI Speech 本机不可用（0x8A1F022F）时不再静默落麦克风，而是按回环 whisper 处理。
- **config 迁移**：旧 config.json 残留 `Asr.PreferredBackend="windows-ai"` 导致 live 探针/GUI
  走麦克风路径、字幕为 0 → 已迁移为 whisper；CreateLive 对任何非 legacy 后端统一按回环 whisper 处理。
- **连续语音出句**：Whisper 识别器原来只在 VAD 静默时出 Final；连续语音（如视频无停顿）只有
  partial 预览、无 final、无翻译 → 加"段达 ring 上限（15s）强制 Final" + `Stop()` 时收尾当前段。
- 流式 partial：说话中每 ~1.2s 对进行中段转写一次发 `Partial`（边说边出预览），转写经
  `SemaphoreSlim` 串行（final 等待、partial 忙时跳过）。

## P5-9（2026-09-08）：停止时 ObjectDisposedException 闪退修复

- **症状**：停止（Dispose）时后台转写任务仍在 `finally` 里 `_transcribeGate.Release()`，
  而 `Dispose()` 已释放该 SemaphoreSlim → `ObjectDisposedException`（线程池线程上未处理异常）。
- **修复**（`OpenVinoWhisperClassicRecognizer`）：① 转写任务改为经 `StartTranscribe` 记录到
  `_inFlight` 列表；② `Dispose()` 先 `Task.WaitAll(在途任务, 30s)` 等转写完成再释放
  decoder/encoder/core；③ **不再 Dispose `_transcribeGate`**（`SemaphoreSlim(1,1)` 无 OS 句柄，
  不释放无泄漏；避免停止竞态）。
- 实测：live（含翻译完成）+ 连续重复启动/停止均 exit=0，无崩溃。

## P5-10（2026-09-08）：Whisper 编码器上 NPU（音转字）

- 用户要求音转字也放 NPU。`--whisper-npu-probe` 实测：encoder 静态 reshape [1,80,3000]（mel 固定 30s 窗、
  0-padding）→ **NPU 编译成功（~2s）**；decoder 静态化后也能编译，但自回归输入长度逐步变化，
  每长度单独编译成本高（~2.5s/形状）→ **decoder 留 CPU**（与翻译混合模式一致）。
- `OpenVinoWhisperClassicRecognizer`：device=auto/NPU 时尝试静态 NPU 编码器（一次编译，缓存复用），
  失败自动回退 CPU；日志明确 `encoder on NPU, decoder on CPU`。
- 实测（本机）：en 4 段识别全对（"Hello."/"A test of the real time subtitle system."等）；
  zh 3 段全对。**诚实数据**：NPU 编码器单段 265-894ms vs 纯 CPU 207-428ms——这颗 NPU 对 whisper-tiny
  这类小模型有固定传输开销，**CPU 反而更快**；但 NPU 真实参与编码计算。追求低延迟可改 device=CPU。

## P5-11（2026-09-08）：ASR 模型换 whisper-base（准度）

- 用户反馈 tiny 准度差。转换脚本加 `--whisper-size base`（默认仍 tiny，显式指定 base），
  `convert_models.py --whisper-size base --only whisper` 产出 `out/whisper-base-int8`
  （encoder 22MB + decoder 50MB int8），并生成 sidecar（whisper_meta.json/mel_filters.bin）。
- **默认 ASR 模型改为 whisper-base**：`ModelCatalog.WhisperModel = "whisper-base-int8"`，
  tiny 保留为备用条目；GUI 下载/启动默认取 base；离线包 `dist/whisper-base-int8.zip`（66MB）。
- 网络：hf-mirror 在 python 栈不稳定，脚本/文档注明可 `--hf-endpoint https://huggingface.co` 直连官方
  （本机实测官方端点可用）。
- 精度实测（SAPI 合成测试音，en/zh/ja）：zh 明显更准（"這是實實字幕系統的測試" vs tiny "實時自穆"）；
  en "A test of the real-time subtitle system." 完整；个别合成音句子 base 与 tiny 各有小错
  （"Big brown fox"→"Brom Fox" 在 CPU/NPU 均出现，与设备无关）。NPU 编码器对 base 编译成功
  （~4.6s 一次），en/zh 输出与 CPU 一致，ja 个别字与 CPU 有数值差异（"認識"→"意識"）——追求极致
  可切 CPU（device=CPU）。

## P5-12（2026-09-08）：whisper-small + GUI 模型热切换

- 新增 **whisper-small-int8**（encoder 88MB + decoder 147MB，`--whisper-size small` 导出，dist zip 211MB），
  `ModelCatalog` 三档：tiny/base/small，默认 base。
- **GUI 热切换**：新增"识别模型"下拉（tiny/base/small）；选择即持久化 `Asr.Model`，
  运行中直接 `AppServices.SwitchAsrModel`——只停 feeder 推送与旧识别器（`CaptureFeeder.StopPipeline`
  保持 capturer 运行），换新识别器重建 feeder 续流（真·热切换，回环不断）。
- small 实测：**精度最佳**——中文"这是实时字幕系统的测试。"全对（base 错"實實"、tiny 错"自穆"）；
  英文段完整。NPU encoder 编译成功（~14.8s 一次）；段延迟 1.4-3.1s（decoder 自回归为 CPU 瓶颈）——
  句子级实时可接受，追求低延迟用 base/tiny。

## P6-1（2026-09-08）：识别提速（一）——mel 谱 SIMD 化 + NPU 编译缓存

- 用户反馈"速度太慢"。定位：C# 的 log-mel 是标量 double 朴素 DFT（每帧 201 bin × 400 tap），
  13.6s 音频 ≈ 1.36 亿次乘加，单次转写固定吃 ~300-400ms，且每个 partial 都重算一遍。
- 重写 `MelSpectrogram`：预计算 float 基底表 + `System.Numerics.Vector<float>` 点积 + `Parallel.For`
  按帧并行（窗函数/功率谱/mel 投影/归一化分四段）。新增 `ComputeReference`（原标量实现，internal）
  与 `MelSpectrogramTests` 回归对比：最大绝对误差 < 1e-4（25/25 测试通过）。
- **实测：mel 从 ~300-400ms 降到 2-5ms**（首帧 77-81ms 为 JIT 预热）。
- **NPU 编译缓存**（`OpenVinoDevice.ApplyCache`）：给 NPU/GPU/CPU 设 `CACHE_DIR`
  （`%LOCALAPPDATA%\RealtimeSubtitle\cache\openvino`）。whisper-small 编码器 NPU 编译
  **14.3s → 1.2s**（第二次起），base 4.7s → 0.3s；离线 blob 425MB。热切换/重启不再等编译。
- 顺带：CPU 编码器改为按需惰性编译（NPU 可用时不再白编译一份 CPU 图）。

## P6-2（2026-09-08）：识别提速（二）——状态化 KV-cache 解码器（约 5x）

- 根因：原先导出的是 **无 past 解码器**，每个解码步都要把整个前缀重喂一遍，并重算 1500 帧的
  cross-attention K/V。whisper-small 实测 **55ms/步**，36 token 的句子 ≈ 2s。
- 方案：optimum 的 `--task automatic-speech-recognition-with-past` 会把 KV cache 藏进图里
  作为 OpenVINO 状态（`ReadValue`/`Assign`，small 32 个），输入只剩 `input_ids`/`encoder_hidden_states`
  /`beam_idx`。识别器检测 decoder XML 是否含 `ReadValue` 自动切换两条路径：
  - 状态化：每段**新建 InferRequest**（等价于清空 KV cache，绑定层未暴露 reset_state），
    prefill prompt 后每步只喂 1 个 token；`encoder_hidden_states`/`beam_idx` 只设一次。
  - 旧无 past 模型仍走原全前缀路径（向后兼容）。
- 语言自动检测并入同一请求：先喂 `[sot]` 取语言 token，再喂 `[lang, transcribe, notimestamps]`。
- **实测（whisper-small，13.6s 音频 / 36 token）**：解码 **36×10.8ms ≈ 390ms**（原 ≈ 2s），
  首次 92ms、稳态 10ms；文本完全正确。base：解码 27-51ms/段。tiny：2.5ms/步。
- 设备实测（同一模型）：解码器 CPU 10.8ms/步 **最快**，GPU.0 19.2ms，GPU.1 57.9ms，
  **NPU 不可用**（cross-attn 投影的 state 长度动态，vpux 报 "Upper bounds are not specified"）。
  编码器反过来：**NPU 185ms 最快**，CPU 345ms，GPU.0 270ms，GPU.1 1004ms → 保持"NPU 编码器 + CPU 解码器"。
- 端到端（whisper-small）：单段 **~310-400ms**（mel 4 + enc 190 + dec 110）；whisper-base 单段 **~130ms**。
  原为 1.4-3.1s / 0.3-1s。

## P6-3（2026-09-08）：NPU 编码器已到该平台上限

- 试过并**无收益**（base 编码器 60.6ms 基线）：`NPU_TURBO`、`PERFORMANCE_HINT=THROUGHPUT`、
  `NPU_QDQ_OPTIMIZATION(_AGGRESSIVE)`、`EXECUTION_MODE_HINT`、`NPU_COMPILER_DYNAMIC_QUANTIZATION=NO`、
  `NPU_DEFER_WEIGHTS_LOAD`——全部 60.5-60.7ms（±0.3%）。
- 编码器输入窗口无法缩短：whisper 编码器位置编码固定 1500 帧，reshape 到更短的 mel
  （2000/1500/1000/600/400/200 帧）直接报
  `broadcast_merge_into ... failed`（见 `tools/model-convert/probe_encoder.py`）。
  所以 30s 窗口是固定成本，只能靠减少转写次数（partial 频率）来摊薄。
- NPU 能力（本机）：`DEVICE_GOPS` fp16 6553 / int8 13107，`NPU_MAX_TILES=2`，driver 32.0.100.5540。
  结论：**NPU 用于编码器是当前最优选择**，比 CPU 快 1.9x；解码器留在 CPU。

## P6-4（2026-09-08）：语言选择 + 各语言专精 ASR 后端

- GUI 新增"识别语言"下拉（自动/中文/英语/日语），`AsrConfig.Language`（auto|zh|en|ja），
  选择联动 `SourceLanguage`（zh→zh-CN、en→en-US、ja→ja-JP、auto→auto）。
- 按语言路由 ASR 模型（`ModelCatalog.AsrModelId`）：

  | 语言 | 后端 | 模型/包 | 实测延迟 |
  |---|---|---|---|
  | 自动 | 多语言 Whisper（原） | whisper-{tiny,base,small}-int8 | 0.08-0.4s/段 |
  | 英语 | **whisper.en**（单语） | whisper-en-{tiny,base,small}-int8 | 0.1-0.4s/段 |
  | 中文 | **Qwen3-ASR 1.7B** | qwen3-asr-1.7b-int8（4.4GB） | 0.5-2s/段（仅终稿） |
  | 日语 | **SenseVoiceSmall**（单程 CTC） | sensevoice-small-int8（230MB） | 0.08-0.2s/段 |

- whisper.en：同一架构 + with-past 状态化解码器，sidecar `"mono": true`（词表虽含全部语言
  token，但模型只输出英语）→ 识别器跳过语言 token 与语言检测（prompt = [sot, transcribe,
  notimestamps]），与 OpenAI 的 decode 契约一致。实测 small.en 文本全对。
- 热切换泛化：`AppServices.SwitchAsrBackend(Func<ISpeechRecognizer>)`（停 feeder → 换识别器 →
  重建 feeder 续流），模型尺寸与语言切换共用；GUI 运行中切语言直热生效并持久化。

## P6-5（2026-09-08）：中文专精——Qwen3-ASR（OpenVINO 社区导出）

- 模型：`dseditor/Qwen3-ASR-1.7B-INT8_OpenVINO`（Apache-2.0，4.39GB）四个 IR：
  audio_encoder（FP16，mel[128,?]→(1,n_audio,2048)）、thinker_embeddings（INT8，input_ids→embeds）、
  decoder_prefill_kv、decoder_kv（显式 KV cache，28 层 × 8 头 × 128，词表 151936）。
- 特征：**128-mel**（whisper slaney 滤波器 n_mels=128，`mel_filters_128.bin` 转储）；
  mel 时间维需为 **100 的倍数**（graph 内 Reshape `(-1,100,128)` 硬约束，其他长度直接报错）→
  段长取整补零到下一个百帧。
- 解码：prefix + audio_pad×n_audio + suffix + 语言后缀（中文 `[11528,8453,151704]`）→
  thinker 嵌入 → 在 audio_pad 位置替换 audio_embeds → 预填充（input_embeds+position_ids →
  logits+past）→ 每步 1 token（new_embed+new_pos+past）→ **Qwen 字节级 BPE 解码**
  （GPT2 字节表，无 byte-fallback；`QwenBpeDecoder`）。
- 实测（speech_zh）："你好。"/"这是实时字幕系统的测试。"全对；单段 0.5-0.8s（enc 36-81ms +
  prefill 131-326ms + dec 14 step×~30-100ms）。**仅出终稿**（1.2s partial 节拍跑不动 1.7B）。
- NPU：audio_encoder 静态 [128,800]/[128,3000] 可编译（29s/18s 首次，缓存后秒级），
  infer 51ms vs CPU 167ms；但解码器（1.7B 动态 LLM）无法 NPU → 总延迟由 CPU 解码主导，
  NPU 只省 ~5%，故编码器也走 CPU（动态形状，实现简单可靠）。

## P6-6（2026-09-08）：日语专精——SenseVoiceSmall（单程 CTC ONNX）

- 模型：`DennisHuang648/SenseVoiceSmall-onnx`（Apache-2.0，`model_quant.onnx` INT8 230MB），
  OpenVINO 直接读 ONNX。输入 speech[1,T,560]、speech_lengths、language、textnorm；
  输出 ctc_logits[1,T,25055]（CTC 非自回归，一次前向）。
- 特征链（`KaldiFbank`，与 kaldi-native-fbank 数值对齐，`testrefs/kaldi_*.bin` 回归测试）：
  25ms/10ms hamming → **HTK mel 80 分带**（knf 实测为 htk 尺度 + 无 norm，非 slaney）
  → ln(max(x, FLT_EPSILON)) → LFR 7/6 堆叠 560 维 → CMVN（am.mvn kaldi nnet 文本的
  AddShift/Rescale 560 维，正则解析）。mel 时间长度需为 100 倍数同 Qwen3（本模型是 Reshape 约束）。
- 解码：每帧 argmax → CTC（blank=0、去重复）→ tokens.json → 剥 `<|zh|>/<|ja|>/<|NEUTRAL|>/<|Speech|>/
  <|woitn|>` 等控制符 → `▁`→空格。language=0、textnorm=0（raw，无 ITN）。
- 实测：speech_ja "こんにちは/これは字幕システムのテストです/日本語の音声認識をテストしています"，
  speech_zh "你好/这是实时字幕系统的测试/中文语音识别正在进行" 全对；单段 80-190ms（mel 6-17 + model 70-180）。
- 坑：OpenVINO 动态形状对该 ONNX 的某些长度（129+ 或非 100 倍数）会崩/产零 → 一律补零到百帧倍数。
  特征与 knf 有少量高频瞬态帧偏差（max ~5-7，mean 0.16）——模型鲁棒，端到端文本正确。

## P6-7（2026-09-08）：歌曲场景——过滤 whisper 的音频事件标注（[Music]）

- 用户反馈播歌时字幕显示残缺的 `Music]` 而非歌词。根因：whisper 对纯器乐/前奏/片段会输出
  `[Music]` 事件标注（流式 partial 还会生成半截 `Music]`），被当作字幕上屏并顶掉歌词。
- 新增 `Core/Speech/AsrJunkFilter`：在 `AppServices.BindRecognizer` 入口拦截——
  文本（去 `[]()♪♫` 等符号后）等于 music/歌曲/音乐/applause… 或以 `[music`/`Music]`/`♪` 开头
  即视为事件标注，partial/final 一律丢弃，不进字幕不进翻译队列。真实歌词含 "music"/"歌曲" 字样
  不受影响（仅精确/前缀匹配）。15 条单测覆盖。

## P6-8（2026-09-08）：翻译提速——Marian 解码器正式启用隐式 KV cache

- 发现 optimum 导出的 Marian 解码器**本就带 KV 状态**（ReadValue=50，与 whisper -with-past 同构），
  但 `OpenVinoTranslator` 每步 `CreateInferRequest` 重建请求（状态被重置）并重喂整个前缀 →
  解码退化回 O(n²) 全量重算。
- 修复（纯 C# 用法，模型文件不变）：每句一个请求 = 干净 KV 状态；prefill 只喂 `[decoder_start]`，
  之后每步喂 1 个 token；encoder_hidden_states/mask/beam_idx 只设一次（cross-attn K/V 首步算好入缓存）。
- 实测（短句/中句/长句）：110→66ms、124→77ms、长句 ~116ms（~1.5-1.7x）。
  新路径与旧全前缀路径输出有轻微 int8 数值差异（"跳过"/"跳过来"），属正常——KV 路径与
  transformers/optimum 的生成方式一致，为准。解码器仍 CPU（KV 长度动态，NPU 拒动态形状）。

## P6-9（2026-09-08）：SenseVoice 静态 ONNX + **NPU 成功（纯 FP32 去量化）**

- 用户要求导出静态 ONNX。`export_sensevoice_static.py` / `convert --only sensevoice-static`
  （默认 N=200）：折叠 speech_lengths/language/textnorm 为常量、speech 固定 [1,N,560]。
  无掩码补零 ≈ 掩码（实测逐词一致）。
- **NPU 排查过程**（逐步定位）：
  1. int8 静态可编译但输出全 blank（logits 分布"正常"但 top token 全错，值域被压缩 ~3x）——
     QDQ 优化开关无效。
  2. 常量折叠 0 个节点——掩码来自 `ShapeOf(speech)`，非 speech_lengths。
  3. **根因**：iic 的 model_quant.onnx 用 **DynamicQuantizeLinear→MatMulInteger** 动态量化链，
     NPU 对这些算子算错。`dequantize_sensevoice.py` 把 281 个量化 MatMul 精确反量化为
     纯 FP32 MatMul（CPU 输出逐词一致验证）→ `model_fp32_static.onnx`（938MB）。
  4. **纯 FP32 静态在 NPU 上完全正确**：ja/zh/en 全对，**infer 55-68ms/段**（比 int8 静态
     267ms 快 4.6x、比动态 CPU 70-190ms 更快，且腾出 CPU）。NPU 冷编译 ~43-49s，缓存后秒级。
  5. fp16 转换（470MB）NPU 编译失败，弃用。
- 运行时优先级：`model_fp32_static.onnx`（NPU，回退 CPU）→ `model_static.onnx`（int8 CPU）→
  动态 CPU。C# 端到端实测：日语/中文/英语全对，单段 **73-80ms**（mel 6-20 + model 59-68ms）。
- 解码行数取 `min(lens, 真实LFR+4)`（无掩码补零的尾部偏移与幻觉折衷）。

## P6-10（2026-09-09）：日语翻译补齐——Marian opus-mt ja→zh

- 背景：翻译管线此前只有 en→zh（`opus-mt-en-zh-int8`）。日语识别（SenseVoice）出文本后
  仍走 en→zh 模型，译文全错。用户要求"补全日语的翻译"。
- 官方 `Helsinki-NLP/opus-mt-ja-zh` 已被 gated（401 "Invalid username or password"，官方与
  hf-mirror 均如此）；改用公开镜像 **`shun89/opus-mt-ja-zh`**（文件与官方一致：
  pytorch_model.bin + source/target.spm + vocab.json）。
- 转换：`convert_models.py` 新增 `--marian-pair` / `--marian-repo` 参数与
  `--marian-pair ja-zh --marian-repo shun89/opus-mt-ja-zh` 用法；导出必须显式
  `--task text2text-generation-with-past`（镜像仓库缺 pipeline tag，auto 检测失败：
  "task auto not supported for marian"）。产出 `out/opus-mt-ja-zh-int8`（INT8，
  encoder/decoder + tokenizer.json 65001 pieces，ID: bos=1 eos=0 unk=1 pad=65000
  decoder_start=65000，与 en-zh 同构）→ dist zip 94MB。
- 运行时路由（`ModelCatalog.TranslationModelId(language)`）：`ja` → `opus-mt-ja-zh-int8`，
  其余（en/auto）→ `opus-mt-en-zh-int8`；GUI 模型状态/下载/启动解析与 `--live` 探针均按
  语言路由（`MainWindow.RefreshModelStatus`/`OnDownloadClick`/`ResolveTranslationModelAsync`、
  `App.xaml.cs` RunLiveModeAsync）。
- **C# 复用 `OpenVinoTranslator` 零改动**：ja-zh 与 en-zh 的 IR 结构完全一致
  （encoder input_ids/attention_mask→last_hidden_state[?,?,512]；decoder 四输入静态重塑
  [1,64] NPU 同理、状态化 KV decoder）。端到端验证（WavDumpTool --translate，CPU）：
  `こんにちは→你好`、`これは字幕システムのテストです→这是字幕系统的测试`、
  `日本語の音声認識をテストしています→正在测试日语语音识别`、`私は今日学校に行きます→
  今天我要去学校`（68-119ms/句）。opus-mt 为老 SNMT，个别句带方言腔（如"佢哋"），
  属模型固有，后续可换更大的 ja→zh 模型（如火山的 M2M / NLLB）提质量。
- UI 任务同期完成：主窗口启用 Win11 **Mica 背景**（`Window.SystemBackdrop` + `MicaBackdrop`，
  XAML 声明）并整页可滚动（外层 `ScrollViewer`）——纯黑背景移除。

## P6-11（2026-09-09）：英语听歌 [Music] 抽搐修复

- 现象：英语识别（whisper.en，听歌曲）反复出现 "[Music]"，日志刷屏，歌词不出。
- 根因（子代理分析，全部源码/词表/抓音证实）：
  1. **"[Music]" 是 BPE 字面 token**（whisper.en 词表 pieces[22648]，无 `<|music|>` special token），
     模型听伴奏/人声混音时贪心解码直接输出它；
  2. **VAD 无法区分音乐与语音**：RMS 阈值下音乐段 99.7% 语音判定 → 整首曲子成一个连续段，
     直到 15s ring 上限强制 finalize → whisper 把 15s 音乐块压成 "[Music]"；
  3. **partial 每 1.2s 重新编码整段** → 日志每 1.2s 一条 "[Music]"（"抽搐"）；
  4. **mono 路径掉首 token**：prompt 3 token 却 `ids.Skip(4)` → 真实歌词也丢第一个字；
  5. GUI 无文件日志（只有内存 LogBox），采集/识别全程不可事后检查。
- 修复：
  - `DecodeStateful`/`DecodeFullPrefix`：`Skip(mono ? 3 : 4)` —— 保留首个生成 token；
  - `AsrJunkFilter.StripAnnotations`（P6-11）：剥掉行首 "[Music]" 等注解、保留其后歌词
    （"[Music] Hello love" → "Hello love"），纯注解整行丢弃；顺带硬化解析（`<|music|>`、
    `[Music playing]`、`[Instrumental]`、`[noise]`、`♪ ♪` 均判 junk，`'<' '>' '"' '.' '!'` 入 trim）；
    `AppServices` 改用 StripAnnotations 后再入字幕/翻译；
  - 识别器 junk 突发抑制：连续 3 次 junk 解码后跳过 partial 编码（不再每 1.2s 全段重编码），
    且 junk 解码记 Debug 级日志，LogBox 不再刷屏；
  - 连续语音 cap 15s → **8s**（MaxRingSamples 240000→128000），音乐段变短、转写更准；
  - VAD 参数接入配置（VadConfig 0.01/150/400，原硬编码 0.02/150/500）+ 删掉 CaptureFeeder
    里结果被丢弃的第二 VAD；
  - **GUI 文件日志**：`%LOCALAPPDATA%\RealtimeSubtitle\logs\app-<时间戳>.log`（LogSink.LogFile）。
- 验证：59/59 单测；whisper.en 实测 "Hello / a test of the real-time subtitle system / …"
  全对（首 token 不再丢）。**模型层限制仍存在**：whisper.en base/small 对带伴奏歌声仍会输出
  "[Music]"（junk 突发抑制保证不刷屏、不占字幕/翻译，但歌词识别率有限）；要歌词级识别需
  更大的音乐适配模型（如 whisper large-v3）或专门的"音乐模式"。

## P6-12（2026-09-09）：翻译设备选择失效修复 + NPU/CPU 实测

- **Bug**：GUI"翻译设备"下拉（auto/NPU/CPU）的选择从未传进运行时——`AppServices.cs`
  构造器硬编码 `new OpenVinoTranslator(path, "auto", ...)`，忽略 `config.Translation.Device`，
  用户选 CPU/NPU 均无效（实测复现：device 恒为 auto 路径）。
- **修复**：AppServices 改为读取 `config.Translation.Device`（空→"auto"）传给 translator，
  并在启动日志打印实际设备。GUI 选择现在真实生效。
- **实测（本机 en→zh，50 句基准中位数）**：
  - 解码器：CPU **2.5ms/步**（p50 2.3ms） vs GPU.0 5.3ms/步（iGPU 更慢，弃用）；
  - 编码器：CPU 3.5ms vs **NPU 4.3ms**（静态 [1,64] 固定 64 token 输入，NPU 不占优）；
  - 稳态单句：CPU 15ms vs NPU 20ms（NPU 反而慢 ~33%）；
  - 解码器全静态 reshape 后尝试 NPU 编译仍失败（Level0 pfnCreate / 内部动态形状），
    NPU 不可编译解码器维持 CPU。
- **结论/建议**：对 marian 翻译，NPU 无速度收益（编码器固定 64 token 填充抵消 NPU 优势，
  解码器又必须 CPU）。追求低延迟应选 **CPU**；GUI 默认 auto 保持尝试 NPU 编码器
  （NPU 编译失败自动回退 CPU），用户可在"翻译设备"下拉显式选 CPU 获得最快路径。

## P6-13（2026-09-09）：翻译结果不触发字幕 UI 更新的确定性 bug 修复

- 现象：双语字幕只有原文、译文行永远为空；ASR/字幕流正常。
- 根因（代码审查确认，非偶发）：
  1. `SubtitleManager.OnTranslated` 用 `Current.SourceText == 请求原文` **字符串全等匹配**回填；
  2. `OnSourcePartial` 无条件改写 `Current.SourceText`（不管该行是否已 final/翻译中）；
  3. 连续语音时下一句 partial 先到达 → 当前行文本被改成新句 → 翻译 A 回填时匹配失败 →
     译文静默丢弃 → UI 只显示原文。
- 修复（按行 ID 回填，不再靠文本匹配）：
  - `TranslationRequest.SubtitleLineId`（stable line id）；
  - `SubtitleManager.OnSourceFinal` 返回行 ID；`OnTranslated(lineId, …)` 按 ID 落行
    （Current 优先，历史行保持数据一致）；保留文本匹配重载兼容旧调用；
  - `OnSourcePartial` 只在 Recognizing 态更新当前行——翻译中/已完成的行不被新语音 partial
    覆盖，下一个 partial 开新预览行；
  - `AppServices`：Final 拿 lineId 入队 → Completed 按 `request.SubtitleLineId` 回填。
- 验证：新增 `SubtitleManagerTests` 5 例（按 ID 更新 UI / 下一句 partial 不污染翻译中行 /
  未知 ID 安全忽略 / 文本重载兼容 / partial 预览更新）；64/64 单测通过。
  GLM-4.7F 一次性调查（newapi/glm-zai-glm-4.7-flash）确认无其他残留主题/字幕钉子；
  其 DWMWA 补丁建议含编译缺陷未采纳（标准做法：无 RequestedTheme + MicaBackdrop 即跟随系统）。

## P6-14（2026-09-09）：歌词听歌场景翻译"跟不上"——final 稀疏 + partial 无翻译

- 现象：日文歌词场景原文每 ~1.2s 滚动更新，但译文几乎不出现/总慢半拍；
  用户报告"翻译速度慢，跟不上歌词"。
- 实测证据（app 日志）：SenseVoice（ja）输出 `partial` 每 ~1.2s 一次，但 `final`
  极稀疏——连续演唱时 VAD 的 500ms hangover 恒不满足，整段歌词（10-15s）才一个 final。
- 根因：**翻译只挂在 `Final` 事件上**（AppServices.BindRecognizer），partial 只更新原文
  预览。歌词连续演唱 → final 频率 ≈ 0.1/s → 译文几乎不触发，感知为"翻译慢/跟不上"。
- 修复：
  1. `AppServices.BindRecognizer`：partial（清理后非空）也入队翻译，带 `SubtitleLineId`
     （P6-13 行 ID 回填复用）；用 `ShouldTranslatePartial` 节流去重（同文本跳过 + 至少
     ~700ms 间隔），防止队列被近重复 partial 淹没；final 仍按原路径最终修正。
  2. `SubtitleManager.OnSourcePartial` 返回所在行 ID（partial 翻译按 ID 落行）；
  3. `SubtitleManager.Publish`：Recognizing 状态行若有已回填译文也显示（partial 级译文
     随歌词滚动，bilingual/仅译文两模式均生效），不必等 Completed。
  4. `OpenVinoSenseVoiceRecognizer` 接收 `config.Vad`（阈值/min/hang 不再硬编码
     0.02/150/500），与 whisper classic 一致——歌词场景可调 hangover 获得更频繁 final。
- 验证：新增 `SubtitleManagerTests` 2 例（partial 译文按 ID 落预览行且 Recognizing 即显示 /
  下一句 partial 译文不污染已 final 行）；66/66 单测通过，构建 0 警告 0 错误。

## P6-15（2026-09-09）：标题栏不随 Windows 深浅色变化

- 现象：主窗口标题栏不跟随系统"默认应用模式"（浅色/深色）切换。
- 根因：`AppWindowTitleBar.PreferredTheme` 默认值即 `TitleBarTheme.Legacy`——
  **Legacy 不跟随系统 light/dark**；此前仅移除 App.xaml 的 RequestedTheme（影响窗口
  内容/Mica，不影响 caption），所以标题栏仍纹丝不动。
- 修复（MainWindow）：
  1. 构造时 `AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode`
     （DWM 按 Windows 默认应用模式渲染 caption）；
  2. 监听 `UISettings.ColorValuesChanged`（系统主题实时切换事件）→ 派发到 UI 线程
     重新应用该主题，运行中切换深浅色立即刷新，无需重启应用。
- 验证：构建通过；需 GUI 手测（系统设置切换深浅色看标题栏/按钮颜色跟随）。

## 待办（Phase 5 范围）

- [x] whisper 经典 API 自实现（mel 谱 + BPE decode + 贪心；U4a 解阻塞）→ ASR 三语 U2/U3 验证、NPU/CPU 延迟实测
- [x] LegacySpeechRecognizer（Windows.Media）实装
- [x] 覆盖层真透明（P5-3 DWM 瞬态背景）与模型自动补齐（P5-4，含离线 zip 实测）
- [x] 控制面板 UI 完善（模式/字幕样式选择、模型状态、下载进度、日志卡片）
- [ ] 资源占用压测（CPU/内存/NPU/RTX 占用）与错误矩阵走测（含设备热插拔重连）
- [ ] MSIX 正式签名、OpenVINO 运行时捆绑清单、首次运行向导
- [ ] 全屏游戏叠字手动验证（清单 Phase 4 手动项）