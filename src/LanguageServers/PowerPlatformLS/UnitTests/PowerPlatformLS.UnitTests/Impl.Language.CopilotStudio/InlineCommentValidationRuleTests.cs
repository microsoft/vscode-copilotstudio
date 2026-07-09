// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Validation;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Xunit;

    public class InlineCommentValidationRuleTests
    {
        private const string WorkspaceRoot = "c:/agent";

        [Theory]
        [MemberData(nameof(WarningWithQuickFixCases))]
        public void ReportsWarningAndQuickFix(string text, string expectedNewText)
        {
            var diagnostic = Assert.Single(Validate(text));
            AssertWarningWithQuickFix(diagnostic, expectedNewText);
        }

        [Fact]
        public void RuleIsRegisteredInCopilotStudioModule()
        {
            var world = new World();
            world.GetWorkspace();
            Assert.Contains(world.GetRequiredServices<IValidationRule<McsLspDocument>>(), rule => rule is InlineCommentValidationRule);
        }

        [Theory]
        [MemberData(nameof(SkippedFieldCases))]
        public void DoesNotReportForSkippedFields(string text)
        {
            Assert.Empty(Validate(text));
        }

        [Theory]
        [MemberData(nameof(NoWarningCases))]
        public void DoesNotReportForSafeCommentOrHashCases(string text)
        {
            Assert.Empty(Validate(text));
        }

        public static IEnumerable<object[]> WarningWithQuickFixCases()
        {
            yield return ["kind: AdaptiveDialog\nmodelDescription: foo # bar\n", "\"foo # bar\""];
            yield return ["kind: AdaptiveDialog\nmodelDescription: path \\ and \"quote\" # tail\n", "\"path \\\\ and \\\"quote\\\" # tail\""];
            yield return ["mcs.metadata:\n  description: foo # bar\nkind: AdaptiveDialog\n", "\"foo # bar\""];
            yield return ["mcs.metadata:\n  componentName: Foo # 1\nkind: AdaptiveDialog\n", "\"Foo # 1\""];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    displayName: Goodbye # 9\n", "\"Goodbye # 9\""];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  actions:\n    - kind: Question\n      id: question\n      variable: Topic.EndConversation\n      prompt: Would you like to end our conversation. # 18\n      entity: BooleanPrebuiltEntity\n", "\"Would you like to end our conversation. # 18\""];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  actions:\n    - kind: SendActivity\n      id: send\n      activity: Go ahead. I'm listening. # 33\n", "\"Go ahead. I'm listening. # 33\""];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnConversationStart\n  id: main\n  actions:\n    - kind: SetVariable\n      id: setVariable\n      variable: Global.GlobalVar1\n      value: abc # 5\n", "\"abc # 5\""];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnConversationStart\n  id: main\n  actions:\n    - kind: SendActivity\n      id: send\n      activity:\n        text:\n          - Hello there # 1\n", "\"Hello there # 1\""];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnConversationStart\n  id: main\n  actions:\n    - kind: SendActivity\n      id: send\n      activity:\n        speak:\n          - Welcome friend # 2\n", "\"Welcome friend # 2\""];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnConversationStart\n  id: main\n  actions:\n    - kind: SendActivity\n      id: send\n      activity:\n        text:\n          - Hello {System.Bot.Name} # 1\n", "\"Hello {System.Bot.Name} # 1\""];
        }

        public static IEnumerable<object[]> SkippedFieldCases()
        {
            yield return ["kind: AdaptiveDialog # note\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main # note\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  runOnce: true # note\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  actions:\n    - kind: SendActivity\n      id: send\n      inputHint: acceptingInput # note\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  actions:\n    - kind: ConditionGroup\n      id: condition\n      conditions:\n        - id: conditionItem\n          condition: =Topic.Flag # note\n          actions: []\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnConversationStart\n  id: main\n  actions:\n    - kind: SetVariable\n      id: setVariable\n      variable: Global.GlobalVar2\n      value: 123 # test 4\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnConversationStart\n  id: main\n  actions:\n    - kind: SetVariable\n      id: setVariable\n      variable: Global.GlobalVar2\n      value: true # test 4\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  actions:\n    - kind: SendActivity\n      id: send\n      activity: =System.User.DisplayName # note\n"];
            yield return ["kind: AdaptiveDialog\nunknownProperty: foo # bar\n"];
            yield return ["kind: AdaptiveDialog\nunknownList:\n  - hello # tail\n"];
        }

        public static IEnumerable<object[]> NoWarningCases()
        {
            yield return ["kind: AdaptiveDialog\n# standalone comment\nmodelDescription: foo\n"];
            yield return ["kind: AdaptiveDialog\nmodelDescription: foo\n# between comment\ntriggerQueries:\n  - hello\n"];
            yield return ["kind: AdaptiveDialog\nmodelDescription: \"foo # bar\"\n"];
            yield return ["kind: AdaptiveDialog\nmodelDescription: 'foo # bar'\n"];
            yield return ["kind: AdaptiveDialog\nbeginDialog:\n  kind: OnConversationStart\n  id: main\n  actions:\n    - kind: SendActivity\n      id: send\n      activity: \"#5 Hello there.\" # 6\n"];
            yield return ["kind: AdaptiveDialog\nmodelDescription: |\n  foo # bar\n"];
            yield return ["kind: AdaptiveDialog\nmodelDescription: loves C# programming\n"];
            yield return ["kind: AdaptiveDialog\nmodelDescription: foo#bar\n"];
            yield return ["kind: AdaptiveDialog\nmodelDescription: plain text\n"];
        }

        private static IReadOnlyList<Diagnostic> Validate(string text)
        {
            var root = new DirectoryPath(WorkspaceRoot);
            var document = new McsLspDocument(new FilePath(WorkspaceRoot + "/topics/Foo.mcs.yml"), text, root);
            var context = new RequestContext(new FakeLanguage(), new Workspace(root), document, 0);
            IValidationRule<McsLspDocument> rule = new InlineCommentValidationRule();
            return rule.ComputeValidation(context, document).ToList();
        }

        private static void AssertWarningWithQuickFix(Diagnostic diagnostic, string expectedNewText)
        {
            Assert.Equal(InlineCommentValidationRule.DiagnosticCode, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            var quickFix = Assert.Single(diagnostic.Data?.Quickfix ?? []);
            Assert.Equal("Quote YAML value", quickFix.Title);
            var textEdit = Assert.IsType<TextDocumentEdit>(Assert.Single(quickFix.Edit!.DocumentChanges!));
            Assert.Equal(expectedNewText, Assert.Single(textEdit.Edits).NewText);
        }

        private sealed class FakeLanguage : ILanguageAbstraction
        {
            public LanguageType LanguageType => LanguageType.CopilotStudio;

            public LspDocument CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspacePath) => throw new NotImplementedException();

            public bool IsValidAgentDirectory(DirectoryPath directory, out DirectoryPath validDirectory)
            {
                validDirectory = directory;
                return false;
            }
        }
    }
}
