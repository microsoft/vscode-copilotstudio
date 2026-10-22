import { Uri } from 'vscode';

export const LOCAL_STATE_SCHEME = 'mcs';
export const REMOTE_STATE_SCHEME = 'mcs-remote';

export const localStateUri = (componentSchemaName: string, workspaceUri: string): Uri =>
    Uri.from({ scheme: LOCAL_STATE_SCHEME, authority: 'local', path: `/Local Cache: ${componentSchemaName}`, query: workspaceUri });

export const remoteStateUri = (componentSchemaName: string, workspaceUri: string): Uri =>
    Uri.from({ scheme: REMOTE_STATE_SCHEME, authority: 'remote', path: `/Remote: ${componentSchemaName}`, query: workspaceUri });

export const componentSchemaName = (agentSchemaName: string, kind: string, name: string): string =>
    `${agentSchemaName}.${kind}.${name}`;
