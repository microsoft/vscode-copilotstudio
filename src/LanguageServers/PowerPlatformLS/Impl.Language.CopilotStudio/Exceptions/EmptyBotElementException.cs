namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions
{
    using System;

    internal class EmptyBotElementException : Exception
    {
        public EmptyBotElementException()
            : base("BotElement is null. Only DialogBase Types are supported. Make sure 'kind' is specified.")
        {
        }
    }
}
