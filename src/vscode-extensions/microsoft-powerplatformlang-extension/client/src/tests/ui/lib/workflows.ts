import * as fs from 'node:fs';
import * as path from 'node:path';
import { applyReplacements, readScalar, renamePaths, type RenameOutcome } from './renamer';
import { replaceLine } from './lineEdit';

const WORKFLOWS_DIRECTORY = 'workflows';
const METADATA_FILE = 'metadata.yml';
const DEFINITION_FILE = 'workflow.json';
const DESCRIPTION_KEY = 'description:';

export interface WorkflowIdentity {
    folder: string;
    metadataPath: string;
    definitionPath: string;
    workflowId: string;
    name: string;
}

/**
 * The folder name the product derives from a workflow name: every character a folder may not carry
 * on some platform is dropped, so the fixture's folders and its `jsonFileName` can be rewritten the
 * same way the service would have written them.
 */
export const portableFolderName = (name: string): string =>
    [...name].filter(character => !/[\s\\/:*?"<>|]/.test(character) && character >= ' ').join('').replace(/[. ]+$/, '');

export const workflowIdentity = (workspaceRoot: string): WorkflowIdentity => {
    const workflowsRoot = path.join(workspaceRoot, WORKFLOWS_DIRECTORY);
    const folders = (fs.existsSync(workflowsRoot) ? fs.readdirSync(workflowsRoot, { withFileTypes: true }) : [])
        .filter(entry => entry.isDirectory())
        .map(entry => path.join(workflowsRoot, entry.name))
        .filter(candidate => fs.existsSync(path.join(candidate, METADATA_FILE)));

    if (folders.length !== 1) {
        throw new Error(`expected exactly one workflow under ${workflowsRoot}, found ${folders.length}`);
    }

    const metadataPath = path.join(folders[0], METADATA_FILE);
    const workflowId = readScalar(metadataPath, 'workflowId');
    const name = readScalar(metadataPath, 'name');

    if (!workflowId || !name) {
        throw new Error(`${metadataPath} does not name a workflow: workflowId '${workflowId ?? ''}', name '${name ?? ''}'`);
    }

    return { folder: folders[0], metadataPath, definitionPath: path.join(folders[0], DEFINITION_FILE), workflowId, name };
};

const unquoted = (value: string): string =>
    /^(['"]).*\1$/.test(value) ? value.slice(1, -1) : value;

export const workflowDescription = (workspaceRoot: string): string =>
    unquoted(readScalar(workflowIdentity(workspaceRoot).metadataPath, 'description') ?? '');

export const setWorkflowDescription = (workspaceRoot: string, description: string): void => {
    replaceLine(workflowIdentity(workspaceRoot).metadataPath, DESCRIPTION_KEY, `${DESCRIPTION_KEY} ${description}`);
};

/**
 * Gives a placed workflow fixture an identity of its own, so one run's cloud flow is never the
 * flow another run created. The name, the folder name the product derives from it, and the
 * identifier are rewritten together, because the fixture spells all three.
 */
export const renameWorkflowWorkspace = (contentRoot: string, displayName: string, workflowId: string): RenameOutcome => {
    const identity = workflowIdentity(contentRoot);
    const replacements = new Map<string, string>([[identity.workflowId, workflowId], [identity.name, displayName]]);
    const portable = portableFolderName(identity.name);

    if (!replacements.has(portable)) {
        replacements.set(portable, portableFolderName(displayName));
    }

    const filesRewritten = applyReplacements(contentRoot, replacements);
    renamePaths(contentRoot, replacements);

    return { originalIdentity: identity.workflowId, identity: workflowId, displayName, filesRewritten };
};
