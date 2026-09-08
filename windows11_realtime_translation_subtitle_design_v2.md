# Windows 11 实时翻译字幕系统设计 v2.0

## 1. 项目定位

构建一个运行于 **Windows 11 24H2 及以上版本**的本地实时语音翻译字幕系统。

核心目标：

- 从游戏、视频或其他系统输出中获取音频。
- 使用 Windows 原生 Windows AI Speech Recognition API 进行本地语音识别。
- 使用 OpenVINO 调用 Intel NPU 执行本地机器翻译。
- 使用 C# 完成核心业务逻辑、任务调度、字幕处理和 UI。
- 使用 WinUI 3 / Windows App SDK 创建低开销透明字幕覆盖层。
- 尽可能降低 CPU、内存和 GPU 占用，避免影响游戏性能。
- RTX 5060 默认不参与 ASR 和翻译计算，将 GPU 资源留给游戏或视频渲染。

---

## 2. 平台与技术约束

### 系统
仅支持 Windows 11 24H2+，最低 Build 26100。

### 核心语言
```text
C#
.NET 8+
```

### Windows 原生组件
- Windows Runtime
- Windows AI APIs
- Windows AI Speech Recognition
- WASAPI
- WinUI 3
- Windows App SDK
- Windows 音频设备 API
- Windows 窗口与桌面 API

不使用 Python、Electron、Qt、Node.js、Java 或第三方音频采集框架。

### AI 推理例外
机器翻译允许使用 **OpenVINO**。OpenVINO 仅作为翻译推理后端，不负责整个程序的业务逻辑。

---

# 3. 总体架构

```text
游戏 / 视频 / 系统声音
        │
        ▼
   WASAPI Loopback
        │ PCM
        ▼
Windows AI Speech Recognition
        │
        │ 实时 ASR
        ▼
   C# ASR 状态管理器
        │
        ▼
   翻译任务队列
        │
        ▼
     OpenVINO
        │
        ▼
   Intel NPU
        │
        ▼
  C# Subtitle Manager
        │
        ▼
 WinUI 3 透明字幕层

RTX 5060：
默认不参与 ASR / 翻译，保留给游戏和图形渲染。
```

---

# 4. 音频采集

使用 Windows 原生 **WASAPI Loopback** 捕获当前播放设备的系统声音。

```text
游戏
 ↓
Windows Audio Engine
 ↓
默认播放设备
 ↓
WASAPI Loopback
 ↓
PCM
```

音频层负责：

- PCM 缓冲
- 环形缓冲区
- 声道处理
- 采样率转换
- 音量检测
- 静音检测
- 音频分段

推荐 Ring Buffer，避免频繁分配数组造成 GC 压力。

加入轻量级 VAD：

```text
Audio
 ↓
VAD
 ├── 无语音 → 丢弃
 └── 有语音 → ASR
```

---

# 5. Windows AI Speech Recognition

使用 Windows 11 24H2+ 的：

```text
Windows AI Speech Recognition API
```

优先使用：

```text
StreamingRecognition
```

基本流程：

```text
检查 SpeechRecognitionModel 状态
        ↓
EnsureReadyAsync()
        ↓
TryCreateAsync()
        ↓
创建 AudioConfiguration
        ↓
创建 StreamingRecognition
        ↓
监听 Recognized
        ↓
StartContinuousRecognitionAsync()
```

ASR 结果区分：

```text
Partial Result
Final Result
```

推荐：

```text
Partial
 ↓
仅更新当前字幕预览

Final
 ↓
进入翻译队列
```

不要对每个 Partial Result 都进行翻译，以避免 NPU 重复推理和队列堆积。

---

# 6. ASR 状态管理

建立：

```text
AsrSession
```

维护：

- 当前识别文本
- 当前 phrase
- Partial 文本
- Final 文本
- 时间戳
- 语言
- 识别状态

状态机：

