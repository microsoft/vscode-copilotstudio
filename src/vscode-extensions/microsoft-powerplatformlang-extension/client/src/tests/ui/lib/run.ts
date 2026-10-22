import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { randomUUID } from 'node:crypto';
import { RunManifest, formatSeconds, scenarioLine, type Outcome } from './manifest';
import { CATALOGUE, catalogueEntry, isSelected, parseGroups } from './groups';
import { ALL_SHAPES, type SeedShape, type ShapeId } from './shapes';
import { copyDirectory, renameWorkspace } from './renamer';
import { renameWorkflowWorkspace, workflowIdentity } from './workflows';
import { runMintedConnectionReferences } from './components';
import { heading, detail, line, show, beginCapture, takeCapture, assertThat, takeAssertions, takeStubs, type LoggedAssertion, type LoggedStub } from './log';
import { installModalStub, installOpenFolderSuppression, installQuickPickAutoSelect, agentItemRule, assetItemRule } from './stubs';
import { proveRunIsReal, proveLspResponds, type ExtensionApi } from './liveness';
import { pushWorkspace, REATTACH_DONE_SIGNALS, REATTACH_COMMAND, REATTACH_EXPECTATIONS, reattachQuickPickRules } from './commands';
import { pollUntil, waitUntil, withTimeout } from './async';
import { cancellationSignal } from '../../runnerCore';
import { signedInAccount, signIn, resourceScope, type AgentConnection } from './dataverse';
import type { DependencyTarget } from './cleanup';

export interface RunSettings {
    suffix: string;
    runRoot: string;
    fixturesRoot: string;
    environmentId: string;
    publisherPrefix: string;
    namePrefix: string;
}

export interface SeededAgent {
    status: 'seeded';
    shape: ShapeId;
    agentId: string;
    schemaName: string;
    displayName: string;
    seedPath: string;
    connection: AgentConnection;
}

export interface FailedSeed {
    status: 'failed';
    shape: ShapeId;
    error: string;
}

export type SeedResult = SeededAgent | FailedSeed;

export interface ScenarioRecord {
    id: string;
    shape: ShapeId;
    outcome: Outcome;
    milliseconds: number;
    detail?: string;
    assertions: LoggedAssertion[];
    stubs: LoggedStub[];
}

const required = (name: string): string => {
    const value = process.env[name];

    if (!value) {
        throw new Error(`${name} must be supplied by scripts/runUiE2E.js`);
    }

    return value;
};

export const settings: RunSettings = {
    suffix: required('MCSUI_RUN_SUFFIX'),
    runRoot: required('MCSUI_RUN_ROOT'),
    fixturesRoot: required('MCSUI_FIXTURES'),
    environmentId: required('MCSUI_ENVIRONMENT_ID'),
    publisherPrefix: required('MCSUI_PUBLISHER_PREFIX'),
    namePrefix: required('MCSUI_NAME_PREFIX'),
};

export const selectedGroups = parseGroups(process.env.MCSUI_GROUPS);

export const manifest = new RunManifest();
export const records: ScenarioRecord[] = [];

export const schemaNameFor = (shape: SeedShape): string => `${settings.publisherPrefix}_lspe2e${settings.suffix}${shape.id}`;
export const displayNameFor = (shape: SeedShape): string => `${settings.namePrefix} ${settings.suffix} ${shape.id}`;
export const runOwnedPrefix = (): string => `${settings.namePrefix} ${settings.suffix}`;

export const seedPathFor = (shape: SeedShape): string => path.join(settings.runRoot, 'seed', shape.id);
export const workPathFor = (copy: string): string => path.join(settings.runRoot, 'work', copy);

let liveness: { api: ExtensionApi } | undefined;

export const extensionApi = async (): Promise<ExtensionApi> => {
    if (!liveness) {
        heading('run preconditions');
        const proven = await proveRunIsReal();
        await proveLspResponds(proven, 60_000);
        const account = await signedInAccount();
        detail('account', account.label);
        detail('environment', settings.environmentId);
        detail('run root', settings.runRoot);
    detail('open folders', (vscode.workspace.workspaceFolders ?? []).map(folder => folder.uri.fsPath).join(', ') || '(none — the extension can only discover agents under an open folder)');
        liveness = proven;
    }

    return liveness.api;
};

