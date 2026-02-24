namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Auth
{
    using System;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    internal abstract class AuthorizeRequestHandler : DelegatingHandler
    {
        private readonly ITokenProvider _tokenProvider;

        public AuthorizeRequestHandler(ITokenProvider tokenProvider)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetToken(_tokenProvider));
            return await base.SendAsync(request, cancellationToken);
        }

        protected abstract string GetToken(ITokenProvider tokenProvider);
    }

    internal class AuthorizeDataverseRequestHandler(ITokenProvider tokenProvider) : AuthorizeRequestHandler(tokenProvider)
    {
        protected override string GetToken(ITokenProvider tokenProvider) => tokenProvider.GetDataverseToken();
    }

    internal class AuthorizeCopilotStudioRequestHandler(ITokenProvider tokenProvider) : AuthorizeRequestHandler(tokenProvider)
    {
        protected override string GetToken(ITokenProvider tokenProvider) => tokenProvider.GetCopilotStudioToken();
    }
}
