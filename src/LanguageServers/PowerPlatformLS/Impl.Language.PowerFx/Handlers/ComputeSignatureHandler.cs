namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerFx;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;
    
    [LspMethodHandler(LspMethods.SignatureHelp)]
    class ComputeSignatureHandler : IRequestHandler<SignatureHelpParams, SignatureHelp, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task<SignatureHelp> HandleRequestAsync(SignatureHelpParams request, RequestContext context, CancellationToken cancellationToken)
        {
            var document = context.Document.As<PowerFxLspDocument>();
            var checkResult = document.FileModel;
            var suggestions = new RecalcEngine().Suggest(checkResult, context.Index);
            var signatureInfo = suggestions.SignatureHelp;
            var signatureHelp = new SignatureHelp()
            {
                ActiveParameter = signatureInfo.ActiveParameter,
                ActiveSignature = signatureInfo.ActiveSignature,
                Signatures = (signatureInfo.Signatures ?? []).Select(signature =>
                {
                    return new SignatureInformation
                    {
                        Label = signature.Label,
                        Documentation = signature.Documentation,
                        Parameters = (signature.Parameters ?? []).Select(parameter =>
                        {
                            return new ParameterInformation()
                            {
                                Label = parameter.Label,
                                Documentation = parameter.Documentation,
                            };
                        }).ToArray(),
                    };
                }).ToArray()
            };

            return Task.FromResult(signatureHelp);
        }
    }
}