export const dataverseToken = async (dataverseUrl: string): Promise<string> =>
    (await signIn(resourceScope(dataverseUrl))).accessToken;

export const readConnection = (agentRoot: string): AgentConnection =>
    JSON.parse(fs.readFileSync(path.join(agentRoot, '.mcs', 'conn.json'), 'utf8'));

const samePath = (left: string, right: string): boolean =>
    path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase();

export const normaliseId = (value: string | undefined): string => (value ?? '').toLowerCase().replace(/[^a-z0-9]/g, '');

export const waitForWorkspace = async (api: ExtensionApi, folderPath: string, timeoutMs: number) =>
    pollUntil(
        async () => api.getAllWorkspaces().find(workspace => samePath(vscode.Uri.parse(workspace.workspaceUri).fsPath, folderPath)),
        timeoutMs,
        `workspace ${folderPath} was never tracked`,
        {
            pollMs: 1_000,
            describeFailure: () => `tracked: [${api.getAllWorkspaces().map(workspace => vscode.Uri.parse(workspace.workspaceUri).fsPath).join(', ')}]`,
        });

export { withTimeout };

const ledgerPath = (root: string = settings.runRoot): string => path.join(root, 'ledger.json');

export const failureLogPath = (id: string, shape: string): string =>
    path.join(settings.runRoot, 'failures', `${id}-${shape}.log`);

const writeFailureLog = (id: string, shape: string, lines: string[]): void => {
    const target = failureLogPath(id, shape);

    try {
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(target, `${lines.join('\n')}\n`, 'utf8');
    } catch {
    }
};

let announcedGroup: string | undefined;

const announceGroup = (id: string): void => {
    const group = catalogueEntry(id)?.group ?? 'ungrouped';

    if (group !== announcedGroup) {
        announcedGroup = group;
        show(`\n${group}`);
    }
};

export const readLedger = (root?: string): Record<string, unknown>[] =>
    fs.existsSync(ledgerPath(root)) ? JSON.parse(fs.readFileSync(ledgerPath(root), 'utf8')) : [];

export const recordLedger = (entry: Record<string, unknown>): void => {
    const existing = readLedger();
    existing.push({ ...entry, at: new Date().toISOString() });
    fs.writeFileSync(ledgerPath(), JSON.stringify(existing, null, 2), 'utf8');
};

const dependenciesIn = (entries: readonly Record<string, unknown>[]): DependencyTarget[] =>
    entries
        .filter(entry => entry.kind === 'dependency' && typeof entry.id === 'string' && typeof entry.name === 'string')
        .map(entry => ({ kind: entry.dependencyKind as DependencyTarget['kind'], id: entry.id as string, name: entry.name as string }));

export const ledgerDependencies = (): DependencyTarget[] => dependenciesIn(readLedger());

export const everyRunsDependencies = (): DependencyTarget[] => {
    const runsRoot = path.dirname(settings.runRoot);
    const ownsEnvironment = (entries: readonly Record<string, unknown>[]): boolean =>
        entries.some(entry => String(entry.environmentId ?? '').toLowerCase() === settings.environmentId.toLowerCase());

    const collected = new Map<string, DependencyTarget>();

    for (const entry of fs.existsSync(runsRoot) ? fs.readdirSync(runsRoot, { withFileTypes: true }) : []) {
        if (!entry.isDirectory()) {
            continue;
        }

        const entries = readLedger(path.join(runsRoot, entry.name));

        if (!ownsEnvironment(entries)) {
            continue;
        }

        for (const dependency of dependenciesIn(entries)) {
            collected.set(`${dependency.kind}:${dependency.id}`, dependency);
        }
    }

    return [...collected.values()];
};

const seeds = new Map<ShapeId, Promise<SeedResult>>();

export interface Placement {
    identityRoot: string;
    schemaName: string;
    displayName: string;
}

let placements: Promise<Map<ShapeId, Placement>> | undefined;

/** A workflow is identified by the flow's own identifier, an agent by its schema name. */
const identityFor = (shape: SeedShape): string => shape.kind === 'workflow' ? randomUUID() : schemaNameFor(shape);

