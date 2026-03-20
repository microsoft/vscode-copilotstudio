namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions
{
    using Microsoft.Agents.ObjectModel;
    using System;

    internal class UnsupportedBotElementException : Exception
    {
        public Type BotElementType { get; }

        public UnsupportedBotElementException(string reason, BotElement? botElement)
            : this(reason, botElement?.GetType() ?? typeof(Nullable))
        {
        }

        public UnsupportedBotElementException(string reason, Type botElementType)
            : base($"BotElement Type not supported in current context : {botElementType.Name}. Reason={reason}")
        {
            BotElementType = botElementType;
        }
    }
}
