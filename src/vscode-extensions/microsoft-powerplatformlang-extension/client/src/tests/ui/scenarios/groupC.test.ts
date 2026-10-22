import { describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { SHAPES, variantLine, variantFragment, runsScenario, type ShapeDescriptor } from '../lib/shapes';
import { runScenario, settings, dataverseToken, workPathFor, extensionApi, waitForWorkspace } from '../lib/run';
import { cloneInto, adoptExternalClone, trackedWorkspace, type WorkingCopy } from '../lib/workingCopies';
import { pushWorkspace, runCommand, GET_COMMAND, GET_DONE_TEXT } from '../lib/commands';
import { assertThat, detail } from '../lib/log';
import { readLine, replaceLine } from '../lib/lineEdit';
import { findAgentRoot } from '../lib/tree';
import { componentContent } from '../lib/dataverse';
import { cloneArgs, initArgs, deploymentArgs, deploymentNameFor, pullArgs, pushArgs, requireExternal, repairExternalConnection, type ExternalTool } from '../lib/external';

type Agent = 'vscode' | ExternalTool;

interface Interop {
    id: string;
    name: string;
    clonedBy: Agent;
    syncedBy: Agent;
}

const JOURNEYS: readonly Interop[] = [
    { id: 'C-01', name: 'cloned by pac, pulled and pushed by vscode', clonedBy: 'pac', syncedBy: 'vscode' },
    { id: 'C-02', name: 'cloned by msagent, pulled and pushed by vscode', clonedBy: 'msagent', syncedBy: 'vscode' },
    { id: 'C-03', name: 'cloned by vscode, pulled and pushed by msagent', clonedBy: 'vscode', syncedBy: 'msagent' },
    { id: 'C-04', name: 'cloned by vscode, pulled and pushed by pac', clonedBy: 'vscode', syncedBy: 'pac' },
];

const cloneWith = async (agent: Agent, journey: Interop, shape: ShapeDescriptor, agentId: string, displayName: string): Promise<string> => {
    const root = path.join(workPathFor(journey.id.toLowerCase()), shape.id);
    fs.mkdirSync(root, { recursive: true });

    if (agent === 'vscode') {
        return (await cloneInto(agentId, root)).agentRoot;
    }

    await requireExternal(agent, cloneArgs(agent, { agentId, environmentId: settings.environmentId, projectDirectory: root, displayName }), `${agent} clone for ${journey.id} [${shape.id}]`);

    return findAgentRoot(root);
};

const pullWith = async (agent: Agent, agentRoot: string, agentId: string, copy: WorkingCopy | undefined, label: string): Promise<void> => {
    if (agent === 'vscode') {
        await runCommand(GET_COMMAND, await trackedWorkspace(copy!), { timeoutMs: 900_000, label: `${GET_COMMAND} (${label})`, awaitMessage: GET_DONE_TEXT });
        return;
    }

    await requireExternal(agent, pullArgs(agent, { agentId, projectDirectory: agentRoot }), `${agent} pull for ${label}`);
};

const pushWith = async (agent: Agent, agentRoot: string, agentId: string, copy: WorkingCopy | undefined, label: string, deploymentName?: string): Promise<void> => {
    if (agent === 'vscode') {
        await pushWorkspace(await trackedWorkspace(copy!), label);
        return;
    }

    await requireExternal(agent, pushArgs(agent, { agentId, projectDirectory: agentRoot, deploymentName }), `${agent} push for ${label}`);
};

const publishFromSeed = async (seedPath: string, shape: ShapeDescriptor, value: string, label: string): Promise<void> => {
    const seedWorkspace = await waitForWorkspace(await extensionApi(), seedPath, 120_000);
    await runCommand(GET_COMMAND, seedWorkspace, { timeoutMs: 900_000, label: `${GET_COMMAND} (${label} refresh)`, awaitMessage: GET_DONE_TEXT });

    replaceLine(path.join(seedPath, ...shape.targetRelativePath.split('/')), shape.targetKey, value);
    await pushWorkspace(seedWorkspace, label);
};

describe('lspE2E group C interop', () => {
    for (const shape of SHAPES) {
        for (const journey of JOURNEYS) {
            if (!runsScenario(shape, journey.id)) {
                continue;
            }

            test(`${journey.id} [${shape.id}] ${journey.name}`, { timeout: 2_400_000 }, async () => {
                await runScenario(journey.id, journey.name, shape, async seed => {
                    const agentRoot = await cloneWith(journey.clonedBy, journey, shape, seed.agentId, seed.displayName);
                    assertThat(`${journey.clonedBy} produced a workspace`, 'present', fs.existsSync(path.join(agentRoot, '.mcs', 'conn.json')) ? 'present' : 'absent', fs.existsSync(path.join(agentRoot, '.mcs', 'conn.json')));

                    const targetPath = path.join(agentRoot, ...shape.targetRelativePath.split('/'));
                    assertThat(`${journey.clonedBy} brought down ${shape.targetRelativePath}`, 'present', fs.existsSync(targetPath) ? 'present' : 'absent', fs.existsSync(targetPath));

                    let copy: WorkingCopy | undefined;

                    if (journey.syncedBy === 'vscode') {
                        const repaired = repairExternalConnection(agentRoot, seed.seedPath);

                        if (repaired.length > 0) {
                            detail('connection repaired', `${journey.clonedBy} left ${repaired.join(', ')} unset; the extension cannot sync the workspace until they are filled`);
                        }

                        copy = await adoptExternalClone(agentRoot);
                    }

                    const remoteFragment = variantFragment(shape, `${journey.id} remote ${settings.suffix}`);
                    await publishFromSeed(seed.seedPath, shape, variantLine(shape, `${journey.id} remote ${settings.suffix}`), `${journey.id} [${shape.id}] seed edit`);
                    detail('cloud moved', `${shape.targetRelativePath} → ${remoteFragment}; the clone is now behind, so its pull has something to bring down`);

                    let deploymentName: string | undefined;

                    if (journey.syncedBy !== 'vscode' && journey.syncedBy !== journey.clonedBy) {
                        const register = initArgs(journey.syncedBy, { agentName: seed.displayName, projectDirectory: agentRoot });

                        if (register.length > 0) {
                            detail('registering', `${journey.syncedBy} did not create this project, so it must adopt it before it can sync`);
                            await requireExternal(journey.syncedBy, register, `${journey.syncedBy} init for ${journey.id} [${shape.id}]`);

                            deploymentName = deploymentNameFor(settings.suffix, shape.id);
                            detail('deployment', `${deploymentName} — an adopted project has no slot to deploy into until one is created`);
                            await requireExternal(journey.syncedBy, deploymentArgs(journey.syncedBy, { projectDirectory: agentRoot, deploymentName }), `${journey.syncedBy} deployment for ${journey.id} [${shape.id}]`);
                        }
                    }

                    await pullWith(journey.syncedBy, agentRoot, seed.agentId, copy, `${journey.id} [${shape.id}]`);

                    assertThat(
                        `the pull ${journey.syncedBy} ran brought down the change made after the clone`,
                        remoteFragment,
                        readLine(targetPath, shape.targetKey).includes(remoteFragment) ? 'present' : 'absent',
                        readLine(targetPath, shape.targetKey).includes(remoteFragment));

                    const value = variantLine(shape, `${journey.id} ${settings.suffix}`);
                    const fragment = variantFragment(shape, `${journey.id} ${settings.suffix}`);
                    replaceLine(targetPath, shape.targetKey, value);
                    detail('edit', `${shape.targetRelativePath} → ${value}`);

                    await pushWith(journey.syncedBy, agentRoot, seed.agentId, copy, `${journey.id} [${shape.id}]`, deploymentName);

                    const content = await componentContent(seed.connection, await dataverseToken(seed.connection.DataverseEndpoint), shape.componentName);
                    assertThat(`the edit ${journey.syncedBy} pushed reached the cloud`, fragment, content.includes(fragment) ? 'present' : 'absent', content.includes(fragment));
                });
            });
        }
    }
});