/** What the cloud record the attach creates is called in the ledger. */
const ledgerKindOf = (shape: SeedShape): string => shape.kind === 'workflow' ? 'workflow' : 'agent';

/** The identifier of the cloud record a seeded workspace is bound to. */
const boundRecordOf = (shape: SeedShape, identityRoot: string, connection: AgentConnection): string =>
    shape.kind === 'workflow' ? workflowIdentity(identityRoot).workflowId : connection.AgentId;

const placeAllFixtures = async (): Promise<Map<ShapeId, Placement>> => {
    const placed = new Map<ShapeId, Placement>();

    heading('placing fixtures');

    for (const shape of ALL_SHAPES) {
        const schemaName = identityFor(shape);
        const displayName = displayNameFor(shape);
        const seedPath = seedPathFor(shape);

        copyDirectory(path.join(settings.fixturesRoot, shape.fixtureDirectory), seedPath);

        const identityRoot = shape.identitySubdirectory ? path.join(seedPath, shape.identitySubdirectory) : seedPath;

        const rename = shape.kind === 'workflow'
            ? renameWorkflowWorkspace(seedPath, displayName, schemaName)
            : renameWorkspace(identityRoot, seedPath, schemaName, displayName);
        detail(shape.id, `${rename.originalIdentity} → ${schemaName}, ${rename.filesRewritten} files rewritten`);

        for (const logicalName of runMintedConnectionReferences(seedPath, schemaName)) {
            recordLedger({ kind: 'dependency', dependencyKind: 'connectionreference', id: logicalName, name: logicalName, shape: shape.id });
        }

        placed.set(shape.id, { identityRoot, schemaName, displayName });
    }

    const api = await extensionApi();

    for (const shape of ALL_SHAPES) {
        const opener = path.join(placed.get(shape.id)!.identityRoot, shape.openerFile);

        if (fs.existsSync(opener)) {
            await vscode.workspace.openTextDocument(vscode.Uri.file(opener));
        }
    }

    const tracked = (): string[] => api.getAllWorkspaces().map(workspace => vscode.Uri.parse(workspace.workspaceUri).fsPath);

    await pollUntil(
        async () => ALL_SHAPES.every(shape => tracked().some(candidate => samePath(candidate, placed.get(shape.id)!.identityRoot))) || undefined,
        180_000,
        'the extension discovering every placed fixture',
        { describeFailure: () => `tracked: [${tracked().join(', ')}]` }).catch(() => undefined);

    detail('tracked', api.getAllWorkspaces().map(workspace => `${workspace.displayName} (${workspace.type})`).join(', ') || '(none)');
    return placed;
};

export const ensurePlaced = (): Promise<Map<ShapeId, Placement>> => {
    if (!placements) {
        placements = placeAllFixtures();
    }

    return placements;
};

