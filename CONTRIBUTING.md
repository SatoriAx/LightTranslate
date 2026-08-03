# Contributing

欢迎提交 Bug 报告、OCR 样本和改进建议。

## 提交 Issue 前

1. 确认使用的是最新 Release。
2. 说明 Windows 版本、显示器缩放比例和是否多显示器。
3. OCR 问题请说明语言、横排/竖排及文字大小；避免上传含隐私的原图。
4. API 问题请提供 HTTP 状态码与已脱敏错误，不要粘贴 API Key。

## 本地构建

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release
```

项目目标框架为 .NET 10，发布目标为 Windows x64 单文件。

## Pull Request

- 保持界面轻量、克制，不引入账号、遥测或功能堆叠。
- 涉及 OCR、截图坐标或请求生命周期的改动应附回归说明。
- 不要提交本地配置、密钥、日志、历史记录或构建产物。
