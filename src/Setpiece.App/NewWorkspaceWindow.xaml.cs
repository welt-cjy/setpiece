using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Setpiece.App;

public partial class NewWorkspaceWindow : Window
{
    public sealed class WorkspaceNode : INotifyPropertyChanged
    {
        private bool _expanded;
        private bool _checked;

        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsFolder { get; set; }
        public bool IsLoaded { get; set; }
        public bool Suppress { get; set; }
        public WorkspaceNode? Parent { get; set; }
        public ObservableCollection<WorkspaceNode> Children { get; } = new();

        public Action<WorkspaceNode>? OnExpand;
        public Action<WorkspaceNode, bool>? OnChecked;

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                if (value) OnExpand?.Invoke(this);
            }
        }

        public bool IsChecked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                OnPropertyChanged(nameof(IsChecked));
                if (!Suppress) OnChecked?.Invoke(this, value);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly string _root;
    private readonly SetpieceConfig? _existing;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _folderFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _filterTimer;
    private WorkspaceNode? _rootNode;

    public string CreatedSceneName { get; private set; } = "";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            WindowScanner.UseDarkTitleBar(source.Handle);
    }

    internal NewWorkspaceWindow(string root, SetpieceConfig? existing = null)
    {
        InitializeComponent();
        _root = root;
        _existing = existing;
        PathText.Text = root;
        SceneNameBox.Text = new DirectoryInfo(root).Name;

        if (existing is not null)
        {
            Title = "新建场景";
            HeaderText.Text = "新建场景";
        }

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyFilter();
        };

        FilterBox.TextChanged += (_, _) =>
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        };
        SelectAllButton.Click += (_, _) => SelectAll();
        ClearAllButton.Click += (_, _) => ClearAll();
        CreateButton.Click += (_, _) => CreateWorkspace();
        CancelButton.Click += (_, _) => DialogResult = false;

        BuildTree();
    }

    private void BuildTree()
    {
        _rootNode = CreateNode(Path.GetFileName(_root.TrimEnd('\\', '/')), "", _root, true, null);
        LoadChildren(_rootNode);
        FileTree.ItemsSource = _rootNode.Children;
        UpdateStatus();
    }

    private WorkspaceNode CreateNode(string name, string relative, string full, bool folder,
        WorkspaceNode? parent)
    {
        var node = new WorkspaceNode
        {
            Name = name,
            RelativePath = relative,
            FullPath = full,
            IsFolder = folder,
            Parent = parent,
        };
        node.OnExpand = LoadChildren;
        node.OnChecked = OnNodeChecked;
        return node;
    }

    private void LoadChildren(WorkspaceNode node)
    {
        if (!node.IsFolder || node.IsLoaded) return;

        var folders = new List<WorkspaceNode>();
        var files = new List<WorkspaceNode>();

        try
        {
            var dir = new DirectoryInfo(node.FullPath);

            foreach (DirectoryInfo sub in dir.GetDirectories()
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (sub.Name.StartsWith('.')) continue;
                if (ProjectScanner.IsExcludedDir(sub.Name)) continue;

                folders.Add(CreateNode(sub.Name, Combine(node.RelativePath, sub.Name),
                    sub.FullName, true, node));
            }

            foreach (FileInfo file in dir.GetFiles()
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!ProjectScanner.IsSupportedExtension(file.Extension)) continue;

                string relative = Combine(node.RelativePath, file.Name);
                WorkspaceNode child = CreateNode(file.Name, relative, file.FullName, false, node);
                child.Suppress = true;
                child.IsChecked = _selected.Contains(relative);
                child.Suppress = false;
                files.Add(child);
            }
        }
        catch
        {
            // 权限不足等，跳过该目录
        }

        foreach (WorkspaceNode child in folders.Concat(files)) node.Children.Add(child);
        node.IsLoaded = true;

        // 同时刷新子节点的勾选状态：全选后未展开的文件夹也要显示为已选中。
        RefreshSubtree(node);
    }

    private static string Combine(string prefix, string name) =>
        prefix.Length == 0 ? name : prefix + "/" + name;

    private bool ComputeState(WorkspaceNode folder)
    {
        if (folder.IsLoaded)
        {
            int total = 0, selected = 0;
            foreach (WorkspaceNode child in folder.Children)
            {
                if (child.IsFolder)
                {
                    total++;
                    if (ComputeState(child)) selected++;
                }
                else
                {
                    total++;
                    if (_selected.Contains(child.RelativePath)) selected++;
                }
            }

            return total > 0 && selected == total;
        }

        // 未展开的文件夹没有子节点，用实际文件列表来判断，避免全选后不亮。
        List<string> files = FilesIn(folder);
        if (files.Count == 0) return false;

        foreach (string file in files)
        {
            if (!_selected.Contains(file)) return false;
        }
        return true;
    }

    private List<string> FilesIn(WorkspaceNode folder)
    {
        if (_folderFiles.TryGetValue(folder.FullPath, out List<string>? cached)) return cached;

        List<string> files = ProjectScanner.FilesUnder(_root, folder.FullPath);
        _folderFiles[folder.FullPath] = files;
        return files;
    }

    private void OnNodeChecked(WorkspaceNode node, bool value)
    {
        bool select = value;

        if (node.IsFolder)
        {
            foreach (string file in FilesIn(node))
            {
                if (select) _selected.Add(file);
                else _selected.Remove(file);
            }
            RefreshSubtree(node);
        }
        else
        {
            if (select) _selected.Add(node.RelativePath);
            else _selected.Remove(node.RelativePath);
        }

        RefreshAncestors(node.Parent);
        UpdateStatus();
    }

    private void RefreshSubtree(WorkspaceNode node)
    {
        foreach (WorkspaceNode child in node.Children)
        {
            child.Suppress = true;
            if (child.IsFolder)
            {
                if (child.IsLoaded) RefreshSubtree(child);
                child.IsChecked = ComputeState(child);
            }
            else
            {
                child.IsChecked = _selected.Contains(child.RelativePath);
            }
            child.Suppress = false;
        }

        node.Suppress = true;
        node.IsChecked = ComputeState(node);
        node.Suppress = false;
    }

    private void RefreshAncestors(WorkspaceNode? node)
    {
        while (node is not null)
        {
            node.Suppress = true;
            node.IsChecked = ComputeState(node);
            node.Suppress = false;
            node = node.Parent;
        }
    }

    private void ApplyFilter()
    {
        string filter = FilterBox.Text.Trim();

        if (filter.Length == 0)
        {
            BuildTree();
            return;
        }

        var matches = new List<WorkspaceNode>();
        foreach (string relative in ProjectScanner.FilesUnder(_root, _root, 800))
        {
            if (!relative.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            WorkspaceNode node = CreateNode(Path.GetFileName(relative), relative,
                Path.Combine(_root, relative), false, null);
            node.Suppress = true;
            node.IsChecked = _selected.Contains(relative);
            node.Suppress = false;

            matches.Add(node);
            if (matches.Count >= 400) break;
        }

        FileTree.ItemsSource = matches;
        UpdateStatus();
    }

    private void SelectAll()
    {
        foreach (string file in ProjectScanner.FilesUnder(_root, _root)) _selected.Add(file);
        BuildTree();
    }

    private void ClearAll()
    {
        _selected.Clear();
        BuildTree();
    }

    private void UpdateStatus()
    {
        StatusText.Text = $"{_selected.Count} 个文件已选";
    }

    private void CreateWorkspace()
    {
        string name = SceneNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "场景名不能为空";
            return;
        }

        if (_selected.Count == 0)
        {
            StatusText.Text = "至少选择一个文件";
            return;
        }

        List<string> files = _selected
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SetpieceConfig config = _existing ?? new SetpieceConfig();
        config.Scenes.Add(new Scene
        {
            Name = name,
            Template = "auto",
            Monitor = "primary",
            Items = files.Select(f => new SceneItem { File = f }).ToList(),
        });

        CreatedSceneName = name;
        ConfigStore.Save(_root, config);
        DialogResult = true;
    }
}
