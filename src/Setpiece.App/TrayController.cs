using System.Drawing;
using System.Windows.Forms;

namespace Setpiece.App;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<int>? SceneRequested;

    public TrayController()
    {
        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Setpiece",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        RebuildMenu(null);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/app.ico"));
            if (resource?.Stream is not null) return new Icon(resource.Stream);
        }
        catch
        {
            // 资源缺失时退回系统图标。
        }
        return SystemIcons.Application;
    }

    public void RebuildMenu(IReadOnlyList<string>? scenes)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => ShowRequested?.Invoke());

        if (scenes is { Count: > 0 })
        {
            menu.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < scenes.Count && i < 9; i++)
            {
                int index = i;
                menu.Items.Add($"{i + 1}. 打开 {scenes[i]}", null, (_, _) => SceneRequested?.Invoke(index));
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        ContextMenuStrip? old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void Notify(string text) =>
        _icon.ShowBalloonTip(2500, "Setpiece", text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
