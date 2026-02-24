namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio.Methods
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Range = PowerPlatformLS.Contracts.Lsp.Models.Range;

    public class OpenMethodTests
    {
        [Fact]
        public async Task Success_OnNestedAgentDirectoriesResolving_Async()
        {
            var testFileModule = new TestFileModule();
            await using var context = new TestHost([new McsLspModule(), testFileModule]);
            await context.InitializeLanguageServerAsync();

            // open a topic doc in children directory first : initialize agent directory in ws/topics
            {
                var documentUri = new Uri("file:///c:/ws/topics/test.mcs.yml");
                var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
                Assert.Empty(diagParams.Diagnostics.Where(x => x.Severity < DiagnosticSeverity.Warning));
                AssertAgentResolvingEvent(context, "Agent Directory initialized at: 'c:/ws/topics/'");
                AssertAgentDirectoryChangeNotification(context);
                context.Logs.Clear();
            }

            // open a topic doc in parent directory : initialize a 2nd agent directory in ws
            {
                var documentUri = new Uri("file:///c:/ws/test.mcs.yml");
                var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
                Assert.Empty(diagParams.Diagnostics.Where(x => x.Severity < DiagnosticSeverity.Warning));
                AssertAgentResolvingEvent(context, "Agent Directory initialized at: 'c:/ws/'");
                AssertAgentDirectoryChangeNotification(context);
                context.Logs.Clear();
            }

            // open first topic doc again, see that it resolves to the first agent directory still
            {
                var documentUri = new Uri("file:///c:/ws/topics/test.mcs.yml");
                var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
                Assert.Empty(diagParams.Diagnostics.Where(x => x.Severity < DiagnosticSeverity.Warning));
                AssertAgentResolvingEvent(context, "Agent Directory selected: 'c:/ws/topics/' (Out of 2 parent directories)");
            }
        }

        [Fact]
        public async Task Success_OnWalkingUpDirectories_ExpectFindAgentWithinClientWorkspace_Async()
        {
            var diskContent = new Dictionary<string, string>()
            {
                { "parent/ws/agent.mcs.yml", "instructions: " },
            };
            var testFileModule = new TestFileModule(diskContent);
            await using var context = new TestHost([new McsLspModule(), testFileModule]);
            await context.InitializeLanguageServerAsync(workspaceDirectoryPath: "c:/parent/ws");

            // document is within client workspace but agent.mcs.yml is not
            var documentUri = new Uri("file:///c:/parent/ws/a/b/test.mcs.yml");
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
            Assert.Equal(documentUri, diagParams.Uri);
            Assert.Single(diagParams.Diagnostics);
            Assert.Equal("Elements of type 'AdaptiveDialog' are expected in either the 'topics' folder or the 'translations' folder.", diagParams.Diagnostics.First().Message);
            AssertAgentResolvingEvent(context, "Valid agent directory detected: 'c:/parent/ws/'");
        }

        [Fact]
        public async Task Warning_OnWalkingUpDirectories_ExpectStopBeforeLeavingClientWorkspace_Async()
        {
            var diskContent = new Dictionary<string, string>()
            {
                { "parent/agent.mcs.yml", "instructions: " },
            };
            var testFileModule = new TestFileModule(diskContent);
            await using var context = new TestHost([new McsLspModule(), testFileModule]);
            await context.InitializeLanguageServerAsync(workspaceDirectoryPath: "c:/parent/topics");

            // document is within client workspace but agent.mcs.yml is not
            var documentUri = new Uri("file:///c:/parent/topics/test.mcs.yml");
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
            Assert.Equal(documentUri, diagParams.Uri);
            Assert.Empty(diagParams.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error));
            Assert.Equal(["Agent file is missing.", "Elements of type 'AdaptiveDialog' are expected in either the 'topics' folder or the 'translations' folder."], diagParams.Diagnostics.Select(x => x.Message));
            AssertAgentResolvingEvent(context, "No valid agent directory detected. Initializing new directory at file location: c:/parent/topics/");
            AssertAgentDirectoryChangeNotification(context);
        }

        [Fact]
        public async Task Success_OnMergingAgentDirectories_Async()
        {
            var diskContent = new Dictionary<string, string>()
            {
                { "ws/topics/test.mcs.yml", "kind: AdaptiveDialog" },
            };
            var testFileModule = new TestFileModule(diskContent);
            await using var context = new TestHost([new McsLspModule(), testFileModule]);
            await context.InitializeLanguageServerAsync("file:///");
            var documentUri = new Uri("file:///c:/ws/topics/test.mcs.yml");

            // send a first request with no file on disk
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
            Assert.Equal(documentUri, diagParams.Uri);
            Assert.Empty(diagParams.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error));
            Assert.Equal(["Agent file is missing.", "Elements of type 'AdaptiveDialog' are expected in either the 'topics' folder or the 'translations' folder."], diagParams.Diagnostics.Select(x => x.Message));
            AssertAgentResolvingEvent(context, "No valid agent directory detected. Initializing new directory at file location: c:/ws/topics/");
            AssertAgentDirectoryChangeNotification(context);
            context.Logs.Clear();

            // open document again
            var diagParams2 = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
            Assert.Equal(diagParams.Uri, diagParams2.Uri);
            Assert.Equal(diagParams.Diagnostics.Select(x => x.Message), diagParams2.Diagnostics.Select(x => x.Message));
            Assert.Equal(1, context.Logs.Info.Count(x => x.StartsWith("[AgentResolvingEvent]")));
            AssertAgentResolvingEvent(context, "Agent Directory selected: 'c:/ws/topics/'");
            context.Logs.Clear();

            // add agent.mcs.yml in parent directory and open document again
            diskContent["ws/agent.mcs.yml"] = "instructions: ";
            var agentUri = new Uri("file:///c:/ws/agent.mcs.yml");
            var openAgentDiagParams = await context.OpenDocumentWithTextAsync(agentUri, "instructions: ");
            Assert.Equal(agentUri, openAgentDiagParams.Uri);
            Assert.Empty(openAgentDiagParams.Diagnostics);
            AssertAgentResolvingEvent(context, "Valid agent directory detected: 'c:/ws/'");
            AssertAgentResolvingEvent(context, "Document 'c:/ws/topics/test.mcs.yml' removed from previous MCS directory 'c:/ws/topics/'");
            AssertAgentResolvingEvent(context, "Deleting previous MCS directory 'c:/ws/topics/'. All documents ownership have been transferred to a new valid agent directory.");
            context.Logs.Clear();

            // re-open topic file and see that all diagnostics were cleared : topic is now in topics folder (relative to ws) and agent file exists in ws
            var diagParams3 = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
            Assert.Equal(documentUri, diagParams3.Uri);
            Assert.Empty(diagParams3.Diagnostics);
            AssertAgentResolvingEvent(context, "Agent Directory selected: 'c:/ws/'");
        }

        [Theory]
        [InlineData("")]
        [InlineData("instructions: ")]
        [InlineData("kind: GptComponentMetadata")]
        public async Task Success_OnOpenDocumentAlongInvalidAgentFile_Async(string agentFileContent)
        {
            var testFileModule = new TestFileModule(new Dictionary<string, string>
            {
                { "topics/agent.mcs.yml", "\\" }, // triggers YamlReaderException when deserializing agent file content
                { "ws/agent.mcs.yml", agentFileContent },
            });
            await using var context = new TestHost([new McsLspModule(), testFileModule]);
            await context.InitializeLanguageServerAsync("file:///");
            var documentUri = new Uri("file:///c:/ws/topics/test.mcs.yml");
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: AdaptiveDialog");
            Assert.Equal(documentUri, diagParams.Uri);
            Assert.Empty(diagParams.Diagnostics);
            Assert.Empty(context.Logs.Error);

            // c:/ws/topics is not a valid agent directory because of the invalid agent file.
            // The parent directory has a valid agent file.
            AssertAgentResolvingEvent(context, "Valid agent directory detected: 'c:/ws/'");
        }

        [Fact]
        public async Task Error_OnOpenFileWithUnsupportedEntity_Async()
        {
            await using var context = new TestHost();
            await context.InitializeLanguageServerAsync();

            var documentUri = new Uri("file:///c:/entities/test.mcs.yml");
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, "kind: UnknownBotElement");
            Assert.Equal(documentUri, diagParams.Uri);
            var info = diagParams.Diagnostics.Single(x => x.Severity == DiagnosticSeverity.Information);
            Assert.Equal("Document was not compiled under the current Agent Definition.", info.Message);
            var error = diagParams.Diagnostics.Single(x => x.Severity == DiagnosticSeverity.Error);
            Assert.Equal("BotElement Type not supported in current context : UnknownBotElement. Reason=Unhandled data type", error.Message);
        }

        [Fact]
        public async Task CodeAction_OnWarningForTopicInWrongFolder_Async()
        {
            await using var context = new TestHost();
            await context.InitializeLanguageServerAsync();

            var documentUri = new Uri("file:///c:/new_topic.mcs.yml");
            var diagParams = await context.OpenFileAsync(documentUri: documentUri);
            Assert.Equal(documentUri, diagParams.Uri);
            var warnings = diagParams.Diagnostics;
            Assert.All(warnings, warn => Assert.Equal(DiagnosticSeverity.Warning, warn.Severity));
            string[] expectedWarningMessages = [
                "Agent file is missing.",
                "Elements of type 'AdaptiveDialog' are expected in either the 'topics' folder or the 'translations' folder.",
            ];
            Assert.Equal(expectedWarningMessages, warnings.Select(x => x.Message));

            // request quickfix code action
            var codeActionParams = new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = documentUri },
                Range = Range.Zero,
                Context = new CodeActionContext
                {
                    Diagnostics = new[] { warnings.Last() },
                },
            };
            var codeActionRequest = JsonRpc.CreateRequestMessage(LspMethods.CodeAction, codeActionParams);
            context.TestStream.WriteMessage(codeActionRequest);
            var codeActionResponse = await context.GetResponseAsync();
            var codeActions = JsonRpc.GetValidResult<CodeAction[]>(codeActionResponse as JsonRpcResponse);

            // Translation folder will also be suggested as a possible location due to the fact that translation files are also AdaptiveDialog.
            var codeAction = codeActions.First();
            Assert.Equal("Move to 'topics/'", codeAction.Title);
            Assert.Equal(CodeActionKind.QuickFix, codeAction.Kind);
            var renameAction = codeAction?.Edit?.DocumentChanges?[0] as RenameFile;
            Assert.NotNull(renameAction);
            Assert.Equal(documentUri, renameAction?.OldUri);
            Assert.EndsWith("/topics/new_topic.mcs.yml", renameAction!.NewUri.ToString());

            // inform the server that the action was applied
            var renameFileParams = new RenameFilesParams
            {
                Files = [
                    new FileRename
                    {
                        OldUri = documentUri,
                        NewUri = renameAction.NewUri,
                    },
                ],
            };
            var didRenameNotification = JsonRpc.CreateMessage(LspMethods.DidRename, renameFileParams);
            context.TestStream.WriteMessage(didRenameNotification);

            // assert previous diagnostics were cleared
            var diagnosticsMessage = await context.GetResponseAsync([LspMethods.Diagnostics]);
            var finalDiagnostics = JsonRpc.GetValidParams<DiagnosticsParams>(diagnosticsMessage as LspJsonRpcMessage);
            // diagnostic is cleared because new file was not "opened" yet
            Assert.Empty(finalDiagnostics.Diagnostics);
            Assert.Equal(documentUri, finalDiagnostics.Uri);
        }

        [Theory]
        // Expression error
        [InlineData(@"
kind: AdaptiveDialog
beginDialog:
  kind: OnError
  id: main
  actions:
    - kind: SetVariable
      id: setVariable_timestamp
      variable: init:Topic.CurrentTime
      value: =[[Text]](Now(), DateTimeFormat[[.UTC1]])", "IdentifierNotRecognized", true)]
        // duplicate ids (reference error)
        [InlineData(@"
kind: AdaptiveDialog
beginDialog:
  kind: OnError
  id: main
  actions:
    - kind: SetVariable
      id: [[setVariable_timestamp]]
      variable: init:Topic.CurrentTime
      value: =Text(Now(), DateTimeFormat.UTC)
    - kind: SetVariable
      id: [[setVariable_timestamp]]
      variable: init:Topic.CurrentTime
      value: =Text(Now(), DateTimeFormat.UTC)", "DuplicateActionId", true)]
        // incorrect type error
        [InlineData(@"
kind: AdaptiveDialog
beginDialog:
  kind: OnError
  id: main
  actions:
    - kind: SetVariable
      id: setVariable_timestamp
      variable: init:Topic.CurrentTime
      value: =Text(Now(), DateTimeFormat.UTC)
    - kind: SetVariable
      id: setVariable_timestamp_number
      variable: [[Topic.CurrentTime]]
      value: 2", "IncorrectTypeAssignment")]
        // duplicate init (property error)
        [InlineData(@"
kind: AdaptiveDialog
beginDialog:
  kind: OnError
  id: main
  actions:
    - kind: SetVariable
      id: setVariable_timestamp
      variable: init:Topic.CurrentTime
      value: =Text(Now(), DateTimeFormat.UTC)
    - kind: SetVariable
      id: setVariable_duplicateInit
      variable: [[init:Topic.CurrentTime]]
      value: =Text(Now(), DateTimeFormat.UTC)", "DuplicateVariableInitializer", false)]
        // expression error within template line
        [InlineData(@"
kind: AdaptiveDialog
beginDialog:
  kind: OnError
  id: main
  actions:
    - kind: SendActivity
      id: sendMessage_XJBYMo
      activity: |-
        Error Message: {System[[.ErrorX]][[.Message]]}
        Error Code: {System.Error.Code}
        Conversation Id: { System.Conversation.Id}", "IdentifierNotRecognized")]
        // missing property error
        [InlineData(@"
kind: AdaptiveDialog
beginDialog:
  kind: OnError
  id: main
  actions:
    - [[kind: SetVariable
      id: setVariable_X
      variable: Topic.X]]", "MissingRequiredProperty")]
        // duplicate ids init on binding
        [InlineData(@"
kind: AdaptiveDialog
outputType:
  properties:
    one:
      type: Number
beginDialog:
  kind: OnError
  id: main
  actions:
    - kind: SetVariable
      id: set_one
      variable: init:Topic.One
      value: =0
    - kind: BeginDialog
      id: reinit
      dialog: test
      output:
[[        binding:
          one: init:Topic.One]]", "DuplicateVariableInitializer")]
        public async Task DiagnosticRange_OnDialogError_Async(string testString, string expectedErrorCode, bool isRangeReversed = false)
        {
            (var dialogYaml, var expectedRanges) = Utils.ExtractRanges(testString);
            expectedRanges = isRangeReversed ? expectedRanges.Reverse() : expectedRanges;
            await using var context = new TestHost([new McsLspModule(), new TestFileModule(true)]);
            await context.InitializeLanguageServerAsync("file:///");
            var documentUri = new Uri("file:///c:/topics/test.mcs.yml");
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, dialogYaml);
            Assert.Equal(documentUri, diagParams.Uri);
            var errors = diagParams.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error);
            Assert.Equal(expectedRanges, errors.Select(x => (Range)x.Range!));

            Assert.Contains(expectedErrorCode, errors.Select(x => x.Code));
            var actualQuickfixTitles = errors
                .Where(x => x.Code == expectedErrorCode)
                .Select(x => x.Data?.Quickfix)
                .Where(x => x != null)
                .SelectMany(x => x!)
                .Select(x => x.Title);
            if (ExpectedQuickfixTitlesPerErrorKind.TryGetValue(expectedErrorCode, out var expectedQuickfixTitles))
            {
                Assert.Equal(expectedQuickfixTitles, actualQuickfixTitles);
            }
            else
            {
                Assert.Empty(actualQuickfixTitles);
            }
        }

        [Fact]
        public async Task Error_OnLongSchemaName_Async()
        {
            await using var context = new TestHost([new McsLspModule(), new TestFileModule(true)]);
            await context.InitializeLanguageServerAsync("file:///");
            var documentUri = new Uri("file:///c:/topics/test0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789.mcs.yml");
            const string DialogYaml = "kind: AdaptiveDialog";
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, DialogYaml);
            Assert.Equal(documentUri, diagParams.Uri);
            var diagnostic = diagParams.Diagnostics.Single(x => x.Code == "PropertyLengthTooLong");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(1, diagnostic.Data?.Quickfix?.Length);
            var quickfix = diagnostic.Data?.Quickfix?.Single();
            Assert.True(quickfix?.Title.StartsWith("Rename file to 'test012345678901234567890123456789012345678901234567890123456789012345678901234"));
        }

        [Fact]
        public async Task Error_OnFilenameInvalidCharsSchemaName_Async()
        {
            await using var context = new TestHost([new McsLspModule(), new TestFileModule(true)]);
            await context.InitializeLanguageServerAsync("file:///");
            var documentUri = new Uri("file:///c:/topics/abc%21%40%23.mcs.yml"); // abc!@#.mcs.yml
            const string DialogYaml = "kind: AdaptiveDialog";
            var diagParams = await context.OpenDocumentWithTextAsync(documentUri, DialogYaml);
            var diagnostic = diagParams.Diagnostics.Single(x => x.Code == "McsWorkspaceSchemaNameContainsInvalidChars");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(1, diagnostic.Data?.Quickfix?.Length);
            var quickfix = diagnostic.Data?.Quickfix?.Single();
            Assert.Equal("Rename file to 'abc!.mcs.yml'", quickfix?.Title);
        }

        private static void AssertAgentDirectoryChangeNotification(TestHost context)
        {
            var notification = context.Notifications.Single();
            Assert.Equal(Constants.JsonRpcMethods.AgentDirectoryChange, notification.Method);
            context.Notifications.Clear();
        }

        private static readonly Dictionary<string, IEnumerable<string>> ExpectedQuickfixTitlesPerErrorKind = new Dictionary<string, IEnumerable<string>>
        {
            { "IncorrectTypeAssignment", ["Change variable name for 'Number'", "Create new variable"] },
            { "DuplicateActionId", ["Generate new identifier", "Generate new identifier"] },
            { "DuplicateVariableInitializer", ["Remove initializer", "Create new variable"] },
        };

        private static void AssertAgentResolvingEvent(TestHost context, string expectedEventLog, int expectedCount = 1)
        {
            var agentResolvingEvents = context.Logs.Info
                .Where(x => x.StartsWith("[AgentResolvingEvent]"))
                .Select(x => x.Substring("[AgentResolvingEvent] ".Length))
                .ToArray();
            var actualCount = agentResolvingEvents.Count(x => x == expectedEventLog);

            string additionalInformation = string.Empty;
            if (actualCount != expectedCount)
            {
                additionalInformation = $"Expected exactly {expectedCount} event with value {{{expectedEventLog}}} but got {actualCount}.\n";
                var agentResolvingEventsCount = agentResolvingEvents.Count();
                if (agentResolvingEventsCount > 0)
                {
                    var agentResolvingEventsString = string.Join("\n", agentResolvingEvents);
                    additionalInformation += $"{agentResolvingEvents.Count()} Total [AgentResolvingEvent]s: \n" + agentResolvingEventsString;
                }
                else
                {
                    const int NumberOfInfoInDetails = 10;
                    additionalInformation += $"\nLast {NumberOfInfoInDetails} [Info] log lines:\n";
                    var lastInfo = context.Logs.Info.Reverse().Take(NumberOfInfoInDetails).Reverse().ToArray();
                    additionalInformation += lastInfo.Length > 0 ? string.Join("\n", lastInfo) : "<none>";

                    const int NumberOfErrorsInDetails = 5;
                    additionalInformation += $"\nLast {NumberOfErrorsInDetails} [Error] log lines:\n";
                    var lastErrors = context.Logs.Error.Reverse().Take(NumberOfErrorsInDetails).Reverse().ToArray();
                    additionalInformation += lastErrors.Length > 0 ? string.Join("\n", lastErrors) : "<none>";
                }
            }

            Assert.True(expectedCount == actualCount, additionalInformation);
        }
    }
}
