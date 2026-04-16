namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.DependencyInjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class ChangeMethodTests
    {
        private const string EnvironmentId = "TestEnvironment";
        private const string AccountId = "testAccount";
        private const string AccountEmail = "testEmail";
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AgentManagementUrl = "https://test.agentmanagement.com";
        private const string CopilotStudioToken = "CopilotStudioToken";
        private const string DataverseToken = "DataverseToken";

        [Fact]
        public async Task FileFilter_OnDidChangeWatchedFiles_Async()
        {
            (string filepath, bool isFiltered)[] testData =
            [
                ("file:///c:/path/topics/Topic1.mcs.yml", false),
                ("file:///c:/path/topics/Topic2.mcs.yaml", false),
                ("file:///c:/path/topics/Topic3.mcs.YML", false),
                ("file:///c:/path/data/readme.md", true),
                ("file:///c:/path/scripts/script.py", true),
                ("file:///c:/path/data/test_list.txt", true),
            ];
            TestLogger logs;
            await using (var context = new TestHost([new McsLspModule(), new PassRequestModule(), new TestFileModule()]))
            {
                await context.InitializeLanguageServerAsync();
                var changeParams = new DidChangeWatchedFilesParams
                {
                    Changes = testData.Select(x => new FileEvent
                    {
                        Uri = new Uri(x.filepath),
                        // arbitrary type - files don't exist anyway
                        Type = FileChangeType.Changed,
                    }).ToArray()
                };
                var changeMessage = JsonRpc.CreateMessage(LspMethods.DidChangeWatchedFiles, changeParams);
                context.TestStream.WriteMessage(changeMessage);
                logs = context.Logs;

                // make sure notification are not cancelled
                await PassRequestModule.AssertPassAsync(context);
            }

            // request completed without error
            Assert.Empty(logs.Error);

            foreach (var entry in testData)
            {
                if (entry.isFiltered)
                {
                    Assert.Single(logs.Info.Where(x => x == $"Client notified 'Changed' event on watched files that has no language definition: {entry.filepath.Split('/')[^1]}. Change won't be tracked."));
                }
                else
                {
                    Assert.Single(logs.Warning.Where(x => x == $"Can't process 'Changed' event for '{entry.filepath.Split('/')[^1]}': The file does not exist."));
                }
            }
        }

        [Fact]
        public async Task IgnoreFileRenaming_OnDidChangeWatchedFiles_Async()
        {
            (string filepath, FileChangeType changeType, bool shouldIgnore)[] testData =
            [
                ("file:///c:/path/topics/Topic1.mcs.yml", FileChangeType.Deleted, true),
                ("file:///c:/path/topics/TOPIC1.mcs.yml", FileChangeType.Created, true),
                ("file:///c:/path/topics/Topic3.mcs.yml", FileChangeType.Deleted, false),
                ("file:///c:/path/topics/Topic4.mcs.yml", FileChangeType.Created, false),
            ];
            TestLogger logs;
            await using (var context = new TestHost([new McsLspModule(), new PassRequestModule(), new TestFileModule()]))
            {
                await context.InitializeLanguageServerAsync();
                var changeParams = new DidChangeWatchedFilesParams
                {
                    Changes = testData.Select(x => new FileEvent
                    {
                        Uri = new Uri(x.filepath),
                        Type = x.changeType,
                    }).ToArray()
                };
                var changeMessage = JsonRpc.CreateMessage(LspMethods.DidChangeWatchedFiles, changeParams);
                context.TestStream.WriteMessage(changeMessage);
                logs = context.Logs;

                // make sure notification are not cancelled
                await PassRequestModule.AssertPassAsync(context);
            }

            Assert.Empty(logs.Error);

            foreach (var entry in testData)
            {
                var filename = Path.GetFileName(entry.filepath);

                if (entry.shouldIgnore)
                {
                    // Ignored files should not get processed at all - no warning or info logs.
                    Assert.False(logs.Warning.Any(l => l.Contains(filename)) || logs.Info.Any(l => l.Contains(filename)));
                }
                else
                {
                    Assert.True(logs.Info.Any(l => l.Contains(filename)) || logs.Warning.Any(l => l.Contains(filename)));
                }
            }
        }

        [Fact]
        public async Task Success_OnCreateAndRenameWatchedFile_Async()
        {
            TestLogger logs;
            await using (var context = new TestHost([new McsLspModule(), new PassRequestModule()]))
            {
                await context.InitializeLanguageServerAsync(Path.GetFullPath("TestData/DidChange"));

                // Create file
                {
                    var changeParams = new DidChangeWatchedFilesParams
                    {
                        Changes =
                        [
                            new FileEvent
                            {
                                Uri = new Uri(Path.GetFullPath("TestData/DidChange/BrokenDialog.mcs.yml")),
                                Type = FileChangeType.Created
                            }
                        ]
                    };
                    var message = JsonRpc.CreateMessage(LspMethods.DidChangeWatchedFiles, changeParams);
                    context.TestStream.WriteMessage(message);
                }

                await AssertDiagnosticsAsync(context, [
                    ("TestData/DidChange/BrokenDialog.mcs.yml", 6),
                    ("TestData/DidChange/agent.mcs.yml", 0),
                ]);

                // Rename file
                {
                    var renameFileParams = new DidChangeWatchedFilesParams
                    {
                        Changes =
                        [
                            new FileEvent
                    {
                        Uri = new Uri(Path.GetFullPath("TestData/DidChange/BrokenDialog.mcs.yml")),
                        Type = FileChangeType.Deleted
                    },
                    new FileEvent
                    {
                        Uri = new Uri(Path.GetFullPath("TestData/DidChange/AdaptiveDialog.mcs.yml")),
                        Type = FileChangeType.Created
                    }
                        ]
                    };
                    var renameFileMessage = JsonRpc.CreateMessage(LspMethods.DidChangeWatchedFiles, renameFileParams);
                    context.TestStream.WriteMessage(renameFileMessage);
                }

                await AssertDiagnosticsAsync(context, [
                    // cleared after rename
                    ("TestData/DidChange/BrokenDialog.mcs.yml", 0),
                    // 1 diagnostic for not in topic folder
                    ("TestData/DidChange/AdaptiveDialog.mcs.yml", 1),
                    ("TestData/DidChange/agent.mcs.yml", 0),
                ]);

                logs = context.Logs;
            }

            // both request completed without error
            Assert.Empty(logs.Error);
            Assert.Equal(2, logs.Info.Count(x => x.Contains("EndContext: workspace/didChangeWatchedFiles")));
        }

        /// <summary>
        /// Change file that doesn't exist
        /// </summary>
        [Fact]
        public async Task Warning_OnFileNotFoundHasChanged_Async()
        {
            TestLogger logs;
            await using (var context = new TestHost([new McsLspModule(), new PassRequestModule()]))
            {
                await context.InitializeLanguageServerAsync();
                var changeParams = new DidChangeWatchedFilesParams
                {
                    Changes =
                        [
                            new FileEvent
                                {
                                    Uri = new Uri(Path.GetFullPath("TestData/NotAFile.yml")),
                                    Type = FileChangeType.Changed
                                }
                        ]
                };
                var changeMessage = JsonRpc.CreateMessage(LspMethods.DidChangeWatchedFiles, changeParams);
                context.TestStream.WriteMessage(changeMessage);

                logs = context.Logs;

                // make sure notification are not cancelled
                await PassRequestModule.AssertPassAsync(context);
            }

            Assert.Empty(logs.Error);
            Assert.Contains(logs.Warning, x => x.EndsWith("The file does not exist."));
            Assert.Single(logs.Info.Where(x => x.Contains("EndContext: workspace/didChangeWatchedFiles")));
        }

        /// <summary>
        /// E2E test for icon change.
        /// 1. Check localChanges - make sure icon is synced.
        /// 2. change icon in the workspace., signal change through didChangeWatchedFiles.
        /// 3. Check that the icon is updated in the localChanges.
        /// 4. Clean up, revert icon change.
        /// </summary>
        [Fact]
        public async Task Success_OnIconChange_Async()
        {
            TestLogger logs;
            var workspacePath = Path.GetFullPath("TestData/WorkspaceWithSubAgents");
            var iconPath = Path.Combine(workspacePath, "icon.png");
            var diffLocalRequest = new DiffLocalRequest
            {
                WorkspaceUri = new Uri(workspacePath),
                AccountInfo = new AccountInfo
                {
                    AccountId = AccountId,
                    TenantId = Guid.NewGuid(),
                    AccountEmail = AccountEmail
                },
                EnvironmentInfo = new EnvironmentInfo
                {
                    DataverseUrl = DataverseUrl,
                    AgentManagementUrl = AgentManagementUrl,
                    EnvironmentId = EnvironmentId,
                    DisplayName = "Test Environment"
                },
                SolutionVersions = new SolutionInfo
                {
                    CopilotStudioSolutionVersion = new Version(1, 0, 0, 0)
                },
                CopilotStudioAccessToken = CopilotStudioToken,
                DataverseAccessToken = DataverseToken
            };
            var getLocalChangesMessage = JsonRpc.CreateRequestMessage(Constants.JsonRpcMethods.GetLocalChanges, diffLocalRequest);

            // make sure icon was not altered
            const string OriginalIcon = "aGVsbG8=";
            byte[] iconBytes = Convert.FromBase64String(OriginalIcon);
            File.WriteAllBytes(iconPath, iconBytes);

            try
            {
                await using var context = new TestHost([new McsLspModule(), new PullAgentLspModule(new BuildVersionInfo { VsixVersion = "1.0.0-test", Hash = "pullAgentLsp" })]);
                await context.InitializeLanguageServerAsync(workspacePath);

                // run first sync check - this will force compilation of current workspace
                context.TestStream.WriteMessage(getLocalChangesMessage);
                var localChangesBeforeFileChange = await context.GetResponseAsync([Constants.JsonRpcMethods.GetLocalChanges]) as JsonRpcResponse;
                Assert.NotNull(localChangesBeforeFileChange);
                var response = JsonRpc.GetValidResult<SyncAgentResponse>(localChangesBeforeFileChange);
                Assert.False(response.LocalChanges.Any(x => x.Uri.EndsWith("icon.png")));

                // Change icon file on disk
                var newIconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header as dummy content
                File.WriteAllBytes(iconPath, newIconBytes);

                // Check localChanges - make sure icon is still synced internally.
                context.TestStream.WriteMessage(getLocalChangesMessage);
                var localChangesAfterFileChange = await context.GetResponseAsync([Constants.JsonRpcMethods.GetLocalChanges]) as JsonRpcResponse;
                Assert.NotNull(localChangesAfterFileChange);
                response = JsonRpc.GetValidResult<SyncAgentResponse>(localChangesBeforeFileChange);
                Assert.False(response.LocalChanges.Any(x => x.Uri.EndsWith("icon.png")));

                // Signal change through DidChangeWatchedFiles
                var changeParams = new DidChangeWatchedFilesParams
                {
                    Changes =
                [
                    new FileEvent
                        {
                            Uri = new Uri(iconPath),
                            Type = FileChangeType.Changed
                        }
                ]
                };
                var changeMessage = JsonRpc.CreateMessage(LspMethods.DidChangeWatchedFiles, changeParams);
                context.TestStream.WriteMessage(changeMessage);

                // Check that the icon is updated in the localChanges
                context.TestStream.WriteMessage(getLocalChangesMessage);
                var localChangesAfterFileWatcher = await context.GetResponseAsync([Constants.JsonRpcMethods.GetLocalChanges]) as JsonRpcResponse;
                Assert.NotNull(localChangesAfterFileWatcher);
                var response2 = JsonRpc.GetValidResult<SyncAgentResponse>(localChangesAfterFileWatcher);
                Assert.True(response2.LocalChanges.Any(x => x.Uri.EndsWith("icon.png")));

                logs = context.Logs;
            }
            finally
            {
                // 4. Clean up, revert icon change
                File.WriteAllBytes(iconPath, iconBytes);
            }

            Assert.Empty(logs.Error);
        }

        [Fact]
        public async Task Success_OnFileChange_Async()
        {
            TestLogger logs;
            await using (var context = new TestHost([new McsLspModule(), new PassRequestModule()]))
            {
                await context.InitializeLanguageServerAsync(Path.GetFullPath("TestData/DidChange"));
                var changeParams = new DidChangeWatchedFilesParams
                {
                    Changes =
                    [
                        new FileEvent
                        {
                            Uri = new Uri(Path.GetFullPath("TestData/DidChange/AdaptiveDialog.mcs.yml")),
                            Type = FileChangeType.Changed
                        }
                    ]
                };
                var changeMessage = JsonRpc.CreateMessage(LspMethods.DidChangeWatchedFiles, changeParams);
                context.TestStream.WriteMessage(changeMessage);
                logs = context.Logs;

                await AssertDiagnosticsAsync(context, [
                    // 1 warning for not in topic folder
                    ("TestData/DidChange/AdaptiveDialog.mcs.yml", 1),
                    ("TestData/DidChange/agent.mcs.yml", 0),
                ]);
            }

            // request completed without error
            Assert.Empty(logs.Error);
            Assert.Single(logs.Info.Where(x => x.Contains("EndContext: workspace/didChangeWatchedFiles")));
        }

        private async Task AssertDiagnosticsAsync(TestHost context, (string uriEnd, int count)[] expected)
        {
            for (int idx = 0; idx < expected.Length; idx++)
            {
                var response = await context.GetResponseAsync([LspMethods.Diagnostics]);
                var diagMsg = response as LspJsonRpcMessage;
                Assert.NotNull(diagMsg);
                Assert.Equal(LspMethods.Diagnostics, diagMsg!.Method);
                var diagnosticsParams = JsonRpc.GetValidParams<DiagnosticsParams>(diagMsg);

                // must match the expected uri end
                var expectedDiagnostics = expected.Where(x => diagnosticsParams.Uri.ToFilePath().ToString().EndsWith(x.uriEnd));
                if (!expectedDiagnostics.Any())
                {
                    Assert.True(false, $"Received unexpected diagnostic for: {diagnosticsParams.Uri}");
                }

                if (expectedDiagnostics.Count() > 1)
                {
                    Assert.True(false, $"{diagnosticsParams.Uri} matches multiple expected values. Review test fixture.");
                }

                Assert.Equal(expectedDiagnostics.Single().count, diagnosticsParams.Diagnostics.Length);
            }

            await PassRequestModule.AssertPassAsync(context);
        }
    }
}
