import { installModalStub, installOpenFolderSuppression, installQuickPickAutoSelect, agentItemRule, assetItemRule, environmentRule, type ModalExpectation, type ModalRecord, type QuickPickRule } from './stubs';
import { delay, waitUntil, withTimeout } from './async';
import { detail } from './log';
import * as vscode from 'vscode';

export const APPLY_COMMAND = 'microsoft-copilot-studio.applyChanges';
export const PREVIEW_COMMAND = 'microsoft-copilot-studio.previewChanges';
export const GET_COMMAND = 'microsoft-copilot-studio.getChanges';
export const REATTACH_COMMAND = 'microsoft-copilot-studio.reattachAgent';

export const REATTACH_EXPECTATIONS: readonly ModalExpectation[] = [
    { contains: 'Reattach' },
    { contains: 'Retarget' },
    { contains: 'already exists' },
];

export const reattachQuickPickRules = (environmentId: string): readonly QuickPickRule[] =>
    [environmentRule(environmentId), agentItemRule, assetItemRule];

export const BUSY_TEXT = 'sync is already in progress';
export const REMOTE_BLOCK_TEXT = 'remote change';
export const GET_REMOTE_ACTION = 'Get Remote Changes';
export const APPLY_DONE_TEXT = 'Completed applying changes';
export const GET_DONE_TEXT = 'Completed getting changes';
export const PREVIEW_DONE_TEXT = 'Completed previewing changes';
export const REATTACH_DONE_TEXT = 'reattached successfully';
export const RETARGET_REFRESH_TEXT = 'completed by refreshing';
export const REATTACH_DONE_SIGNALS = [REATTACH_DONE_TEXT, GET_DONE_TEXT, APPLY_DONE_TEXT];
export const REATTACH_ATTACHED_SIGNALS = [...REATTACH_DONE_SIGNALS, RETARGET_REFRESH_TEXT, 'retargeted successfully'];
export const CLOUD_DRIFT_TEXT = 'changed in the cloud';
export const ERROR_GATE_TEXT = 'error(s) in';
export const UNREADABLE_TEXT = 'could not be read';
export const FAILURE_SIGNALS = ['Failed to execute', 'Cannot perform', 'Error applying changes', 'Error getting changes', 'Error previewing changes'];

const BUSY_RETRIES = 30;
const BUSY_WAIT_MS = 10_000;
const SETTLE_WAIT_MS = 2_000;

export interface CommandOptions {
    expectations?: readonly ModalExpectation[];
    quickPickRules?: readonly QuickPickRule[];
    timeoutMs?: number;
    label?: string;
    awaitMessage?: string;
    awaitAnyMessage?: readonly string[];
    awaitTimeoutMs?: number;
}

export const runCommand = async (command: string, workspace: unknown, options: CommandOptions = {}): Promise<ModalRecord[]> => {
    const timeoutMs = options.timeoutMs ?? 900_000;
    const label = options.label ?? command;

    for (let attempt = 1; attempt <= BUSY_RETRIES; attempt++) {
        const modals = installModalStub(options.expectations ?? []);
        const restoreQuickPick = installQuickPickAutoSelect(options.quickPickRules ?? [agentItemRule, assetItemRule]);
        const gate = installOpenFolderSuppression();

        try {
            await withTimeout(gate.execute(command, { workspace }), timeoutMs, label);

            const busy = modals.seen.some(record => record.message.includes(BUSY_TEXT));
            const wanted = options.awaitAnyMessage ?? (options.awaitMessage ? [options.awaitMessage] : undefined);

            if (!busy && wanted) {
                const arrived = (): boolean => modals.seen.some(record => wanted.some(signal => record.message.includes(signal)));
                const seen = await waitUntil(
                    async () => arrived(),
                    options.awaitTimeoutMs ?? timeoutMs,
                    `${label} reporting ${wanted.join(' or ')}`,
                    { pollMs: SETTLE_WAIT_MS }).then(() => true, () => false);

                if (!seen && options.awaitMessage) {
                    throw new Error(`${label} never reported '${options.awaitMessage}'; saw [${modals.seen.map(record => record.message).join(' | ') || 'nothing'}]`);
                }
            }

            if (!busy) {
                return modals.seen;
            }
        } finally {
            gate.restore();
            restoreQuickPick();
            modals.restore();
        }

        detail('waiting', `${label} — another sync is in progress (attempt ${attempt})`);
        await delay(BUSY_WAIT_MS);
    }

    throw new Error(`${label} never ran: another sync was always in progress`);
};

