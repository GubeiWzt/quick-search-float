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
                    MessageBox.Show("快捷搜索已经在运行，请按 Ctrl+Alt+Space 唤出。", "快捷搜索",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                bool preview = args.Any(arg => string.Equals(arg, "--ui-preview",
                    StringComparison.OrdinalIgnoreCase));
                MainWindow window = new MainWindow(settings, preview);
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
                if (!settings.DarkBackgroundColor.StartsWith("#") ||
                    !settings.LightBackgroundColor.StartsWith("#"))
                    throw new InvalidOperationException("背景颜色配置无效。");

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
        public string DarkBackgroundColor { get; private set; }
        public string LightBackgroundColor { get; private set; }

        private SearchSettings(string filePath)
        {
            FilePath = filePath;
            Engines = new List<SearchEngine>();
            BackgroundOpacity = 70;
            DarkBackgroundColor = "#1C2027";
            LightBackgroundColor = "#FFFFFF";
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

                    Uri uri;
                    string sample = value.Replace("{query}", "test");
                    if (value.Contains("{query}") && Uri.TryCreate(sample, UriKind.Absolute, out uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
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

        public void SaveAppearance(int opacity, string darkColor, string lightColor)
        {
            BackgroundOpacity = NormalizeOpacity(opacity);
            DarkBackgroundColor = IsRgbColor(darkColor) ? darkColor.ToUpperInvariant() : "#1C2027";
            LightBackgroundColor = IsRgbColor(lightColor) ? lightColor.ToUpperInvariant() : "#FFFFFF";

            List<string> lines = File.Exists(FilePath)
                ? File.ReadAllLines(FilePath, Encoding.UTF8).ToList()
                : new List<string>();
            Upsert(lines, "opacity", BackgroundOpacity.ToString(CultureInfo.InvariantCulture));
            Upsert(lines, "darkColor", DarkBackgroundColor);
            Upsert(lines, "lightColor", LightBackgroundColor);
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
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

        private static bool IsRgbColor(string value)
        {
            int parsed;
            return value != null && value.Length == 7 && value[0] == '#' &&
                int.TryParse(value.Substring(1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out parsed);
        }
    }

    internal sealed class MainWindow : Window
    {
        private const int HotkeyId = 0x5153;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint VirtualKeySpace = 0x20;

        private SearchSettings _settings;
        private readonly TextBox _queryBox;
        private readonly TextBlock _placeholder;
        private readonly Button _engineButton;
        private readonly Button _statusButton;
        private readonly Button _settingsButton;
        private readonly Grid _idleControls;
        private readonly Border _shell;
        private readonly Border _glassBaseLayer;
        private readonly Border _glassAmbientLayer;
        private readonly Border _glassRimLayer;
        private readonly System.Windows.Shapes.Path _searchGlyph;
        private readonly Forms.NotifyIcon _trayIcon;
        private readonly EdgeExtensionBridge _bridge;
        private readonly bool _preview;
        private HwndSource _source;
        private SearchEngine _selectedEngine;
        private string _bridgeStartError;
        private string _lastRequestId;
        private string _statusColor = "#FF475467";
        private bool _isDark;
        private bool _busy;
        private bool _exiting;
        private int _focusGuardCount;

        public MainWindow(SearchSettings settings, bool preview)
        {
            _settings = settings;
            _preview = preview;
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

            Grid material = new Grid();
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
            System.Windows.Automation.AutomationProperties.SetName(_queryBox, "搜索网页");
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
                Text = "搜索网页",
                FontSize = 21,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(3, 0, 0, 1),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            queryArea.Children.Add(_placeholder);

            _idleControls = new Grid { Margin = new Thickness(0, 0, 1, 0) };
            _idleControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            _idleControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            Grid.SetColumn(_idleControls, 2);
            layout.Children.Add(_idleControls);

            _engineButton = GlassButton("", 14);
            _engineButton.Margin = new Thickness(0, 2, 4, 2);
            _engineButton.ToolTip = "点击选择，滚轮切换搜索引擎";
            _engineButton.Click += OpenEngineMenu;
            _engineButton.PreviewMouseWheel += EngineButtonOnMouseWheel;
            _idleControls.Children.Add(_engineButton);

            _settingsButton = GlassButton("\uE713", 16);
            _settingsButton.FontFamily = new FontFamily("Segoe Fluent Icons");
            _settingsButton.FontWeight = FontWeights.Normal;
            _settingsButton.Margin = new Thickness(1, 2, 1, 2);
            _settingsButton.ToolTip = "搜索设置";
            System.Windows.Automation.AutomationProperties.SetName(_settingsButton, "搜索设置");
            _settingsButton.Click += OpenSettingsMenu;
            Grid.SetColumn(_settingsButton, 1);
            _idleControls.Children.Add(_settingsButton);

            _statusButton = GlassButton("", 13);
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
            _trayIcon.Icon = Drawing.SystemIcons.Information;
            _trayIcon.Text = "快捷搜索";
            _trayIcon.Visible = true;
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开搜索", null, delegate { Dispatcher.BeginInvoke(new Action(ShowSearch)); });
            menu.Items.Add("编辑搜索引擎", null, delegate { Dispatcher.BeginInvoke(new Action(EditSettings)); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(ExitApplication)); });
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowSearch)); };

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
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

        private static Button GlassButton(string text, double fontSize)
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
                Template = CreateGlassButtonTemplate()
            };
        }

        private static ControlTemplate CreateGlassButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.Name = "Surface";
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(24));
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
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#38FFFFFF"), "Surface"));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#52FFFFFF"), "Surface"));
            pressed.Setters.Add(new Setter(Button.OpacityProperty, 0.88));
            template.Triggers.Add(pressed);
            Trigger focus = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#FF0A84FF"), "Surface"));
            focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Surface"));
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
            Color glassBottom = glassTop;
            glassBottom.A = (byte)Math.Round(glassTop.A * 0.8);
            _glassBaseLayer.Background = new LinearGradientBrush(glassTop, glassBottom, 35);

            _glassAmbientLayer.Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(ColorFrom(_isDark ? "#1A7DD3FC" : "#247DD3FC"), 0),
                    new GradientStop(ColorFrom("#007DD3FC"), 0.48),
                    new GradientStop(ColorFrom(_isDark ? "#12F3A4FF" : "#18FFB6E6"), 1)
                }, new Point(0, 0), new Point(1, 1));
            _glassRimLayer.BorderBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(ColorFrom(_isDark ? "#48FFFFFF" : "#80FFFFFF"), 0),
                    new GradientStop(ColorFrom(_isDark ? "#16FFFFFF" : "#42FFFFFF"), 0.36),
                    new GradientStop(ColorFrom(_isDark ? "#087DD3FC" : "#207DD3FC"), 0.7),
                    new GradientStop(ColorFrom(_isDark ? "#2CFFFFFF" : "#50FFFFFF"), 1)
                }, new Point(0, 0), new Point(1, 1));

            Brush foreground = Brush(_isDark ? "#FFF5F7FA" : "#F20B1220");
            Brush secondary = Brush(_isDark ? "#D1C9CFD8" : "#C2485565");
            _queryBox.Foreground = foreground;
            _queryBox.CaretBrush = Brush(_isDark ? "#FF64D2FF" : "#FF007AFF");
            _queryBox.SelectionBrush = Brush(_isDark ? "#8040C8E0" : "#663B82F6");
            _placeholder.Foreground = secondary;
            _searchGlyph.Stroke = secondary;
            _engineButton.Foreground = foreground;
            _settingsButton.Foreground = secondary;
            _engineButton.Background = Brush(_isDark ? "#30FFFFFF" : "#78FFFFFF");
            _engineButton.BorderBrush = Brush(_isDark ? "#20FFFFFF" : "#48FFFFFF");
            _engineButton.BorderThickness = new Thickness(1);
            _settingsButton.Background = Brushes.Transparent;
            _statusButton.Background = Brush(_isDark ? "#26FFFFFF" : "#78FFFFFF");
            _statusButton.Foreground = Brush(DarkStatusColor(_statusColor));

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

        private void OpenEngineMenu(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = CreateGlassMenu(_engineButton);
            foreach (SearchEngine engine in _settings.Engines)
            {
                MenuItem item = new MenuItem
                {
                    Header = (ReferenceEquals(engine, _selectedEngine) ? "✓  " : "    ") + engine.Name
                };
                item.Click += delegate { SelectEngine(engine); };
                menu.Items.Add(item);
            }
            OpenGuardedMenu(menu);
        }

        private void EngineButtonOnMouseWheel(object sender, MouseWheelEventArgs e)
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
            _engineButton.Content = engine.Name + "  ▾";
            if (engine.Name == _settings.SelectedName)
                return;
            try { _settings.SaveSelection(engine.Name); }
            catch (IOException) { SetStatus("无法保存搜索引擎选择", "#FFD92D20", false); }
        }

        private void OpenSettingsMenu(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = CreateGlassMenu(_settingsButton);
            MenuItem appearance = new MenuItem { Header = "外观设置…" };
            appearance.Click += delegate { EditAppearance(); };
            MenuItem edit = new MenuItem { Header = "编辑搜索引擎…" };
            edit.Click += delegate { EditSettings(); };
            MenuItem reload = new MenuItem { Header = "重新加载配置" };
            reload.Click += delegate { ReloadSettings(); };
            menu.Items.Add(appearance);
            menu.Items.Add(new Separator());
            menu.Items.Add(edit);
            menu.Items.Add(reload);
            OpenGuardedMenu(menu);
        }

        private ContextMenu CreateGlassMenu(FrameworkElement target)
        {
            ContextMenu menu = new ContextMenu
            {
                PlacementTarget = target,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HasDropShadow = false,
                Template = CreateGlassMenuTemplate()
            };
            menu.Resources[typeof(MenuItem)] = CreateGlassMenuItemStyle();
            menu.Resources[typeof(Separator)] = CreateGlassSeparatorStyle();
            return menu;
        }

        private ControlTemplate CreateGlassMenuTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ContextMenu));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
            surface.SetValue(Border.PaddingProperty, new Thickness(6));
            surface.SetValue(Border.BackgroundProperty,
                Brush(_isDark ? "#F21A1E25" : "#F7F7FAFE"));
            surface.SetValue(Border.BorderBrushProperty,
                Brush(_isDark ? "#30FFFFFF" : "#4FFFFFFF"));
            surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            FrameworkElementFactory items = new FrameworkElementFactory(typeof(StackPanel));
            items.SetValue(StackPanel.IsItemsHostProperty, true);
            surface.AppendChild(items);
            template.VisualTree = surface;
            return template;
        }

        private Style CreateGlassMenuItemStyle()
        {
            Style style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(MenuItem.HeightProperty, 34.0));
            style.Setters.Add(new Setter(MenuItem.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(MenuItem.ForegroundProperty,
                Brush(_isDark ? "#FFF3F5F8" : "#FF161B24")));

            ControlTemplate template = new ControlTemplate(typeof(MenuItem));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.Name = "ItemSurface";
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(10, 0, 16, 0));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(content);
            template.VisualTree = surface;
            Trigger highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlighted.Setters.Add(new Setter(Border.BackgroundProperty,
                Brush(_isDark ? "#2EFFFFFF" : "#18000000"), "ItemSurface"));
            template.Triggers.Add(highlighted);
            Trigger disabled = new Trigger { Property = MenuItem.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(MenuItem.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(MenuItem.TemplateProperty, template));
            return style;
        }

        private Style CreateGlassSeparatorStyle()
        {
            Style style = new Style(typeof(Separator));
            ControlTemplate template = new ControlTemplate(typeof(Separator));
            FrameworkElementFactory line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(Border.HeightProperty, 1.0);
            line.SetValue(Border.MarginProperty, new Thickness(10, 5, 10, 5));
            line.SetValue(Border.BackgroundProperty,
                Brush(_isDark ? "#20FFFFFF" : "#18000000"));
            template.VisualTree = line;
            style.Setters.Add(new Setter(Separator.TemplateProperty, template));
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
            _statusButton.Visibility = Visibility.Collapsed;
            _statusButton.IsHitTestVisible = false;
            _idleControls.Visibility = Visibility.Visible;
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
            ApplyTheme();
            if (!NativeMethods.RegisterHotKey(handle, HotkeyId, ModControl | ModAlt, VirtualKeySpace))
                SetStatus("Ctrl+Alt+Space 已被占用，可从托盘打开", "#FFB54708", false);
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

        private void ShowSearch()
        {
            PositionWindow();
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
            SetStatus("Edge 正在后台标签页加载…", "#FFB54708", false);
            try
            {
                if (!string.IsNullOrWhiteSpace(_lastRequestId))
                    await _bridge.CancelAsync(_lastRequestId);
                _lastRequestId = null;

                string url = SearchHelper.BuildSearchUrl(query, engine.Template);
                _lastRequestId = await _bridge.SearchAsync(url, 30);
                SetStatus("✓ 页面已加载，点击打开", "#FF067647", true);
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
            SetStatus("正在切换到搜索结果…", "#FF475467", false);
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

        private void SetStatus(string text, string color, bool clickable)
        {
            _idleControls.Visibility = Visibility.Collapsed;
            _statusButton.Visibility = Visibility.Visible;
            _statusButton.Content = text;
            _statusColor = color;
            _statusButton.Foreground = Brush(DarkStatusColor(color));
            _statusButton.IsHitTestVisible = clickable;
            _statusButton.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;
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
            _engineButton.Content = _selectedEngine.Name + "  ▾";
        }

        private void ReloadSettings()
        {
            _settings = SearchSettings.Load(_settings.FilePath);
            LoadEngines();
            ApplyTheme();
            SetStatus("配置已重新加载", "#FF067647", false);
        }

        private void EditAppearance()
        {
            Window dialog = new Window
            {
                Title = "外观设置",
                Owner = this,
                Width = 410,
                Height = 300,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                FontFamily = FontFamily,
                Background = Brush(_isDark ? "#FF1C2027" : "#FFF7F9FC"),
                Foreground = Brush(_isDark ? "#FFF5F7FA" : "#FF111827")
            };

            Grid root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            root.RowDefinitions.Add(new RowDefinition());

            TextBlock title = new TextBlock
            {
                Text = "Liquid Glass 外观",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold
            };
            root.Children.Add(title);

            Grid opacityRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition());
            opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            TextBlock opacityLabel = new TextBlock
            {
                Text = "背景不透明度",
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock opacityHint = new TextBlock
            {
                Text = "0% 仍保留环境折射",
                FontSize = 11,
                Opacity = 0.68,
                Margin = new Thickness(0, 3, 0, 0)
            };
            StackPanel opacityText = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            opacityText.Children.Add(opacityLabel);
            opacityText.Children.Add(opacityHint);
            Slider opacitySlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = _settings.BackgroundOpacity,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock opacityValue = new TextBlock
            {
                Text = _settings.BackgroundOpacity + "%",
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            opacitySlider.ValueChanged += delegate
            {
                opacityValue.Text = Math.Round(opacitySlider.Value) + "%";
            };
            Grid.SetColumn(opacitySlider, 1);
            Grid.SetColumn(opacityValue, 2);
            System.Windows.Automation.AutomationProperties.SetName(opacitySlider, "背景不透明度");
            opacityRow.Children.Add(opacityText);
            opacityRow.Children.Add(opacitySlider);
            opacityRow.Children.Add(opacityValue);
            Grid.SetRow(opacityRow, 1);
            root.Children.Add(opacityRow);

            Button darkColorButton = new Button { Width = 142, Height = 34 };
            SetColorButton(darkColorButton, _settings.DarkBackgroundColor);
            darkColorButton.Click += delegate { PickColor(darkColorButton, dialog); };
            root.Children.Add(AppearanceColorRow("深色模式背景", darkColorButton, 2));

            Button lightColorButton = new Button { Width = 142, Height = 34 };
            SetColorButton(lightColorButton, _settings.LightBackgroundColor);
            lightColorButton.Click += delegate { PickColor(lightColorButton, dialog); };
            root.Children.Add(AppearanceColorRow("浅色模式背景", lightColorButton, 3));

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Button cancel = new Button
            {
                Content = "取消",
                Width = 82,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true
            };
            Button save = new Button
            {
                Content = "保存",
                Width = 82,
                Height = 34,
                IsDefault = true
            };
            save.Click += delegate
            {
                try
                {
                    _settings.SaveAppearance((int)Math.Round(opacitySlider.Value),
                        (string)darkColorButton.Tag, (string)lightColorButton.Tag);
                    ApplyTheme();
                    dialog.DialogResult = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "无法保存外观设置：" + ex.Message, "快捷搜索",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            Grid.SetRow(actions, 4);
            root.Children.Add(actions);
            dialog.Content = root;

            _focusGuardCount++;
            try { dialog.ShowDialog(); }
            finally
            {
                _focusGuardCount = Math.Max(0, _focusGuardCount - 1);
                Dispatcher.BeginInvoke(new Action(HideIfInactive));
            }
        }

        private static Grid AppearanceColorRow(string label, Button button, int row)
        {
            Grid grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
            Grid.SetRow(grid, row);
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

        private void EditSettings()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("notepad.exe", "\"" + _settings.FilePath + "\"");
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                SetStatus("无法打开配置：" + ex.Message, "#FFD92D20", false);
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
            IntPtr handle = new WindowInteropHelper(this).Handle;
            NativeMethods.UnregisterHotKey(handle, HotkeyId);
            if (_source != null)
                _source.RemoveHook(WindowProcedure);
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
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
        private const int SwRestore = 9;

        private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

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
