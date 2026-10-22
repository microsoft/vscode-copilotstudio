import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { runTestsInDirectory, runWasAbandoned } from '../runnerCore';
import { manifest, records, settings, dataverseToken, selectedGroups, everyRunsDependencies, ledgerDependencies, readLedger, runOwnedPrefix, failureLogPath } from './lib/run';
import { activationDecision } from './lib/reentry';
import { orderScenarioFiles } from './lib/groups';
import { ellipsise, formatSeconds, scenarioLabel, scenarioLine, splitExpectation } from './lib/manifest';
import { deleteArtifacts, deleteDependencies, findArtifacts, findWorkflowArtifacts, shouldSkipCleanup, verifiedDeletionTarget, type DependencyTarget } from './lib/cleanup';
import { BAP_SCOPE, signIn } from './lib/dataverse';
import { installModalStub } from './lib/stubs';
import { raw, show } from './lib/log';

const scenariosDirectory = (): string => path.join(__dirname, 'scenarios');

const runStarted = Date.now();

const createdAgents = (): { agentId?: string; displayName?: string }[] =>
    (readLedger() as { state?: string; agentId?: string; displayName?: string }[]).filter(entry => entry.state === 'created');

const runFailed = (): boolean => {
    try {
        return manifest.failed();
    } catch {
        return true;
    }
};

const writeReportFile = (): void => {
    fs.writeFileSync(
        path.join(settings.runRoot, 'run-report.json'),
        JSON.stringify({ suffix: settings.suffix, environmentId: settings.environmentId, groups: selectedGroups, wallClockMs: Date.now() - runStarted, counts: manifest.counts(), results: manifest.reconcile(), records, ledger: readLedger() }, null, 2),
        'utf8');
};

const printStatus = (cleanup: CleanupResult, infrastructureFailed: boolean): void => {
    const results = manifest.reconcile();
    const counts = manifest.counts();

    for (const missing of results.filter(result => result.outcome === 'NOT-RUN')) {
        show(scenarioLine(missing.id, missing.shape, missing.name, missing.outcome, missing.milliseconds));
    }

    show(`\n  ${counts.PASS} passed · ${counts.FAIL} failed · ${counts.BLOCKED} blocked · ${counts['NOT-RUN']} not-run${formatSeconds(Date.now() - runStarted).padStart(16)}`);

    for (const result of results.filter(candidate => candidate.outcome === 'FAIL' || candidate.outcome === 'BLOCKED')) {
        const expectation = splitExpectation(result.detail ?? 'no detail was recorded');

        show(`\n${result.outcome}  ${scenarioLabel(result.id, result.shape)}  ${result.name}\n`);
        show(`  ${expectation.what}\n`);

        if (expectation.expected !== undefined) {
            show(`  expected  ${expectation.expected}`);
            show(`  actual    ${expectation.actual}\n`);
        }

        show(`  log  ${failureLogPath(result.id, result.shape)}`);
    }

    show(cleanup.rows.length > 0 ? '\n  Created and removed' : '');

    for (const row of cleanup.rows) {
        show(`    ${ellipsise(row.name, 52).padEnd(53)} ${row.disposition}`);
    }

    show(`  ${cleanup.note}`);

    if (infrastructureFailed) {
        show('\n  the run did not complete cleanly — see the full log');
    }
};

const reportSurvivingAgents = (reason: string): void => {
    raw(`${reason}; these agents are still in the cloud:\n`);

    for (const entry of createdAgents()) {
        raw(`  ${entry.agentId}  ${entry.displayName}`);
    }

    raw(`\ndelete them with:  npm run test:ui:clean ${settings.namePrefix}`);
};

interface CleanupRow {
    name: string;
    disposition: string;
}

interface CleanupResult {
    ok: boolean;
    rows: CleanupRow[];
    note: string;
}

