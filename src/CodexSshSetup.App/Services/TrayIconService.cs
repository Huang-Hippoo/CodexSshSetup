using System.Drawing;
using System.Windows.Forms;

namespace CodexSshSetup.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIconService(Action show, Action connect, Action disconnect, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => show());
        menu.Items.Add("连接", null, (_, _) => connect());
        menu.Items.Add("断开", null, (_, _) => disconnect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("停止隧道并退出", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Text = "Codex SSH Setup",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
