import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { type SeedShape, type ShapeDescriptor } from './shapes';
import { settings, workPathFor, withTimeout, waitForWorkspace, extensionApi, readConnection } from './run';
import { pollUntil } from './async';
import { installOpenDialogStub, installOpenFolderSuppression, installQuickPickAutoSelect, installShowQuickPickAutoSelect, installModalStub, accountItemRule, agentItemRule, assetItemRule, environmentRule, workflowItemRule, type Restore } from './stubs';
import { findAgentRoot } from './tree';
import { readLine, replaceLine } from './lineEdit';
import { detail } from './log';
import type { AgentConnection } from './dataverse';

const CLONE_COMMAND = 'microsoft-copilot-studio.cloneAgent';
const CLONE_WORKFLOW_COMMAND = 'microsoft-copilot-studio.cloneStandaloneWorkflow';

export interface WorkingCopy {
    root: string;
    agentRoot: string;
    connection: AgentConnection;
    workspaceUri: string;
}

export interface WorkingPair {
    v1: WorkingCopy;
    v2: WorkingCopy;
}

export const agentUrlFor = (agentId: string): string =>
    `https://copilotstudio.preview.microsoft.com/environments/${settings.environmentId}/agents/${agentId}`;

export const targetFileOf = (copy: WorkingCopy, shape: ShapeDescriptor): string =>
    path.join(copy.agentRoot, ...shape.targetRelativePath.split('/'));

export const setTarget = (copy: WorkingCopy, shape: ShapeDescriptor, value: string): void => {
    replaceLine(targetFileOf(copy, shape), shape.targetKey, value);
};

export const readTarget = (copy: WorkingCopy, shape: ShapeDescriptor): string =>
    readLine(targetFileOf(copy, shape), shape.targetKey).trim();

export const cloneInto = async (agentId: string, cloneRoot: string): Promise<WorkingCopy> =>
    runClone(cloneRoot, CLONE_COMMAND, async () => {
        const restore = installQuickPickAutoSelect([agentItemRule, assetItemRule]);
        await vscode.env.clipboard.writeText(agentUrlFor(agentId));

        return restore;
    });

export const cloneWorkflowInto = async (workflowName: string, cloneRoot: string): Promise<WorkingCopy> =>
    runClone(cloneRoot, CLONE_WORKFLOW_COMMAND, async () =>
        installShowQuickPickAutoSelect([accountItemRule, environmentRule(settings.environmentId), workflowItemRule(workflowName)]));

const runClone = async (cloneRoot: string, command: string, prepare: () => Promise<Restore>): Promise<WorkingCopy> => {
    const api = await extensionApi();
    fs.mkdirSync(cloneRoot, { recursive: true });

    const before = new Set(fs.readdirSync(cloneRoot));
    const restoreDialog = installOpenDialogStub(cloneRoot);
    const modals = installModalStub([]);
    const gate = installOpenFolderSuppression();
    const restorePickers = await prepare();

    try {
        await withTimeout(gate.execute(command), 600_000, `${command} into ${path.basename(cloneRoot)}`);
    } finally {
        gate.restore();
        modals.restore();
        restorePickers();
        restoreDialog();
    }

    const cloned = await pollUntil(
        async () => fs.readdirSync(cloneRoot, { withFileTypes: true })
            .filter(entry => entry.isDirectory() && !before.has(entry.name))
            .map(entry => path.join(cloneRoot, entry.name))
            .find(candidate => fs.existsSync(path.join(candidate, '.mcs', 'conn.json'))
                || fs.readdirSync(candidate, { withFileTypes: true }).some(inner => inner.isDirectory() && fs.existsSync(path.join(candidate, inner.name, '.mcs', 'conn.json')))),
        120_000,
        `a cloned workspace appearing under ${cloneRoot}`,
        { pollMs: 1_000 });

    const agentRoot = fs.existsSync(path.join(cloned, '.mcs', 'conn.json')) ? cloned : findAgentRoot(cloned);
    const workspace = await waitForWorkspace(api, agentRoot, 120_000);
    detail('cloned', `${path.basename(cloneRoot)} → ${path.relative(cloneRoot, agentRoot)}`);

    return { root: cloned, agentRoot, connection: readConnection(agentRoot), workspaceUri: workspace.workspaceUri };
};

const pairs = new Map<string, Promise<WorkingPair>>();

const ensurePair = (shape: SeedShape, clone: (cloneRoot: string) => Promise<WorkingCopy>): Promise<WorkingPair> => {
    if (!pairs.has(shape.id)) {
        pairs.set(shape.id, (async () => ({
            v1: await clone(path.join(workPathFor('v1'), shape.id)),
            v2: await clone(path.join(workPathFor('v2'), shape.id)),
        }))());
    }

    return pairs.get(shape.id)!;
};

export const ensureCloned = (shape: ShapeDescriptor, agentId: string): Promise<WorkingPair> =>
    ensurePair(shape, cloneRoot => cloneInto(agentId, cloneRoot));

export const ensureWorkflowCloned = (shape: SeedShape, workflowName: string): Promise<WorkingPair> =>
    ensurePair(shape, cloneRoot => cloneWorkflowInto(workflowName, cloneRoot));

export const trackedWorkspace = async (copy: WorkingCopy) => waitForWorkspace(await extensionApi(), copy.agentRoot, 30_000);

export const adoptExternalClone = async (agentRoot: string): Promise<WorkingCopy> => {
    const api = await extensionApi();
    const workspace = await waitForWorkspace(api, agentRoot, 180_000);
    detail('adopted', `${path.basename(agentRoot)} is tracked by the extension`);

    return { root: agentRoot, agentRoot, connection: readConnection(agentRoot), workspaceUri: workspace.workspaceUri };
};