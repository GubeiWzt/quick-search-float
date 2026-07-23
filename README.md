# 快捷搜索悬浮窗

- 执行者：Codex

这个版本使用本地 Edge 扩展创建真正的后台标签页，不再先显示浏览器窗口再最小化。快捷键唤出的界面只有一个 Liquid Glass 搜索框；输入为空时以当前搜索引擎名称作为占位提示，在输入区滚动鼠标滚轮即可切换引擎。界面自动跟随 Windows 明暗模式。窗口先取样其背后的真实桌面内容并应用可调 Gaussian Blur，再叠加逐像素染色、连续环境折射和内侧微光；设置子菜单与外观设置页沿用同一玻璃、圆角、描边与交互体系。圆角由 WPF 逐像素透明边缘绘制，不使用会产生锯齿的原生窗口区域硬裁剪，也不绘制矩形背板。搜索时仅边框显示高对比橙色呼吸灯，玻璃内部保持原色；网页加载完成后边框切换为绿色常亮，不显示额外提示文字。点击搜索框时，程序会把前台权限交给 Edge 并主动弹出对应结果页。

## 首次安装

1. 在 Edge 地址栏打开 `edge://extensions/`。
2. 打开“开发人员模式”。
3. 点击“加载解压缩的扩展”，选择本目录下的 `edge-extension` 文件夹。
4. 双击 `QuickSearchFloat.exe`。搜索框右侧显示搜索引擎后即可使用。

扩展只声明 `alarms` 权限，用于在本机程序稍后启动时重新连接。程序与扩展只通过 `127.0.0.1:17891` 通信。

## 使用

- `Ctrl+Alt+Space`：显示或隐藏搜索框
- `Enter`：在 Edge 后台标签页开始搜索
- `Esc`：隐藏搜索框
- 切换到其他窗口：搜索框自动隐藏
- 搜索框边缘变绿后：点击搜索框或按 `Enter` 切换到已加载的结果页
- 左侧放大镜：按住拖动悬浮窗
- 输入框的搜索引擎名称提示：鼠标位于输入区时向上/向下滚动可切换引擎；不再提供下拉菜单
- 齿轮按钮：在统一的 Liquid Glass 外观页设置 0%–100% 不透明度、0–40 高斯模糊和深浅主题背景色，或编辑并重新加载 `settings.ini`
- 托盘图标：打开搜索或退出程序

`settings.ini` 中每个搜索引擎占一行：

```ini
# opacity 允许 0-100；0% 仍保留环境折射与内侧微光
opacity=70
# blurRadius 允许 0-40；0 为关闭背景高斯模糊
blurRadius=18
darkColor=#1C2027
lightColor=#FFFFFF
名称=https://example.com/search?q={query}
```

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
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:QuickSearchFloat.exe Program.cs /reference:"$framework\WindowsBase.dll" /reference:"$framework\PresentationCore.dll" /reference:"$framework\PresentationFramework.dll" /reference:"$framework\System.Xaml.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll
```