const attach = async (shape: SeedShape): Promise<SeedResult> => {
    const api = await extensionApi();

    try {
        const placement = (await ensurePlaced()).get(shape.id)!;
        const { identityRoot, schemaName, displayName } = placement;

        assertThat('fixture has no .mcs binding', 'absent', fs.existsSync(path.join(identityRoot, '.mcs')) ? 'present' : 'absent', !fs.existsSync(path.join(identityRoot, '.mcs')));

        const workspace = await waitForWorkspace(api, identityRoot, 120_000);
        detail('tracked', `${workspace.displayName} — ${identityRoot}`);

        recordLedger({ kind: ledgerKindOf(shape), shape: shape.id, schemaName, displayName, environmentId: settings.environmentId, state: 'pending' });

        const restoreQuickPick = installQuickPickAutoSelect(reattachQuickPickRules(settings.environmentId));
        const modals = installModalStub(REATTACH_EXPECTATIONS);
        const gate = installOpenFolderSuppression();
        const connectionPath = path.join(identityRoot, '.mcs', 'conn.json');

        try {
            await withTimeout(gate.execute(REATTACH_COMMAND, { workspace }), 600_000, REATTACH_COMMAND);

            await waitUntil(async () => fs.existsSync(connectionPath), 900_000, `${shape.id}: reattach writing .mcs/conn.json`);
            await waitUntil(
                async () => modals.seen.some(record => REATTACH_DONE_SIGNALS.some(signal => record.message.includes(signal))),
                900_000,
                `${shape.id}: reattach finishing — saw [${modals.seen.map(record => record.message.slice(0, 40)).join(' | ') || 'nothing'}]`);
        } finally {
            gate.restore();
            modals.restore();
            restoreQuickPick();
        }

        assertThat('reattach wrote .mcs/conn.json', 'present', fs.existsSync(connectionPath) ? 'present' : 'absent', fs.existsSync(connectionPath));

        const connection = readConnection(identityRoot);
        const sameEnvironment = normaliseId(connection.EnvironmentId) === normaliseId(settings.environmentId);
        assertThat('reattach bound the configured environment', settings.environmentId, connection.EnvironmentId, sameEnvironment);

        const bound = await waitForWorkspace(api, identityRoot, 120_000);
        detail('pushing', 'reattach creates the agent but does not upload content');
        await pushWorkspace(bound, `seed ${shape.id}`);

        const agentId = boundRecordOf(shape, identityRoot, connection);

        recordLedger({ kind: ledgerKindOf(shape), shape: shape.id, schemaName, displayName, agentId, environmentId: settings.environmentId, state: 'created' });
        detail('agent', `${agentId}  ${displayName}`);

        return {
            status: 'seeded',
            shape: shape.id,
            agentId,
            schemaName,
            displayName,
            seedPath: identityRoot,
            connection,
        };
    } catch (error) {
        return { status: 'failed', shape: shape.id, error: (error as Error).message ?? String(error) };
    }
};

export const ensureSeeded = (shape: SeedShape): Promise<SeedResult> => {
    if (!seeds.has(shape.id)) {
        seeds.set(shape.id, attach(shape));
    }

    return seeds.get(shape.id)!;
};

export const scenarioSelected = (id: string, shape: SeedShape): boolean =>
    isSelected(id, shape.scenarios, selectedGroups);

export const declareAll = (): void => {
    for (const shape of ALL_SHAPES) {
        for (const entry of CATALOGUE) {
            if (scenarioSelected(entry.id, shape)) {
                manifest.declare({ id: entry.id, name: entry.name, shape: shape.id });
            }
        }
    }
};

export const runScenario = async (id: string, name: string, shape: SeedShape, body: (seed: SeededAgent) => Promise<void>, ownsSeed = false): Promise<void> => {
    if (!scenarioSelected(id, shape)) {
        return;
    }

    announceGroup(id);
    beginCapture();
    heading(`${id} [${shape.id}] ${name}`);
    takeAssertions();
    takeStubs();
    const started = Date.now();
    const cancellation = cancellationSignal();
    let recorded = false;

    const finish = (outcome: Outcome, why?: string): void => {
        if (recorded) {
            return;
        }

        recorded = true;
        const abandoned = cancellation?.aborted === true && outcome === 'PASS';
        const resolved: Outcome = abandoned ? 'FAIL' : outcome;
        const reason = abandoned ? 'the scenario timed out and its work only finished after the test was abandoned' : why;
        const milliseconds = Date.now() - started;
        manifest.record({ id, name, shape: shape.id, outcome: resolved, milliseconds, detail: reason });
        records.push({ id, shape: shape.id, outcome: resolved, milliseconds, detail: reason, assertions: takeAssertions(), stubs: takeStubs() });
        line(`${resolved}  ${formatSeconds(milliseconds)}${reason ? `  ${reason}` : ''}`);

        const transcript = takeCapture();

        if (resolved !== 'PASS') {
            writeFailureLog(id, shape.id, transcript);
        }

        show(scenarioLine(id, shape.id, name, resolved, milliseconds));
    };

    const seed = await ensureSeeded(shape);

    if (seed.status === 'failed') {
        finish(ownsSeed ? 'FAIL' : 'BLOCKED', `the attach for '${shape.id}' failed: ${seed.error}`);

        if (ownsSeed) {
            throw new Error(`attach for '${shape.id}' failed: ${seed.error}`);
        }

        return;
    }

    try {
        await body(seed);
        finish('PASS');
    } catch (error) {
        const message = (error as Error).message ?? String(error);
        finish('FAIL', message);
        throw error;
    }
};
