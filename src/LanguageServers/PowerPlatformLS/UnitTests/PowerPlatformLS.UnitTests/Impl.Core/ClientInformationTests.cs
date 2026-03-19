namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using System.Collections.Generic;
    using System;
    using Xunit;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.Linq;

    public class ClientInformationTests
    {
        private ClientInformation CreateEmptyClientInfo() => new ClientInformation(new LspLogger(new TestLogger<LspLogger>(new TestLogger())));

        [Fact]
        public void Success_DisjointFolders_InOut()
        {
            var info = CreateEmptyClientInfo();
            var request = new InitializeParams
            {
                Capabilities = new ClientCapabilities(),
                WorkspaceFolders =
                [
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Projects")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Data")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Documents")
                    },
                ],
            };
            info.Initialize(request);

            // items are sorted by length
            Assert.Equal(["C:/Data/", "C:/Projects/", "C:/Documents/"], info.WorkspaceFolders.Select(x => x.ToString()));
        }


        [Fact]
        public void Success_OverlappingFolders_In_DisjointFolders_Out()
        {
            var info = CreateEmptyClientInfo();
            var request = new InitializeParams
            {
                Capabilities = new ClientCapabilities(),
                WorkspaceFolders =
                [
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Projects")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Projects")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Projects/Y")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Data")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Documents")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Documents/TopSecret")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Data/SomeFiles/MyData")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Data/SomeFiles/FooData")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Data/SomeFiles/BarData")
                    },
                    new WorkspaceFolder
                    {
                        Name = "projects",
                        Uri = new Uri("file:///C:/Projects/X")
                    },
                ],
            };
            info.Initialize(request);

            // items are sorted by length
            Assert.Equal(["C:/Data/", "C:/Projects/", "C:/Documents/"], info.WorkspaceFolders.Select(x => x.ToString()));
        }
    }
}
