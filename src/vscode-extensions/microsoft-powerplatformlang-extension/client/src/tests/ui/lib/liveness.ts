import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { execFile } from 'node:child_process';
import { pollUntil } from './async';
import { detail } from './log';

const EXTENSION_ID = 'ms-CopilotStudio.vscode-copilotstudio';

export interface ChangeRecord {
    name: string;
    schemaName: string;
    uri: string;
    changeType: number;
    changeKind: string;
}

export interface ExtensionApi {
    addWorkspaceChangeSubscription: (callback: () => void) => vscode.Disposable;
    getAllWorkspaces: () => { workspaceUri: string; displayName: string; type?: number; schemaName?: string; syncInfo?: { agentId?: string; environmentId?: string } }[];
    readLocalChanges: (workspace: unknown) => Promise<ChangeRecord[]>;
    readRemoteChanges: (workspace: unknown) => Promise<ChangeRecord[]>;
}

export interface Liveness {
    api: ExtensionApi;
    serverPath: string;
}

const findServerBinary = (extensionPath: string): string => {
    const explicit = process.env.LSP_PATH;

    if (explicit && fs.existsSync(explicit)) {
        return explicit;
    }

    const lspOut = path.join(extensionPath, 'lspOut');

    if (!fs.existsSync(lspOut)) {
        throw new Error(`no language server directory at ${lspOut} — run 'npm run buildLsp' first`);
    }

    const candidates = fs.readdirSync(lspOut, { recursive: true })
        .filter((name): name is string => typeof name === 'string' && /LanguageServerHost(\.exe)?$/.test(name))
        .map(name => path.join(lspOut, name));

    if (candidates.length === 0) {
        throw new Error(`no LanguageServerHost binary under ${lspOut}`);
    }

    return candidates[0];
};

export const parseServerProcessIds = (output: string, serverPath: string): number[] => {
    const wanted = serverPath.toLowerCase();

    return output.split('\n')
        .filter(line => line.toLowerCase().includes(wanted))
        .map(line => Number((/^\s*(\d+)\s/.exec(line) ?? [])[1]))
        .filter(processId => Number.isFinite(processId) && processId > 0);
};

const listServerProcessIds = (serverPath: string): Promise<number[]> =>
    new Promise(resolve => {
        const windows = process.platform === 'win32';
        const command = windows ? 'powershell' : 'ps';
        const args = windows
            ? ['-NoProfile', '-NonInteractive', '-Command', `Get-CimInstance Win32_Process -Filter "Name='${path.basename(serverPath).replace(/'/g, "''")}'" | ForEach-Object { "$($_.ProcessId) $($_.ExecutablePath)" }; exit 0`]
            : ['-A', '-o', 'pid=,args='];

        execFile(command, args, { windowsHide: true, maxBuffer: 16 * 1024 * 1024 }, (error, stdout) => {
            resolve(error ? [] : parseServerProcessIds(stdout, serverPath));
        });
    });

export const proveRunIsReal = async (): Promise<Liveness> => {
    const extension = vscode.extensions.all.find(candidate => candidate.id === EXTENSION_ID);

    if (!extension) {
        throw new Error(`extension ${EXTENSION_ID} is not loaded in the test host`);
    }

    const api = await extension.activate() as ExtensionApi;

    if (typeof api?.getAllWorkspaces !== 'function' || typeof api?.addWorkspaceChangeSubscription !== 'function') {
        throw new Error('extension activate() did not return the expected API — the suite is not driving the real extension');
    }

    const serverPath = findServerBinary(extension.extensionPath);
    const stats = fs.statSync(serverPath);

    detail('extension', `${EXTENSION_ID}  (${extension.extensionPath})`);
    detail('lsp server', `${serverPath}  ${(stats.size / (1024 * 1024)).toFixed(1)} MB  ${stats.mtime.toISOString()}`);
    detail('transport', process.platform === 'win32' ? 'named pipe' : 'unix socket');

    return { api, serverPath };
};

export const proveLspResponds = async (liveness: Liveness, timeoutMs: number): Promise<void> => {
    const processIds = await pollUntil(
        async () => {
            const running = await listServerProcessIds(liveness.serverPath);
            return running.length > 0 ? running : undefined;
        },
        timeoutMs,
        `a language server started from ${liveness.serverPath}`,
        { pollMs: 1_000 });

    detail('lsp server', `running as pid ${processIds.join(', ')} from ${liveness.serverPath}`);
    detail('lsp client', `tracking ${liveness.api.getAllWorkspaces().length} workspace(s)`);
};
