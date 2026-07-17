import * as vscode from 'vscode';

interface SetVariableInsertion {
  line: number;
  character: number;
  textBeforeValue: string;
  textAfterValue: string;
}

interface CreateGlobalVariableArgs {
  documentUri: string;
  variableName: string;
  newFileUri: string;
  fileContent: string;
  setVariable?: SetVariableInsertion;
}

export const registerCreateGlobalVariableCommand = (context: vscode.ExtensionContext): void => {
  const command = vscode.commands.registerCommand(
    'microsoft-copilot-studio.createGlobalVariable',
    async (args?: CreateGlobalVariableArgs) => {
      if (!args) {
        return;
      }

      let value = '""';
      if (args.setVariable) {
        const entered = await vscode.window.showInputBox({
          title: `Initialize Global.${args.variableName}`,
          prompt: "Enter the initial global variable value. This sets the variable's type.",
          value: '""',
        });
        if (entered === undefined) {
          return;
        }
        value = entered;
      }

      const edit = new vscode.WorkspaceEdit();
      const newFileUri = vscode.Uri.parse(args.newFileUri);
      edit.createFile(newFileUri, { ignoreIfExists: true, contents: new TextEncoder().encode(args.fileContent) });

      if (args.setVariable) {
        const documentUri = vscode.Uri.parse(args.documentUri);
        const position = new vscode.Position(args.setVariable.line, args.setVariable.character);
        edit.insert(documentUri, position, args.setVariable.textBeforeValue + value + args.setVariable.textAfterValue);
      }

      const applied = await vscode.workspace.applyEdit(edit);
      if (!applied) {
        void vscode.window.showErrorMessage(`Failed to apply edits to create Global.${args.variableName}.`);
      }
    }
  );
  context.subscriptions.push(command);
};
