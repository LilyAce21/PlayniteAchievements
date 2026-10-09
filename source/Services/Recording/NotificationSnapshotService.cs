using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>The running game an announcement belongs to, with what is needed to capture it.</summary>
    internal sealed class RunningGameTarget
    {
        public Guid GameId { get; set; }

        public IntPtr WindowHandle { get; set; }

        public int? ProcessId { get; set; }
    }

    /// <summary>
    /// Takes a game-window screenshot at the moment the Achievements desktop app announces an
    /// unlock, and later hands it to this add-on's own screenshot flow in place of the live
    /// capture (which can run seconds after the unlock for slower providers). The picture is a
    /// normal game-window capture, so screenshots stay exactly what they were; only the instant
    /// they are taken moves earlier. When nothing matches, the flow falls back to the live capture.
    /// </summary>
    internal sealed class NotificationSnapshotService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Func<bool> _isEnabled;
        private readonly Func<UnlockNotifyMessage, RunningGameTarget> _resolveGame;
        private readonly Func<IntPtr, int?, int, Bitmap> _captureGameWindow;
        private readonly Func<int> _capHeight;
        private readonly Dispatcher _uiDispatcher;
        private readonly NotificationSnapshotStore _store;
        private readonly UnlockNotifyPipeServer _server;
        private bool _disposed;

        public NotificationSnapshotService(
            ILogger logger,
            Dispatcher uiDispatcher,
            Func<bool> isEnabled,
            Func<UnlockNotifyMessage, RunningGameTarget> resolveGame,
            Func<IntPtr, int?, int, Bitmap> captureGameWindow,
            Func<int> capHeight)
        {
            _logger = logger;
            _uiDispatcher = uiDispatcher;
            _isEnabled = isEnabled ?? (() => false);
            _resolveGame = resolveGame ?? throw new ArgumentNullException(nameof(resolveGame));
            _captureGameWindow = captureGameWindow ?? throw new ArgumentNullException(nameof(captureGameWindow));
            _capHeight = capHeight ?? (() => 0);
            _store = new NotificationSnapshotStore();
            _server = new UnlockNotifyPipeServer(
                UnlockNotifyPipeServer.DefaultPipeName,
                OnLine,
                text => _logger?.Debug(text));
        }

        public void Start()
        {
            try
            {
                _server.Start();
                _logger?.Info("[Screenshots] Listening for unlock announcements from the Achievements app.");
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[Screenshots] Could not start the unlock announcement listener.");
            }
        }

        private void OnLine(string line)
        {
            if (_disposed || !_isEnabled())
            {
                return;
            }

            var received = DateTime.UtcNow;
            if (!UnlockNotifyMessage.TryParse(line, received, out var message))
            {
                return;
            }

            // Window handles are resolved on the UI thread (as the toast service does); the
            // capture itself runs on the pool.
            if (_uiDispatcher == null)
            {
                return;
            }

            _uiDispatcher.BeginInvoke(new Action(() => CaptureFor(message)));
        }

        private void CaptureFor(UnlockNotifyMessage message)
        {
            RunningGameTarget target;
            try
            {
                target = _resolveGame(message);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Screenshots] Could not resolve the running game for an announcement.");
                return;
            }

            if (target == null || (target.WindowHandle == IntPtr.Zero && !(target.ProcessId.HasValue && target.ProcessId.Value > 0)))
            {
                return;
            }

            var capHeight = _capHeight();
            Task.Run(() =>
            {
                try
                {
                    using (var bitmap = _captureGameWindow(target.WindowHandle, target.ProcessId, capHeight))
                    {
                        if (bitmap == null)
                        {
                            return;
                        }

                        byte[] png;
                        using (var stream = new MemoryStream())
                        {
                            bitmap.Save(stream, ImageFormat.Png);
                            png = stream.ToArray();
                        }

                        _store.Add(new NotificationSnapshot
                        {
                            GameId = target.GameId,
                            NameKey = NotificationSnapshotStore.NormalizeKey(message.AchievementName),
                            ApiKey = NotificationSnapshotStore.NormalizeKey(message.ApiName),
                            CapturedUtc = message.ReceivedUtc,
                            Png = png
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[Screenshots] Taking the announcement snapshot failed.");
                }
            });
        }

        /// <summary>
        /// The snapshot for this add-on's own unlock event, decoded into a bitmap the caller owns,
        /// or null when none fits (the caller then keeps its normal capture). Never throws.
        /// </summary>
        public Bitmap TryTake(AchievementUnlockedEventArgs e, int capHeight)
        {
            try
            {
                if (_disposed || e == null)
                {
                    return null;
                }

                var snapshot = _store.TakeBest(e.PlayniteGameId, e.DisplayName, e.ApiName, e.UnlockTimeUtc);
                if (snapshot == null)
                {
                    return null;
                }

                using (var stream = new MemoryStream(snapshot.Png))
                using (var image = Image.FromStream(stream))
                {
                    return new Bitmap(image);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[Screenshots] Could not use the announcement snapshot; keeping the live capture.");
                return null;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                _server.Dispose();
            }
            catch (Exception)
            {
            }

            _store.Clear();
        }
    }
}