```text
Idle
 ↓
Listening
 ↓
Recognizing
 ├── Partial
 └── Final
       ↓
    Completed
```

---

# 7. 翻译系统

## 7.1 推理后端

机器翻译使用：

```text
OpenVINO
```

目标设备：

```text
Intel NPU
```

概念调用：

```csharp
core.CompileModel(model, "NPU");
```

实际代码根据使用的 OpenVINO C# API / NuGet 版本调整。

## 7.2 模型

优先选择：

- 小型机器翻译模型
- 低延迟模型
- 支持静态或可控输入输出尺寸
- Intel NPU 可编译
- 支持目标语言组合

优先考虑：

```text
Marian
M2M100
其他 OpenVINO NPU 兼容翻译模型
```

实时字幕不追求超大型通用 LLM。

优先级：

```text
延迟 > 稳定性 > 准确率 > 模型体积
```

## 7.3 设备策略

默认：

```text
NPU
```

失败时：

```text
NPU
 ↓
失败
 ↓
CPU fallback
```

不默认使用 GPU，避免占用 RTX 5060。

---

# 8. 翻译任务队列

使用异步生产者/消费者模型：

```text
ASR Final
    ↓
Translation Queue
    ↓
Translation Worker
    ↓
OpenVINO NPU
    ↓
Translation Result
```

实时字幕不应该无限排队。

原则：

> 实时字幕优先保证“最新”，而不是保证所有历史文本都被翻译。

如果旧任务已经失去实时价值，可以根据策略丢弃。

---

# 9. 翻译上下文

维护：

```text
TranslationContext
```

保存最近少量句子，例如：

```text
Sentence N-2
Sentence N-1
Current Sentence
```

默认建议：

```text
最近 2~3 句
```

严格限制上下文长度，避免增加 NPU 推理时间和延迟。

---

# 10. 字幕管理器

建立：

```text
SubtitleManager
```

负责：

- 原文
- 译文
- 时间戳
- 当前字幕
- 历史字幕
- 字幕过期
- 字幕刷新
- 双语显示

数据结构：

```text
SubtitleItem
 ├── Id
 ├── SourceText
 ├── TranslatedText
 ├── StartTime
 ├── EndTime
 └── State
```

状态：

```text
Recognizing
Translating
Completed
Expired
```

---

# 11. 双语字幕

默认：

```text
原文
Translation
```

例如：

```text
Where are you going?

你要去哪里？
```

支持：

```text
只显示原文
只显示译文
原文 + 译文
```

---

# 12. WinUI 3 字幕覆盖层

UI 使用：

```text
WinUI 3
Windows App SDK
```

设计目标：

- 无边框
- 透明背景
- 始终置顶
- 可穿透鼠标
- 不抢游戏焦点
- 支持全屏/窗口游戏
- 支持位置调整
- 支持字体大小调整

避免复杂动画、Blur、粒子效果和高频 UI 重建。

---

# 13. 线程模型

```text
Audio Thread
     │
     ▼
Audio Buffer
     │
     ▼
ASR Task
     │
     ▼
Translation Channel
     │
     ▼
NPU Worker
     │
     ▼
Subtitle Manager
     │
     ▼
UI Dispatcher
```

原则：

- 音频采集不能阻塞。
- ASR 不阻塞 UI。
- NPU 推理不阻塞 UI。
- UI 不直接执行推理。
- 使用 async/await。
- 使用 Channel / CancellationToken 管理任务。

---

# 14. 低资源占用策略

## CPU

避免高频轮询：

```csharp
while (true)
{
    // 不推荐
}
```

优先使用：

```text
Event
Async
Channel
WaitHandle
```

## 内存

避免：

```text
大量短生命周期 byte[]
大量 string 拼接
频繁创建对象
```

推荐：

- ArrayPool
- Ring Buffer
- StringBuilder
- 对象复用
- 有限长度字幕缓存

## GPU

默认不进行 AI 推理。

RTX 5060 保留给：

