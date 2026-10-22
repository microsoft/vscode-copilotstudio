import { describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { SHAPES, runsScenario, type ShapeDescriptor } from '../lib/shapes';
import { runScenario, settings, normaliseId, workPathFor, readConnection, extensionApi, waitForWorkspace } from '../lib/run';
import { cloneInto, trackedWorkspace, type WorkingCopy } from '../lib/workingCopies';
import { attemptPush, runCommand, ERROR_GATE_TEXT, UNREADABLE_TEXT, GET_COMMAND, GET_DONE_TEXT, PREVIEW_COMMAND, PREVIEW_DONE_TEXT, REATTACH_COMMAND, REATTACH_ATTACHED_SIGNALS, REATTACH_EXPECTATIONS, reattachQuickPickRules } from '../lib/commands';
import { waitUntil } from '../lib/async';
import { assertThat, detail } from '../lib/log';
import { hashTree, diffHashes } from '../lib/tree';

const BROKEN_YAML = '\n\tbroken: [unclosed\n';
const REATTACH_TIMEOUT_MS = 300_000;
const DIAGNOSTIC_WAIT_MS = 60_000;

const scratchCopies = new Map<string, WorkingCopy>();

const scratchCopy = async (shape: ShapeDescriptor, agentId: string, slot = 'group-e'): Promise<WorkingCopy> => {
    const key = `${slot}:${shape.id}`;
    const existing = scratchCopies.get(key);

    if (existing) {
        return existing;
    }

    const copy = await cloneInto(agentId, path.join(workPathFor(slot), shape.id));
    scratchCopies.set(key, copy);

    return copy;
};

const diffFileOf = (copy: WorkingCopy, shape: ShapeDescriptor): string =>
    path.join(copy.agentRoot, ...shape.diffRelativePath.split('/'));

const errorsIn = (filePath: string): vscode.Diagnostic[] => {
    const wanted = filePath.toLowerCase();

    for (const [uri, diagnostics] of vscode.languages.getDiagnostics()) {
        if (uri.fsPath.toLowerCase() === wanted) {
            return diagnostics.filter(entry => entry.severity === vscode.DiagnosticSeverity.Error);
        }
    }

    return [];
};

const diagnosticsUnder = (root: string): string[] => {
    const prefix = root.toLowerCase();
    const found: string[] = [];

    for (const [uri, diagnostics] of vscode.languages.getDiagnostics()) {
        if (uri.fsPath.toLowerCase().startsWith(prefix) && diagnostics.length > 0) {
            found.push(`${path.basename(uri.fsPath)}: ${diagnostics.map(entry => `[${vscode.DiagnosticSeverity[entry.severity]}] ${entry.message}`).join('; ')}`);
        }
    }

    return found;
};

describe('Group E — the sync commands on their quieter paths', () => {
    for (const shape of SHAPES) {
        if (runsScenario(shape, 'E-01')) {
            test(`E-01 [${shape.id}] reattaching an attached workspace keeps it on the same agent`, { timeout: 1_800_000 }, async () => {
                await runScenario('E-01', 'reattach of an attached workspace keeps the agent', shape, async seed => {
                    const copy = await scratchCopy(shape, seed.agentId, 'e-01');
                    const before = readConnection(copy.agentRoot);

                    const seen = await runCommand(REATTACH_COMMAND, await trackedWorkspace(copy), {
                        timeoutMs: REATTACH_TIMEOUT_MS,
                        awaitTimeoutMs: REATTACH_TIMEOUT_MS,
                        label: `${REATTACH_COMMAND} (${shape.id} already attached)`,
                        expectations: REATTACH_EXPECTATIONS,
                        quickPickRules: reattachQuickPickRules(settings.environmentId),
                        awaitAnyMessage: REATTACH_ATTACHED_SIGNALS,
                    });

                    const reported = seen.find(record => REATTACH_ATTACHED_SIGNALS.some(signal => record.message.includes(signal)));
                    const after = readConnection(copy.agentRoot);
                    detail('reattach', reported?.message ?? 'no completion message');

                    assertThat('the reattach reported completion', REATTACH_ATTACHED_SIGNALS.join(' or '), reported?.message ?? '(nothing)', reported !== undefined);
                    assertThat('the workspace stayed on the same agent', before.AgentId, after.AgentId, normaliseId(after.AgentId) === normaliseId(before.AgentId));
                    assertThat('the workspace stayed in the run environment', settings.environmentId, after.EnvironmentId,
                        normaliseId(after.EnvironmentId) === normaliseId(settings.environmentId));

                    const api = await extensionApi();
                    const tracked = await waitForWorkspace(api, copy.agentRoot, 120_000);
                    assertThat('the workspace is still tracked after reattach', 'tracked', tracked ? 'tracked' : 'lost', Boolean(tracked));
                });
            });
        }

        if (runsScenario(shape, 'E-02')) {
            test(`E-02 [${shape.id}] apply is refused while a component file has invalid yaml`, { timeout: 1_800_000 }, async () => {
                await runScenario('E-02', 'invalid yaml blocks apply', shape, async seed => {
                    const copy = await scratchCopy(shape, seed.agentId);
                    const target = diffFileOf(copy, shape);
                    const original = fs.readFileSync(target, 'utf8');

                    try {
                        fs.writeFileSync(target, `${original}${BROKEN_YAML}`, 'utf8');
                        await vscode.workspace.openTextDocument(vscode.Uri.file(target));
                        await waitUntil(
                            async () => errorsIn(target).length > 0,
                            DIAGNOSTIC_WAIT_MS,
                            `${shape.id}: an error diagnostic for ${shape.diffRelativePath}`,
                            { describeFailure: () => `diagnostics under the workspace: [${diagnosticsUnder(copy.agentRoot).join(' | ') || 'none'}]` });

                        const diagnostics = errorsIn(target);
                        detail('diagnostics', `${diagnostics.length} error(s): ${diagnostics[0].message}`);

                        const attempt = await attemptPush(await trackedWorkspace(copy), `broken yaml ${shape.id}`, 900_000);
                        const gate = attempt.reason ?? '';
                        detail('apply', `${attempt.outcome} — ${gate || 'no reason reported'}`);

                        assertThat('apply refused the workspace while it had an error', 'refused', attempt.outcome, attempt.outcome === 'refused');
                        assertThat('the refusal named a validation gate', `${ERROR_GATE_TEXT} or ${UNREADABLE_TEXT}`, gate || '(none)',
                            gate.includes(ERROR_GATE_TEXT) || gate.includes(UNREADABLE_TEXT));
                    } finally {
                        fs.writeFileSync(target, original, 'utf8');
                    }

                    await waitUntil(async () => errorsIn(target).length === 0, DIAGNOSTIC_WAIT_MS, `${shape.id}: the error diagnostic clearing after the file was restored`);
                    assertThat('restoring the file cleared the error', 'no errors', String(errorsIn(target).length), errorsIn(target).length === 0);
                });
            });
        }

        if (runsScenario(shape, 'E-03')) {
            test(`E-03 [${shape.id}] a second get with nothing new writes nothing`, { timeout: 1_800_000 }, async () => {
                await runScenario('E-03', 'a second get writes nothing', shape, async seed => {
                    const copy = await scratchCopy(shape, seed.agentId);
                    const workspace = await trackedWorkspace(copy);

                    await runCommand(GET_COMMAND, workspace, {
                        timeoutMs: 900_000,
                        label: `${GET_COMMAND} (${shape.id} settle)`,
                        awaitMessage: GET_DONE_TEXT,
                    });

                    const before = hashTree(copy.agentRoot, ['.mcs']);

                    const seen = await runCommand(GET_COMMAND, workspace, {
                        timeoutMs: 900_000,
                        label: `${GET_COMMAND} (${shape.id} second)`,
                        awaitMessage: GET_DONE_TEXT,
                    });

                    const differences = diffHashes(before, hashTree(copy.agentRoot, ['.mcs']));
                    detail('second get', differences.length === 0 ? 'no file changes' : differences.join(', '));

                    assertThat('the second get reported completion', GET_DONE_TEXT,
                        seen.some(record => record.message.includes(GET_DONE_TEXT)) ? 'reported' : 'never reported',
                        seen.some(record => record.message.includes(GET_DONE_TEXT)));
                    assertThat('the second get changed nothing on disk', 'no file changes',
                        differences.length === 0 ? 'no file changes' : differences.join(', '), differences.length === 0);
                });
            });
        }

        if (runsScenario(shape, 'E-04')) {
            test(`E-04 [${shape.id}] preview with nothing pending still reports completion`, { timeout: 1_800_000 }, async () => {
                await runScenario('E-04', 'preview with nothing pending reports completion', shape, async seed => {
                    const copy = await scratchCopy(shape, seed.agentId);
                    const workspace = await trackedWorkspace(copy);
                    const before = hashTree(copy.agentRoot, ['.mcs']);

                    const seen = await runCommand(PREVIEW_COMMAND, workspace, {
                        timeoutMs: 900_000,
                        label: `${PREVIEW_COMMAND} (${shape.id} quiet)`,
                        awaitMessage: PREVIEW_DONE_TEXT,
                    });

                    const differences = diffHashes(before, hashTree(copy.agentRoot, ['.mcs']));
                    detail('quiet preview', differences.length === 0 ? 'no file changes' : differences.join(', '));

                    assertThat('preview reported completion with nothing pending', PREVIEW_DONE_TEXT,
                        seen.some(record => record.message.includes(PREVIEW_DONE_TEXT)) ? 'reported' : 'never reported',
                        seen.some(record => record.message.includes(PREVIEW_DONE_TEXT)));
                    assertThat('preview wrote nothing', 'no file changes',
                        differences.length === 0 ? 'no file changes' : differences.join(', '), differences.length === 0);
                });
            });
        }
    }
});
