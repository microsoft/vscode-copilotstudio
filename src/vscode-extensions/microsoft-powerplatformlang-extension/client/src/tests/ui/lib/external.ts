import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { detail } from './log';

const scriptsModule = (name: string): string => {
    let directory = __dirname;

    for (let depth = 0; depth < 10; depth++) {
        const candidate = path.join(directory, 'scripts', name);

        if (fs.existsSync(candidate)) {
            return candidate;
        }

        directory = path.dirname(directory);
    }

    throw new Error(`cannot locate scripts/${name} above ${__dirname}`);
};

const { stopProcessTree } = require(scriptsModule('processTree.js')) as { stopProcessTree: (processId?: number) => void };

export type ExternalTool = 'pac' | 'msagent';

export interface ExternalResult {
    code: number;
    output: string;
}

export interface CloneRequest {
    agentId: string;
    environmentId: string;
    projectDirectory: string;
    displayName: string;
}

export interface ProjectRequest {
    agentId: string;
    projectDirectory: string;
    deploymentName?: string;
}

export const deploymentNameFor = (suffix: string, shapeId: string): string =>
    `lspe2e-${suffix}-${shapeId}`.replace(/[^-a-zA-Z0-9_]/g, '-').slice(0, 42).padEnd(4, '0');

export const deploymentArgs = (tool: ExternalTool, request: { projectDirectory: string; deploymentName: string }): string[] =>
    tool === 'pac'
        ? []
        : ['deployment', 'create', '--project', request.projectDirectory, '--deployment-name', request.deploymentName, '--deployment-type', 'dev', '--non-interactive'];

export const cloneArgs = (tool: ExternalTool, request: CloneRequest): string[] =>
    tool === 'pac'
        ? ['copilot', 'clone', '--bot', request.agentId, '--environment', request.environmentId, '--output-dir', request.projectDirectory, '--display-name', request.displayName]
        : ['agent', 'create', '--agent-type', 'MCSAgent', '--project', request.projectDirectory, '--environment-id', request.environmentId, '--mcs-clone-agent-id', request.agentId, '--mcs-clone-environment-id', request.environmentId, '--non-interactive'];

export const initArgs = (tool: ExternalTool, request: { agentName: string; projectDirectory: string }): string[] =>
    tool === 'pac'
        ? []
        : ['agent', 'init', '--agent-type', 'MCSAgent', '--agent-name', request.agentName, '--mcs-agent-source', request.projectDirectory, '--non-interactive'];

export const pullArgs = (tool: ExternalTool, request: ProjectRequest): string[] =>
    tool === 'pac'
        ? ['copilot', 'pull', '--project-dir', request.projectDirectory]
        : ['agent', 'pull', '--project', request.projectDirectory, '--non-interactive'];

export const pushArgs = (tool: ExternalTool, request: ProjectRequest): string[] =>
    tool === 'pac'
        ? ['copilot', 'push', '--project-dir', request.projectDirectory]
        : ['agent', 'deploy', '--project', request.projectDirectory, ...(request.deploymentName ? ['--deployment-name', request.deploymentName] : []), '--overwrite', '--non-interactive'];

export const quoteArgument = (value: string): string =>
    /[\s"&|<>^]/.test(value) ? `"${value.replace(/"/g, '\\"')}"` : value;

const onWindows = (): boolean => process.platform === 'win32';

export const runExternal = (tool: ExternalTool, args: readonly string[], timeoutMs = 900_000): Promise<ExternalResult> =>
    new Promise(resolve => {
        const windows = onWindows();
        const child = windows
            ? spawn(tool, args.map(quoteArgument), { shell: true, windowsHide: true })
            : spawn(tool, [...args], { detached: true });

        let output = '';
        let timedOut = false;
        let settled = false;
        const timer = setTimeout(() => {
            timedOut = true;
            stopProcessTree(child.pid);
        }, timeoutMs);

        const settle = (code: number): void => {
            if (settled) {
                return;
            }

            settled = true;
            clearTimeout(timer);
            detail(`${tool} ${args[0]} ${args[1]}`, timedOut ? `timed out after ${timeoutMs}ms` : `exit ${code}`);
            resolve({ code, output: timedOut ? `${output}\n${tool} was terminated after ${timeoutMs}ms` : output });
        };

        child.stdout.on('data', chunk => { output += String(chunk); });
        child.stderr.on('data', chunk => { output += String(chunk); });

        child.on('error', error => {
            output += String(error);
            settle(-1);
        });

        child.on('exit', code => settle(timedOut ? -1 : code ?? -1));
    });

export const requireExternal = async (tool: ExternalTool, args: readonly string[], label: string, timeoutMs = 900_000): Promise<string> => {
    const result = await runExternal(tool, args, timeoutMs);

    if (result.code !== 0) {
        throw new Error(`${label} failed (exit ${result.code}): ${result.output.trim().split('\n').slice(-6).join(' | ')}`);
    }

    return result.output;
};

export const CONNECTION_RELATIVE_PATH = path.join('.mcs', 'conn.json');

const fillMissing = (target: Record<string, unknown>, reference: Record<string, unknown>, repaired: string[], prefix = ''): void => {
    for (const [key, value] of Object.entries(reference)) {
        if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
            if (target[key] !== null && typeof target[key] === 'object' && !Array.isArray(target[key])) {
                fillMissing(target[key] as Record<string, unknown>, value as Record<string, unknown>, repaired, `${prefix}${key}.`);
                continue;
            }
        }

        if (target[key] === undefined || target[key] === null) {
            target[key] = value;
            repaired.push(`${prefix}${key}`);
        }
    }
};

export const repairExternalConnection = (agentRoot: string, referenceAgentRoot: string): string[] => {
    const targetPath = path.join(agentRoot, CONNECTION_RELATIVE_PATH);
    const referencePath = path.join(referenceAgentRoot, CONNECTION_RELATIVE_PATH);

    if (!fs.existsSync(targetPath) || !fs.existsSync(referencePath)) {
        return [];
    }

    const target = JSON.parse(fs.readFileSync(targetPath, 'utf8')) as Record<string, unknown>;
    const reference = JSON.parse(fs.readFileSync(referencePath, 'utf8')) as Record<string, unknown>;
    const repaired: string[] = [];

    fillMissing(target, reference, repaired);

    if (repaired.length > 0) {
        fs.writeFileSync(targetPath, JSON.stringify(target, undefined, 2), 'utf8');
    }

    return repaired;
};