const runCleanup = async (prefix: string, dependencies: DependencyTarget[]): Promise<CleanupResult> => {
    raw('\n== cleanup ==\n');

    if (shouldSkipCleanup(process.env.MCSUI_SKIP_CLEANUP)) {
        reportSurvivingAgents('skipped by --no-clean');

        return {
            ok: true,
            rows: createdAgents().map(entry => ({ name: entry.displayName ?? 'agent', disposition: 'kept' })),
            note: 'Nothing was deleted — this run used --no-clean.',
        };
    }

    try {
        const bapToken = (await signIn(BAP_SCOPE)).accessToken;
        const dataverseUrl = await verifiedDeletionTarget(settings.environmentId, bapToken);
        const token = await dataverseToken(dataverseUrl);
        const targets = await findArtifacts(dataverseUrl, token, prefix);
        const assets = [...dependencies, ...await findWorkflowArtifacts(dataverseUrl, token, prefix)];

        if (targets.length === 0 && assets.length === 0) {
            raw(`nothing to delete matching '${prefix}'`);
            return { ok: true, rows: [], note: 'Nothing left behind.' };
        }

        const scratchOutsideWorkspace = path.dirname(settings.runRoot);
        const agents = await deleteArtifacts(settings.environmentId, targets, scratchOutsideWorkspace);
        const removed = await deleteDependencies(dataverseUrl, token, assets);
        const rows: CleanupRow[] = [];

        for (const name of [...agents.deleted, ...removed.deleted]) {
            raw(`deleted  ${name}`);
            rows.push({ name, disposition: 'deleted' });
        }

        for (const retained of [...agents.retained, ...removed.retained]) {
            raw(`kept     ${retained.name} — ${retained.reason}`);
            rows.push({ name: retained.name, disposition: 'kept' });
        }

        for (const failure of [...agents.failed, ...removed.failed]) {
            raw(`FAILED   ${failure.name} — ${failure.error}`);
            rows.push({ name: failure.name, disposition: 'NOT DELETED' });
        }

        const kept = agents.retained.length + removed.retained.length;
        const failed = agents.failed.length + removed.failed.length;

        if (failed > 0) {
            return { ok: false, rows, note: `${failed} could not be deleted — see the full log.` };
        }

        return { ok: true, rows, note: kept > 0 ? `Nothing left behind except ${kept} still bound to components this run did not create.` : 'Nothing left behind.' };
    } catch (error) {
        const message = (error as Error).message ?? String(error);
        raw(`cleanup did not run: ${message}`);

        return { ok: false, rows: [], note: `Cleanup did not run — ${message}` };
    }
};

export async function activate(): Promise<void> {
    const runRoot = process.env.MCSUI_RUN_ROOT;

    if (!runRoot) {
        console.error('MCSUI_RUN_ROOT is not set — the driver was loaded outside scripts/runUiE2E.js');
        return;
    }

    const transcriptPath = path.join(runRoot, 'console.log');

    if (process.env.MCSUI_CLEANUP_ONLY === '1') {
        const cleaned = await runCleanup(settings.namePrefix, everyRunsDependencies());
        fs.writeFileSync(path.join(runRoot, 'done'), cleaned.ok ? '0' : '1', 'utf8');
        await vscode.commands.executeCommand('workbench.action.quit');
        return;
    }

    const baseline = installModalStub([]);
    const startedMarker = path.join(runRoot, 'started');
    const decision = activationDecision({
        exists: fs.existsSync(startedMarker),
        firstActivation: fs.existsSync(startedMarker) ? fs.readFileSync(startedMarker, 'utf8') : undefined,
    });

    if (decision.standDown) {
        console.error(decision.message);
        fs.appendFileSync(transcriptPath, `\n${decision.message}\n`, 'utf8');
        baseline.restore();
        return;
    }

    fs.writeFileSync(startedMarker, new Date().toISOString(), 'utf8');

    let infrastructureFailed = false;

    try {
        infrastructureFailed = await runTestsInDirectory(scenariosDirectory(), writeReportFile, false, { log: raw, error: raw }, orderScenarioFiles) !== 0;
    } catch (error) {
        infrastructureFailed = true;
        const message = `the run failed before it could report: ${(error as Error)?.stack ?? String(error)}`;
        console.error(message);

        try {
            fs.appendFileSync(transcriptPath, `${message}\n`, 'utf8');
            fs.writeFileSync(path.join(runRoot, 'run-report.json'), JSON.stringify({ fatal: String(error) }, null, 2), 'utf8');
        } catch {
        }
    }

    const abandonedBy = runWasAbandoned();

    if (abandonedBy) {
        raw('\n== cleanup ==\n');
        reportSurvivingAgents(`skipped: '${abandonedBy}' timed out and the work it started cannot be cancelled, so deleting now could race it`);
    }

    const cleaned: CleanupResult = abandonedBy
        ? {
            ok: false,
            rows: createdAgents().map(entry => ({ name: entry.displayName ?? 'agent', disposition: 'kept' })),
            note: `Nothing was deleted — '${abandonedBy}' timed out, so deleting could race work still in flight.`,
        }
        : await runCleanup(runOwnedPrefix(), ledgerDependencies());

    printStatus(cleaned, infrastructureFailed);
    baseline.restore();
    fs.writeFileSync(path.join(runRoot, 'done'), infrastructureFailed || runFailed() || !cleaned.ok ? '1' : '0', 'utf8');
    await vscode.commands.executeCommand('workbench.action.quit');
}

export function deactivate(): void {
}
