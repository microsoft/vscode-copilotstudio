namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.Core.IO;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using AgentFilePath = Microsoft.PowerPlatformLS.Contracts.FileLayout.AgentFilePath;
    using DirectoryPath = Microsoft.PowerPlatformLS.Contracts.Internal.Common.DirectoryPath;
    using FilePath = Microsoft.PowerPlatformLS.Contracts.Internal.Common.FilePath;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    // Test Helper for creating McsWorkspace state and managing documents. 
    internal class World
    {
        private TestLogger Logs { get; } = new();

        // Initialized with some default services.
        // Can update this as needed before calling GetWorkspace(). 
        public ServiceCollection Services { get; } = new ServiceCollection();

        private ILspServices? _lspServices;

        private McsWorkspace? _workspace;

        // IF set, this should point to the workspace directory with settings.yml.
        // If null, then this is just loose files (missing settings.yml). 
        private readonly string? _workspacePath;

        public World(string? workspacePath = null)
        {
            _workspacePath = workspacePath;

            Services.Install(new CoreLspModule(new WatcherLspTransport(MessagesReceived)));
            Services.Install(new McsLspModule());

            Services.AddSingleton<TestLogger>(Logs);
            Services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            Services.AddSingleton<ILspLogger, LspLogger>();
            Services.AddSingleton<ClientInformation>();
            Services.AddSingleton<IClientInformation>(p => p.GetRequiredService<ClientInformation>());
            Services.AddSingleton<IClientInformationInitializer>(p => p.GetRequiredService<ClientInformation>());
            Services.AddSingleton<IClientWorkspaceFileProvider, ClientWorkspaceFileProvider>();
            Services.AddSingleton<IFileProviderFactory, PhysicalFileProviderFactory>();
        }

        public IList<BaseJsonRpcMessage> MessagesReceived { get; } = new List<BaseJsonRpcMessage>();

        public IReadOnlyCollection<T> GetRequiredServices<T>() where T : notnull
        {
            if (_lspServices == null)
            {
                throw new InvalidOperationException($"Test bug: Must create workspace first");
            }
            return _lspServices.GetRequiredServices<T>().ToArray();
        }

        public T GetRequiredService<T>() where T : notnull
        {
            if (_lspServices == null)
            {
                throw new InvalidOperationException($"Test bug: Must create workspace first");
            }
            return _lspServices.GetRequiredService<T>();
        }

        public T GetHandler<T>() where T : IMethodHandler
        {
            return GetRequiredServices<IMethodHandler>().OfType<T>().First();
        }

        private McsWorkspace OpenWorkspaceFromDirectory()
        {
            // ! caller ensured _workspacePath was non-null.
            string workspacePath = _workspacePath!;

            var language = GetRequiredService<ILanguageAbstraction>();

            var workspaceUri = new Uri(workspacePath);
            InitializeClientInfo(new WorkspaceFolder { Uri = workspaceUri, Name = "world" });
            var workspaceFolder = new WorkspaceFolder
            {
                Name = "LocalWorkspace",
                Uri = workspaceUri,
            };

            if (_lspServices == null)
            {
                throw new InvalidOperationException($"LspServices notsetup");
            }

            // find the workspace we just created
            var settingsYml = workspaceUri.ToDirectoryPath().GetChildFilePath("settings.yml");
            var workspace = (McsWorkspace) language.ResolveWorkspace(settingsYml);
            _workspace = workspace;

            return _workspace;
        }

        public McsWorkspace GetWorkspace(string dir)
        {
            if (_lspServices == null)
            {
                _lspServices = new LspServices(Services);

                InitializeClientInfo(new WorkspaceFolder { Uri = new Uri(_workspacePath!), Name = "world" });
            }

            // All services must be registered by this point. 
            // This will call services.BuildServiceProvider()
            var language = GetRequiredService<ILanguageAbstraction>();

            var workspaceUri = new Uri(dir);
            var workspaceFolder = new WorkspaceFolder
            {
                Name = dir,
                Uri = workspaceUri,
            };

            // find the workspace we just created
            var settingsYml = workspaceUri.ToDirectoryPath().GetChildFilePath("settings.yml");
            var workspace = (McsWorkspace)language.ResolveWorkspace(settingsYml);

            workspace.BuildCompilationModel();

            return workspace;
        }

        public McsWorkspace GetWorkspace()
        {
            if (_workspace == null)
            {
                // All services must be registered by this point. 
                // This will call services.BuildServiceProvider()
                _lspServices = new LspServices(Services);
                if (_workspacePath == null)
                {
                    // Workspace is loose documents. 
                    var workspaceFolder = new DirectoryPath(string.Empty);
                    var workspace = new McsWorkspace(workspaceFolder, _lspServices);
                    _workspace = workspace;
                }
                else
                {
                    // Workspace is whole directory. 
                    _workspace = OpenWorkspaceFromDirectory();
                }
            }

            return _workspace;
        }

        /// <summary>
        /// Fake intialize LSP request, which must be invoked before any other request.
        /// </summary>
        private void InitializeClientInfo(WorkspaceFolder root)
        {
            var clientInfo = GetRequiredService<IClientInformationInitializer>();
            clientInfo.Initialize(new InitializeParams
            {
                Capabilities = new ClientCapabilities(),
                WorkspaceFolders = [root]
            });
        }

        public McsLspDocument AddFile(string filename, bool elementCheck = true)
        {
            var text = TestDataReader.GetTestData(filename);
            return AddFile(filename, text, elementCheck);
        }

        public McsLspDocument AddFile(string filename, string text, bool elementCheck = true)
        {
            var docPath = new FilePath("c:/agent/" + filename);
            var doc1 = new McsLspDocument(docPath, text, new DirectoryPath("c:/agent"));

            var workspace = GetWorkspace();
            workspace.AddDocument(doc1);

            // Ensure we can get root. 
            if (elementCheck)
            {
                GetFileElement(doc1);
            }

            return doc1;
        }

        public BotElement GetFileElement(McsLspDocument doc)
        {
            var workspace = GetWorkspace();

            // Ensure current. 
            workspace.BuildCompilationModel();

            // ! called BuildCompilationModel above.
            var analyzer = workspace.CompilationAnalyzer!;

            var rootElement = analyzer.GetDocumentRoot(doc);

            return rootElement;
        }

        public McsLspDocument? GetDocument(string filename)
        {
            var uri = new Uri(filename);

            return GetDocument(uri);
        }

        public McsLspDocument? GetDocument(Uri uri)
        {
            var workspace = GetWorkspace();
            var doc = workspace.GetDocument(uri.ToFilePath());

            return (McsLspDocument?)doc;
        }

        // Get the workspace containing the document
        // Workspace must have been previously loaded. 
        public McsWorkspace GetWorkspace(McsLspDocument doc)
        {
            var docFullPath = doc.FilePath.ToString();
            var docPath = docFullPath.Substring(0, docFullPath.Length - doc.RelativePath.ToString().Length);

            Workspace? workspace = null;
            var language = GetRequiredService<ILanguageAbstraction>();
            foreach (var ws in language.Workspaces)
            {
                if (ws.FolderPath.ToString() == docPath)
                {
                    workspace = ws;
                    break;
                }
            }
            if (workspace == null)
            {
                throw new InvalidOperationException($"Can't find workspace for: {docPath}");
            }

            return (McsWorkspace)workspace;
        }

        // SearchText should have a |  to mark where cursor is. 
        public RequestContext GetRequestContext(McsLspDocument doc, string searchText)
        {
            int cursorIndex = searchText.IndexOf('|');
            if (cursorIndex < 0)
            {
                throw new InvalidOperationException($"searchText is missing | cursor");
            }

            searchText = searchText.Replace("|", "");
            var index = doc.Text.IndexOf(searchText);
            if (index < 0)
            {
                throw new InvalidOperationException($"Document is missing searchText: {searchText}");
            }

            return GetRequestContext(doc, index + cursorIndex);
        }

        public RequestContext GetRequestContext(McsLspDocument doc, int index)
        {
            if (index < 0 || index > doc.Text.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Workspace workspace = _workspace ?? GetWorkspace(doc);

            // ! not used.

            var language = GetRequiredService<ILanguageAbstraction>();

            return new RequestContext(language, workspace, doc, index);
        }

        public BotElement GetElementAtCursor(RequestContext requestContext)
        {
            var doc = (McsLspDocument)requestContext.Document;
            var rootElement = GetFileElement(doc);

            Assert.NotNull(rootElement);

            // ! asserted 
            var fileSyntax = rootElement!.Syntax!;

            int index = requestContext.Index;
            SyntaxNode syntaxNodeAtCursor = fileSyntax.GetSyntaxNodeAtPosition(index);

            var e1 = syntaxNodeAtCursor.GetElement();

            return e1;
        }


        private class MockIsland : IIslandControlPlaneService
        {
            private readonly IReadOnlyDictionary<Guid, McsWorkspace> _workspaces;

            public MockIsland(IReadOnlyDictionary<Guid, McsWorkspace> workspaces)
            {
                _workspaces = workspaces;
            }

            public Task<PvaComponentChangeSet> GetComponentsAsync(AuthoringOperationContextBase operationContext, string? changeToken, CancellationToken cancellationToken)
            {
                var id = operationContext switch
                {
                    BotComponentCollectionAuthoringOperationContext a1 => a1.BotComponentCollectionReference.CdsId,
                    AuthoringOperationContext a2 => a2.BotReference.CdsBotId,
                    _ => throw new NotImplementedException()
                };

                var workspace = _workspaces[id];
                var changeSet = World.GetChangeset(workspace);

                return Task.FromResult(changeSet);
            }

            public Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet pushChangeset, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            // "https://test.agentmanagement.com"
            public void SetIslandBaseEndpoint(string baseEndpoint)
            {
                // Nop.
            }

            public void SetConnectionContext(string baseEndpoint, CoreServicesClusterCategory clusterCategory)
            {
                // Nop.
            }
        }

        // Get an IIslandControlPlaneService that can be used to clone this world.
        // workspaceDirectoryNames should be subdirs for workspaces that we can clone. 
        public IIslandControlPlaneService GetControlIsland(params string[] workspaceDirectoryNames)
        {
            var workspaces = new Dictionary<Guid, McsWorkspace>();

            foreach(var dir in workspaceDirectoryNames)
            {
                var id = GetCdsId(dir);
                var workspace = GetWorkspace(Path.Combine(_workspacePath!, dir));
                workspaces.Add(id, workspace);
            };
            return new MockIsland(workspaces);
        }

        // Get the CDSid for a anget. . 
        public Guid GetCdsId(string workspaceDirectoryName)
        {
            return HashStringToGuid(workspaceDirectoryName);
        }

        private static Guid HashStringToGuid(string input)
        {
            // Used only a single time within this class.
            using MD5 md5 = MD5.Create(); // CodeQL [SM02196] False Positive: Not used for security purposes
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            Guid result = new Guid(hash);

            return result;
        }

        // Create a PvaComponentChangeSet that represents cloning this workspace from the cloud..
        // This means the changeset needs guids, which we get from the mcs\botdefinition.json file.
        // This is used for testing Cloning code.         
        private static PvaComponentChangeSet GetChangeset(McsWorkspace workspace)
        {
            var definition = workspace.Definition;

            // PvaComponentChangeSet needs the ID guids. 
            var baseDefinitionDoc = workspace.GetDocumentOrThrow(new AgentFilePath(".mcs/botdefinition.json"));
            var baseDefinition = (DefinitionBase) baseDefinitionDoc.FileModel!;

            List<BotComponentInsert> inserts = new List<BotComponentInsert>();
            foreach(var component in definition.Components)
            {
                if (baseDefinition.TryGetComponentBySchemaName(component.SchemaNameString, out var grounded))
                {
                    var c2 = component.WithId(grounded.Id);

                    if (grounded.ParentBotComponentCollectionId.HasValue)
                    {
                        c2 = c2.WithParentBotComponentCollectionId(grounded.ParentBotComponentCollectionId);
                    }

                    inserts.Add(new BotComponentInsert(c2));
                }
            }

            string changeToken = "Test Change Token"; // can't be null

            if (definition is BotDefinition bot)
            {
                var botComponentCollectionChanges = new BotComponentCollectionChange[0];
                if (bot.ComponentCollections != null)
                {
                    botComponentCollectionChanges = bot.ComponentCollections.Select(c => new BotComponentCollectionInsert(c)).ToArray();
                }

                return new PvaComponentChangeSet(
                    inserts, // botComponentChanges
                    default,
                    default,
                    default,
                    default,
                    botComponentCollectionChanges,
                    default,
                    default,
                    default,
                    bot.Entity,
                    changeToken);
            }
            if (definition is BotComponentCollectionDefinition ccDef)
            {
                var botComponentCollectionChanges = new BotComponentCollectionChange[]
                {
                    new BotComponentCollectionInsert(ccDef.ComponentCollection)
                };

                return new PvaComponentChangeSet(
                    inserts, // botComponentChanges
                    default,
                    default,
                    default,
                    default,
                    botComponentCollectionChanges,
                    default,
                    default,
                    default,
                    default, // bot.Entity,
                    changeToken);
            }
            throw new NotImplementedException();
        }

        private class WatcherLspTransport : ILspTransport
        {
            private readonly IList<BaseJsonRpcMessage> _messageListener;

            public WatcherLspTransport(IList<BaseJsonRpcMessage> messageListener)
            {
                _messageListener = messageListener;
            }

            public bool IsActive => throw new NotImplementedException();

            public void Dispose()
            {
                throw new NotImplementedException();
            }

            public Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public Task SendAsync<T>(T response, CancellationToken cancellationToken) where T : BaseJsonRpcMessage
            {
                _messageListener.Add(response);
                return Task.CompletedTask;
            }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }
    }
}
