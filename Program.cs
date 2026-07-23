using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace QuickSearchFloat
{
    internal static class Program
    {
        private const string MutexName = "Local\\QuickSearchFloat.8D8C4568";

        [STAThread]
        public static int Main(string[] args)
        {
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            SearchSettings settings = SearchSettings.Load(Path.Combine(appDirectory, "settings.ini"));

            if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                return RunSelfTest(settings, appDirectory);
            if (args.Length > 0 && string.Equals(args[0], "--live-test", StringComparison.OrdinalIgnoreCase))
                return RunLiveTest(settings, appDirectory);

            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show("快捷搜索已经在运行，请按 " + settings.Hotkey + " 唤出。", "快捷搜索",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                bool appearancePreview = args.Any(arg => string.Equals(arg, "--appearance-preview",
                    StringComparison.OrdinalIgnoreCase));
                bool preview = appearancePreview || args.Any(arg => string.Equals(arg, "--ui-preview",
                    StringComparison.OrdinalIgnoreCase));
                MainWindow window = new MainWindow(settings, preview, appearancePreview);
                app.Run(window);
            }
            return 0;
        }

        private static int RunSelfTest(SearchSettings settings, string appDirectory)
        {
            string reportPath = Path.Combine(appDirectory, "self-test.txt");
            try
            {
                if (settings.Engines.Count < 1)
                    throw new InvalidOperationException("没有有效的搜索引擎配置。");
                if (settings.Engines.Any(e => !e.Template.Contains("{query}")))
                    throw new InvalidOperationException("搜索地址缺少 {query}。");
                if (settings.BackgroundOpacity < 0 || settings.BackgroundOpacity > 100)
                    throw new InvalidOperationException("背景不透明度超出范围。");
                if (SearchSettings.NormalizeOpacity(0) != 0 ||
                    SearchSettings.NormalizeOpacity(101) != 100)
                    throw new InvalidOperationException("背景不透明度边界检查失败。");
                if (settings.BlurRadius < 0 || settings.BlurRadius > 40)
                    throw new InvalidOperationException("高斯模糊强度超出范围。");
                if (SearchSettings.NormalizeBlur(-1) != 0 ||
                    SearchSettings.NormalizeBlur(41) != 40)
                    throw new InvalidOperationException("高斯模糊强度边界检查失败。");
                if (!settings.DarkBackgroundColor.StartsWith("#") ||
                    !settings.LightBackgroundColor.StartsWith("#"))
                    throw new InvalidOperationException("背景颜色配置无效。");
                uint testModifiers;
                uint testVirtualKey;
                string testHotkey;
                if (!SearchSettings.TryParseHotkey("Control + Space", out testModifiers,
                    out testVirtualKey, out testHotkey) || testHotkey != "Ctrl+Space")
                    throw new InvalidOperationException("快捷键解析检查失败。");
                if (SearchSettings.TryParseHotkey("Space", out testModifiers,
                    out testVirtualKey, out testHotkey))
                    throw new InvalidOperationException("快捷键修饰键检查失败。");
                if (SearchSettings.NormalizeDynamicBlurFps(30) != 30 ||
                    SearchSettings.NormalizeDynamicBlurFps(59) != 10)
                    throw new InvalidOperationException("动态模糊帧率档位检查失败。");
                if (StartupManager.BuildCommand(@"C:\Program Files\Quick Search\app.exe") !=
                    "\"C:\\Program Files\\Quick Search\\app.exe\"")
                    throw new InvalidOperationException("开机启动命令检查失败。");
                if (!SearchSettings.IsValidEngineTemplate(
                        "https://example.test/search?q={query}") ||
                    SearchSettings.IsValidEngineTemplate("javascript:{query}"))
                    throw new InvalidOperationException("搜索引擎地址检查失败。");
                string settingsTestPath = Path.Combine(appDirectory,
                    ".quick-search-settings-test-" + Guid.NewGuid().ToString("N") + ".ini");
                try
                {
                    File.WriteAllText(settingsTestPath,
                        "selected=One\r\nhotkey=Ctrl+Space\r\n" +
                        "One=https://one.example/search?q={query}\r\n",
                        new UTF8Encoding(false));
                    SearchSettings editable = SearchSettings.Load(settingsTestPath);
                    editable.SaveGeneral(0, 40, "#112233", "#F0F0F0", "Ctrl+Shift+K",
                        true, 60);
                    bool rejectedReservedName = false;
                    try
                    {
                        editable.SaveEngines(new List<SearchEngine>
                        {
                            new SearchEngine("hotkey", "https://example.test/?q={query}")
                        }, "hotkey");
                    }
                    catch (InvalidOperationException)
                    {
                        rejectedReservedName = true;
                    }
                    if (!rejectedReservedName)
                        throw new InvalidOperationException("保留设置名称检查失败。");
                    editable.SaveEngines(new List<SearchEngine>
                    {
                        new SearchEngine("One", "https://one.example/search?q={query}"),
                        new SearchEngine("Two", "https://two.example/find/{query}")
                    }, "Two");
                    SearchSettings reloaded = SearchSettings.Load(settingsTestPath);
                    if (reloaded.Hotkey != "Ctrl+Shift+K" ||
                        reloaded.BackgroundOpacity != 0 || reloaded.BlurRadius != 40 ||
                        !reloaded.DynamicBlur || reloaded.DynamicBlurFps != 60 ||
                        reloaded.Engines.Count != 2 || reloaded.SelectedName != "Two")
                        throw new InvalidOperationException("设置保存与重新加载检查失败。");
                }
                finally
                {
                    if (File.Exists(settingsTestPath))
                        File.Delete(settingsTestPath);
                }

                string encoded = SearchHelper.BuildSearchUrl(
                    "空 格", "https://example.test/search?q={query}");
                if (encoded != "https://example.test/search?q=%E7%A9%BA%20%E6%A0%BC")
                    throw new InvalidOperationException("搜索词编码检查失败。");
                if (SearchHelper.MoveIndex(0, 3, 120) != 2 ||
                    SearchHelper.MoveIndex(2, 3, -120) != 0)
                    throw new InvalidOperationException("滚轮切换搜索引擎检查失败。");

                string browser = SearchHelper.GetDefaultBrowserExecutable();
                if (string.IsNullOrWhiteSpace(browser) || !File.Exists(browser))
                    throw new InvalidOperationException("未找到默认浏览器可执行文件。");

                string extensionDirectory = Path.Combine(appDirectory, "edge-extension");
                string manifestPath = Path.Combine(extensionDirectory, "manifest.json");
                string workerPath = Path.Combine(extensionDirectory, "service-worker.js");
                if (!File.Exists(manifestPath) || !File.Exists(workerPath))
                    throw new InvalidOperationException("Edge 扩展文件不完整。");
                if (File.ReadAllText(manifestPath, Encoding.UTF8).IndexOf("\"manifest_version\": 3",
                    StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException("Edge 扩展清单不是 Manifest V3。");

                RunBridgeProtocolSelfTest().GetAwaiter().GetResult();

                File.WriteAllText(reportPath,
                    "SelfTest=OK\r\nEngines=" + settings.Engines.Count +
                    "\r\nBrowser=" + browser +
                    "\r\nOpacity=" + settings.BackgroundOpacity +
                    "\r\nBlurRadius=" + settings.BlurRadius +
                    "\r\nHotkey=" + settings.Hotkey +
                    "\r\nDynamicBlur=" + settings.DynamicBlur +
                    "\r\nDynamicBlurFps=" + settings.DynamicBlurFps +
                    "\r\nSettingsPersistence=OK" +
                    "\r\nExtension=Ready\r\nBridgeProtocol=OK\r\n",
                    new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(reportPath, "SelfTest=FAILED\r\n" + ex + "\r\n",
                    new UTF8Encoding(false));
                return 1;
            }
        }

        private static int RunLiveTest(SearchSettings settings, string appDirectory)
        {
            string reportPath = Path.Combine(appDirectory, "live-test.txt");
            try
            {
                using (EdgeExtensionBridge bridge = new EdgeExtensionBridge(EdgeExtensionBridge.DefaultPort))
                {
                    bridge.Start();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(35);
                    while (!bridge.IsConnected && DateTime.UtcNow < deadline)
                        Thread.Sleep(100);
                    if (!bridge.IsConnected)
                        throw new InvalidOperationException("Edge 扩展未连接。");

                    SearchEngine engine = settings.Engines.First(e => e.Name == settings.SelectedName);
                    string url = SearchHelper.BuildSearchUrl("QuickSearchFloat 后台加载测试", engine.Template);
                    string requestId = bridge.SearchAsync(url, 30).GetAwaiter().GetResult();
                    bridge.CancelAsync(requestId).GetAwaiter().GetResult();
                    File.WriteAllText(reportPath,
                        "LiveTest=OK\r\nEngine=" + engine.Name + "\r\nPageComplete=Yes\r\n",
                        new UTF8Encoding(false));
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(reportPath, "LiveTest=FAILED\r\n" + ex + "\r\n",
                    new UTF8Encoding(false));
                return 1;
            }
        }

        private static async Task RunBridgeProtocolSelfTest()
        {
            int port;
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }

            using (EdgeExtensionBridge bridge = new EdgeExtensionBridge(port))
            using (ClientWebSocket client = new ClientWebSocket())
            {
                bridge.Start();
                client.Options.SetRequestHeader("Origin", "chrome-extension://quick-search-self-test");
                await client.ConnectAsync(new Uri(bridge.WebSocketUrl), CancellationToken.None);

                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (!bridge.IsConnected && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                if (!bridge.IsConnected)
                    throw new InvalidOperationException("本机桥接连接测试失败。");

                Task<string> readyTask = bridge.SearchAsync("https://example.test/search?q=test", 2);
                string searchMessage = await ReceiveClientMessageAsync(client);
                string[] searchParts = searchMessage.Split(new[] { '\t' }, 3);
                if (searchParts.Length != 3 || searchParts[0] != "search")
                    throw new InvalidOperationException("搜索协议格式错误。");

                await SendClientMessageAsync(client, "ready\t" + searchParts[1]);
                string requestId = await readyTask;
                if (requestId != searchParts[1])
                    throw new InvalidOperationException("搜索协议请求编号不一致。");

                Task showTask = bridge.ShowAsync(requestId, 2);
                string showMessage = await ReceiveClientMessageAsync(client);
                if (showMessage != "show\t" + requestId)
                    throw new InvalidOperationException("跳转协议格式错误。");
                await SendClientMessageAsync(client, "shown\t" + requestId);
                await showTask;
            }
        }

        private static async Task<string> ReceiveClientMessageAsync(ClientWebSocket socket)
        {
            byte[] buffer = new byte[4096];
            using (MemoryStream message = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }

        private static Task SendClientMessageAsync(ClientWebSocket socket, string message)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            return socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true,
                CancellationToken.None);
        }
    }

    internal sealed class SearchEngine
    {
        public string Name { get; private set; }
        public string Template { get; private set; }

        public SearchEngine(string name, string template)
        {
            Name = name;
            Template = template;
        }
    }

    internal sealed class SearchSettings
    {
        public string FilePath { get; private set; }
        public string SelectedName { get; private set; }
        public List<SearchEngine> Engines { get; private set; }
        public int BackgroundOpacity { get; private set; }
        public int BlurRadius { get; private set; }
        public string DarkBackgroundColor { get; private set; }
        public string LightBackgroundColor { get; private set; }
        public string Hotkey { get; private set; }
        public bool DynamicBlur { get; private set; }
        public int DynamicBlurFps { get; private set; }

        private SearchSettings(string filePath)
        {
            FilePath = filePath;
            Engines = new List<SearchEngine>();
            BackgroundOpacity = 70;
            BlurRadius = 18;
            DarkBackgroundColor = "#1C2027";
            LightBackgroundColor = "#FFFFFF";
            Hotkey = "Ctrl+Space";
            DynamicBlur = false;
            DynamicBlurFps = 10;
        }

        public static SearchSettings Load(string filePath)
        {
            SearchSettings settings = new SearchSettings(filePath);
            if (File.Exists(filePath))
            {
                foreach (string raw in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;

                    int separator = line.IndexOf('=');
                    if (separator < 1)
                        continue;

                    string name = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    if (string.Equals(name, "selected", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.SelectedName = value;
                        continue;
                    }
                    if (string.Equals(name, "opacity", StringComparison.OrdinalIgnoreCase))
                    {
                        int opacity;
                        if (int.TryParse(value, out opacity))
                            settings.BackgroundOpacity = NormalizeOpacity(opacity);
                        continue;
                    }
                    if (string.Equals(name, "blurRadius", StringComparison.OrdinalIgnoreCase))
                    {
                        int blurRadius;
                        if (int.TryParse(value, out blurRadius))
                            settings.BlurRadius = NormalizeBlur(blurRadius);
                        continue;
                    }
                    if (string.Equals(name, "darkColor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsRgbColor(value))
                            settings.DarkBackgroundColor = value.ToUpperInvariant();
                        continue;
                    }
                    if (string.Equals(name, "lightColor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsRgbColor(value))
                            settings.LightBackgroundColor = value.ToUpperInvariant();
                        continue;
                    }
                    if (string.Equals(name, "hotkey", StringComparison.OrdinalIgnoreCase))
                    {
                        uint modifiers;
                        uint virtualKey;
                        string normalized;
                        if (TryParseHotkey(value, out modifiers, out virtualKey, out normalized))
                            settings.Hotkey = normalized;
                        continue;
                    }
                    if (string.Equals(name, "dynamicBlur", StringComparison.OrdinalIgnoreCase))
                    {
                        bool dynamicBlur;
                        if (bool.TryParse(value, out dynamicBlur))
                            settings.DynamicBlur = dynamicBlur;
                        continue;
                    }
                    if (string.Equals(name, "dynamicBlurFps", StringComparison.OrdinalIgnoreCase))
                    {
                        int fps;
                        if (int.TryParse(value, out fps))
                            settings.DynamicBlurFps = NormalizeDynamicBlurFps(fps);
                        continue;
                    }

                    if (IsValidEngineTemplate(value))
                        settings.Engines.Add(new SearchEngine(name, value));
                }
            }

            if (settings.Engines.Count == 0)
            {
                settings.Engines.Add(new SearchEngine("百度", "https://www.baidu.com/s?wd={query}"));
                settings.Engines.Add(new SearchEngine("必应", "https://www.bing.com/search?q={query}"));
                settings.Engines.Add(new SearchEngine("Google", "https://www.google.com/search?q={query}"));
            }
            if (!settings.Engines.Any(e => e.Name == settings.SelectedName))
                settings.SelectedName = settings.Engines[0].Name;
            return settings;
        }

        public void SaveSelection(string name)
        {
            SelectedName = name;
            List<string> lines = File.Exists(FilePath)
                ? File.ReadAllLines(FilePath, Encoding.UTF8).ToList()
                : new List<string>();
            int index = lines.FindIndex(line => line.TrimStart().StartsWith("selected=",
                StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                lines[index] = "selected=" + name;
            else
                lines.Insert(0, "selected=" + name);
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
        }

        public void SaveGeneral(int opacity, int blurRadius, string darkColor, string lightColor,
            string hotkey, bool dynamicBlur, int dynamicBlurFps)
        {
            uint modifiers;
            uint virtualKey;
            string normalizedHotkey;
            if (!TryParseHotkey(hotkey, out modifiers, out virtualKey, out normalizedHotkey))
                throw new InvalidOperationException("快捷键必须包含至少一个修饰键和一个按键。");

            BackgroundOpacity = NormalizeOpacity(opacity);
            BlurRadius = NormalizeBlur(blurRadius);
            DarkBackgroundColor = IsRgbColor(darkColor) ? darkColor.ToUpperInvariant() : "#1C2027";
            LightBackgroundColor = IsRgbColor(lightColor) ? lightColor.ToUpperInvariant() : "#FFFFFF";
            Hotkey = normalizedHotkey;
            DynamicBlur = dynamicBlur;
            DynamicBlurFps = NormalizeDynamicBlurFps(dynamicBlurFps);

            List<string> lines = File.Exists(FilePath)
                ? File.ReadAllLines(FilePath, Encoding.UTF8).ToList()
                : new List<string>();
            Upsert(lines, "opacity", BackgroundOpacity.ToString(CultureInfo.InvariantCulture));
            Upsert(lines, "blurRadius", BlurRadius.ToString(CultureInfo.InvariantCulture));
            Upsert(lines, "darkColor", DarkBackgroundColor);
            Upsert(lines, "lightColor", LightBackgroundColor);
            Upsert(lines, "hotkey", Hotkey);
            Upsert(lines, "dynamicBlur", DynamicBlur ? "true" : "false");
            Upsert(lines, "dynamicBlurFps",
                DynamicBlurFps.ToString(CultureInfo.InvariantCulture));
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
        }

        public void SaveEngines(List<SearchEngine> engines, string selectedName)
        {
            if (engines == null || engines.Count == 0)
                throw new InvalidOperationException("至少保留一个搜索引擎。");
            if (engines.Any(engine => string.IsNullOrWhiteSpace(engine.Name) ||
                engine.Name.Contains("=") || IsReservedSettingName(engine.Name) ||
                !IsValidEngineTemplate(engine.Template)))
                throw new InvalidOperationException("搜索引擎名称或地址无效。");
            if (engines.Select(engine => engine.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != engines.Count)
                throw new InvalidOperationException("搜索引擎名称不能重复。");
            if (!engines.Any(engine => engine.Name == selectedName))
                selectedName = engines[0].Name;

            List<string> lines = File.Exists(FilePath)
                ? File.ReadAllLines(FilePath, Encoding.UTF8).ToList()
                : new List<string>();
            lines.RemoveAll(IsEngineLine);
            Upsert(lines, "selected", selectedName);
            foreach (SearchEngine engine in engines)
                lines.Add(engine.Name + "=" + engine.Template);
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));

            Engines = engines;
            SelectedName = selectedName;
        }

        private static void Upsert(List<string> lines, string name, string value)
        {
            int index = lines.FindIndex(line => line.TrimStart().StartsWith(name + "=",
                StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                lines[index] = name + "=" + value;
            else
                lines.Insert(0, name + "=" + value);
        }

        public static int NormalizeOpacity(int opacity)
        {
            return Math.Max(0, Math.Min(100, opacity));
        }

        public static int NormalizeBlur(int blurRadius)
        {
            return Math.Max(0, Math.Min(40, blurRadius));
        }

        public static int NormalizeDynamicBlurFps(int fps)
        {
            return fps == 30 || fps == 60 ? fps : 10;
        }

        public static bool TryParseHotkey(string value, out uint modifiers, out uint virtualKey,
            out string normalized)
        {
            modifiers = 0;
            virtualKey = 0;
            normalized = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            Key key = Key.None;
            foreach (string rawToken in value.Split('+'))
            {
                string token = rawToken.Trim();
                switch (token.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= (uint)ModifierKeys.Control;
                        continue;
                    case "alt":
                        modifiers |= (uint)ModifierKeys.Alt;
                        continue;
                    case "shift":
                        modifiers |= (uint)ModifierKeys.Shift;
                        continue;
                    case "win":
                    case "windows":
                        modifiers |= (uint)ModifierKeys.Windows;
                        continue;
                    case "enter":
                        key = Key.Return;
                        continue;
                    case "esc":
                        key = Key.Escape;
                        continue;
                }

                Key parsedKey;
                if (token.Length == 1 && char.IsLetter(token[0]))
                    parsedKey = (Key)Enum.Parse(typeof(Key), token.ToUpperInvariant());
                else if (token.Length == 1 && char.IsDigit(token[0]))
                    parsedKey = (Key)((int)Key.D0 + (token[0] - '0'));
                else if (!Enum.TryParse(token, true, out parsedKey))
                    return false;
                if (key != Key.None)
                    return false;
                key = parsedKey;
            }

            if (modifiers == 0 || IsModifierKey(key))
                return false;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0)
                return false;
            normalized = FormatHotkey((ModifierKeys)modifiers, key);
            return true;
        }

        public static bool TryFormatHotkey(ModifierKeys modifiers, Key key, out string normalized)
        {
            normalized = null;
            modifiers &= ModifierKeys.Control | ModifierKeys.Alt |
                ModifierKeys.Shift | ModifierKeys.Windows;
            if (modifiers == ModifierKeys.None || IsModifierKey(key))
                return false;
            normalized = FormatHotkey(modifiers, key);
            return true;
        }

        public static bool IsValidEngineTemplate(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Contains("{query}"))
                return false;
            Uri uri;
            string sample = value.Replace("{query}", "test");
            return Uri.TryCreate(sample, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static bool IsEngineLine(string raw)
        {
            string line = raw.Trim();
            int separator = line.IndexOf('=');
            return separator > 0 && IsValidEngineTemplate(line.Substring(separator + 1).Trim());
        }

        private static bool IsReservedSettingName(string name)
        {
            return string.Equals(name, "selected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "opacity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "blurRadius", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "darkColor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "lightColor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "hotkey", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "dynamicBlur", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "dynamicBlurFps", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.None || key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftShift ||
                key == Key.RightShift || key == Key.LWin || key == Key.RWin;
        }

        private static string FormatHotkey(ModifierKeys modifiers, Key key)
        {
            List<string> parts = new List<string>();
            if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

            string keyName;
            if (key >= Key.D0 && key <= Key.D9)
                keyName = ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
            else if (key == Key.Return)
                keyName = "Enter";
            else if (key == Key.Escape)
                keyName = "Esc";
            else
                keyName = key.ToString();
            parts.Add(keyName);
            return string.Join("+", parts);
        }

        private static bool IsRgbColor(string value)
        {
            int parsed;
            return value != null && value.Length == 7 && value[0] == '#' &&
                int.TryParse(value.Substring(1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out parsed);
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath =
            "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string ValueName = "QuickSearchFloat";

        public static bool IsEnabled(string executablePath)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                string command = key == null ? null : key.GetValue(ValueName) as string;
                return string.Equals(command, BuildCommand(executablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SetEnabled(string executablePath, bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enabled)
                    key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
                else
                    key.DeleteValue(ValueName, false);
            }
        }

        public static string BuildCommand(string executablePath)
        {
            return "\"" + Path.GetFullPath(executablePath) + "\"";
        }
    }

    internal sealed class MainWindow : Window
    {
        private const int HotkeyId = 0x5153;

        private SearchSettings _settings;
        private readonly TextBox _queryBox;
        private readonly TextBlock _placeholder;
        private readonly Button _statusButton;
        private readonly Button _settingsButton;
        private readonly Border _shell;
        private readonly Image _backdropImage;
        private readonly BlurEffect _backdropBlur;
        private readonly Border _glassBaseLayer;
        private readonly Border _glassAmbientLayer;
        private readonly Border _glassRimLayer;
        private readonly System.Windows.Shapes.Path _searchGlyph;
        private readonly Forms.NotifyIcon _trayIcon;
        private readonly EdgeExtensionBridge _bridge;
        private readonly DispatcherTimer _dynamicBlurTimer;
        private readonly Drawing.Icon _appIcon;
        private readonly bool _preview;
        private readonly bool _appearancePreview;
        private HwndSource _source;
        private SearchEngine _selectedEngine;
        private string _bridgeStartError;
        private string _lastRequestId;
        private string _statusColor = "#FF475467";
        private Brush _idleRimBrush;
        private bool _isDark;
        private bool _busy;
        private bool _readyToOpen;
        private bool _exiting;
        private int _focusGuardCount;
        private uint _registeredHotkeyModifiers;
        private uint _registeredHotkeyKey;
        private bool _hotkeyRegistered;
        private bool _dynamicCaptureSupported = true;
        private bool _captureAffinityApplied;

        public MainWindow(SearchSettings settings, bool preview, bool appearancePreview)
        {
            _settings = settings;
            _preview = preview;
            _appearancePreview = appearancePreview;
            Title = "快捷搜索";
            Width = 760;
            Height = 80;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = preview;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
            _appIcon = Drawing.Icon.ExtractAssociatedIcon(
                Process.GetCurrentProcess().MainModule.FileName);
            if (_appIcon != null)
                Icon = Imaging.CreateBitmapSourceFromHIcon(_appIcon.Handle,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            _dynamicBlurTimer = new DispatcherTimer(DispatcherPriority.Render);
            _dynamicBlurTimer.Tick += delegate
            {
                if (_settings.DynamicBlur && _settings.BlurRadius > 0 &&
                    IsVisible && _dynamicCaptureSupported)
                    RefreshBackdrop();
                else
                    ConfigureDynamicBlurTimer();
            };

            _bridge = new EdgeExtensionBridge(EdgeExtensionBridge.DefaultPort);
            _bridge.ConnectionChanged += BridgeOnConnectionChanged;
            try { _bridge.Start(); }
            catch (Exception ex) { _bridgeStartError = ex.Message; }

            _shell = new Border
            {
                CornerRadius = new CornerRadius(34),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            _shell.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(ShellOnMouseLeftButtonUp), true);

            Grid material = new Grid();
            _backdropImage = new Image
            {
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            _backdropBlur = new BlurEffect
            {
                Radius = _settings.BlurRadius,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            };
            _backdropImage.Effect = _backdropBlur;
            Grid backdropClip = new Grid
            {
                Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), 34, 34),
                IsHitTestVisible = false
            };
            backdropClip.Children.Add(_backdropImage);
            _glassBaseLayer = new Border
            {
                CornerRadius = new CornerRadius(34),
                IsHitTestVisible = false
            };
            _glassAmbientLayer = new Border
            {
                CornerRadius = new CornerRadius(34),
                IsHitTestVisible = false
            };
            _glassRimLayer = new Border
            {
                CornerRadius = new CornerRadius(33),
                Margin = new Thickness(1),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            material.Children.Add(backdropClip);
            material.Children.Add(_glassBaseLayer);
            material.Children.Add(_glassAmbientLayer);
            material.Children.Add(_glassRimLayer);

            Grid layout = new Grid { Margin = new Thickness(10) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            material.Children.Add(layout);
            _shell.Child = material;

            _searchGlyph = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 17,16 L 22,21 M 19,10 A 8,8 0 1 1 3,10 A 8,8 0 1 1 19,10"),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.SizeAll,
                ToolTip = "拖动悬浮窗"
            };
            _searchGlyph.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragMove();
            };
            layout.Children.Add(_searchGlyph);

            Grid queryArea = new Grid { Margin = new Thickness(2, 0, 8, 0) };
            queryArea.ToolTip = "滚轮切换搜索引擎";
            queryArea.PreviewMouseWheel += EngineAreaOnMouseWheel;
            Grid.SetColumn(queryArea, 1);
            layout.Children.Add(queryArea);

            _queryBox = new TextBox
            {
                FontSize = 21,
                FontWeight = FontWeights.Medium,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2, 0, 0, 1),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            _queryBox.KeyDown += QueryBoxOnKeyDown;
            System.Windows.Automation.AutomationProperties.SetName(_queryBox, "搜索输入框");
            _queryBox.TextChanged += delegate
            {
                _placeholder.Visibility = _queryBox.Text.Length == 0
                    ? Visibility.Visible : Visibility.Collapsed;
                if (!_busy && string.IsNullOrWhiteSpace(_lastRequestId))
                    ShowIdleControls();
            };
            queryArea.Children.Add(_queryBox);

            _placeholder = new TextBlock
            {
                Text = "",
                FontSize = 21,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(3, 0, 0, 1),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            queryArea.Children.Add(_placeholder);

            _settingsButton = GlassButton("\uE713", 16, false);
            _settingsButton.FontFamily = new FontFamily("Segoe Fluent Icons");
            _settingsButton.FontWeight = FontWeights.Normal;
            _settingsButton.Margin = new Thickness(1, 2, 17, 2);
            _settingsButton.ToolTip = "搜索设置";
            System.Windows.Automation.AutomationProperties.SetName(_settingsButton, "搜索设置");
            _settingsButton.Click += OpenSettingsMenu;
            Grid.SetColumn(_settingsButton, 2);
            layout.Children.Add(_settingsButton);

            _statusButton = GlassButton("", 13, true);
            _statusButton.Margin = new Thickness(0, 2, 1, 2);
            _statusButton.Padding = new Thickness(14, 0, 14, 0);
            _statusButton.MinWidth = 220;
            _statusButton.MaxWidth = 340;
            _statusButton.Visibility = Visibility.Collapsed;
            _statusButton.IsHitTestVisible = false;
            _statusButton.Click += StatusButtonOnClick;
            Grid.SetColumn(_statusButton, 2);
            layout.Children.Add(_statusButton);

            Content = _shell;
            ApplyTheme();
            LoadEngines();

            _trayIcon = new Forms.NotifyIcon();
            _trayIcon.Icon = _appIcon ?? Drawing.SystemIcons.Application;
            _trayIcon.Text = "快捷搜索";
            _trayIcon.Visible = true;
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开搜索", null, delegate { Dispatcher.BeginInvoke(new Action(ShowSearch)); });
            menu.Items.Add("搜索引擎", null,
                delegate { Dispatcher.BeginInvoke(new Action(EditSearchEngines)); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(ExitApplication)); });
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowSearch)); };

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            IsVisibleChanged += delegate { ConfigureDynamicBlurTimer(); };
            SourceInitialized += OnSourceInitialized;
            Loaded += delegate
            {
                PositionWindow();
                _queryBox.Focus();
                if (!string.IsNullOrWhiteSpace(_bridgeStartError))
                    SetStatus("本机桥接启动失败：" + _bridgeStartError, "#FFD92D20", false);
                else if (_bridge.IsConnected)
                    ShowIdleControls();
                else
                    SetStatus("正在等待 Edge 扩展连接…", "#FFB54708", false);
                if (_appearancePreview)
                    Dispatcher.BeginInvoke(new Action(EditGeneralSettings));
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!_exiting)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
            Closed += delegate { Cleanup(); };
            Deactivated += delegate { Dispatcher.BeginInvoke(new Action(HideIfInactive)); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                    Hide();
            };
        }

        private static Brush Brush(string value)
        {
            return (Brush)new BrushConverter().ConvertFromString(value);
        }

        private static Button GlassButton(string text, double fontSize, bool surfaceFeedback)
        {
            return new Button
            {
                Content = text,
                Height = 44,
                FontSize = fontSize,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold,
                Template = CreateGlassButtonTemplate(surfaceFeedback)
            };
        }

        private static ControlTemplate CreateGlassButtonTemplate(bool surfaceFeedback,
            double cornerRadius = 24)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.Name = "Surface";
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
            surface.SetBinding(Border.BackgroundProperty, new Binding("Background")
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
            surface.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
            surface.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
            {
                RelativeSource = RelativeSource.TemplatedParent
            });

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content")
            {
                RelativeSource = RelativeSource.TemplatedParent
            });
            surface.AppendChild(content);
            template.VisualTree = surface;

            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            if (surfaceFeedback)
                hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#38FFFFFF"), "Surface"));
            else
                hover.Setters.Add(new Setter(Button.OpacityProperty, 0.82));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            if (surfaceFeedback)
                pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#52FFFFFF"), "Surface"));
            pressed.Setters.Add(new Setter(Button.OpacityProperty, surfaceFeedback ? 0.88 : 0.64));
            template.Triggers.Add(pressed);
            Trigger focus = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
            if (surfaceFeedback)
            {
                focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#FF0A84FF"), "Surface"));
                focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Surface"));
            }
            else
            {
                focus.Setters.Add(new Setter(Button.OpacityProperty, 0.82));
            }
            template.Triggers.Add(focus);
            return template;
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }

        private void ApplyTheme()
        {
            _isDark = IsDarkTheme();
            Color glassTop = ColorFrom(_isDark
                ? _settings.DarkBackgroundColor
                : _settings.LightBackgroundColor);
            glassTop.A = (byte)Math.Round(255 * _settings.BackgroundOpacity / 100.0);
            _glassBaseLayer.Background = new SolidColorBrush(glassTop);

            _glassAmbientLayer.Background = new LinearGradientBrush(
                ColorFrom(_isDark ? "#147DD3FC" : "#207DD3FC"),
                ColorFrom(_isDark ? "#10F3A4FF" : "#18FFB6E6"),
                new Point(0, 0), new Point(1, 1))
            {
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation
            };
            _idleRimBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(ColorFrom(_isDark ? "#48FFFFFF" : "#80FFFFFF"), 0),
                    new GradientStop(ColorFrom(_isDark ? "#16FFFFFF" : "#42FFFFFF"), 0.36),
                    new GradientStop(ColorFrom(_isDark ? "#087DD3FC" : "#207DD3FC"), 0.7),
                    new GradientStop(ColorFrom(_isDark ? "#2CFFFFFF" : "#50FFFFFF"), 1)
                }, new Point(0, 0), new Point(1, 1));
            if (!_busy && !_readyToOpen)
                _glassRimLayer.BorderBrush = _idleRimBrush;

            Brush foreground = Brush(_isDark ? "#FFF5F7FA" : "#F20B1220");
            Brush secondary = Brush(_isDark ? "#D1C9CFD8" : "#C2485565");
            _queryBox.Foreground = foreground;
            _queryBox.CaretBrush = Brush(_isDark ? "#FF64D2FF" : "#FF007AFF");
            _queryBox.SelectionBrush = Brush(_isDark ? "#8040C8E0" : "#663B82F6");
            _placeholder.Foreground = secondary;
            _searchGlyph.Stroke = secondary;
            _settingsButton.Foreground = secondary;
            _settingsButton.Background = Brushes.Transparent;
            _statusButton.Background = Brush(_isDark ? "#26FFFFFF" : "#78FFFFFF");
            _statusButton.Foreground = Brush(DarkStatusColor(_statusColor));
            _backdropBlur.Radius = _settings.BlurRadius;
            _backdropImage.Visibility = _settings.BlurRadius > 0
                ? Visibility.Visible : Visibility.Collapsed;
            ConfigureDynamicBlurTimer();
        }

        private void ConfigureDynamicBlurTimer()
        {
            _dynamicBlurTimer.Interval = TimeSpan.FromSeconds(
                1.0 / SearchSettings.NormalizeDynamicBlurFps(_settings.DynamicBlurFps));
            bool shouldRun = _settings.DynamicBlur && _settings.BlurRadius > 0 &&
                IsVisible && _source != null && _dynamicCaptureSupported;
            if (shouldRun)
            {
                if (!_captureAffinityApplied)
                {
                    // ponytail: 窗口可见期间持续排除自身，避免动态取样递归叠亮。
                    if (!NativeMethods.SetWindowDisplayAffinity(
                        new WindowInteropHelper(this).Handle,
                        NativeMethods.WdaExcludeFromCapture))
                    {
                        _dynamicCaptureSupported = false;
                        _dynamicBlurTimer.Stop();
                        return;
                    }
                    _captureAffinityApplied = true;
                    NativeMethods.DwmFlush();
                }
                _dynamicBlurTimer.Start();
            }
            else
            {
                _dynamicBlurTimer.Stop();
                RestoreCaptureAffinity();
            }
        }

        private void RestoreCaptureAffinity()
        {
            if (!_captureAffinityApplied)
                return;
            NativeMethods.SetWindowDisplayAffinity(
                new WindowInteropHelper(this).Handle, NativeMethods.WdaNone);
            _captureAffinityApplied = false;
        }

        private static bool IsDarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value != null && Convert.ToInt32(value) == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static Color ColorFrom(string value)
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }

        private void EngineAreaOnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0 || _settings.Engines.Count < 2)
                return;
            int index = _settings.Engines.IndexOf(_selectedEngine);
            index = SearchHelper.MoveIndex(index, _settings.Engines.Count, e.Delta);
            SelectEngine(_settings.Engines[index]);
            e.Handled = true;
        }

        private void SelectEngine(SearchEngine engine)
        {
            _selectedEngine = engine;
            _placeholder.Text = engine.Name;
            System.Windows.Automation.AutomationProperties.SetName(
                _queryBox, "使用 " + engine.Name + " 搜索");
            if (engine.Name == _settings.SelectedName)
                return;
            try { _settings.SaveSelection(engine.Name); }
            catch (IOException) { SetStatus("无法保存搜索引擎选择", "#FFD92D20", false); }
        }

        private void OpenSettingsMenu(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = CreateGlassMenu(_settingsButton);
            MenuItem appearance = new MenuItem { Header = "通用设置" };
            appearance.Click += delegate { EditGeneralSettings(); };
            MenuItem edit = new MenuItem { Header = "搜索引擎" };
            edit.Click += delegate { EditSearchEngines(); };
            menu.Items.Add(appearance);
            menu.Items.Add(edit);
            OpenGuardedMenu(menu);
        }

        private ContextMenu CreateGlassMenu(FrameworkElement target)
        {
            ContextMenu menu = new ContextMenu
            {
                PlacementTarget = target,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                VerticalOffset = 8,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HasDropShadow = false,
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
                Template = CreateGlassMenuTemplate()
            };
            menu.Resources[typeof(MenuItem)] = CreateGlassMenuItemStyle();
            return menu;
        }

        private ControlTemplate CreateGlassMenuTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ContextMenu));
            FrameworkElementFactory material = new FrameworkElementFactory(typeof(Grid));
            Color menuBase = ColorFrom(_isDark
                ? _settings.DarkBackgroundColor
                : _settings.LightBackgroundColor);
            menuBase.A = (byte)Math.Max(210,
                Math.Round(255 * _settings.BackgroundOpacity / 100.0));

            FrameworkElementFactory baseLayer = new FrameworkElementFactory(typeof(Border));
            baseLayer.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
            baseLayer.SetValue(Border.BackgroundProperty, new SolidColorBrush(menuBase));
            baseLayer.SetValue(UIElement.IsHitTestVisibleProperty, false);
            material.AppendChild(baseLayer);

            FrameworkElementFactory ambientLayer = new FrameworkElementFactory(typeof(Border));
            ambientLayer.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
            ambientLayer.SetValue(Border.BackgroundProperty, new LinearGradientBrush(
                ColorFrom(_isDark ? "#147DD3FC" : "#207DD3FC"),
                ColorFrom(_isDark ? "#10F3A4FF" : "#18FFB6E6"),
                new Point(0, 0), new Point(1, 1))
            {
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation
            });
            ambientLayer.SetValue(UIElement.IsHitTestVisibleProperty, false);
            material.AppendChild(ambientLayer);

            FrameworkElementFactory rimLayer = new FrameworkElementFactory(typeof(Border));
            rimLayer.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
            rimLayer.SetValue(Border.MarginProperty, new Thickness(1));
            rimLayer.SetValue(Border.BorderBrushProperty, _idleRimBrush);
            rimLayer.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            rimLayer.SetValue(UIElement.IsHitTestVisibleProperty, false);
            material.AppendChild(rimLayer);

            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
            surface.SetValue(Border.PaddingProperty, new Thickness(7));
            FrameworkElementFactory items = new FrameworkElementFactory(typeof(StackPanel));
            items.SetValue(StackPanel.IsItemsHostProperty, true);
            surface.AppendChild(items);
            material.AppendChild(surface);
            template.VisualTree = material;
            return template;
        }

        private Style CreateGlassMenuItemStyle()
        {
            Style style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(MenuItem.HeightProperty, 38.0));
            style.Setters.Add(new Setter(MenuItem.FontSizeProperty, 13.5));
            style.Setters.Add(new Setter(MenuItem.FontWeightProperty, FontWeights.Medium));
            style.Setters.Add(new Setter(MenuItem.ForegroundProperty,
                Brush(_isDark ? "#FFF3F5F8" : "#FF161B24")));

            ControlTemplate template = new ControlTemplate(typeof(MenuItem));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.Name = "ItemSurface";
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(12, 0, 18, 0));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(content);
            template.VisualTree = surface;
            Trigger highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlighted.Setters.Add(new Setter(Border.BackgroundProperty,
                Brush(_isDark ? "#287DD3FC" : "#207DD3FC"), "ItemSurface"));
            template.Triggers.Add(highlighted);
            Trigger disabled = new Trigger { Property = MenuItem.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(MenuItem.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(MenuItem.TemplateProperty, template));
            return style;
        }

        private void OpenGuardedMenu(ContextMenu menu)
        {
            _focusGuardCount++;
            menu.Closed += delegate
            {
                _focusGuardCount = Math.Max(0, _focusGuardCount - 1);
                Dispatcher.BeginInvoke(new Action(HideIfInactive));
            };
            menu.IsOpen = true;
        }

        private void HideIfInactive()
        {
            if (!_preview && _focusGuardCount == 0 && !IsActive)
                Hide();
        }

        private void ShowIdleControls()
        {
            _readyToOpen = false;
            ResetShellLight();
            _statusButton.Visibility = Visibility.Collapsed;
            _statusButton.IsHitTestVisible = false;
            _settingsButton.Visibility = Visibility.Visible;
            _queryBox.IsReadOnly = false;
            _shell.Cursor = Cursors.Arrow;
            System.Windows.Automation.AutomationProperties.SetHelpText(
                _queryBox, "输入文字后按 Enter 搜索，滚轮切换搜索引擎");
        }

        private void BridgeOnConnectionChanged(bool connected)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_busy || !string.IsNullOrWhiteSpace(_lastRequestId))
                    return;
                if (connected)
                    ShowIdleControls();
                else
                    SetStatus("Edge 扩展未连接", "#FFB54708", false);
            }));
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(WindowProcedure);
            PositionWindow();
            RefreshBackdrop();
            ApplyTheme();
            string error;
            if (!RegisterConfiguredHotkey(_settings.Hotkey, out error))
                SetStatus(error + "，可从托盘打开", "#FFB54708", false);
        }

        private bool RegisterConfiguredHotkey(string hotkey, out string error)
        {
            uint modifiers;
            uint virtualKey;
            string normalized;
            if (!SearchSettings.TryParseHotkey(hotkey, out modifiers, out virtualKey,
                out normalized))
            {
                error = "快捷键格式无效";
                return false;
            }

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (!NativeMethods.RegisterHotKey(handle, HotkeyId, modifiers, virtualKey))
            {
                error = normalized + " 已被占用";
                return false;
            }
            _registeredHotkeyModifiers = modifiers;
            _registeredHotkeyKey = virtualKey;
            _hotkeyRegistered = true;
            error = null;
            return true;
        }

        private bool TryApplyHotkey(string hotkey, out string error)
        {
            uint modifiers;
            uint virtualKey;
            string normalized;
            if (!SearchSettings.TryParseHotkey(hotkey, out modifiers, out virtualKey,
                out normalized))
            {
                error = "快捷键格式无效";
                return false;
            }
            if (_hotkeyRegistered && modifiers == _registeredHotkeyModifiers &&
                virtualKey == _registeredHotkeyKey)
            {
                error = null;
                return true;
            }

            uint previousModifiers = _registeredHotkeyModifiers;
            uint previousKey = _registeredHotkeyKey;
            bool hadPrevious = _hotkeyRegistered;
            SuspendConfiguredHotkey();
            if (NativeMethods.RegisterHotKey(new WindowInteropHelper(this).Handle,
                HotkeyId, modifiers, virtualKey))
            {
                _registeredHotkeyModifiers = modifiers;
                _registeredHotkeyKey = virtualKey;
                _hotkeyRegistered = true;
                error = null;
                return true;
            }

            _registeredHotkeyModifiers = previousModifiers;
            _registeredHotkeyKey = previousKey;
            if (hadPrevious)
                ResumeConfiguredHotkey();
            error = normalized + " 已被占用";
            return false;
        }

        private void SuspendConfiguredHotkey()
        {
            if (!_hotkeyRegistered)
                return;
            NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        private bool ResumeConfiguredHotkey()
        {
            if (_hotkeyRegistered)
                return true;
            if (_registeredHotkeyModifiers == 0 || _registeredHotkeyKey == 0)
                return false;
            _hotkeyRegistered = NativeMethods.RegisterHotKey(
                new WindowInteropHelper(this).Handle, HotkeyId,
                _registeredHotkeyModifiers, _registeredHotkeyKey);
            return _hotkeyRegistered;
        }

        private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                if (IsVisible)
                    Hide();
                else
                    ShowSearch();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void PositionWindow()
        {
            Rect area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + Math.Max(42, area.Height * 0.14);
        }

        private void RefreshBackdrop()
        {
            if (_settings.BlurRadius <= 0)
            {
                _backdropImage.Source = null;
                _backdropImage.Visibility = Visibility.Collapsed;
                return;
            }

            double scaleX = 1;
            double scaleY = 1;
            if (_source != null && _source.CompositionTarget != null)
            {
                Matrix transform = _source.CompositionTarget.TransformToDevice;
                scaleX = transform.M11;
                scaleY = transform.M22;
            }

            double padding = Math.Max(8, _settings.BlurRadius * 2);
            int pixelLeft = (int)Math.Round((Left - padding) * scaleX);
            int pixelTop = (int)Math.Round((Top - padding) * scaleY);
            int pixelWidth = Math.Max(1, (int)Math.Ceiling((Width + padding * 2) * scaleX));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling((Height + padding * 2) * scaleY));

            try
            {
                using (Drawing.Bitmap bitmap = new Drawing.Bitmap(pixelWidth, pixelHeight,
                    Drawing.Imaging.PixelFormat.Format32bppPArgb))
                using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(pixelLeft, pixelTop, 0, 0,
                        new Drawing.Size(pixelWidth, pixelHeight), Drawing.CopyPixelOperation.SourceCopy);
                    IntPtr bitmapHandle = bitmap.GetHbitmap(Drawing.Color.FromArgb(0));
                    try
                    {
                        BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle,
                            IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        _backdropImage.Source = source;
                        _backdropImage.Width = Width + padding * 2;
                        _backdropImage.Height = Height + padding * 2;
                        _backdropImage.Margin = new Thickness(-padding);
                        _backdropImage.Visibility = Visibility.Visible;
                    }
                    finally
                    {
                        NativeMethods.DeleteObject(bitmapHandle);
                    }
                }
            }
            catch (Exception)
            {
                _backdropImage.Source = null;
                _backdropImage.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowSearch()
        {
            PositionWindow();
            RefreshBackdrop();
            Show();
            Activate();
            if (SystemParameters.ClientAreaAnimation)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(0.78, 1,
                    TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            _queryBox.Focus();
            _queryBox.SelectAll();
        }

        private void QueryBoxOnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_readyToOpen)
                    StatusButtonOnClick(sender, e);
                else
                    BeginSearch();
            }
        }

        private async void BeginSearch()
        {
            if (_busy)
                return;

            SearchEngine engine = _selectedEngine;
            string query = _queryBox.Text.Trim();
            if (engine == null || query.Length == 0)
            {
                SetStatus("请输入要搜索的内容", "#FFD92D20", false);
                _queryBox.Focus();
                return;
            }
            if (!_bridge.IsConnected)
            {
                SetStatus("Edge 扩展未连接，请确认扩展已启用", "#FFD92D20", false);
                return;
            }

            _busy = true;
            ShowLoadingState();
            Task minimumPulse = Task.Delay(900);
            try
            {
                if (!string.IsNullOrWhiteSpace(_lastRequestId))
                    await _bridge.CancelAsync(_lastRequestId);
                _lastRequestId = null;

                string url = SearchHelper.BuildSearchUrl(query, engine.Template);
                _lastRequestId = await _bridge.SearchAsync(url, 30);
                // 保证快速网页也能完整显示一次呼吸动画。
                await minimumPulse;
                ShowReadyToOpen();
            }
            catch (Exception ex)
            {
                _lastRequestId = null;
                SetStatus("后台加载失败：" + ex.Message, "#FFD92D20", false);
            }
            finally
            {
                _busy = false;
            }
        }

        private async void StatusButtonOnClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastRequestId) || _busy)
                return;

            string requestId = _lastRequestId;
            _busy = true;
            _readyToOpen = false;
            _shell.Cursor = Cursors.Wait;
            try
            {
                NativeMethods.AllowAnyProcessToSetForegroundWindow();
                await _bridge.ShowAsync(requestId, 5);
                _lastRequestId = null;
                ShowIdleControls();
                Hide();
                bool activated = NativeMethods.BringProcessWindowToFront("msedge");
                if (_preview)
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                            "ui-handoff-test.txt"),
                        "BrowserActivated=" + (activated ? "Yes" : "No") +
                        "\r\nForegroundProcess=" + NativeMethods.GetForegroundProcessName() + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                SetStatus("无法打开结果：" + ex.Message, "#FFD92D20", true);
            }
            finally
            {
                _busy = false;
            }
        }

        private void ShellOnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_readyToOpen || _busy || _searchGlyph.IsMouseOver)
                return;
            e.Handled = true;
            StatusButtonOnClick(sender, e);
        }

        private void ShowReadyToOpen()
        {
            _readyToOpen = true;
            SetShellLight(DarkStatusColor("#FF067647"), false);
            _statusButton.Visibility = Visibility.Collapsed;
            _statusButton.IsHitTestVisible = false;
            _settingsButton.Visibility = Visibility.Collapsed;
            _queryBox.IsReadOnly = true;
            _shell.Cursor = Cursors.Hand;
            System.Windows.Automation.AutomationProperties.SetHelpText(
                _queryBox, "搜索完成，点击搜索框或按 Enter 打开");
        }

        private void ShowLoadingState()
        {
            _readyToOpen = false;
            SetShellLight(DarkStatusColor("#FFB54708"), true);
            _statusButton.Visibility = Visibility.Collapsed;
            _statusButton.IsHitTestVisible = false;
            _settingsButton.Visibility = Visibility.Collapsed;
            _queryBox.IsReadOnly = true;
            _shell.Cursor = Cursors.Wait;
            System.Windows.Automation.AutomationProperties.SetHelpText(
                _queryBox, "正在加载搜索结果");
        }

        private void SetShellLight(string color, bool pulse)
        {
            _glassRimLayer.BeginAnimation(OpacityProperty, null);
            _glassRimLayer.BorderThickness = new Thickness(2);
            _glassRimLayer.BorderBrush = Brush(color);
            _glassRimLayer.Opacity = 1;
            if (pulse && SystemParameters.ClientAreaAnimation)
            {
                _glassRimLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.05, 1,
                    TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
        }

        private void ResetShellLight()
        {
            _glassRimLayer.BeginAnimation(OpacityProperty, null);
            _glassRimLayer.Opacity = 1;
            _glassRimLayer.BorderThickness = new Thickness(1);
            _glassRimLayer.BorderBrush = _idleRimBrush;
        }

        private void SetStatus(string text, string color, bool clickable)
        {
            _readyToOpen = false;
            ResetShellLight();
            _settingsButton.Visibility = Visibility.Collapsed;
            _statusButton.Visibility = Visibility.Visible;
            _statusButton.Content = text;
            _statusColor = color;
            _statusButton.Foreground = Brush(DarkStatusColor(color));
            _statusButton.IsHitTestVisible = clickable;
            _statusButton.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;
            _queryBox.IsReadOnly = false;
            _shell.Cursor = Cursors.Arrow;
        }

        private string DarkStatusColor(string color)
        {
            if (!_isDark)
                return color;
            if (color == "#FF067647") return "#FF30D158";
            if (color == "#FFD92D20") return "#FFFF453A";
            if (color == "#FFB54708") return "#FFFF9F0A";
            return "#FFD1D5DB";
        }

        private void LoadEngines()
        {
            _selectedEngine = _settings.Engines.FirstOrDefault(e => e.Name == _settings.SelectedName)
                ?? _settings.Engines[0];
            _placeholder.Text = _selectedEngine.Name;
            System.Windows.Automation.AutomationProperties.SetName(
                _queryBox, "使用 " + _selectedEngine.Name + " 搜索");
        }

        private void EditGeneralSettings()
        {
            Window dialog = CreateSettingsDialog("通用设置", 430, 466);
            Grid material = CreateSettingsMaterial();
            Grid root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

            TextBlock heading = SettingsHeading("通用设置", dialog);
            root.Children.Add(heading);

            Grid opacityRow = new Grid();
            opacityRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            opacityRow.RowDefinitions.Add(new RowDefinition());
            Grid opacityHeader = new Grid();
            opacityHeader.ColumnDefinitions.Add(new ColumnDefinition());
            opacityHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock opacityValue = new TextBlock
            {
                Text = _settings.BackgroundOpacity + "%",
                TextAlignment = TextAlignment.Right,
                FontWeight = FontWeights.SemiBold
            };
            opacityHeader.Children.Add(new TextBlock
            {
                Text = "透明度",
                FontSize = 14,
                FontWeight = FontWeights.Medium
            });
            Grid.SetColumn(opacityValue, 1);
            opacityHeader.Children.Add(opacityValue);
            Slider opacitySlider = SettingsSlider(0, 100, _settings.BackgroundOpacity);
            opacitySlider.ValueChanged += delegate
            {
                opacityValue.Text = Math.Round(opacitySlider.Value) + "%";
            };
            Grid.SetRow(opacitySlider, 1);
            System.Windows.Automation.AutomationProperties.SetName(opacitySlider, "背景不透明度");
            opacityRow.Children.Add(opacityHeader);
            opacityRow.Children.Add(opacitySlider);
            root.Children.Add(AppearanceRow(opacityRow, 1));

            Grid blurRow = new Grid();
            blurRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            blurRow.RowDefinitions.Add(new RowDefinition());
            Grid blurHeader = new Grid();
            blurHeader.ColumnDefinitions.Add(new ColumnDefinition());
            blurHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock blurValue = new TextBlock
            {
                Text = _settings.BlurRadius.ToString(CultureInfo.InvariantCulture),
                TextAlignment = TextAlignment.Right,
                FontWeight = FontWeights.SemiBold
            };
            blurHeader.Children.Add(new TextBlock
            {
                Text = "高斯模糊",
                FontSize = 14,
                FontWeight = FontWeights.Medium
            });
            Grid.SetColumn(blurValue, 1);
            blurHeader.Children.Add(blurValue);
            Slider blurSlider = SettingsSlider(0, 40, _settings.BlurRadius);
            blurSlider.ValueChanged += delegate
            {
                blurValue.Text = Math.Round(blurSlider.Value).ToString(CultureInfo.InvariantCulture);
            };
            Grid.SetRow(blurSlider, 1);
            System.Windows.Automation.AutomationProperties.SetName(blurSlider, "高斯模糊强度");
            blurRow.Children.Add(blurHeader);
            blurRow.Children.Add(blurSlider);
            root.Children.Add(AppearanceRow(blurRow, 2));

            string pendingHotkey = _settings.Hotkey;
            string hotkeyBeforeCapture = pendingHotkey;
            bool capturingHotkey = false;
            bool hotkeySuspended = false;
            Button hotkeyButton = SettingsValueButton(FormatHotkeyForDisplay(pendingHotkey), 150);
            hotkeyButton.ToolTip = "点击后按下新的组合键";
            System.Windows.Automation.AutomationProperties.SetName(hotkeyButton, "设置唤醒快捷键");
            hotkeyButton.Click += delegate
            {
                if (capturingHotkey)
                    return;
                hotkeyBeforeCapture = pendingHotkey;
                capturingHotkey = true;
                hotkeySuspended = _hotkeyRegistered;
                SuspendConfiguredHotkey();
                hotkeyButton.Content = "请按快捷键";
                hotkeyButton.Focus();
            };
            hotkeyButton.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!capturingHotkey)
                    return;
                e.Handled = true;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                {
                    pendingHotkey = hotkeyBeforeCapture;
                    capturingHotkey = false;
                    hotkeyButton.Content = FormatHotkeyForDisplay(pendingHotkey);
                    if (hotkeySuspended)
                        ResumeConfiguredHotkey();
                    hotkeySuspended = false;
                    return;
                }

                string formatted;
                if (!SearchSettings.TryFormatHotkey(Keyboard.Modifiers, key, out formatted))
                {
                    hotkeyButton.Content = "继续按主键";
                    return;
                }
                pendingHotkey = formatted;
                capturingHotkey = false;
                hotkeyButton.Content = FormatHotkeyForDisplay(pendingHotkey);
                if (hotkeySuspended)
                    ResumeConfiguredHotkey();
                hotkeySuspended = false;
            };
            root.Children.Add(AppearanceRow(
                AppearanceColorRow("唤醒快捷键", hotkeyButton), 3));

            Button darkColorButton = SettingsValueButton("", 150);
            SetColorButton(darkColorButton, _settings.DarkBackgroundColor);
            darkColorButton.Click += delegate { PickColor(darkColorButton, dialog); };
            root.Children.Add(AppearanceRow(
                AppearanceColorRow("深色背景", darkColorButton), 4));

            Button lightColorButton = SettingsValueButton("", 150);
            SetColorButton(lightColorButton, _settings.LightBackgroundColor);
            lightColorButton.Click += delegate { PickColor(lightColorButton, dialog); };
            root.Children.Add(AppearanceRow(
                AppearanceColorRow("浅色背景", lightColorButton), 5));

            string executablePath = Process.GetCurrentProcess().MainModule.FileName;
            bool previousAutoStart = StartupManager.IsEnabled(executablePath);
            CheckBox autoStartCheck = SettingsCheckBox(previousAutoStart, "开机自启动");
            root.Children.Add(AppearanceRow(
                AppearanceColorRow("开机自启动", autoStartCheck), 6));

            int pendingDynamicBlurFps = _settings.DynamicBlurFps;
            CheckBox dynamicBlurCheck = SettingsCheckBox(
                _settings.DynamicBlur, "动态高斯模糊");
            dynamicBlurCheck.Margin = new Thickness(0, 0, 8, 0);
            StackPanel dynamicControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            dynamicControls.Children.Add(dynamicBlurCheck);
            Dictionary<int, Button> fpsButtons = new Dictionary<int, Button>();
            foreach (int fps in new[] { 10, 30, 60 })
            {
                Button fpsButton = SettingsFpsButton(fps);
                fpsButtons.Add(fps, fpsButton);
                fpsButton.Click += delegate(object sender, RoutedEventArgs e)
                {
                    pendingDynamicBlurFps = (int)((Button)sender).Tag;
                    RefreshFpsButtons(fpsButtons, pendingDynamicBlurFps,
                        dynamicBlurCheck.IsChecked == true);
                };
                dynamicControls.Children.Add(fpsButton);
            }
            dynamicBlurCheck.Checked += delegate
            {
                RefreshFpsButtons(fpsButtons, pendingDynamicBlurFps, true);
            };
            dynamicBlurCheck.Unchecked += delegate
            {
                RefreshFpsButtons(fpsButtons, pendingDynamicBlurFps, false);
            };
            RefreshFpsButtons(fpsButtons, pendingDynamicBlurFps,
                dynamicBlurCheck.IsChecked == true);
            root.Children.Add(AppearanceRow(
                AppearanceColorRow("动态高斯模糊", dynamicControls), 7));

            StackPanel actions = SettingsActions();
            Button cancel = SettingsActionButton("取消", false);
            cancel.IsCancel = true;
            Button save = SettingsActionButton("保存", true);
            save.IsDefault = true;
            save.Click += delegate
            {
                if (capturingHotkey)
                    return;
                if (hotkeySuspended)
                {
                    ResumeConfiguredHotkey();
                    hotkeySuspended = false;
                }

                string previousHotkey = _settings.Hotkey;
                string hotkeyError;
                if (!TryApplyHotkey(pendingHotkey, out hotkeyError))
                {
                    MessageBox.Show(dialog, hotkeyError, "快捷搜索",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    StartupManager.SetEnabled(executablePath,
                        autoStartCheck.IsChecked == true);
                    _settings.SaveGeneral((int)Math.Round(opacitySlider.Value),
                        (int)Math.Round(blurSlider.Value),
                        (string)darkColorButton.Tag, (string)lightColorButton.Tag,
                        pendingHotkey, dynamicBlurCheck.IsChecked == true,
                        pendingDynamicBlurFps);
                    _dynamicCaptureSupported = true;
                    ApplyTheme();
                    dialog.DialogResult = true;
                }
                catch (Exception ex)
                {
                    try { StartupManager.SetEnabled(executablePath, previousAutoStart); }
                    catch { }
                    string ignored;
                    TryApplyHotkey(previousHotkey, out ignored);
                    _settings = SearchSettings.Load(_settings.FilePath);
                    _dynamicCaptureSupported = true;
                    ApplyTheme();
                    MessageBox.Show(dialog, "无法保存通用设置：" + ex.Message, "快捷搜索",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            Grid.SetRow(actions, 8);
            root.Children.Add(actions);
            material.Children.Add(root);
            dialog.Content = material;
            dialog.Closed += delegate
            {
                if (hotkeySuspended)
                    ResumeConfiguredHotkey();
            };

            bool saved = ShowSettingsDialog(dialog);
            if (saved)
            {
                Hide();
                RefreshBackdrop();
                Show();
                Activate();
                _queryBox.Focus();
            }
        }

        private void EditSearchEngines()
        {
            Window dialog = CreateSettingsDialog("搜索引擎", 720, 420);
            Grid material = CreateSettingsMaterial();
            Grid root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });

            Grid titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition());
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.Children.Add(SettingsHeading("搜索引擎", dialog));
            Button add = SettingsActionButton("添加", false);
            add.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(add, 1);
            titleRow.Children.Add(add);
            root.Children.Add(titleRow);

            Grid header = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            AddEngineColumns(header);
            header.Children.Add(EngineColumnLabel("默认", 0));
            header.Children.Add(EngineColumnLabel("名称", 1));
            header.Children.Add(EngineColumnLabel("搜索地址", 2));
            Grid.SetRow(header, 1);
            root.Children.Add(header);

            StackPanel rows = new StackPanel();
            ScrollViewer scroll = new ScrollViewer
            {
                Content = rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            List<EngineEditorEntry> entries = new List<EngineEditorEntry>();
            EngineEditorEntry selectedEntry = null;
            Action refreshRows = delegate
            {
                rows.Children.Clear();
                foreach (EngineEditorEntry item in entries)
                {
                    item.CurrentButton.Content = item == selectedEntry ? "●" : "○";
                    item.CurrentButton.Foreground = item == selectedEntry
                        ? Brush(_isDark ? "#FF64D2FF" : "#FF007AFF")
                        : Brush(_isDark ? "#FF9CA3AF" : "#FF667085");
                    rows.Children.Add(item.Container);
                }
            };
            Action<EngineEditorEntry> selectEntry = delegate(EngineEditorEntry item)
            {
                selectedEntry = item;
                refreshRows();
            };
            Action<EngineEditorEntry, int> moveEntry = delegate(EngineEditorEntry item, int offset)
            {
                int oldIndex = entries.IndexOf(item);
                int newIndex = oldIndex + offset;
                if (newIndex < 0 || newIndex >= entries.Count)
                    return;
                entries.RemoveAt(oldIndex);
                entries.Insert(newIndex, item);
                refreshRows();
            };
            Action<EngineEditorEntry> removeEntry = delegate(EngineEditorEntry item)
            {
                if (entries.Count == 1)
                {
                    MessageBox.Show(dialog, "至少保留一个搜索引擎。", "快捷搜索",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                int oldIndex = entries.IndexOf(item);
                entries.Remove(item);
                if (selectedEntry == item)
                    selectedEntry = entries[Math.Min(oldIndex, entries.Count - 1)];
                refreshRows();
            };

            foreach (SearchEngine engine in _settings.Engines)
            {
                EngineEditorEntry entry = CreateEngineEditorEntry(engine, selectEntry,
                    moveEntry, removeEntry);
                entries.Add(entry);
                if (engine.Name == _settings.SelectedName)
                    selectedEntry = entry;
            }
            if (selectedEntry == null)
                selectedEntry = entries[0];
            refreshRows();

            add.Click += delegate
            {
                int suffix = 1;
                string name = "新搜索引擎";
                while (entries.Any(item => string.Equals(item.NameBox.Text, name,
                    StringComparison.OrdinalIgnoreCase)))
                    name = "新搜索引擎 " + ++suffix;
                SearchEngine engine = new SearchEngine(name,
                    "https://www.google.com/search?q={query}");
                EngineEditorEntry entry = CreateEngineEditorEntry(engine, selectEntry,
                    moveEntry, removeEntry);
                entries.Add(entry);
                selectedEntry = entry;
                refreshRows();
                entry.NameBox.Focus();
                entry.NameBox.SelectAll();
                scroll.ScrollToEnd();
            };

            StackPanel actions = SettingsActions();
            Button cancel = SettingsActionButton("取消", false);
            cancel.IsCancel = true;
            Button save = SettingsActionButton("保存", true);
            save.IsDefault = true;
            save.Click += delegate
            {
                try
                {
                    List<SearchEngine> engines = entries.Select(item => new SearchEngine(
                        item.NameBox.Text.Trim(), item.TemplateBox.Text.Trim())).ToList();
                    int selectedIndex = entries.IndexOf(selectedEntry);
                    string selectedName = engines[Math.Max(0, selectedIndex)].Name;
                    _settings.SaveEngines(engines, selectedName);
                    LoadEngines();
                    ShowIdleControls();
                    dialog.DialogResult = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, ex.Message, "快捷搜索",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            Grid.SetRow(actions, 3);
            root.Children.Add(actions);
            material.Children.Add(root);
            dialog.Content = material;
            ShowSettingsDialog(dialog);
        }

        private Window CreateSettingsDialog(string title, double width, double height)
        {
            Window dialog = new Window
            {
                Title = title,
                Owner = this,
                Width = width,
                Height = height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                FontFamily = FontFamily,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Foreground = Brush(_isDark ? "#FFF5F7FA" : "#FF111827")
            };
            PositionSettingsDialog(dialog);
            return dialog;
        }

        private Grid CreateSettingsMaterial()
        {
            Grid material = new Grid();
            Color dialogBase = ColorFrom(_isDark
                ? _settings.DarkBackgroundColor
                : _settings.LightBackgroundColor);
            dialogBase.A = (byte)Math.Max(220,
                Math.Round(255 * _settings.BackgroundOpacity / 100.0));
            material.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(26),
                Background = new SolidColorBrush(dialogBase),
                IsHitTestVisible = false
            });
            material.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(26),
                Background = new LinearGradientBrush(
                    ColorFrom(_isDark ? "#147DD3FC" : "#207DD3FC"),
                    ColorFrom(_isDark ? "#10F3A4FF" : "#18FFB6E6"),
                    new Point(0, 0), new Point(1, 1))
                {
                    ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation
                },
                IsHitTestVisible = false
            });
            material.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(25),
                Margin = new Thickness(1),
                BorderBrush = _idleRimBrush,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            });
            return material;
        }

        private void PositionSettingsDialog(Window dialog)
        {
            Rect area = SystemParameters.WorkArea;
            double left = area.Left + (area.Width - dialog.Width) / 2;
            double top = area.Top + (area.Height - dialog.Height) / 2;
            if (IsVisible)
            {
                Rect search = new Rect(Left - 16, Top - 16, Width + 32, Height + 32);
                Rect settings = new Rect(left, top, dialog.Width, dialog.Height);
                if (settings.IntersectsWith(search))
                {
                    double below = Top + Height + 20;
                    double above = Top - dialog.Height - 20;
                    if (below + dialog.Height <= area.Bottom)
                        top = below;
                    else if (above >= area.Top)
                        top = above;
                    else
                        left = Math.Min(area.Right - dialog.Width,
                            Math.Max(area.Left, Left + Width + 20));
                }
            }
            dialog.Left = Math.Max(area.Left, Math.Min(left, area.Right - dialog.Width));
            dialog.Top = Math.Max(area.Top, Math.Min(top, area.Bottom - dialog.Height));
        }

        private bool ShowSettingsDialog(Window dialog)
        {
            _focusGuardCount++;
            try { return dialog.ShowDialog() == true; }
            finally
            {
                _focusGuardCount = Math.Max(0, _focusGuardCount - 1);
                Dispatcher.BeginInvoke(new Action(HideIfInactive));
            }
        }

        private TextBlock SettingsHeading(string text, Window dialog)
        {
            TextBlock heading = new TextBlock
            {
                Text = text,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.SizeAll
            };
            heading.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    dialog.DragMove();
            };
            return heading;
        }

        private Slider SettingsSlider(double minimum, double maximum, double value)
        {
            return new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = value,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = Brush(_isDark ? "#FF64D2FF" : "#FF007AFF")
            };
        }

        private Button SettingsValueButton(string text, double width)
        {
            Button button = GlassButton(text, 13, false);
            button.Width = width;
            button.Height = 34;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.Background = Brush(_isDark ? "#24FFFFFF" : "#7AFFFFFF");
            button.BorderBrush = Brush(_isDark ? "#36FFFFFF" : "#300B1220");
            button.BorderThickness = new Thickness(1);
            button.Foreground = Brush(_isDark ? "#FFF5F7FA" : "#FF111827");
            button.Template = CreateGlassButtonTemplate(false, 12);
            return button;
        }

        private StackPanel SettingsActions()
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        private Button SettingsActionButton(string text, bool primary)
        {
            Button button = GlassButton(text, 13, false);
            button.Width = 80;
            button.Height = 34;
            button.Margin = new Thickness(8, 0, 0, 0);
            button.Background = Brush(primary
                ? (_isDark ? "#FF0A84FF" : "#FF007AFF")
                : (_isDark ? "#24FFFFFF" : "#7AFFFFFF"));
            button.Foreground = primary ? Brushes.White
                : Brush(_isDark ? "#FFF5F7FA" : "#FF111827");
            button.BorderBrush = Brush(primary
                ? "#00000000"
                : (_isDark ? "#30FFFFFF" : "#260B1220"));
            button.BorderThickness = new Thickness(1);
            button.Template = CreateGlassButtonTemplate(false, 12);
            return button;
        }

        private CheckBox SettingsCheckBox(bool isChecked, string accessibleName)
        {
            CheckBox checkBox = new CheckBox
            {
                IsChecked = isChecked,
                Width = 24,
                Height = 24,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Template = CreateSettingsCheckBoxTemplate()
            };
            System.Windows.Automation.AutomationProperties.SetName(
                checkBox, accessibleName);
            return checkBox;
        }

        private ControlTemplate CreateSettingsCheckBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Grid));

            FrameworkElementFactory box = new FrameworkElementFactory(typeof(Border));
            box.Name = "CheckSurface";
            box.SetValue(FrameworkElement.WidthProperty, 24.0);
            box.SetValue(FrameworkElement.HeightProperty, 24.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            box.SetValue(Border.BackgroundProperty,
                Brush(_isDark ? "#20FFFFFF" : "#66FFFFFF"));
            box.SetValue(Border.BorderBrushProperty,
                Brush(_isDark ? "#40FFFFFF" : "#380B1220"));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            surface.AppendChild(box);

            FrameworkElementFactory check = new FrameworkElementFactory(
                typeof(System.Windows.Shapes.Path));
            check.Name = "CheckMark";
            check.SetValue(System.Windows.Shapes.Path.DataProperty,
                Geometry.Parse("M 5,12 L 10,17 L 19,7"));
            check.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.White);
            check.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.2);
            check.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty,
                PenLineCap.Round);
            check.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty,
                PenLineCap.Round);
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            surface.AppendChild(check);
            template.VisualTree = surface;

            Trigger selected = new Trigger
            {
                Property = CheckBox.IsCheckedProperty,
                Value = true
            };
            selected.Setters.Add(new Setter(Border.BackgroundProperty,
                Brush(_isDark ? "#FF0A84FF" : "#FF007AFF"), "CheckSurface"));
            selected.Setters.Add(new Setter(Border.BorderBrushProperty,
                Brush(_isDark ? "#FF64D2FF" : "#FF007AFF"), "CheckSurface"));
            selected.Setters.Add(new Setter(UIElement.VisibilityProperty,
                Visibility.Visible, "CheckMark"));
            template.Triggers.Add(selected);

            Trigger hover = new Trigger
            {
                Property = CheckBox.IsMouseOverProperty,
                Value = true
            };
            hover.Setters.Add(new Setter(CheckBox.OpacityProperty, 0.82));
            template.Triggers.Add(hover);
            Trigger focus = new Trigger
            {
                Property = CheckBox.IsKeyboardFocusedProperty,
                Value = true
            };
            focus.Setters.Add(new Setter(Border.BorderThicknessProperty,
                new Thickness(2), "CheckSurface"));
            template.Triggers.Add(focus);
            return template;
        }

        private Button SettingsFpsButton(int fps)
        {
            Button button = GlassButton(fps.ToString(CultureInfo.InvariantCulture), 12, false);
            button.Width = 42;
            button.Height = 30;
            button.Margin = new Thickness(4, 0, 0, 0);
            button.Tag = fps;
            button.BorderThickness = new Thickness(1);
            button.Template = CreateGlassButtonTemplate(false, 10);
            System.Windows.Automation.AutomationProperties.SetName(
                button, fps + " FPS");
            return button;
        }

        private void RefreshFpsButtons(Dictionary<int, Button> buttons,
            int selectedFps, bool enabled)
        {
            foreach (KeyValuePair<int, Button> item in buttons)
            {
                bool selected = item.Key == selectedFps;
                item.Value.IsEnabled = enabled;
                item.Value.Opacity = enabled ? 1 : 0.42;
                item.Value.Background = Brush(selected
                    ? (_isDark ? "#FF0A84FF" : "#FF007AFF")
                    : (_isDark ? "#20FFFFFF" : "#66FFFFFF"));
                item.Value.Foreground = selected ? Brushes.White
                    : Brush(_isDark ? "#FFF5F7FA" : "#FF111827");
                item.Value.BorderBrush = Brush(selected
                    ? "#00000000"
                    : (_isDark ? "#30FFFFFF" : "#260B1220"));
            }
        }

        private TextBox SettingsTextBox(string text)
        {
            TextBox box = new TextBox
            {
                Text = text,
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 0, 10, 0),
                Height = 34,
                BorderThickness = new Thickness(1),
                BorderBrush = Brush(_isDark ? "#30FFFFFF" : "#260B1220"),
                Background = Brush(_isDark ? "#20FFFFFF" : "#66FFFFFF"),
                Foreground = Brush(_isDark ? "#FFF5F7FA" : "#FF111827"),
                SelectionBrush = Brush(_isDark ? "#FF64D2FF" : "#FF007AFF"),
                Template = CreateSettingsTextBoxTemplate()
            };
            return box;
        }

        private ControlTemplate CreateSettingsTextBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.Name = "TextSurface";
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            surface.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            surface.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            surface.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(Control.BorderThicknessProperty));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ScrollViewer));
            content.Name = "PART_ContentHost";
            content.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(content);
            template.VisualTree = surface;
            Trigger focused = new Trigger { Property = TextBox.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty,
                Brush(_isDark ? "#FF64D2FF" : "#FF007AFF"), "TextSurface"));
            template.Triggers.Add(focused);
            return template;
        }

        private static string FormatHotkeyForDisplay(string hotkey)
        {
            return hotkey.Replace("+", " + ");
        }

        private static void AddEngineColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(134) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        }

        private static TextBlock EngineColumnLabel(string text, int column)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Opacity = 0.66,
                Margin = new Thickness(column == 0 ? 0 : 5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, column);
            return label;
        }

        private EngineEditorEntry CreateEngineEditorEntry(SearchEngine engine,
            Action<EngineEditorEntry> select, Action<EngineEditorEntry, int> move,
            Action<EngineEditorEntry> remove)
        {
            EngineEditorEntry entry = new EngineEditorEntry();
            Grid row = new Grid();
            AddEngineColumns(row);

            entry.CurrentButton = EngineActionButton("○", "设为默认搜索引擎");
            entry.CurrentButton.Click += delegate { select(entry); };
            row.Children.Add(entry.CurrentButton);

            entry.NameBox = SettingsTextBox(engine.Name);
            entry.NameBox.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(entry.NameBox, 1);
            row.Children.Add(entry.NameBox);

            entry.TemplateBox = SettingsTextBox(engine.Template);
            entry.TemplateBox.Margin = new Thickness(4, 0, 4, 0);
            System.Windows.Automation.AutomationProperties.SetName(
                entry.TemplateBox, engine.Name + " 搜索地址");
            Grid.SetColumn(entry.TemplateBox, 2);
            row.Children.Add(entry.TemplateBox);

            Button up = EngineActionButton("↑", "上移");
            up.Click += delegate { move(entry, -1); };
            Grid.SetColumn(up, 3);
            row.Children.Add(up);

            Button down = EngineActionButton("↓", "下移");
            down.Click += delegate { move(entry, 1); };
            Grid.SetColumn(down, 4);
            row.Children.Add(down);

            Button delete = EngineActionButton("×", "删除");
            delete.Click += delegate { remove(entry); };
            Grid.SetColumn(delete, 5);
            row.Children.Add(delete);

            entry.Container = new Border
            {
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(8, 3, 8, 3),
                Padding = new Thickness(4),
                Background = Brush(_isDark ? "#14FFFFFF" : "#3AFFFFFF"),
                Child = row
            };
            return entry;
        }

        private Button EngineActionButton(string text, string tooltip)
        {
            Button button = GlassButton(text, 14, false);
            button.Width = 30;
            button.Height = 30;
            button.Margin = new Thickness(3, 0, 3, 0);
            button.Background = Brushes.Transparent;
            button.Foreground = Brush(_isDark ? "#FFF5F7FA" : "#FF111827");
            button.ToolTip = tooltip;
            System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
            return button;
        }

        private static Border AppearanceRow(UIElement content, int row)
        {
            Border container = new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                Child = content
            };
            Grid.SetRow(container, row);
            return container;
        }

        private static Grid AppearanceColorRow(string label, FrameworkElement control)
        {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        private static void SetColorButton(Button button, string value)
        {
            Color color = ColorFrom(value);
            button.Tag = value.ToUpperInvariant();
            button.Content = value.ToUpperInvariant();
            button.Background = new SolidColorBrush(color);
            double brightness = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
            button.Foreground = brightness > 150 ? Brushes.Black : Brushes.White;
        }

        private static void PickColor(Button button, Window owner)
        {
            using (Forms.ColorDialog picker = new Forms.ColorDialog())
            {
                picker.Color = Drawing.ColorTranslator.FromHtml((string)button.Tag);
                picker.FullOpen = true;
                IntPtr handle = new WindowInteropHelper(owner).Handle;
                if (picker.ShowDialog(new WindowHandleWrapper(handle)) != Forms.DialogResult.OK)
                    return;
                string value = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    picker.Color.R, picker.Color.G, picker.Color.B);
                SetColorButton(button, value);
            }
        }

        private void ExitApplication()
        {
            _exiting = true;
            Close();
            Application.Current.Shutdown();
        }

        private void Cleanup()
        {
            _dynamicBlurTimer.Stop();
            RestoreCaptureAffinity();
            SuspendConfiguredHotkey();
            if (_source != null)
                _source.RemoveHook(WindowProcedure);
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            if (_appIcon != null)
                _appIcon.Dispose();
            _bridge.Dispose();
        }

        private sealed class WindowHandleWrapper : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }

            public WindowHandleWrapper(IntPtr handle)
            {
                Handle = handle;
            }
        }

        private sealed class EngineEditorEntry
        {
            public Border Container { get; set; }
            public Button CurrentButton { get; set; }
            public TextBox NameBox { get; set; }
            public TextBox TemplateBox { get; set; }
        }
    }

    internal static class SearchHelper
    {
        public static int MoveIndex(int index, int count, int wheelDelta)
        {
            int direction = wheelDelta > 0 ? -1 : 1;
            return (index + direction + count) % count;
        }

        public static string BuildSearchUrl(string query, string template)
        {
            return template.Replace("{query}", Uri.EscapeDataString(query));
        }

        public static string GetDefaultBrowserExecutable()
        {
            uint length = 0;
            NativeMethods.AssocQueryString(0, NativeMethods.AssocStrExecutable, "http", null, null, ref length);
            if (length == 0)
                return null;
            StringBuilder output = new StringBuilder((int)length);
            uint result = NativeMethods.AssocQueryString(0, NativeMethods.AssocStrExecutable,
                "http", null, output, ref length);
            return result == 0 ? output.ToString() : null;
        }
    }

    internal sealed class EdgeExtensionBridge : IDisposable
    {
        public const int DefaultPort = 17891;
        private const int MaximumMessageBytes = 16384;

        private readonly object _gate = new object();
        private readonly int _port;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, TaskCompletionSource<string>> _ready =
            new Dictionary<string, TaskCompletionSource<string>>();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _shown =
            new Dictionary<string, TaskCompletionSource<bool>>();
        private HttpListener _listener;
        private WebSocket _socket;
        private bool _disposed;

        public event Action<bool> ConnectionChanged;

        public EdgeExtensionBridge(int port)
        {
            _port = port;
        }

        public string WebSocketUrl
        {
            get { return "ws://127.0.0.1:" + _port + "/quicksearch/"; }
        }

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                    return _socket != null && _socket.State == WebSocketState.Open;
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_listener != null)
                    return;
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/quicksearch/");
                _listener.Start();
            }
            Task.Run((Func<Task>)AcceptLoopAsync);
        }

        public async Task<string> SearchAsync(string url, int timeoutSeconds)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("搜索地址无效。", "url");

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<string> completion = new TaskCompletionSource<string>();
            lock (_gate)
                _ready.Add(requestId, completion);

            try
            {
                await SendTextAsync("search\t" + requestId + "\t" + url);
                return await WaitWithTimeout(completion.Task, timeoutSeconds, "网页加载超时");
            }
            catch
            {
                lock (_gate)
                    _ready.Remove(requestId);
                if (IsConnected)
                {
                    Task ignored = SendTextAsync("cancel\t" + requestId).ContinueWith(delegate(Task task)
                    {
                        if (task.IsFaulted) { Exception observed = task.Exception; }
                    });
                }
                throw;
            }
        }

        public async Task ShowAsync(string requestId, int timeoutSeconds)
        {
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            lock (_gate)
                _shown[requestId] = completion;
            try
            {
                await SendTextAsync("show\t" + requestId);
                await WaitWithTimeout(completion.Task, timeoutSeconds, "Edge 未响应");
            }
            finally
            {
                lock (_gate)
                    _shown.Remove(requestId);
            }
        }

        public async Task CancelAsync(string requestId)
        {
            if (!string.IsNullOrWhiteSpace(requestId) && IsConnected)
                await SendTextAsync("cancel\t" + requestId);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_disposed)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    string origin = context.Request.Headers["Origin"];
                    if (!context.Request.IsWebSocketRequest || string.IsNullOrWhiteSpace(origin) ||
                        !origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = 403;
                        context.Response.Close();
                        continue;
                    }

                    HttpListenerWebSocketContext webSocketContext =
                        await context.AcceptWebSocketAsync(null);
                    WebSocket old;
                    lock (_gate)
                    {
                        old = _socket;
                        _socket = webSocketContext.WebSocket;
                    }
                    if (old != null)
                        old.Dispose();
                    RaiseConnectionChanged(true);
                    Task ignored = ReceiveLoopAsync(webSocketContext.WebSocket).ContinueWith(delegate(Task task)
                    {
                        if (task.IsFaulted) { Exception observed = task.Exception; }
                    });
                }
                catch (HttpListenerException)
                {
                    if (!_disposed)
                        FailAll(new InvalidOperationException("本机桥接已停止。"));
                }
                catch (ObjectDisposedException) { }
            }
        }

        private async Task ReceiveLoopAsync(WebSocket socket)
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!_disposed && socket.State == WebSocketState.Open)
                {
                    using (MemoryStream message = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                                return;
                            message.Write(buffer, 0, result.Count);
                            if (message.Length > MaximumMessageBytes)
                                throw new InvalidOperationException("扩展消息过长。");
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Text)
                            HandleMessage(Encoding.UTF8.GetString(message.ToArray()));
                    }
                }
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                bool disconnected = false;
                lock (_gate)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                        disconnected = true;
                    }
                }
                socket.Dispose();
                if (disconnected)
                {
                    FailAll(new InvalidOperationException("Edge 扩展连接已断开。"));
                    RaiseConnectionChanged(false);
                }
            }
        }

        private void HandleMessage(string message)
        {
            string[] parts = message.Split(new[] { '\t' }, 3);
            if (parts.Length == 0)
                return;
            if (parts[0] == "ping")
            {
                SendTextAsync("pong").ContinueWith(delegate(Task task)
                {
                    if (task.IsFaulted) { Exception ignored = task.Exception; }
                });
                return;
            }
            if (parts.Length < 2)
                return;

            TaskCompletionSource<string> readyCompletion = null;
            TaskCompletionSource<bool> shownCompletion = null;
            lock (_gate)
            {
                if (parts[0] == "ready" && _ready.TryGetValue(parts[1], out readyCompletion))
                    _ready.Remove(parts[1]);
                else if (parts[0] == "shown")
                    _shown.TryGetValue(parts[1], out shownCompletion);
                else if (parts[0] == "error")
                {
                    _ready.TryGetValue(parts[1], out readyCompletion);
                    _shown.TryGetValue(parts[1], out shownCompletion);
                    _ready.Remove(parts[1]);
                    _shown.Remove(parts[1]);
                }
            }

            if (parts[0] == "ready" && readyCompletion != null)
                readyCompletion.TrySetResult(parts[1]);
            else if (parts[0] == "shown" && shownCompletion != null)
                shownCompletion.TrySetResult(true);
            else if (parts[0] == "error")
            {
                string detail = parts.Length > 2 ? parts[2] : "Edge 扩展执行失败。";
                InvalidOperationException error = new InvalidOperationException(detail);
                if (readyCompletion != null)
                    readyCompletion.TrySetException(error);
                if (shownCompletion != null)
                    shownCompletion.TrySetException(error);
            }
        }

        private async Task SendTextAsync(string message)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            await _sendLock.WaitAsync();
            try
            {
                WebSocket socket;
                lock (_gate)
                    socket = _socket;
                if (socket == null || socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("Edge 扩展未连接。");
                await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true,
                    CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static async Task<T> WaitWithTimeout<T>(Task<T> task, int timeoutSeconds, string message)
        {
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner != task)
                throw new TimeoutException(message);
            return await task;
        }

        private static async Task WaitWithTimeout(Task task, int timeoutSeconds, string message)
        {
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (winner != task)
                throw new TimeoutException(message);
            await task;
        }

        private void RaiseConnectionChanged(bool connected)
        {
            Action<bool> handler = ConnectionChanged;
            if (handler != null)
                handler(connected);
        }

        private void FailAll(Exception error)
        {
            List<TaskCompletionSource<string>> ready;
            List<TaskCompletionSource<bool>> shown;
            lock (_gate)
            {
                ready = _ready.Values.ToList();
                shown = _shown.Values.ToList();
                _ready.Clear();
                _shown.Clear();
            }
            foreach (TaskCompletionSource<string> completion in ready)
                completion.TrySetException(error);
            foreach (TaskCompletionSource<bool> completion in shown)
                completion.TrySetException(error);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            HttpListener listener;
            WebSocket socket;
            lock (_gate)
            {
                listener = _listener;
                _listener = null;
                socket = _socket;
                _socket = null;
            }
            if (listener != null)
            {
                listener.Stop();
                listener.Close();
            }
            if (socket != null)
                socket.Dispose();
            FailAll(new ObjectDisposedException("EdgeExtensionBridge"));
            _sendLock.Dispose();
        }
    }

    internal static class NativeMethods
    {
        public const int WmHotkey = 0x0312;
        public const uint AssocStrExecutable = 2;
        public const uint WdaNone = 0;
        public const uint WdaExcludeFromCapture = 0x11;
        private const int SwRestore = 9;

        private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr objectHandle);

        [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern uint AssocQueryString(uint flags, uint query, string association,
            string extra, StringBuilder output, ref uint outputLength);

        public static void AllowAnyProcessToSetForegroundWindow()
        {
            AllowSetForegroundWindow(uint.MaxValue);
        }

        public static bool BringProcessWindowToFront(string processName)
        {
            IntPtr target = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr state)
            {
                if (!IsWindowVisible(hwnd))
                    return true;
                uint processId;
                GetWindowThreadProcessId(hwnd, out processId);
                try
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        if (string.Equals(process.ProcessName, processName,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            target = hwnd;
                            return false;
                        }
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                return true;
            }, IntPtr.Zero);

            if (target == IntPtr.Zero)
                return false;
            ShowWindowAsync(target, SwRestore);
            return SetForegroundWindow(target);
        }

        public static string GetForegroundProcessName()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return "";
            uint processId;
            GetWindowThreadProcessId(foreground, out processId);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    return process.ProcessName;
            }
            catch
            {
                return "";
            }
        }

    }
}
