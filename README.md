# 快捷搜索悬浮窗

- 执行者：Codex

这个版本使用本地 Edge 扩展创建真正的后台标签页，不再先显示浏览器窗口再最小化。快捷键唤出的界面只有一个 Liquid Glass 搜索框；输入为空时以当前搜索引擎名称作为占位提示，在输入区滚动鼠标滚轮即可切换引擎。界面自动跟随 Windows 明暗模式。窗口先取样其背后的真实桌面内容并应用可调 Gaussian Blur，再叠加逐像素染色、连续环境折射和内侧微光；设置菜单、通用设置页与搜索引擎管理页沿用同一玻璃、圆角、描边与交互体系。两个设置页独立居中显示，并在可能与搜索框相交时自动避让。圆角由 WPF 逐像素透明边缘绘制，不使用会产生锯齿的原生窗口区域硬裁剪，也不绘制矩形背板。搜索时仅边框显示高对比橙色呼吸灯，玻璃内部保持原色；网页加载完成后边框切换为绿色常亮，不显示额外提示文字。点击搜索框时，程序会把前台权限交给 Edge 并主动弹出对应结果页。

## 首次安装

1. 在 Edge 地址栏打开 `edge://extensions/`。
2. 打开“开发人员模式”。
3. 点击“加载解压缩的扩展”，选择本目录下的 `edge-extension` 文件夹。
4. 双击 `QuickSearchFloat.exe`。搜索框内显示当前搜索引擎名称后即可使用。

扩展只声明 `alarms` 权限，用于在本机程序稍后启动时重新连接。程序与扩展只通过 `127.0.0.1:17891` 通信。

## 使用

- `Ctrl+Space`：显示或隐藏搜索框（可在“通用设置”中重新录制）
- `Enter`：在 Edge 后台标签页开始搜索
- `Esc`：隐藏搜索框
- 切换到其他窗口：搜索框自动隐藏
- 搜索框边缘变绿后：点击搜索框或按 `Enter` 切换到已加载的结果页
- 左侧放大镜：按住拖动悬浮窗
- 输入框的搜索引擎名称提示：鼠标位于输入区时向上/向下滚动可切换引擎；不再提供下拉菜单
- 齿轮按钮：“通用设置”可调整透明度、高斯模糊、深浅背景色、唤醒快捷键、开机自启动和动态模糊；动态模糊支持 10/30/60 FPS，且只在搜索框可见时刷新
- “搜索引擎”可添加、删除、排序、修改并指定默认引擎
- 托盘图标：打开搜索或退出程序

`settings.ini` 中每个搜索引擎占一行：

```ini
# opacity 允许 0-100；0% 仍保留环境折射与内侧微光
opacity=70
# blurRadius 允许 0-40；0 为关闭背景高斯模糊
blurRadius=18
darkColor=#1C2027
lightColor=#FFFFFF
hotkey=Ctrl+Space
# 动态模糊关闭时沿用唤出时的静态背景取样
dynamicBlur=false
dynamicBlurFps=10
名称=https://example.com/search?q={query}
```

“开机自启动”使用当前用户的 Windows 启动项，不需要管理员权限。应用图标源文件为 `QuickSearchFloat-icon.png`，Windows 多尺寸图标为 `QuickSearchFloat.ico`。
动态模糊开启且搜索框可见时，程序会将自身从系统屏幕捕获中排除，避免背景反复采集搜索框而逐帧变亮；隐藏搜索框后立即恢复。

## 自检与编译

程序是单文件 .NET Framework 4.8 WPF 应用，不需要第三方运行库。退出正在运行的程序后执行自检：

```powershell
Start-Process .\QuickSearchFloat.exe -ArgumentList '--self-test' -Wait
Get-Content .\self-test.txt
```

已安装扩展后可执行真实后台加载测试（测试标签页完成加载后会自动关闭）：

```powershell
Start-Process .\QuickSearchFloat.exe -ArgumentList '--live-test' -Wait
Get-Content .\live-test.txt
```

重新编译：

```powershell
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework='C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:QuickSearchFloat.ico /out:QuickSearchFloat.exe Program.cs /reference:"$framework\WindowsBase.dll" /reference:"$framework\PresentationCore.dll" /reference:"$framework\PresentationFramework.dll" /reference:"$framework\System.Xaml.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll
```
