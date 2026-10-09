using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Listens on a local named pipe for one-line JSON messages from the Achievements desktop app.
    /// The pipe is limited to the current Windows user, accepts one short message per connection,
    /// caps the size it will read, and never throws into the host: a bad or slow client is simply
    /// dropped. It touches nothing but the callback it was given.
    /// </summary>
    internal sealed class UnlockNotifyPipeServer : IDisposable
    {
        public const string DefaultPipeName = "PlayniteAchievements.UnlockNotify.v1";

        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

        private readonly string _pipeName;
        private readonly Action<string> _onMessage;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;
        private int _started;

        public UnlockNotifyPipeServer(string pipeName, Action<string> onMessage, Action<string> log = null)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
            _onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var server = CreateServer())
                    {
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                        var text = await ReadMessageAsync(server, token).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(text))
                        {
                            foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var trimmed = line.Trim();
                                if (trimmed.Length > 0)
                                {
                                    Deliver(trimmed);
                                }
                            }
                        }

                        try
                        {
                            if (server.IsConnected)
                            {
                                server.Disconnect();
                            }
                        }
                        catch (Exception)
                        {
                            // the client already left
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log("Unlock notify pipe error: " + ex.Message);
                    try
                    {
                        await Task.Delay(RestartDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private NamedPipeServerStream CreateServer()
        {
            var security = new PipeSecurity();
            var self = WindowsIdentity.GetCurrent().User;
            if (self != null)
            {
                security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }

            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                security);
        }

        private static async Task<string> ReadMessageAsync(Stream stream, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(ReadTimeout);
                var buffer = new byte[2048];
                var collected = new MemoryStream();
                try
                {
                    while (collected.Length <= UnlockNotifyMessage.MaxMessageBytes)
                    {
                        var read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        collected.Write(buffer, 0, read);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    // A client that connected and went quiet is dropped; what arrived so far is used.
                }

                if (collected.Length > UnlockNotifyMessage.MaxMessageBytes)
                {
                    return null;
                }

                return Encoding.UTF8.GetString(collected.ToArray());
            }
        }

        private void Deliver(string line)
        {
            try
            {
                _onMessage(line);
            }
            catch (Exception ex)
            {
                _log("Unlock notify handler failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
            }

            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
        }
    }
}
