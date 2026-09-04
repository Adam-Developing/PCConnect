using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace PCConnect.Companion.Services;

/// <summary>
/// The system tray presence. The v1 client lived here and users expect it to:
/// closing the window leaves PCConnect running, and the tray is how it comes back.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private NotifyIcon? _icon;

    public void Show(Action openWindow, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PCConnect", null, (_, _) => openWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "PCConnect",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => openWindow();
    }

    /// <summary>
    /// Uses the shipped icon when it is present and the executable's own icon
    /// otherwise, rather than throwing at startup because a resource is missing —
    /// which is exactly how the v1 client failed when its working directory was
    /// not what it expected.
    /// </summary>
    private static Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "PCConnect.ico");

        if (File.Exists(path))
        {
            try
            {
                return new Icon(path);
            }
            catch (ArgumentException)
            {
                // fall through to the executable's icon
            }
        }

        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
