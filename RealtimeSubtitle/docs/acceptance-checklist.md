# 验收清单 (Acceptance Checklist)

> 每阶段结束运行对应节；全部通过才进入下一阶段。记录日期与结果。

## Phase 1 — 项目骨架 + 系统音频采集

- [x] 环境：Windows 11 24H2+（Build 26200）
- [x] 环境：.NET 8 可构建（SDK 10 构建 net8.0）
- [x] 环境：NPU 检出（Intel(R) AI Boost，PCI AD1D，驱动 32.0.100.5540）
- [x] `dotnet build` 全解决方案成功（App/ Core / Windows / Tools / Tests）
- [x] `dotnet test` 全绿（21/21：Resampler / VAD / RingBuffer / Config）
- [x] WavDumpTool 真机捕获成功：phase1b.wav 16k mono PCM 11.22s，WAV 头校验通过（2026-09-08）
- [x] 实时输出 RMS/VAD：静音 silence、蜂鸣 SPEECH（RMS≈0.35），行为符合阈值语义
- [x] 静音时 CPU 占用观察：事件驱动，消费无瓶颈（15s 连续音全程实时）
- [ ]（手动）拔插音频设备后工具可恢复 —— 已实现 Error 事件与 `AudioDeviceLostException`，完整重连回退留待 Phase 5 精修

日期：2026-09-08　结果：**通过（1 项手动项留待 Phase 5）**

## Phase 2 — Windows AI Speech 集成（**硬件裁决后 pivoted，见 decisions.md P2-3**）

