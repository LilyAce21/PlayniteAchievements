using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Security;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services
{
    public partial class NotificationPublisher
    {


        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        /// <summary>
        /// Gives the process id of a game's running process (null when it is not running). Lets the
        /// SAN live blur capture the game window directly instead of the whole monitor.
        /// </summary>
        public Func<Guid, int?> GameProcessIdResolver { get; set; }

        private UnlockScreenshotService _sanScreenshotService;
        private static readonly List<OverlayWindowState> ActiveOverlayWindows = new List<OverlayWindowState>();
        private static readonly object WebView2LoaderSync = new object();
        private static bool _webView2LoaderConfigured;
        private static bool _sanWebViewWarmupStarted;
        private static Task<CoreWebView2Environment> _sanWebViewEnvironmentTask;
        private static Window _sanWebViewWarmupWindow;
        private static WebView2 _sanWebViewWarmupControl;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        public NotificationPublisher(IPlayniteAPI api, PlayniteAchievementsSettings settings, ILogger logger)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            WarmUpSanWebView2();
        }

        private void LogAchievementNotificationDebug(string message)
        {
            AchievementNotificationDebugLog.Info(message);
        }

        /// <summary>
        /// Reports an unattended background refresh that fell short — a provider threw, a provider
        /// needs re-authentication, or the run failed outright. A clean refresh says nothing, so
        /// seeing this always means something wants attention rather than being routine noise.
        /// There is no per-feature setting; only the plugin-wide notification switch silences it.
        /// </summary>
        public void ShowRefreshFailed(string status)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
                return;

            var title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
            var text = string.IsNullOrWhiteSpace(status)
                ? ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed")
                : status;

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-RefreshFailed-{Guid.NewGuid()}",
                    $"{title}\n{text}",
                    NotificationType.Error));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show refresh failure notification.");
            }
        }

        public void ShowThemeAutoMigrated(string themeName)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
            {
                return;
            }

            var title = ResourceProvider.GetString("LOCPlayAch_ThemeMigration_AutoMigratedTitle");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Theme Auto-Migrated";
            }

            var displayName = string.IsNullOrWhiteSpace(themeName) ? "Theme" : themeName;

            var message = string.Format(
                ResourceProvider.GetString("LOCPlayAch_ThemeMigration_AutoMigratedMessage"),
                displayName);

            var restart = ResourceProvider.GetString("LOCPlayAch_ThemeMigration_RestartRequired");

            var text = $"{message}\n{restart}";

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-ThemeAutoMigrated-{Guid.NewGuid()}",
                    $"{title}\n{text}",
                    NotificationType.Info,
                    RestartAfterThemeAutoMigration));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show theme auto-migrated notification.");
            }
        }

        private void RestartAfterThemeAutoMigration()
        {
            var dispatcher = _api.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(RestartAfterThemeAutoMigration));
                return;
            }

            try
            {
                // Restart is not exposed by the SDK. Use the host's own restart path
                // so settings are saved, extensions shut down, and the current mode is retained.
                var applicationType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("Playnite.PlayniteApplication", false))
                    .FirstOrDefault(type => type != null);
                var application = applicationType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                var restart = applicationType?.GetMethod("Restart", new[] { typeof(bool) });
                if (application == null || restart == null)
                {
                    throw new InvalidOperationException("Playnite's restart function is unavailable.");
                }

                restart.Invoke(application, new object[] { true });
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Error(ex, "Failed to restart Playnite after automatic theme migration.");
                _api.Dialogs.ShowErrorMessage("Playnite could not restart automatically. Please restart Playnite to apply the migrated theme.");
            }
        }

        public void ShowUpstreamReleaseAvailable(string upstreamVersion, string releaseUrl)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
            {
                return;
            }

            var title = ResourceProvider.GetString("LOCPlayAch_Notification_UpstreamReleaseTitle");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Original Fork Update Available";
            }

            var messageFormat = ResourceProvider.GetString("LOCPlayAch_Notification_UpstreamReleaseMessage");
            if (string.IsNullOrWhiteSpace(messageFormat))
            {
                messageFormat = "The original PlayniteAchievements fork released version {0}. Click to open the upstream releases page.";
            }

            var message = string.Format(messageFormat, upstreamVersion ?? "?");

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-UpstreamRelease-{upstreamVersion}",
                    $"{title}\n{message}",
                    NotificationType.Info,
                    () => OpenUrl(releaseUrl)));
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Debug(ex, "Failed to show upstream release notification.");
            }
        }

        public void ShowForkReleaseAvailable(string forkVersion, string releaseUrl)
        {
            if (_settings?.Persisted?.EnableNotifications != true)
            {
                return;
            }

            var title = "Santodan Fork Update Available";
            var message = string.Format(
                "The Santodan PlayniteAchievements fork released version {0}. Click to open the fork releases page.",
                forkVersion ?? "?");

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"PlayniteAchievements-ForkRelease-{forkVersion}",
                    $"{title}\n{message}",
                    NotificationType.Info,
                    () => OpenUrl(releaseUrl)));
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Debug(ex, "Failed to show fork release notification.");
            }
        }

        

                public void ShowLocalAchievementUnlocked(
            string gameName,
            IReadOnlyList<AchievementUnlockNotificationItem> unlockedAchievements,
            string notificationProviderKey = "Local",
            Game game = null)
        {
            var resolvedProviderKey = string.IsNullOrWhiteSpace(notificationProviderKey) ? "Local" : notificationProviderKey.Trim();

            // The official SAN template is the only notification renderer. This does not look at the
            // plugin's "enable notifications" switch, the per-provider toast flags or the in-app
            // notification setting.
            ShowOfficialSanUnlocks(
                gameName,
                unlockedAchievements,
                resolvedProviderKey,
                game,
                ProviderRegistry.Settings<LocalSettings>());
        }







        


        private static string AuthNotificationId(string providerKey) => $"PlayAch-AuthFailed-{providerKey}";

        public void ShowProviderAuthFailed(List<string> providerKeys)
        {
            if (providerKeys == null || providerKeys.Count == 0)
                return;

            var pluginName = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");

            foreach (var providerKey in providerKeys)
            {
                try
                {
                    var providerName = GetLocalizedProviderName(providerKey);
                    var message = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Notification_ProviderAuthFailed"),
                        providerName);

                    var capturedKey = providerKey;
                    _api.Notifications.Add(new NotificationMessage(
                        AuthNotificationId(providerKey),
                        $"{pluginName}\n{message}",
                        NotificationType.Error,
                        () => OpenPluginSettingsForProvider(capturedKey)));
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed to show auth notification for {providerKey}.");
                }
            }
        }

        /// <summary>
        /// Clears the auth-failed notification for the given providers. Callers pass the providers
        /// their refresh actually spoke to, so a refresh cannot clear an unrelated provider's
        /// warning.
        /// </summary>
        public void ClearProviderAuthNotifications(IEnumerable<string> providerKeys)
        {
            if (providerKeys == null)
                return;

            foreach (var providerKey in providerKeys)
            {
                try
                {
                    _api.Notifications.Remove(AuthNotificationId(providerKey));
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed to clear auth notification for {providerKey}.");
                }
            }
        }

        private static string GetLocalizedProviderName(string providerKey)
        {
            var resourceKey = $"LOCPlayAch_Provider_{providerKey}";
            var name = ResourceProvider.GetString(resourceKey);
            return !string.IsNullOrWhiteSpace(name) ? name : providerKey;
        }

        private void OpenPluginSettingsForProvider(string providerKey)
        {
            try
            {
                var plugin = PlayniteAchievementsPlugin.Instance;
                if (plugin == null)
                    return;

                Views.SettingsControl.PendingNavigationProviderKey = providerKey;
                _api.MainView.OpenPluginSettings(plugin.Id);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to open plugin settings from notification click.");
            }
        }

        private void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Debug(ex, $"Failed to open URL: {url}");
            }
        }











        private void PositionOverlayWindow(
            Window window,
            LocalUnlockOverlayPosition position,
            int stackIndex = 0,
            bool followActiveGameMonitor = true,
            bool debugLoggingEnabled = false)
        {
            if (window == null)
            {
                return;
            }

            const double margin = 16;
            const double spacing = 8;
            var systemWorkArea = SystemParameters.WorkArea;
            var monitorDiagnostics = new OverlayMonitorDiagnostics();
            var foregroundWorkArea = systemWorkArea;
            if (followActiveGameMonitor || debugLoggingEnabled)
            {
                foregroundWorkArea = GetForegroundMonitorWorkArea(out monitorDiagnostics);
            }

            var workArea = followActiveGameMonitor ? foregroundWorkArea : systemWorkArea;
            var stackOffset = Math.Max(0, stackIndex) * (GetOverlayWindowHeight(window) + spacing);
            switch (position)
            {
                case LocalUnlockOverlayPosition.TopLeft:
                    window.Left = workArea.Left + margin;
                    window.Top = workArea.Top + margin + stackOffset;
                    break;
                case LocalUnlockOverlayPosition.TopCenter:
                    window.Left = workArea.Left + ((workArea.Width - window.Width) / 2);
                    window.Top = workArea.Top + margin + stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomLeft:
                    window.Left = workArea.Left + margin;
                    window.Top = workArea.Bottom - GetOverlayWindowHeight(window) - margin - stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomCenter:
                    window.Left = workArea.Left + ((workArea.Width - window.Width) / 2);
                    window.Top = workArea.Bottom - GetOverlayWindowHeight(window) - margin - stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomRight:
                    window.Left = workArea.Right - window.Width - margin;
                    window.Top = workArea.Bottom - GetOverlayWindowHeight(window) - margin - stackOffset;
                    break;
                default:
                    window.Left = workArea.Right - window.Width - margin;
                    window.Top = workArea.Top + margin + stackOffset;
                    break;
            }

            if (debugLoggingEnabled)
            {
                var wpfDpi = VisualTreeHelper.GetDpi(window);
                LogAchievementNotificationDebug(
                    $"[LocalOverlayDebug] Positioned renderer='{ResolveOverlayRendererName(window)}', position='{position}', stackIndex='{stackIndex}', " +
                    $"followActiveMonitor='{followActiveGameMonitor}', foregroundHwnd='{FormatHandle(monitorDiagnostics.ForegroundWindow)}', " +
                    $"monitorHandle='{FormatHandle(monitorDiagnostics.Monitor)}', monitorInfo='{monitorDiagnostics.HasMonitorInfo}', " +
                    $"monitorBoundsRaw='{FormatNativeRect(monitorDiagnostics.MonitorBounds)}', monitorWorkRaw='{FormatNativeRect(monitorDiagnostics.WorkArea)}', " +
                    $"foregroundDpi='{monitorDiagnostics.ForegroundDpi}', wpfScale='{wpfDpi.DpiScaleX:0.###}x{wpfDpi.DpiScaleY:0.###}', " +
                    $"selectedWorkArea='{FormatRect(workArea)}', systemWorkAreaDip='{FormatRect(systemWorkArea)}', " +
                    $"windowBoundsDip='{window.Left:0.###},{window.Top:0.###},{window.Width:0.###},{GetOverlayWindowHeight(window):0.###}'.");
            }
        }

        private static double GetOverlayWindowHeight(Window window)
        {
            if (window == null)
            {
                return 0;
            }

            if (window.ActualHeight > 0)
            {
                return window.ActualHeight;
            }

            if (!double.IsNaN(window.Height) && window.Height > 0)
            {
                return window.Height;
            }

            if (window.MinHeight > 0)
            {
                return window.MinHeight;
            }

            return 110;
        }


        private static OverlayWindowState RegisterOverlayWindow(Window window, LocalUnlockOverlayPosition position)
        {
            if (window == null)
            {
                return null;
            }

            var state = new OverlayWindowState(window, position);
            ActiveOverlayWindows.RemoveAll(item => item?.Window == null || !item.Window.IsVisible);

            // Newest first: a new notification takes the slot nearest the screen corner and the ones
            // already showing move one slot further away.
            ActiveOverlayWindows.Insert(0, state);
            return state;
        }

        private void UnregisterOverlayWindow(OverlayWindowState state)
        {
            if (state == null)
            {
                return;
            }

            state.CloseTimer?.Stop();
            ActiveOverlayWindows.Remove(state);

            // The cards that were beyond this one slide back towards the corner.
            RepositionActiveOverlayWindows(state.Position, animate: true);
        }

        private static int GetOverlayStackIndex(OverlayWindowState state)
        {
            if (state == null)
            {
                return 0;
            }

            return ActiveOverlayWindows
                .Where(item => item != null && item.Position == state.Position)
                .TakeWhile(item => !ReferenceEquals(item, state))
                .Count();
        }

        /// <summary>
        /// Lays the active windows of one screen position out in order, newest first. Windows that
        /// are exiting still count and still move, so nothing overlaps while a card leaves.
        /// <paramref name="skip"/> is a window that has just been placed (it may not be visible yet, so
        /// it is counted anyway) and is not moved again.
        /// </summary>
        private void RepositionActiveOverlayWindows(LocalUnlockOverlayPosition position, bool animate = false, OverlayWindowState skip = null)
        {
            var overlays = ActiveOverlayWindows
                .Where(item => item != null && item.Window != null && item.Position == position &&
                               (item.Window.IsVisible || ReferenceEquals(item, skip)))
                .ToList();

            for (var i = 0; i < overlays.Count; i++)
            {
                if (ReferenceEquals(overlays[i], skip))
                {
                    continue;
                }

                var settings = ProviderRegistry.Settings<LocalSettings>();
                if (overlays[i].Renderer == "OfficialSAN")
                {
                    PositionOfficialSanWindow(overlays[i], i, settings?.ShowOverlayOnActiveGameMonitor == true, animate);
                    continue;
                }

                PositionOverlayWindow(
                    overlays[i].Window,
                    position,
                    i,
                    settings?.ShowOverlayOnActiveGameMonitor == true,
                    settings?.EnableOverlayDebugLogging == true);
            }
        }

        private sealed class OverlayWindowState
        {
            public OverlayWindowState(Window window, LocalUnlockOverlayPosition position)
            {
                Window = window;
                Position = position;
            }

            public Window Window { get; }
            public LocalUnlockOverlayPosition Position { get; }
            public bool IsClosing { get; set; }
            public string Renderer { get; set; }
            /// <summary>Transparent margin (DIP) between an official-SAN window edge and its card.</summary>
            public double SanInset { get; set; }
            /// <summary>Height (DIP) of an official-SAN card without its margin.</summary>
            public double SanCardHeight { get; set; }
            public DispatcherTimer CloseTimer { get; set; }
            public Action<string> UpdateHtml { get; set; }
        }



        private void ConfigureWebView2LoaderDirectory()
        {
            try
            {
                lock (WebView2LoaderSync)
                {
                    if (_webView2LoaderConfigured)
                    {
                        return;
                    }

                    var pluginDirectory = GetPluginAssemblyDirectory();
                    var architectureFolder = Environment.Is64BitProcess ? "x64" : "x86";
                    var loaderDirectory = Path.Combine(pluginDirectory, architectureFolder);
                    var loaderPath = Path.Combine(loaderDirectory, "WebView2Loader.dll");
                    if (!File.Exists(loaderPath))
                    {
                        ForkLog.Wrap(_logger)?.Warn($"[LocalOverlay] WebView2 loader was not found at '{loaderPath}'.");
                        return;
                    }

                    TryCopyWebView2LoaderToPluginRoot(loaderPath);
                    SetDllDirectory(loaderDirectory);
                    var module = LoadLibrary(loaderPath);
                    if (module == IntPtr.Zero)
                    {
                        ForkLog.Wrap(_logger)?.Warn($"[LocalOverlay] Failed to preload WebView2 loader from '{loaderPath}'.");
                    }

                    if (!SetDllDirectory(loaderDirectory))
                    {
                        ForkLog.Wrap(_logger)?.Warn($"[LocalOverlay] Failed to set WebView2 loader directory to '{loaderDirectory}'.");
                    }

                    _webView2LoaderConfigured = true;
                }
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Warn(ex, "[LocalOverlay] Failed to configure WebView2 loader directory.");
            }
        }

        private static Rect GetForegroundMonitorWorkArea(out OverlayMonitorDiagnostics diagnostics)
        {
            diagnostics = new OverlayMonitorDiagnostics();
            try
            {
                var foregroundWindow = GetForegroundWindow();
                diagnostics.ForegroundWindow = foregroundWindow;
                diagnostics.ForegroundDpi = TryGetWindowDpi(foregroundWindow);
                var monitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToPrimary);
                diagnostics.Monitor = monitor;
                if (monitor != IntPtr.Zero)
                {
                    var monitorInfo = new MonitorInfo
                    {
                        Size = Marshal.SizeOf(typeof(MonitorInfo))
                    };

                    if (GetMonitorInfo(monitor, ref monitorInfo))
                    {
                        diagnostics.HasMonitorInfo = true;
                        diagnostics.MonitorBounds = monitorInfo.Monitor;
                        diagnostics.WorkArea = monitorInfo.Work;
                        return new Rect(
                            monitorInfo.Work.Left,
                            monitorInfo.Work.Top,
                            monitorInfo.Work.Right - monitorInfo.Work.Left,
                            monitorInfo.Work.Bottom - monitorInfo.Work.Top);
                    }
                }
            }
            catch
            {
            }

            return SystemParameters.WorkArea;
        }



        private static string ResolveOverlayRendererName(Window window)
        {
            return window?.Content is WebView2 ? "SAN-WebView2" : "WPF";
        }

        private static string FormatHandle(IntPtr handle)
        {
            return $"0x{handle.ToInt64():X}";
        }

        private static string FormatRect(Rect rect)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.###},{1:0.###},{2:0.###},{3:0.###}",
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height);
        }

        private static string FormatNativeRect(NativeRect rect)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},{3}",
                rect.Left,
                rect.Top,
                rect.Right,
                rect.Bottom);
        }

        private static uint TryGetWindowDpi(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return GetDpiForWindow(window);
            }
            catch
            {
                return 0;
            }
        }

        private const uint MonitorDefaultToPrimary = 1;

        private sealed class OverlayMonitorDiagnostics
        {
            public IntPtr ForegroundWindow { get; set; }
            public IntPtr Monitor { get; set; }
            public bool HasMonitorInfo { get; set; }
            public NativeRect MonitorBounds { get; set; }
            public NativeRect WorkArea { get; set; }
            public uint ForegroundDpi { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);


        private void AttachOverlayTopmostGuard(Window window, bool debugLoggingEnabled)
        {
            if (window == null)
            {
                return;
            }

            DispatcherTimer topmostTimer = null;
            var loggedSuccess = false;
            var loggedFailure = false;
            Action enforceTopmost = () =>
            {
                try
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    if (handle != IntPtr.Zero)
                    {
                        var succeeded = SetWindowPos(
                            handle,
                            HwndTopmost,
                            0,
                            0,
                            0,
                            0,
                            SetWindowPosNoMove |
                            SetWindowPosNoSize |
                            SetWindowPosNoActivate |
                            SetWindowPosShowWindow);

                        if (succeeded && !loggedSuccess && debugLoggingEnabled)
                        {
                            loggedSuccess = true;
                            LogAchievementNotificationDebug(
                                $"[LocalOverlayDebug] Topmost guard succeeded renderer='{ResolveOverlayRendererName(window)}', " +
                                $"hwnd='{FormatHandle(handle)}'.");
                        }
                        else if (!succeeded && !loggedFailure)
                        {
                            loggedFailure = true;
                            ForkLog.Wrap(_logger)?.Warn(
                                $"[LocalOverlay] Topmost guard failed renderer='{ResolveOverlayRendererName(window)}', " +
                                $"hwnd='{FormatHandle(handle)}', win32Error='{Marshal.GetLastWin32Error()}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!loggedFailure)
                    {
                        loggedFailure = true;
                        ForkLog.Wrap(_logger)?.Warn(ex, "[LocalOverlay] Topmost guard threw an exception.");
                    }
                }
            };

            window.SourceInitialized += (_, __) => enforceTopmost();
            window.Loaded += (_, __) =>
            {
                enforceTopmost();
                topmostTimer = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                topmostTimer.Tick += (_, __) => enforceTopmost();
                topmostTimer.Start();
            };
            window.Closed += (_, __) =>
            {
                topmostTimer?.Stop();
                topmostTimer = null;
            };
        }

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SetWindowPosNoSize = 0x0001;
        private const uint SetWindowPosNoMove = 0x0002;
        private const uint SetWindowPosNoActivate = 0x0010;
        private const uint SetWindowPosShowWindow = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

                public void SendScoreProgressNotification(
            string scoreName,
            string changeName,
            int level,
            string tier,
            ScoreProgressNotificationSettings notification,
            LocalSettings baseSettings)
        {
            if (notification == null || baseSettings == null) return;
            var detail = string.Equals(changeName, "tier", StringComparison.OrdinalIgnoreCase)
                ? $"{scoreName} tier reached: {tier}"
                : $"{scoreName} level reached: {level}";
            RunOnUiThread(() =>
            {
                try
                {
                    TryShowOfficialSanNotification(
                        "Achievement Collection", detail, null, "Local", baseSettings, null, null, null, null);
                }
                catch (Exception ex)
                {
                    ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Failed to show a score-progress notification.");
                }
            });
        }


        private void WarmUpSanWebView2()
        {
            if (_sanWebViewWarmupStarted)
            {
                return;
            }

            _sanWebViewWarmupStarted = true;

            try
            {
                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        var environment = await GetSanWebView2EnvironmentAsync();
                        var webView = new WebView2
                        {
                            Width = 1,
                            Height = 1,
                            DefaultBackgroundColor = System.Drawing.Color.Transparent
                        };

                        var window = new Window
                        {
                            Content = webView,
                            Width = 1,
                            Height = 1,
                            WindowStyle = WindowStyle.ToolWindow,
                            ResizeMode = ResizeMode.NoResize,
                            AllowsTransparency = false,
                            Background = Brushes.Black,
                            ShowInTaskbar = false,
                            ShowActivated = false,
                            IsHitTestVisible = false,
                            Focusable = false,
                            Opacity = 0,
                            Left = -32000,
                            Top = -32000
                        };

                        _sanWebViewWarmupWindow = window;
                        _sanWebViewWarmupControl = webView;
                        window.Show();

                        await webView.EnsureCoreWebView2Async(environment);
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        webView.NavigateToString("<!doctype html><html><head><meta charset=\"utf-8\"></head><body></body></html>");
                        // Keep the initialized controller alive without leaving a visible WPF window in
                        // Application.Current.Windows. Fullscreen themes can treat any visible secondary
                        // window as an active overlay and stop updating their focus/navigation state.
                        window.Hide();
                        ForkLog.Wrap(_logger)?.Debug("[LocalOverlay] SAN WebView2 controller warm-up completed.");
                    }
                    catch (Exception ex)
                    {
                        ForkLog.Wrap(_logger)?.Debug(ex, "[LocalOverlay] SAN WebView2 controller warm-up failed.");
                        try
                        {
                            _sanWebViewWarmupWindow?.Close();
                            _sanWebViewWarmupControl?.Dispose();
                        }
                        catch
                        {
                        }

                        _sanWebViewWarmupWindow = null;
                        _sanWebViewWarmupControl = null;
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Debug(ex, "[LocalOverlay] Failed to schedule SAN WebView2 warm-up.");
            }
        }

        private Task<CoreWebView2Environment> GetSanWebView2EnvironmentAsync()
        {
            lock (WebView2LoaderSync)
            {
                if (_sanWebViewEnvironmentTask == null)
                {
                    ConfigureWebView2LoaderDirectory();
                    _sanWebViewEnvironmentTask = CoreWebView2Environment.CreateAsync(null, null, new CoreWebView2EnvironmentOptions("--allow-file-access-from-files --disable-features=CalculateNativeWinOcclusion --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"));
                }

                return _sanWebViewEnvironmentTask;
            }
        }

        private void TryCopyWebView2LoaderToPluginRoot(string loaderPath)
        {
            try
            {
                var rootLoaderPath = Path.Combine(GetPluginAssemblyDirectory(), "WebView2Loader.dll");
                var source = new FileInfo(loaderPath);
                var destination = new FileInfo(rootLoaderPath);
                if (destination.Exists && destination.Length == source.Length)
                {
                    return;
                }

                File.Copy(loaderPath, rootLoaderPath, true);
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Warn(ex, "[LocalOverlay] Failed to stage WebView2 loader in the plugin root.");
            }
        }

        private string WriteSanWebViewDocumentToTempFile(string html)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "SanOverlay");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"san-overlay-{Guid.NewGuid():N}.html");
            File.WriteAllText(path, html ?? string.Empty, Encoding.UTF8);
            return path;
        }

        private void TryDeleteSanWebViewDocument(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string GetPluginAssemblyDirectory()
        {
            var assemblyLocation = typeof(NotificationPublisher).Assembly.Location;
            return string.IsNullOrWhiteSpace(assemblyLocation)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(assemblyLocation) ?? AppDomain.CurrentDomain.BaseDirectory;
        }


















































































































































        private void RunOnUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        public static string ResolveSoundPath(string soundPath)
        {
            if (string.IsNullOrWhiteSpace(soundPath))
            {
                return string.Empty;
            }

            var trimmedPath = soundPath.Trim();
            if (Path.IsPathRooted(trimmedPath))
            {
                return trimmedPath;
            }

            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                return trimmedPath;
            }

            return Path.GetFullPath(Path.Combine(assemblyDirectory, trimmedPath));
        }

    }

    public sealed class AchievementUnlockNotificationItem
    {
        public AchievementUnlockNotificationItem(
            string name,
            string iconPath = null,
            string description = null,
            int? points = null,
            string rarity = null,
            string trophy = null)
        {
            Name = name ?? string.Empty;
            IconPath = iconPath;
            Description = description;
            Points = points;
            Rarity = rarity;
            Trophy = trophy;
        }

        public string Name { get; }

        public string IconPath { get; }

        public string Description { get; }

        public int? Points { get; }

        public string Rarity { get; }

        public string Trophy { get; }

        // Extra unlock details used by the official SAN template. Optional.
        public bool IsCapstone { get; set; }

        public bool IsHidden { get; set; }

        public bool IsCompletion { get; set; }

        public string RarityTier { get; set; }

        public double? GlobalPercent { get; set; }

        public string GameIconPath { get; set; }

        public string GameCoverPath { get; set; }
    }
}
