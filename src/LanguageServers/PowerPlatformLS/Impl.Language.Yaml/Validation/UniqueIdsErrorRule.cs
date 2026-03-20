namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Validation
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model;

    internal class UniqueIdsErrorRule : IValidationRule<YamlLspDocument>
    {
        IEnumerable<Diagnostic> IValidationRule<YamlLspDocument>.ComputeValidation(RequestContext _, YamlLspDocument document)
        {
            var semanticModel = document.FileModel ?? throw new ArgumentNullException(nameof(document.FileModel));
            var idToNode = new Dictionary<string, YNodeProperty>();
            foreach (var node in semanticModel.AllPropertyNodes)
            {
                if (node.Name == "id")
                {
                    var idValueString = node.ScalarValue;
                    if (idValueString == null)
                    {
                        yield return new Diagnostic
                        {
                            Range = node.ValueRange.ToLspRange(),
                            Severity = DiagnosticSeverity.Error,
                            Message = $"Id property should be a string.",
                        };
                    }
                    else if (idToNode.TryGetValue(idValueString, out var prevProp))
                    {
                        yield return new Diagnostic
                        {
                            Range = node.ValueRange.ToLspRange(),
                            Severity = DiagnosticSeverity.Error,
                            Message = $"Duplicate id '{idValueString}' found at line {prevProp?.ValueRange.Start.Line}."
                        };
                    }
                    else
                    {
                        idToNode[idValueString] = node;
                    }
                }
            }
        }
    }
}