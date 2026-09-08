# RealtimeSubtitle — Windows 11 实时翻译字幕（本地）

基于 v3.0 规划（`windows11_realtime_translation_subtitle_design_v2.md`）实现的本地实时双语字幕工具：
**WASAPI 回环采集系统音频 → Whisper(OpenVINO) 本地 ASR → MarianMT(OpenVINO) 本地翻译 → WinUI 3 透明置顶覆盖层**。
全程不依赖云端、不使用 RTX 5060（GPU 路径不存在）。

## 当前状态（2026-09-08，Phase 1-4 通过；Phase 5 进行中）

| 能力 | 状态 | 说明 |
|---|---|---|
| WASAPI loopback 采集 | ✅ | `RealtimeSubtitle.Windows/WindowsAudio.cs`；mix 格式自动适配，事件驱动 |
| ASR（Whisper 回环，默认） | ✅ | **识别语言下拉（自动/中文/英语/日语）按语言路由专精模型**：自动→多语言 Whisper（tiny/base/small 热切换）、英语→**whisper.en**、中文→**Qwen3-ASR**（准、~0.5-2s/段、仅终稿）、日语→**SenseVoice**（单程快速）；扬声器/系统音频回环，无需麦克风权限；流式 partial + 段级 final；NPU 编码器 + KV-cache 状态解码器（P6-2/P6-4） |
| ASR（Windows 语音识别，可选） | ✅ | `Windows.Media.SpeechRecognition`（麦克风，需权限）；GUI 下拉可选 |
| 翻译（Marian opus-mt） | ✅ | **按源语言路由**：en/auto→**en→zh**、日语（SenseVoice）→**ja→zh** 专用模型（P6-10）；GUI"翻译设备"选择真实生效（P6-12，原硬编码 auto）：**auto**=NPU(enc)+CPU(dec) 混合（encoder 静态 [1,64] 一次编译跑 NPU；decoder 动态不可 NPU 编译 → CPU）；**CPU**=全 CPU 最快（实测稳态 15ms/句 vs NPU 20ms/句，NPU 对翻译无速度收益）；**解码器启用隐式 KV cache，68-119ms/句含首编译**（P6-8，~1.5-1.7x） |
| 覆盖层（真透明） | ✅ | **WPF 逐像素透明**（UpdateLayeredWindow，业界标准）；穿透/置顶/不抢焦点/底部居中 |
| 端到端链路 | ✅ | `--demo`（录播源）→ 翻译 → 覆盖层上屏验证通过 |
| 真声闭环 | ✅ | `--live` 探针实测通过（Windows 语音识别/whisper → 翻译 → 覆盖层） |
| 模型自动补齐 | ✅ | 多根目录解析 + 本地离线包（dist zip）+ 可配置网络源（Models.BaseUrl） |
| 控制面板 UI | ✅ | 模式/字幕样式/语音引擎/翻译设备选择、自定义翻译模型路径、模型状态灯、下载进度、日志卡片；**Win11 Mica 背景 + 整页可滚动**（P6-10） |
| Windows AI Speech（微软新 API） | ⚠️ | 本机硬件不可用（`0x8A1F022F`，上游 [WindowsAppSDK#6561](https://github.com/microsoft/WindowsAppSDK/issues/6561)）；能力机器上可配置 |

## 构建

前置：.NET 8 SDK（本机 10.0.400 亦可用）、Python 3.10+（仅模型转换）、Intel NPU 驱动（可选）。

```powershell
# 环境（沙箱/公司网络必设，见 docs/decisions.md）
$env:TMP=$env:TEMP="$PWD\.tmp"; $env:NUGET_PACKAGES="$PWD\.nuget-packages"
Remove-Item Env:SSL_CERT_FILE -ErrorAction SilentlyContinue

dotnet build -c Debug                      # 全解决方案（App 默认 MSIX 打包）
dotnet test RealtimeSubtitle.Tests -c Debug
```

## 模型（一次性，需网络）

```powershell
# whisper 语音识别模型（默认 base；--whisper-size tiny/small 可选）
python tools\model-convert\.venv\Scripts\python.exe tools\model-convert\convert_models.py --whisper-size base --only whisper
# 语言专精模型（P6-4）：英语 whisper.en（尺寸同上）、日语 SenseVoice、中文 Qwen3-ASR（约 4.4GB）
python tools\model-convert\.venv\Scripts\python.exe tools\model-convert\convert_models.py --whisper-size base --only whisper-en
python tools\model-convert\.venv\Scripts\python.exe tools\model-convert\convert_models.py --only sensevoice
python tools\model-convert\.venv\Scripts\python.exe tools\model-convert\convert_models.py --only sensevoice-static   # 静态 ONNX（固定输入、无动态形状脆弱）
python tools\model-convert\.venv\Scripts\python.exe tools\model-convert\convert_models.py --only qwen3-asr
# 翻译模型（按源语言路由 P6-10：ja → ja→zh，其余 → en→zh）
python tools\model-convert\.venv\Scripts\python.exe tools\model-convert\convert_models.py --only marian --only compress-marian
# 日语翻译（官方 Helsinki-NLP/opus-mt-ja-zh 已 gated，自动回退用公开镜像 siasun/opus-mt-ja-zh 或 shun89/opus-mt-ja-zh）
$env:HF_ENDPOINT = "https://huggingface.co"
python tools\model-convert\.venv\Scripts\python.exe tools\model-convert\convert_models.py --hf-endpoint https://huggingface.co --only marian --marian-pair ja-zh --marian-repo shun89/opus-mt-ja-zh
# 产出 tools/model-convert/out/{whisper-base-int8, opus-mt-en-zh-int8, opus-mt-ja-zh-int8}（含 tokenizer/mel 边车）
# 可选：打包离线 zip 供“自动下载”使用（应用找不到模型时优先从 dist 解压）
Compress-Archive -Path tools\model-convert\out\whisper-base-int8\* -DestinationPath tools\model-convert\dist\whisper-base-int8.zip
Compress-Archive -Path tools\model-convert\out\opus-mt-en-zh-int8\* -DestinationPath tools\model-convert\dist\opus-mt-en-zh-int8.zip
Compress-Archive -Path tools\model-convert\out\opus-mt-ja-zh-int8\* -DestinationPath tools\model-convert\dist\opus-mt-ja-zh-int8.zip
```

> 网络说明：默认走 `hf-mirror.com`（脚本内置）；镜像不稳时加 `--hf-endpoint https://huggingface.co` 直连官方。
>
> 若已有旧的**无 past 解码器**模型目录（识别慢 5 倍），可原地升级为 KV-cache 版本（权重相同）：
> `python tools\model-convert\convert_models.py --whisper-size base --only whisper-past`。

## 识别性能（本机实测，Core Ultra 7 255HX / Intel AI Boost NPU）

单段转写耗时拆解（13.6s 测试音频，36 token；`WavDumpTool --asr`）：

| 模型 | mel 谱 | 编码器(NPU) | 解码器(CPU) | 单段合计 | 优化前 |
|---|---|---|---|---|---|
| whisper-tiny | 2-10 ms | 42-76 ms | 18-33 ms | **~75-90 ms** | ~0.3 s |
| whisper-base | 2-5 ms | 78-91 ms | 27-51 ms | **~130 ms** | ~0.5 s |
| whisper-small | 4 ms | 188-196 ms | 83-145 ms | **~310-400 ms** | 1.4-3.1 s |

关键点：

- **mel 谱**：`System.Numerics.Vector` SIMD + 按帧并行（原标量 double DFT 约 300-400 ms/次）；
- **解码器**：`-with-past` 导出的状态化 KV-cache 图，每步只喂 1 个 token（原每步重跑整个前缀，55 ms/步 → 10.8 ms/步）；
- **编码器**：NPU 185 ms（small）最快，CPU 345 ms、GPU.0 270 ms、GPU.1 1004 ms；解码器反过来 CPU 最快
  （NPU 因 state 长度动态不可编译）。故固定"**NPU 编码器 + CPU 解码器**"。
- **NPU 编译缓存**：`%LOCALAPPDATA%\RealtimeSubtitle\cache\openvino`，small 首次编译 14.3 s → 之后 1.2 s
  （首次运行仍需等一次编译）；编码器窗口不可缩短（whisper 位置编码固定 1500 帧）。

### 语言专精模型（P6-4/5/6 + 翻译路由 P6-10，实测）

| 语言 | ASR 后端 | 单段延迟 | 文本实测（SAPI 合成测试音） | 翻译路由 |
|---|---|---|---|---|
| 自动 | 多语言 Whisper | 0.08-0.4 s | en/zh/ja 均可 | en→zh |
| 英语 | whisper.en | 0.1-0.4 s | "test of the real-time subtitle system." 等全对 | en→zh |
| 日语 | SenseVoice（单程 CTC） | **NPU 0.06-0.08 s/段**（纯 FP32 静态，P6-9） | "こんにちは…字幕システムのテスト…" 全对 | **ja→zh（opus-mt-ja-zh，~70-120 ms/句）** |
| 中文 | Qwen3-ASR（1.7B，仅终稿） | 0.5-2 s | "你好。这是实时字幕系统的测试。" 全对（带标点） | en→zh（zh→zh 直通需求待评估） |

探测命令：`WavDumpTool --sensevoice <wav>`、`WavDumpTool --qwen3 <wav> --lang zh`、
`WavDumpTool --asr <wav> --model tools\model-convert\out\whisper-en-small-int8`。

## 模型自动补齐（无需手工拷贝）

应用按以下顺序找模型（见 `RealtimeSubtitle.Core/Models/`）：

1. 安装目录 `%LOCALAPPDATA%\RealtimeSubtitle\models\<id>`（下载/解压的落点）；
2. 仓库检出 `tools/model-convert/out/<id>`（沿启动目录向上查找）；
3. 本地离线包 `tools/model-convert/dist/<id>.zip`（自动解压到安装目录）；
4. 网络源 `Models.BaseUrl`（`<BaseUrl>/<id>.zip`，在 `%LOCALAPPDATA%\RealtimeSubtitle\config.json` 配置，
   例如公司内网镜像；huggingface.co 在部分网络不可达，故未内置默认公网源）。

GUI 内“下载缺失模型”按钮 / 点击“开始”都会自动触发补齐并显示进度；
`WavDumpTool --download-models [--dir <installRoot>]` 可在命令行完成同样动作。

## 运行

```powershell
# 诊断/验收工具
WavDumpTool.exe --asr testdata\speech_zh.wav --lang auto   # Whisper 转写（en/zh/ja）
WavDumpTool.exe --translate "Where are you going?"          # Marian 翻译
WavDumpTool.exe --bench                                     # 50 句基准（CPU 中位 ~24ms）

# 覆盖层（非打包变体联调）
dotnet build -c Debug -p:WindowsPackageType=None
RealtimeSubtitle.App.exe --demo RealtimeSubtitle.App\demo.txt --probe demo-report.txt --seconds 30
# 或直接启动控制面板（GUI：选模式/语音引擎/翻译设备 → 点"开始"；缺模型自动下载）
RealtimeSubtitle.App.exe
```

## 翻译/识别诊断

```powershell
WavDumpTool.exe --translate "Where are you going?" --device NPU   # NPU(enc)+CPU(dec) 混合
WavDumpTool.exe --npu-probe <modelDir>                            # 单独验证 NPU 静态编译可行性
WavDumpTool.exe --bench --device NPU                              # 同进程 50 句基准（NPU vs CPU 中位）
WavDumpTool.exe --whisper-npu-probe <modelDir>                    # whisper 编码器 NPU 静态编译可行性

# 识别性能拆解（mel/编码器/解码器）与设备对比（需 venv + python）
python tools\model-convert\bench_whisper.py tools\model-convert\out\whisper-small-int8 testdata\test_speech.wav CPU
python tools\model-convert\bench_stateful.py tools\model-convert\out\whisper-small-int8 testdata\test_speech.wav CPU,NPU,GPU.0
python tools\model-convert\probe_encoder.py tools\model-convert\out\whisper-base-int8 CPU   # 验证编码器窗口不可缩短
```

## 目录速览

- `RealtimeSubtitle.Core` — 平台无关：音频（环/重采样/VAD）、语音抽象（`ISpeechRecognizer`）、翻译抽象（`ITranslator`/队列/设备选择）、字幕管理、配置、mel 谱与 BPE 解码
- `RealtimeSubtitle.Windows` — WASAPI 互操作、`OpenVinoTranslator`、`OpenVinoWhisperClassicRecognizer`、窗口互操作、Windows AI 封装
- `RealtimeSubtitle.App` — WinUI 3：覆盖层、控制面板、`AppServices` 组合根、`--demo`/`--overlay-probe`/`--ai-probe`
- `RealtimeSubtitle.Tools` — `WavDumpTool`（采集/合成/ASR/翻译/基准）
- `tools/model-convert` — Python 模型转换与边车导出
- `docs/` — decisions.md（版本钉死/环境配方/决策）、acceptance-checklist.md（分阶段验收）

## 已知限制

- 语音模型为 whisper-tiny（体积/延迟优先，准确率有限）；可用 whisper-base 换装（重新导出即可）。
- **听歌识别**：音乐/带伴奏歌声会触发 whisper 输出 "[Music]" 等音频事件注解。已做多级防护
  （P6-11）：mono 首 token 修复 + 行首注解剥离（保留其后歌词）+ junk 突发 partial 抑制 +
  分段 8s + Debug 级日志，日志/字幕不再被刷屏；但 whisper.en base/small 对带伴奏人声的
  歌词级识别率本身有限（模型层限制，非管线问题）。
- **覆盖层为 WPF 逐像素透明**（`RealtimeSubtitle.Overlay.Wpf`，独立 STA 线程宿主于 App 进程内）。WinUI 3 窗口无逐像素 alpha，
  LWA_COLORKEY / DWMSBT_TRANSIENTWINDOW 两种方案在本机实测仍黑底（见 decisions.md P5-3/P5-7），故弃用。
  本机 BitBlt/CopyFromScreen 捕获不到 layered/DirectComposition 窗口，最终透明效果以真实屏幕目视为准。
- **翻译 NPU 为混合模式**：encoder 静态 [1,64] 编译跑 NPU（启动时 ~2.3s 一次），decoder 动态不可 NPU → CPU；
  实测混合 34ms/句 vs 纯 CPU 29ms/句（NPU 未更快，但真实参与编码计算）。decoder 全 NPU 需静态化重导出（后续）。
- 默认 ASR 为 Whisper 回环（识别扬声器/系统音频，无需麦克风权限）；麦克风识别（Windows 语音识别）为 GUI 可选引擎。
  回环会捕获**所有**系统声音（包括自己播放的内容）；源语言为 auto 自动检测，可在 config `SourceLanguage` 指定（如 en-US）。
- 默认渲染端点空闲时不产生 loopback 数据（游戏/视频播放时正常）。
- DRM 音频不进 loopback；系统提示音会混入（VAD 缓解）。