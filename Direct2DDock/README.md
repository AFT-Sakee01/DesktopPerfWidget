# Dock Direct2D Rewrite

## 现有 Dock 摘要

当前 `DesktopPerfWidget.cs` 里的 Dock 是一个 `DockForm`：

- 使用 WinForms + GDI+ + `UpdateLayeredWindow` 绘制透明圆角 Dock。
- 左侧包含返回桌面按钮和开始菜单/启动台按钮。
- 中间包含固定应用、正在运行应用、固定/运行分隔线、hover 放大、运行指示点、窗口预览。
- 右侧包含媒体控制器和 Codex 额度小组件。
- 运行应用通过枚举顶层窗口得到，固定应用从 `settings.ini` 的 `DockItemsText` 读取。
- 媒体通过 GSMTC 读取标题、播放状态、媒体封面。
- 启动台读取 Start Menu 快捷方式，并使用 WinForms/GDI+ 绘制。

主要性能问题来自旧渲染路径：

- 每帧新建整张 `Bitmap`。
- 每帧完整 GDI+ 重绘 Dock。
- 每帧 `GetHbitmap` 后提交 `UpdateLayeredWindow`。
- 伸缩动画如果改窗口尺寸，会引发额外重绘。

## Direct2D 重写边界

新增的 `Direct2DDock.cs` 是独立重写，不替换主程序：

- 独立入口：`Direct2DDockProgram`
- 独立窗口：`Direct2DDockForm`
- 独立构建脚本：`Build-Direct2DDock-Arm64.ps1`
- 输出：`Direct2DDock.exe`

它先实现 Dock 核心链路：

- 读取主程序同一份 `settings.ini` 的 Dock 尺寸、放大比例、底部边距、固定应用列表。
- Direct2D `WindowRenderTarget` 硬件绘制 Dock 背景、按钮、图标、运行指示点。
- 固定应用和运行应用列表。
- hover 放大。
- 运行应用增加/关闭时的宽度动画和图标上下淡入淡出。
- 左侧返回桌面按钮和开始菜单按钮。

暂未迁移的旧 Dock 功能：

- 媒体控制器。
- Codex 额度小组件。
- 自定义启动台面板。
- DWM 窗口缩略图预览。

这些适合在 Direct2D 核心稳定后继续迁移，避免一次性把旧 GDI+ 结构搬过去。

## 构建与运行

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Direct2DDock-Arm64.ps1
.\Direct2DDock.exe
```

停止独立 Direct2D Dock：

```powershell
.\Direct2DDock.exe --stop
```

主程序不受影响，两个版本可以分开测试。
