import * as vscode from 'vscode';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';

export interface PostOpenInstruction {
    workspaceUri: string; // workspace folder to validate
    targetFileUri: string; // file to open within that workspace
    expiresAt?: number;    // ms epoch TTL
    version: number;       // shape/versioning for future-proofing
}

const GLOBAL_STATE_KEY = 'mcs.postOpenInstruction';
const INSTRUCTION_VERSION = 1;

/** Write a one-shot instruction executed after window reload. */
export async function writePostOpenInstruction(
    context: vscode.ExtensionContext,
    workspace: vscode.Uri,
    targetFile: vscode.Uri,
    expirationMs = 180_000): Promise<void> {
    
    const instruction: PostOpenInstruction = {
        workspaceUri: workspace.toString(),
        targetFileUri: targetFile.toString(),
        expiresAt: Date.now() + expirationMs,
        version: INSTRUCTION_VERSION
    };
    await context.globalState.update(GLOBAL_STATE_KEY, instruction);
}

/** Attempt to consume and act upon any pending post-open instruction. */
export async function maybeOpenFileFromPostOpen(context: vscode.ExtensionContext): Promise<void> {
    const data = context.globalState.get<PostOpenInstruction>(GLOBAL_STATE_KEY);
    if (!data) {
         return;
    }
    
    const clear = async () => context.globalState.update(GLOBAL_STATE_KEY, undefined);

    try {
        if (data.version !== INSTRUCTION_VERSION) {
            await clear();
            return;
        }

        if (data.expiresAt && Date.now() > data.expiresAt) {
            await clear();
            logger.logInfo(TelemetryEventsKeys.PostOpenInstruction, undefined, { phase: 'expire', detail: 'postOpenInstruction expired' });
            return;
        }

        const intendedWs = vscode.Uri.parse(data.workspaceUri).toString(true);
        const currentWorkspaces = new Set((vscode.workspace.workspaceFolders ?? []).map(f => f.uri.toString(true)));
        if (!currentWorkspaces.has(intendedWs)) {
            await clear();
            logger.logInfo(TelemetryEventsKeys.PostOpenInstruction, undefined, { phase: 'workspaceMismatch', detail: 'postOpenInstruction workspace mismatch' });
            return;
        }

        try {
            const target = vscode.Uri.parse(data.targetFileUri);
            await vscode.workspace.fs.stat(target);
            const doc = await vscode.workspace.openTextDocument(target);
            await vscode.window.showTextDocument(doc, { preview: false });
        } catch (err) {
            // While postOpen is best-effort, we do want to log failures to open the identified file.
            logger.logInfo(TelemetryEventsKeys.PostOpenError, undefined, {
                phase: 'openFailed',
                detail: 'Failed to auto-open identified agent file after clone',
                error: err instanceof Error ? err.message : String(err)
            });
        }
    } finally {
        await clear();
    }
}
