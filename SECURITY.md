# Security

## 数据边界

- 截图只在本机交给 PP-OCRv5 / ONNX Runtime 识别。
- 只有识别后的文字或用户手动输入的文字会发送到已配置的 AI API。
- API Key 使用 Windows CurrentUser DPAPI 加密保存在 `%APPDATA%\LightTranslate\api-key.dat`。
- 项目不包含账号系统、遥测、广告或云同步。

## 报告安全问题

请不要在公开 Issue 中粘贴 API Key、个人截图、日志全文或其他敏感信息。

发现安全问题时，请通过 GitHub 私信仓库所有者，或创建不包含敏感内容的 Issue 说明影响范围与复现方式。

## Security boundary

Screenshots are processed locally. Only OCR text or manually entered text is sent to the user-configured AI endpoint. Never include API keys or personal screenshots in public issues.
