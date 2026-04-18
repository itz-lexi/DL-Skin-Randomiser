using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DL_Skin_Randomiser.Services
{
    public sealed class DiscordRichPresenceService : IDisposable
    {
        private const int HandshakeOpcode = 0;
        private const int FrameOpcode = 1;
        private const int CloseOpcode = 2;
        private const string DefaultGitHubUrl = "https://github.com/itz-lexi/DL-Skin-Randomiser";

        private readonly string _clientId;
        private readonly string _gitHubUrl;
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private NamedPipeClientStream? _pipe;
        private bool _isDisposed;

        public DiscordRichPresenceService(string clientId)
        {
            _clientId = clientId.Trim();
            _gitHubUrl = GetAssemblyMetadata("RepositoryUrl") ?? DefaultGitHubUrl;
        }

        public bool IsEnabled => !string.IsNullOrWhiteSpace(_clientId);

        public static DiscordRichPresenceService FromAppConfiguration()
        {
            var clientId = Environment.GetEnvironmentVariable("DL_SKIN_RANDOMISER_DISCORD_CLIENT_ID");
            if (string.IsNullOrWhiteSpace(clientId))
            {
                clientId = GetAssemblyMetadata("DiscordClientId");
            }

            return new DiscordRichPresenceService(clientId ?? "");
        }

        public void ShowLoaded(string profileName, int modCount, int enabledCount)
        {
            SetActivity(
                "Picking tonight's disguise",
                $"{profileName} - hideout wardrobe open");
        }

        public void ShowRerolled(string profileName, int pickCount)
        {
            SetActivity(
                "Shuffling the wardrobe",
                $"{profileName} - fashion crimes pending");
        }

        public void ShowApplied(string profileName, int enabledCount)
        {
            SetActivity(
                "Packing the lookout bag",
                $"{profileName} - ready for the streets");
        }

        public void ShowPlaying(string profileName, int pickCount)
        {
            SetActivity(
                "Heading out from the hideout",
                $"{profileName} - no outfit regrets");
        }

        public void ShowInGame(string profileName, int pickCount)
        {
            SetActivity(
                "In the hideout",
                $"{profileName} - doing important outfit business");
        }

        public void ShowIdle()
        {
            SetActivity("Browsing the wardrobe", "One more reroll. Probably.");
        }

        private void SetActivity(string details, string state)
        {
            if (!IsEnabled || _isDisposed)
                return;

            _ = Task.Run(() => SetActivityAsync(details, state));
        }

        private async Task SetActivityAsync(string details, string state)
        {
            await _mutex.WaitAsync();
            try
            {
                if (_isDisposed || !await EnsureConnectedAsync())
                    return;

                var activity = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = new
                        {
                            details,
                            state,
                            buttons = new[]
                            {
                                new
                                {
                                    label = "View on GitHub",
                                    url = _gitHubUrl
                                }
                            }
                        }
                    },
                    nonce = Guid.NewGuid().ToString("N")
                };

                await WriteFrameAsync(FrameOpcode, activity);
            }
            catch
            {
                ClosePipe();
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task ClearActivityAsync()
        {
            await _mutex.WaitAsync();
            try
            {
                if (!IsEnabled || !await EnsureConnectedAsync())
                    return;

                await WriteFrameAsync(FrameOpcode, new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = (object?)null
                    },
                    nonce = Guid.NewGuid().ToString("N")
                });
            }
            catch
            {
                ClosePipe();
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task<bool> EnsureConnectedAsync()
        {
            if (_pipe?.IsConnected == true)
                return true;

            ClosePipe();

            for (var index = 0; index < 10; index++)
            {
                var pipe = new NamedPipeClientStream(
                    ".",
                    $"discord-ipc-{index}",
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try
                {
                    await pipe.ConnectAsync(timeout.Token);
                    _pipe = pipe;
                    await WriteFrameAsync(HandshakeOpcode, new { v = 1, client_id = _clientId });
                    _ = await ReadFrameAsync(TimeSpan.FromSeconds(2));
                    return true;
                }
                catch
                {
                    pipe.Dispose();
                    _pipe = null;
                }
            }

            return false;
        }

        private async Task WriteFrameAsync(int opcode, object payload)
        {
            if (_pipe is null)
                return;

            var json = JsonSerializer.Serialize(payload);
            var payloadBytes = Encoding.UTF8.GetBytes(json);
            var header = new byte[8];
            BitConverter.GetBytes(opcode).CopyTo(header, 0);
            BitConverter.GetBytes(payloadBytes.Length).CopyTo(header, 4);

            await _pipe.WriteAsync(header);
            await _pipe.WriteAsync(payloadBytes);
            await _pipe.FlushAsync();
        }

        private async Task<string> ReadFrameAsync(TimeSpan timeout)
        {
            if (_pipe is null)
                return "";

            using var timeoutSource = new CancellationTokenSource(timeout);
            var header = await ReadExactlyAsync(8, timeoutSource.Token);
            var length = BitConverter.ToInt32(header, 4);
            if (length <= 0)
                return "";

            var payload = await ReadExactlyAsync(length, timeoutSource.Token);
            return Encoding.UTF8.GetString(payload);
        }

        private async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
        {
            if (_pipe is null)
                return [];

            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await _pipe.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException("Discord IPC closed the connection.");

                offset += read;
            }

            return buffer;
        }

        private static string? GetAssemblyMetadata(string key)
        {
            return Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }

        private void ClosePipe()
        {
            try
            {
                if (_pipe?.IsConnected == true)
                    WriteFrameAsync(CloseOpcode, new { }).GetAwaiter().GetResult();
            }
            catch
            {
                // Discord may close the local pipe first; that is fine.
            }
            finally
            {
                _pipe?.Dispose();
                _pipe = null;
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            ClearActivityAsync().GetAwaiter().GetResult();
            ClosePipe();
            _mutex.Dispose();
        }
    }
}
