import * as vscode from 'vscode';

const CONNECTION_REFERENCE_DIAGNOSTIC_CODES = new Set<string>([
  'UnknownConnectionReference',
  'UndeclaredConnectionReference',
  'UnboundConnectionReference'
]);

const isConnectionReferenceDiagnostic = (diagnostic: vscode.Diagnostic): boolean => {
  const code = typeof diagnostic.code === 'object' && diagnostic.code !== null ? diagnostic.code.value : diagnostic.code;
  return typeof code === 'string' && CONNECTION_REFERENCE_DIAGNOSTIC_CODES.has(code);
};

class ConnectionReferenceCodeActionProvider implements vscode.CodeActionProvider {
  public static readonly providedKinds = [vscode.CodeActionKind.QuickFix];

  public provideCodeActions(document: vscode.TextDocument, _range: vscode.Range | vscode.Selection, context: vscode.CodeActionContext): vscode.CodeAction[] {
    const relevant = context.diagnostics.filter(isConnectionReferenceDiagnostic);
    if (!relevant.length) {
      return [];
    }

    const diagnostic = relevant[0];
    const logicalName = extractLogicalName(document, diagnostic);

    const manage = new vscode.CodeAction('Manage connections…', vscode.CodeActionKind.QuickFix);
    manage.command = {
      command: 'microsoft-copilot-studio.manageConnections',
      title: 'Manage connections',
      arguments: logicalName ? [{ connectionReferenceLogicalName: logicalName }] : undefined
    };
    manage.diagnostics = relevant;

    const add = new vscode.CodeAction('Add new connection reference…', vscode.CodeActionKind.QuickFix);
    add.command = {
      command: 'microsoft-copilot-studio.addConnectionReferenceForDiagnostic',
      title: 'Add new connection reference',
      arguments: [{ documentUri: document.uri, range: diagnostic.range, currentValue: logicalName ?? '' }]
    };
    add.diagnostics = relevant;
    add.isPreferred = true;

    return [add, manage];
  }
}

const extractLogicalName = (document: vscode.TextDocument, diagnostic: vscode.Diagnostic): string | undefined => {
  const text = document.getText(diagnostic.range).trim();
  return text.length > 0 ? text : undefined;
};

export const registerConnectionReferenceQuickFix = (context: vscode.ExtensionContext): void => {
  const provider = vscode.languages.registerCodeActionsProvider(
    { language: 'CopilotStudio' },
    new ConnectionReferenceCodeActionProvider(),
    { providedCodeActionKinds: ConnectionReferenceCodeActionProvider.providedKinds }
  );
  context.subscriptions.push(provider);
};
