import * as vscode from 'vscode';
import { LOCAL_STATE_SCHEME, OriginalFileSystem } from '../sync/originalFileSystem';

export const registerOriginalFileSystemProvider = (context: vscode.ExtensionContext) => {
    const originalProvider = new OriginalFileSystem();
    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(LOCAL_STATE_SCHEME, originalProvider, { isReadonly: true })
    );
};