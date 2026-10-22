import * as fs from 'node:fs';
import * as path from 'node:path';
import { DataverseError, deleteRow, environmentDataverseUrl, listRows } from './dataverse';
import { runExternal } from './external';
import { detail } from './log';

export interface CleanupTarget {
    id: string;
    name: string;
}

export type DependencyKind = 'connectionreference' | 'workflow';

export interface DependencyTarget {
    kind: DependencyKind;
    id: string;
    name: string;
}

export interface CleanupOutcome {
    deleted: string[];
    retained: { name: string; reason: string }[];
    failed: { name: string; error: string }[];
}

export const MINIMUM_PREFIX_LENGTH = 3;

const DEPENDENCY_ENTITY_SETS: Record<DependencyKind, string> = {
    connectionreference: 'connectionreferences',
    workflow: 'workflows',
};

export const odataLiteral = (value: string): string => value.replace(/'/g, "''");

export const assertDeletableEnvironment = (environmentId: string, configuredEnvironmentId: string): void => {
    if (!environmentId) {
        throw new Error('refusing to delete: no environment was given');
    }

    if (environmentId.toLowerCase() !== configuredEnvironmentId.toLowerCase()) {
        throw new Error(`refusing to delete in '${environmentId}': the suite is configured for '${configuredEnvironmentId}'`);
    }
};

export const assertSafePrefix = (prefix: string | undefined): string => {
    const trimmed = (prefix ?? '').trim();

    if (trimmed.length < MINIMUM_PREFIX_LENGTH) {
        throw new Error(`refusing to delete with prefix '${prefix ?? ''}': cleanup deletes by prefix, so at least ${MINIMUM_PREFIX_LENGTH} characters are required`);
    }

    return trimmed;
};

export const requireEnvironmentDataverseUrl = (environmentId: string, resolvedDataverseUrl: string | undefined): string => {
    if (!resolvedDataverseUrl) {
        throw new Error(`refusing to delete: environment '${environmentId}' has no Dataverse instance`);
    }

    return resolvedDataverseUrl;
};

export const verifiedDeletionTarget = async (environmentId: string, bapToken: string): Promise<string> => {
    assertDeletableEnvironment(environmentId, environmentId);
    const resolved = await environmentDataverseUrl(environmentId, bapToken);

    return requireEnvironmentDataverseUrl(environmentId, resolved);
};

export const shouldSkipCleanup = (value: string | undefined): boolean => value === '1';

export const matchesPrefix = (name: string | undefined, prefix: string): boolean =>
    (name ?? '').toLowerCase().startsWith(prefix.toLowerCase());

export const findArtifacts = async (dataverseUrl: string, accessToken: string, prefix: string): Promise<CleanupTarget[]> => {
    const safePrefix = assertSafePrefix(prefix);
    const filter = encodeURIComponent(`startswith(name,'${odataLiteral(safePrefix)}')`);

    return (await listRows<{ botid: string; name: string }>(dataverseUrl, accessToken, `bots?$select=botid,name&$filter=${filter}`))
        .filter(row => matchesPrefix(row.name, safePrefix))
        .map(row => ({ id: row.botid, name: row.name }));
};

/**
 * The standalone cloud flows a run created. They carry no agent, so deleting agents leaves them
 * behind; they are deleted as Dataverse rows of their own.
 */
export const findWorkflowArtifacts = async (dataverseUrl: string, accessToken: string, prefix: string): Promise<DependencyTarget[]> => {
    const safePrefix = assertSafePrefix(prefix);
    const filter = encodeURIComponent(`startswith(name,'${odataLiteral(safePrefix)}')`);

    return (await listRows<{ workflowid: string; name: string }>(dataverseUrl, accessToken, `workflows?$select=workflowid,name&$filter=${filter}`))
        .filter(row => matchesPrefix(row.name, safePrefix))
        .map(row => ({ kind: 'workflow' as const, id: row.workflowid, name: row.name }));
};

interface DeletionRequest {
    label: string;
    entitySet: string;
    resolveId: () => Promise<string | undefined>;
}

const recordFailure = (outcome: CleanupOutcome, label: string, error: unknown): void => {
    const message = (error as Error).message ?? String(error);

    if (alreadyGone(error)) {
        return;
    }

    const referencedBy = stillReferencedBy(message);

    if (referencedBy) {
        outcome.retained.push({ name: label, reason: `still referenced by ${referencedBy} component(s) the run did not create` });
        return;
    }

    outcome.failed.push({ name: label, error: message });
};

const deleteRows = async (dataverseUrl: string, accessToken: string, requests: readonly DeletionRequest[]): Promise<CleanupOutcome> => {
    const outcome: CleanupOutcome = { deleted: [], retained: [], failed: [] };

    for (const request of requests) {
        try {
            const id = await request.resolveId();

            if (!id) {
                continue;
            }

            await deleteRow(dataverseUrl, accessToken, request.entitySet, id);
            outcome.deleted.push(request.label);
        } catch (error) {
            recordFailure(outcome, request.label, error);
        }
    }

    return outcome;
};

export const buildAgentCloneArgs = (agentId: string, environmentId: string, projectDirectory: string): string[] => [
    'agent', 'create',
    '--agent-type', 'MCSAgent',
    '--environment-id', environmentId,
    '--mcs-clone-agent-id', agentId,
    '--mcs-clone-environment-id', environmentId,
    '--project', projectDirectory,
    '--json', '--non-interactive',
];

export const buildAgentDeleteArgs = (projectDirectory: string): string[] =>
    ['agent', 'delete', '--project', projectDirectory, '--json', '--non-interactive'];

const runMsagent = async (args: readonly string[], timeoutMs: number): Promise<string> => {
    const result = await runExternal('msagent', args, timeoutMs);

    if (result.code !== 0) {
        throw new Error(msagentFailure(result.output) || `msagent ${args[1]} exited ${result.code}`);
    }

    return result.output;
};

const jsonErrorOf = (output: string): string | undefined => {
    const start = output.indexOf('{');
    const end = output.lastIndexOf('}');

    if (start < 0 || end <= start) {
        return undefined;
    }

    try {
        const parsed = JSON.parse(output.slice(start, end + 1)) as { errorMessage?: string; errorKind?: string };

        if (!parsed.errorMessage) {
            return undefined;
        }

        return parsed.errorKind ? `${parsed.errorMessage} (${parsed.errorKind})` : parsed.errorMessage;
    } catch {
        return undefined;
    }
};

export const msagentFailure = (output: string): string =>
    jsonErrorOf(output) ?? output.trim().split('\n').slice(-6).join(' | ');

const removeScratch = (directory: string): void => {
    try {
        fs.rmSync(directory, { recursive: true, force: true });
    } catch (error) {
        detail('scratch', `${directory} could not be removed: ${(error as Error).message ?? String(error)}`);
    }
};

export const deleteArtifacts = async (environmentId: string, targets: readonly CleanupTarget[], scratchRoot: string, timeoutMs = 600_000): Promise<CleanupOutcome> => {
    const outcome: CleanupOutcome = { deleted: [], retained: [], failed: [] };

    for (const target of targets) {
        const projectDirectory = path.join(scratchRoot, `delete-${target.id}`);

        try {
            await runMsagent(buildAgentCloneArgs(target.id, environmentId, projectDirectory), timeoutMs);
            await runMsagent(buildAgentDeleteArgs(projectDirectory), timeoutMs);
            outcome.deleted.push(target.name);
        } catch (error) {
            recordFailure(outcome, target.name, error);
        } finally {
            removeScratch(projectDirectory);
        }
    }

    return outcome;
};

const alreadyGone = (error: unknown): boolean =>
    (error instanceof DataverseError && error.status === 404)
    || /Does Not Exist|was not found|agent-not-found|could not be found/i.test((error as Error)?.message ?? '');

export const stillReferencedBy = (message: string): string | undefined =>
    (/0x8004f01f|cannot be deleted because it is referenced by/i.test(message)
        ? (/referenced by (\d+) other component/i.exec(message) ?? [])[1] ?? 'another component'
        : undefined);

const resolveConnectionReferenceId = async (dataverseUrl: string, accessToken: string, target: DependencyTarget): Promise<string | undefined> => {
    const filter = encodeURIComponent(`connectionreferencelogicalname eq '${odataLiteral(target.id)}'`);
    const rows = await listRows<{ connectionreferenceid: string }>(
        dataverseUrl,
        accessToken,
        `connectionreferences?$select=connectionreferenceid&$filter=${filter}`);

    return rows[0]?.connectionreferenceid;
};

const DEPENDENCY_ID_RESOLVERS: Record<DependencyKind, (dataverseUrl: string, accessToken: string, target: DependencyTarget) => Promise<string | undefined>> = {
    connectionreference: resolveConnectionReferenceId,
    workflow: async (_dataverseUrl, _accessToken, target) => target.id,
};

export const deleteDependencies = async (dataverseUrl: string, accessToken: string, targets: readonly DependencyTarget[]): Promise<CleanupOutcome> =>
    deleteRows(dataverseUrl, accessToken, targets.map(target => ({
        label: `${target.kind} ${target.name}`,
        entitySet: DEPENDENCY_ENTITY_SETS[target.kind],
        resolveId: () => DEPENDENCY_ID_RESOLVERS[target.kind](dataverseUrl, accessToken, target),
    })));
