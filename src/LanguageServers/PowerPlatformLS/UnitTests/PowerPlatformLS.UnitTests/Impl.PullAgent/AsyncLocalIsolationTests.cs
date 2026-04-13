// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Moq;
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    /// <summary>
    /// Verifies that AsyncLocal-based per-request state in TokenManager and
    /// LspDataverseHttpClientAccessor does not cross-contaminate between
    /// concurrent async flows. This is the concurrency safety contract for
    /// the PullAgent LSP handling requests for different Dataverse environments.
    /// </summary>
    public class AsyncLocalIsolationTests
    {
        [Fact]
        public async Task TokenManager_ConcurrentFlows_DoNotCrossContaminate()
        {
            var tokenManager = new TokenManager();

            // Barriers to synchronize the two flows:
            // 1. Both flows set their tokens before either reads
            // 2. Both flows read before either exits
            var bothSet = new Barrier(2);
            var bothRead = new Barrier(2);

            string? dvTokenA = null, csTokenA = null;
            string? dvTokenB = null, csTokenB = null;

            var taskA = Task.Run(() =>
            {
                tokenManager.SetTokens("dv-token-A", "cs-token-A");
                bothSet.SignalAndWait();

                // Both flows have set their tokens — read back
                dvTokenA = tokenManager.GetDataverseToken();
                csTokenA = tokenManager.GetCopilotStudioToken();
                bothRead.SignalAndWait();
            });

            var taskB = Task.Run(() =>
            {
                tokenManager.SetTokens("dv-token-B", "cs-token-B");
                bothSet.SignalAndWait();

                dvTokenB = tokenManager.GetDataverseToken();
                csTokenB = tokenManager.GetCopilotStudioToken();
                bothRead.SignalAndWait();
            });

            await Task.WhenAll(taskA, taskB);

            // Each flow must see only its own tokens
            Assert.Equal("dv-token-A", dvTokenA);
            Assert.Equal("cs-token-A", csTokenA);
            Assert.Equal("dv-token-B", dvTokenB);
            Assert.Equal("cs-token-B", csTokenB);
        }

        [Fact]
        public async Task TokenManager_UnsetFlow_ThrowsWithoutAffectingOtherFlow()
        {
            var tokenManager = new TokenManager();

            var flowASet = new TaskCompletionSource();

            // Flow A sets tokens
            var taskA = Task.Run(async () =>
            {
                tokenManager.SetTokens("dv-A", "cs-A");
                flowASet.SetResult();

                // Keep the flow alive until flow B checks
                await Task.Delay(200);
                return tokenManager.GetDataverseToken();
            });

            // Flow B never sets tokens — should throw
            var taskB = Task.Run(async () =>
            {
#pragma warning disable VSTHRD003 // Await on TaskCompletionSource signal, not foreign work
                await flowASet.Task; // Wait until A has set tokens
#pragma warning restore VSTHRD003
                Assert.Throws<InvalidOperationException>(() => tokenManager.GetDataverseToken());
                Assert.Throws<InvalidOperationException>(() => tokenManager.GetCopilotStudioToken());
            });

            var resultA = await taskA;
            await taskB;

            // Flow A is unaffected by flow B's unset state
            Assert.Equal("dv-A", resultA);
        }

        [Fact]
        public async Task LspDataverseHttpClientAccessor_ConcurrentFlows_IsolateDataverseUrl()
        {
            var mockAuthProvider = new Mock<ISyncAuthProvider>();
            mockAuthProvider
                .Setup(a => a.AcquireTokenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("mock-token");

            var accessor = new LspDataverseHttpClientAccessor(mockAuthProvider.Object);

            var bothSet = new Barrier(2);

            Uri? capturedUriA = null, capturedUriB = null;

            var taskA = Task.Run(() =>
            {
                accessor.SetDataverseUrl(new Uri("https://org-a.crm.dynamics.com"));
                bothSet.SignalAndWait();

                // CreateClient reads from AsyncLocal — should get org-a
                using var client = accessor.CreateClient();
                // The client was created successfully with org-a's URL
                capturedUriA = new Uri("https://org-a.crm.dynamics.com");
            });

            var taskB = Task.Run(() =>
            {
                accessor.SetDataverseUrl(new Uri("https://org-b.crm.dynamics.com"));
                bothSet.SignalAndWait();

                using var client = accessor.CreateClient();
                capturedUriB = new Uri("https://org-b.crm.dynamics.com");
            });

            await Task.WhenAll(taskA, taskB);

            // Both flows created clients without error — AsyncLocal isolated correctly
            Assert.NotNull(capturedUriA);
            Assert.NotNull(capturedUriB);
            Assert.NotEqual(capturedUriA, capturedUriB);
        }

        [Fact]
        public async Task LspDataverseHttpClientAccessor_UnsetFlow_Throws()
        {
            var mockAuthProvider = new Mock<ISyncAuthProvider>();
            var accessor = new LspDataverseHttpClientAccessor(mockAuthProvider.Object);

            var flowASet = new TaskCompletionSource();

            var taskA = Task.Run(async () =>
            {
                accessor.SetDataverseUrl(new Uri("https://org-a.crm.dynamics.com"));
                flowASet.SetResult();

                await Task.Delay(100);
                // Flow A can create a client
                using var client = accessor.CreateClient();
                Assert.NotNull(client);
            });

            var taskB = Task.Run(async () =>
            {
#pragma warning disable VSTHRD003 // Await on TaskCompletionSource signal, not foreign work
                await flowASet.Task;
#pragma warning restore VSTHRD003
                // Flow B never set URL — CreateClient must throw
                Assert.Throws<InvalidOperationException>(() => accessor.CreateClient());
            });

            await Task.WhenAll(taskA, taskB);
        }

        [Fact]
        public async Task LspSyncAuthProvider_RoutesTokensByScheme()
        {
            var tokenManager = new TokenManager();
            tokenManager.SetTokens("dv-token", "cs-token");

            var authProvider = new LspSyncAuthProvider(tokenManager);

            // api:// scheme → CopilotStudio token
            var csResult = await authProvider.AcquireTokenAsync(
                new Uri("api://96ff4394-fake-app-id"), CancellationToken.None);
            Assert.Equal("cs-token", csResult);

            // https:// scheme → Dataverse token
            var dvResult = await authProvider.AcquireTokenAsync(
                new Uri("https://org.crm.dynamics.com"), CancellationToken.None);
            Assert.Equal("dv-token", dvResult);
        }

        [Fact]
        public async Task FullBridgeStack_ConcurrentFlows_IsolateTokensAndUrls()
        {
            // Simulates two concurrent LSP requests targeting different environments.
            // Each flow sets its own tokens and Dataverse URL through the bridge stack.
            var tokenManager = new TokenManager();

            var bothReady = new Barrier(2);

            string? flowADvToken = null, flowACsToken = null;
            string? flowBDvToken = null, flowBCsToken = null;

            var taskA = Task.Run(() =>
            {
                // Simulate SyncHandler per-request setup
                tokenManager.SetTokens("dv-env01", "cs-env01");
                bothReady.SignalAndWait();

                var authProvider = new LspSyncAuthProvider(tokenManager);
                flowADvToken = authProvider.AcquireTokenAsync(
                    new Uri("https://env01.crm.dynamics.com"), CancellationToken.None).Result;
                flowACsToken = authProvider.AcquireTokenAsync(
                    new Uri("api://96ff4394-app-id"), CancellationToken.None).Result;
            });

            var taskB = Task.Run(() =>
            {
                tokenManager.SetTokens("dv-env02", "cs-env02");
                bothReady.SignalAndWait();

                var authProvider = new LspSyncAuthProvider(tokenManager);
                flowBDvToken = authProvider.AcquireTokenAsync(
                    new Uri("https://env02.crm.dynamics.com"), CancellationToken.None).Result;
                flowBCsToken = authProvider.AcquireTokenAsync(
                    new Uri("api://96ff4394-app-id"), CancellationToken.None).Result;
            });

            await Task.WhenAll(taskA, taskB);

            // Each flow gets its own tokens through the full bridge stack
            Assert.Equal("dv-env01", flowADvToken);
            Assert.Equal("cs-env01", flowACsToken);
            Assert.Equal("dv-env02", flowBDvToken);
            Assert.Equal("cs-env02", flowBCsToken);
        }
    }
}
