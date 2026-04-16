namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Serialization;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using Xunit;

    public class CompletionIntegrationTests
    {
        [Theory]
        [InlineData("Completions/SimpleText.yml", "agent.mcs.yml")]
        public void Completion_Snapshot(string fileName, string fileNameInTestEnvironment)
        {
            const string CaretMarker = "|caret|";
            var text = TestDataReader.GetTestData(fileName);
            var index = text.IndexOf(CaretMarker);
            Assert.True(index >= 0, $"Missing | in {fileName}");
            text = text.Remove(index, CaretMarker.Length);
            var world = new World();
            var doc = world.AddFile(fileNameInTestEnvironment, text);
            var context = world.GetRequestContext(doc, index);

            var rule = world.GetRequiredService<ICompletionRule<McsLspDocument>>();

            var completions = rule.ComputeCompletion(context, new CompletionContext());
            var list = completions.ToList();
            Assert.NotNull(list[0].Detail);
            Assert.NotNull(list[0].Documentation);
        }

        private string JsonDump(object value)
        {
            var options = new JsonSerializerOptions(Constants.DefaultSerializationOptions);
            options.WriteIndented = true;
            return LspJsonSerializationHelper.Serialize(value, options);
        }

        // Validate that handlers are registered and we have clear patern for adding future handlers. 
        [Fact]
        public void TestDI()
        {
            var world = new World();

            // Ensure both handlers are hit. 
            world.Services.AddCompletionHandler<My1Handler, EditPropertyValueCompletionEvent>();
            world.Services.AddCompletionHandler<My2Handler, EditPropertyValueCompletionEvent>();

            var doc = world.AddFile("topic2.mcs.yml");

            var rule = world.GetRequiredService<ICompletionRule<McsLspDocument>>();

            // Verify handlers where registered.
            world.GetRequiredService<My1Handler>();
            world.GetRequiredService<My2Handler>();

            // Pick any context that will generate a EditPropertyValueCompletionEvent event.
            var reqCtx = world.GetRequestContext(doc, "kind: SetVar|iable"); 

            // This should invoke each handler. 
            var results = rule.ComputeCompletion(reqCtx, new CompletionContext());
            string[] labels = results.Select(x => x.Label).ToArray();

            Assert.NotEmpty(labels); // registration is missing

            // Assert each handler was invoked and results are included.
            Assert.Contains(My1Handler.Text, labels);
            Assert.Contains(My2Handler.Text, labels);
        }

        // Register 2 handlers (not just 1) to hit bugs with overwriting services. 
        public class My1Handler : ICompletionEventHandler<EditPropertyValueCompletionEvent>
        {
            public const string Text = "H11111";

            IEnumerable<CompletionItem> ICompletionEventHandler<EditPropertyValueCompletionEvent>.CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, EditPropertyValueCompletionEvent completionEvent)
            {
                return [new CompletionItem
                {
                     Label = Text
                }];
            }
        }

        public class My2Handler : ICompletionEventHandler<EditPropertyValueCompletionEvent>
        {
            public const string Text = "H22222";

            IEnumerable<CompletionItem> ICompletionEventHandler<EditPropertyValueCompletionEvent>.CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, EditPropertyValueCompletionEvent completionEvent)
            {
                return [new CompletionItem
                {
                     Label = Text
                }];
            }
        }

        // An e2e test for Power Fx intellisense.
        [Fact]
        public void Test_FxIntellisense()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            // At this cursor spot, we should get suggestions for "Topic"
            string search = "activity: I'm sorry, {Topic.|Var2 =";

            var reqCtx = world.GetRequestContext(doc, search);

            var rule = world.GetRequiredService<ICompletionRule<McsLspDocument>>();

            var results = rule.ComputeCompletion(reqCtx, new CompletionContext());

            var labels = results.Select(x => x.Label).Order().ToArray();
            var labelStr = string.Join(",", labels);

            Assert.Equal("Var1,Var2,Var3", labelStr);
        }
    }
}
