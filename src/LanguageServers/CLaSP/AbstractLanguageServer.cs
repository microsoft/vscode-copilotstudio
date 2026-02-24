// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public abstract class AbstractLanguageServer<TRequestContext>
    {
        protected readonly ILspLogger Logger;

        /// <summary>
        /// These are lazy to allow implementations to define custom variables that are used by
        /// <see cref="ConstructRequestExecutionQueue"/> or <see cref="ConstructLspServices"/>
        /// </summary>
        private readonly Lazy<IRequestExecutionQueue<TRequestContext>> _queue;
        private readonly IJsonRpcStream _jsonRpcStream;
        private readonly Lazy<ILspServices> _lspServices;
        private readonly Lazy<AbstractHandlerProvider> _handlerProvider;

        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Ensures that we only run shutdown and exit code once in order.
        /// Guards access to <see cref="_shutdownRequestTask"/> and <see cref="_exitNotificationTask"/>
        /// </summary>
        private readonly object _lifeCycleLock = new();

        /// <summary>
        /// Task representing the work done on LSP server shutdown.
        /// </summary>
        private Task? _shutdownRequestTask;

        /// <summary>
        /// Task representing the work down on LSP exit.
        /// </summary>
        private Task? _exitNotificationTask;

        /// <summary>
        /// Task completion source that is started when the server starts and completes when the server exits.
        /// Used when callers need to wait for the server to cleanup.
        /// </summary>
        private readonly TaskCompletionSource<object?> _serverExitedSource = new();

        public AbstractTypeRefResolver TypeRefResolver { get; }

        protected AbstractLanguageServer(
            IJsonRpcStream jsonRpcStream,
            ILspLogger logger,
            AbstractTypeRefResolver? typeRefResolver)
        {
            Logger = logger;
            TypeRefResolver = typeRefResolver ?? TypeRef.DefaultResolver.Instance;

            _jsonRpcStream = jsonRpcStream;
            _jsonRpcStream.DisconnectServerAction = SignalStreamDisconnectedAsync;
            _lspServices = new Lazy<ILspServices>(() => ConstructLspServices(new ShutdownHandler(this), new ExitHandler(this)));
            _queue = new Lazy<IRequestExecutionQueue<TRequestContext>>(() => ConstructRequestExecutionQueue());
            _handlerProvider = new Lazy<AbstractHandlerProvider>(() =>
            {
                var lspServices = _lspServices.Value;
                var handlerProvider = new HandlerProvider(lspServices, TypeRefResolver);
                SetupRequestDispatcher(handlerProvider);
                return handlerProvider;
            });
        }

        /// <summary>
        /// Initializes the LanguageServer.
        /// </summary>
        /// <remarks>Should be called at the bottom of the implementing constructor or immediately after construction.</remarks>
        public void Initialize()
        {
            GetRequestExecutionQueue();
        }

        /// <summary>
        /// Extension point to allow creation of <see cref="ILspServices"/> since that can't always be handled in the constructor.
        /// </summary>
        /// <returns>An <see cref="ILspServices"/> instance for this server.</returns>
        /// <remarks>This should only be called once, and then cached.</remarks>
        protected abstract ILspServices ConstructLspServices(IMethodHandler shutdownHandler, IMethodHandler exitHandler);

        protected virtual AbstractHandlerProvider HandlerProvider
        {
            get
            {
                return _handlerProvider.Value;
            }
        }

        public ILspServices GetLspServices() => _lspServices.Value;

        protected virtual void SetupRequestDispatcher(AbstractHandlerProvider handlerProvider)
        {
            // Get unique set of methods from the handler provider for the default language.
            foreach (var methodGroup in handlerProvider
                .GetRegisteredMethods()
                .Append(new RequestHandlerMetadata("exit", null, null, LanguageServerConstants.DefaultLanguageName))
                .Append(new RequestHandlerMetadata("shutdown", null, null, LanguageServerConstants.DefaultLanguageName))
                .GroupBy(m => m.MethodName))
            {
                // Instead of concretely defining methods for each LSP method, we instead dynamically construct the
                // generic method info from the exported handler types.  This allows us to define multiple handlers for
                // the same method but different type parameters.  This is a key functionality to support LSP extensibility
                // in cases like XAML, TS to allow them to use different LSP type definitions

                // Verify that we are not mixing different numbers of request parameters and responses between different language handlers
                // e.g. it is not allowed to have a method have both a parameterless and regular parameter handler.
                var requestTypes = methodGroup.Select(m => m.RequestTypeRef);
                var responseTypes = methodGroup.Select(m => m.ResponseTypeRef);
                if (!AllTypesMatch(requestTypes))
                {
                    throw new InvalidOperationException($"Language specific handlers for {methodGroup.Key} have mis-matched number of parameters:{Environment.NewLine}{string.Join(Environment.NewLine, methodGroup)}");
                }

                if (!AllTypesMatch(responseTypes))
                {
                    throw new InvalidOperationException($"Language specific handlers for {methodGroup.Key} have mis-matched number of returns:{Environment.NewLine}{string.Join(Environment.NewLine, methodGroup)}");
                }

                var delegatingEntryPoint = CreateDelegatingEntryPoint(methodGroup.Key);

                // We verified above that parameters match, set flag if this request has parameters or is parameterless so we can set the entrypoint correctly.
                // Initial CLaSP implementation pass a method with or without parameter. StreamJsonRpc needs reflection to know what to send to the method.
                // Our implementation always assume we need to pass "Params", even when it's null, which has the same outcome, without the need for Reflection.
                // var hasParameters = methodGroup.First().RequestTypeRef != null;
                var hasParameters = true;
                var entryPoint = delegatingEntryPoint.GetEntryPoint(hasParameters);
                _jsonRpcStream.AddLocalRpcMethod(entryPoint, delegatingEntryPoint, methodGroup.Key);
            }

            static bool AllTypesMatch(IEnumerable<TypeRef?> typeRefs)
            {
                if (typeRefs.All(r => r is null) || typeRefs.All(r => r is not null))
                {
                    return true;
                }

                return false;
            }
        }

        private class LocalMethodHandler : INotificationHandler<TRequestContext>
        {
            public LocalMethodHandler(Func<Task> action)
            {
                _action = action;
            }

            private readonly Func<Task> _action;

            public bool MutatesSolutionState => true;

            public async Task HandleNotificationAsync(TRequestContext requestContext, CancellationToken cancellationToken)
            {
                await _action();
            }
        }

        [LanguageServerEndpoint("shutdown", LanguageServerConstants.DefaultLanguageName)]
        private class ShutdownHandler : LocalMethodHandler
        {
            public ShutdownHandler(AbstractLanguageServer<TRequestContext> server)
                : base(async () => await server.ShutdownAsync())
            {
            }
        }

        [LanguageServerEndpoint("exit", LanguageServerConstants.DefaultLanguageName)]
        private class ExitHandler : LocalMethodHandler
        {
            public ExitHandler(AbstractLanguageServer<TRequestContext> server)
                : base(async () => await server.ExitAsync())
            {
            }
        }

        public virtual void OnInitialized()
        {
            IsInitialized = true;
        }

        protected virtual IRequestExecutionQueue<TRequestContext> ConstructRequestExecutionQueue()
        {
            var handlerProvider = HandlerProvider;
            var queue = new RequestExecutionQueue<TRequestContext>(this, Logger, handlerProvider);

            queue.Start();

            return queue;
        }

        protected IRequestExecutionQueue<TRequestContext> GetRequestExecutionQueue()
        {
            return _queue.Value;
        }

        public virtual bool TryGetLanguageForRequest(string methodName, object? serializedRequest, [NotNullWhen(true)] out string? language)
        {
            Logger.LogInformation($"Using default language handler for {methodName}");
            language = LanguageServerConstants.DefaultLanguageName;
            return true;
        }

        protected abstract DelegatingEntryPoint<TRequestContext> CreateDelegatingEntryPoint(string method);

        public abstract TRequest DeserializeRequest<TRequest>(object? serializedRequest, RequestHandlerMetadata metadata);

        public Task WaitForExitAsync()
        {
            lock (_lifeCycleLock)
            {
                // Ensure we've actually been asked to shutdown before waiting.
                if (_shutdownRequestTask == null)
                {
                    throw new InvalidOperationException("The language server has not yet been asked to shutdown.");
                }
            }

            // Note - we return the _serverExitedSource task here instead of the _exitNotification task as we may not have
            // finished processing the exit notification before a client calls into us asking to restart.
            // This is because unlike shutdown, exit is a notification where clients do not need to wait for a response.
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            return _serverExitedSource.Task;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
        }

        /// <summary>
        /// Tells the LSP server to stop handling any more incoming messages (other than exit).
        /// Typically called from an LSP shutdown request.
        /// </summary>
        public Task ShutdownAsync(string message = "Shutting down")
        {
            Task shutdownTask;
            lock (_lifeCycleLock)
            {
                // Run shutdown or return the already running shutdown request.
                _shutdownRequestTask ??= Shutdown_NoLockAsync(message);
                shutdownTask = _shutdownRequestTask;
                return shutdownTask;
            }

            // Runs the actual shutdown outside of the lock - guaranteed to be only called once by the above code.
            async Task Shutdown_NoLockAsync(string message)
            {
                // Immediately yield so that this does not run under the lock.
                await Task.Yield();

                Logger.LogInformation(message);

                // Allow implementations to do any additional cleanup on shutdown.
                var lifeCycleManager = GetLspServices().GetRequiredService<ILifeCycleManager>();
                await lifeCycleManager.ShutdownAsync(message).ConfigureAwait(false);

                // Note: we intentionally do NOT dispose the request execution queue here.
                // Disposing the queue from within a queue handler (the ShutdownHandler) causes
                // a self-cancellation race: _cancelSource.Cancel() fires the cancellation
                // callback on the shutdown item's own _completionSource (TrySetCanceled) before
                // the handler returns and TrySetResult can run, producing a nondeterministic
                // -32603 error instead of a clean null response.  Additionally, completing the
                // queue here prevents the subsequent 'exit' notification from being enqueued,
                // so ExitAsync never runs and the server process hangs until force-killed.
                //
                // ExitAsync already calls ShutdownRequestExecutionQueueAsync, so the queue is
                // properly disposed when the exit notification arrives.
            }
        }

        /// <summary>
        /// Tells the LSP server to exit.  Requires that <see cref="ShutdownAsync(string)"/> was called first.
        /// Typically called from an LSP exit notification.
        /// </summary>
        public Task ExitAsync()
        {
            Task exitTask;
            lock (_lifeCycleLock)
            {
                if (_shutdownRequestTask?.IsCompleted != true)
                {
                    throw new InvalidOperationException("The language server has not yet been asked to shutdown or has not finished shutting down.");
                }

                // Run exit or return the already running exit request.
                _exitNotificationTask ??= Exit_NoLockAsync();
                exitTask = _exitNotificationTask;
                return exitTask;
            }

            // Runs the actual exit outside of the lock - guaranteed to be only called once by the above code.
            async Task Exit_NoLockAsync()
            {
                // Immediately yield so that this does not run under the lock.
                await Task.Yield();

                try
                {
                    var lspServices = GetLspServices();

                    // Allow implementations to do any additional cleanup on exit.
                    var lifeCycleManager = lspServices.GetRequiredService<ILifeCycleManager>();
                    await lifeCycleManager.ExitAsync().ConfigureAwait(false);

                    await ShutdownRequestExecutionQueueAsync().ConfigureAwait(false);

                    lspServices.Dispose();
                }
                catch (Exception)
                {
                    // Swallow exceptions thrown by disposing our JsonRpc object. Disconnected events can potentially throw their own exceptions so
                    // we purposefully ignore all of those exceptions in an effort to shutdown gracefully.
                }
                finally
                {
                    Logger.LogInformation("Exiting server");
                    _serverExitedSource.TrySetResult(null);
                }
            }
        }

        private ValueTask ShutdownRequestExecutionQueueAsync()
        {
            var queue = GetRequestExecutionQueue();
            return queue.DisposeAsync();
        }

        /// <summary>
        /// Cleanup the server if we encounter a json rpc disconnect so that we can be restarted later.
        /// </summary>
        private async Task SignalStreamDisconnectedAsync(object? sender)
        {
            // It is possible this gets called during normal shutdown and exit.
            // ShutdownAsync and ExitAsync will no-op if shutdown was already triggered by something else.
            await ShutdownAsync(message: "Shutdown triggered by JsonRpc disconnect").ConfigureAwait(false);
            await ExitAsync().ConfigureAwait(false);
        }

        internal TestAccessor GetTestAccessor()
        {
            return new(this);
        }

        internal readonly struct TestAccessor
        {
            private readonly AbstractLanguageServer<TRequestContext> _server;

            internal TestAccessor(AbstractLanguageServer<TRequestContext> server)
            {
                _server = server;
            }

            public T GetRequiredLspService<T>() where T : class => _server.GetLspServices().GetRequiredService<T>();

            internal RequestExecutionQueue<TRequestContext>.TestAccessor? GetQueueAccessor()
            {
                if (_server._queue.Value is RequestExecutionQueue<TRequestContext> requestExecution)
                    return requestExecution.GetTestAccessor();

                return null;
            }

            internal bool HasShutdownStarted()
            {
                return GetShutdownTaskAsync() != null;
            }

            internal Task? GetShutdownTaskAsync()
            {
                lock (_server._lifeCycleLock)
                {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
                    return _server._shutdownRequestTask;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
                }
            }
        }
    }
}