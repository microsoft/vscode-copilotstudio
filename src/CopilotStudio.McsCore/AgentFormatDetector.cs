namespace Microsoft.CopilotStudio.McsCore
{
    using Microsoft.Agents.ObjectModel;

    public static class AgentFormatDetector
    {
        private const string CliTemplatePrefix = "cliagent-";
        private const string ClassicTemplatePrefix = "default-";

        public static AgentFormat Detect(DefinitionBase? definition)
        {
            var bot = (definition as BotDefinition)?.Entity;
            var template = bot?.Template;
            if (!string.IsNullOrEmpty(template))
            {
                if (template.StartsWith(CliTemplatePrefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    return AgentFormat.Cli;
                }
                if (template.StartsWith(ClassicTemplatePrefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    return AgentFormat.Classic;
                }
            }

            if (bot.IsCliAgent())
            {
                return AgentFormat.Cli;
            }

            return AgentFormat.Unknown;
        }

        public static AgentFormat DetectFromFolder(string agentFolder)
        {
            if (string.IsNullOrEmpty(agentFolder))
            {
                return AgentFormat.Unknown;
            }

            var settingsPath = System.IO.Path.Combine(agentFolder, "settings.mcs.yml");
            if (!System.IO.File.Exists(settingsPath))
            {
                return AgentFormat.Unknown;
            }

            string text;
            try
            {
                text = System.IO.File.ReadAllText(settingsPath);
            }
            catch
            {
                return AgentFormat.Unknown;
            }

            if (text.IndexOf("template: " + CliTemplatePrefix, System.StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("CLICopilotRecognizer", System.StringComparison.Ordinal) >= 0
                || text.IndexOf("CLIAgentRecognizer", System.StringComparison.Ordinal) >= 0)
            {
                return AgentFormat.Cli;
            }

            if (text.IndexOf("template: " + ClassicTemplatePrefix, System.StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("GenerativeAIRecognizer", System.StringComparison.Ordinal) >= 0)
            {
                return AgentFormat.Classic;
            }

            return AgentFormat.Unknown;
        }
    }
}
