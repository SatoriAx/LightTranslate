# LightTranslate

Windows 原生轻量 AI 翻译工具，当前版本为 0.6.0。

## v0.6.0 就地截图翻译浮层

- 截图后不再弹出主窗口，译文直接在选区附近的小浮层中流式显示
- `Ctrl + Alt + X` 框选翻译、`Ctrl + Alt + R` 重复上次选区、`Ctrl + Alt + F` 固定选区连续翻译，全部走浮层
- 右键 / Esc / × 关闭浮层，支持复制译文
- 普通翻译推理档位从 HIGH 调整为 MEDIUM，首字延迟明显下降（看懂/精校仍为 MAX）

## v0.5.7 原生 Responses API

- DeepSeek 官方 `deepseek-v4-flash` 在“自动”模式下默认使用原生 Responses API
- Responses 请求使用 `instructions`、`input` 与 `reasoning.effort`
- 流式解析支持 `response.output_text.delta`、`response.completed`、`response.incomplete` 与 `response.failed`
- Responses 流不依赖 `data: [DONE]`，完成事件前异常断流会明确报错
- 其他 OpenAI-compatible 服务继续使用 Chat Completions
- 设置页可选择“自动（推荐）”“Responses API”或“Chat Completions”
- 普通翻译保持 HIGH，看懂与精校保持 MAX

## 推理档位策略

Chat Completions 使用 `thinking.type=enabled` 与 `reasoning_effort`；Responses API 使用原生 `reasoning.effort`。按用途分配推理强度：

| 功能 | reasoning_effort |
| --- | --- |
| 手动翻译、剪贴板、截图、重复选区、固定选区连续翻译 | `high` |
| 看懂、精校 | `max` |

## v0.5.6 GitHub 发布版

- 自定义无边框标题栏左上角改用正式折页交汇图标
- 准备公开仓库演示素材、搜索关键词、README 与 Release

## v0.5.5 正式图标

- 采用折页交汇形态的石墨黑、象牙白与香槟金图标
- ICO 包含 16、20、24、32、40、48、64、128 与 256 像素帧
- EXE、主窗口、设置、历史、术语窗口与托盘统一使用正式图标
- 固定选区连续翻译开启时，在正式托盘图标右下角叠加鼠尾草绿状态点
- 新版本文件名可绕开 Windows 对旧 EXE 图标的缓存

## v0.5.4 速度调整

- 普通翻译链路由 MAX 调整为 HIGH，降低短文本和截图翻译等待时间
- 看懂与精校继续使用 MAX，保留疑难内容的深度处理能力
- 主窗口、处理状态、设置页和托盘明确显示当前档位
- 自动化测试分别捕获普通翻译 HIGH 与看懂 MAX 的真实请求载荷

## v0.5.3 单文件版

日常使用只需要一个 `LightTranslate.exe`：

- 托管程序集、SkiaSharp 与 ONNX Runtime 原生依赖收进单文件
- 四个 PP-OCRv5 模型作为嵌入资源随 EXE 分发
- 第一次使用 OCR 时释放到 `%LOCALAPPDATA%\LightTranslate\ocr-cache\v5`
- 每次初始化都校验模型 SHA-256，缓存缺失或损坏时自动恢复
- 仍为框架依赖发布，需要 Windows 已安装 .NET 10 Desktop Runtime
- 设置、历史、术语与 DPAPI 加密密钥继续保存在用户数据目录

源码归档用于维护、审计和重新构建；只使用软件时无需下载源码包。

## v0.5.2 修复

Windows 事件日志确认 v0.5.1 的闪退根因为：

```text
System.ObjectDisposedException: The CancellationTokenSource has been disposed.
at LightTranslate.MainWindow.RunActionAsync(...)
at LightTranslate.MainWindow.Translate_Click(...)
```

旧实现会在请求结束时释放 `CancellationTokenSource`，但字段仍指向它；下一次翻译先调用旧对象的 `Cancel()`，异常发生在内部 try/catch 之外，并从 WPF `async void` 点击事件逃出，最终终止进程。

v0.5.2 使用 `TranslationCancellationManager` 管理请求：

- 新请求只取消旧请求，不提前释放仍在收尾的对象
- 每个请求仅在自己的 finally 中完成与释放
- 旧请求完成时不会清除后来创建的新请求
- 对已释放对象的取消有安全保护
- 窗口关闭统一取消并释放当前请求

同时增加：

- WPF `DispatcherUnhandledException` 全局保护
- 后台任务未观察异常记录
- 进程级异常记录
- 本地日志 `%APPDATA%\LightTranslate\lighttranslate.log`
- 日志超过 512KB 自动轮换到 `.old`
- 当结果与原文完全相同时提示“可能是名称、型号或无需翻译”

## v0.5.1 稳定性

- SSE 连续 60 秒无数据时自动中止，不限制正常推理总时长
- 设置、历史、术语、加密密钥采用临时文件写入和安全替换
- 覆盖前保留 `.bak`，损坏文件隔离为 `.corrupt-*` 后从备份恢复
- OCR 先识别原图，低置信度时才进行增强并择优
- 连续模式失败后允许同一内容重试
- 连续模式托盘绿点与实时推理档位状态
- 设置页两次确认清除密钥
- 历史清空二次确认
- 术语错误行提示

## 核心能力

- WPF + .NET 10 LTS
- DeepSeek V4 Flash 原生 Responses API + OpenAI-compatible Chat Completions 双协议
- 普通翻译 HIGH / 看懂与精校 MAX
- 中英日 PP-OCRv5 Mobile 本地 OCR
- 日语横排与竖排阅读顺序整理
- 流式输出与主动取消
- 重复选区与固定选区连续翻译
- 最近 20 条本地历史
- 个人术语表
- 看懂与精校
- DPAPI 加密 API Key
- 无账户、遥测、云同步和截图历史

## 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl + Alt + T` | 读取剪贴板文字 |
| `Ctrl + Alt + X` | 框选截图并翻译 |
| `Ctrl + Alt + R` | 重复上次选区 |
| `Ctrl + Alt + F` | 开启或停止固定选区连续翻译 |
| `Ctrl + Enter` | 翻译当前文字 |
| `Esc` | 取消当前请求、收起窗口或取消截图 |

## 构建

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release
```

发布产物为 Windows x64 框架依赖单 EXE。
