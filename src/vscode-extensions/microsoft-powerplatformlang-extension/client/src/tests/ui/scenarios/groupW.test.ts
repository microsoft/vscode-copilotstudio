import { describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { WORKFLOW_SHAPES } from '../lib/shapes';
import { runScenario, settings, normaliseId, dataverseToken, type SeededAgent } from '../lib/run';
import { ensureWorkflowCloned, trackedWorkspace } from '../lib/workingCopies';
import { pushWorkspace, runCommand, GET_COMMAND, GET_DONE_TEXT } from '../lib/commands';
import { cloudWorkflow, type WorkflowRow } from '../lib/dataverse';
import { setWorkflowDescription, workflowDescription, workflowIdentity } from '../lib/workflows';
import { pollUntil } from '../lib/async';
import { assertThat, detail } from '../lib/log';
import { fileSet } from '../lib/tree';

const PUSHED_DESCRIPTION = `pushed by the end to end suite, run ${settings.suffix}`;
const CLOUD_WAIT_MS = 300_000;

const awaitCloud = (seed: SeededAgent, description: string, accept: (row: WorkflowRow) => boolean): Promise<WorkflowRow> =>
    pollUntil(
        async () => {
            const row = await cloudWorkflow(seed.connection.DataverseEndpoint, await dataverseToken(seed.connection.DataverseEndpoint), seed.agentId);

            return row !== undefined && accept(row) ? row : undefined;
        },
        CLOUD_WAIT_MS,
        description);

describe('Group W — a standalone workflow, with no agent anywhere', () => {
    for (const shape of WORKFLOW_SHAPES) {
        test(`W-01 [${shape.id}] reattaching a workflow fixture creates the cloud flow`, { timeout: 1_800_000 }, async () => {
            await runScenario('W-01', 'reattach binds a standalone workflow to the cloud', shape, async seed => {
                assertThat('workflow id returned', 'a guid', seed.agentId, /^[0-9a-f-]{36}$/i.test(seed.agentId));
                assertThat('bound environment', settings.environmentId, seed.connection.EnvironmentId,
                    normaliseId(seed.connection.EnvironmentId) === normaliseId(settings.environmentId));

                const marker = path.join(seed.seedPath, shape.markerFile);
                assertThat('project marker present', shape.markerFile, fs.existsSync(marker) ? 'present' : 'absent', fs.existsSync(marker));

                const local = workflowIdentity(seed.seedPath);
                detail('workflow', `${local.workflowId}  ${local.name}`);
                assertThat('the workspace holds the workflow the run minted', seed.agentId, local.workflowId, local.workflowId === seed.agentId);
                assertThat('the workspace kept its definition', 'present', fs.existsSync(local.definitionPath) ? 'present' : 'absent', fs.existsSync(local.definitionPath));

                const cloud = await awaitCloud(seed, `the cloud holding workflow ${seed.agentId}`, () => true);
                detail('cloud', `${cloud.workflowid}  ${cloud.name}`);
                assertThat('the cloud names the pushed workflow', seed.displayName, cloud.name, cloud.name === seed.displayName);
            }, true);
        });

        test(`W-02 [${shape.id}] cloning the cloud flow into two working copies`, { timeout: 1_800_000 }, async () => {
            await runScenario('W-02', 'clone the cloud flow into two working copies', shape, async seed => {
                const pair = await ensureWorkflowCloned(shape, seed.displayName);

                for (const [label, copy] of [['v1', pair.v1], ['v2', pair.v2]] as const) {
                    const marker = path.join(copy.agentRoot, shape.markerFile);
                    assertThat(`${label} carries ${shape.markerFile}`, 'present', fs.existsSync(marker) ? 'present' : 'absent', fs.existsSync(marker));

                    const identity = workflowIdentity(copy.agentRoot);
                    assertThat(`${label} binds the seeded workflow`, seed.agentId, identity.workflowId,
                        normaliseId(identity.workflowId) === normaliseId(seed.agentId));
                    assertThat(`${label} names the cloud workflow`, seed.displayName, identity.name, identity.name === seed.displayName);
                    assertThat(`${label} carries the definition`, 'present', fs.existsSync(identity.definitionPath) ? 'present' : 'absent', fs.existsSync(identity.definitionPath));
                }

                const seedFiles = fileSet(seed.seedPath).filter(relative => !relative.startsWith('.mcs/'));
                const cloneFiles = fileSet(pair.v1.agentRoot).filter(relative => !relative.startsWith('.mcs/'));
                const missing = seedFiles.filter(relative => !cloneFiles.includes(relative));
                detail('tree', `seed ${seedFiles.length} files, clone ${cloneFiles.length} files`);
                assertThat('clone kept every seeded file', 'none missing', missing.length === 0 ? 'none missing' : missing.join(', '), missing.length === 0);
            });
        });

        test(`W-03 [${shape.id}] v1's workflow edit reaches the cloud`, { timeout: 1_800_000 }, async () => {
            await runScenario('W-03', "v1's workflow edit reaches the cloud", shape, async seed => {
                const pair = await ensureWorkflowCloned(shape, seed.displayName);

                setWorkflowDescription(pair.v1.agentRoot, PUSHED_DESCRIPTION);
                detail('edit', `description → ${PUSHED_DESCRIPTION}`);

                await pushWorkspace(await trackedWorkspace(pair.v1), path.basename(pair.v1.root), 900_000);

                const cloud = await awaitCloud(seed, `the cloud carrying '${PUSHED_DESCRIPTION}'`, row => row.description === PUSHED_DESCRIPTION);
                assertThat('the cloud carries the edit', PUSHED_DESCRIPTION, cloud.description ?? '(none)', cloud.description === PUSHED_DESCRIPTION);
                assertThat('the push left the workflow on its own identity', seed.agentId, cloud.workflowid,
                    normaliseId(cloud.workflowid) === normaliseId(seed.agentId));
            });
        });

        test(`W-04 [${shape.id}] v2 pulls the workflow and matches v1`, { timeout: 1_800_000 }, async () => {
            await runScenario('W-04', 'v2 pulls the workflow and matches v1', shape, async seed => {
                const pair = await ensureWorkflowCloned(shape, seed.displayName);
                const expected = workflowDescription(pair.v1.agentRoot);
                const behind = workflowDescription(pair.v2.agentRoot);

                assertThat('the cloud holds a change v2 has not got yet, so the get has something to find', `not '${expected}'`, `'${behind}'`, behind !== expected);

                await runCommand(GET_COMMAND, await trackedWorkspace(pair.v2), {
                    timeoutMs: 900_000,
                    label: `${GET_COMMAND} (v2 workflow)`,
                    awaitMessage: GET_DONE_TEXT,
                });

                const actual = workflowDescription(pair.v2.agentRoot);
                detail('pulled', `description → ${actual || '(empty)'}`);
                assertThat('v2 matches v1', expected, actual, expected === actual);

                const identity = workflowIdentity(pair.v2.agentRoot);
                assertThat('the get left v2 on the same workflow', seed.agentId, identity.workflowId,
                    normaliseId(identity.workflowId) === normaliseId(seed.agentId));

                const cloud = await cloudWorkflow(seed.connection.DataverseEndpoint, await dataverseToken(seed.connection.DataverseEndpoint), seed.agentId);
                assertThat('v2 matches the cloud', cloud?.description ?? '(none)', actual, actual === (cloud?.description ?? ''));
            });
        });
    }
});