export const reportBlockingDiagnostics = (workspaceUri: string): void => {
    const root = vscode.Uri.parse(workspaceUri).fsPath.toLowerCase();
    let found = 0;

    for (const [uri, diagnostics] of vscode.languages.getDiagnostics()) {
        if (!uri.fsPath.toLowerCase().startsWith(root)) {
            continue;
        }

        for (const diagnostic of diagnostics.filter(candidate => candidate.severity === vscode.DiagnosticSeverity.Error)) {
            found++;
            detail('diagnostic', `${uri.fsPath}: ${diagnostic.message}`);
        }
    }

    if (found === 0) {
        detail('diagnostic', 'the apply was gated on errors, but no error diagnostics are visible to the test');
    }
};

export type PushOutcome = 'applied' | 'refused' | 'inconclusive';

export interface PushAttempt {
    outcome: PushOutcome;
    reason?: string;
}

const REFUSAL_SIGNALS = [ERROR_GATE_TEXT, REMOTE_BLOCK_TEXT, CLOUD_DRIFT_TEXT, UNREADABLE_TEXT];
const TERMINAL_PUSH_SIGNALS = [APPLY_DONE_TEXT, ...REFUSAL_SIGNALS];

export const classifyPush = (messages: readonly string[]): PushAttempt => {
    if (messages.some(message => message.includes(APPLY_DONE_TEXT))) {
        return { outcome: 'applied' };
    }

    const refusal = messages.find(message => REFUSAL_SIGNALS.some(signal => message.includes(signal)));

    return refusal
        ? { outcome: 'refused', reason: refusal }
        : { outcome: 'inconclusive', reason: `${APPLY_COMMAND} reported neither completion nor refusal; saw [${messages.join(' | ') || 'nothing'}]` };
};

export const attemptPush = async (workspace: unknown, label: string, timeoutMs = 900_000): Promise<PushAttempt> => {
    const seen = await runCommand(APPLY_COMMAND, workspace, {
        timeoutMs,
        label: `${APPLY_COMMAND} (${label})`,
        awaitAnyMessage: TERMINAL_PUSH_SIGNALS,
        awaitTimeoutMs: 180_000,
    });

    return classifyPush(seen.map(record => record.message));
};

export const pushWorkspace = async (workspace: unknown, label: string, timeoutMs = 900_000): Promise<void> => {
    for (let attempt = 1; attempt <= 4; attempt++) {
        const seen = await runCommand(APPLY_COMMAND, workspace, {
            expectations: [{ contains: REMOTE_BLOCK_TEXT, answerWith: GET_REMOTE_ACTION }],
            timeoutMs,
            label: `${APPLY_COMMAND} (${label} attempt ${attempt})`,
            awaitAnyMessage: TERMINAL_PUSH_SIGNALS,
            awaitTimeoutMs: 180_000,
        });

        if (seen.some(record => record.message.includes(APPLY_DONE_TEXT))) {
            return;
        }

        const pulled = seen.some(record => record.message.includes(REMOTE_BLOCK_TEXT));
        const drifted = seen.some(record => record.message.includes(CLOUD_DRIFT_TEXT));
        const blocking = seen.find(record => record.message.includes(ERROR_GATE_TEXT) || record.message.includes(UNREADABLE_TEXT));

        if (blocking) {
            reportBlockingDiagnostics((workspace as { workspaceUri: string }).workspaceUri);
            throw new Error(`${APPLY_COMMAND} (${label}) was refused and retrying cannot help: ${blocking.message}`);
        }

        if (!pulled && !drifted) {
            await runCommand(GET_COMMAND, workspace, { timeoutMs, label: `${GET_COMMAND} (${label} settle)`, awaitMessage: GET_DONE_TEXT });
            continue;
        }

        detail('retrying', `${label} — ${drifted ? 'the cloud moved under the push' : 'apply pulled first'}, getting then pushing again`);

        if (drifted) {
            await runCommand(GET_COMMAND, workspace, { timeoutMs, label: `${GET_COMMAND} (${label} drift)`, awaitMessage: GET_DONE_TEXT });
        }
    }

    throw new Error(`${APPLY_COMMAND} (${label}) never reported '${APPLY_DONE_TEXT}'`);
};

export const reportedFailure = (messages: readonly string[]): string | undefined =>
    messages.find(message => FAILURE_SIGNALS.some(signal => message.includes(signal)));
