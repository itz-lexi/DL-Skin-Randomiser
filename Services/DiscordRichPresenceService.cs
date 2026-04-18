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
        private readonly Random _random = new();
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
            SetActivity(profileName, [
                ("Picking tonight's disguise", "hideout wardrobe open"),
                ("Consulting the outfit board", "very serious fashion strategy"),
                ("Planning a clean entrance", "no promises on the exit"),
                ("Choosing the least suspicious jacket", "probably still suspicious")
            ]);
        }

        public void ShowRerolled(string profileName, int pickCount)
        {
            SetActivity(profileName, [
                ("Shuffling the wardrobe", "fashion crimes pending"),
                ("Rerolling the closet", "the hideout approves maybe"),
                ("Auditioning disguises", "one of these has to work"),
                ("Letting chaos pick the fit", "style by dice roll")
            ]);
        }

        public void ShowApplied(string profileName, int enabledCount)
        {
            SetActivity(profileName, [
                ("Packing the lookout bag", "ready for the streets"),
                ("Locking in the disguise", "too late to change hats"),
                ("Leaving the mirror alone", "confidence selected"),
                ("Staging the outfit reveal", "dramatic entrance pending")
            ]);
        }

        public void ShowPlaying(string profileName, int pickCount)
        {
            SetActivity(profileName, [
                ("Heading out from the hideout", "no outfit regrets"),
                ("Taking the fit outside", "public safety uncertain"),
                ("Leaving before another reroll", "heroic restraint"),
                ("Stepping into the city", "looking suspiciously curated")
            ]);
        }

        public void ShowInGame(string profileName, int pickCount)
        {
            SetActivity(profileName, [
                ("In the hideout", "doing important outfit business"),
                ("Out causing wardrobe problems", "the streets were warned"),
                ("Field-testing the disguise", "blend in by standing out"),
                ("Busy looking suspicious", "professionally, of course")
            ]);
        }

        public void ShowIdle()
        {
            var line = Pick([
                ("Browsing the wardrobe", "One more reroll. Probably."),
                ("Hovering over the closet", "nothing good happens quickly"),
                ("Waiting in the hideout", "the coat rack fears them"),
                ("Considering poor decisions", "stylishly")
            ]);
            SetActivity(line.Details, line.State);
        }

        private void SetActivity(string profileName, IReadOnlyList<(string Details, string State)> lines)
        {
            var line = Pick(lines);
            SetActivity(line.Details, $"{profileName} - {line.State}");
        }

        private (string Details, string State) Pick(IReadOnlyList<(string Details, string State)> lines)
        {
            if (lines.Count == 0)
                return ("Browsing the wardrobe", "One more reroll. Probably.");

            lock (_random)
            {
                return lines[_random.Next(lines.Count)];
            }
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
                _ = await ReadFrameAsync(TimeSpan.FromSeconds(1));
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
