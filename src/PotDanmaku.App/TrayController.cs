using System.Drawing;
using Forms = System.Windows.Forms;

namespace PotDanmaku.App;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon trayIcon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem toggle = new("显示弹幕");

    public TrayController(Action open, Action toggleDanmaku, Action exit)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/PotDanmaku.ico"))!.Stream;
        using var resourceIcon = new Icon(stream);
        trayIcon = (Icon)resourceIcon.Clone();
        menu.Items.Add("打开设置", null, (_, _) => open());
        toggle.Click += (_, _) => toggleDanmaku(); menu.Items.Add(toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Icon = trayIcon, Text = "PotDanmaku", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => open();
        Update(true);
    }
    public void Update(bool enabled)
    {
        toggle.Checked = enabled;
        icon.Text = enabled ? "PotDanmaku · 弹幕显示已开启" : "PotDanmaku · 弹幕已隐藏";
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); trayIcon.Dispose(); menu.Dispose(); }
}
