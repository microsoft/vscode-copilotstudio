namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport
{
    using System.Collections.Concurrent;
    using System.IO;
    using System.IO.Pipes;
    using System.Linq;
    using System.Net.Sockets;

    /// <summary>
    /// Manages the LSP server process lifecycle: start, stop, and access to the transport.
    /// </summary>
    public sealed class LspServerProcess : IAsyncDisposable
    {
        private readonly string _serverPath;
        private readonly string[] _serverArgs;
        private NamedPipeServerStream? _pipeStream;
        private Socket? _unixListener;
        private Socket? _unixConnection;
        private NetworkStream? _unixStream;
        private string? _unixSocketPath;
        private System.Diagnostics.Process? _process;
        private LspClientTransport? _transport;
        private readonly ConcurrentQueue<string> _stderrLines = new();
        private bool _stopped;

        public LspClientTransport Transport => _transport ?? throw new InvalidOperationException("Server not started.");

        /// <summary>
        /// Lines captured from the server's stderr stream.
        /// </summary>
        public IReadOnlyList<string> StderrLines => _stderrLines.ToArray();

        public LspServerProcess(
            string serverPath,
            string[]? serverArgs = null)
        {
            _serverPath = serverPath;
            _serverArgs = serverArgs ?? [];
        }

        /// <summary>
        /// When true, enables wire-level tracing to stderr.
        /// </summary>
        public bool Verbose { get; set; }


        /// <summary>
        /// Set this to true if the script already sent shutdown+exit.
        /// Prevents <see cref="DisposeAsync"/> from sending a redundant shutdown sequence.
        /// </summary>
        public bool ShutdownAlreadySent { get; set; }

        /// <summary>
        /// Start the LSP server process and establish the transport.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var (args, pipeName) = ResolveTransport();
            InitializePipeServer(pipeName);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _serverPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            _process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start LSP server: {_serverPath}");

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    _stderrLines.Enqueue(e.Data);
                }
            };
            _process.BeginErrorReadLine();

            if (_pipeStream is not null)
            {
                await _pipeStream.WaitForConnectionAsync(cancellationToken);
                _transport = new LspClientTransport(_pipeStream, _pipeStream);
            }
            else
            {
                if (_unixListener is null)
                {
                    throw new InvalidOperationException("Unix socket listener was not initialized.");
                }

                _unixConnection = await _unixListener.AcceptAsync(cancellationToken);
                _unixStream = new NetworkStream(_unixConnection, ownsSocket: true);
                _transport = new LspClientTransport(_unixStream, _unixStream);
            }

            if (Verbose)
            {
                _transport.EnableTrace(Console.Error);
            }


            _transport.StartListening(cancellationToken);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Send shutdown + exit sequence and wait for process to end.
        /// If <paramref name="skipShutdownSequence"/> is true, only waits for exit
        /// (use when the script already sent shutdown+exit).
        /// Idempotent — safe to call multiple times.
        /// </summary>
        public async Task StopAsync(bool skipShutdownSequence = false, CancellationToken cancellationToken = default)
        {
            if (_stopped) return;
            _stopped = true;

            if (_transport is not null && !skipShutdownSequence)
            {
                try
                {
                    // Send shutdown request
                    await _transport.SendRequestAsync("shutdown", null, cancellationToken);

                    // Send exit notification
                    await _transport.SendNotificationAsync("exit", null, cancellationToken);
                }
                catch (Exception)
                {
                    // Best-effort shutdown
                }
            }

            if (_process is not null && !_process.HasExited)
            {
                // If the script already sent shutdown+exit, the server had time during
                // the remaining script execution. A short grace period suffices before
                // force-killing a hung server.
                var gracePeriod = skipShutdownSequence
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(5);

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(gracePeriod);
                    await _process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                    // Wait briefly for the kill to finalize so HasExited/ExitCode are accurate
                    try { await _process.WaitForExitAsync(new CancellationTokenSource(1000).Token); }
                    catch { /* best-effort */ }
                }
            }
        }

        /// <summary>
        /// Get process info for diagnostics (exit code, state).
        /// </summary>
        public string? GetProcessInfo()
        {
            if (_process is null) return null;
            try
            {
                if (_process.HasExited)
                {
                    return $"exited with code {_process.ExitCode}";
                }
                return "still running";
            }
            catch
            {
                return "unknown state";
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(skipShutdownSequence: ShutdownAlreadySent);
            _pipeStream?.Dispose();
            _unixStream?.Dispose();
            _unixListener?.Dispose();
            _unixConnection?.Dispose();
            if (!string.IsNullOrWhiteSpace(_unixSocketPath) && File.Exists(_unixSocketPath))
            {
                File.Delete(_unixSocketPath);
            }
            _process?.Dispose();
        }

        private (string[] Args, string PipeName) ResolveTransport()
        {
            var args = new List<string>(_serverArgs);
            var hasStdio = args.Any(a => string.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase));
            var pipeIndex = args.FindIndex(a => string.Equals(a, "--pipe", StringComparison.OrdinalIgnoreCase));
            var hasPipe = pipeIndex >= 0;
            string? pipeName = null;

            if (hasStdio)
            {
                throw new InvalidOperationException("Stdio transport is not supported by this harness.");
            }

            if (hasPipe)
            {
                pipeName = pipeIndex + 1 < args.Count ? args[pipeIndex + 1] : null;
                if (string.IsNullOrWhiteSpace(pipeName))
                {
                    throw new InvalidOperationException("--pipe requires a pipe name argument.");
                }
            }
            else
            {
                pipeName = OperatingSystem.IsWindows()
                    ? $"PowerPlatformLS_Journal_{Guid.NewGuid():N}"
                    : Path.Combine(Path.GetTempPath(), $"PowerPlatformLS_Journal_{Guid.NewGuid():N}.sock");
                args.Add("--pipe");
                args.Add(pipeName);
            }

            return (args.ToArray(), pipeName);
        }

        private void InitializePipeServer(string pipeName)
        {
            if (OperatingSystem.IsWindows())
            {
                var normalized = NormalizeWindowsPipeName(pipeName);
                _pipeStream = new NamedPipeServerStream(
                    normalized,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                return;
            }

            _unixSocketPath = ResolveUnixSocketPath(pipeName);
            if (File.Exists(_unixSocketPath))
            {
                File.Delete(_unixSocketPath);
            }

            _unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _unixListener.Bind(new UnixDomainSocketEndPoint(_unixSocketPath));
            _unixListener.Listen(1);
        }

        private static string NormalizeWindowsPipeName(string pipeName)
        {
            const string Prefix = "\\\\.\\pipe\\";
            return pipeName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                ? pipeName[Prefix.Length..]
                : pipeName;
        }

        private static string ResolveUnixSocketPath(string pipeName)
        {
            if (Path.IsPathRooted(pipeName))
            {
                return pipeName;
            }

            return Path.Combine(Path.GetTempPath(), pipeName);
        }
    }
}