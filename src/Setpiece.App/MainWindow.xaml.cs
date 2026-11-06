using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly TrayController _tray = new();
    private HwndSource? _source;
    private bool _exiting;
    private bool _trayHintShown;

    private static readonly string[] Templates =
    {
        "auto", "left-right", "left-right-top-bottom", "grid", "row", "column", "focus+side",
    };

    private static readonly string[] Adapters =
    {
        "auto", "vscode", "excel", "browser", "pdf", "image", "shell",
    };

    private sealed class EditItem
    {
        public string File = "";
        public string? Adapter;
        public double[]? Rect;
        public string? State;

        public override string ToString() =>
            $"{System.IO.Path.GetFileName(File)}   [{Adapter ?? "auto"}]";
    }

    private string? _root;
    private SetpieceConfig? _config;
    private bool _editing;
    private bool _syncing;
    private ObservableCollection<EditItem> _editItems = new();

    public MainWindow()
    {
        InitializeComponent();

        BrowseButton.Click += (_, _) => PickFolder();
        RefreshButton.Click += (_, _) => LoadProject(_root);
        SceneList.SelectionChanged += (_, _) => ShowScene();

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
        EditItems.SelectionChanged += (_, _) => SyncAdapterFromSelection();
        EditAdapter.SelectionChanged += (_, _) => ApplyAdapter();
        SaveEditButton.Click += (_, _) => SaveEdit();
        CancelEditButton.Click += (_, _) => ExitEdit();
        ResetLayoutButton.Click += (_, _) => ResetLayout();

        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        _tray.SceneRequested += index => _ = ApplySceneAsync(index);

        LoadProject(Directory.GetCurrentDirectory());
        StatusText.Text += $" · 完整性 {WindowScanner.IntegrityLevel()}";
    }

    // ---------------- 托盘与全局热键 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WndProc);

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
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            for (int id = 1; id <= 10; id++) UnregisterHotKey(_source.Handle, id);
        }

        _tray.Dispose();
        base.OnClosing(e);
    }

    // ---------------- 项目与场景 ----------------

    private void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择项目根目录" };
        if (dialog.ShowDialog(this) == true) LoadProject(dialog.FolderName);
    }

    private void LoadProject(string? root)
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
            if (_config.Scenes.Count > 0) SceneList.SelectedIndex = 0;
            _tray.RebuildMenu(_config.Scenes.Select(s => s.Name).ToList());
            StatusText.Text = $"已加载 {_config.Scenes.Count} 个场景";
        }
        catch (Exception ex)
        {
            _config = null;
            SceneList.ItemsSource = null;
            _tray.RebuildMenu(null);
            SceneTitle.Text = "没有可用场景";
            SceneMeta.Text = "";
            Log($"无法加载配置: {ex.Message}");
            StatusText.Text = "未找到 .setpiece/setpiece.json，可先用命令行 setpiece init 创建";
        }
    }

    private void ShowScene()
    {
        if (_editing) return;
        if (_config is null || SceneList.SelectedIndex < 0) return;

        Scene scene = _config.Scenes[SceneList.SelectedIndex];
        SceneTitle.Text = scene.Name;
        SceneMeta.Text = $"{scene.Items.Count} 个文件 · 模板 {scene.Template} · 显示器 {scene.Monitor}";
        LogBox.Text = string.Join(Environment.NewLine,
            scene.Items.Select(i => $"{i.File}   [{(i.Adapter ?? "auto")}]"));
    }

    // ---------------- 编辑器 ----------------

    private List<string> MonitorOptions() =>
        new[] { "primary" }.Concat(Native.Screens().Select(s => s.Index.ToString())).ToList();

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
        EditTemplate.ItemsSource = Templates;
        EditTemplate.SelectedItem = Templates.Contains(scene.Template) ? scene.Template : "auto";

        List<string> monitors = MonitorOptions();
        EditMonitor.ItemsSource = monitors;
        EditMonitor.SelectedItem = monitors.Contains(scene.Monitor) ? scene.Monitor : "primary";

        _editItems = new ObservableCollection<EditItem>(scene.Items.Select(i => new EditItem
        {
            File = i.File,
            Adapter = i.Adapter,
            Rect = i.Rect,
            State = i.State,
        }));
        EditItems.ItemsSource = _editItems;
        EditItems.SelectedIndex = _editItems.Count > 0 ? 0 : -1;

        EditAdapter.ItemsSource = Adapters;
        EditAdapter.SelectedItem = "auto";

        RunPanel.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;
        EditToggle.Content = "退出编辑";
        SceneMeta.Text = "布局由模板决定；手动摆好后回到运行页点“保存当前位置”";
    }

    private void ExitEdit()
    {
        _editing = false;
        RunPanel.Visibility = Visibility.Visible;
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

    private void SyncAdapterFromSelection()
    {
        if (_syncing) return;
        if (EditItems.SelectedItem is not EditItem item) return;

        _syncing = true;
        EditAdapter.SelectedItem = item.Adapter ?? "auto";
        _syncing = false;
    }

    private void ApplyAdapter()
    {
        if (_syncing) return;
        if (EditItems.SelectedItem is not EditItem item) return;

        string value = EditAdapter.SelectedItem as string ?? "auto";
        item.Adapter = value == "auto" ? null : value;
        EditItems.Items.Refresh();
    }

    private void ResetLayout()
    {
        foreach (EditItem item in _editItems)
        {
            item.Rect = null;
            item.State = null;
        }
        Log("已清除所有自定义位置，运行时将按模板分配");
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
        scene.Template = EditTemplate.SelectedItem as string ?? "auto";
        scene.Monitor = EditMonitor.SelectedItem as string ?? "primary";
        scene.Items = _editItems.Select(e => new SceneItem
        {
            File = e.File,
            Adapter = e.Adapter,
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
        var (code, output) = await Task.Run(() => ExecuteOnSta(() =>
        {
            Runtime.Configure(config);
            return action switch
            {
                "apply" => SceneRunner.Apply(root, config, scene.Name, null, false, 25000),
                "capture" => SceneRunner.Capture(root, config, scene.Name),
                "close" => SceneRunner.Close(root, config, scene.Name),
                _ => 2,
            };
        }));

        Log($"> {action} {scene.Name}");
        Log(output.TrimEnd());
        StatusText.Text = code == 0
            ? $"{action} 完成"
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
        if (busy) StatusText.Text = "运行中…";
    }

    private void Log(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
