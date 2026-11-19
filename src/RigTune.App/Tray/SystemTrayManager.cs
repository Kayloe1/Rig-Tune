using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Services.GameMode;
using Wpf.Ui.Tray.Controls;

namespace RigTune.App.Tray;

/// <summary>
/// Owns the system-tray experience (SPEC §5.5): dynamic context menu (Game Mode status, Start/Stop,
/// refresh-rate status with 1-click Restore, Open, Exit), minimize-to-tray, and balloon notifications
/// whose click runs an action (e.g. restore the refresh rate).
/// </summary>
public sealed class SystemTrayManager : IDisposable
{
    private const int NIM_MODIFY = 0x1;
    private const int NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x1;
    private const int NIIF_WARNING = 0x2;
    private const int NIN_BALLOONUSERCLICK = 0x405;

    private readonly GameModeService _gameMode;
    private readonly RefreshRateWatchdogService _watchdog;
    private readonly SettingsService _settings;
    private NotifyIcon? _icon;
    private Window? _window;
    private HwndSource? _hook;
    private Action? _balloonAction;

    public SystemTrayManager(GameModeService gameMode, RefreshRateWatchdogService watchdog, SettingsService settings)
    {
        _gameMode = gameMode;
        _watchdog = watchdog;
        _settings = settings;
    }

    /// <summary>Set when the user chose Exit, so closing the window really quits.</summary>
    public bool IsExiting { get; private set; }

    public void Attach(Window window, NotifyIcon icon)
    {
        _window = window;
        _icon = icon;
        icon.Menu = new ContextMenu();
        icon.Menu.Opened += (_, _) => BuildMenu();
        BuildMenu();
#pragma warning disable CS8622 // WPF-UI's RoutedNotifyIconEvent declares mismatched nullability on its parameters.
        icon.LeftDoubleClick += OnTrayDoubleClick;
#pragma warning restore CS8622

        window.Closing += async (_, e) =>
        {
            if (IsExiting)
            {
                return;
            }

            e.Cancel = true;
            if ((await _settings.GetAsync()).MinimizeToTrayOnClose)
            {
                window.Hide();
                Notify("RigTune is still running", "RigTune keeps watching games and refresh rates from the tray. Right-click the icon to exit.", null);
            }
            else
            {
                Exit();
            }
        };

        _watchdog.DriftDetected += (_, drift) => Application.Current.Dispatcher.BeginInvoke(() =>
            Notify("Refresh rate dropped",
                string.Join("\n", drift.Select(d => $"{d.Display.FriendlyName}: {d.CurrentHz} Hz (target {d.TargetHz} Hz)")) + "\nClick to restore.",
                () => _ = _watchdog.RestoreAllAsync(), warning: true));

        _gameMode.SessionChanged += (_, session) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdateTooltip();
            if (session is not null && !session.IsManual)
            {
                Notify("Game Mode on", $"{session.GameName}: {session.Throttled.Count} background app process(es) moved to efficiency mode.", ShowWindow);
            }
        });

        UpdateTooltip();
    }

    private void OnTrayDoubleClick(NotifyIcon sender, RoutedEventArgs e) => ShowWindow();

    public void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>Lets the window close for real (used when Windows is shutting down).</summary>
    public void MarkExiting() => IsExiting = true;

    public void Exit()
    {
        IsExiting = true;
        Application.Current.Shutdown();
    }

    private void BuildMenu()
    {
        if (_icon?.Menu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();
        GameSession? session = _gameMode.ActiveSession;
        menu.Items.Add(new MenuItem
        {
            Header = session is null ? "Game Mode: idle" : $"Game Mode: active — {session.GameName ?? "manual"}",
            IsEnabled = false,
        });
        menu.Items.Add(Item(session is null ? "Start Game Mode" : "Stop Game Mode", async () =>
        {
            if (_gameMode.ActiveSession is null)
            {
                await _gameMode.StartManualAsync();
            }
            else
            {
                await _gameMode.StopAsync("stopped from tray");
            }
        }));
        menu.Items.Add(new Separator());

        bool drifted = _watchdog.LastStatus.Any(s => s.IsDrifted);
        int pinned = _watchdog.LastStatus.Count(s => s.TargetHz is not null);
        menu.Items.Add(new MenuItem
        {
            Header = pinned == 0 ? "Refresh rate: no targets pinned" : drifted ? "Refresh rate: below target ⚠" : "Refresh rate: OK",
            IsEnabled = false,
        });
        if (drifted)
        {
            menu.Items.Add(Item("Restore refresh rate", () => _watchdog.RestoreAllAsync()));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open RigTune", () =>
        {
            ShowWindow();
            return Task.CompletedTask;
        }));
        menu.Items.Add(Item("Exit", () =>
        {
            Exit();
            return Task.CompletedTask;
        }));
    }

    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private void UpdateTooltip()
    {
        if (_icon is not null)
        {
            _icon.TooltipText = _gameMode.ActiveSession is { } s ? $"RigTune — Game Mode: {s.GameName ?? "manual"}" : "RigTune";
        }
    }

    /// <summary>Shows a tray balloon (a toast on Windows 11); clicking it runs <paramref name="onClick"/>.</summary>
    public void Notify(string title, string message, Action? onClick, bool warning = false)
    {
        if (_icon is not { IsRegistered: true, HookWindow: { } hook })
        {
            return;
        }

        if (!ReferenceEquals(_hook, hook))
        {
            _hook?.RemoveHook(WndProc);
            _hook = hook;
            hook.AddHook(WndProc);
        }

        _balloonAction = onClick;
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hook.Handle,
            uID = _icon.Id,
            uFlags = NIF_INFO,
            szInfo = message.Length > 255 ? message[..255] : message,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO,
            szTip = string.Empty,
        };
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The icon's callback message carries the balloon event in LOWORD(lParam) (any NOTIFYICON_VERSION).
        if (msg >= 0x400 && (lParam.ToInt64() & 0xFFFF) == NIN_BALLOONUSERCLICK && _balloonAction is { } action)
        {
            _balloonAction = null;
            action();
        }

        return IntPtr.Zero;
    }

    public void Dispose() => _hook?.RemoveHook(WndProc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
}
