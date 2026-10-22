import * as assert from 'node:assert';
import { after, describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { renameWorkspace, readSchemaName, copyDirectory, readScalar } from '../ui/lib/renamer';
import { assertDeletableEnvironment, requireEnvironmentDataverseUrl, assertSafePrefix, matchesPrefix, buildAgentCloneArgs, buildAgentDeleteArgs, msagentFailure, deleteDependencies, findWorkflowArtifacts, shouldSkipCleanup, odataLiteral, stillReferencedBy } from '../ui/lib/cleanup';
import { parseServerProcessIds } from '../ui/lib/liveness';
import { activationDecision } from '../ui/lib/reentry';
import { runVerdict } from '../runnerCore';
import { classifyPush, reportedFailure, reattachQuickPickRules, APPLY_DONE_TEXT, ERROR_GATE_TEXT, REMOTE_BLOCK_TEXT, CLOUD_DRIFT_TEXT, REATTACH_DONE_SIGNALS, REATTACH_ATTACHED_SIGNALS } from '../ui/lib/commands';
import { labelsCarry, namesCarry, describeComponentDrift, type ComponentState } from '../ui/lib/dataverse';
import { cloneArgs, initArgs, deploymentArgs, deploymentNameFor, pullArgs, pushArgs, repairExternalConnection, quoteArgument, CONNECTION_RELATIVE_PATH } from '../ui/lib/external';
import { hasConflictMarkers, findConflictBlocks, resolveKeepingValue } from '../ui/lib/conflict';
import { localStateUri, remoteStateUri, componentSchemaName } from '../ui/lib/diffUris';
import { RunManifest, formatSeconds, scenarioLine, splitExpectation } from '../ui/lib/manifest';
import { replaceLine, readLine } from '../ui/lib/lineEdit';
import { hashTree, diffHashes, fileSet, listFiles } from '../ui/lib/tree';
import { SHAPES, ALL_SHAPES, WORKFLOW_SHAPES, variantLine, runsScenario } from '../ui/lib/shapes';
import { ALL_GROUPS, ALL_SELECTORS, CATALOGUE, parseGroups, catalogueEntry, isSelected, orderScenarioFiles } from '../ui/lib/groups';
import { addNewComponents, metadataMaterialised, newComponentSourceRoot, removeComponent, declaredConnectionReferences, requiredConnectionReferences, runMintedConnectionReferences, survivingComponents } from '../ui/lib/components';
import { portableFolderName, renameWorkflowWorkspace, setWorkflowDescription, workflowDescription, workflowIdentity } from '../ui/lib/workflows';

const temporaryRoots: string[] = [];

const scratch = (): string => {
	const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lspe2e-unit-'));
	temporaryRoots.push(root);
	return root;
};

const listFilesIfPresent = (root: string): string[] => fs.existsSync(root) ? listFiles(root, []) : [];

const fixtures = path.resolve(__dirname, '..', '..', '..', 'src', 'tests', 'ui', 'fixtures');

const fixtureRootOf = (shape: typeof SHAPES[number]): string => path.join(fixtures, shape.fixtureDirectory);

const fixtureSchemaName = (shape: typeof SHAPES[number]): string => readSchemaName(fixtureRootOf(shape));

const stage = (shape: typeof SHAPES[number]): string => {
	const root = scratch();
	copyDirectory(fixtureRootOf(shape), root);

	return root;
};

const stageAndAdd = (shape: typeof SHAPES[number], suffix: string): { root: string; facts: ReturnType<typeof addNewComponents> } => {
	const root = stage(shape);

	return { root, facts: addNewComponents(root, shape, fixtures, fixtureSchemaName(shape), suffix) };
};

const ASSET_ID_KEYS = ['aIModelId', 'workflowId', 'connectorid', 'connectorinternalid'];

const assetIdsIn = (root: string): string[] =>
	listFiles(root, []).filter(file => path.basename(file) === 'metadata.yml')
		.flatMap(file => ASSET_ID_KEYS.map(key => {
			const value = readScalar(file, key) ?? (/"?connectorid"?\s*:\s*"([^"]+)"/.exec(fs.readFileSync(file, 'utf8')) ?? [])[1];
			return value ? `${key}=${value}` : undefined;
		}))
		.filter((entry): entry is string => entry !== undefined)
		.sort();

type StubbedResponse = { ok: boolean; status: number; body: string };

const withStubbedFetch = async <T>(seen: { url: string; method: string }[], reply: () => StubbedResponse, body: () => Promise<T>): Promise<T> => {
	const original = globalThis.fetch;

	globalThis.fetch = (async (url: string, init?: { method?: string }) => {
		seen.push({ url: String(url), method: init?.method ?? 'GET' });
		const response = reply();

		return { ok: response.ok, status: response.status, text: async () => response.body };
	}) as unknown as typeof globalThis.fetch;

	try {
		return await body();
	} finally {
		globalThis.fetch = original;
	}
};

