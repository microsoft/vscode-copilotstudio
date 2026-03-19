namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    // Provide signature help for Power Fx expressions.
    [LspMethodHandler(LspMethods.SignatureHelp)]
    class PowerFxSignatureHandler : IRequestHandler<SignatureHelpParams, SignatureHelp, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public static SignatureHelp Empty => new SignatureHelp();

        public Task<SignatureHelp> HandleRequestAsync(SignatureHelpParams request, RequestContext requestContext, CancellationToken cancellationToken)
        {
            // SigHelp only works in editing property values, and needs same context.
            var intellisenseEvent = requestContext.Triage(new CompletionContext());
            if (intellisenseEvent is EditPropertyValueCompletionEvent editProp)
            {
                var element = editProp.Element;
                if (element != null)
                {
                    if (element.TryGetExpressionContext(requestContext, out var expressionContext))
                    {
                        var sigHelp = AddPowerFxSignatureHelp(expressionContext);
                        return Task.FromResult(sigHelp ?? Empty);
                    }
                }
            }

            return Task.FromResult(Empty);        
        }

        // Add Power Fx suggestions when editing an expression value
        private SignatureHelp? AddPowerFxSignatureHelp(
            ExpressionContext expressionContext)
        {
            var intellisense = expressionContext.GetPowerFxIntellisense();
            if (intellisense == null)
            {
                return null;
            }

            // Convert to LSP
            var signatureInfo = intellisense.SignatureHelp;
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

            return signatureHelp;
        }
    }
}
