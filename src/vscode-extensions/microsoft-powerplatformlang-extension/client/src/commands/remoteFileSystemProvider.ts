import * as vscode from 'vscode';
import { REMOTE_STATE_SCHEME, RemoteFileSystem } from '../sync/remoteFileSystem';

export const registerRemoteFileSystemProvider = (context: vscode.ExtensionContext) => {
    const remoteProvider = new RemoteFileSystem();
    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(REMOTE_STATE_SCHEME, remoteProvider, { isReadonly: true })
    );
};