after(() => {
	for (const root of temporaryRoots.splice(0, temporaryRoots.length)) {
		fs.rmSync(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
	}
});

describe('lspE2E renamer', () => {

	const buildWorkspace = (): string => {
		const root = scratch();
		fs.mkdirSync(path.join(root, 'topics'), { recursive: true });
		fs.mkdirSync(path.join(root, 'connectors', 'cre98_weather'), { recursive: true });
		fs.writeFileSync(path.join(root, 'settings.mcs.yml'), 'displayName: Old Name\nschemaName: ahe2e_Original\n');
		fs.writeFileSync(path.join(root, 'topics', 'Goodbye.mcs.yml'), 'dialog: ahe2e_Original.topic.Escalate\n');
		fs.writeFileSync(path.join(root, 'topics', 'ahe2e_Original.topic.X.mcs.yml'), 'kind: AdaptiveDialog\n');
		fs.writeFileSync(path.join(root, 'connectors', 'cre98_weather', 'metadata.yml'), 'iconblob: connectors/cre98_weather/iconblob.png\n');
		fs.writeFileSync(path.join(root, 'connectors', 'cre98_weather', 'iconblob.png'), Buffer.from([0x89, 0x50, 0x4e, 0x47]));
		return root;
	};

	test('rewrites only the agent schema name', () => {
		const root = buildWorkspace();
		renameWorkspace(root, root, 'abc12_lspe2eabcdclassic', 'lspe2e abcd classic');

		assert.strictEqual(readSchemaName(root), 'abc12_lspe2eabcdclassic');
		assert.match(fs.readFileSync(path.join(root, 'topics', 'Goodbye.mcs.yml'), 'utf8'), /abc12_lspe2eabcdclassic\.topic\.Escalate/);
	});

	test('leaves connector and connection prefixes untouched', () => {
		const root = buildWorkspace();
		renameWorkspace(root, root, 'abc12_new', 'New');

		assert.ok(fs.existsSync(path.join(root, 'connectors', 'cre98_weather', 'metadata.yml')));
		assert.match(fs.readFileSync(path.join(root, 'connectors', 'cre98_weather', 'metadata.yml'), 'utf8'), /cre98_weather/);
	});

	test('renames file names carrying the schema name', () => {
		const root = buildWorkspace();
		renameWorkspace(root, root, 'abc12_new', 'New');

		assert.ok(fs.existsSync(path.join(root, 'topics', 'abc12_new.topic.X.mcs.yml')));
		assert.ok(!fs.existsSync(path.join(root, 'topics', 'ahe2e_Original.topic.X.mcs.yml')));
	});

	test('sets the display name', () => {
		const root = buildWorkspace();
		renameWorkspace(root, root, 'abc12_new', 'Brand New');

		assert.match(fs.readFileSync(path.join(root, 'settings.mcs.yml'), 'utf8'), /displayName: Brand New/);
	});

	test('skips binary files', () => {
		const root = buildWorkspace();
		renameWorkspace(root, root, 'abc12_new', 'New');

		assert.deepStrictEqual(
			[...fs.readFileSync(path.join(root, 'connectors', 'cre98_weather', 'iconblob.png'))],
			[0x89, 0x50, 0x4e, 0x47]);
	});

	test('renames from a nested identity root without touching siblings', () => {
		const root = scratch();
		const agentRoot = path.join(root, 'Agent');
		fs.mkdirSync(agentRoot, { recursive: true });
		fs.mkdirSync(path.join(root, 'Sibling'), { recursive: true });
		fs.writeFileSync(path.join(agentRoot, 'settings.mcs.yml'), 'displayName: Old\nschemaName: abc12_Original\n');
		fs.writeFileSync(path.join(root, 'Sibling', 'reference.mcs.yml'), 'owner: abc12_Original\n');

		renameWorkspace(agentRoot, root, 'abc12_renamed', 'Renamed');

		assert.match(fs.readFileSync(path.join(root, 'Sibling', 'reference.mcs.yml'), 'utf8'), /abc12_renamed/);
	});

	test('copyDirectory reproduces the tree', () => {
		const source = buildWorkspace();
		const destination = path.join(scratch(), 'copy');
		copyDirectory(source, destination);

		assert.deepStrictEqual(fileSet(destination), fileSet(source));
	});
});

describe('lspE2E fixtures reference rows they do not own', () => {

	const buildAssets = (): string => {
		const root = scratch();
		fs.mkdirSync(path.join(root, 'prompts', 'summarize-00000000-0000-0000-0000-000000009a1d'), { recursive: true });
		fs.mkdirSync(path.join(root, 'actions'), { recursive: true });
		fs.writeFileSync(path.join(root, 'settings.mcs.yml'), 'displayName: Old\nschemaName: ahe2e_Original\n');
		fs.writeFileSync(
			path.join(root, 'prompts', 'summarize-00000000-0000-0000-0000-000000009a1d', 'metadata.yml'),
			'aIModelId: 00000000-0000-0000-0000-000000009a1d\nname: summarize\n');
		fs.writeFileSync(path.join(root, 'actions', 'summarize.mcs.yml'), '  aIModelId: 00000000-0000-0000-0000-000000009a1d\n');
		return root;
	};

	test('a rename leaves every cloud-scoped asset id exactly as the fixture recorded it', () => {
		const root = buildAssets();
		renameWorkspace(root, root, 'abc12_renamed', 'Renamed');

		assert.ok(fs.existsSync(path.join(root, 'prompts', 'summarize-00000000-0000-0000-0000-000000009a1d')));
		assert.strictEqual(
			readScalar(path.join(root, 'prompts', 'summarize-00000000-0000-0000-0000-000000009a1d', 'metadata.yml'), 'aIModelId'),
			'00000000-0000-0000-0000-000000009a1d');
	});

	test('the action and the prompt still agree after a rename, so the reference resolves', () => {
		const root = buildAssets();
		renameWorkspace(root, root, 'abc12_renamed', 'Renamed');

		const [folder] = fs.readdirSync(path.join(root, 'prompts'));

		assert.strictEqual(
			readScalar(path.join(root, 'actions', 'summarize.mcs.yml'), 'aIModelId'),
			readScalar(path.join(root, 'prompts', folder, 'metadata.yml'), 'aIModelId'));
	});

	test('every shipped fixture keeps the asset ids it was captured with', () => {
		for (const shape of SHAPES) {
			const staged = stage(shape);
			const before = assetIdsIn(staged);
			renameWorkspace(staged, staged, 'abc12_lspe2etest', 'lspe2e test');

			assert.deepStrictEqual(
				assetIdsIn(staged),
				before,
				`${shape.id} rewrote an asset id. These rows are referenced, not created: the reattach pull deletes prompts/, workflows/ and connectors/ before any push can create them, so a rewritten id resolves to nothing and the push is gated on 'AIModel with id ... not found'.`);
		}
	});
});

describe('lspE2E cleanup only ever targets rows the run minted', () => {
	test('only connection references carrying the run agent schema name are recorded', () => {
		for (const shape of SHAPES) {
			const staged = stage(shape);
			const schemaName = 'abc12_lspe2edeadbeefshape';
			renameWorkspace(staged, staged, schemaName, 'lspe2e deadbeef shape');

			const declared = declaredConnectionReferences(staged);
			const minted = runMintedConnectionReferences(staged, schemaName);
			const shared = declared.filter(name => !minted.includes(name));

			assert.ok(minted.length > 0, `${shape.id} mints no connection reference, so the run would leave one behind`);
			assert.ok(
				minted.every(name => name.startsWith(`${schemaName}.`)),
				`${shape.id} recorded a reference it did not mint; cleanup would delete a row shared with the tenant`);

			for (const name of shared) {
				assert.ok(!name.startsWith(`${schemaName}.`), `${name} was wrongly treated as shared`);
			}
		}
	});

	test('a reference the fixture shares with the tenant is never recorded for deletion', () => {
		const root = scratch();
		fs.writeFileSync(path.join(root, 'settings.mcs.yml'), 'displayName: x\nschemaName: abc12_run\n');
		fs.writeFileSync(path.join(root, 'connectionreferences.mcs.yml'), [
			'connectionReferences:',
			'  - connectionReferenceLogicalName: abc12_run.shared_teams.abc',
			'  - connectionReferenceLogicalName: new_sharedmsnweather_08889',
			'  - connectionReferenceLogicalName: abc12_runner.shared_teams.def',
			'',
		].join('\n'));

		assert.deepStrictEqual(runMintedConnectionReferences(root, 'abc12_run'), ['abc12_run.shared_teams.abc']);
	});

	test('an empty workspace mints nothing rather than reporting a phantom row', () => {
		assert.deepStrictEqual(runMintedConnectionReferences(scratch(), 'abc12_run'), []);
	});
});

describe('lspE2E cloud drift', () => {
	const state = (id: string, schemaName: string, body: string): ComponentState => ({ id, schemaName, name: schemaName, body });

	test('an untouched cloud reports no drift', () => {
		const before = [state('1', 'a.topic.Goodbye', 'x'), state('2', 'a.topic.ThankYou', 'y')];

		assert.deepStrictEqual(describeComponentDrift(before, [...before]), []);
	});

	test('a component deleted and recreated under the same name is still reported', () => {
		const before = [state('1', 'a.topic.Goodbye', 'x')];
		const after = [state('2', 'a.topic.Goodbye', 'x')];

		assert.deepStrictEqual(describeComponentDrift(before, after), ['added a.topic.Goodbye', 'removed a.topic.Goodbye']);
	});

	test('a rewritten component is reported even when nothing was added or removed', () => {
		const before = [state('1', 'a.topic.Goodbye', 'x'), state('2', 'a.topic.ThankYou', 'y')];
		const after = [state('1', 'a.topic.Goodbye', 'x'), state('2', 'a.topic.ThankYou', 'CHANGED')];

		assert.deepStrictEqual(describeComponentDrift(before, after), ['rewrote a.topic.ThankYou']);
	});

	test('a component renamed in place is reported, because its body alone did not move', () => {
		const before = [state('1', 'a.topic.Goodbye', 'x')];

		assert.deepStrictEqual(
			describeComponentDrift(before, [{ ...before[0], name: 'Farewell' }]),
			['rewrote a.topic.Goodbye']);

		assert.deepStrictEqual(
			describeComponentDrift(before, [{ ...before[0], schemaName: 'a.topic.Farewell' }]),
			['rewrote a.topic.Goodbye']);
	});

	test('an equal component count cannot hide a swap', () => {
		assert.deepStrictEqual(
			describeComponentDrift([state('1', 'a', 'x')], [state('9', 'b', 'x')]),
			['added b', 'removed a']);
	});
});

describe('lspE2E conflict handling', () => {

	const conflicted = [
		'intro line',
		'<<<<<<< local',
		'prompt: Version 2 - keep me',
		'=======',
		'prompt: Local 1 - drop me',
		'>>>>>>> remote',
		'outro line',
	].join('\n');

	test('detects conflict markers', () => {
		assert.strictEqual(hasConflictMarkers(conflicted), true);
	});

	test('a markdown setext heading is not a conflict', () => {
		assert.strictEqual(hasConflictMarkers('Title\n=======\nbody\n'), false);
	});

	test('finds both sides of a block', () => {
		const blocks = findConflictBlocks(conflicted);

		assert.strictEqual(blocks.length, 1);
		assert.deepStrictEqual(blocks[0].first, ['prompt: Version 2 - keep me']);
		assert.deepStrictEqual(blocks[0].second, ['prompt: Local 1 - drop me']);
	});

	test('keeps the side holding the expected value regardless of order', () => {
		const first = resolveKeepingValue(conflicted, 'Version 2 - keep me');
		assert.strictEqual(first.keptSide, 'first');
		assert.match(first.content, /Version 2 - keep me/);
		assert.doesNotMatch(first.content, /Local 1 - drop me/);

		const flipped = conflicted
			.replace('prompt: Version 2 - keep me', 'PLACEHOLDER')
			.replace('prompt: Local 1 - drop me', 'prompt: Version 2 - keep me')
			.replace('PLACEHOLDER', 'prompt: Local 1 - drop me');

		const second = resolveKeepingValue(flipped, 'Version 2 - keep me');
		assert.strictEqual(second.keptSide, 'second');
		assert.match(second.content, /Version 2 - keep me/);
	});

	test('leaves no markers behind', () => {
		assert.strictEqual(hasConflictMarkers(resolveKeepingValue(conflicted, 'Version 2 - keep me').content), false);
	});

	test('fails when neither side carries the local value', () => {
		assert.throws(() => resolveKeepingValue(conflicted, 'Version 3 - absent'), /did not survive the merge/);
	});

	test('fails when there is nothing to resolve', () => {
		assert.throws(() => resolveKeepingValue('clean file\n', 'anything'), /no conflict block/);
	});

	test('resolves several blocks in one file', () => {
		const twice = `${conflicted}\n${conflicted}`;
		const outcome = resolveKeepingValue(twice, 'Version 2 - keep me');

		assert.strictEqual(outcome.blocksResolved, 2);
		assert.strictEqual(hasConflictMarkers(outcome.content), false);
	});
});

describe('lspE2E liveness', () => {
	const SERVER_PATH = process.platform === 'win32' ? 'C:\\repo\\lspOut\\LanguageServerHost.exe' : '/repo/lspOut/LanguageServerHost';
	const OTHER_PATH = process.platform === 'win32' ? 'C:\\other\\lspOut\\LanguageServerHost.exe' : '/other/lspOut/LanguageServerHost';

	test('only a server started from this run\'s binary counts', () => {
		const listing = [`  732 ${SERVER_PATH}`, `  918 ${OTHER_PATH}`].join('\n');

		assert.deepStrictEqual(parseServerProcessIds(listing, SERVER_PATH), [732]);
	});

	test('several servers from the same binary are all reported', () => {
		const listing = [`24680 ${SERVER_PATH}`, `13579 ${SERVER_PATH}`].join('\n');

		assert.deepStrictEqual(parseServerProcessIds(listing, SERVER_PATH), [24680, 13579]);
	});

	test('no running server yields no ids', () => {
		assert.deepStrictEqual(parseServerProcessIds('  501 /usr/bin/node', SERVER_PATH), []);
		assert.deepStrictEqual(parseServerProcessIds('INFO: No tasks are running which match the specified criteria.', SERVER_PATH), []);
	});

	test('a path that differs only in case still matches, because Windows reports either', () => {
		assert.deepStrictEqual(parseServerProcessIds(`  732 ${SERVER_PATH.toUpperCase()}`, SERVER_PATH), [732]);
	});

	test('a line naming the binary without a pid is ignored', () => {
		assert.deepStrictEqual(parseServerProcessIds(`stopped ${SERVER_PATH}`, SERVER_PATH), []);
	});
});

describe('lspE2E runner verdict', () => {
	const clean = { passed: 8, failed: 0, reportFailed: false };

	test('a passing run is never failed by state the runner did not set', () => {
		assert.strictEqual(runVerdict(clean), 0, 'the extension host shares its process with other extensions, so process.exitCode is not ours to read');
	});

	test('a real failure fails the run', () => {
		assert.strictEqual(runVerdict({ ...clean, failed: 1 }), 1);
	});

	test('an abandoned scenario fails the run even when nothing reported a failure', () => {
		assert.strictEqual(runVerdict({ ...clean, abandoned: 'B-07 [cli]' }), 1);
	});

	test('a report that throws fails the run, because its results were never written', () => {
		assert.strictEqual(runVerdict({ ...clean, reportFailed: true }), 1);
	});

	test('a run where nothing executed proves nothing and fails', () => {
		assert.strictEqual(runVerdict({ passed: 0, failed: 0, reportFailed: false }), 1);
	});
});

describe('lspE2E cleanup guards', () => {
	const CONFIGURED = '00000000-0000-0000-0000-00000000c0f6';

	test('deleting in the configured environment is allowed', () => {
		assertDeletableEnvironment(CONFIGURED, CONFIGURED);
	});

	test('environment id casing does not defeat the guard', () => {
		assertDeletableEnvironment(CONFIGURED.toUpperCase(), CONFIGURED);
	});

	test('a different environment is refused', () => {
		assert.throws(
			() => assertDeletableEnvironment('00000000-0000-0000-0000-00000000da7a', CONFIGURED),
			/the suite is configured for/);
	});

	test('an empty environment is refused', () => {
		assert.throws(() => assertDeletableEnvironment('', CONFIGURED), /no environment/);
	});

	test('the prefix match is anchored at the start and case insensitive', () => {
		assert.strictEqual(matchesPrefix('lspe2e 7f3a classic', 'lspe2e'), true);
		assert.strictEqual(matchesPrefix('LSPE2E 7f3a classic', 'lspe2e'), true);
		assert.strictEqual(matchesPrefix('my lspe2e agent', 'lspe2e'), false);
		assert.strictEqual(matchesPrefix('N2 A9', 'lspe2e'), false);
		assert.strictEqual(matchesPrefix(undefined, 'lspe2e'), false);
	});

	test('agents the operator owns are never selected', () => {
		for (const name of ['NB2', 'NC1', 'N2 A9', 'Agent M1', 'NA B4', 'Ex1']) {
			assert.strictEqual(matchesPrefix(name, 'lspe2e'), false, name);
		}
	});

	test('deleting an agent clones it to a scratch project first, since delete acts on a project', () => {
		const args = buildAgentCloneArgs('bot-1', CONFIGURED, 'C:\\scratch\\delete-bot-1');

		assert.deepStrictEqual(args.slice(0, 2), ['agent', 'create']);
		assert.strictEqual(args[args.indexOf('--mcs-clone-agent-id') + 1], 'bot-1');
		assert.strictEqual(args[args.indexOf('--project') + 1], 'C:\\scratch\\delete-bot-1');
		assert.ok(args.includes('--non-interactive'));
	});

	test('both environment flags are pinned, so the active msagent environment cannot misdirect a delete', () => {
		const args = buildAgentCloneArgs('bot-1', CONFIGURED, 'C:\\scratch\\delete-bot-1');

		assert.strictEqual(args[args.indexOf('--mcs-clone-environment-id') + 1], CONFIGURED);
		assert.strictEqual(args[args.indexOf('--environment-id') + 1], CONFIGURED);
	});

	test('the delete names the scratch project rather than relying on the working directory', () => {
		const args = buildAgentDeleteArgs('C:\\scratch\\delete-bot-1');

		assert.deepStrictEqual(args.slice(0, 2), ['agent', 'delete']);
		assert.strictEqual(args[args.indexOf('--project') + 1], 'C:\\scratch\\delete-bot-1');
		assert.ok(args.includes('--non-interactive'));
	});

	test('a failure is reported from msagent json rather than as a bare exit code', () => {
		assert.strictEqual(
			msagentFailure(JSON.stringify({ success: false, errorMessage: 'No agent configuration was found.', errorKind: 'config-not-found' })),
			'No agent configuration was found. (config-not-found)');
		assert.match(msagentFailure('not json, just noise\nlast line'), /last line/);
	});

	test('a dependency that is already gone is not reported as a failure', async () => {
		const result = await withStubbedFetch([], () => ({ ok: false, status: 404, body: 'Does Not Exist' }), () =>
			deleteDependencies('https://contoso.crm.dynamics.com', 'token', [{ kind: 'connectionreference', id: 'cr_one', name: 'one' }]));

		assert.deepStrictEqual(result, { deleted: [], retained: [], failed: [] });
	});

	test('a dependency held by components the run did not create is kept, not failed', async () => {
		const seen: { url: string; method: string }[] = [];
		let call = 0;
		const result = await withStubbedFetch(seen, () => (call++ === 0
			? { ok: true, status: 200, body: JSON.stringify({ value: [{ connectionreferenceid: 'cr-1' }] }) }
			: { ok: false, status: 400, body: 'cannot be deleted because it is referenced by 3 other component(s)' }), () =>
			deleteDependencies('https://contoso.crm.dynamics.com', 'token', [{ kind: 'connectionreference', id: 'cr_one', name: 'one' }]));

		assert.strictEqual(result.failed.length, 0);
		assert.strictEqual(result.retained.length, 1);
		assert.match(result.retained[0].reason, /referenced by 3 component/);
		assert.ok(seen.some(entry => entry.method === 'DELETE' && entry.url.endsWith('/connectionreferences(cr-1)')), seen.map(e => e.url).join('\n'));
	});

	test('cleanup is skipped only on an explicit opt out', () => {
		assert.strictEqual(shouldSkipCleanup('1'), true);
		assert.strictEqual(shouldSkipCleanup('0'), false);
		assert.strictEqual(shouldSkipCleanup(undefined), false);
		assert.strictEqual(shouldSkipCleanup('true'), false);
	});

	test('a prefix too short to be safe is refused at the deletion boundary', () => {
		for (const prefix of [undefined, '', ' ', 'a', 'ab', '  ab  ']) {
			assert.throws(() => assertSafePrefix(prefix), /at least 3 characters/, String(prefix));
		}
	});

	test('a usable prefix is accepted and trimmed', () => {
		assert.strictEqual(assertSafePrefix('  lspe2e '), 'lspe2e');
		assert.strictEqual(assertSafePrefix('abc'), 'abc');
	});

	test('a run-owned prefix is longer than the configured one, so it cannot sweep another run', () => {
		const runOwned = `${'lspe2e'} ${'4f3a'}`;

		assert.ok(matchesPrefix('lspe2e 4f3a classic', runOwned));
		assert.ok(!matchesPrefix('lspe2e 9bc1 classic', runOwned), 'a concurrent run was selected for deletion');
		assert.ok(!matchesPrefix('lspe2e kept-by-no-clean', runOwned));
	});

	test('a reference a surviving component still binds is kept, not reported as a failure', () => {
		const message = 'Dataverse DELETE failed: 400 {"error":{"code":"0x8004f01f","message":"The connectionreference(5d6becdc) component cannot be deleted because it is referenced by 1 other components."}}';

		assert.strictEqual(stillReferencedBy(message), '1');
	});

	test('an ordinary failure is never mistaken for a retained reference', () => {
		assert.strictEqual(stillReferencedBy('Dataverse DELETE failed: 403 forbidden'), undefined);
		assert.strictEqual(stillReferencedBy('Dataverse DELETE failed: 500 server error'), undefined);
	});
});

describe('lspE2E deletion targets the listed environment', () => {
	const RESOLVED_URL = 'https://orgexample.crm.dynamics.com';

	test('the Dataverse instance resolved from the environment is used', () => {
		assert.strictEqual(requireEnvironmentDataverseUrl('env', RESOLVED_URL), RESOLVED_URL);
	});

	test('an environment with no Dataverse instance is refused rather than assumed to match', () => {
		assert.throws(() => requireEnvironmentDataverseUrl('env', undefined), /no Dataverse instance/);
		assert.throws(() => requireEnvironmentDataverseUrl('env', ''), /no Dataverse instance/);
	});
});

describe('lspE2E diff uris', () => {

	test('local uri matches the shape changeTracking builds', () => {
		const uri = localStateUri('abc12_agent.topic.Goodbye', 'file:///c%3A/work/agent');

		assert.strictEqual(uri.scheme, 'mcs');
		assert.strictEqual(uri.authority, 'local');
		assert.strictEqual(uri.path, '/Local Cache: abc12_agent.topic.Goodbye');
		assert.strictEqual(uri.query, 'file:///c%3A/work/agent');
	});

	test('remote uri matches the shape changeTracking builds', () => {
		const uri = remoteStateUri('abc12_agent.topic.Goodbye', 'file:///c%3A/work/agent');

		assert.strictEqual(uri.scheme, 'mcs-remote');
		assert.strictEqual(uri.authority, 'remote');
		assert.strictEqual(uri.path, '/Remote: abc12_agent.topic.Goodbye');
	});

	test('component schema name follows the agent convention', () => {
		assert.strictEqual(componentSchemaName('abc12_agent', 'topic', 'Goodbye'), 'abc12_agent.topic.Goodbye');
	});
});

describe('lspE2E manifest', () => {

	const build = (): RunManifest => {
		const manifest = new RunManifest();
		manifest.declare({ id: 'B-01', name: 'clone', shape: 'classic' });
		manifest.declare({ id: 'B-01', name: 'clone', shape: 'cli' });
		manifest.declare({ id: 'B-02', name: 'push', shape: 'classic' });
		return manifest;
	};

	test('a declared scenario that never ran reports NOT-RUN', () => {
		const manifest = build();
		manifest.record({ id: 'B-01', name: 'clone', shape: 'classic', outcome: 'PASS', milliseconds: 10 });

		assert.strictEqual(manifest.counts()['NOT-RUN'], 2);
		assert.strictEqual(manifest.counts().PASS, 1);
	});

	test('NOT-RUN fails the run', () => {
		assert.strictEqual(build().failed(), true);
	});

	test('blocked scenarios fail the run, because a selected scenario never ran', () => {
		const manifest = build();
		manifest.record({ id: 'B-01', name: 'clone', shape: 'classic', outcome: 'PASS', milliseconds: 1 });
		manifest.record({ id: 'B-01', name: 'clone', shape: 'cli', outcome: 'BLOCKED', milliseconds: 1 });
		manifest.record({ id: 'B-02', name: 'push', shape: 'classic', outcome: 'PASS', milliseconds: 1 });

		assert.strictEqual(manifest.failed(), true);
	});

	test('a run is successful only when every declared scenario passed', () => {
		const manifest = build();
		manifest.record({ id: 'B-01', name: 'clone', shape: 'classic', outcome: 'PASS', milliseconds: 1 });
		manifest.record({ id: 'B-01', name: 'clone', shape: 'cli', outcome: 'PASS', milliseconds: 1 });
		manifest.record({ id: 'B-02', name: 'push', shape: 'classic', outcome: 'PASS', milliseconds: 1 });

		assert.strictEqual(manifest.failed(), false);
	});

	test('a failure fails the run', () => {
		const manifest = build();
		manifest.record({ id: 'B-01', name: 'clone', shape: 'classic', outcome: 'FAIL', milliseconds: 1 });
		manifest.record({ id: 'B-01', name: 'clone', shape: 'cli', outcome: 'PASS', milliseconds: 1 });
		manifest.record({ id: 'B-02', name: 'push', shape: 'classic', outcome: 'PASS', milliseconds: 1 });

		assert.strictEqual(manifest.failed(), true);
	});

	test('every declared scenario is reconciled exactly once so none can vanish from the report', () => {
		const reconciled = build().reconcile();

		assert.strictEqual(reconciled.length, 3);
		assert.deepStrictEqual(
			reconciled.map(result => `${result.id} [${result.shape}]`).sort(),
			['B-01 [classic]', 'B-01 [cli]', 'B-02 [classic]']);
	});
});

describe('lspE2E line editing', () => {

	const write = (content: string): string => {
		const filePath = path.join(scratch(), 'file.yml');
		fs.writeFileSync(filePath, content, 'utf8');
		return filePath;
	};

	test('preserves indentation', () => {
		const filePath = write('a: 1\n      prompt: original\nb: 2\n');
		replaceLine(filePath, 'prompt:', 'prompt: changed');

		assert.match(fs.readFileSync(filePath, 'utf8'), /\n {6}prompt: changed\n/);
	});

	test('leaves every other line alone', () => {
		const filePath = write('a: 1\n      prompt: original\nb: 2\n');
		replaceLine(filePath, 'prompt:', 'prompt: changed');
		const lines = fs.readFileSync(filePath, 'utf8').split('\n');

		assert.strictEqual(lines[0], 'a: 1');
		assert.strictEqual(lines[2], 'b: 2');
	});

	test('preserves crlf line endings', () => {
		const filePath = write('a: 1\r\nprompt: original\r\nb: 2\r\n');
		replaceLine(filePath, 'prompt:', 'prompt: changed');

		assert.match(fs.readFileSync(filePath, 'utf8'), /\r\n/);
	});

	test('returns the original line', () => {
		const filePath = write('      prompt: original\n');

		assert.strictEqual(replaceLine(filePath, 'prompt:', 'prompt: changed').trim(), 'prompt: original');
		assert.strictEqual(readLine(filePath, 'prompt:').trim(), 'prompt: changed');
	});

	test('throws when the key is absent', () => {
		assert.throws(() => replaceLine(write('a: 1\n'), 'prompt:', 'prompt: x'), /no line starting/);
	});
});

describe('lspE2E tree hashing', () => {

	test('an untouched tree reports no differences', () => {
		const root = scratch();
		fs.writeFileSync(path.join(root, 'a.txt'), 'one');

		assert.deepStrictEqual(diffHashes(hashTree(root), hashTree(root)), []);
	});

	test('a changed file is reported', () => {
		const root = scratch();
		fs.writeFileSync(path.join(root, 'a.txt'), 'one');
		const before = hashTree(root);
		fs.writeFileSync(path.join(root, 'a.txt'), 'two');

		assert.deepStrictEqual(diffHashes(before, hashTree(root)), ['changed a.txt']);
	});

	test('added and removed files are reported', () => {
		const root = scratch();
		fs.writeFileSync(path.join(root, 'a.txt'), 'one');
		const before = hashTree(root);
		fs.writeFileSync(path.join(root, 'b.txt'), 'two');
		fs.rmSync(path.join(root, 'a.txt'));

		assert.deepStrictEqual(diffHashes(before, hashTree(root)), ['added b.txt', 'removed a.txt']);
	});

	test('the .mcs directory is skipped', () => {
		const root = scratch();
		fs.mkdirSync(path.join(root, '.mcs'));
		fs.writeFileSync(path.join(root, '.mcs', 'conn.json'), '{}');
		fs.writeFileSync(path.join(root, 'a.txt'), 'one');

		assert.deepStrictEqual(fileSet(root), ['a.txt']);
	});
});

describe('lspE2E shapes', () => {

	test('every shape runs clone, push and pull', () => {
		for (const shape of SHAPES) {
			assert.ok(runsScenario(shape, 'B-01'), shape.id);
			assert.ok(runsScenario(shape, 'B-02'), shape.id);
			assert.ok(runsScenario(shape, 'B-04'), shape.id);
		}
	});

	test('only the classic and cli shapes are in the suite', () => {
		assert.deepStrictEqual(SHAPES.map(shape => shape.id), ['classic', 'cli']);
	});

	test('every diff target is a .mcs.yml the change view can represent', () => {
		for (const shape of SHAPES) {
			assert.ok(shape.diffRelativePath.endsWith('.mcs.yml'), `${shape.id}: ${shape.diffRelativePath}`);
			assert.ok(runsScenario(shape, 'B-06'), shape.id);
		}
	});

	test('the cli diff target is the component, not the markdown body it syncs', () => {
		const cli = SHAPES.find(shape => shape.id === 'cli')!;

		assert.ok(cli.targetRelativePath.endsWith('.md'));
		assert.strictEqual(cli.diffRelativePath, 'behaviors/skill-1/skill.mcs.yml');
		assert.notStrictEqual(cli.diffRelativePath, cli.targetRelativePath);
	});

	test('a keyed line variant keeps its key', () => {
		const classic = SHAPES.find(shape => shape.id === 'classic')!;

		assert.strictEqual(variantLine(classic, 'Local 1'), 'prompt: Local 1 - Would you like to end our conversation?');
	});

	test('an unkeyed line variant prefixes the whole line', () => {
		const cli = SHAPES.find(shape => shape.id === 'cli')!;

		assert.strictEqual(variantLine(cli, 'Local 1'), 'Local 1 - When this skill is activated:');
	});

	test('the fixture line each shape targets really exists', () => {
		for (const shape of SHAPES) {
			const identity = shape.identitySubdirectory ? path.join(fixtures, shape.fixtureDirectory, shape.identitySubdirectory) : path.join(fixtures, shape.fixtureDirectory);
			const target = path.join(identity, ...shape.targetRelativePath.split('/'));

			assert.ok(fs.existsSync(target), `${shape.id}: ${target} is missing`);
			assert.ok(
				fs.readFileSync(target, 'utf8').split('\n').some(line => line.trimStart().startsWith(shape.targetKey)),
				`${shape.id}: no line starting '${shape.targetKey}' in ${shape.targetRelativePath}`);
		}
	});

	test('no fixture carries an agent icon', () => {
		for (const shape of SHAPES) {
			const identity = shape.identitySubdirectory ? path.join(fixtures, shape.fixtureDirectory, shape.identitySubdirectory) : path.join(fixtures, shape.fixtureDirectory);

			assert.ok(!fs.existsSync(path.join(identity, 'icon.png')), `${shape.id} still carries icon.png`);
		}
	});
});

describe('lsp e2e scenario groups', () => {
	test('a full run keeps every scenario of a group together instead of interleaving them', () => {
		const ordered = orderScenarioFiles([
			'/s/groupA.test.js', '/s/groupB.test.js', '/s/groupC.test.js',
			'/s/groupD.test.js', '/s/groupE.test.js', '/s/groupF.test.js', '/s/groupW.test.js',
		]).map(file => file.replace('/s/group', '').replace('.test.js', ''));

		assert.deepStrictEqual(ordered, ['A', 'B', 'D', 'E', 'F', 'W', 'C']);
	});

	test('the file order follows the catalogue, so a scenario moving group moves with it', () => {
		const rank = (letter: string): number => {
			const entry = CATALOGUE.find(candidate => candidate.id.startsWith(`${letter}-`));
			return entry ? ALL_GROUPS.indexOf(entry.group) : -1;
		};

		assert.strictEqual(rank('C'), ALL_GROUPS.indexOf('interop'));
		assert.ok(ALL_GROUPS.indexOf('vscode') < rank('C'), 'vscode must come before interop for the grouping to hold');
	});

	test('an empty or absent selection means every group', () => {
		assert.deepStrictEqual(parseGroups(undefined), ALL_GROUPS);
		assert.deepStrictEqual(parseGroups(''), ALL_GROUPS);
		assert.deepStrictEqual(parseGroups('all'), ALL_GROUPS);
	});

	test('a single group selects only itself', () => {
		assert.deepStrictEqual(parseGroups('vscode'), ['vscode']);
		assert.deepStrictEqual(parseGroups('interop'), ['interop']);
	});

	test('selection is case and whitespace tolerant and order is stable', () => {
		assert.deepStrictEqual(parseGroups('  INTEROP , vscode '), ['vscode', 'interop']);
	});

	test('an unknown group is rejected rather than silently ignored', () => {
		assert.throws(() => parseGroups('bogus'), /unknown group 'bogus'/);
		assert.throws(() => parseGroups('vscode,bogus'), /unknown group 'bogus'/);
	});

	test('every catalogue id is unique', () => {
		const ids = CATALOGUE.map(entry => entry.id);

		assert.strictEqual(new Set(ids).size, ids.length);
	});

	test('every catalogue entry belongs to a known group', () => {
		for (const entry of CATALOGUE) {
			assert.ok(ALL_GROUPS.includes(entry.group), `${entry.id} has group '${entry.group}'`);
		}
	});

	test('every scenario a shape claims exists in the catalogue', () => {
		for (const shape of ALL_SHAPES) {
			for (const id of shape.scenarios) {
				assert.ok(catalogueEntry(id), `${shape.id} claims unknown scenario ${id}`);
			}
		}
	});

	test('every group is populated so no selection runs nothing', () => {
		for (const group of ALL_GROUPS) {
			const members = CATALOGUE.filter(entry => entry.group === group);

			assert.ok(members.length > 0, `group ${group} has no scenarios`);
			assert.ok(ALL_SHAPES.some(shape => members.some(entry => shape.scenarios.includes(entry.id))), `no shape runs any ${group} scenario`);
		}
	});

	test('selection needs both the group and the shape to opt in', () => {
		assert.ok(isSelected('B-01', ['B-01'], ['vscode']));
		assert.ok(!isSelected('B-01', ['B-01'], ['interop']));
		assert.ok(!isSelected('B-01', ['B-02'], ['vscode']));
		assert.ok(!isSelected('does-not-exist', ['does-not-exist'], ALL_GROUPS));
	});

	test('selecting one group excludes the other group entirely', () => {
		const interopIds = CATALOGUE.filter(entry => entry.group === 'interop').map(entry => entry.id);

		for (const id of interopIds) {
			assert.ok(!isSelected(id, interopIds, ['vscode']), `${id} leaked into the vscode selection`);
		}
	});

	test('the workflow shape runs the workflow group and nothing an agent shape runs', () => {
		const workflow = WORKFLOW_SHAPES[0];

		for (const id of workflow.scenarios) {
			assert.strictEqual(catalogueEntry(id)!.group, 'workflow', `${id} is claimed by the workflow shape but is not a workflow scenario`);
		}

		for (const shape of SHAPES) {
			assert.deepStrictEqual(
				shape.scenarios.filter(id => workflow.scenarios.includes(id)),
				[],
				`${shape.id} claims a workflow scenario, but it holds an agent rather than a standalone workflow`);
		}
	});
});

describe('lsp e2e standalone workflow fixture', () => {
	const workflow = WORKFLOW_SHAPES[0];
	const workflowFixture = path.join(fixtures, workflow.fixtureDirectory);

	test('the fixture declares a standalone workflow project and no agent project', () => {
		assert.ok(fs.existsSync(path.join(workflowFixture, workflow.markerFile)), `${workflow.markerFile} is missing, so the workspace would not be a workflow project`);

		for (const agentFile of ['settings.mcs.yml', 'settings.mcs.yaml', 'agent.mcs.yml', 'agent.mcs.yaml', 'collection.mcs.yml', 'collection.mcs.yaml']) {
			assert.ok(!fs.existsSync(path.join(workflowFixture, agentFile)), `the fixture carries ${agentFile}, which makes it an agent project instead`);
		}
	});

	test('the fixture is a draft, so cleanup can delete the flow it creates', () => {
		assert.strictEqual(readScalar(workflowIdentity(workflowFixture).metadataPath, 'stateCode'), '0');
	});

	test('every connection reference the workflow needs is declared', () => {
		assert.deepStrictEqual(
			requiredConnectionReferences(workflowFixture).filter(name => !declaredConnectionReferences(workflowFixture).includes(name)),
			[]);
	});

	test('the folder name is derived the way the product derives it, so a pull relocates nothing', () => {
		const identity = workflowIdentity(workflowFixture);

		assert.strictEqual(path.basename(identity.folder), `${portableFolderName(identity.name)}-${identity.workflowId}`);
	});

	test('a portable folder name drops what a folder may not carry on some platform', () => {
		assert.strictEqual(portableFolderName('S5D1 WF 1'), 'S5D1WF1');
		assert.strictEqual(portableFolderName('a/b\\c:d*e?f"g<h>i|j'), 'abcdefghij');
		assert.strictEqual(portableFolderName('trailing dots... '), 'trailingdots');
	});

	test('renaming gives the fixture a run-owned identity and leaves nothing of the captured one', () => {
		const root = scratch();
		copyDirectory(workflowFixture, root);

		const captured = workflowIdentity(root);
		const workflowId = '3f1d6b2a-0c4e-4a7b-9d52-8e1f0a6c7b34';
		const outcome = renameWorkflowWorkspace(root, 'lspe2e 4f3a workflow', workflowId);
		const renamed = workflowIdentity(root);

		assert.strictEqual(outcome.originalIdentity, captured.workflowId);
		assert.strictEqual(renamed.workflowId, workflowId);
		assert.strictEqual(renamed.name, 'lspe2e 4f3a workflow');
		assert.strictEqual(path.basename(renamed.folder), `lspe2e4f3aworkflow-${workflowId}`);
		assert.strictEqual(readScalar(renamed.metadataPath, 'jsonFileName'), `workflows/lspe2e4f3aworkflow-${workflowId}/workflow.json`);

		for (const file of listFiles(root, [])) {
			const relative = path.relative(root, file);

			assert.ok(!relative.includes(captured.workflowId) && !relative.includes(portableFolderName(captured.name)), `${relative} still carries the captured identity`);
			assert.ok(!fs.readFileSync(file, 'utf8').includes(captured.workflowId), `${relative} still names workflow ${captured.workflowId}`);
			assert.ok(!fs.readFileSync(file, 'utf8').includes(captured.name), `${relative} still names '${captured.name}'`);
		}
	});

	test('the description round trips, including the empty one the fixture ships', () => {
		const root = scratch();
		copyDirectory(workflowFixture, root);

		assert.strictEqual(workflowDescription(root), '');

		setWorkflowDescription(root, 'pushed by the end to end suite, run 4f3a');

		assert.strictEqual(workflowDescription(root), 'pushed by the end to end suite, run 4f3a');
		assert.strictEqual(readScalar(workflowIdentity(root).metadataPath, 'workflowId'), '1eeeda41-e55b-ecb9-826d-be8d29bcd4bd');
	});

	test('a workspace that does not hold exactly one workflow is refused rather than guessed at', () => {
		const empty = scratch();

		assert.throws(() => workflowIdentity(empty), /expected exactly one workflow/);

		const two = scratch();
		copyDirectory(workflowFixture, two);
		copyDirectory(workflowIdentity(two).folder, path.join(two, 'workflows', 'Second-7c2d9b81-4a35-4e60-8f12-3b5a6c7d8e90'));

		assert.throws(() => workflowIdentity(two), /expected exactly one workflow/);
	});

	test('a standalone flow is deleted as a workflow row of its own, since no agent owns it', async () => {
		const seen: { url: string; method: string }[] = [];
		const result = await withStubbedFetch(seen, () => ({ ok: true, status: 204, body: '' }), () =>
			deleteDependencies('https://contoso.crm.dynamics.com', 'token', [{ kind: 'workflow', id: 'wf-1', name: 'lspe2e 4f3a workflow' }]));

		assert.deepStrictEqual(result.deleted, ['workflow lspe2e 4f3a workflow']);
		assert.deepStrictEqual(seen, [{ url: 'https://contoso.crm.dynamics.com/api/data/v9.2/workflows(wf-1)', method: 'DELETE' }]);
	});

	test('a flow another run already deleted is not reported as a failure', async () => {
		const result = await withStubbedFetch([], () => ({ ok: false, status: 404, body: 'Does Not Exist' }), () =>
			deleteDependencies('https://contoso.crm.dynamics.com', 'token', [{ kind: 'workflow', id: 'wf-1', name: 'lspe2e 4f3a workflow' }]));

		assert.deepStrictEqual(result, { deleted: [], retained: [], failed: [] });
	});

	test('only flows the prefix owns are swept', async () => {
		const seen: { url: string; method: string }[] = [];
		const rows = { value: [{ workflowid: 'wf-1', name: 'lspe2e 4f3a workflow' }, { workflowid: 'wf-2', name: 'Nightly reminder' }] };
		const found = await withStubbedFetch(seen, () => ({ ok: true, status: 200, body: JSON.stringify(rows) }), () =>
			findWorkflowArtifacts('https://contoso.crm.dynamics.com', 'token', 'lspe2e 4f3a'));

		assert.deepStrictEqual(found, [{ kind: 'workflow', id: 'wf-1', name: 'lspe2e 4f3a workflow' }]);
		assert.ok(seen[0].url.includes(encodeURIComponent("startswith(name,'lspe2e 4f3a')")), seen[0].url);
	});
});
describe('lsp e2e new component sources', () => {
	const cli = SHAPES.find(shape => shape.id === 'cli')!;
	const classic = SHAPES.find(shape => shape.id === 'classic')!;


	test('a new component source exists only where a shape copies files from one', () => {
		for (const shape of SHAPES) {
			const source = newComponentSourceRoot(fixtures, shape);
			const copied = listFilesIfPresent(source);

			if (shape.bareSkillRelativeDir) {
				assert.ok(copied.length > 0, `${shape.id} declares a bare skill but its source is empty`);
			}
		}
	});

	test('no new component source carries a prompt or a workflow', () => {
		for (const shape of SHAPES) {
			const source = newComponentSourceRoot(fixtures, shape);

			for (const folder of ['prompts', 'workflows']) {
				assert.deepStrictEqual(
					listFilesIfPresent(path.join(source, folder)),
					[],
					`${shape.id} adds a ${folder} entry; pull deletes local-only ${folder} folders before they are pushed, so B-07 would fail on a known product defect rather than on its own subject`);
			}
		}
	});

	test('the cli fixture keeps exactly the three skills that stay behind', () => {
		const behaviors = path.join(fixtures, cli.fixtureDirectory, 'behaviors');
		const skills = fs.readdirSync(behaviors, { withFileTypes: true }).filter(entry => entry.isDirectory()).map(entry => entry.name).sort();

		assert.deepStrictEqual(skills, ['get-us-weather', 'my-skill-5', 'skill-1']);
	});

	test('the skill moved out is bare so the product must generate its metadata', () => {
		const skill = path.join(newComponentSourceRoot(fixtures, cli), ...cli.bareSkillRelativeDir.split('/'));
		const files = listFilesIfPresent(skill).map(file => path.basename(file));

		assert.ok(files.includes('SKILL.md'), 'the bare skill lost its SKILL.md');
		assert.ok(files.length > 1, 'the bare skill has no supporting content');
		assert.deepStrictEqual(files.filter(name => name.endsWith('.mcs.yml')), [], 'the bare skill still carries generated metadata');
	});

	test('a shape that declares no bare skill has none to copy', () => {
		assert.strictEqual(classic.bareSkillRelativeDir, '');
	});


	test('every fixture still exercises a workflow after the new component split', () => {
		for (const shape of SHAPES) {
			const workflows = listFilesIfPresent(path.join(fixtures, shape.fixtureDirectory, 'workflows')).filter(file => path.basename(file) === 'metadata.yml');

			assert.ok(workflows.length > 0, `${shape.id} fixture has no workflow, so the base scenarios never sync one`);
		}
	});

	test('a fixture component is never the one the new component source adds', () => {
		for (const shape of SHAPES) {
			for (const folder of ['workflows', 'prompts']) {
				const idsIn = (root: string): string[] => listFilesIfPresent(path.join(root, folder))
					.filter(file => path.basename(file) === 'metadata.yml')
					.map(file => path.basename(path.dirname(file)));

				const inFixture = idsIn(path.join(fixtures, shape.fixtureDirectory));
				const inSource = idsIn(newComponentSourceRoot(fixtures, shape));

				assert.deepStrictEqual(inFixture.filter(id => inSource.includes(id)), [], `${shape.id} adds a ${folder} entry it already has, so B-07 would prove nothing`);
			}
		}
	});

	test('the classic fixture carries a prompt the main agent owns, not only a sub-agent one', () => {
		const classicShape = SHAPES.find(shape => shape.supportsTopics)!;
		const root = path.join(fixtures, classicShape.fixtureDirectory);
		const mainPromptIds = listFilesIfPresent(path.join(root, 'prompts'))
			.filter(file => path.basename(file) === 'metadata.yml')
			.map(file => readScalar(file, 'aIModelId'));

		const invokedByMain = listFilesIfPresent(path.join(root, 'actions'))
			.flatMap(file => [...fs.readFileSync(file, 'utf8').matchAll(/aIModelId:\s*([0-9a-f-]{36})/gi)].map(match => match[1]));

		assert.ok(invokedByMain.length > 0, 'the classic fixture main agent invokes no prompt, so base runs never exercise one');
		assert.ok(invokedByMain.every(id => mainPromptIds.includes(id)), 'the main agent invokes a prompt that is not in prompts/');
	});

	test('every connection reference a workflow needs is declared, in the fixture and after adding new components', () => {
		for (const shape of SHAPES) {
			const fixtureRoot = fixtureRootOf(shape);
			const missingInFixture = requiredConnectionReferences(fixtureRoot).filter(name => !declaredConnectionReferences(fixtureRoot).includes(name));

			assert.deepStrictEqual(missingInFixture, [], `${shape.id} fixture has a workflow whose connection reference is not declared`);

			const { root: staged } = stageAndAdd(shape, 'CR');
			const missing = requiredConnectionReferences(staged).filter(name => !declaredConnectionReferences(staged).includes(name));

			assert.deepStrictEqual(missing, [], `${shape.id} adds a workflow whose connection reference nothing declares`);
		}
	});

	test('no fixture references an agent it does not belong to', () => {
		for (const shape of SHAPES) {
			const own = readSchemaName(path.join(fixtures, shape.fixtureDirectory));

			for (const root of [path.join(fixtures, shape.fixtureDirectory), newComponentSourceRoot(fixtures, shape)]) {
				for (const file of listFilesIfPresent(root)) {
					if (path.extname(file) === '.png') {
						continue;
					}

					const foreign = /(?:^|[\s:"'\/-])((?:cre98|abc12|ahe2e)_[A-Za-z0-9]+)/g;
					const content = fs.readFileSync(file, 'utf8');
					let match: RegExpExecArray | null;

					while ((match = foreign.exec(content)) !== null) {
						assert.ok(match[1] === own || !match[1].startsWith('ahe2e_'), `${file} references the foreign agent ${match[1]}; the renamer will not rewrite it`);
					}
				}
			}
		}
	});

	test('the deletable component named by each shape really is in the fixture', () => {
		for (const shape of SHAPES) {
			const target = path.join(fixtures, shape.fixtureDirectory, ...shape.deletableRelativePath.split('/'));

			assert.ok(fs.existsSync(target), `${shape.id} cannot delete ${shape.deletableRelativePath}: it is not in the fixture`);
		}
	});

	test('the deletable component schema matches its folder, so B-08 tests the ordinary case', () => {
		for (const shape of SHAPES) {
			const target = path.join(fixtures, shape.fixtureDirectory, ...shape.deletableRelativePath.split('/'));
			const metadata = fs.statSync(target).isDirectory() ? path.join(target, 'skill.mcs.yml') : target;

			if (!fs.existsSync(metadata)) {
				continue;
			}

			const schemaName = readScalar(metadata, 'schemaName');

			if (!schemaName) {
				continue;
			}

			const leaf = schemaName.split('.').pop()!;
			const folder = path.basename(shape.deletableRelativePath).replace(/\.mcs\.yml$/, '');

			assert.strictEqual(leaf, folder, `${shape.id} deletes '${folder}' whose schema leaf is '${leaf}'; a mismatch is a suspected product defect and would make B-08 fail for a reason other than its subject`);
		}
	});
	test('the deletable component is not the one the sync scenarios edit', () => {
		for (const shape of SHAPES) {
			assert.notStrictEqual(shape.deletableRelativePath, shape.targetRelativePath, `${shape.id} would delete the file B-02 edits`);
			assert.ok(!shape.diffRelativePath.startsWith(shape.deletableRelativePath), `${shape.id} would delete the file B-06 diffs`);
		}
	});

	test('the knowledge directory each shape names exists in its fixture', () => {
		for (const shape of SHAPES) {
			const directory = path.join(fixtures, shape.fixtureDirectory, ...shape.knowledgeRelativeDir.split('/'));

			assert.ok(fs.existsSync(directory), `${shape.id} knowledge directory ${shape.knowledgeRelativeDir} is missing`);
		}
	});
});
describe('lsp e2e component authoring', () => {
	test('new components land on disk at the paths they report', () => {
		for (const shape of SHAPES) {
			const root = stage(shape);

			for (const fact of addNewComponents(root, shape, fixtures, 'cr000_newAgent', 'S1')) {
				assert.ok(fs.existsSync(path.join(root, ...fact.relativePath.split('/'))), `${shape.id} reported ${fact.relativePath} but nothing is there`);
			}
		}
	});

	test('every shape contributes knowledge, and each contributes what its shape supports', () => {
		for (const shape of SHAPES) {
			const kinds = addNewComponents(stage(shape), shape, fixtures, 'cr000_newAgent', 'S2').map(fact => fact.kind);

			assert.ok(kinds.includes('knowledge'), `${shape.id} added no knowledge file`);
			assert.strictEqual(kinds.includes('skill'), shape.bareSkillRelativeDir !== '');
			assert.strictEqual(kinds.includes('topic'), shape.supportsTopics);
			assert.deepStrictEqual(kinds.filter(kind => kind === 'prompt' || kind === 'workflow'), [], `${shape.id} still adds a prompt or workflow`);
		}
	});

	test('the suffix makes added names unique so parallel runs cannot collide', () => {
		const shape = SHAPES[0];
		const a = addNewComponents(stage(shape), shape, fixtures, 'cr000_newAgent', 'AAA').map(fact => fact.relativePath);
		const b = addNewComponents(stage(shape), shape, fixtures, 'cr000_newAgent', 'BBB').map(fact => fact.relativePath);
		const shared = a.filter(entry => b.includes(entry) && entry.includes('TestFile2'));

		assert.deepStrictEqual(shared, [], 'knowledge file names repeat across runs');
	});

	test('the source agent schema is rewritten so copied components bind to the clone', () => {
		for (const shape of SHAPES) {
			const root = stage(shape);
			const original = fixtureSchemaName(shape);

			addNewComponents(root, shape, fixtures, 'cr000_rewritten', 'S3');

			const source = newComponentSourceRoot(fixtures, shape);
			const copied = fs.existsSync(source)
				? fs.readdirSync(source, { withFileTypes: true }).filter(entry => entry.isDirectory()).map(entry => path.join(root, entry.name))
				: [];

			for (const directory of copied) {
				for (const file of listFilesIfPresent(directory)) {
					if (path.extname(file) === '.png') {
						continue;
					}

					assert.ok(!fs.readFileSync(file, 'utf8').includes(original), `${file} still references the fixture agent ${original}`);
				}
			}
		}
	});

	test('generated metadata is reported as missing until the product writes it', () => {
		const shape = SHAPES.find(candidate => candidate.bareSkillRelativeDir !== '')!;
		const root = stage(shape);
		const facts = addNewComponents(root, shape, fixtures, 'cr000_newAgent', 'S4');
		const skill = facts.find(fact => fact.kind === 'skill')!;
		const knowledge = facts.find(fact => fact.kind === 'knowledge')!;

		assert.ok(!metadataMaterialised(root, skill), 'the bare skill already has metadata');
		assert.ok(!metadataMaterialised(root, knowledge), 'the new knowledge file already has metadata');

		fs.writeFileSync(path.join(root, ...skill.relativePath.split('/'), 'skill.mcs.yml'), 'kind: Skill\n', 'utf8');
		fs.writeFileSync(path.join(path.dirname(path.join(root, ...knowledge.relativePath.split('/'))), knowledge.expectMaterialisedMetadata!), 'kind: Knowledge\n', 'utf8');

		assert.ok(metadataMaterialised(root, skill), 'skill metadata was written but not detected');
		assert.ok(metadataMaterialised(root, knowledge), 'knowledge metadata was written but not detected');
	});

	test('a component with no expected metadata is never reported as missing', () => {
		assert.ok(metadataMaterialised(os.tmpdir(), { kind: 'topic', relativePath: 'topics/Nothing.mcs.yml', cloudName: 'Nothing' }));
	});

	test('deleting a component removes it and reports what went', () => {
		for (const shape of SHAPES) {
			const root = stage(shape);
			const removed = removeComponent(root, shape.deletableRelativePath, shape.deletableKind, shape.deletableCloudName);

			assert.strictEqual(removed.cloudName, shape.deletableCloudName);
			assert.ok(!fs.existsSync(path.join(root, ...shape.deletableRelativePath.split('/'))), `${shape.id} left ${shape.deletableRelativePath} behind`);
		}
	});

	test('deleting something that is not there fails loudly instead of passing quietly', () => {
		assert.throws(() => removeComponent(stage(SHAPES[0]), 'topics/NeverExisted.mcs.yml', 'topic', 'Never Existed'), /cannot delete/);
	});
});
describe('lsp e2e push outcome', () => {
	test('a completed apply is not blocked', () => {
		assert.deepStrictEqual(classifyPush([`x ${APPLY_DONE_TEXT} y`]), { outcome: 'applied' });
	});

	test('an error gate blocks the push and says why', () => {
		const outcome = classifyPush([`found 2 ${ERROR_GATE_TEXT} 1 file(s)`]);

		assert.strictEqual(outcome.outcome, 'refused');
		assert.match(outcome.reason!, /found 2/);
	});

	test('a remote block and a cloud drift both count as refusals', () => {
		assert.strictEqual(classifyPush([`a ${REMOTE_BLOCK_TEXT} b`]).outcome, 'refused');
		assert.strictEqual(classifyPush([`a ${CLOUD_DRIFT_TEXT} b`]).outcome, 'refused');
	});

	test('an unreadable workspace file counts as a refusal, which is how a conflicted non-yaml file is rejected', () => {
		const singular = classifyPush(['Failed to execute Apply operation: 1 workspace file could not be read:\n  behaviors/skill-1/SKILL.md(1,1): Merge conflict markers.']);

		assert.strictEqual(singular.outcome, 'refused');
		assert.match(singular.reason!, /could not be read/);
		assert.strictEqual(classifyPush(['Error applying changes: 2 workspace files could not be read:']).outcome, 'refused');
	});

	test('the reattach quick pick rules answer the environment picker, which is the picker a reattach opens first', () => {
		const rules = reattachQuickPickRules('ABC-123');

		assert.ok(rules.some(rule => rule.match({ label: 'Hoa Dev', environment: { environmentId: 'abc-123' } } as never)), 'no rule answers the environment picker, so a reattach would hang on it');
		assert.ok(rules.some(rule => rule.match({ label: 'agent', agentData: {} } as never)), 'no rule answers the agent picker');
	});

	test('a retarget that refreshes in place is recognised as a completion, not silence', () => {
		const refreshed = 'Retarget agent completed by refreshing the agent from its current environment.';

		assert.ok(REATTACH_ATTACHED_SIGNALS.some(signal => refreshed.includes(signal)));
		assert.ok(!REATTACH_DONE_SIGNALS.some(signal => refreshed.includes(signal)), 'the first-attach signals must not be expected to cover the refresh-in-place path');
	});

	test('silence is inconclusive rather than a refusal, so it cannot pass as one', () => {
		const outcome = classifyPush([]);

		assert.strictEqual(outcome.outcome, 'inconclusive');
		assert.match(outcome.reason!, /neither completion nor refusal/);
	});

	test('an unrelated failure is inconclusive, not evidence that a conflict was caught', () => {
		assert.strictEqual(classifyPush(['Failed to execute Apply operation']).outcome, 'inconclusive');
	});

	test('a completion wins even when a refusal was seen earlier in the run', () => {
		assert.strictEqual(classifyPush([`a ${REMOTE_BLOCK_TEXT} b`, `c ${APPLY_DONE_TEXT} d`]).outcome, 'applied');
	});

	test('a reported failure is surfaced so a silent command cannot look successful', () => {
		assert.match(reportedFailure(['Failed to execute Preview operation'])!, /Failed to execute/);
		assert.match(reportedFailure(['Cannot perform apply operation: found 1 error(s) in 1 file(s).'])!, /Cannot perform/);
		assert.strictEqual(reportedFailure([`x ${APPLY_DONE_TEXT} y`]), undefined);
		assert.strictEqual(reportedFailure([]), undefined);
	});

	test('a failure the sync code showed once is surfaced although the command does not repeat it', () => {
		for (const shown of ['Error previewing changes: The service is unavailable.', 'Error getting changes: refused', 'Error applying changes: refused']) {
			assert.strictEqual(reportedFailure([shown]), shown);
		}
	});
});

describe('lsp e2e cloud component matching', () => {
	test('a component is found by its exact name', () => {
		assert.ok(labelsCarry(['Thank you', 'cr123_thankyou'], 'Thank you'));
	});

	test('matching ignores punctuation and case the platform rewrites', () => {
		assert.ok(labelsCarry(['cr123_thankyou'], 'Thank you'));
		assert.ok(labelsCarry(['get-us-temperature'], 'get_us_temperature'));
		assert.ok(labelsCarry(['TestFile2ABC.txt'], 'testfile2abc.txt'));
	});

	test('an absent component is not matched', () => {
		assert.ok(!labelsCarry(['Goodbye', 'cr123_goodbye'], 'Thank you'));
	});

	test('an empty expectation never matches so a bug cannot pass silently', () => {
		assert.ok(!labelsCarry(['anything'], ''));
		assert.ok(!labelsCarry([], 'Thank you'));
	});

	test('a component is matched by its schema leaf, not by any label that contains it', () => {
		assert.ok(labelsCarry(['abc12_agent.topic.NewTopicABC'], 'NewTopic ABC'));
		assert.ok(!labelsCarry(['abc12_agent.topic.NewTopicABCExtra'], 'NewTopic ABC'));
	});

	test('a bundle generated beside a component does not stand in for it', () => {
		const produced = ['get-us-temperature.SKILL.md', 'getustemperature6fd9fd40zip', 'get-us-temperature'];

		assert.ok(labelsCarry(produced, 'get-us-temperature'));
		assert.ok(!labelsCarry(produced.slice(0, 2), 'get-us-temperature'));
	});
});
describe('lsp e2e external tool commands', () => {
	const request = { agentId: 'agent-1', environmentId: 'env-1', projectDirectory: 'C:\\work\\p', displayName: 'Agent One' };
	const project = { agentId: 'agent-1', projectDirectory: 'C:\\work\\p' };

	test('every command names the project directory so nothing runs against the cwd', () => {
		for (const args of [cloneArgs('pac', request), cloneArgs('msagent', request), pullArgs('pac', project), pullArgs('msagent', project), pushArgs('pac', project), pushArgs('msagent', project)]) {
			assert.ok(args.includes(request.projectDirectory), `a command did not pin the project directory: ${args.join(' ')}`);
		}
	});

	test('both clones pin the environment so no run can touch another one', () => {
		assert.ok(cloneArgs('pac', request).includes('--environment') && cloneArgs('pac', request).includes('env-1'));
		assert.ok(cloneArgs('msagent', request).includes('--environment-id') && cloneArgs('msagent', request).includes('env-1'));
	});

	test('both clones identify the source agent by id', () => {
		assert.ok(cloneArgs('pac', request).includes('--bot'));
		assert.ok(cloneArgs('msagent', request).includes('--mcs-clone-agent-id'));

		for (const tool of ['pac', 'msagent'] as const) {
			assert.ok(cloneArgs(tool, request).includes('agent-1'), `${tool} clone lost the agent id`);
		}
	});

	test('msagent syncs the project record it adopted, not a cloud id it was handed', () => {
		for (const args of [pullArgs('msagent', project), pushArgs('msagent', project)]) {
			assert.ok(!args.includes('--agent-id'), `naming a cloud agent id conflicts with the project's own record: ${args.join(' ')}`);
		}
	});

	test('pac still identifies the project only by directory', () => {
		for (const args of [pullArgs('pac', project), pushArgs('pac', project)]) {
			assert.ok(!args.includes('--agent-id'));
			assert.ok(args.includes('--project-dir'));
		}
	});
	test('msagent commands never wait for a browser prompt', () => {
		for (const args of [cloneArgs('msagent', request), pullArgs('msagent', project), pushArgs('msagent', project)]) {
			assert.ok(args.includes('--non-interactive'), `a msagent command could block on sign-in: ${args.join(' ')}`);
		}
	});

	test('pac uses its copilot verbs and msagent its agent verbs', () => {
		assert.strictEqual(cloneArgs('pac', request)[0], 'copilot');
		assert.strictEqual(pullArgs('pac', project)[1], 'pull');
		assert.strictEqual(pushArgs('pac', project)[1], 'push');
		assert.strictEqual(cloneArgs('msagent', request)[0], 'agent');
		assert.strictEqual(pullArgs('msagent', project)[1], 'pull');
		assert.strictEqual(pushArgs('msagent', project)[1], 'deploy');
	});

	test('a msagent deploy accepts drift so an interop push is not refused for being second', () => {
		assert.ok(pushArgs('msagent', project).includes('--overwrite'));
	});

	const connectionPair = (target: unknown, reference: unknown): { targetRoot: string; referenceRoot: string } => {
		const targetRoot = scratch();
		const referenceRoot = scratch();

		for (const [root, body] of [[targetRoot, target], [referenceRoot, reference]] as const) {
			fs.mkdirSync(path.join(root, '.mcs'), { recursive: true });
			fs.writeFileSync(path.join(root, CONNECTION_RELATIVE_PATH), JSON.stringify(body), 'utf8');
		}

		return { targetRoot, referenceRoot };
	};

	test('a null endpoint the external tool left behind is filled from the reference workspace', () => {
		const { targetRoot, referenceRoot } = connectionPair(
			{ AgentId: 'a', AgentManagementEndpoint: null },
			{ AgentId: 'a', AgentManagementEndpoint: 'https://endpoint', AuthoringShape: 1 });

		const repaired = repairExternalConnection(targetRoot, referenceRoot);
		const result = JSON.parse(fs.readFileSync(path.join(targetRoot, CONNECTION_RELATIVE_PATH), 'utf8'));

		assert.ok(repaired.includes('AgentManagementEndpoint'));
		assert.ok(repaired.includes('AuthoringShape'));
		assert.strictEqual(result.AgentManagementEndpoint, 'https://endpoint');
	});

	test('a value the external tool did set is never overwritten', () => {
		const { targetRoot, referenceRoot } = connectionPair(
			{ AgentId: 'theirs', AgentManagementEndpoint: 'https://theirs' },
			{ AgentId: 'ours', AgentManagementEndpoint: 'https://ours' });

		assert.deepStrictEqual(repairExternalConnection(targetRoot, referenceRoot), []);

		const result = JSON.parse(fs.readFileSync(path.join(targetRoot, CONNECTION_RELATIVE_PATH), 'utf8'));

		assert.strictEqual(result.AgentId, 'theirs');
		assert.strictEqual(result.AgentManagementEndpoint, 'https://theirs');
	});

	test('a null nested field is repaired without discarding its populated siblings', () => {
		const { targetRoot, referenceRoot } = connectionPair(
			{ AccountInfo: { AccountId: 'kept', clusterCategory: null } },
			{ AccountInfo: { AccountId: 'other', clusterCategory: 4 } });

		assert.deepStrictEqual(repairExternalConnection(targetRoot, referenceRoot), ['AccountInfo.clusterCategory']);

		const result = JSON.parse(fs.readFileSync(path.join(targetRoot, CONNECTION_RELATIVE_PATH), 'utf8'));

		assert.strictEqual(result.AccountInfo.AccountId, 'kept');
		assert.strictEqual(result.AccountInfo.clusterCategory, 4);
	});

	test('a missing connection file on either side is reported as nothing to repair', () => {
		const empty = scratch();

		assert.deepStrictEqual(repairExternalConnection(empty, empty), []);
	});

	test('an argument containing spaces is quoted so the tool sees one token', () => {
		assert.strictEqual(quoteArgument('lspe2e 4d8d classic'), '"lspe2e 4d8d classic"');
		assert.strictEqual(quoteArgument('C:\\work\\a b\\c'), '"C:\\work\\a b\\c"');
	});

	test('an argument with no shell-significant character is left alone', () => {
		assert.strictEqual(quoteArgument('--bot'), '--bot');
		assert.strictEqual(quoteArgument('7d3f8bf4-d8c1-f111'), '7d3f8bf4-d8c1-f111');
	});

	test('every clone argument survives quoting as a single token', () => {
		const args = cloneArgs('pac', { agentId: 'a', environmentId: 'e', projectDirectory: 'C:\\a b', displayName: 'My Agent Name' });

		assert.ok(args.map(quoteArgument).includes('"My Agent Name"'), 'a display name with spaces would split into separate arguments');
		assert.ok(args.map(quoteArgument).includes('"C:\\a b"'), 'a path with spaces would split into separate arguments');
	});
});
describe('lsp e2e run timing', () => {
	test('short durations read in seconds and long ones in minutes', () => {
		assert.strictEqual(formatSeconds(0), '0.0s');
		assert.strictEqual(formatSeconds(1500), '1.5s');
		assert.strictEqual(formatSeconds(59_900), '59.9s');
		assert.strictEqual(formatSeconds(60_000), '1m00s');
		assert.strictEqual(formatSeconds(125_000), '2m05s');
	});

	test('the scenario line carries the outcome and duration beside a readable name', () => {
		const line = scenarioLine('B-01', 'cli', 'clone into two working copies', 'PASS', 2500);

		assert.match(line, /B-01 \[cli\]/);
		assert.match(line, /clone into two working copies/);
		assert.match(line, /PASS/);
		assert.match(line, /2\.5s$/);
	});

	test('a long scenario name is shortened so the outcome column stays aligned', () => {
		const long = scenarioLine('B-01', 'cli', 'x'.repeat(80), 'PASS', 1000);
		const short = scenarioLine('B-01', 'cli', 'short', 'PASS', 1000);

		assert.strictEqual(long.length, short.length);
		assert.match(long, /…/);
	});

	test('a failure detail is split into what was expected and what happened', () => {
		const split = splitExpectation('the push reached the cloud: expected 3 files, actual 0 files');

		assert.strictEqual(split.what, 'the push reached the cloud');
		assert.strictEqual(split.expected, '3 files');
		assert.strictEqual(split.actual, '0 files');
	});

	test('a detail that is not an expectation is reported unchanged rather than mangled', () => {
		const split = splitExpectation('the language server never answered');

		assert.strictEqual(split.what, 'the language server never answered');
		assert.strictEqual(split.expected, undefined);
		assert.strictEqual(split.actual, undefined);
	});
});
describe('lsp e2e new components are complete', () => {
	test('every directory in the new component source is actually copied into the clone', () => {
		for (const shape of SHAPES) {
			const { root } = stageAndAdd(shape, 'CP');
			const source = newComponentSourceRoot(fixtures, shape);

			for (const file of listFilesIfPresent(source)) {
				const landed = path.join(root, path.relative(source, file));

				assert.ok(fs.existsSync(landed), `${shape.id} never copied ${path.relative(source, file)}`);
			}
		}
	});

	test('an action that invokes a prompt always arrives with the prompt it needs', () => {
		for (const shape of SHAPES) {
			const { root } = stageAndAdd(shape, 'CA');
			const promptIds = listFilesIfPresent(path.join(root, 'prompts')).filter(file => path.basename(file) === 'metadata.yml').map(file => readScalar(file, 'aIModelId'));

			for (const file of listFiles(root, ['.mcs']).filter(candidate => candidate.endsWith('.mcs.yml'))) {
				for (const match of fs.readFileSync(file, 'utf8').matchAll(/aIModelId:\s*([0-9a-f-]{36})/gi)) {
					assert.ok(promptIds.includes(match[1]), `${file} invokes AI model ${match[1]} but no prompt folder provides it`);
				}
			}
		}
	});
});
describe('lsp e2e smoke selection', () => {
	const smokeIds = CATALOGUE.filter(entry => entry.smoke).map(entry => entry.id);

	test('smoke is accepted as a selector and resolves to itself alone', () => {
		assert.deepStrictEqual(parseGroups('smoke'), ['smoke']);
	});

	test('smoke covers clone, push and pull and stays small', () => {
		assert.ok(smokeIds.includes('B-01'), 'smoke does not clone');
		assert.ok(smokeIds.includes('B-02'), 'smoke does not push');
		assert.ok(smokeIds.includes('B-04'), 'smoke does not pull');
		assert.ok(smokeIds.length <= 5, `smoke has grown to ${smokeIds.length} scenarios and is no longer a smoke test`);
	});

	test('smoke includes the attach that creates the agent the rest depend on', () => {
		assert.ok(smokeIds.includes('A-01'), 'a smoke run would have no agent to work against');
	});

	test('selecting smoke runs only the smoke scenarios', () => {
		for (const entry of CATALOGUE) {
			const ids = CATALOGUE.map(candidate => candidate.id);

			assert.strictEqual(isSelected(entry.id, ids, ['smoke']), entry.smoke === true, `${entry.id} is wrong under a smoke selection`);
		}
	});

	test('every smoke scenario still runs under its own full group', () => {
		for (const entry of CATALOGUE.filter(candidate => candidate.smoke)) {
			assert.ok(isSelected(entry.id, [entry.id], [entry.group]), `${entry.id} is smoke but excluded from ${entry.group}`);
		}
	});

	test('all still means the full groups, which already contain every smoke scenario', () => {
		assert.deepStrictEqual(parseGroups('all'), ALL_GROUPS);

		for (const entry of CATALOGUE.filter(candidate => candidate.smoke)) {
			assert.ok(isSelected(entry.id, [entry.id], ALL_GROUPS), `${entry.id} is missing from an all run`);
		}
	});

	test('every shape can run the whole smoke set, so neither shape is silently skipped', () => {
		for (const shape of SHAPES) {
			for (const id of smokeIds) {
				assert.ok(shape.scenarios.includes(id), `${shape.id} cannot run smoke scenario ${id}`);
			}
		}
	});

	test('smoke scenarios are declared in an order that satisfies their dependencies', () => {
		const order = CATALOGUE.map(entry => entry.id);

		assert.ok(order.indexOf('A-01') < order.indexOf('B-01'), 'cloning is declared before the agent exists');
		assert.ok(order.indexOf('B-01') < order.indexOf('B-02'), 'pushing is declared before cloning');
		assert.ok(order.indexOf('B-02') < order.indexOf('B-04'), 'pulling is declared before anything was pushed');
	});

	test('combining smoke with a group is additive rather than narrowing', () => {
		assert.ok(isSelected('C-01', ['C-01'], ['interop', 'smoke']), 'adding smoke dropped an interop scenario');
		assert.ok(isSelected('B-01', ['B-01'], ['interop', 'smoke']), 'a smoke scenario outside interop was dropped');
	});

	test('smoke is a valid selector everywhere the launcher accepts one', () => {
		assert.ok(ALL_SELECTORS.includes('smoke'));

		for (const selector of ALL_SELECTORS) {
			assert.doesNotThrow(() => parseGroups(selector));
		}
	});
});
describe('lsp e2e driver re-entry', () => {
	test('a first activation owns the run', () => {
		assert.deepStrictEqual(activationDecision({ exists: false }), { standDown: false });
	});

	test('a second activation stands down instead of starting a second pass', () => {
		const decision = activationDecision({ exists: true, firstActivation: '2026-01-01T00:00:00.000Z' });

		assert.strictEqual(decision.standDown, true);
		assert.match(decision.message!, /second activation/);
		assert.match(decision.message!, /2026-01-01T00:00:00\.000Z/);
	});

	test('standing down says it will not quit, because quitting would kill the live run', () => {
		const decision = activationDecision({ exists: true, firstActivation: 'x' });

		assert.match(decision.message!, /\.mcs/);
		assert.match(decision.message!, /quitting here would kill the activation that owns the run/);
	});

	test('a marker with no timestamp still stands down and says so', () => {
		for (const firstActivation of [undefined, '', '   ']) {
			const decision = activationDecision({ exists: true, firstActivation });

			assert.strictEqual(decision.standDown, true);
			assert.match(decision.message!, /no timestamp/);
		}
	});

	test('a timestamp with surrounding whitespace is reported cleanly', () => {
		assert.match(activationDecision({ exists: true, firstActivation: '  2026-05-05T10:00:00Z\n' }).message!, /the run started at 2026-05-05T10:00:00Z\./);
	});
});
describe('lsp e2e component survival', () => {
	test('nothing is reported missing when every component is on disk', () => {
		const root = scratch();
		fs.writeFileSync(path.join(root, 'present.txt'), 'x', 'utf8');

		assert.deepStrictEqual(survivingComponents(root, [{ kind: 'knowledge', relativePath: 'present.txt', cloudName: 'present.txt' }]).missing, []);
	});

	test('a component removed behind the suite is named, with its kind, so the cause is obvious', () => {
		const missing = survivingComponents(scratch(), [
			{ kind: 'prompt', relativePath: 'prompts/gone-123', cloudName: 'gone' },
			{ kind: 'workflow', relativePath: 'workflows/also-gone', cloudName: 'also gone' },
		]).missing;

		assert.deepStrictEqual(missing, ['prompt:prompts/gone-123', 'workflow:workflows/also-gone']);
	});

	test('a directory component counts as surviving, not only a file', () => {
		const root = scratch();
		fs.mkdirSync(path.join(root, 'behaviors', 'a-skill'), { recursive: true });

		assert.deepStrictEqual(survivingComponents(root, [{ kind: 'skill', relativePath: 'behaviors/a-skill', cloudName: 'a-skill' }]).missing, []);
	});

	test('an empty component list cannot report a phantom loss', () => {
		assert.deepStrictEqual(survivingComponents(os.tmpdir(), []).missing, []);
	});
});
describe('lsp e2e delete targets the exact component', () => {
	test('every file a shape deletes really is in its fixture', () => {
		for (const shape of SHAPES) {
			for (const relative of [shape.deletableRelativePath, ...shape.deletableExtraPaths]) {
				assert.ok(fs.existsSync(path.join(fixtures, shape.fixtureDirectory, ...relative.split('/'))), `${shape.id} cannot delete '${relative}': it is not in the fixture`);
			}
		}
	});

	test('a component that is a metadata and content pair deletes both halves', () => {
		const paired = SHAPES.filter(shape => shape.deletableExtraPaths.length > 0);

		for (const shape of paired) {
			assert.ok(shape.deletableRelativePath.endsWith('.mcs.yml'), `${shape.id} pairs files but does not name the metadata first`);
			assert.ok(shape.deletableExtraPaths.every(extra => !extra.endsWith('.mcs.yml')), `${shape.id} lists a second metadata file rather than its content`);
		}
	});

	test('an exact name match ignores case but never matches a different component', () => {
		const names = ['file 3.txt', 'file 4.txt', 'thank you'];

		assert.ok(namesCarry(names, 'File 3.txt'));
		assert.ok(!namesCarry(names, 'File 5.txt'));
	});

	test('an exact name match is not fooled by a component whose name merely contains it', () => {
		const names = ['get-us-weather.skill.md', 'getusweather6fd9fd40zip'];

		assert.ok(!namesCarry(names, 'get-us-weather'), 'the component is gone, so its leftover files must not count as present');
		assert.ok(!labelsCarry(names, 'get-us-weather'), 'a skill bundle and its manifest are not the skill, so they must not satisfy the check either');
	});

	test('an empty expectation never matches', () => {
		assert.ok(!namesCarry(['file 3.txt'], ''));
		assert.ok(!namesCarry([], 'file 3.txt'));
	});
});

describe('lsp e2e external project registration', () => {
	test('msagent adopts a project it did not create', () => {
		const args = initArgs('msagent', { agentName: 'lspe2e 1234 cli', projectDirectory: 'C:\\work\\p' });

		assert.strictEqual(args[1], 'init');
		assert.ok(args.includes('--mcs-agent-source') && args.includes('C:\\work\\p'));
		assert.ok(args.includes('--agent-name') && args.includes('lspe2e 1234 cli'));
		assert.ok(args.includes('--non-interactive'));
	});

	test('pac needs no registration step, so it reports nothing to run', () => {
		assert.deepStrictEqual(initArgs('pac', { agentName: 'a', projectDirectory: 'C:\\work\\p' }), []);
	});

	test('the agent name survives quoting as a single token', () => {
		assert.ok(initArgs('msagent', { agentName: 'lspe2e 1234 cli', projectDirectory: 'C:\\a b' }).map(quoteArgument).includes('"lspe2e 1234 cli"'));
	});
});
describe('lsp e2e new knowledge is authored bare', () => {
	test('the new knowledge file is written as a plain text file', () => {
		for (const shape of SHAPES) {
			const { root, facts } = stageAndAdd(shape, 'K1');
			const knowledge = facts.find(fact => fact.kind === 'knowledge')!;

			assert.ok(knowledge.relativePath.endsWith('.txt'), `${shape.id} added '${knowledge.relativePath}' rather than a text file`);
			assert.ok(fs.readFileSync(path.join(root, ...knowledge.relativePath.split('/')), 'utf8').length > 0, `${shape.id} added an empty knowledge file`);
		}
	});

	test('the suite never writes the metadata itself, because generating it is what is being tested', () => {
		for (const shape of SHAPES) {
			const { root, facts } = stageAndAdd(shape, 'K2');
			const knowledge = facts.find(fact => fact.kind === 'knowledge')!;
			const beside = path.join(path.dirname(path.join(root, ...knowledge.relativePath.split('/'))), `${path.basename(knowledge.relativePath)}.mcs.yml`);

			assert.ok(!fs.existsSync(beside), `${shape.id} wrote ${path.basename(beside)}; the product is supposed to generate it`);
			assert.ok(!metadataMaterialised(root, knowledge), `${shape.id} reports the metadata as already present`);
		}
	});

	test('the metadata the product must generate is named after the text file', () => {
		for (const shape of SHAPES) {
			const knowledge = stageAndAdd(shape, 'K3').facts.find(fact => fact.kind === 'knowledge')!;

			assert.strictEqual(knowledge.expectMaterialisedMetadata, `${path.basename(knowledge.relativePath)}.mcs.yml`);
		}
	});

	test('once the product writes the metadata, the scenario sees it', () => {
		for (const shape of SHAPES) {
			const { root, facts } = stageAndAdd(shape, 'K4');
			const knowledge = facts.find(fact => fact.kind === 'knowledge')!;
			const beside = path.join(path.dirname(path.join(root, ...knowledge.relativePath.split('/'))), knowledge.expectMaterialisedMetadata!);

			fs.writeFileSync(beside, 'mcs.metadata:\n  componentName: generated\n', 'utf8');

			assert.ok(metadataMaterialised(root, knowledge), `${shape.id} did not detect the generated metadata`);
		}
	});
});
describe('lsp e2e deployment slots', () => {
	test('a deployment name satisfies the characters and length msagent accepts', () => {
		for (const [suffix, shape] of [['4868', 'classic'], ['ab12', 'cli'], ['z', 'x']] as const) {
			const name = deploymentNameFor(suffix, shape);

			assert.match(name, /^[-a-zA-Z0-9_]+$/, `'${name}' contains a character msagent rejects`);
			assert.ok(name.length >= 4 && name.length <= 42, `'${name}' is ${name.length} characters, outside msagent's 4 to 42`);
		}
	});

	test('a deployment name is unique per run and per shape', () => {
		assert.notStrictEqual(deploymentNameFor('4868', 'cli'), deploymentNameFor('4868', 'classic'));
		assert.notStrictEqual(deploymentNameFor('4868', 'cli'), deploymentNameFor('9ef0', 'cli'));
	});

	test('msagent creates a dev slot in the project directory', () => {
		const args = deploymentArgs('msagent', { projectDirectory: 'C:\\work\\p', deploymentName: 'lspe2e-4868-cli' });

		assert.strictEqual(args[0], 'deployment');
		assert.strictEqual(args[1], 'create');
		assert.ok(args.includes('--deployment-type') && args.includes('dev'));
		assert.ok(args.includes('--project') && args.includes('C:\\work\\p'));
	});

	test('pac has no deployment step, so it reports nothing to run', () => {
		assert.deepStrictEqual(deploymentArgs('pac', { projectDirectory: 'C:\\work\\p', deploymentName: 'x' }), []);
	});

	test('a deploy names the slot it was given, and omits it when there is none', () => {
		const withSlot = pushArgs('msagent', { agentId: 'a', projectDirectory: 'C:\\work\\p', deploymentName: 'lspe2e-4868-cli' });
		const withoutSlot = pushArgs('msagent', { agentId: 'a', projectDirectory: 'C:\\work\\p' });

		assert.ok(withSlot.includes('--deployment-name') && withSlot.includes('lspe2e-4868-cli'));
		assert.ok(!withoutSlot.includes('--deployment-name'));
	});
});
