namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Auth
{
    using System;

    internal interface ITokenManager
    {
        void SetTokens(string dataverseToken, string copilotStudioToken);
    }

    internal interface ITokenProvider
    {
        string GetCopilotStudioToken();

        string GetDataverseToken();
    }

    class TokenManager : ITokenManager, ITokenProvider
    {
        private readonly AsyncLocal<string> _copilotStudioToken = new AsyncLocal<string>();
        private readonly AsyncLocal<string> _dvToken = new AsyncLocal<string>();

        public string GetCopilotStudioToken()
        {
            return _copilotStudioToken.Value ?? throw new InvalidOperationException("Access token is not set.");
        }

        public string GetDataverseToken()
        {
            return _dvToken.Value ?? throw new InvalidOperationException("Access token is not set.");
        }

        public void SetTokens(string dataverseToken, string copilotStudioToken)
        {
            _copilotStudioToken.Value = copilotStudioToken;
            _dvToken.Value = dataverseToken;
        }
    }
}
