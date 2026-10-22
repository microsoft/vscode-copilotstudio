import { describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { SHAPES, runsScenario, variantLine, type ShapeDescriptor } from '../lib/shapes';
import { runScenario, extensionApi, settings } from '../lib/run';
import { ensureCloned, targetFileOf, setTarget, trackedWorkspace } from '../lib/workingCopies';
import { pushWorkspace } from '../lib/commands';
import { pollUntil } from '../lib/async';
import { assertThat, detail } from '../lib/log';
import type { ChangeRecord } from '../lib/liveness';

const CHANGE_TYPE = ['create', 'update', 'delete'];

const describeChanges = (changes: readonly ChangeRecord[]): string =>
    changes.length === 0
        ? 'none'
        : changes.map(change => `${change.name} [${change.changeKind} ${CHANGE_TYPE[change.changeType] ?? change.changeType}]`).join(', ');

const mentions = (change: ChangeRecord, shape: ShapeDescriptor): boolean =>
    change.name === shape.componentName
    || change.schemaName.toLowerCase().endsWith(`.${shape.componentKind}.${shape.componentName}`.toLowerCase());

const awaitChange = async (
    read: () => Promise<ChangeRecord[]>,
    shape: ShapeDescriptor,
    description: string,
): Promise<{ matched: ChangeRecord; all: ChangeRecord[] }> => {
    let latest: ChangeRecord[] = [];

    return pollUntil(
        async () => {
            latest = await read();
            const matched = latest.find(change => mentions(change, shape));

            return matched ? { matched, all: latest } : undefined;
        },
        180_000,
        description,
        { pollMs: 3_000, describeFailure: () => `the list held: ${describeChanges(latest)}` });
};

describe('Group F — the agent changes list', () => {
    for (const shape of SHAPES) {
        if (runsScenario(shape, 'F-01')) {
            test(`F-01 [${shape.id}] an edit to the ${shape.componentKind} shows up as a local change`, { timeout: 1_800_000 }, async () => {
                await runScenario('F-01', `an edited ${shape.componentKind} appears in the local change list`, shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const api = await extensionApi();
                    const workspace = await trackedWorkspace(pair.v1);
                    const target = targetFileOf(pair.v1, shape);
                    const original = fs.readFileSync(target, 'utf8');

                    try {
                        const clean = await api.readLocalChanges(workspace);
                        detail('before the edit', describeChanges(clean));
                        assertThat('the change list is clean before the edit', 'no local change for the component',
                            describeChanges(clean.filter(change => mentions(change, shape))),
                            !clean.some(change => mentions(change, shape)));

                        const written = variantLine(shape, `Changes ${settings.suffix}`);
                        setTarget(pair.v1, shape, written);
                        detail('edit', `${shape.targetRelativePath} → ${written.trim()}`);

                        const { matched, all } = await awaitChange(
                            () => api.readLocalChanges(workspace),
                            shape,
                            `${shape.id}: the local change list naming ${shape.componentName}`);

                        detail('local changes', describeChanges(all));
                        assertThat('the edited component is listed as a local change', shape.componentName, matched.name, true);
                        assertThat('the local change is an update, not a create or delete', 'update',
                            CHANGE_TYPE[matched.changeType] ?? String(matched.changeType), matched.changeType === 1);
                        assertThat('the local change carries the component schema name', `*.${shape.componentKind}.${shape.componentName}`, matched.schemaName,
                            matched.schemaName.toLowerCase().endsWith(`.${shape.componentKind}.${shape.componentName}`.toLowerCase()));
                        assertThat('the local change points at the edited file', path.basename(target), matched.uri,
                            matched.uri.toLowerCase().includes(path.basename(path.dirname(target)).toLowerCase()));
                    } finally {
                        fs.writeFileSync(target, original, 'utf8');
                    }

                    await pollUntil(
                        async () => (await api.readLocalChanges(workspace)).some(change => mentions(change, shape)) ? undefined : true,
                        180_000,
                        `${shape.id}: the local change clearing once the file was restored`,
                        { pollMs: 3_000 });
                });
            });
        }

        if (runsScenario(shape, 'F-02')) {
            test(`F-02 [${shape.id}] a pushed edit shows up as a remote change in the other copy`, { timeout: 2_400_000 }, async () => {
                await runScenario('F-02', `a pushed ${shape.componentKind} appears in the remote change list`, shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const api = await extensionApi();
                    const v2Workspace = await trackedWorkspace(pair.v2);

                    const written = variantLine(shape, `Remote ${settings.suffix}`);
                    setTarget(pair.v1, shape, written);
                    detail('v1 edit', `${shape.targetRelativePath} → ${written.trim()}`);

                    await pushWorkspace(await trackedWorkspace(pair.v1), `remote change ${shape.id}`);

                    const { matched, all } = await awaitChange(
                        () => api.readRemoteChanges(v2Workspace),
                        shape,
                        `${shape.id}: v2's remote change list naming ${shape.componentName}`);

                    detail('remote changes', describeChanges(all));
                    assertThat('the pushed component is listed as a remote change', shape.componentName, matched.name, true);
                    assertThat('the remote change is an update, not a create or delete', 'update',
                        CHANGE_TYPE[matched.changeType] ?? String(matched.changeType), matched.changeType === 1);
                    assertThat('the remote change carries the component schema name', `*.${shape.componentKind}.${shape.componentName}`, matched.schemaName,
                        matched.schemaName.toLowerCase().endsWith(`.${shape.componentKind}.${shape.componentName}`.toLowerCase()));

                    const v1Remote = await api.readRemoteChanges(await trackedWorkspace(pair.v1));
                    detail('v1 remote changes', describeChanges(v1Remote));
                    assertThat('the copy that pushed has nothing of its own to get back', 'no remote change for the component',
                        describeChanges(v1Remote.filter(change => mentions(change, shape))),
                        !v1Remote.some(change => mentions(change, shape)));
                });
            });
        }
    }
});
