import path from 'path';
import * as vscode from 'vscode';
import type { CopilotStudioWorkspace } from '../../sync/localWorkspaces';

/**
 * Waits for the first non-empty workspace list, or rejects after timeout.
 */
export async function waitForFirstWorkspace(
    addWorkspaceChangeSubscription: (callback: (ws: CopilotStudioWorkspace[]) => void) => vscode.Disposable,
    getAllWorkspaces: () => CopilotStudioWorkspace[],
    timeoutMs: number = 10000,
): Promise<CopilotStudioWorkspace[]> {
    // 1) Instant hit
    const cached = getAllWorkspaces();
    if (cached.length > 0) {
        return cached;
    }

    let sub: vscode.Disposable | undefined = undefined;

    try {
        // 2) Event promise
        const eventPromise = new Promise<CopilotStudioWorkspace[]>(resolve => {
            sub = addWorkspaceChangeSubscription(ws => {
                if (ws.length > 0) {
                    resolve(ws);
                }
            });
        });

        // 3) Timeout promise
        const timeoutPromise = new Promise<never>((_, reject) => {
            setTimeout(() => {
                reject(new Error('Timed out waiting for workspace list'));
            }, timeoutMs);
        });

        // 4) Race them, auto-unsub in finally
        return await Promise.race([eventPromise, timeoutPromise]);
    } finally {
        (sub as vscode.Disposable | undefined)?.dispose();
    }
}