- [x] 任务 0 版本钉死：WinAppSDK **2.4.1-experimental** + `TargetPlatformVersion 10.0.26226.0`（编译探测实证；稳定 2.4.0 / 2.2.2-exp9 / OS 投影单独均不含 `Microsoft.Windows.AI.Speech`）
- [x] MSIX 打包 + `systemAIModels` 能力 + `MaxVersionTested=26226`；`Add-AppxPackage -Register` 开发者模式松散注册获包标识（PFN `RealtimeSubtitle.App_kapzmtexe8rqg`）；打包 App 无头探测 `--ai-probe`/`--ai-setup` 可运行
- [x] `SpeechModelManager`（四分支骨架 + 容错：`GetReadyState` 永不抛错）+ `WindowsAiSpeechRecognizer`（provider/streaming 事件）编译通过；`ISpeechRecognizer`/`AsrSession`/`IPcm16Sink` 抽象完成
- [x] **硬件裁决**：本机 `GetReadyState` 与 `EnsureReadyAsync` 均 `0x8A1F022F` 瞬时失败（非"模型未下载"）；上游 [WindowsAppSDK#6561](https://github.com/microsoft/WindowsAppSDK/issues/6561) 同类 open → **本机 Windows AI Speech 不可用，P2-3 定案**
- [x] 遗留实测项迁移：U2（语言支持面）/U3（延迟）/U6（缓冲复用）改由 **Whisper-via-OpenVINO** 链路承接（Phase 3）；Whisper 多语言天然覆盖 U2
- [ ] 待 Phase 3：Whisper 识别端到端验收（AsrConsole 等价验证）

日期：2026-09-08　结果：**以 P2-3 pivoted 通过（ASR 后端换 Whisper/OpenVINO；MSIX/缝隙/探测机制保留）**

## Phase 3 — OpenVINO 翻译（进行中）

- [x] 任务 0：绑定 `JYPPX.OpenVINO.CSharp.API 3.3.1` + `JYPPX.OpenVINO.GenAI.runtime.win 2026.3.0`；真机 `GetAvailableDevices()` = CPU/GPU.0/GPU.1/**NPU**（P3-1）
- [x] 模型管线 `tools/model-convert/convert_models.py`：whisper-tiny INT8（encoder/decoder/tokenizer ~45MB）、opus-mt-en-zh INT8（encoder 50MB+decoder 57MB）、tokenizer.json（special ids）、shapes.txt；venv 组合固定 optimum-intel 2.1.0 + transformers 4.57.6 + openvino 2026.3.1
- [x] `OpenVinoTranslator`（经典 Core API：encoder+decoder 分离动态图、贪心、eos=0/decoder_start=65000、重复后缀裁剪）实测：**请开门 / 我们现在需要行动了 / 你要去哪里? 来**（P3-2/3/4）
- [x] `TranslationQueue`（有界丢最旧）、`TranslationContext`、`DeviceSelector`（自动择 CPU）、Tools `--translate`/`--bench`
- [x] marian 性能达标：`--bench`（50 句×3 轮）CPU 中位 **~24ms/句**
- [ ] whisper ASR 端到端：**上游契约失配阻塞 U4a**（P3-5）——optimum 2.0/2.1/1.25 导读出的 whisper 解码器均无 `beam_idx`，GenAI 2026.3 whisper 运行时无条件要求它；`is_beam_search` 只读不可关。出路：Phase 5 自实现 whisper 经典 API（mel+BPE）或等上游修复；已备好 ISpeechRecognizer 缝隙
- [ ] ASR 三语 U2 验证（en/zh/ja）、whisper NPU/CPU 延迟（U4a）——随 U4a 阻塞顺延

日期：2026-09-08　结果：**翻译侧通过；ASR 侧 U4a 上游阻塞（已记录出路）**

## Phase 4 — WinUI 3 覆盖层端到端闭环（2026-09-08）

- [x] `WindowsInterop`（F11 组合）：`WS_EX_TRANSPARENT|NOACTIVATE|TOOLWINDOW` + 可选 LAYERED alpha + `DWMWCP_DONOTROUND` + `IsAlwaysOnTop` + `IsShownInSwitchers=false` + 主屏底部居中定位（`DisplayArea` work area + `Position.YRatio`）
- [x] `SubtitleBar`（双语两行 TextBlock，半透明圆角 Border，无动画/Blur）+ `OverlayWindow`
- [x] 端到端装配 `AppServices`：recognizer 缝隙 → `SubtitleManager` → `TranslationQueue`(真实 OpenVINO marian) → DispatcherQueue → 覆盖层（P2-3 缝隙即插即用）
- [x] **`--overlay-probe` 实测**：`STYLES=TRANSPARENT|NOACTIVATE|TOOLWINDOW|LAYERED`、`RECT=680,1072,1880,1242`（1200×170 底部居中）、双语快照渲染 ✓
- [x] **`--demo` 端到端实测**：DemoFeed（ASR 站桩）→ marian 翻译 → 覆盖层，末句 "We made it." → **"我们成功了"** 上屏 ✓
- [x] `MainWindow` 控制面板（开始/停止演示、状态、日志）
- [x] 默认 MSIX 构建 0 错误；单测 21/21 保持绿
- [ ]（手动项）真实全屏 DX12 游戏叠字/置顶/穿透验证 —— 需游戏环境人工操作（机制类检查已由 probe 覆盖）

日期：2026-09-08　结果：**通过（1 项手动游戏验证留待人工）**

## Phase 5 — 优化/健壮性/打包（进行中，2026-09-08）

- [x] **whisper 经典 API 自实现（U4a 解除，P5-1）**：C# mel 谱 + GPT2 BPE 解码 + 贪心（语言自动检测）→ `OpenVinoWhisperClassicRecognizer`
- [x] **三语 U2 实测**：en "Hello."/"Big brown fox jumps over the lazy dog."/"A test of the realtime subtitle system."；zh "你好"/"中文语音识别正在进行"；ja "こんにちは"/"これは字幕システムのテストです"/"日本語の音声認識をテストしています" —— **全部正确，单段 168-371ms（U3 达标）**
- [x] whisper 编码器 NPU：动态维度触发 vpux 原生崩溃 → **v1 全 CPU**（P5-2 静态化后续）
- [x] 资源快照（demo 12s）：CPU 均值 ~18%（翻译突发）、**工作集 401MB 稳定零增长**（无泄漏；>300MB 目标，记为调优项）
- [x] **真声闭环（产品完整链路）**：`--live` 实测——扬声器播放英文 TTS → WASAPI 回环 → **真 Whisper 识别** → **Marian 翻译** → 透明覆盖层双语上屏；`COMPLETED_TRANSLATIONS=4`、末行 "local speech recognition on this computer."→"本地语言识别"、样式/位置全对 ✓
- [x] 错误矩阵（实现+自动覆盖）：队列溢出丢弃（单测 `TranslationQueueTests` 23/23 覆盖）；模型缺失（MainWindow/`--live` 明确报错）；设备热插拔重连（`CaptureFeeder.OnCapturerError` 退避 3 次——真机拔插为手动项）
- [x] **LegacySpeechRecognizer（Windows.Media）实装**：连续识别（`ContinuousRecognitionSession.ResultGenerated`→Final、`HypothesisGenerated`→Partial），编译通过（麦克风实测为手动项）
- [x] 打包：开发者证书 + `Add-AppxPackage -Register` 松散注册（PFN 可安装）；非打包变体联调；README 含构建/模型/运行指引（正式签名/商店分发=手动项）
- [x] **覆盖层真透明（P5-3/P5-7）**：WinUI colorkey 与 DWM 瞬态背景均实测黑底 → **pivoted 到 WPF 逐像素透明**（`RealtimeSubtitle.Overlay.Wpf`，UpdateLayeredWindow）；probe 实测 `STYLES=TRANSPARENT|NOACTIVATE|TOOLWINDOW|LAYERED`、底部居中、demo/live 上屏 ✓（目视最终确认）
- [x] **ASR 默认 Windows 语音识别（P5-5）**：`Windows.Media.SpeechRecognition` 流式（Hypothesis→Partial/Result→Final），麦克风免模型；whisper 回环后端保留可选
- [x] **翻译 NPU（P5-6）**：encoder 静态 [1,64] 一次编译跑 NPU + decoder CPU（decoder 动态不可 NPU 编译，`--npu-probe` 实证）；混合 34ms vs 纯 CPU 29ms；单句 ~100ms
- [x] **模型自动补齐（P5-4）**：多根目录解析 + 离线 zip（`tools/model-convert/dist/*.zip`，本机实测解压→翻译/识别可用）+ `Models.BaseUrl` 网络源；`--download-models` 工具 + GUI"下载缺失模型"按钮 + 启动时自动补齐
- [x] **控制面板 UI 完善**：运行模式（演示/真声直播）+ 字幕样式 + 语音引擎（Windows 语音识别/Whisper）+ 翻译设备（自动/NPU/CPU）+ 自定义翻译模型目录；模型状态灯与路径、下载进度条、日志卡片、运行中统计
- [ ] 全屏 DX12 游戏叠字手动项（机制项已由 `--overlay-probe` 覆盖：穿透/置顶/不抢焦点/定位全过）

日期：2026-09-08　结果：**自动化可验收项全部通过；覆盖层（WPF 逐像素透明）/Windows 语音识别/翻译 NPU 已就绪（目视/热插拔/麦克风/游戏实景/正式签名等手动项待人工）**