namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.ObjectModel.Syntax.Text;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.SemanticToken;
    using Moq;
    using System;
    using System.Linq;
    using System.Text;
    using Xunit;

    public class SemanticTokensTests
    {
        [Fact]
        public void SemanticTokenTest()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");
            var fileSyntax = Assert.IsAssignableFrom<SyntaxNode>(doc.FileModel?.Syntax);
            var requestContext = world.GetRequestContext(doc, 0);
            var semanticTokenVisitor = new SemanticTokenVisitor(requestContext, Mock.Of<ILspLogger>());
            semanticTokenVisitor.Visit(fileSyntax);

            var semanticTokenData = semanticTokenVisitor.SemanticTokenData.ToArray();

            var semanticToken = new SemanticTokens
            {
                ResultId = "123",
                Data = semanticTokenData
            };

            var kindProperty = new int[] { 0, 0, 4, (int)SemanticTokenType.Keyword, (int)SemanticTokenModifier.Declaration }; // { line: 0, startChar: 0, length: 4, tokenType: 0(keyword), tokenModifiers: 0(declaration) },
            Assert.True(kindProperty.All(item => semanticToken.Data.Contains(item)));

            // Comment token {line: 0, startChart: 2, length: 18, tokenType: 4 (comment), tokenModifier: 0} for comment "/*second number*/"
            var commentToken = new int[] { 0, 2, 18, (int)SemanticTokenType.Comment, (int)SemanticTokenModifier.Declaration };
            Assert.True(commentToken.All(item => semanticToken.Data.Contains(item)));
        }

        [Fact]
        public void BotEntity_SemanticToken()
        {
            var world = new World();
            var doc = world.AddFile("settings.mcs.yml");
            var fileSyntax = Assert.IsAssignableFrom<SyntaxNode>(doc.FileModel?.Syntax);
            var requestContext = world.GetRequestContext(doc, 0);
            var semanticTokenVisitor = new SemanticTokenVisitor(requestContext, Mock.Of<ILspLogger>());
            semanticTokenVisitor.Visit(fileSyntax);
            var semanticTokenData = semanticTokenVisitor.SemanticTokenData.ToArray();
            var expectedText = """
[1:0-19]Property: accessControlPolicy
[1:19-21]Operator: : 
[1:21-24]EnumMember: Any
[2:0-13]Property: configuration
[2:13-14]Operator: :
[3:2-17]Property: publishOnCreate
[3:17-19]Operator: : 
[3:19-23]Keyword: true
[4:2-18]Property: isLightweightBot
[4:18-20]Operator: : 
[4:20-24]Keyword: true
[5:0-8]Property: language
[5:8-10]Operator: : 
[5:10-14]Number: 1033
[6:0-4]Property: name
[6:4-6]Operator: : 
[6:6-11]String: Agent
""";
            AssertTokens(semanticTokenData, doc.Text, expectedText);
        }

        [Fact]
        public void Templateline_SemanticToken()
        {
            var world = new World();
            var doc = world.AddFile("simpleagent.yml");
            var fileSyntax = Assert.IsAssignableFrom<SyntaxNode>(doc.FileModel?.Syntax);
            var requestContext = world.GetRequestContext(doc, 0);
            var semanticTokenVisitor = new SemanticTokenVisitor(requestContext, Mock.Of<ILspLogger>());
            semanticTokenVisitor.Visit(fileSyntax);
            var semanticTokenData = semanticTokenVisitor.SemanticTokenData.ToArray();
            var expectedText = """
[1:0-4]Property: kind
[1:4-6]Operator: : 
[1:6-26]EnumMember: GptComponentMetadata
[2:0-12]Property: instructions
[2:12-14]Operator: : 
[2:14-15]Keyword: |
[4:0-3]String:   
[5:0-26]String:   This is a template line
[6:0-23]String:   This is variable use 
[6:23-24]Keyword: {
[6:24-30]Keyword: System
[6:30-31]Operator: .
[6:31-35]Keyword: User
[6:35-36]Operator: .
[6:36-45]Property: FirstName
[6:45-46]Keyword: }
[6:46-47]String: 
[7:0-13]String:   This is an 
[7:13-14]Keyword: {
[7:14-20]Keyword: System
[7:20-21]Operator: .
[7:21-29]Keyword: Activity
[7:29-30]Operator: .
[7:30-34]Property: Text
[7:34-35]Keyword: }
[7:35-52]String:  inline variable
[8:0-13]String:   This is an 
[8:13-14]Keyword: {
[8:14-15]Number: 5
[8:16-17]Operator: >
[8:18-19]Number: 3
[8:19-20]Keyword: }
[8:20-39]String:  inline expression
[9:0-24]String:   This is an expression 
[9:24-25]Keyword: {
[9:25-26]Number: 5
[9:27-28]Operator: +
[9:29-30]Number: 3
[9:30-31]Keyword: }
[9:31-49]String:  after expression
[10:0-1]String: 
[11:0-1]String: 
[12:0-1]String: 
[13:0-26]String:   consecutive break lines
[14:0-1]String: 
[15:0-1]String: 
[16:0-24]String:   then more break lines
[17:0-1]String: 
[18:0-1]String: 
[19:0-1]String: 
[20:0-1]String: 
[21:0-3]Property: foo
[21:3-5]Operator: : 
[21:5-8]String: Bar
""";
            AssertTokens(semanticTokenData, doc.Text, expectedText);
        }

        [Fact]
        public void PowerFx_SemanticToken()
        {
            var world = new World();
            var doc = world.AddFile("powerfx.mcs.yml");
            var fileSyntax = Assert.IsAssignableFrom<SyntaxNode>(doc.FileModel?.Syntax);
            var requestContext = world.GetRequestContext(doc, 0);
            var semanticTokenVisitor = new SemanticTokenVisitor(requestContext, Mock.Of<ILspLogger>());
            semanticTokenVisitor.Visit(fileSyntax);
            var semanticTokenData = semanticTokenVisitor.SemanticTokenData.ToArray();
            var expectedText = """
[1:0-4]Property: kind
[1:4-6]Operator: : 
[1:6-20]EnumMember: AdaptiveDialog
[2:0-13]Property: startBehavior
[2:13-15]Operator: : 
[2:15-32]EnumMember: CancelOtherTopics
[3:0-11]Property: beginDialog
[3:11-12]Operator: :
[4:2-6]Property: kind
[4:6-8]Operator: : 
[4:8-26]EnumMember: OnRecognizedIntent
[5:2-4]Property: id
[5:4-6]Operator: : 
[5:6-10]EnumMember: main
[7:2-9]Property: actions
[7:9-10]Operator: :
[8:4-6]Operator: - 
[8:6-10]Property: kind
[8:10-12]Operator: : 
[8:12-26]EnumMember: ConditionGroup
[9:6-8]Property: id
[9:8-10]Operator: : 
[9:10-26]EnumMember: condition_DGc1Wy
[10:6-16]Property: conditions
[10:16-17]Operator: :
[11:8-10]Operator: - 
[11:10-12]Property: id
[11:12-14]Operator: : 
[11:14-37]String: condition_DGc1Wy-item-0
[12:10-19]Property: condition
[12:19-21]Operator: : 
[12:21-22]Keyword: |
[13:12-13]Keyword: =
[13:13-18]Property: Topic
[13:18-19]Operator: .
[13:19-34]Property: EndConversation
[13:35-36]Operator: =
[14:14-15]Keyword: (
[15:16-19]Property: Len
[15:19-20]Keyword: (
[15:20-28]String: "Hello,
[16:0-18]String:                 a
[17:0-1]String: 
[18:0-1]String: 
[19:0-1]String: 
[20:0-1]String: 
[21:0-1]String: 
[22:0-34]String:                 b                
[23:0-24]String:                   World
[24:0-20]String:                   !"
[24:21-38]Comment: /* string inside
[25:0-1]Comment: 
[26:0-1]Comment: 
[27:0-37]Comment:                       Len function */
[27:37-38]Keyword: )
[28:20-21]Operator: >
[28:23-26]Number: 456
[28:27-47]Comment: /* 456 is number */ 
[28:47-48]Operator: +
[28:61-78]Comment: // plus operator
[29:20-22]Number: 12
[29:23-24]Operator: *
[29:39-72]Comment: // 12 is number                 
[30:20-45]Comment: /* additional number. */ 
[30:45-51]Number: 523545
[31:14-15]Keyword: )
[31:16-38]Comment: // PowerFx expression
[34:0-1]Comment: 
[35:0-29]Comment:               // empty lines
[37:0-1]Comment: 
[38:0-39]Comment:               // End of the expression
[44:10-17]Property: actions
[44:17-18]Operator: :
[45:12-14]Operator: - 
[45:14-18]Property: kind
[45:18-20]Operator: : 
[45:20-31]EnumMember: BeginDialog
[46:14-16]Property: id
[46:16-18]Operator: : 
[46:18-24]EnumMember: dn94DC
[47:14-20]Property: dialog
[47:20-22]Operator: : 
[47:22-33]Namespace: cree9_agent
[47:33-34]Operator: .
[47:34-39]Namespace: topic
[47:39-40]Operator: .
[47:40-57]EnumMember: EndofConversation
[49:8-10]Operator: - 
[49:10-12]Property: id
[49:12-14]Operator: : 
[49:14-37]String: condition_DGc1Wy-item-1
[50:10-19]Property: condition
[50:19-21]Operator: : 
[50:21-22]Keyword: =
[50:22-27]Property: Topic
[50:27-28]Operator: .
[50:28-43]Property: EndConversation
[50:44-45]Operator: =
[50:46-65]Comment: /* boolean type */ 
[50:65-70]Keyword: false
[50:75-93]Comment: // comment        
[51:10-17]Property: actions
[51:17-18]Operator: :
[52:12-14]Operator: - 
[52:14-18]Property: kind
[52:18-20]Operator: : 
[52:20-32]EnumMember: SendActivity
[53:14-16]Property: id
[53:16-18]Operator: : 
[53:18-36]EnumMember: sendMessage_LdLhmf
[54:14-22]Property: activity
[54:22-24]Operator: : 
[54:24-48]String: Go ahead. I'm listening.
[56:4-6]Operator: - 
[56:6-10]Property: kind
[56:10-12]Operator: : 
[56:12-23]EnumMember: SetVariable
[57:6-8]Property: id
[57:8-10]Operator: : 
[57:10-28]EnumMember: setProperty_2zIik9
[58:6-14]Property: variable
[58:14-16]Operator: : 
[58:16-21]Keyword: Topic
[58:21-22]Operator: .
[58:22-38]Property: RichMessageTitle
[59:6-11]Property: value
[59:11-13]Operator: : 
[59:13-14]Keyword: =
[59:14-18]Property: Text
[59:18-19]Keyword: (
[59:19-28]Property: ParseJSON
[59:28-29]Keyword: (
[59:29-34]Property: Topic
[59:34-35]Operator: .
[59:35-46]Property: richMessage
[59:46-47]Keyword: )
[59:47-48]Operator: .
[59:48-62]Property: richObjectName
[59:62-63]Keyword: )
[61:4-6]Operator: - 
[61:6-10]Property: kind
[61:10-12]Operator: : 
[61:12-24]EnumMember: SendActivity
[62:6-8]Property: id
[62:8-10]Operator: : 
[62:10-28]EnumMember: sendMessage_aannBa
[63:6-14]Property: activity
[63:14-15]Operator: :
[64:8-19]Property: attachments
[64:19-20]Operator: :
[65:10-12]Operator: - 
[65:12-16]Property: kind
[65:16-18]Operator: : 
[65:18-38]EnumMember: AdaptiveCardTemplate
[66:12-23]Property: cardContent
[66:23-25]Operator: : 
[66:25-27]Keyword: |-
[67:14-15]Keyword: =
[67:15-16]Keyword: {
[68:16-20]Property: type
[68:20-21]Operator: :
[68:22-36]String: "AdaptiveCard"
[68:36-37]Operator: ,
[69:16-25]Property: '$schema'
[69:25-26]Operator: :
[69:27-79]String: "http://adaptivecards.io/schemas/adaptive-card.json"
[69:79-80]Operator: ,
[70:16-23]Property: version
[70:23-24]Operator: :
[70:25-30]String: "1.0"
[70:30-31]Operator: ,
[71:16-20]Property: body
[71:20-21]Operator: :
[71:22-23]Keyword: [
[72:18-19]Keyword: {
[73:20-22]Property: id
[73:22-23]Operator: :
[73:24-36]String: "ocsurveyjs"
[73:36-37]Operator: ,
[74:20-24]Property: type
[74:24-25]Operator: :
[74:26-37]String: "TextBlock"
[74:37-38]Operator: ,
[75:20-29]Property: isVisible
[75:29-30]Operator: :
[75:31-36]Keyword: false
[75:36-37]Operator: ,
[76:20-24]Property: text
[76:24-25]Operator: :
[76:26-31]Property: Topic
[76:31-32]Operator: .
[76:32-43]Property: richMessage
[77:18-19]Keyword: }
[77:19-20]Operator: ,
[78:18-19]Keyword: {
[79:20-22]Property: id
[79:22-23]Operator: :
[79:24-31]String: "title"
[79:31-32]Operator: ,
[80:20-24]Property: type
[80:24-25]Operator: :
[80:26-37]String: "TextBlock"
[80:37-38]Operator: ,
[81:20-24]Property: text
[81:24-25]Operator: :
[81:26-31]Property: Topic
[81:31-32]Operator: .
[81:32-48]Property: RichMessageTitle
[82:18-19]Keyword: }
[83:16-17]Keyword: ]
[84:0-15]Keyword:               }
[86:0-9]Property: inputType
[86:9-10]Operator: :
[87:2-12]Property: properties
[87:12-13]Operator: :
[88:4-15]Property: richMessage
[88:15-16]Operator: :
[89:6-17]Property: displayName
[89:17-19]Operator: : 
[89:19-30]String: richMessage
[90:6-10]Property: type
[90:10-12]Operator: : 
[90:12-18]String: String
""";
            AssertTokens(semanticTokenData, doc.Text, expectedText);
        }

        [Fact]
        public void OnError_SemanticToken()
        {
            var world = new World();
            var doc = world.AddFile("workspace/localworkspace/topics/OnError.mcs.yml");
            var fileSyntax = Assert.IsAssignableFrom<SyntaxNode>(doc.FileModel?.Syntax);
            var requestContext = world.GetRequestContext(doc, 0);
            var semanticTokenVisitor = new SemanticTokenVisitor(requestContext, Mock.Of<ILspLogger>());
            semanticTokenVisitor.Visit(fileSyntax);
            var semanticTokenData = semanticTokenVisitor.SemanticTokenData.ToArray();
            var expectedText = """
[1:0-4]Property: kind
[1:4-6]Operator: : 
[1:6-20]EnumMember: AdaptiveDialog
[2:0-11]Property: beginDialog
[2:11-12]Operator: :
[3:2-6]Property: kind
[3:6-8]Operator: : 
[3:8-15]EnumMember: OnError
[4:2-4]Property: id
[4:4-6]Operator: : 
[4:6-10]EnumMember: main
[5:2-9]Property: actions
[5:9-10]Operator: :
[6:4-6]Operator: - 
[6:6-10]Property: kind
[6:10-12]Operator: : 
[6:12-23]EnumMember: SetVariable
[7:6-8]Property: id
[7:8-10]Operator: : 
[7:10-31]EnumMember: setVariable_timestamp
[8:6-14]Property: variable
[8:14-16]Operator: : 
[8:16-20]Keyword: init
[8:20-21]Operator: :
[8:21-26]Keyword: Topic
[8:26-27]Operator: .
[8:27-38]Property: CurrentTime
[9:6-11]Property: value
[9:11-13]Operator: : 
[9:13-14]Keyword: =
[9:14-18]Property: Text
[9:18-19]Keyword: (
[9:19-22]Property: Now
[9:22-23]Keyword: (
[9:23-24]Keyword: )
[9:24-25]Operator: ,
[9:26-40]Property: DateTimeFormat
[9:40-41]Operator: .
[9:41-44]Property: UTC
[9:44-45]Keyword: )
[11:4-6]Operator: - 
[11:6-10]Property: kind
[11:10-12]Operator: : 
[11:12-26]EnumMember: ConditionGroup
[12:6-8]Property: id
[12:8-10]Operator: : 
[12:10-21]EnumMember: condition_1
[13:6-16]Property: conditions
[13:16-17]Operator: :
[14:8-10]Operator: - 
[14:10-12]Property: id
[14:12-14]Operator: : 
[14:14-20]String: bL4wmY
[15:10-19]Property: condition
[15:19-21]Operator: : 
[15:21-22]Keyword: =
[15:22-28]Property: System
[15:28-29]Operator: .
[15:29-41]Property: Conversation
[15:41-42]Operator: .
[15:42-52]Property: InTestMode
[15:53-54]Operator: =
[15:55-59]Keyword: true
[16:10-17]Property: actions
[16:17-18]Operator: :
[17:12-14]Operator: - 
[17:14-18]Property: kind
[17:18-20]Operator: : 
[17:20-32]EnumMember: SendActivity
[18:14-16]Property: id
[18:16-18]Operator: : 
[18:18-36]EnumMember: sendMessage_XJBYMo
[19:14-22]Property: activity
[19:22-24]Operator: : 
[19:24-26]Keyword: |-
[20:16-31]String: Error Message: 
[20:31-32]Keyword: {
[20:32-38]Keyword: System
[20:38-39]Operator: .
[20:39-44]Keyword: Error
[20:44-45]Operator: .
[20:45-52]Property: Message
[20:52-53]Keyword: }
[20:53-54]String: 
[21:0-28]String:                 Error Code: 
[21:28-29]Keyword: {
[21:29-35]Keyword: System
[21:35-36]Operator: .
[21:36-41]Keyword: Error
[21:41-42]Operator: .
[21:42-46]Property: Code
[21:46-47]Keyword: }
[21:47-48]String: 
[22:0-33]String:                 Conversation Id: 
[22:33-34]Keyword: {
[22:34-40]Keyword: System
[22:40-41]Operator: .
[22:41-53]Keyword: Conversation
[22:53-54]Operator: .
[22:54-56]Property: Id
[22:56-57]Keyword: }
[22:57-58]String: 
[23:0-28]String:                 Time (UTC): 
[23:28-29]Keyword: {
[23:29-34]Keyword: Topic
[23:34-35]Operator: .
[23:35-46]Property: CurrentTime
[23:46-47]Keyword: }
[25:6-17]Property: elseActions
[25:17-18]Operator: :
[26:8-10]Operator: - 
[26:10-14]Property: kind
[26:14-16]Operator: : 
[26:16-28]EnumMember: SendActivity
[27:10-12]Property: id
[27:12-14]Operator: : 
[27:14-32]EnumMember: sendMessage_dZ0gaF
[28:10-18]Property: activity
[28:18-19]Operator: :
[29:12-16]Property: text
[29:16-17]Operator: :
[30:14-16]Operator: - 
[30:16-18]Keyword: |-
[31:16-39]String: An error has occurred.
[32:0-28]String:                 Error code: 
[32:28-29]Keyword: {
[32:29-35]Keyword: System
[32:35-36]Operator: .
[32:36-41]Keyword: Error
[32:41-42]Operator: .
[32:42-46]Property: Code
[32:46-47]Keyword: }
[32:47-48]String: 
[33:0-33]String:                 Conversation Id: 
[33:33-34]Keyword: {
[33:34-40]Keyword: System
[33:40-41]Operator: .
[33:41-53]Keyword: Conversation
[33:53-54]Operator: .
[33:54-56]Property: Id
[33:56-57]Keyword: }
[33:57-58]String: 
[34:0-28]String:                 Time (UTC): 
[34:28-29]Keyword: {
[34:29-34]Keyword: Topic
[34:34-35]Operator: .
[34:35-46]Property: CurrentTime
[34:46-47]Keyword: }
[34:47-49]String: .
[35:12-17]Property: speak
[35:17-18]Operator: :
[36:14-16]Operator: - 
[36:16-56]String: An error has occurred, please try again.
[38:4-6]Operator: - 
[38:6-10]Property: kind
[38:10-12]Operator: : 
[38:12-35]EnumMember: LogCustomTelemetryEvent
[39:6-8]Property: id
[39:8-10]Operator: : 
[39:10-16]EnumMember: 9KwEAn
[40:6-15]Property: eventName
[40:15-17]Operator: : 
[40:17-27]String: OnErrorLog
[41:6-16]Property: properties
[41:16-18]Operator: : 
[43:4-6]Operator: - 
[43:6-10]Property: kind
[43:10-12]Operator: : 
[43:12-28]EnumMember: CancelAllDialogs
[44:6-8]Property: id
[44:8-10]Operator: : 
[44:10-16]EnumMember: NW7NyY
""";
            AssertTokens(semanticTokenData, doc.Text, expectedText);
        }

        [Fact]
        public void OAI_SemanticToken()
        {
            var world = new World();
            var doc = world.AddFile("oai.mcs.yml");
            var fileSyntax = Assert.IsAssignableFrom<SyntaxNode>(doc.FileModel?.Syntax);
            var requestContext = world.GetRequestContext(doc, 0);
            var semanticTokenVisitor = new SemanticTokenVisitor(requestContext, Mock.Of<ILspLogger>());
            semanticTokenVisitor.Visit(fileSyntax);
            var semanticTokenData = semanticTokenVisitor.SemanticTokenData.ToArray();

            var expectedText = """
[1:0-12]Property: mcs.metadata
[1:12-13]Operator: :
[2:2-15]Property: componentName
[2:15-17]Operator: : 
[2:17-54]String: _OAI_Private_HeartbeatTriggerCallback
[3:0-4]Property: kind
[3:4-6]Operator: : 
[3:6-20]EnumMember: AdaptiveDialog
[4:0-11]Property: beginDialog
[4:11-12]Operator: :
[5:2-6]Property: kind
[5:6-8]Operator: : 
[5:8-25]EnumMember: RecurrenceTrigger
[6:2-4]Property: id
[6:4-6]Operator: : 
[6:6-19]Namespace: oai_framework
[6:19-20]Operator: .
[6:20-25]Namespace: topic
[6:25-26]Operator: .
[6:26-63]EnumMember: _OAI_Private_HeartbeatTriggerCallback
[7:2-9]Property: actions
[7:9-10]Operator: :
[8:4-6]Operator: - 
[8:6-10]Property: kind
[8:10-12]Operator: : 
[8:12-23]EnumMember: BeginDialog
[9:6-8]Property: id
[9:8-10]Operator: : 
[9:10-22]EnumMember: log_callback
[10:6-17]Property: displayName
[10:17-19]Operator: : 
[10:19-36]String: Log trigger fired
[11:6-11]Property: input
[11:11-12]Operator: :
[12:8-15]Property: binding
[12:15-16]Operator: :
[13:10-19]Property: EventName
[13:19-21]Operator: : 
[13:21-45]String: HeartbeatTriggerCallback
[14:10-18]Property: LogLevel
[14:18-20]Operator: : 
[14:20-21]Operator: =
[14:21-27]Keyword: Global
[14:27-28]Operator: .
[14:28-40]Property: LogLevelInfo
[15:10-17]Property: Message
[15:17-19]Operator: : 
[15:19-46]String: In HeartbeatTriggerCallback
[16:10-23]Property: MessageRecord
[16:23-25]Operator: : 
[16:25-26]Operator: =
[16:26-32]Keyword: Global
[16:32-33]Operator: .
[16:33-59]Property: HeartbeatTriggerParameters
[17:10-17]Property: Payload
[17:17-19]Operator: : 
[18:6-12]Property: dialog
[18:12-14]Operator: : 
[18:14-22]Namespace: oai_core
[18:22-23]Operator: .
[18:23-28]Namespace: topic
[18:28-29]Operator: .
[18:29-51]EnumMember: _OAI_Public_LogMessage
[20:4-6]Operator: - 
[20:6-10]Property: kind
[20:10-12]Operator: : 
[20:12-23]EnumMember: BeginDialog
[21:6-8]Property: id
[21:8-10]Operator: : 
[21:10-34]EnumMember: process_trigger_callback
[22:6-17]Property: displayName
[22:17-19]Operator: : 
[22:19-63]String: Process trigger callback using shared helper
[23:6-11]Property: input
[23:11-12]Operator: :
[24:8-15]Property: binding
[24:15-16]Operator: :
[25:10-33]Property: TrafficManagerInputText
[25:33-35]Operator: : 
[25:35-44]String: Heartbeat
[26:10-27]Property: TriggerParameters
[26:27-29]Operator: : 
[26:29-30]Operator: =
[26:30-36]Keyword: Global
[26:36-37]Operator: .
[26:37-63]Property: HeartbeatTriggerParameters
[28:6-12]Property: dialog
[28:12-14]Operator: : 
[28:14-27]Namespace: oai_framework
[28:27-28]Operator: .
[28:28-33]Namespace: topic
[28:33-34]Operator: .
[28:34-69]EnumMember: _OAI_Private_ProcessTriggerCallback
[29:6-12]Property: output
[29:12-13]Operator: :
[30:8-15]Property: binding
[30:15-16]Operator: :
[31:10-34]Property: UpdatedTriggerParameters
[31:34-36]Operator: : 
[31:36-69]String: Global.HeartbeatTriggerParameters
""";
            AssertTokens(semanticTokenData, doc.Text, expectedText);
        }

        [Fact]
        public void TestSemanticTokenHelper()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");
            SyntaxNode? fileSyntax = doc.FileModel?.Syntax;
            var requestContext = world.GetRequestContext(doc, 0);

            if (fileSyntax != null)
            {
                var result = SemanticTokenHelper.GetSemanticTokenData(fileSyntax, requestContext, Mock.Of<ILspLogger>());
                Assert.True(result?.Length > 0);
            }

            // test when fileSyntax is null
            fileSyntax = null;
            var emptyResult = SemanticTokenHelper.GetSemanticTokenData(fileSyntax, requestContext, Mock.Of<ILspLogger>());
            Assert.True(emptyResult?.Length == 0);
        }

        private static void AssertTokens(ReadOnlySpan<int> data, string rawText, string expectedExplanation)
        {
            var explanation = Explain(data, rawText);
            Assert.Equal(expectedExplanation.Trim(), explanation.Trim());
        }

        private static string Explain(ReadOnlySpan<int> data, string rawText)
        {
            if (data.Length % 5 != 0)
            {
                throw new InvalidOperationException("Must have data in pairs of 4");
            }

            var lines = rawText.Split('\n', StringSplitOptions.None);

            int actualLine = 1;
            int actualOffset = 0;
            var sb = new StringBuilder();
            var textBuilder = new StringBuilder();
            while (data.Length > 0)
            {
                var slice = data.Slice(0, 5);
                var deltaLine = slice[0];
                var deltaOffset = slice[1];
                var length = slice[2];
                var type = (SemanticTokenType)slice[3];
                var modifier = (SemanticTokenModifier)slice[4];
                actualLine += deltaLine;
                actualOffset = deltaLine > 0 ? deltaOffset : actualOffset + deltaOffset;

                var position = new Position { Character = actualOffset, Line = actualLine };
                var endPosition = new Position { Character = actualOffset + length, Line = actualLine };

                var line = lines[actualLine - 1];
                int crlf = line[endPosition.Character - 1] == '\r' ? 1 : 0;
                var text = line.Substring(position.Character, length - crlf);

                // var text = "ab";
                sb.AppendLine($"[{position}-{actualOffset + length}]{type}: {text}");
                data = data.Slice(5);
            }

            return sb.ToString();
        }
    }
}
