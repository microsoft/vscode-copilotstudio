import * as assert from 'node:assert';
import * as path from 'node:path';
import { describe, test } from 'node:test';
import { CopilotStudioWorkspace, WorkspaceType } from '../../sync/localWorkspaces';
import { buildReattachPlanCore, parseReferencedDirectories, CollectionCandidate } from '../../commands/reattachPlan';

const makeWorkspace = (overrides: Partial<CopilotStudioWorkspace>): CopilotStudioWorkspace => ({
	workspaceUri: 'file:///agents/test/',
	displayName: 'Test Agent',
	description: '',
	icon: undefined as any,
	type: WorkspaceType.Agent,
	...overrides,
});

const hostFolder = path.resolve('/agents/host');
const makeCollectionCandidate = (relativeDirectory: string, trailingSeparator = false): CollectionCandidate => ({
	workspace: makeWorkspace({ displayName: relativeDirectory, type: WorkspaceType.ComponentCollection }),
	folderPath: path.resolve(hostFolder, relativeDirectory) + (trailingSeparator ? path.sep : ''),
});

describe('parseReferencedDirectories', () => {
	test('parses the schema-first clone format where directory is on its own line', () => {
		const result = parseReferencedDirectories('componentCollections:\n  - schemaName:\n    directory: ../MyCC333/\n');
		assert.deepStrictEqual(result, ['../MyCC333/']);
	});

	test('parses the directory-first list format', () => {
		const result = parseReferencedDirectories('componentCollections:\n  - directory: ../MyCC333/\n');
		assert.deepStrictEqual(result, ['../MyCC333/']);
	});

	test('returns every directory value across multiple references', () => {
		const result = parseReferencedDirectories('componentCollections:\n  - schemaName:\n    directory: ../Cc1/\n  - schemaName:\n    directory: ../Cc2/\n');
		assert.deepStrictEqual(result, ['../Cc1/', '../Cc2/']);
	});

	test('unwraps single- and double-quoted directory values', () => {
		const result = parseReferencedDirectories(`  - directory: "../Cc1/"\n  - directory: '../Cc2/'\n`);
		assert.deepStrictEqual(result, ['../Cc1/', '../Cc2/']);
	});

	test('skips empty directory values', () => {
		const result = parseReferencedDirectories('  - directory:   \n  - directory: ../Cc1/\n');
		assert.deepStrictEqual(result, ['../Cc1/']);
	});

	test('ignores unrelated keys and returns empty array when no directory entries exist', () => {
		assert.deepStrictEqual(parseReferencedDirectories('componentCollections:\n  - schemaName: bot_cc1\n'), []);
	});
});

describe('buildReattachPlanCore', () => {
	test('a component collection workspace returns only itself with no missing directories', () => {
		const collection = makeWorkspace({ type: WorkspaceType.ComponentCollection });
		const plan = buildReattachPlanCore(collection, hostFolder, '  - directory: ../ignored/\n', []);
		assert.deepStrictEqual(plan.workspaces, [collection]);
		assert.deepStrictEqual(plan.missingCollectionDirectories, []);
	});

	test('an agent with no references file returns only itself', () => {
		const agent = makeWorkspace({});
		const plan = buildReattachPlanCore(agent, hostFolder, undefined, []);
		assert.deepStrictEqual(plan.workspaces, [agent]);
		assert.deepStrictEqual(plan.missingCollectionDirectories, []);
	});

	test('an agent whose referenced directory resolves to a known collection includes that collection', () => {
		const agent = makeWorkspace({});
		const candidate = makeCollectionCandidate('../MyCC333');
		const plan = buildReattachPlanCore(agent, hostFolder, 'componentCollections:\n  - schemaName:\n    directory: ../MyCC333/\n', [candidate]);
		assert.deepStrictEqual(plan.workspaces, [agent, candidate.workspace]);
		assert.deepStrictEqual(plan.missingCollectionDirectories, []);
	});

	test('an agent whose referenced directory has no matching workspace reports it as missing', () => {
		const agent = makeWorkspace({});
		const plan = buildReattachPlanCore(agent, hostFolder, '  - directory: ../MyCC333/\n', []);
		assert.deepStrictEqual(plan.workspaces, [agent]);
		assert.deepStrictEqual(plan.missingCollectionDirectories, [path.resolve(hostFolder, '../MyCC333')]);
	});

	test('a candidate at the referenced path that is not a component collection is treated as missing', () => {
		const agent = makeWorkspace({});
		const nonCollection: CollectionCandidate = { workspace: makeWorkspace({ type: WorkspaceType.Agent }), folderPath: path.resolve(hostFolder, '../MyCC333') };
		const plan = buildReattachPlanCore(agent, hostFolder, '  - directory: ../MyCC333/\n', [nonCollection]);
		assert.deepStrictEqual(plan.workspaces, [agent]);
		assert.deepStrictEqual(plan.missingCollectionDirectories, [path.resolve(hostFolder, '../MyCC333')]);
	});

	test('duplicate references that normalize to the same directory are collapsed to a single collection', () => {
		const agent = makeWorkspace({});
		const candidate = makeCollectionCandidate('../MyCC333', true);
		const plan = buildReattachPlanCore(agent, hostFolder, '  - directory: ../MyCC333/\n  - directory: ../MyCC333\n', [candidate]);
		assert.deepStrictEqual(plan.workspaces, [agent, candidate.workspace]);
		assert.deepStrictEqual(plan.missingCollectionDirectories, []);
	});

	test('matches candidate paths ignoring case only on case-insensitive file systems', () => {
		const agent = makeWorkspace({});
		const candidate: CollectionCandidate = { workspace: makeWorkspace({ type: WorkspaceType.ComponentCollection }), folderPath: path.resolve(hostFolder, '../MYCC333') + path.sep };
		const plan = buildReattachPlanCore(agent, hostFolder, '  - directory: ../mycc333/\n', [candidate]);
		if (process.platform === 'win32' || process.platform === 'darwin') {
			assert.deepStrictEqual(plan.workspaces, [agent, candidate.workspace]);
			assert.deepStrictEqual(plan.missingCollectionDirectories, []);
		} else {
			assert.deepStrictEqual(plan.workspaces, [agent]);
			assert.deepStrictEqual(plan.missingCollectionDirectories, [path.resolve(hostFolder, '../mycc333')]);
		}
	});
});
