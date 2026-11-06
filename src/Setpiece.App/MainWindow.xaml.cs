using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Setpiece;

namespace Setpiece.App;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkKeyW = 0x57;

    private const string GitHubUrl = "https://github.com/welt-cjy/setpiece";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly TrayController _tray = new();
    private readonly AppSettings _settings = SettingsStore.Load();
    private HwndSource? _source;
    private bool _exiting;
    private bool _trayHintShown;

    private static readonly Choice[] TemplateChoices =
    {
        new("auto", "自动"),
        new("left-right", "左右分屏"),
        new("left-right-top-bottom", "左大右上下"),
        new("grid", "网格"),
        new("row", "横向排列"),
        new("column", "纵向排列"),
        new("focus+side", "主窗口 + 侧栏"),
        new("cross-monitor", "跨屏轮转"),
    };

    private static readonly Choice[] AdapterChoices =
    {
        new("auto", "自动（推荐）"),
        new("vscode", "VS Code"),
        new("excel", "Excel"),
        new("browser", "浏览器"),
        new("pdf", "PDF 阅读器"),
        new("image", "图片查看器"),
        new("shell", "系统默认程序"),
    };

    private const string InheritMonitor = "(继承场景)";

    private sealed record Choice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class SceneFileRow
    {
        public string File { get; init; } = "";
        public string Adapter { get; init; } = "";
        public string Monitor { get; init; } = "";
        public string AutoOpen { get; init; } = "";
    }

    private sealed class EditItem
    {
        public string File = "";
        public string? Adapter;
        public string? Monitor;
        public bool? AutoOpen;
        public double[]? Rect;
        public string? State;

        public override string ToString() =>
            $"{System.IO.Path.GetFileName(File)}   [{AdapterLabel(Adapter)}]" +
            (Monitor is null ? "" : $"  @{MonitorLabel(Monitor)}") +
            (AutoOpen == false ? "  [未启用]" : "");
    }

    private string? _root;
    private SetpieceConfig? _config;
    private string? _pendingRoot;
    private string? _promptedRoot;
    private bool _editing;
    private bool _syncing;
    private int _progressFailures;
    private ObservableCollection<EditItem> _editItems = new();

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();

        BrowseButton.Click += (_, _) => PickFolder();
        RefreshButton.Click += (_, _) => LoadProject(_root);
        NewWorkspaceButton.Click += (_, _) => NewSceneInCurrentProject();
        NewSceneButton.Click += (_, _) => NewSceneInCurrentProject();
        CaptureWindowsButton.Click += async (_, _) => await CaptureFromWindowsAsync();
        DeleteSceneButton.Click += (_, _) => DeleteScene();
        SceneList.SelectionChanged += (_, _) => ShowScene();
        SceneList.MouseDoubleClick += async (_, _) =>
        {
            if (!_editing && SceneList.SelectedIndex >= 0) await RunAsync("apply");
        };

        ApplyButton.Click += async (_, _) => await RunAsync("apply");
        CaptureButton.Click += async (_, _) => await RunAsync("capture");
        CloseButton.Click += async (_, _) => await RunAsync("close");

        EditToggle.Click += (_, _) =>
        {
            if (_editing) ExitEdit();
            else StartEdit();
        };
        AddFileButton.Click += (_, _) => AddFiles();
        RemoveItemButton.Click += (_, _) => RemoveItem();
        MoveUpButton.Click += (_, _) => MoveItem(-1);
        MoveDownButton.Click += (_, _) => MoveItem(1);
        EditItems.SelectionChanged += (_, _) => SyncFromSelection();
        EditAdapter.SelectionChanged += (_, _) => ApplyItemOptions();
        EditItemMonitor.SelectionChanged += (_, _) => ApplyItemOptions();
        EditAutoOpen.Click += (_, _) => ApplyItemOptions();
        SaveEditButton.Click += (_, _) => SaveEdit();
        CancelEditButton.Click += (_, _) => ExitEdit();
        ResetLayoutButton.Click += (_, _) => ResetLayout();

        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        _tray.SceneRequested += index => _ = ApplySceneAsync(index);

        MenuCloseToTray.IsChecked = _settings.CloseToTray;

        string? last = App.StartupProject ?? RecentProjects.Load().FirstOrDefault(Directory.Exists);
        if (last is not null)
        {
            LoadProject(last, promptIfMissing: false);
        }
        else
        {
            string cwd = Directory.GetCurrentDirectory();
            if (ConfigStore.Exists(cwd)) LoadProject(cwd, promptIfMissing: false);
        }

        UpdateWelcome();
    }

    // ---------------- 菜单与快捷键 ----------------

    private void MenuOpenProject_Click(object sender, RoutedEventArgs e) => PickFolder();
    private void MenuNewWorkspace_Click(object sender, RoutedEventArgs e) => PickWorkspaceFolder();
    private void MenuNewScene_Click(object sender, RoutedEventArgs e) => NewSceneInCurrentProject();
    private void MenuExit_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void MenuApplyScene_Click(object sender, RoutedEventArgs e) => _ = RunAsync("apply");
    private void MenuCapture_Click(object sender, RoutedEventArgs e) => _ = RunAsync("capture");
    private void MenuCloseScene_Click(object sender, RoutedEventArgs e) => _ = RunAsync("close");

    private void MenuEditScene_Click(object sender, RoutedEventArgs e)
    {
        if (_editing) ExitEdit();
        else StartEdit();
    }

    private void MenuCaptureWindows_Click(object sender, RoutedEventArgs e) =>
        _ = CaptureFromWindowsAsync();

    private void MenuDeleteScene_Click(object sender, RoutedEventArgs e) => DeleteScene();

    private void MenuOpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_root is null)
        {
            Log("先选择项目");
            return;
        }
        OpenPath(_root);
    }

    private void MenuOpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_root is null)
        {
            Log("先选择项目");
            return;
        }

        string dir = Path.Combine(_root, ConfigStore.DirName);
        if (!Directory.Exists(dir))
        {
            Log("这个项目还没有配置文件");
            return;
        }
        OpenPath(dir);
    }

    private void MenuCloseToTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = MenuCloseToTray.IsChecked;
        _settings.ClosePromptShown = true;
        SettingsStore.Save(_settings);
        Log(MenuCloseToTray.IsChecked ? "关闭窗口时将最小化到托盘" : "关闭窗口时将退出程序");
    }

    private void MenuEnvironmentCheck_Click(object sender, RoutedEventArgs e)
    {
        var report = new StringBuilder();
        report.AppendLine($"完整性级别: {WindowScanner.IntegrityLevel()}");
        report.AppendLine($"配置文件: {(_root is null ? "（未选择项目）" : ConfigStore.ConfigPath(_root))}");
        report.AppendLine();
        foreach (MonitorInfo screen in Native.Screens())
            report.AppendLine($"{screen.DeviceKey}{(screen.Primary ? "（主屏）" : "")} 工作区 {screen.WorkArea}");

        string text = report.ToString().TrimEnd();
        Log(text);
        MessageBox.Show(this, text, "环境检查", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuReadme_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl + "#readme");
    private void MenuGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0";
        MessageBox.Show(
            this,
            $"Setpiece {version}\n\n" +
            "按项目保存工作现场：文件、窗口位置和显示器布局，一键恢复。\n\n" +
            GitHubUrl,
            "关于 Setpiece",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void WelcomeOpen_Click(object sender, RoutedEventArgs e) => PickFolder();
    private void WelcomeNew_Click(object sender, RoutedEventArgs e) => PickWorkspaceFolder();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.O) PickFolder();
        else if (ctrl && e.Key == Key.N) PickWorkspaceFolder();
        else if (ctrl && e.Key == Key.S)
        {
            if (_editing) SaveEdit();
            else _ = RunAsync("capture");
        }
        else if (ctrl && e.Key == Key.E)
        {
            if (_editing) ExitEdit();
            else StartEdit();
        }
        else if (e.Key == Key.F5) _ = RunAsync("apply");
        else
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
    }

    // ---------------- 托盘与全局热键 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WndProc);
        WindowScanner.UseDarkTitleBar(_source.Handle);

        RegisterHotKey(_source.Handle, 1, ModControl | ModAlt | ModNoRepeat, VkKeyW);
        for (uint i = 0; i < 9; i++)
            RegisterHotKey(_source.Handle, 2 + (int)i, ModControl | ModAlt | ModNoRepeat, 0x31 + i);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (id == 1) ToggleWindow();
        else if (id is >= 2 and <= 10) _ = ApplySceneAsync(id - 2);

        handled = true;
        return IntPtr.Zero;
    }

    private void ToggleWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void HideToTray()
    {
        Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray.Notify("已最小化到托盘。Ctrl+Alt+W 唤起，Ctrl+Alt+1~9 打开场景");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            if (!_settings.ClosePromptShown)
            {
                MessageBoxResult choice = MessageBox.Show(
                    this,
                    "关闭窗口时要最小化到托盘继续运行吗？\n\n" +
                    "是：最小化到托盘，全局热键继续可用\n" +
                    "否：退出 Setpiece\n" +
                    "取消：不关闭",
                    "Setpiece",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (choice == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                _settings.CloseToTray = choice == MessageBoxResult.Yes;
                _settings.ClosePromptShown = true;
                SettingsStore.Save(_settings);
                MenuCloseToTray.IsChecked = _settings.CloseToTray;
            }

            if (_settings.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
        }

        SaveWindowPlacement();

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            for (int id = 1; id <= 10; id++) UnregisterHotKey(_source.Handle, id);
        }

        _tray.Dispose();
        base.OnClosing(e);
    }

    private void RestoreWindowPlacement()
    {
        if (_settings.WindowWidth is >= 860 and <= 6000) Width = _settings.WindowWidth.Value;
        if (_settings.WindowHeight is >= 620 and <= 4000) Height = _settings.WindowHeight.Value;

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top)
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;

            // 上次的位置可能来自已经拔掉的显示器；只恢复标题栏仍可见的坐标。
            if (left + 200 > vl && left < vl + vw - 200 &&
                top + 80 > vt && top < vt + vh - 120)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        System.Windows.Rect bounds = WindowState == WindowState.Normal
            ? new System.Windows.Rect(Left, Top, Width, Height)
            : RestoreBounds;

        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        SettingsStore.Save(_settings);
    }

    // ---------------- 项目与场景 ----------------

    private void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择项目根目录" };
        if (dialog.ShowDialog(this) == true) LoadProject(dialog.FolderName);
    }

    private void PickWorkspaceFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择项目根目录" };
        if (dialog.ShowDialog(this) != true) return;

        LoadProject(dialog.FolderName, promptIfMissing: false);
        PromptWorkspace(dialog.FolderName, force: true);
    }

    private void NewSceneInCurrentProject()
    {
        if (_root is null || _config is null)
        {
            PickFolder();
            return;
        }
        PromptWorkspace(_root, force: true);
    }

    private void LoadProject(string? root, bool promptIfMissing = true)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        _root = root;
        ProjectBox.Text = root;
        LogBox.Clear();

        try
        {
            _config = ConfigStore.Load(root);
            SceneList.ItemsSource = _config.Scenes
                .Select(s => $"{s.Name}   ({s.Items.Count} 个文件)")
                .ToList();
            if (_config.Scenes.Count > 0)
            {
                SceneList.SelectedIndex = 0;
            }
            else
            {
                SceneTitle.Text = "还没有场景";
                SceneMeta.Text = "点左侧「新建」创建第一个场景，或从当前窗口生成";
            }

            _tray.RebuildMenu(_config.Scenes.Select(s => s.Name).ToList());
            RecentProjects.Add(root);
            _promptedRoot = null;
            _pendingRoot = null;
            Title = $"Setpiece — {DisplayName(root)}";
            SceneHeader.Text = $"场景（{_config.Scenes.Count}）";
            if (_config.Scenes.Count == 0) SceneFiles.ItemsSource = null;
            RefreshRecentMenus();
            UpdateWelcome();
            StatusText.Text = $"已加载 {_config.Scenes.Count} 个场景 · 完整性 {WindowScanner.IntegrityLevel()}";
        }
        catch (Exception ex)
        {
            _config = null;
            SceneList.ItemsSource = null;
            SceneFiles.ItemsSource = null;
            _tray.RebuildMenu(null);
            Title = "Setpiece";
            SceneHeader.Text = "场景";
            SceneTitle.Text = "没有可用场景";
            SceneMeta.Text = "";
            Log($"无法加载配置: {ex.Message}");
            StatusText.Text = "这个项目还没有 Setpiece 配置";
            if (promptIfMissing && IsVisible)
            {
                _pendingRoot = root;
                PromptWorkspace(root);
            }
            UpdateWelcome();
        }
    }

    private void UpdateWelcome()
    {
        bool hasProject = _config is not null;
        WelcomePanel.Visibility = hasProject ? Visibility.Collapsed : Visibility.Visible;
        MainContent.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;
        ProjectBar.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;

        if (!hasProject)
        {
            Title = "Setpiece";
            SceneHeader.Text = "场景";
            SceneFiles.ItemsSource = null;
            RefreshRecentMenus();
            StatusText.Text = $"未打开项目 · 完整性 {WindowScanner.IntegrityLevel()}";
        }
    }

    private void RefreshRecentMenus()
    {
        List<string> items = RecentProjects.Load().Where(Directory.Exists).ToList();

        MenuRecent.Items.Clear();
        if (items.Count == 0)
        {
            MenuRecent.Items.Add(new MenuItem { Header = "（暂无）", IsEnabled = false });
        }
        else
        {
            foreach (string path in items)
            {
                var item = new MenuItem
                {
                    Header = DisplayName(path),
                    ToolTip = path,
                    Tag = path,
                };
                item.Click += (_, _) => LoadProject(path);
                MenuRecent.Items.Add(item);
            }
        }

        RecentList.Children.Clear();
        if (items.Count == 0)
        {
            RecentList.Children.Add(new TextBlock
            {
                Text = "还没有打开过项目",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x6F, 0x79)),
                Margin = new Thickness(10, 8, 0, 8),
                FontSize = 12,
            });
            return;
        }

        var style = (Style)FindResource("RecentItem");
        foreach (string path in items)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = DisplayName(path),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEA)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = path,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x94)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var button = new Button
            {
                Content = text,
                Style = style,
                Tag = path,
                ToolTip = path,
            };
            button.Click += (_, _) => LoadProject(path);
            RecentList.Children.Add(button);
        }
    }

    private static string DisplayName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_pendingRoot is null) return;

        string root = _pendingRoot;
        _pendingRoot = null;
        PromptWorkspace(root);
    }

    private void PromptWorkspace(string? root, bool force = false)
    {
        if (root is null || !Directory.Exists(root)) return;
        if (!force && _config is not null) return;
        if (!force && string.Equals(_promptedRoot, root, StringComparison.OrdinalIgnoreCase)) return;

        if (!IsVisible)
        {
            _pendingRoot = root;
            return;
        }

        _promptedRoot = root;
        _pendingRoot = null;

        var dialog = new NewWorkspaceWindow(root, _config) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        LoadProject(root);
        int index = _config?.Scenes.FindIndex(s =>
            string.Equals(s.Name, dialog.CreatedSceneName, StringComparison.OrdinalIgnoreCase)) ?? -1;
        if (index >= 0) SceneList.SelectedIndex = index;
        StartEdit();
    }

    private async Task CaptureFromWindowsAsync()
    {
        if (_root is null)
        {
            Log("先选择项目");
            return;
        }

        string root = _root;
        SetpieceConfig config = _config ?? new SetpieceConfig();

        SetBusy(true);
        CaptureResult result = await Task.Run(() => WindowSceneBuilder.Capture(config, root));

        if (result.Windows.Count == 0)
        {
            Log($"没有从窗口里识别到项目文件（{result.Unmatched} 个窗口未匹配）");
            SetBusy(false);
            return;
        }

        string name = UniqueSceneName(config, "窗口场景");
        config.Scenes.Add(new Scene
        {
            Name = name,
            Template = "auto",
            Monitor = "primary",
            Items = result.Windows.Select(w => new SceneItem
            {
                File = w.RelativePath,
                Adapter = w.Adapter.Length > 0 ? w.Adapter : null,
                Monitor = w.Monitor,
                Rect = w.Rect,
                State = w.State,
            }).ToList(),
        });

        ConfigStore.Save(root, config);
        string extra = result.Unmatched > 0 ? $"，另有 {result.Unmatched} 个窗口未匹配" : "";

        LoadProject(root);
        int index = _config?.Scenes.FindIndex(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? -1;
        if (index >= 0) SceneList.SelectedIndex = index;

        SetBusy(false);
        StartEdit();
        Log($"已从 {result.Windows.Count} 个窗口生成场景「{name}」{extra}");
    }

    private static string UniqueSceneName(SetpieceConfig config, string baseName)
    {
        string name = baseName;
        int i = 2;
        while (config.Scenes.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {i++}";
        return name;
    }

    private void DeleteScene()
    {
        if (_config is null || _root is null || SceneList.SelectedIndex < 0)
        {
            Log("先选择一个场景");
            return;
        }

        Scene scene = _config.Scenes[SceneList.SelectedIndex];
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"删除场景「{scene.Name}」？\n文件本身不会被删除。",
            "Setpiece",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ExitEdit();
        _config.Scenes.Remove(scene);
        ConfigStore.Save(_root, _config);
        Log($"已删除场景 {scene.Name}");
        LoadProject(_root);
    }

    private void ShowScene()
    {
        if (_editing) return;
        ProgressPanel.Visibility = Visibility.Collapsed;
        if (_config is null || SceneList.SelectedIndex < 0) return;

        Scene scene = _config.Scenes[SceneList.SelectedIndex];
        SceneTitle.Text = scene.Name;
        int disabled = scene.Items.Count(i => i.AutoOpen == false);
        SceneMeta.Text = $"{scene.Items.Count} 个文件 · 模板 {TemplateLabel(scene.Template)} · " +
                         $"显示器 {MonitorLabel(scene.Monitor)}" +
                         (disabled > 0 ? $" · {disabled} 个未启用" : "");
        SceneFiles.ItemsSource = scene.Items.Select(i => new SceneFileRow
        {
            File = i.File,
            Adapter = AdapterLabel(i.Adapter),
            Monitor = i.Monitor is null ? "跟随场景" : MonitorLabel(i.Monitor),
            AutoOpen = i.AutoOpen == false ? "否" : "是",
        }).ToList();
    }

    private static string TemplateLabel(string? key) =>
        FindChoice(TemplateChoices, key)?.Label ?? key ?? "自动";

    private static string AdapterLabel(string? key) =>
        string.IsNullOrEmpty(key) ? "自动" : FindChoice(AdapterChoices, key)?.Label ?? key;

    private static string MonitorLabel(string? key)
    {
        if (string.IsNullOrEmpty(key) || key == "primary") return "主显示器";
        return key;
    }

    private static Choice? FindChoice(IEnumerable<Choice> choices, string? key) =>
        choices.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    // ---------------- 编辑器 ----------------

    private List<Choice> MonitorChoices()
    {
        var list = new List<Choice> { new("primary", "主显示器") };
        foreach (MonitorInfo screen in Native.Screens())
            list.Add(new Choice(screen.DeviceKey,
                screen.Primary ? $"{screen.DeviceKey}（主屏）" : screen.DeviceKey));
        return list;
    }

    private List<Choice> ItemMonitorChoices()
    {
        var list = new List<Choice> { new("", InheritMonitor) };
        list.AddRange(MonitorChoices());
        return list;
    }

    private void StartEdit()
    {
        if (_config is null || _root is null || SceneList.SelectedIndex < 0)
        {
            Log("先选择一个场景");
            return;
        }

        Scene scene = _config.Scenes[SceneList.SelectedIndex];
        _editing = true;

        EditName.Text = scene.Name;

        List<Choice> templates = TemplateChoices.ToList();
        EditTemplate.ItemsSource = templates;
        EditTemplate.SelectedItem = FindChoice(templates, scene.Template) ?? templates[0];

        List<Choice> monitors = MonitorChoices();
        EditMonitor.ItemsSource = monitors;
        EditMonitor.SelectedItem = FindChoice(monitors, scene.Monitor) ?? monitors[0];

        _editItems = new ObservableCollection<EditItem>(scene.Items.Select(i => new EditItem
        {
            File = i.File,
            Adapter = i.Adapter,
            Monitor = i.Monitor,
            AutoOpen = i.AutoOpen,
            Rect = i.Rect,
            State = i.State,
        }));
        EditItems.ItemsSource = _editItems;
        EditItems.SelectedIndex = _editItems.Count > 0 ? 0 : -1;

        List<Choice> adapters = AdapterChoices.ToList();
        EditAdapter.ItemsSource = adapters;
        EditAdapter.SelectedItem = adapters[0];

        List<Choice> itemMonitors = ItemMonitorChoices();
        EditItemMonitor.ItemsSource = itemMonitors;
        EditItemMonitor.SelectedItem = itemMonitors[0];

        RunPanel.Visibility = Visibility.Collapsed;
        SceneFilesPanel.Visibility = Visibility.Collapsed;
        LogBox.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;
        EditToggle.Content = "退出编辑";
        SceneMeta.Text = "布局由模板决定；手动摆好后回到运行页点“保存当前位置”";
    }

    private void ExitEdit()
    {
        _editing = false;
        RunPanel.Visibility = Visibility.Visible;
        SceneFilesPanel.Visibility = Visibility.Visible;
        LogBox.Visibility = Visibility.Visible;
        EditPanel.Visibility = Visibility.Collapsed;
        EditToggle.Content = "编辑场景";
        ShowScene();
    }

    private void AddFiles()
    {
        if (_root is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要加入场景的文件",
            Filter = "所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (string file in dialog.FileNames)
            _editItems.Add(new EditItem { File = Path.GetRelativePath(_root, file) });

        EditItems.SelectedIndex = _editItems.Count - 1;
    }

    private void RemoveItem()
    {
        if (EditItems.SelectedIndex < 0) return;
        _editItems.RemoveAt(EditItems.SelectedIndex);
    }

    private void MoveItem(int delta)
    {
        int index = EditItems.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _editItems.Count) return;

        EditItem item = _editItems[index];
        _editItems.RemoveAt(index);
        _editItems.Insert(target, item);
        EditItems.SelectedIndex = target;
    }

    private void SyncFromSelection()
    {
        if (_syncing) return;
        if (EditItems.SelectedItem is not EditItem item) return;

        _syncing = true;
        EditAdapter.SelectedItem = FindChoice(AdapterChoices, item.Adapter) ?? AdapterChoices[0];

        var monitors = EditItemMonitor.ItemsSource as List<Choice> ?? ItemMonitorChoices();
        EditItemMonitor.SelectedItem = FindChoice(monitors, item.Monitor) ?? monitors[0];
        EditAutoOpen.IsChecked = item.AutoOpen != false;
        _syncing = false;
    }

    private void ApplyItemOptions()
    {
        if (_syncing) return;
        if (EditItems.SelectedItem is not EditItem item) return;

        string? adapter = (EditAdapter.SelectedItem as Choice)?.Key;
        item.Adapter = string.IsNullOrEmpty(adapter) || adapter == "auto" ? null : adapter;

        string? monitor = (EditItemMonitor.SelectedItem as Choice)?.Key;
        item.Monitor = string.IsNullOrEmpty(monitor) ? null : monitor;

        item.AutoOpen = EditAutoOpen.IsChecked == true ? null : false;

        EditItems.Items.Refresh();
    }

    private void ResetLayout()
    {
        foreach (EditItem item in _editItems)
        {
            item.Rect = null;
            item.State = null;
            item.Monitor = null;
        }
        Log("已清除所有自定义位置和显示器，运行时将按模板分配");
    }

    private void SaveEdit()
    {
        if (!_editing || _config is null || _root is null || SceneList.SelectedIndex < 0) return;

        string name = EditName.Text.Trim();
        if (name.Length == 0)
        {
            Log("场景名不能为空");
            return;
        }

        if (_config.Scenes.Any(s => !ReferenceEquals(s, _config.Scenes[SceneList.SelectedIndex]) &&
                                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"已存在同名场景: {name}");
            return;
        }

        Scene scene = _config.Scenes[SceneList.SelectedIndex];
        scene.Name = name;
        scene.Template = (EditTemplate.SelectedItem as Choice)?.Key ?? "auto";
        scene.Monitor = (EditMonitor.SelectedItem as Choice)?.Key ?? "primary";
        scene.Items = _editItems.Select(e => new SceneItem
        {
            File = e.File,
            Adapter = e.Adapter,
            Monitor = e.Monitor,
            AutoOpen = e.AutoOpen,
            Rect = e.Rect,
            State = e.State,
        }).ToList();

        ConfigStore.Save(_root, _config);
        Log($"已保存场景 {name}（{scene.Items.Count} 个文件）");

        ExitEdit();
        LoadProject(_root);
        int index = _config.Scenes.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) SceneList.SelectedIndex = index;
    }

    // ---------------- 运行 ----------------

    private Task RunAsync(string action)
    {
        if (SceneList.SelectedIndex < 0)
        {
            Log("先选择一个场景");
            return Task.CompletedTask;
        }

        return RunSceneAsync(action, SceneList.SelectedIndex);
    }

    private async Task ApplySceneAsync(int index)
    {
        if (_config is null || _root is null || index < 0 || index >= _config.Scenes.Count)
        {
            _tray.Notify("请先在窗口里选择项目");
            return;
        }

        await RunSceneAsync("apply", index);
    }

    private async Task RunSceneAsync(string action, int index)
    {
        if (_config is null || _root is null)
        {
            Log("先选择项目");
            return;
        }

        if (index < 0 || index >= _config.Scenes.Count)
        {
            Log("无效的场景");
            return;
        }

        Scene scene = _config.Scenes[index];
        string root = _root;
        SetpieceConfig config = _config;
        bool wasHidden = !IsVisible;

        SetBusy(true);
        if (action == "apply") ShowProgress();
        else ProgressPanel.Visibility = Visibility.Collapsed;

        int disabled = 0;
        var (code, output) = await Task.Run(() => ExecuteOnSta(() =>
        {
            Runtime.Configure(config);
            return action switch
            {
                "apply" => SceneRunner.Apply(root, config, scene.Name, null, false, 25000, p =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(() => UpdateProgress(p)));
                }),
                "capture" => SceneRunner.Capture(root, config, scene.Name, out disabled),
                "close" => SceneRunner.Close(root, config, scene.Name),
                _ => 2,
            };
        }));

        Log($"> {action} {scene.Name}");
        Log(output.TrimEnd());
        StatusText.Text = code == 0
            ? $"{action} 完成" + (action == "capture" && disabled > 0 ? $"（{disabled} 个未打开已停用）" : "")
            : $"{action} 有未完成的项（退出码 {code}）";
        SetBusy(false);

        if (wasHidden)
        {
            _tray.Notify(code == 0
                ? $"{scene.Name}：已打开"
                : $"{scene.Name}：有未完成的项，打开窗口查看详情");
        }
    }

    // Excel 的 COM 调用要求 STA；放到单独的 STA 线程上跑，避免卡住 UI 线程。
    private static (int Code, string Output) ExecuteOnSta(Func<int> action)
    {
        int code = -1;
        var buffer = new StringWriter();
        var thread = new Thread(() =>
        {
            TextWriter oldOut = Console.Out;
            TextWriter oldErr = Console.Error;
            Console.SetOut(buffer);
            Console.SetError(buffer);
            try
            {
                code = action();
            }
            catch (Exception ex)
            {
                buffer.WriteLine($"错误: {ex.Message}");
                code = 2;
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return (code, buffer.ToString());
    }

    private void SetBusy(bool busy)
    {
        ApplyButton.IsEnabled = !busy;
        CaptureButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        EditToggle.IsEnabled = !busy;
        SceneList.IsEnabled = !busy;
        NewSceneButton.IsEnabled = !busy;
        CaptureWindowsButton.IsEnabled = !busy;
        DeleteSceneButton.IsEnabled = !busy;
        if (busy) StatusText.Text = "运行中…";
    }

    private void ShowProgress()
    {
        _progressFailures = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        RunProgress.Maximum = 1;
        RunProgress.Value = 0;
        ProgressText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3));
        ProgressText.Text = "正在准备打开…";
    }

    private void UpdateProgress(ApplyProgress p)
    {
        if (p.Phase == "跳过") return;

        RunProgress.Maximum = Math.Max(1, p.Total);
        RunProgress.Value = Math.Min(p.Completed, p.Total);

        if (p.Ok == false)
        {
            _progressFailures++;
            Log($"{p.File} 打开失败：{p.Note ?? "未知原因"}");
        }

        if (p.Phase == "结束")
        {
            RunProgress.Value = p.Total;
            int okCount = Math.Max(0, p.Total - _progressFailures);
            ProgressText.Foreground = _progressFailures == 0
                ? new SolidColorBrush(Color.FromRgb(0x7A, 0xC7, 0x7A))
                : new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x50));
            ProgressText.Text = p.Total == 0
                ? "没有需要打开的文件（都已禁用）"
                : _progressFailures == 0
                    ? $"已打开 {okCount} 个文件"
                    : $"完成：{okCount} 个成功，{_progressFailures} 个失败（详见下方日志）";
            return;
        }

        ProgressText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3));
        string name = p.File.Length > 0 ? Path.GetFileName(p.File) : "";
        string detail = p.Note is { Length: > 0 } ? $" — {p.Note}" : "";

        ProgressText.Text = p.Phase switch
        {
            "准备" => $"正在准备打开 {p.Total} 个文件…",
            "等待窗口" => $"正在等待窗口（{Math.Min(p.Completed + 1, p.Total)}/{p.Total}）：{name}{detail}",
            "完成" => $"已打开 {p.Completed}/{p.Total} · {name}",
            "校正" => "正在校正窗口位置…",
            _ => $"正在打开 {p.Completed}/{p.Total} · {p.Phase}：{name}{detail}",
        };
    }

    private void Log(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 没有默认浏览器时静默失败，不打断主流程。
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 路径打不开时静默失败，日志由调用方负责。
        }
    }
}
