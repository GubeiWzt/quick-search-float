# 快捷搜索悬浮窗

- 执行者：Codex

这个版本使用本地 Edge 扩展创建真正的后台标签页，不再先显示浏览器窗口再最小化。快捷键唤出的界面只有一个 Liquid Glass 搜索框，并自动跟随 Windows 明暗模式。窗口以逐像素染色、冷暖环境折射和内侧微光组成三层玻璃材质；圆角由 WPF 逐像素透明边缘绘制，不使用会产生锯齿的原生窗口区域硬裁剪，也不绘制矩形背板。网页触发 `complete` 加载事件后，加载提示会直接显示在搜索框内部；点击提示时，程序会把前台权限交给 Edge 并主动弹出对应结果页。

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
- 点击“✓ 页面已加载，点击打开”：切换到已加载的结果页
- 左侧放大镜：按住拖动悬浮窗
- 搜索引擎按钮：点击打开列表；鼠标悬停时向上/向下滚动可切换引擎
- 齿轮按钮：设置 0%–100% 不透明度、深浅主题背景色，或编辑并重新加载 `settings.ini`
- 托盘图标：打开搜索或退出程序

`settings.ini` 中每个搜索引擎占一行：

```ini
# opacity 允许 0-100；0% 仍保留环境折射与内侧微光
opacity=70
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
