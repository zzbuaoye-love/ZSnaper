
<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/icon-dark.svg">
    <img src="assets/logo/icon-light.svg" alt="ZSnaper Logo" width="15%" />
  </picture>
</p>

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo/text-dark.svg">
    <img src="assets/logo/text-light.svg" alt="ZSnaper" width="320" />
  </picture>
</p>
<p align="center">
  <strong>Zip、Snip、Faster</strong>
</p>

<p align="center">
  <a href="#构建项目">构建</a> ·
  <a href="#相关配置">配置</a> ·
  <a href="#许可证">许可</a>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3" /></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white" alt="8.0" />
  <img src="https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white" alt="C# 12" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2B-0078D6?logo=windows&logoColor=white" alt="Windows 10+" />
  <a href="隐私相关"><img src="https://img.shields.io/badge/OCR-100%25%20Support-success?logo=shield" alt="Offline/Online" /></a>
  <a href="https://github.com/buaoyezz/ZSnaper/releases"><img src="https://img.shields.io/github/v/release/buaoyezz/ZSnaper?include_prereleases&color=orange&label=Version" alt="Latest Release" /></a>
</p>

<p align="center">
  <strong>ZSnaper 是一款专为 Windows 打造的轻量高效截图工具</strong>
</p>

软件深度集成 `Windows.Media.Ocr`，带来极速的本地离线文字提取体验；更将**智能选区、滚动长截图、图像标注与文字识别**整合于一体化轻便功能栏中，随调随用

此外，ZSnaper 现已灵活支持接入**第三方 API / 模型 OCR**，在复杂排版与高难度识别场景下，提供更好的体验，<a href="#隐私相关">隐私政策</a>


<p align="center">
  <img src="assets/banner.png" alt="ZSnaper — Windows screenshot and offline OCR" width="100%" />
</p>

## 构建项目

### 所需环境

- Windows 10 1809（Build 17763）或更高版本
- .NET 8 SDK
- 对应语言的 Windows OCR 语言包

### 常规编译

```powershell
dotnet restore
dotnet build ZSnaper.csproj -c Release
```

### Windows x64 下的单文件发布

```powershell
dotnet publish ZSnaper.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ./publish
```

## 相关配置

用户配置默认保存在：

```bash
%APPDATA%\ZSnaper\config.json
```
您可以通过:
```cmd
explorer %APPDATA%\ZSnaper\
```
快速打开此目录

APP支持在应用内调整`主题`、`动画`、`强调色`、`快捷键`、`自动复制/保存`、`工具栏位置`、`标注样式`和 `OCR 段落清理`策略

选定网页、文档或列表的可滚动区域后，点击工具栏中的`长截图`进入专用捕获模式；可以在透明选区内缓慢手动滚动，也可以点击`自动滚动`，确认内容后再保存或完成。捕获过程中按 `Esc` 可立即停止。

## 隐私相关

本项目的`全部`截图、图像预处理与`离线`的 OCR 识别`均在本地执行`<br>
> 第三方服务免责：若选用第三方 API 或云端模型处理图片，相关数据流转遵循该服务商规范，ZSnaper 无法控制亦不承担其隐私责任
ZSnaper 不需要也不会将图片上传到远程服务器，软件内也不包含数据上报流程<br>
本项目遵循 `GPL-3.0` 协议开源，所有版本更新与相关说明均以中文版本为准并优先维护!

## 许可证

ZSnaper 基于 [GNU General Public License v3.0](LICENSE) 开源协议发布
你可以在该条款下自由使用、研究、修改和重新分发本项目

Copyright © 2026 [ZZBuAoYe](https://github.com/buaoyezz). All rights reserved.

## 友链
[LINUX DO](https://linux.do)
