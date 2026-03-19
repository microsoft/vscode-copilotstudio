namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution
{
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;

    /// <summary>
    /// Result of executing a single journal step.
    /// </summary>
    public record StepResult(JsonElement? Response, List<JournalNotification>? Notifications);

    /// <summary>
    /// Translates journal steps into LSP requests and captures responses.
    /// </summary>
    public sealed class StepExecutor
    {
        private readonly LspClientTransport _transport;
        private bool _shutdownSent;

        /// <summary>
        /// Default timeout for LSP requests (in milliseconds). Prevents hanging on
        /// requests where the server never responds.
        /// </summary>
        internal const int DefaultRequestTimeoutMs = 10_000;

        /// <summary>
        /// True after a shutdown request has been sent through this executor.
        /// Used by <see cref="LspServerProcess"/> to avoid double-shutdown in DisposeAsync.
        /// </summary>
        public bool ShutdownSent => _shutdownSent;

        public StepExecutor(LspClientTransport transport)
        {
            _transport = transport;
        }

        /// <summary>
        /// Execute a journal step and return the response and any captured notifications.
        /// </summary>
        public async Task<StepResult> ExecuteStepAsync(JournalStep step, CancellationToken cancellationToken = default)
        {
            // Apply a request-level timeout so we never hang indefinitely
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(DefaultRequestTimeoutMs);
            var ct = requestCts.Token;

            JsonElement? response = null;

            switch (step.Step)
            {
                case "initialize":
                    response = await ExecuteInitializeAsync(step, ct);
                    break;

                case "initialized":
                    await _transport.SendNotificationAsync("initialized", new { }, ct);
                    break;

                case "open":
                    await ExecuteDidOpenAsync(step, ct);
                    break;

                case "close":
                    await ExecuteDidCloseAsync(step, ct);
                    break;

                case "change":
                    await ExecuteDidChangeAsync(step, ct);
                    break;

                case "completion":
                    response = await ExecuteCompletionAsync(step, ct);
                    break;

                case "diagnostics":
                    response = await ExecuteDiagnosticsAsync(step, ct);
                    break;

                case "definition":
                    response = await ExecuteDefinitionAsync(step, ct);
                    break;

                case "codeAction":
                    response = await ExecuteCodeActionAsync(step, ct);
                    break;

                case "signatureHelp":
                    response = await ExecuteSignatureHelpAsync(step, ct);
                    break;

                case "semanticTokens":
                    response = await ExecuteSemanticTokensAsync(step, ct);
                    break;

                case "shutdown":
                    try
                    {
                        response = await _transport.SendRequestAsync("shutdown", null, ct);
                    }
                    catch (LspErrorException ex)
                    {
                        // Server returned a JSON-RPC error for shutdown — record the error
                        // response rather than propagating it as an exception. The LSP spec
                        // says shutdown should return result:null, but some servers return
                        // error responses (e.g., TaskCanceledException). We still proceed.
                        response = ex.ErrorData;
                    }

                    _shutdownSent = true;
                    break;

                case "exit":
                    await _transport.SendNotificationAsync("exit", null, ct);
                    break;

                case "waitForNotification":
                    response = await ExecuteWaitForNotificationAsync(step, ct);
                    break;

                default:
                    if (step.Step.StartsWith("__notify:", StringComparison.Ordinal))
                    {
                        // REPL notification: strip prefix and send as notification
                        var notifyMethod = step.Step["__notify:".Length..];
                        await _transport.SendNotificationAsync(notifyMethod, DeserializeParams(step.Params), ct);
                    }
                    else
                    {
                        // Generic request: use step name as method, params as-is
                        response = await _transport.SendRequestAsync(step.Step, DeserializeParams(step.Params), ct);
                    }

                    break;
            }

            // Collect wait-for notifications if specified
            List<JournalNotification>? notifications = null;
            if (step.WaitFor is { Count: > 0 })
            {
                notifications = [];
                foreach (var waitSpec in step.WaitFor)
                {
                    var notificationParams = await _transport.WaitForNotificationAsync(
                        waitSpec.Method, waitSpec.TimeoutMs, cancellationToken);
                    notifications.Add(new JournalNotification
                    {
                        Method = waitSpec.Method,
                        Params = notificationParams,
                    });
                }
            }

            return new StepResult(response, notifications);
        }

        private async Task<JsonElement?> ExecuteInitializeAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params) ?? new
            {
                processId = Environment.ProcessId,
                capabilities = new { },
                rootUri = (string?)null,
                workspaceFolders = (object[]?)null,
            };

            return await _transport.SendRequestAsync("initialize", @params, ct);
        }

        private async Task ExecuteDidOpenAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            await _transport.SendNotificationAsync("textDocument/didOpen", @params, ct);
        }

        private async Task ExecuteDidCloseAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            await _transport.SendNotificationAsync("textDocument/didClose", @params, ct);
        }

        private async Task ExecuteDidChangeAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            await _transport.SendNotificationAsync("textDocument/didChange", @params, ct);
        }

        private async Task<JsonElement?> ExecuteCompletionAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            return await _transport.SendRequestAsync("textDocument/completion", @params, ct);
        }

        private async Task<JsonElement?> ExecuteDiagnosticsAsync(JournalStep step, CancellationToken ct)
        {
            // Diagnostics are typically published as notifications.
            // If params specify a document, wait for a publishDiagnostics notification.
            if (step.Params.HasValue && step.Params.Value.TryGetProperty("waitForMethod", out var methodEl))
            {
                var method = methodEl.GetString() ?? "textDocument/publishDiagnostics";
                var timeout = 10_000;
                if (step.Params.Value.TryGetProperty("timeoutMs", out var timeoutEl))
                {
                    timeout = timeoutEl.GetInt32();
                }

                return await _transport.WaitForNotificationAsync(method, timeout, ct);
            }

            // Fallback: send a pull diagnostics request if supported
            var @params = DeserializeParams(step.Params);
            return await _transport.SendRequestAsync("textDocument/diagnostic", @params, ct);
        }

        private async Task<JsonElement?> ExecuteDefinitionAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            return await _transport.SendRequestAsync("textDocument/definition", @params, ct);
        }

        private async Task<JsonElement?> ExecuteCodeActionAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            return await _transport.SendRequestAsync("textDocument/codeAction", @params, ct);
        }

        private async Task<JsonElement?> ExecuteSignatureHelpAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            return await _transport.SendRequestAsync("textDocument/signatureHelp", @params, ct);
        }

        private async Task<JsonElement?> ExecuteSemanticTokensAsync(JournalStep step, CancellationToken ct)
        {
            var @params = DeserializeParams(step.Params);
            return await _transport.SendRequestAsync("textDocument/semanticTokens/full", @params, ct);
        }

        private async Task<JsonElement?> ExecuteWaitForNotificationAsync(JournalStep step, CancellationToken ct)
        {
            var method = "textDocument/publishDiagnostics";
            var timeout = 10_000;

            if (step.Params.HasValue)
            {
                if (step.Params.Value.TryGetProperty("method", out var methodEl))
                {
                    method = methodEl.GetString() ?? method;
                }

                if (step.Params.Value.TryGetProperty("timeoutMs", out var timeoutEl))
                {
                    timeout = timeoutEl.GetInt32();
                }
            }

            return await _transport.WaitForNotificationAsync(method, timeout, ct);
        }

        private static object? DeserializeParams(JsonElement? element)
        {
            if (!element.HasValue || element.Value.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            return element.Value;
        }
    }
}