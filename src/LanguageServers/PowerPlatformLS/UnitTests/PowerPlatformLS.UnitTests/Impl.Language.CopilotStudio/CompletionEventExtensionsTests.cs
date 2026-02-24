namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using System;
    using System.Xml.Linq;
    using Xunit;

    public class CompletionEventExtensionsTests
    {
        [Fact]
        public void Triage_PropertyValue()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            string search = "kind: Set|Variable";

            var requestContext = world.GetRequestContext(doc, search);
            var triggerContext = new CompletionContext();

            var completionEvent = requestContext.Triage(triggerContext);

            var e2 = Assert.IsType<EditPropertyValueCompletionEvent>(completionEvent);
            Assert.Equal("kind", e2.PropertyName);
            var setVariable = Assert.IsType<SetVariable>(e2.Element);
                        
            Assert.NotNull(setVariable.ParentOfType<BotDefinition>());
            Assert.Equal("setVariable_var1", setVariable.Id);
        }

        [Theory]
        [InlineData("k", 1, "k")]
        [InlineData("kind: X\ni", 9, "i")]
        public void Triage_PropertyName(string text, int index, string expectedPropertyName)
        {
            var world = new World();
            var doc = world.AddFile("agent.mcs.yml", text);
            var context = world.GetRequestContext(doc, index);
            var completionEvent = context.Triage(new());
            var e2 = Assert.IsType<NewPropertyCompletionEvent>(completionEvent);

            Assert.Equal(expectedPropertyName, e2.PropertyName);
            var element = Assert.IsType<GptComponentMetadata>(e2.Element);
            Assert.NotNull(element.ParentOfType<BotDefinition>());
        }

        [Theory]
        [InlineData(0, "a", null)]
        [InlineData(0, "a", 0)]
        [InlineData(0, "a", 2)]
        [InlineData(2, "b", null)]
        [InlineData(4, "c", null)]
        [InlineData(4, "c", 6)]
        [InlineData(4, "c", 4)]
        [InlineData(4, "c", 2)]
        [InlineData(4, "c", 0)]
        [InlineData(6, "d", null)]
        [InlineData(8, "e", null)]
        [InlineData(8, "f", null)]
        [InlineData(8, "f", 8)]
        [InlineData(8, "f", 6)]
        [InlineData(8, "f", 4)]
        [InlineData(8, "f", 2)]
        [InlineData(8, "f", 0)]
        public void Triage_MultipleNestedObjects(int indentLevel, string expectedPeerPropertyName, int? suffixIndentLevel)
        {
            const string Yaml = """
                a:
                  b:
                    c:
                      d:
                        e: 1
                        f: 2
                """;

            var indentSuffix = "\r\n" + new string(' ', indentLevel);
            var additionalSuffix = suffixIndentLevel.HasValue ? "\r\n" + new string(' ', suffixIndentLevel.Value) + "z: 1" : string.Empty;
            var text = Yaml + indentSuffix + additionalSuffix;
            var world = new World();
            var doc = world.AddFile("agent.mcs.yml", text);
            var context = world.GetRequestContext(doc, index: Yaml.Length + indentSuffix.Length);
            var triaged = context.Triage(new());

            PropertyCompletionEvent result;
            if (suffixIndentLevel != null && (expectedPeerPropertyName == "e" || expectedPeerPropertyName == "f"))
            {
                result = Assert.IsType<EditPropertyValueCompletionEvent>(triaged);
                
            }
            else
            {
                result = Assert.IsType<NewPropertyCompletionEvent>(triaged);
            }

            var parentObject = Assert.IsType<MappingObjectSyntax>(result.Parent);
            Assert.Contains(expectedPeerPropertyName, parentObject.AllPropertyNames());

        }

        [Theory]
        [InlineData("knowledgeSources:\n  ", "knowledgeSources")]
        public void Triage_Unknown(string text, string expectedPropertyName)
        {
            var world = new World();
            var doc = world.AddFile("agent.mcs.yml", text);
            var context = world.GetRequestContext(doc, index: text.Length);
            var completionEvent = context.Triage(new());
            var e2 = Assert.IsType<EditPropertyValueCompletionEvent>(completionEvent);
            Assert.Equal(expectedPropertyName, e2.PropertyName);
            var element = Assert.IsType<SearchAllKnowledgeSources>(e2.Element);
            Assert.NotNull(element.ParentOfType<BotDefinition>());
        }

        [Fact]
        public void Triage_EditMultiline()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            // Inside a multiline property 
            string search = "/* additional| number. */";

            var requestContext = world.GetRequestContext(doc, search);
            var triggerContext = new CompletionContext();

            var completionEvent = requestContext.Triage(triggerContext);

            var e2 = Assert.IsType<EditPropertyValueCompletionEvent>(completionEvent);
            Assert.Equal("condition", e2.PropertyName);
            var boolExpression = Assert.IsType<BoolExpression>(e2.Element);

            // The triage events are not semantically rooted.
            Assert.NotNull(boolExpression.ParentOfType<BotDefinition>());
            Assert.Equal("condition_DGc1Wy-item-0", ((ConditionItem?)boolExpression.Parent!).Id);
        }

        [Theory]
        [InlineData("value: =123\r\n      |", typeof(SetVariable))]
        [InlineData("value: =123\r\n|", typeof(AdaptiveDialog))]
        public void Triage_NewProperty(string syntaxSearch, Type expectedElementType)
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");
            var requestContext = world.GetRequestContext(doc, syntaxSearch);
            var completionEvent = requestContext.Triage(new());
            var e2 = Assert.IsType<NewPropertyCompletionEvent>(completionEvent);
            Assert.IsType(expectedElementType, e2.Element);
            Assert.NotNull(e2.Element?.ParentOfType<BotDefinition>());
        }

        [Theory]
        [InlineData(true, "{||}", true)]
        [InlineData(true, "||{}", false)]
        [InlineData(true, "{}||", false)]
        [InlineData(true, "{}||\r\n", false)]
        [InlineData(true, "{} text {||} text\r\n", true)]
        [InlineData(true, "{} text {||}", true)]
        [InlineData(true, "{} text {||}\r\n", true)]
        [InlineData(false, "x {||}", true)]
        [InlineData(false, "x ||{}", false)]
        [InlineData(false, "x {}||", false)]
        [InlineData(false, "x {}||\r\n", false)]
        [InlineData(false, "x {} text {||} text\r\n", true)]
        [InlineData(false, "x {} text {||}", true)]
        [InlineData(false, "x {} text {||}\r\n", true)]
        public void Triage_TemplateLine_Formula(bool multiline, string templateLineText, bool inFormula)
        {
            var world = new World();
            var testCase = "instructions: " + (multiline ? "| \r\n  " : string.Empty) + templateLineText;
            var index = testCase.IndexOf("||");
            testCase = testCase.Replace("||", string.Empty);
            var doc = world.AddFile("agent.mcs.yml", testCase);
            var requestContext = world.GetRequestContext(doc, index);
            var completionEvent = requestContext.Triage(new());
            var e2 = Assert.IsType<EditPropertyValueCompletionEvent>(completionEvent);
            BotElement element;
            if (inFormula)
            {
                element = Assert.IsType<ValueExpression>(e2.Element);
                var segment = Assert.IsType<ExpressionSegment>(element.Parent);
                Assert.IsType<TemplateLine>(segment.Parent);
            }
            else
            {
                // either text or template line if there is no matching segment (at the end of an expression)
                Assert.True(e2.Element is TextSegment or TemplateLine);
                element = e2.Element!;
            }

            Assert.NotNull(element.ParentOfType<BotDefinition>());
        }

        [Fact]
        public void Triage_EditProperty()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            string search = "value: =123|"; // Right before newline. 

            var requestContext = world.GetRequestContext(doc, search);
            var completionEvent = requestContext.Triage(new());
            var e2 = Assert.IsType<EditPropertyValueCompletionEvent>(completionEvent);
            var expr = Assert.IsType<ValueExpression>(e2.Element);
            var parent = Assert.IsType<SetVariable>(expr.Parent);
            Assert.Equal("setVariable_var1", parent.Id);
            Assert.NotNull(parent.ParentOfType<BotDefinition>());
        }

        [Fact]
        public void Triage_ArrayElement()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            string search = "- botname.|"; // editing in middle or array

            var requestContext = world.GetRequestContext(doc, search);
            var completionEvent = requestContext.Triage(new());

            var e2 = Assert.IsType<SequenceValueCompletionEvent>(completionEvent);
            Assert.Equal(1, e2.Index);

            var element = Assert.IsType<SearchSpecificKnowledgeSources>(e2.Element);            
            Assert.NotNull(element.ParentOfType<BotDefinition>());
        }

        [Fact]
        public void Triage_OutsideArrayElement()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            string search = "|- botname."; // editing before array

            var requestContext = world.GetRequestContext(doc, search);
            var completionEvent = requestContext.Triage(new());

            Assert.Null(completionEvent); // outside of array. 
        }
    }
}
