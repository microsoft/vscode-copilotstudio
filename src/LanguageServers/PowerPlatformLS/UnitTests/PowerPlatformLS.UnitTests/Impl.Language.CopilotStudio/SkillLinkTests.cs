namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using System;
    using System.IO;
    using System.Text;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Xunit;

    public class SkillLinkTests
    {
        private const string BotName = "crf9a_nagentn1_T2U1EY";

        [Fact]
        public void ReadSchemaLinks_MapsSkillNameToLinkedSchema()
        {
            var accessor = new InMemoryFileWriter();
            WritePackagedSkill(accessor, "get-us-weather", $"{BotName}.skill.get-us-weather_peu");

            var links = SkillLink.ReadSchemaLinks(accessor);

            Assert.Single(links);
            Assert.Equal($"{BotName}.skill.get-us-weather_peu", links["get-us-weather"]);
        }

        [Fact]
        public void ReadSchemaLinks_SkillWithoutLink_IsOmitted()
        {
            var accessor = new InMemoryFileWriter();
            WriteSkillFile(accessor, "get-us-weather");

            var links = SkillLink.ReadSchemaLinks(accessor);

            Assert.Empty(links);
        }

        [Fact]
        public void ReadSchemaLinks_MalformedLink_IsOmittedWithoutThrowing()
        {
            var accessor = new InMemoryFileWriter();
            WriteSkillFile(accessor, "get-us-weather");
            WriteText(accessor, "behaviors/get-us-weather/.skill.json", "{ this is not valid json");

            var links = SkillLink.ReadSchemaLinks(accessor);

            Assert.Empty(links);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("{ this is not valid json")]
        public void ReadSchemaLinks_MissingOrMalformedLink_UniqueCloudFolder_RecoversSchema(string? linkContents)
        {
            var accessor = new InMemoryFileWriter();
            WriteSkillFile(accessor, "get-us-weather");
            if (linkContents != null)
            {
                WriteText(accessor, "behaviors/get-us-weather/.skill.json", linkContents);
            }

            var cloudSkill = CreateSkill($"{BotName}.skill.get-us-weather_peu", "get-us-weather");
            var cloudDefinition = new BotDefinition().WithComponents(new BotComponentBase[] { cloudSkill });

            var links = SkillLink.ReadSchemaLinks(accessor, cloudDefinition);

            Assert.Equal(cloudSkill.SchemaNameString, links["get-us-weather"]);
        }

        [Fact]
        public void ReadSchemaLinks_StaleLinkWithoutCloudMatch_ThrowsWhenRequired()
        {
            var accessor = new InMemoryFileWriter();
            WritePackagedSkill(accessor, "get-us-weather", $"{BotName}.skill.deleted");
            var cloudDefinition = new BotDefinition().WithComponents(new BotComponentBase[] { CreateSkill($"{BotName}.skill.current", "current") });

            Assert.Throws<InvalidOperationException>(() => SkillLink.ReadSchemaLinks(accessor, cloudDefinition, throwOnInvalidLink: true));
        }

        [Fact]
        public void ReadSchemaLinks_ForeignLinkWithNoCloudSkills_ThrowsWhenRequired()
        {
            var accessor = new InMemoryFileWriter();
            WritePackagedSkill(accessor, "get-us-weather", "other_agent.skill.get-us-weather");
            var entity = new BotEntity.Builder { SchemaName = BotName }.Build();
            var cloudDefinition = new BotDefinition(entity: entity);

            Assert.Throws<InvalidOperationException>(() => SkillLink.ReadSchemaLinks(accessor, cloudDefinition, throwOnInvalidLink: true));
        }

        [Fact]
        public void ReadSchemaLinks_LinkedSchemaHasCollisionSuffix_EmptyComponentCloudDefinition_RecoversSchema()
        {
            var accessor = new InMemoryFileWriter();
            WritePackagedSkill(accessor, "get-us-weather", $"{BotName}.skill.get-us-weather_e1W");
            var entity = new BotEntity.Builder { SchemaName = BotName }.Build();
            var cloudDefinition = new BotDefinition(entity: entity);

            var links = SkillLink.ReadSchemaLinks(accessor, cloudDefinition);

            Assert.Equal($"{BotName}.skill.get-us-weather_e1W", links["get-us-weather"]);
        }

        [Fact]
        public void ReadSchemaLinks_PayloadSidecar_NotTreatedAsSkillAnchor()
        {
            var accessor = new InMemoryFileWriter();
            WritePackagedSkill(accessor, "get-us-weather", $"{BotName}.skill.get-us-weather_peu");
            WriteText(accessor, "behaviors/get-us-weather/skillmd_dWNAJ.mcs.yml", "mcs.metadata:\n  componentName: ./SKILL.md\n");

            var links = SkillLink.ReadSchemaLinks(accessor);

            Assert.Single(links);
            Assert.True(links.ContainsKey("get-us-weather"));
            Assert.False(links.ContainsKey("skillmd_dWNAJ"));
        }

        [Theory]
        [InlineData("behaviors/get-us-weather.mcs.yml", true, "get-us-weather")]
        [InlineData("behaviors/get-us-weather/skillmd_dWNAJ.mcs.yml", false, "")]
        [InlineData("behaviors/get-us-weather/SKILL.md", false, "")]
        [InlineData("topics/Greeting.mcs.yml", false, "")]
        public void TryGetSkillName_MatchesOnlyTopLevelSkillFiles(string path, bool expected, string expectedName)
        {
            var matched = SkillLink.TryGetSkillName(new AgentFilePath(path), out var skillName);

            Assert.Equal(expected, matched);
            Assert.Equal(expectedName, skillName);
        }

        private static void WritePackagedSkill(InMemoryFileWriter accessor, string skillName, string schemaName)
        {
            WriteSkillFile(accessor, skillName);
            var link = SchemaLink.Serialize(new SchemaLinkData { SchemaName = schemaName, FolderName = skillName });
            WriteText(accessor, $"behaviors/{skillName}/.skill.json", link);
        }

        private static void WriteSkillFile(InMemoryFileWriter accessor, string skillName)
        {
            WriteText(accessor, $"behaviors/{skillName}.mcs.yml", $"mcs.metadata:\n  componentName: {skillName}\nkind: InlineAgentSkill\ncontent: placeholder\n");
        }

        private static DialogComponent CreateSkill(string schemaName, string displayName) => new DialogComponent(
            schemaName: schemaName,
            displayName: displayName,
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new InlineAgentSkill.Builder { Content = "placeholder" }.Build());

        private static void WriteText(InMemoryFileWriter accessor, string path, string content)
        {
            using var stream = accessor.OpenWrite(new AgentFilePath(path));
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }
    }
}
