# 箭头扩展

通过 Plugin API 1.1 把空心箭头和折线箭头加入截图浮层现有的箭头设置

## 构建与安装

可直接从 [Demo 发布页](https://github.com/zzbuaoye-love/ZSnaper/releases/tag/arrow-tools-demo-v1.0.0) 下载 `ArrowTools.Plugin.zsp` 和配套的 `ZSnaper-ArrowToolsHost.zip`，无需自行编译。宿主压缩包需要 .NET 8 Desktop Runtime，解压后运行其中的 `ZSnaper.exe`；启动前先退出旧版 ZSnaper，避免单实例机制唤起旧程序

自行构建插件：

```powershell
./samples/ArrowTools.Plugin/build-package.ps1
```

生成 `dist/ArrowTools.Plugin.zsp`，包内只有 `manifest.json` 与 `ArrowTools.Plugin.dll`

使用本仓库包含 API 1.1 的新宿主构建，在插件管理中安装并启用这个包，再开始截图。旧的 API 1.0 宿主会拒绝安装

框选截图区域后，点击工具栏原有的箭头按钮，在下方“箭头”样式一行选择新增的空心箭头或折线箭头，悬停可查看名称。拖动绘制，点击箭头可拖动整体或两端控制点。选中已有箭头后切换样式会直接更新该箭头，支持切回原生样式以及撤销。颜色、粗细、删除、撤销和重做沿用宿主操作，复制、保存和贴图都会包含插件箭头

停用插件后，后续截图不再显示这两个样式。进行中的截图使用开始截图时的样式快照，直到该次截图结束

插件只引用 `ZSnaper.Plugin.Abstractions`，不引用宿主程序集，也不通过反射修改浮层。几何算法位于 `ArrowToolsPlugin.cs`；宿主负责渲染、命中检测和标注历史

## 验证

```powershell
dotnet run --project installer/tests/Stability/ZSnaper.Stability.csproj -c Release -- --isolated --plugin-arrow-smoke
```

验证安装、启用、原生箭头设置中的样式选项、绘制、折线命中、端点编辑、颜色与粗细、原生与插件样式切换、删除、撤销重做、导出像素、停用及卸载，同时检查 API 1.0 兼容性拒绝
