# 快捷搜索悬浮窗

- 执行者：Codex

这个版本使用本地 Edge 扩展创建真正的后台标签页，不再先显示浏览器窗口再最小化。Google、必应和百度的结果会显示在搜索框下方：原生 AI 概览优先置顶并仅保留纯文字答案；普通网页结果默认收在独立的玻璃下拉条中，主动展开后以带 favicon 的双列瀑布流排列，滚动接近底部时继续加载下一页，明确标注为广告、Ad、Sponsored、推广或赞助的卡片会被过滤。结果就绪后，点击搜索框或按 `Enter` 可直接打开完整搜索页；点击普通结果则打开精确页面。每次唤醒都会清空上次的输入、结果和展开状态。搜索框和结果面板均取样真实桌面背景并应用可调 Gaussian Blur，再叠加相同的 Liquid Glass 染色、连续环境折射、圆角描边和明暗主题。

## 首次安装

1. 在 Edge 地址栏打开 `edge://extensions/`。
2. 打开“开发人员模式”。
3. 点击“加载解压缩的扩展”，选择本目录下的 `edge-extension` 文件夹。
4. 双击 `QuickSearchFloat.exe`。搜索框内显示当前搜索引擎名称后即可使用。

从旧版本升级时，请在 Edge 扩展页对该扩展点击一次“重新加载”。扩展使用 `alarms` 保持本机连接，使用 `scripting` 和 Google、必应、百度三个精确域名权限读取已加载搜索页中的可见结果；不向外部服务发送数据。程序与扩展只通过 `127.0.0.1:17891` 通信。

## 使用

- `Ctrl+Space`：显示或隐藏搜索框（可在“通用设置”中重新录制）
- `Enter`：在 Edge 后台标签页开始搜索并在搜索框下方展示结果
- `Esc`：隐藏搜索框
- 切换到其他窗口：搜索框自动隐藏
- AI 概览：仅展示去除重复标题和链接后的纯文字摘要，不提供点击跳转
- 网页结果下拉条：位于搜索框外；默认折叠，点击后展示或收起普通网页结果
- 普通结果卡片：显示网站 favicon；点击后复用后台标签页打开该结果并将 Edge 置前
- 结果瀑布流：展开后以双列不等高排列，接近底部时自动加载更多
- 搜索完成但未展开网页结果时：点击搜索框或按 `Enter` 打开完整搜索页
- 每次唤醒：清空上次输入、结果、滚动位置和下拉展开状态
- 自定义搜索引擎：解析不受支持时，搜索框边缘变绿后点击搜索框或按 `Enter` 打开完整搜索页
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

重新加载新版扩展后可执行真实结果提取测试；测试会验证首批结果和可用时的下一页，并在完成后关闭后台标签页：

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
