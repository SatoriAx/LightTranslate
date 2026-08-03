LightTranslate v0.5.7 单文件版

日常使用：
1. 只需要 LightTranslate.exe，无需解压模型或 DLL。
2. 双击 LightTranslate.exe。
3. Ctrl + Alt + T：读取剪贴板文字。
4. Ctrl + Alt + X：截取当前鼠标所在屏幕的文字区域，OCR 后自动翻译。
5. Ctrl + Alt + R：重新识别并翻译上次选区。
6. Ctrl + Alt + F：开启或停止固定选区连续翻译。
7. 翻译中点击主按钮或按 Esc：取消当前请求。
8. Esc 或鼠标右键：取消截图。

v0.5.7 Responses API：
- DeepSeek 官方 deepseek-v4-flash 在“自动”模式下使用原生 Responses API。
- 其他 OpenAI-compatible 服务继续使用 Chat Completions。
- 设置页可手动选择自动、Responses API 或 Chat Completions。
- 普通翻译为 HIGH，看懂与精校为 MAX。

v0.5.6 GitHub 发布版：
- 无边框主窗口左上角已改为正式折页交汇图标。

v0.5.5 正式图标：
- EXE、任务栏、主窗口、设置、历史、术语和托盘统一使用折页交汇图标。
- 连续翻译开启时，托盘图标右下角显示绿色状态点。
- ICO 内含 16 至 256 像素的九档图标。

v0.5.4 推理档位：
- 普通、剪贴板、截图、重复选区和连续翻译：reasoning_effort=high。
- 看懂与精校：reasoning_effort=max。
- 所有模式继续保持 thinking enabled。
- 主窗口、状态栏、设置页和托盘会显示当前 HIGH 或 MAX 档位。

单文件机制：
- ONNX Runtime、SkiaSharp 与托管程序集全部收进 LightTranslate.exe。
- 四个 PP-OCRv5 模型嵌入 EXE，第一次使用 OCR 时自动释放到 %LOCALAPPDATA%\LightTranslate\ocr-cache\v5。
- OCR 缓存会校验 SHA-256；缺失或损坏时由 EXE 自动恢复。
- 设置、历史、术语和 API Key 继续保存在用户数据目录，更新 EXE 不会丢失。
- 本版为 Windows x64 框架依赖版本，需要系统已安装 .NET 10 Desktop Runtime。

v0.5.2 闪退修复继续保留：
- 修复翻译完成后再次点击“开始翻译”可能因旧 CancellationTokenSource 已释放而闪退的问题。
- 请求取消状态由独立生命周期管理器维护。
- 增加 WPF 全局异常保护与本地日志。
- 日志路径：%APPDATA%\LightTranslate\lighttranslate.log。
- 译文与原文相同时明确提示“可能是名称、型号或无需翻译”。

本地 OCR：PP-OCRv5 Mobile，中英日统一识别，支持日语假名、汉字混排及竖排文字。
API Key 使用当前 Windows 用户 DPAPI 加密，不包含在 EXE 中。