```text
游戏
视频渲染
其他 GPU 工作
```

## NPU

NPU Worker 没有任务时保持等待：

```text
NPU Worker → Waiting
```

有任务才执行：

```text
Queue
 ↓
Inference
 ↓
Result
 ↓
Waiting
```

---

# 15. 延迟目标

端到端：

```text
音频
 ↓
ASR
 ↓
翻译
 ↓
字幕
```

目标：

```text
< 500 ms
```

优秀目标：

```text
200 ~ 400 ms
```

具体延迟取决于音频缓冲、ASR 输出速度、语言、翻译模型、NPU 推理时间和字幕刷新策略。

---

# 16. 字幕刷新策略

不推荐：

```text
每个 ASR token
 ↓
翻译一次
```

推荐：

```text
Partial
 ↓
原文实时显示

Final
 ↓
提交翻译
 ↓
NPU
 ↓
更新译文
```

这样能够显著降低 NPU 推理次数。

---

# 17. 错误处理

## ASR 初始化失败

```text
Speech API unavailable
        ↓
显示错误
        ↓
停止 ASR
```

## OpenVINO NPU 编译失败

```text
NPU compile failed
        ↓
CPU fallback
```

## NPU 推理异常

```text
Inference Error
        ↓
重新初始化
        ↓
失败
        ↓
CPU fallback
```

## 音频设备断开

```text
Audio Device Lost
        ↓
重新枚举设备
        ↓
重新建立 Loopback
```

---

# 18. 配置文件

推荐 JSON：

```json
{
  "sourceLanguage": "auto",
  "targetLanguage": "zh-CN",
  "subtitleMode": "bilingual",

  "audio": {
    "device": "default",
    "sampleRate": 16000,
    "channels": 1
  },

  "asr": {
    "mode": "streaming"
  },

  "translation": {
    "backend": "openvino",
    "device": "NPU",
    "model": "marian"
  },

  "subtitle": {
    "fontSize": 32,
    "maxLines": 2,
    "showSource": true,
    "showTranslation": true
  }
}
```

---

# 19. 项目目录

```text
RealtimeSubtitle/
│
├── RealtimeSubtitle.sln
│
├── RealtimeSubtitle.App/
│   ├── App.xaml
│   ├── MainWindow.xaml
│   └── UI/
│
├── RealtimeSubtitle.Core/
│   ├── Audio/
│   │   ├── WasapiLoopback.cs
│   │   ├── AudioBuffer.cs
│   │   └── VoiceActivityDetector.cs
│   │
│   ├── Speech/
│   │   ├── SpeechRecognizer.cs
│   │   ├── AsrSession.cs
│   │   └── AsrResult.cs
│   │
│   ├── Translation/
│   │   ├── TranslationEngine.cs
│   │   ├── OpenVinoTranslator.cs
│   │   ├── TranslationQueue.cs
│   │   └── TranslationContext.cs
│   │
│   ├── Subtitle/
│   │   ├── SubtitleManager.cs
│   │   └── SubtitleItem.cs
│   │
│   └── Configuration/
│
├── RealtimeSubtitle.Windows/
│   ├── WindowsAiSpeech.cs
│   ├── WindowsAudio.cs
│   └── WindowsInterop.cs
│
└── Models/
    └── Translation/
```

---

# 20. 推荐技术栈

| 模块 | 技术 |
|---|---|
| 系统 | Windows 11 24H2+ |
| 核心语言 | C# |
| Runtime | .NET |
| UI | WinUI 3 |
| Windows 框架 | Windows App SDK |
| 系统音频 | WASAPI |
| ASR | Windows AI Speech Recognition API |
| 翻译 | OpenVINO |
| 翻译设备 | Intel NPU |
| GPU | 默认不使用 RTX 5060 |
| 异步 | async/await |
| 队列 | Channel |
| 音频缓冲 | Ring Buffer |
| 配置 | JSON |

