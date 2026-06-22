// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Moq;
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using Xunit;

    public class LspExceptionHandlerTests
    {
        [Fact]
        public void Handle_OperationCanceled_WhenTokenCancelled_Returns499()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var (code, message) = LspExceptionHandler.Handle(new OperationCanceledException(), new Mock<ILspLogger>().Object, cts.Token);

            Assert.Equal(499, code);
            Assert.Contains("cancelled", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Handle_OperationCanceled_WhenTokenNotCancelled_Returns504()
        {
            var (code, _) = LspExceptionHandler.Handle(new OperationCanceledException("timed out"), new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Equal(504, code);
        }

        [Fact]
        public void Handle_HttpRequestException_Returns502()
        {
            var (code, _) = LspExceptionHandler.Handle(new HttpRequestException("network down"), new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Equal(502, code);
        }

        [Fact]
        public void Handle_FileNotFound_Returns400()
        {
            var (code, _) = LspExceptionHandler.Handle(new FileNotFoundException("missing cache"), new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Equal(400, code);
        }

        [Fact]
        public void Handle_InvalidOperation_Returns400()
        {
            var (code, _) = LspExceptionHandler.Handle(new InvalidOperationException("bad input"), new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Equal(400, code);
        }

        [Fact]
        public void Handle_UnexpectedException_Returns500()
        {
            var (code, _) = LspExceptionHandler.Handle(new Exception("boom"), new Mock<ILspLogger>().Object, CancellationToken.None);

            Assert.Equal(500, code);
        }
    }
}
