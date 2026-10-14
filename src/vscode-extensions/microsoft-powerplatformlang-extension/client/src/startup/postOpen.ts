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

/** Queues the entry file of a freshly cloned folder to open after the window reloads. */
export async function prepareClonedWorkspace(
    context: vscode.ExtensionContext,
    workspaceUri: vscode.Uri,
    entryFile?: vscode.Uri,
    telemetryProperties?: Record<string, unknown>): Promise<boolean> {

    if (!entryFile) {
        return false;
    }

    try {
        await vscode.workspace.fs.stat(entryFile);
        await writePostOpenInstruction(context, workspaceUri, entryFile);

        return true;
    } catch {
        logger.logInfo(TelemetryEventsKeys.PostOpenInstruction, undefined, {
            ...telemetryProperties,
            message: 'Post-open instruction skipped: entry file not found',
        });

        return false;
    }
}

/** Opens a freshly cloned folder as the workspace, queueing its entry file to open after the reload. */
export async function openClonedWorkspace(
    context: vscode.ExtensionContext,
    workspaceUri: vscode.Uri,
    entryFile?: vscode.Uri,
    telemetryProperties?: Record<string, unknown>): Promise<void> {

    await prepareClonedWorkspace(context, workspaceUri, entryFile, telemetryProperties);
    await logger.dispose();
    await new Promise(resolve => setTimeout(resolve, 1000));
    await vscode.commands.executeCommand('vscode.openFolder', workspaceUri, { forceReuseWindow: true });
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
            logger.logInfo(TelemetryEventsKeys.PostOpenInstruction, undefined, { message: 'Post-open instruction expired' });
            return;
        }

        const intendedWs = vscode.Uri.parse(data.workspaceUri).toString(true);
        const currentWorkspaces = new Set((vscode.workspace.workspaceFolders ?? []).map(f => f.uri.toString(true)));
        if (!currentWorkspaces.has(intendedWs)) {
            await clear();
            logger.logInfo(TelemetryEventsKeys.PostOpenInstruction, undefined, { message: 'Post-open instruction skipped: workspace mismatch' });
            return;
        }

        try {
            const target = vscode.Uri.parse(data.targetFileUri);
            await vscode.workspace.fs.stat(target);
            const doc = await vscode.workspace.openTextDocument(target);
            await vscode.window.showTextDocument(doc, { preview: false });
        } catch (error) {
            // While postOpen is best-effort, we do want to log failures to open the identified file.
            logger.logError(TelemetryEventsKeys.PostOpenError, undefined, {
                message: 'Failed to auto-open agent file after clone',
                error,
            });
        }
    } finally {
        await clear();
    }
}
