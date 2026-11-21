# 箭头扩展 Demo

箭头扩展通过 Plugin API 1.1，把空心箭头与折线箭头加入截图浮层已有的箭头设置。工具栏保留原有的一个箭头入口，插件样式位于三个原生样式后面

## 下载与安装

从 [Demo 发布页](https://github.com/zzbuaoye-love/ZSnaper/releases/tag/arrow-tools-demo-v1.0.0) 下载：

- [ArrowTools.Plugin.zsp](https://github.com/zzbuaoye-love/ZSnaper/releases/download/arrow-tools-demo-v1.0.0/ArrowTools.Plugin.zsp)：可安装的插件包
- [ZSnaper-ArrowToolsHost.zip](https://github.com/zzbuaoye-love/ZSnaper/releases/download/arrow-tools-demo-v1.0.0/ZSnaper-ArrowToolsHost.zip)：包含 API 1.1 的配套宿主，需要 .NET 8 Desktop Runtime
- [SHA256SUMS.txt](https://github.com/zzbuaoye-love/ZSnaper/releases/download/arrow-tools-demo-v1.0.0/SHA256SUMS.txt)：文件校验值

1. 退出正在运行的旧版 ZSnaper，解压配套宿主并运行其中的 `ZSnaper.exe`，避免单实例机制唤起旧程序
2. 在“插件”页面点击“安装插件”，选择 `ArrowTools.Plugin.zsp`
3. 找到“箭头扩展”并启用
4. 开始截图并框选区域，点击原有箭头按钮
5. 在下方“箭头”一行选择空心箭头或折线箭头，然后拖动绘制；悬停样式按钮可查看名称

选中已有箭头后切换样式，会直接修改该箭头，支持切回原生样式以及撤销重做。整体拖动、端点编辑、颜色、粗细、删除、复制、保存和贴图沿用宿主操作

停用插件后，新开始的截图不再显示插件样式；进行中的截图使用开始截图时的样式快照，直到该次截图结束

ZSnaper `v1.0.0` 正式版已内置插件 API 1.1，从 Demo 发布页下载 `ArrowTools.Plugin.zsp` 即可安装。原有 `v0.0.6-beta` 发布包仅支持 API 1.0，需升级应用或使用 Demo 的配套宿主；配套宿主的应用版本仍显示为 `0.0.6-beta`

## 源码与构建

- [插件源码与清单](https://github.com/zzbuaoye-love/ZSnaper/tree/main/samples/ArrowTools.Plugin)
- [API 1.1 协议](https://github.com/zzbuaoye-love/ZSnaper/blob/main/src/Plugin.Abstractions/PluginOverlayContracts.cs)
- [Plugin API 文档](https://github.com/zzbuaoye-love/ZSnaper/wiki/Plugin-API)

```powershell
./samples/ArrowTools.Plugin/build-package.ps1
```

输出 `samples/ArrowTools.Plugin/dist/ArrowTools.Plugin.zsp`，内含 `manifest.json` 和 `ArrowTools.Plugin.dll`。插件只引用 `ZSnaper.Plugin.Abstractions`，通过 `IZSnaperOverlayHost.Overlay.RegisterArrow` 注册几何回调，由宿主负责渲染和编辑

## 验证

```powershell
dotnet run --project installer/tests/Stability/ZSnaper.Stability.csproj -c Release -- --isolated --plugin-arrow-smoke
```

覆盖安装与启用、箭头设置中的插件样式、绘制、折线命中、端点编辑、颜色与粗细、原生和插件样式切换、删除、撤销重做、窄屏布局、导出像素、停用、卸载和旧 API 兼容性拒绝