---

# 21. 硬件资源分配

以 Intel Core Ultra 7 255HX + RTX 5060 为例：

```text
┌───────────────────────┐
│ Core Ultra 7 255HX    │
├───────────────────────┤
│ CPU                   │
│ 音频处理              │
│ C# 业务逻辑           │
│ Windows API           │
│ UI                    │
├───────────────────────┤
│ Intel NPU             │
│ OpenVINO 翻译         │
└───────────────────────┘

┌───────────────────────┐
│ RTX 5060              │
├───────────────────────┤
│ 游戏渲染              │
│ 光追                  │
│ Lumen                 │
│ 其他 GPU 工作         │
└───────────────────────┘
```

核心思想：

> 让 NPU 承担适合它的 AI 推理工作，把 RTX 5060 留给游戏。

---

# 22. MVP 开发阶段

## Phase 1：系统音频

```text
WASAPI Loopback
 ↓
PCM
 ↓
音频缓冲
```

## Phase 2：Windows AI ASR

```text
Audio
 ↓
Windows AI Speech Recognition
 ↓
Streaming Result
```

## Phase 3：OpenVINO NPU 翻译

```text
Text
 ↓
OpenVINO
 ↓
Intel NPU
 ↓
Translation
```

## Phase 4：字幕系统

```text
ASR
 ↓
Translation
 ↓
Subtitle Manager
 ↓
WinUI Overlay
```

## Phase 5：低资源优化

重点测试：

- CPU 占用
- 内存占用
- NPU 利用率
- GPU 占用
- ASR 延迟
- 翻译延迟
- 字幕延迟

---

# 23. 最终数据流

```text
┌──────────────┐
│ 游戏 / 视频  │
└──────┬───────┘
       │
       ▼
┌──────────────┐
│ WASAPI       │
│ Loopback     │
└──────┬───────┘
       │
       ▼
┌──────────────┐
│ VAD / Buffer │
└──────┬───────┘
       │
       ▼
┌────────────────────────┐
│ Windows AI Speech      │
│ Recognition            │
└──────────┬─────────────┘
           │
           ▼
      Final Text
           │
           ▼
┌────────────────────────┐
│ Translation Queue      │
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│ OpenVINO               │
│ Intel NPU              │
└──────────┬─────────────┘
           │
           ▼
      Translation
           │
           ▼
┌────────────────────────┐
│ Subtitle Manager       │
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│ WinUI 3 Overlay        │
└────────────────────────┘
```

---

# 24. 设计原则总结

1. **只支持 Windows 11 24H2+。**
2. **核心代码全部使用 C#。**
3. **Windows 能原生解决的问题优先使用 Windows 原生 API。**
4. **系统音频使用 WASAPI。**
5. **ASR 使用 Windows AI Speech Recognition API。**
6. **翻译使用 OpenVINO。**
7. **OpenVINO 翻译默认使用 Intel NPU。**
8. **RTX 5060 默认不参与 AI 推理。**
9. **优先低延迟，而不是大模型。**
10. **优先低资源占用，不影响游戏性能。**
11. **使用异步、事件驱动和有限缓存，避免后台高频轮询。**
12. **只翻译 Final ASR 结果，避免 NPU 重复计算。**
13. **翻译队列允许丢弃过期任务，保证实时性。**
14. **UI 使用轻量 WinUI 3 透明覆盖层。**
15. **所有 AI 推理尽可能本地完成，不依赖云端服务。**

---

# 25. 最终目标

```text
启动程序
   ↓
选择系统音频设备
   ↓
开始监听
   ↓
游戏 / 视频正常运行
   ↓
实时识别语音
   ↓
Intel NPU 实时翻译
   ↓
屏幕显示双语字幕
```

最终形成：

> **Windows 11 24H2+、C# 原生核心、Windows AI ASR + OpenVINO Intel NPU 翻译、低资源占用的本地实时翻译字幕工具。**
