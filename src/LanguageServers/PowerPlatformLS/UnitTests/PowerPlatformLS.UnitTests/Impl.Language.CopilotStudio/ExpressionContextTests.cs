namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerFx.Intellisense;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using System.Linq;
    using System.Text;
    using Xunit;

    public class ExpressionContextTests
    {
        [Fact]
        public void Test_TemplateLine_IsExpression()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            string search = "activity: I'm sorry, {Topic.|Var2 =";

            var requestContext = world.GetRequestContext(doc, search);
            var element = (TemplateLine)world.GetElementAtCursor(requestContext);

            bool ok = element.TryGetExpressionContext(requestContext, out var expressionContext);

            Assert.True(ok);

            // ! asserted
            Assert.Equal("Topic.Var2 = \"Foo\"", expressionContext!.ExpressionText);
            Assert.Equal(element, expressionContext.Element);
            Assert.Equal(6, expressionContext.Offset); // cursor is 6 chars in
            Assert.Same(world.GetWorkspace(), expressionContext.Workspace);

            var intellisense = expressionContext.GetPowerFxIntellisense();

            Assert.NotNull(intellisense);

            // ! Asserted 
            var suggestions = ToString(intellisense!);
            Assert.Equal("Var1,Var2,Var3", suggestions);
        }

        [Fact]
        public void Test_TemplateLine_IsVariableReference()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            string search = "activity: I'm sorry, {Topic.|Var2} Can you try rephrasing?";

            var requestContext = world.GetRequestContext(doc, search);
            var element = (TemplateLine)world.GetElementAtCursor(requestContext);

            bool ok = element.TryGetExpressionContext(requestContext, out var expressionContext);

            Assert.True(ok);
            // ! asserted 
            Assert.Equal("Topic.Var2", expressionContext!.ExpressionText);
            Assert.Equal(element, expressionContext.Element);
            Assert.Equal(6, expressionContext.Offset); // cursor is 6 chars in
            Assert.Same(world.GetWorkspace(), expressionContext.Workspace);

            var intellisense = expressionContext.GetPowerFxIntellisense();
            Assert.NotNull(intellisense);

            // ! asserted 
            var suggestions = ToString(intellisense!);
            Assert.Equal("Var1,Var2,Var3", suggestions);
        }

        [Fact]
        public void Expression_Activity()
        {
            var text = """
instructions: |                
  these are the instructions for the agent
  Here is a {System.Activity.||} message from the user:
""";

            var index = text.IndexOf("||");
            var world = new World();
            var doc = world.AddFile("agent.mcs.yml", text.Replace("||", ""));
            var requestContext = world.GetRequestContext(doc, index);
            var element = (TemplateLine)world.GetElementAtCursor(requestContext);

            bool ok = element.TryGetExpressionContext(requestContext, out var expressionContext);

            Assert.True(ok);
            // ! asserted 
            Assert.Equal("System.Activity.", expressionContext!.ExpressionText);
            Assert.Equal(element, expressionContext.Element);
            Assert.Equal(16, expressionContext.Offset); // cursor is 6 chars in
            Assert.Same(world.GetWorkspace(), expressionContext.Workspace);

        }

        [Theory]
        [InlineData("acti|vity: I'm sorry, {Topic.Var2 =")]
        [InlineData("activity: I'm sorry,| {Topic.Var2 =")]
        public void Test_TemplateLine_Not_InExpression(string search)
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            var requestContext = world.GetRequestContext(doc, search);
            var element = world.GetElementAtCursor(requestContext);

            bool ok = element.TryGetExpressionContext(requestContext, out var expressionContext);

            Assert.False(ok);
            Assert.Null(expressionContext);
        }

        [Fact]
        public void Test_ExpressionBase_Intellisense()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            string search = "\"Hi\" & Topic.|Var1";

            var requestContext = world.GetRequestContext(doc, search);
            var element = (ExpressionBase)world.GetElementAtCursor(requestContext);

            bool ok = element.TryGetExpressionContext(requestContext, out var expressionContext);
            Assert.True(ok);
            Assert.NotNull(expressionContext);

            // Leading space is present since expression starts right after '='

            // ! asserted 
            Assert.Equal(" \"Hi\" & Topic.Var1", expressionContext!.ExpressionText);
            Assert.Equal(14, expressionContext.Offset);
            Assert.Equal(element, expressionContext.Element);
            Assert.Same(world.GetWorkspace(), expressionContext.Workspace);

            var intellisense = expressionContext.GetPowerFxIntellisense();
            Assert.NotNull(intellisense);

            // ! asserted 
            var suggestions = ToString(intellisense!);
            Assert.Equal("Var1,Var2,Var3", suggestions);
        }

        // Stringify intellisense suggestions for easy comparison.
        // Suggestions are ordered. 
        public static string ToString(IIntellisenseResult intellisense)
        {
            if (intellisense == null)
            {
                return "<null>";
            }

            var sb = new StringBuilder();

            string dil = "";
            foreach (var item in intellisense.Suggestions.OrderBy(x=>x.DisplayText.Text))
            {
                sb.Append(dil);
                var text = item.DisplayText.Text;
                sb.Append(text);

                dil = ",";
            }

            return sb.ToString();
        }
    }
}

                                          