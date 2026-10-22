import { describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { SHAPES, variantLine, variantFragment, runsScenario, type ShapeDescriptor } from '../lib/shapes';
import { runScenario, settings, dataverseToken } from '../lib/run';
import { ensureCloned, targetFileOf, setTarget, readTarget, trackedWorkspace, type WorkingCopy } from '../lib/workingCopies';
import { runCommand, pushWorkspace, attemptPush, reportedFailure, PREVIEW_COMMAND, GET_COMMAND, APPLY_COMMAND, REMOTE_BLOCK_TEXT, GET_DONE_TEXT, PREVIEW_DONE_TEXT } from '../lib/commands';
import { waitUntil } from '../lib/async';
import { assertThat, detail } from '../lib/log';
import { readLine, replaceLine, valueOf } from '../lib/lineEdit';
import { readScalar } from '../lib/renamer';
import { fileSet, hashTree, diffHashes } from '../lib/tree';
import { componentContent, componentStates, describeComponentDrift, componentLabels, labelsCarry, componentNames, namesCarry } from '../lib/dataverse';
import { addNewComponents, metadataMaterialised, removeComponent, survivingComponents } from '../lib/components';
import { hasConflictMarkers, resolveKeepingValue } from '../lib/conflict';
import { localStateUri, remoteStateUri, componentSchemaName } from '../lib/diffUris';

interface CommandRun {
    seen: { message: string; answer: string | undefined }[];
}

const runOn = async (command: string, copy: WorkingCopy, timeoutMs: number, awaitMessage?: string): Promise<CommandRun> => ({
    seen: await runCommand(command, await trackedWorkspace(copy), { timeoutMs, label: `${command} in ${path.basename(copy.root)}`, awaitMessage }),
});

const pushOn = async (copy: WorkingCopy, timeoutMs: number): Promise<void> =>
    pushWorkspace(await trackedWorkspace(copy), path.basename(copy.root), timeoutMs);


const cloudCarries = async (copy: WorkingCopy, shape: ShapeDescriptor, token: string, fragment: string): Promise<boolean> =>
    (await componentContent(copy.connection, token, shape.componentName)).includes(fragment);


describe('Group B — two working copies, then sync', () => {
    for (const shape of SHAPES) {
        test(`B-01 [${shape.id}] clone into two working copies`, { timeout: 1_800_000 }, async () => {
            await runScenario('B-01', 'clone into two working copies', shape, async seed => {
                const pair = await ensureCloned(shape, seed.agentId);

                for (const [label, copy] of [['v1', pair.v1], ['v2', pair.v2]] as const) {
                    assertThat(`${label} binds the seeded agent`, seed.agentId, copy.connection.AgentId,
                        copy.connection.AgentId.toLowerCase() === seed.agentId.toLowerCase());

                    const marker = path.join(copy.agentRoot, shape.markerFile);
                    assertThat(`${label} carries ${shape.markerFile}`, 'present', fs.existsSync(marker) ? 'present' : 'absent', fs.existsSync(marker));

                    const target = targetFileOf(copy, shape);
                    assertThat(`${label} carries ${shape.targetRelativePath}`, 'present', fs.existsSync(target) ? 'present' : 'absent', fs.existsSync(target));
                }

                const seedFiles = fileSet(seed.seedPath).filter(relative => !relative.startsWith('.mcs/'));
                const cloneFiles = fileSet(pair.v1.agentRoot).filter(relative => !relative.startsWith('.mcs/'));
                const missing = seedFiles.filter(relative => !cloneFiles.includes(relative));
                detail('tree', `seed ${seedFiles.length} files, clone ${cloneFiles.length} files`);
                assertThat('clone kept every seeded file', 'none missing', missing.length === 0 ? 'none missing' : missing.join(', '), missing.length === 0);
            });
        });

        test(`B-02 [${shape.id}] v1's edit reaches the cloud`, { timeout: 1_800_000 }, async () => {
            await runScenario('B-02', "v1's edit reaches the cloud", shape, async seed => {
                const pair = await ensureCloned(shape, seed.agentId);
                const value = variantLine(shape, 'Local 1');
                const fragment = variantFragment(shape, 'Local 1');

                setTarget(pair.v1, shape, value);
                detail('edit', `${shape.targetRelativePath} → ${value}`);

                await pushOn(pair.v1, 900_000);

                await waitUntil(async () => cloudCarries(pair.v1, shape, await dataverseToken(seed.connection.DataverseEndpoint), fragment), 300_000, `cloud carrying '${fragment}'`);
                assertThat('cloud carries the edit', fragment, 'present', true);
            });
        });

        if (runsScenario(shape, 'B-03')) {
            test(`B-03 [${shape.id}] v2 previews it, writes nothing`, { timeout: 1_800_000 }, async () => {
                await runScenario('B-03', 'v2 previews it, writes nothing', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const pending = variantFragment(shape, 'Local 1');
                    const cloudBefore = await componentContent(pair.v2.connection, await dataverseToken(seed.connection.DataverseEndpoint), shape.componentName);

                    assertThat(
                        'the cloud holds a change v2 has not got yet, so preview has something to find',
                        pending,
                        cloudBefore.includes(pending) ? 'present' : 'absent',
                        cloudBefore.includes(pending));

                    const before = hashTree(pair.v2.agentRoot, ['.mcs']);

                    const preview = await runOn(PREVIEW_COMMAND, pair.v2, 900_000, PREVIEW_DONE_TEXT);
                    const previewMessages = preview.seen.map(entry => entry.message);
                    const previewFailure = reportedFailure(previewMessages);
                    assertThat('preview completed without reporting a failure', 'no failure', previewFailure ?? 'no failure', previewFailure === undefined);
                    assertThat(
                        'preview ran to completion rather than doing nothing',
                        PREVIEW_DONE_TEXT,
                        previewMessages.some(message => message.includes(PREVIEW_DONE_TEXT)) ? 'reported' : `never reported; saw [${previewMessages.join(' | ') || 'nothing'}]`,
                        previewMessages.some(message => message.includes(PREVIEW_DONE_TEXT)));

                    const differences = diffHashes(before, hashTree(pair.v2.agentRoot, ['.mcs']));
                    assertThat('preview wrote nothing', 'no file changes', differences.length === 0 ? 'no file changes' : differences.join(', '), differences.length === 0);

                    const stillBehind = readTarget(pair.v2, shape);
                    assertThat('preview left v2 on its own value', 'unchanged', stillBehind.includes(pending) ? 'pulled' : 'unchanged', !stillBehind.includes(pending));

                    const diffPath = path.join(pair.v2.agentRoot, ...shape.diffRelativePath.split('/'));
                    const schema = readScalar(diffPath, 'schemaName') ?? componentSchemaName(seed.schemaName, shape.componentKind, shape.componentName);
                    const remote = (await vscode.workspace.openTextDocument(remoteStateUri(schema, pair.v2.workspaceUri))).getText();
                    detail('remote diff', `${schema} — ${remote.length} chars`);
                    assertThat('the remote side of the diff resolves', '> 0 chars', String(remote.length), remote.length > 0);

                    const run = await runOn(APPLY_COMMAND, pair.v2, 900_000);
                    const blocked = run.seen.find(entry => entry.message.includes(REMOTE_BLOCK_TEXT));
                    assertThat('apply reports the remote change', 'a block modal', blocked ? blocked.message : '(no modal)', Boolean(blocked));
                });
            });
        }

        test(`B-04 [${shape.id}] v2 pulls and matches v1`, { timeout: 1_800_000 }, async () => {
            await runScenario('B-04', 'v2 pulls and matches v1', shape, async seed => {
                const pair = await ensureCloned(shape, seed.agentId);

                await runCommand(GET_COMMAND, await trackedWorkspace(pair.v2), { timeoutMs: 900_000, label: 'get (v2)', awaitMessage: GET_DONE_TEXT });

                const expected = readTarget(pair.v1, shape);
                const actual = readTarget(pair.v2, shape);
                assertThat('v2 matches v1', expected, actual, expected === actual);

                const token = await dataverseToken(seed.connection.DataverseEndpoint);
                const before = await componentStates(pair.v2.connection, token);

                await pushOn(pair.v2, 900_000);

                const after = await componentStates(pair.v2.connection, await dataverseToken(seed.connection.DataverseEndpoint));
                const drift = describeComponentDrift(before, after);

                assertThat(
                    'apply after get touched nothing in the cloud',
                    `${before.length} components unchanged`,
                    drift.length === 0 ? `${after.length} components unchanged` : drift.join(', '),
                    drift.length === 0);
            });
        });

        if (runsScenario(shape, 'B-05')) {
            test(`B-05 [${shape.id}] same-line conflict resolves without loss`, { timeout: 2_400_000 }, async () => {
                await runScenario('B-05', 'same-line conflict resolves without loss', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const localPrefix = `Local 1 ${settings.suffix}`;
                    const conflictPrefix = `Version 2 ${settings.suffix}`;

                    setTarget(pair.v1, shape, variantLine(shape, localPrefix));
                    await pushOn(pair.v1, 900_000);
                    detail('v1 pushed', variantFragment(shape, localPrefix));

                    setTarget(pair.v2, shape, variantLine(shape, conflictPrefix));
                    detail('v2 edited', variantFragment(shape, conflictPrefix));

                    const blockedRun = await runOn(APPLY_COMMAND, pair.v2, 900_000);
                    const blockModal = blockedRun.seen.find(entry => entry.message.includes(REMOTE_BLOCK_TEXT));
                    assertThat('apply is blocked by the remote change', 'a block modal', blockModal ? blockModal.message : '(no modal)', Boolean(blockModal));

                    const stillLocal = readTarget(pair.v2, shape);
                    assertThat('blocked apply left the local edit alone', variantLine(shape, conflictPrefix), stillLocal, stillLocal === variantLine(shape, conflictPrefix));

                    let pullThrew: string | undefined;

                    try {
                        await runOn(GET_COMMAND, pair.v2, 900_000);
                    } catch (error) {
                        pullThrew = (error as Error).message ?? String(error);
                    }

                    const targetPath = targetFileOf(pair.v2, shape);
                    const merged = fs.readFileSync(targetPath, 'utf8');
                    const markers = hasConflictMarkers(merged);
                    detail('merge', markers ? 'conflict markers written' : pullThrew ? `pull threw: ${pullThrew}` : 'no markers');

                    if (!markers) {
                        throw new Error(pullThrew
                            ? `pull threw on conflict — the resolve-and-push journey cannot complete: ${pullThrew}`
                            : `pull produced no conflict markers and did not preserve the local edit. ${targetPath} now reads:\n${merged}`);
                    }

                    const conflictedPush = await attemptPush(await trackedWorkspace(pair.v2), `conflicted ${shape.id}`, 900_000);
                    assertThat('pushing a conflicted file is refused', 'refused', conflictedPush.outcome, conflictedPush.outcome === 'refused');
                    detail('conflicted push', conflictedPush.reason ?? 'the push was accepted while markers were on disk');

                    const refusedContent = await componentContent(pair.v2.connection, await dataverseToken(seed.connection.DataverseEndpoint), shape.componentName);
                    assertThat('the refused push left no markers in the cloud', 'none', hasConflictMarkers(refusedContent) ? 'present' : 'none', !hasConflictMarkers(refusedContent));
                    assertThat('the refused push left the cloud on the pushed value', variantFragment(shape, localPrefix), refusedContent.includes(variantFragment(shape, localPrefix)) ? 'present' : 'absent', refusedContent.includes(variantFragment(shape, localPrefix)));

                    const resolved = resolveKeepingValue(merged, variantFragment(shape, conflictPrefix));
                    fs.writeFileSync(targetPath, resolved.content, 'utf8');
                    detail('resolved', `${resolved.blocksResolved} block(s), kept the ${resolved.keptSide} side`);

                    assertThat('no markers remain on disk', 'none', hasConflictMarkers(resolved.content) ? 'present' : 'none', !hasConflictMarkers(resolved.content));
                    assertThat('local edit survived the merge', variantLine(shape, conflictPrefix), readTarget(pair.v2, shape), readTarget(pair.v2, shape) === variantLine(shape, conflictPrefix));

                    await pushOn(pair.v2, 900_000);

                    const content = await componentContent(pair.v2.connection, await dataverseToken(seed.connection.DataverseEndpoint), shape.componentName);
                    assertThat('cloud carries the resolved value', variantFragment(shape, conflictPrefix), content.includes(variantFragment(shape, conflictPrefix)) ? 'present' : 'absent', content.includes(variantFragment(shape, conflictPrefix)));
                    assertThat('cloud carries no conflict markers', 'none', hasConflictMarkers(content) ? 'present' : 'none', !hasConflictMarkers(content));
                });
            });
        }

        if (runsScenario(shape, 'B-06')) {
            test(`B-06 [${shape.id}] pending change visible as a diff`, { timeout: 1_800_000 }, async () => {
                await runScenario('B-06', 'pending change visible as a diff', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const diffPath = path.join(pair.v1.agentRoot, ...shape.diffRelativePath.split('/'));
                    const original = fs.readFileSync(diffPath, 'utf8');
                    const marker = `diff ${settings.suffix}`;

                    try {
                        replaceLine(diffPath, shape.diffKey, `${shape.diffKey} ${valueOf(readLine(diffPath, shape.diffKey), shape.diffKey)} ${marker}`);
                        const onDisk = fs.readFileSync(diffPath, 'utf8');
                        detail('edit', `${shape.diffRelativePath} → ${readLine(diffPath, shape.diffKey).trim()}`);

                        const schema = readScalar(diffPath, 'schemaName') ?? componentSchemaName(seed.schemaName, shape.componentKind, shape.componentName);
                        const cached = (await vscode.workspace.openTextDocument(localStateUri(schema, pair.v1.workspaceUri))).getText();
                        detail('diff', `${schema} — cache ${cached.length} chars, disk ${onDisk.length} chars`);

                        assertThat('the cache side resolves', '> 0 chars', String(cached.length), cached.length > 0);
                        assertThat('disk carries the pending edit', marker, onDisk.includes(marker) ? 'present' : 'absent', onDisk.includes(marker));
                        assertThat('the cache holds the pre-edit state', `no '${marker}'`, cached.includes(marker) ? 'present' : 'absent', !cached.includes(marker));
                        assertThat('the diff has something to show', 'cache differs from disk', cached === onDisk ? 'identical' : 'different', cached !== onDisk);
                    } finally {
                        fs.writeFileSync(diffPath, original, 'utf8');
                    }
                });
            });
        }

        if (runsScenario(shape, 'B-07')) {
            test(`B-07 [${shape.id}] new components reach the cloud`, { timeout: 2_400_000 }, async () => {
                await runScenario('B-07', 'new components reach the cloud', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const added = addNewComponents(pair.v1.agentRoot, shape, settings.fixturesRoot, seed.schemaName, settings.suffix);
                    detail('added', added.map(fact => `${fact.kind}:${fact.cloudName}`).join(', '));

                    let pushError: string | undefined;

                    try {
                        await pushOn(pair.v1, 1_200_000);
                    } catch (error) {
                        pushError = (error as Error).message ?? String(error);
                    }

                    const survivors = survivingComponents(pair.v1.agentRoot, added);
                    assertThat(
                        'every new component survived the sync',
                        'all still on disk',
                        survivors.missing.length === 0 ? 'all still on disk' : `${survivors.missing.join(', ')} deleted`,
                        survivors.missing.length === 0);

                    if (pushError) {
                        throw new Error(survivors.missing.length > 0
                            ? `the sync deleted ${survivors.missing.join(', ')} before pushing, which left the referencing component orphaned: ${pushError}`
                            : pushError);
                    }

                    const labels = await componentLabels(seed.connection, await dataverseToken(seed.connection.DataverseEndpoint));
                    detail('cloud components', String(labels.length));

                    for (const fact of added) {
                        const present = labelsCarry(labels, fact.cloudName);
                        assertThat(`the new ${fact.kind} reached the cloud`, fact.cloudName, present ? 'present' : 'absent', present);
                    }

                    for (const fact of added.filter(candidate => candidate.expectMaterialisedMetadata)) {
                        const materialised = metadataMaterialised(pair.v1.agentRoot, fact);
                        assertThat(`the product generated ${fact.expectMaterialisedMetadata} for the new ${fact.kind}`, 'present', materialised ? 'present' : 'absent', materialised);
                    }
                });
            });
        }

        if (runsScenario(shape, 'B-08')) {
            test(`B-08 [${shape.id}] deleted components leave both sides`, { timeout: 2_400_000 }, async () => {
                await runScenario('B-08', 'deleted components leave both sides', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const token = await dataverseToken(seed.connection.DataverseEndpoint);

                    const present = namesCarry(await componentNames(seed.connection, token), shape.deletableCloudName);
                    assertThat('the component exists in the cloud before deleting', shape.deletableCloudName, present ? 'present' : 'absent', present);

                    const removed = removeComponent(pair.v1.agentRoot, shape.deletableRelativePath, shape.deletableKind, shape.deletableCloudName);

                    for (const extra of shape.deletableExtraPaths) {
                        removeComponent(pair.v1.agentRoot, extra, shape.deletableKind, shape.deletableCloudName);
                    }

                    detail('deleted locally', [removed.relativePath, ...shape.deletableExtraPaths].join(', '));

                    await pushOn(pair.v1, 1_200_000);

                    await waitUntil(
                        async () => !namesCarry(await componentNames(seed.connection, await dataverseToken(seed.connection.DataverseEndpoint)), shape.deletableCloudName),
                        300_000,
                        `the cloud to drop '${shape.deletableCloudName}'`);
                    assertThat('pushing a local delete removed it from the cloud', 'absent', 'absent', true);

                    const mirror = [shape.deletableRelativePath, ...shape.deletableExtraPaths]
                        .map(relative => path.join(pair.v2.agentRoot, ...relative.split('/')));
                    assertThat('the second copy still has it before pulling', 'present', mirror.every(fs.existsSync) ? 'present' : 'absent', mirror.every(fs.existsSync));

                    await runOn(GET_COMMAND, pair.v2, 900_000, GET_DONE_TEXT);

                    const surviving = mirror.filter(fs.existsSync);
                    assertThat(
                        'pulling a cloud delete removed every file of the component locally',
                        'absent',
                        surviving.length === 0 ? 'absent' : `${surviving.join(', ')} still on disk`,
                        surviving.length === 0);
                });
            });
        }
    }
});
