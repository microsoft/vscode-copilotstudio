import * as assert from 'node:assert';
import { readFileSync } from 'node:fs';
import * as path from 'node:path';
import { describe, test } from 'node:test';
import { runInNewContext } from 'node:vm';
import { buildGraphModel } from '../../workflowVisualization/workflowGraphModel';
import { findEmbeddedJsonFields, isWorkflowFile, parseWorkflow, resolveStringValueSpan } from '../../workflows/workflowParser';

interface IconTestEvent {
	button?: number;
	buttons?: number;
	pointerId?: number;
	clientX?: number;
	clientY?: number;
	target?: IconTestElement;
}

class IconTestElement {
	readonly children: IconTestElement[] = [];
	readonly attributes = new Map<string, string>();
	readonly listeners = new Map<string, (event?: IconTestEvent) => void>();
	readonly pointerCaptures: number[] = [];
	readonly classes = new Set<string>();
	readonly style: { backgroundSize?: string; backgroundPosition?: string } = {};
	parent?: IconTestElement;
	textContent = '';
	readonly classList = {
		add: (value: string) => { this.classes.add(value); },
		remove: (value: string) => { this.classes.delete(value); },
		contains: (value: string) => this.classes.has(value),
		toggle: (value: string, force: boolean) => {
			if (force) {
				this.classes.add(value);
			} else {
				this.classes.delete(value);
			}
		},
	};

	constructor(readonly tagName: string, private readonly onFocus?: (element: IconTestElement) => void) { }
	setAttribute(name: string, value: string): void {
		this.attributes.set(name, value);
		if (name === 'class') {
			value.split(' ').forEach((item) => this.classes.add(item));
		}
	}
	getAttribute(name: string): string | undefined { return this.attributes.get(name); }
	appendChild(child: IconTestElement): void { child.parent = this; this.children.push(child); }
	replaceChildren(): void {
		for (const child of this.children) {
			child.parent = undefined;
		}
		this.children.length = 0;
	}
	focus(): void { this.onFocus?.(this); }
	contains(element: IconTestElement): boolean {
		for (let current: IconTestElement | undefined = element; current; current = current.parent) {
			if (current === this) {
				return true;
			}
		}
		return false;
	}
	remove(): void {
		if (this.parent) {
			this.parent.children.splice(this.parent.children.indexOf(this), 1);
		}
	}
	addEventListener(name: string, listener: (event?: IconTestEvent) => void): void { this.listeners.set(name, listener); }
	setPointerCapture(pointerId: number): void { this.pointerCaptures.push(pointerId); }
	closest(selector: string): IconTestElement | undefined {
		for (let element: IconTestElement | undefined = this; element; element = element.parent) {
			if (element.classList.contains(selector.slice(1))) {
				return element;
			}
		}
		return undefined;
	}
	querySelector(tagName: string): IconTestElement | undefined {
		return this.children.find((child) => child.tagName === tagName);
	}
	getBBox(): { x: number; y: number; width: number; height: number } {
		return { x: 0, y: 0, width: 240, height: 66 };
	}
	getBoundingClientRect(): { width: number; height: number } { return { width: 1000, height: 600 }; }
}

function renderIcons(types: string[], iconUri?: string, paletteIconHint?: string, category?: string): IconTestElement[] {
	const workflow = JSON.parse(workflowText());
	const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
	graph.nodes = types.map((type, index) => ({
		id: String(index), type, name: 'Renamed step', position: { x: index * 320, y: 0 },
		data: { config: { iconUri, paletteIconHint, category, categoryDisplayName: 'Localized display name' } },
	}));
	graph.edges = [];
	return renderWorkflowIcons(JSON.stringify(workflow));
}

function renderWorkflowIcons(text: string, connectors: Parameters<typeof buildGraphModel>[1] = []): IconTestElement[] {
	return findElements(renderWorkflow(text, connectors), 'wf-node-icon');
}

function findElements(root: IconTestElement, className: string): IconTestElement[] {
	return [
		...(root.classList.contains(className) ? [root] : []),
		...root.children.flatMap(child => findElements(child, className)),
	];
}

function renderWorkflow(
	text: string,
	connectors: Parameters<typeof buildGraphModel>[1] = [],
	onPostMessage?: (message: { type: string; nodeId?: string }) => void,
): IconTestElement {
	const webview = createWorkflowWebview(onPostMessage);
	webview.updateModel(buildGraphModel(text, connectors));
	return webview.surface;
}

function createWorkflowWebview(onPostMessage?: (message: { type: string; nodeId?: string }) => void) {
	const elements = new Map<string, IconTestElement>();
	let activeElement: IconTestElement | undefined;
	const onFocus = (element: IconTestElement) => { activeElement = element; };
	const getElement = (id: string): IconTestElement => {
		let element = elements.get(id);
		if (!element) {
			element = new IconTestElement(id, onFocus);
			elements.set(id, element);
		}
		return element;
	};
	getElement('surface').setAttribute('data-icon-base', 'https://webview.example/icons');
	let onMessage: ((event: { data: object }) => void) | undefined;
	runInNewContext(readFileSync(path.join(__dirname, '..', '..', '..', 'src',
		'workflowVisualization', 'media', 'visualizer.js'), 'utf8'), {
		acquireVsCodeApi: () => ({ postMessage: (message: { type: string; nodeId?: string }) => onPostMessage?.(message) }),
		document: {
			get activeElement() { return activeElement; },
			getElementById: getElement,
			createElementNS: (_namespace: string, name: string) => new IconTestElement(name, onFocus),
		},
		window: {
			addEventListener: (_name: string, listener: typeof onMessage) => { onMessage = listener; },
		},
	});
	assert.ok(onMessage);
	const sendMessage = onMessage;
	return {
		surface: getElement('surface'),
		get activeElement() { return activeElement; },
		getElement,
		updateModel: (model: ReturnType<typeof buildGraphModel>) => sendMessage({ data: { type: 'model', model } }),
	};
}

describe('Workflow accessibility', () => {
	test('exposes full edge labels without exposing decorative connections', () => {
		const workflow = JSON.parse(classicWorkflowText());
		workflow.properties.definition.actions.Respond_to_the_agent.runAfter = { Send_an_email_notification: ['Failed'] };
		const switchWorkflow = JSON.parse(classicSwitchWorkflowText());
		const caseLabel = 'A switch case value longer than the displayed label width';
		switchWorkflow.properties.definition.actions.Switch.cases.Case.case = caseLabel;
		for (const [text, expected] of [[JSON.stringify(workflow), 'Failed'], [JSON.stringify(switchWorkflow), caseLabel]]) {
			const surface = renderWorkflow(text);
			const label = findElements(surface, 'wf-edge-label').find(element => element.parent?.getAttribute('aria-label') === expected);
			assert.ok(label);
			assert.strictEqual(label.parent!.getAttribute('role'), 'img');
			for (let element: IconTestElement | undefined = label; element; element = element.parent) {
				assert.notStrictEqual(element.getAttribute('aria-hidden'), 'true');
			}
			for (const path of findElements(surface, 'wf-edge')) {
				assert.strictEqual(path.getAttribute('aria-hidden'), 'true');
			}
		}
	});

	test('includes displayed branch outcomes in the button accessible name even when visually truncated', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		const rejected = 'Rejected with an outcome longer than the branch card width';
		graph.nodes = [{
			id: 'review', name: 'Human review', type: 'classifyOnInlineAgent', width: 240, height: 169,
			position: { x: 0, y: 0 },
			data: { outcomes: [{ id: 'approved', label: 'Approved' }, { id: 'rejected', label: rejected }] },
		}];
		graph.edges = [];
		const surface = renderWorkflow(JSON.stringify(workflow));
		const node = findElements(surface, 'wf-node')[0];
		assert.strictEqual(node.getAttribute('aria-label'), `Human review, classifyOnInlineAgent, Approved, ${rejected}`);
		assert.strictEqual(findElements(node, 'wf-branch-label').length, 2);
		assert.notStrictEqual(findElements(node, 'wf-branch-label')[1].textContent, rejected);
	});

	test('restores focused nodes after model updates, falls back to the canvas, and leaves external focus unchanged', () => {
		const webview = createWorkflowWebview();
		const model = buildGraphModel(workflowText());
		webview.updateModel(model);
		assert.strictEqual(webview.activeElement, undefined);
		const focused = findElements(webview.surface, 'wf-node').find(node => node.getAttribute('data-id') === 'yes-node');
		assert.ok(focused);
		focused.focus();
		webview.updateModel(model);
		const replacement = findElements(webview.surface, 'wf-node').find(node => node.getAttribute('data-id') === 'yes-node');
		assert.ok(replacement);
		assert.notStrictEqual(replacement, focused);
		assert.strictEqual(webview.activeElement, replacement);
		webview.updateModel({ ...model, nodes: model.nodes.filter(node => node.id !== 'yes-node') });
		assert.strictEqual(webview.activeElement, webview.getElement('canvas'));
		const toolbarButton = webview.getElement('btn-fit');
		toolbarButton.focus();
		webview.updateModel(model);
		assert.strictEqual(webview.activeElement, toolbarButton);
	});
});

function createCatalogController() {
	type Catalog = NonNullable<Parameters<typeof buildGraphModel>[1]>;
	let receive: (message: { type: 'ready' }) => void = () => assert.fail('Webview listener was not registered');
	let workspaceChanged: () => void = () => assert.fail('Workspace listener was not registered');
	let dispose: () => void = () => assert.fail('Disposal listener was not registered');
	let scheduled: (() => void) | undefined;
	let now = 0;
	let workspace = { workspaceUri: 'file:///fixture', syncInfo: { environmentId: 'first', accountInfo: {} } };
	const disposable = { dispose() {} };
	const models: ReturnType<typeof buildGraphModel>[] = [];
	const warnings: string[] = [];
	const errorMessages: string[] = [];
	const logs: { message?: string; data?: { message?: string; error: unknown } }[] = [];
	const requests: { resolve: (catalog: Catalog) => void; reject: (error: Error) => void }[] = [];
	const workflow = JSON.parse(workflowText());
	const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
	graph.nodes = [{ id: 'connector', type: 'connector', data: { config: { apiName: 'shared_teams' } } }];
	graph.edges = [];
	const document = {
		uri: { path: '/fixture/workflow.json', toString: () => 'file:///fixture/workflow.json' },
		getText: () => JSON.stringify(workflow),
	};
	const dependencies: Record<string, unknown> = {
		vscode: {
			ViewColumn: { Beside: 2 },
			window: {
				createWebviewPanel: () => ({
					webview: {
						onDidReceiveMessage: (callback: typeof receive) => { receive = callback; return disposable; },
						postMessage: (message: { model: ReturnType<typeof buildGraphModel> }) => {
							models.push(message.model);
							return Promise.resolve(true);
						},
					},
					onDidDispose: (callback: () => void) => { dispose = callback; return disposable; },
				}),
				onDidChangeTextEditorSelection: () => disposable,
				showWarningMessage: (message: string) => { warnings.push(message); },
			},
			workspace: { onDidChangeTextDocument: () => disposable },
		},
		'../services/logger': {
			logError: (_event: string, message?: string, data?: { message?: string; error: unknown }) => {
				logs.push({ message, data });
				if (message) {
					errorMessages.push(message);
				}
			},
		},
		'../constants': { TelemetryEventsKeys: { WorkflowVisualizeError: 'visualizeError' } },
		'../workflows/workflowCommands': {},
		'./workflowGraphModel': { buildGraphModel },
		'./webviewHtml': { buildWebviewHtml: () => '' },
		'../sync/localWorkspaces': {
			getWorkspaceByUri: () => workspace,
			addWorkspaceChangeSubscription: (callback: () => void) => { workspaceChanged = callback; return disposable; },
		},
		'../connections/connectionCatalog': {
			listConnectors: () => new Promise<Catalog>((resolve, reject) => requests.push({ resolve, reject })),
		},
	};
	const exports: { WorkflowVisualizerController?: { show(context: object, document: object): void } } = {};
	runInNewContext(readFileSync(path.join(__dirname, '..', '..', 'workflowVisualization', 'workflowVisualizer.js'), 'utf8'), {
		exports,
		require: (id: string) => { assert.ok(Object.hasOwn(dependencies, id), id); return dependencies[id]; },
		setTimeout: (callback: () => void) => { scheduled = callback; return 1; },
		clearTimeout: () => { scheduled = undefined; },
		Date: { now: () => now },
	});
	assert.ok(exports.WorkflowVisualizerController);
	exports.WorkflowVisualizerController.show({ extensionUri: {} }, document);
	return {
		models, warnings, errorMessages, logs, requests,
		ready: () => receive({ type: 'ready' }),
		refresh: () => workspaceChanged(),
		advanceTime: (milliseconds: number) => { now += milliseconds; },
		retarget: (environmentId = 'second') => {
			workspace = { ...workspace, syncInfo: { ...workspace.syncInfo, environmentId } };
			workspaceChanged();
		},
		dispose: () => dispose(),
		flush: async () => {
			const callback = scheduled;
			scheduled = undefined;
			callback?.();
			await new Promise<void>(resolve => setImmediate(resolve));
		},
	};
}

describe('Workflow catalog recovery', () => {
	const catalog = [{ internalId: 'shared_teams', iconUri: 'https://catalog.example/teams.png' }];

	test('backs off repeated failures, warns once per catalog key, and recovers on a later refresh', async () => {
		const controller = createCatalogController();
		controller.ready();
		const failure = new Error('Catalog unavailable');
		controller.requests[0].reject(failure);
		await controller.flush();
		assert.strictEqual(controller.requests.length, 1, 'do not immediately retry a failed request');
		assert.strictEqual(controller.warnings.length, 1);
		assert.deepStrictEqual(controller.errorMessages, []);
		assert.strictEqual(controller.logs.length, 1);
		assert.strictEqual(controller.logs[0].message, undefined);
		assert.strictEqual(controller.logs[0].data?.message, 'Failed to load workflow connector icons');
		assert.strictEqual(controller.logs[0].data?.error, failure);

		for (const elapsed of [0, 10_000, 19_999]) {
			controller.advanceTime(elapsed);
			controller.refresh();
			await controller.flush();
			assert.strictEqual(controller.requests.length, 1, 'ordinary edits must not retry during backoff');
			assert.strictEqual(controller.models.at(-1)?.nodes[0].catalogIconUri, undefined);
		}
		assert.strictEqual(controller.models.length, 4, 'keep updating the graph during backoff');
		controller.advanceTime(1);
		controller.refresh();
		await controller.flush();
		assert.strictEqual(controller.requests.length, 2);
		controller.requests[1].reject(new Error('Catalog still unavailable'));
		await controller.flush();
		assert.strictEqual(controller.warnings.length, 1, 'do not repeat a warning for the same catalog key');
		assert.strictEqual(controller.logs.length, 2, 'still log each attempted request failure');
		assert.deepStrictEqual(controller.errorMessages, []);
		controller.advanceTime(29_999);
		controller.refresh();
		await controller.flush();
		assert.strictEqual(controller.requests.length, 2, 'apply backoff after every failure');
		controller.advanceTime(1);
		controller.refresh();
		await controller.flush();
		assert.strictEqual(controller.requests.length, 3);
		controller.requests[2].resolve(catalog);
		await controller.flush();
		assert.strictEqual(controller.models.at(-1)?.nodes[0].catalogIconUri, catalog[0].iconUri);
		controller.refresh();
		await controller.flush();
		assert.strictEqual(controller.requests.length, 3, 'reuse the catalog after a successful retry');
		controller.dispose();
	});

	test('allows a new catalog key immediately and remembers warnings when returning to a previous key', async () => {
		const controller = createCatalogController();
		controller.ready();
		controller.requests[0].reject(new Error('First environment unavailable'));
		await controller.flush();
		controller.retarget();
		await controller.flush();
		assert.strictEqual(controller.requests.length, 2, 'a different environment must not inherit the backoff');
		controller.requests[1].reject(new Error('Second environment unavailable'));
		await controller.flush();
		assert.strictEqual(controller.warnings.length, 2, 'warn once for each catalog key');
		controller.retarget('first');
		await controller.flush();
		assert.strictEqual(controller.requests.length, 3);
		controller.requests[2].reject(new Error('First environment still unavailable'));
		await controller.flush();
		assert.strictEqual(controller.warnings.length, 2, 'do not repeat warnings after switching back');
		assert.deepStrictEqual(controller.errorMessages, []);
		controller.dispose();
	});

	test('does not notify or reset a newer request when a previous environment request fails', async () => {
		const controller = createCatalogController();
		controller.ready();
		controller.retarget();
		await controller.flush();
		assert.strictEqual(controller.requests.length, 2);
		controller.requests[0].reject(new Error('Old environment unavailable'));
		await controller.flush();
		assert.deepStrictEqual(controller.warnings, []);
		assert.deepStrictEqual(controller.errorMessages, []);
		assert.strictEqual(controller.logs.length, 1);
		controller.refresh();
		await controller.flush();
		assert.strictEqual(controller.requests.length, 2, 'a stale failure must not clear the current in-flight flag');
		controller.requests[1].resolve(catalog);
		await controller.flush();
		assert.strictEqual(controller.models.at(-1)?.nodes[0].catalogIconUri, catalog[0].iconUri);
		controller.dispose();
	});

	test('logs without notifications or model updates when a disposed panel request fails', async () => {
		const controller = createCatalogController();
		controller.ready();
		controller.dispose();
		controller.requests[0].reject(new Error('Catalog unavailable'));
		await controller.flush();
		assert.deepStrictEqual(controller.warnings, []);
		assert.deepStrictEqual(controller.errorMessages, []);
		assert.strictEqual(controller.logs.length, 1);
		assert.strictEqual(controller.models.length, 1);
	});
});

describe('Workflow container navigation', () => {
	test('pans container backgrounds only after a drag threshold and suppresses source navigation after dragging', () => {
		for (const text of [workflowText(), classicConditionWorkflowText()]) {
			const messages: { type: string; nodeId?: string }[] = [];
			const surface = renderWorkflow(text, [], message => messages.push(message));
			const background = findElements(surface, 'wf-container')[0];
			assert.ok(background);
			const pointer = { button: 0, buttons: 1, pointerId: 1, clientX: 100, clientY: 100, target: background };
			const before = surface.style.backgroundPosition;
			surface.listeners.get('pointerdown')!(pointer);
			surface.listeners.get('pointermove')!({ ...pointer, clientX: 102 });
			assert.strictEqual(surface.style.backgroundPosition, before);
			assert.strictEqual(surface.pointerCaptures.length, 0);
			surface.listeners.get('pointermove')!({ ...pointer, clientX: 140, clientY: 130 });
			assert.notStrictEqual(surface.style.backgroundPosition, before);
			assert.deepStrictEqual(surface.pointerCaptures, [1]);
			assert.ok(surface.classList.contains('panning'));
			surface.listeners.get('pointerup')!(pointer);
			background.parent!.listeners.get('click')!();
			assert.strictEqual(messages.filter(message => message.type === 'reveal').length, 0);
			assert.ok(!surface.classList.contains('panning'));
			const after = surface.style.backgroundPosition;
			surface.listeners.get('pointermove')!({ ...pointer, clientX: 180 });
			assert.strictEqual(surface.style.backgroundPosition, after);
			surface.listeners.get('pointerdown')!(pointer);
			surface.listeners.get('pointerleave')!(pointer);
			surface.listeners.get('pointermove')!({ ...pointer, clientX: 180 });
			assert.strictEqual(surface.style.backgroundPosition, after);
		}
	});

	test('preserves intentional container clicks and keeps child cards clickable instead of draggable', () => {
		for (const [text, childId] of [[workflowText(), 'yes-node'], [classicConditionWorkflowText(), 'Respond_to_the_agent']]) {
			const messages: { type: string; nodeId?: string }[] = [];
			const surface = renderWorkflow(text, [], message => messages.push(message));
			const background = findElements(surface, 'wf-container')[0];
			assert.ok(background);
			const pointer = { button: 0, buttons: 1, pointerId: 1, clientX: 100, clientY: 100, target: background };
			const before = surface.style.backgroundPosition;
			surface.listeners.get('pointerdown')!(pointer);
			surface.listeners.get('pointermove')!({ ...pointer, clientX: 102 });
			surface.listeners.get('pointerup')!(pointer);
			background.parent!.listeners.get('click')!();
			assert.strictEqual(messages.at(-1)?.nodeId, background.parent!.getAttribute('data-id'));
			assert.strictEqual(surface.style.backgroundPosition, before);

			const card = findElements(surface, 'wf-node-card').find(element => element.parent?.getAttribute('data-id')?.endsWith(childId));
			assert.ok(card);
			surface.listeners.get('pointerdown')!({ ...pointer, target: card });
			surface.listeners.get('pointermove')!({ ...pointer, target: card, clientX: 140 });
			surface.listeners.get('pointerup')!({ ...pointer, target: card });
			card.parent!.listeners.get('click')!();
			assert.strictEqual(messages.at(-1)?.nodeId, card.parent!.getAttribute('data-id'));
			assert.strictEqual(surface.style.backgroundPosition, before);
			assert.strictEqual(surface.pointerCaptures.length, 0);
		}
	});
});

describe('Workflow category icons', () => {
	test('uses bundled category artwork for renamed steps and no icon for unmapped types or Copilot', () => {
		const types = ['agent', 'classifyOnInlineAgent', 'agentExtract', 'connector',
			'builtinFunction', 'variable', 'ifElse', 'loop', 'start', 'customFutureStep', 'constructor', 'm365Copilot',
			'classify', 'extract', 'humanReview', 'If', 'Compose'];
		const icons = renderIcons(types);
		const expected = ['agent', 'classify', 'extract', 'connector',
			'function', 'variable', 'if-else', 'loop', 'start'];
		for (const [index, category] of expected.entries()) {
			const fallback = icons[index].children[0];
			const image = fallback.children[0];
			assert.strictEqual(image.getAttribute('href'), `https://webview.example/icons/${category}.svg`);
			assert.ok(readFileSync(path.join(__dirname, '..', '..', '..', 'src',
				'workflowVisualization', 'media', 'icons', `${category}.svg`), 'utf8').includes('<svg'));
			assert.strictEqual(fallback.classList.contains('hidden'), false);
		}
		for (const icon of icons.slice(expected.length)) {
			assert.strictEqual(icon.children[0].children.length, 0);
			assert.strictEqual(icon.parent?.querySelector('text')?.textContent, 'Renamed step');
		}
	});

	test('renders all 53 published classic operation types and the JavaScriptCode alias using only type mappings', () => {
		const groups: [string | null, string[]][] = [
			['http', ['Http']],
			[null, ['ApiApp', 'ApiConnection', 'OpenApiConnection', 'OpenApiConnectionWebhook',
				'ApiConnectionWebhook', 'ApiManagement', 'ApiConnectionNotification']],
			['recurrence', ['Recurrence']],
			['workflow', ['Workflow', 'Flow']],
			['wait', ['Wait']],
			['agent-flow', ['Response', 'Request']],
			['webhook', ['HttpWebhook']],
			['compose', ['Compose']],
			['query', ['Query']],
			['azure-function', ['Function']],
			['function', ['XmlValidation', 'FlatFileEncoding', 'Xslt', 'FlatFileDecoding',
				'IntegrationAccountArtifactLookup', 'ParseJson', 'Table', 'Join', 'Select', 'SendToBatch',
				'Expression', 'Liquid', 'JavascriptCode', 'JavaScriptCode']],
			['scope', ['Scope']],
			['condition', ['If']],
			['foreach', ['Foreach']],
			['until', ['Until']],
			['terminate', ['Terminate']],
			['switch', ['Switch']],
			['variable-operation', ['InitializeVariable', 'SetVariable', 'IncrementVariable', 'DecrementVariable',
				'AppendToArrayVariable', 'AppendToStringVariable']],
			[null, ['Manual', 'Batch', 'SlidingWindow', 'As2Decode', 'As2Encode', 'RosettaNetEncode',
				'RosettaNetDecode', 'RosettaNetWaitForResponse', 'Changeset', 'SwiftEncode',
				'variable', 'builtinFunction', 'customFutureStep', 'constructor']],
		];
		const cases = groups.flatMap(([category, types]) => types.map(type => ({ type, category })));
		const text = JSON.stringify({
			properties: {
				definition: {
					triggers: { manual: { type: 'Request' } },
					actions: Object.fromEntries(cases.map(({ type }, index) => [
						`Step ${index}`, { type, kind: 'Skills', runAfter: {} },
					])),
				},
			},
		});
		assert.strictEqual(buildGraphModel(text).format, 'classic');
		assert.strictEqual(buildGraphModel(workflowText()).format, 'designer');
		const icons = renderWorkflowIcons(text);
		for (const [index, { category }] of cases.entries()) {
			const icon = icons.find(item => item.parent?.getAttribute('data-id') === `Step ${index}`);
			assert.ok(icon);
			assert.strictEqual(icon.children.length, 1);
			const images = icon.children[0].children;
			if (category) {
				assert.strictEqual(images[0].getAttribute('data-category'), category);
				assert.ok(readFileSync(path.join(__dirname, '..', '..', '..', 'src',
					'workflowVisualization', 'media', 'icons', `${category}.svg`), 'utf8').includes('<svg'));
				images[0].listeners.get('error')!();
				assert.strictEqual(icon.children[0].children.length, 0);
			} else {
				assert.strictEqual(images.length, 0);
			}
			assert.strictEqual(icon.parent?.querySelector('text')?.textContent, `Step ${index}`);
		}
	});

	test('prefers a loaded metadata image and restores the category when that image fails', () => {
		const icon = renderIcons(['builtinFunction'], 'https://example.com/current-time.svg')[0];
		const [fallback, metadata] = icon.children;
		assert.strictEqual(metadata.getAttribute('href'), 'https://example.com/current-time.svg');
		metadata.listeners.get('load')!();
		assert.strictEqual(fallback.classList.contains('hidden'), true);
		metadata.listeners.get('error')!();
		assert.strictEqual(icon.children.length, 1);
		assert.strictEqual(fallback.classList.contains('hidden'), false);
		assert.strictEqual(fallback.children[0].getAttribute('data-category'), 'function');
		assert.ok(icon.parent?.querySelector('title')?.textContent.includes('Step icon unavailable.'));
	});

	test('leaves only text when both metadata and category images fail', () => {
		const icon = renderIcons(['variable'], 'https://example.com/broken.svg')[0];
		const [fallback, metadata] = icon.children;
		metadata.listeners.get('error')!();
		fallback.children[0].listeners.get('error')!();
		assert.strictEqual(icon.children.length, 1);
		assert.strictEqual(fallback.children.length, 0);
		assert.strictEqual(fallback.classList.contains('hidden'), false);
		assert.strictEqual(icon.parent?.querySelector('text')?.textContent, 'Renamed step');
	});

	test('shows Copilot artwork only from its metadata URI, never from category hints', () => {
		const withoutUri = renderIcons(['m365Copilot'], undefined, 'connector')[0];
		assert.strictEqual(withoutUri.children[0].children.length, 0);

		const icon = renderIcons(['m365Copilot'], 'https://example.com/copilot.png')[0];
		const [fallback, metadata] = icon.children;
		assert.strictEqual(metadata.getAttribute('href'), 'https://example.com/copilot.png');
		metadata.listeners.get('load')!();
		assert.strictEqual(fallback.classList.contains('hidden'), true);
		metadata.listeners.get('error')!();
		assert.strictEqual(icon.children.length, 1);
		assert.strictEqual(fallback.children.length, 0);
		assert.strictEqual(icon.parent?.querySelector('text')?.textContent, 'Renamed step');
	});

	test('uses the exported Human review palette hint on renamed connector steps', () => {
		const withoutUri = renderIcons(['connector'], undefined, 'humanReview')[0];
		assert.strictEqual(withoutUri.children[0].children[0].getAttribute('data-category'), 'human-review');

		const icon = renderIcons(['connector'], 'https://example.com/approval.png', 'humanReview')[0];
		const [fallback, metadata] = icon.children;
		assert.strictEqual(fallback.children[0].getAttribute('data-category'), 'human-review');
		metadata.listeners.get('load')!();
		assert.ok(fallback.classList.contains('hidden'), 'Human review must keep its provided artwork even if it differs from the fallback');
		metadata.listeners.get('error')!();
		assert.strictEqual(fallback.classList.contains('hidden'), false);
		assert.strictEqual(fallback.children[0].getAttribute('href'), 'https://webview.example/icons/human-review.svg');

		for (const hint of ['futureHint', 'constructor', '../../unknown']) {
			const [connector, unknown] = renderIcons(['connector', 'customFutureStep'], undefined, hint);
			assert.strictEqual(connector.children[0].children[0].getAttribute('data-category'), 'connector');
			assert.strictEqual(unknown.children[0].children.length, 0);
		}
	});

	test('maps all five Function groups by technical category, independent of localized display names', () => {
		const categories = {
			DataOperation: 'function-data-operation', Datetime: 'function-datetime',
			Http: 'function-http', Schedule: 'function-datetime', Request: 'function-request',
		};
		for (const [category, expected] of Object.entries(categories)) {
			const [icon] = renderIcons(['builtinFunction'], undefined, undefined,
				`providers/Microsoft.ProcessSimple/operationGroups/${category}`);
			const [generic, subcategory] = icon.children[0].children;
			assert.strictEqual(subcategory.getAttribute('data-subcategory'), expected);
			assert.ok(readFileSync(path.join(__dirname, '..', '..', '..', 'src',
				'workflowVisualization', 'media', 'icons', `${expected}.svg`), 'utf8').includes('<svg'));
			subcategory.listeners.get('load')!();
			assert.ok(generic.classList.contains('hidden'));
		}
		for (const category of [undefined, 'DataOperation', 'constructor',
			'providers/Microsoft.ProcessSimple/operationGroups/Unknown']) {
			const [icon] = renderIcons(['builtinFunction'], undefined, undefined, category);
			assert.strictEqual(icon.children[0].children.length, 1);
			assert.strictEqual(icon.children[0].children[0].getAttribute('data-category'), 'function');
		}
	});

	test('falls through metadata, Function subcategory, generic category, then text in either load order', () => {
		for (const metadataFirst of [true, false]) {
			const [icon] = renderIcons(['builtinFunction'], 'https://example.com/custom.svg', undefined,
				'providers/Microsoft.ProcessSimple/operationGroups/Http');
			const [fallback, metadata] = icon.children;
			const [generic, subcategory] = fallback.children;
			for (const image of metadataFirst ? [metadata, subcategory] : [subcategory, metadata]) {
				image.listeners.get('load')!();
			}
			assert.ok(fallback.classList.contains('hidden'), 'metadata must win regardless of image load order');
			metadata.listeners.get('error')!();
			assert.ok(!fallback.classList.contains('hidden'));
			assert.ok(generic.classList.contains('hidden'), 'subcategory remains visible after metadata fails');
			subcategory.listeners.get('error')!();
			assert.ok(!generic.classList.contains('hidden'));
			generic.listeners.get('error')!();
			assert.strictEqual(fallback.children.length, 0);
			assert.strictEqual(icon.parent?.querySelector('text')?.textContent, 'Renamed step');
		}
	});

	test('does not apply Function subcategories to non-Function steps', () => {
		const [icon] = renderIcons(['connector'], undefined, undefined,
			'providers/Microsoft.ProcessSimple/operationGroups/Http');
		assert.strictEqual(icon.children[0].children.length, 1);
		assert.strictEqual(icon.children[0].children[0].getAttribute('data-category'), 'connector');
	});
});

describe('Designer connector catalog icons', () => {
	const catalog = [
		{ internalId: '/providers/Microsoft.PowerApps/apis/shared_teams', iconUri: 'https://catalog.example/teams.png' },
		{ internalId: 'shared_office365', iconUri: 'https://catalog.example/outlook.png' },
		{ internalId: 'shared_teams-1', iconUri: 'https://catalog.example/different-connector.png' },
	];
	const designerText = (config: Record<string, unknown>, type = 'connector'): string => {
		const workflow = JSON.parse(workflowText());
		workflow.properties.connectionReferences = {
			'shared_teams-1': { api: { name: 'shared_teams' } },
		};
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes = [{ id: 'connector', type, name: 'Renamed step', position: { x: 0, y: 0 }, data: { config } }];
		graph.edges = [];
		return JSON.stringify(workflow);
	};

	test('matches apiName or a resolved connection alias without guessing from connection names or labels', () => {
		for (const config of [
			{ apiName: 'SHARED_TEAMS' },
			{ apiName: '/providers/Microsoft.PowerApps/apis/shared_teams' },
			{ connectionName: 'shared_teams-1' },
		]) {
			const node = buildGraphModel(designerText(config), catalog).nodes[0];
			assert.strictEqual(node.connectorId, 'shared_teams');
			assert.strictEqual(node.catalogIconUri, catalog[0].iconUri);
			assert.strictEqual(node.iconUri, undefined);
		}
		const direct = buildGraphModel(designerText({
			apiName: 'shared_office365', connectionName: 'shared_teams-1',
		}), catalog).nodes[0];
		assert.strictEqual(direct.catalogIconUri, catalog[1].iconUri);
		for (const config of [
			{ connectionName: 'shared_teams' },
			{ apiName: '/providers/Other/apis/shared_teams' },
			{ apiName: '../shared_teams' },
			{ apiName: 'unknown' },
			{ displayName: 'Microsoft Teams' },
		]) {
			assert.strictEqual(buildGraphModel(designerText(config), catalog).nodes[0].catalogIconUri, undefined);
			const [icon] = renderWorkflowIcons(designerText(config), catalog);
			assert.strictEqual(icon.children.length, 1);
			assert.strictEqual(icon.children[0].children[0].getAttribute('data-category'), 'connector');
		}
		for (const type of ['agent', 'builtinFunction', 'm365Copilot', 'customFutureStep']) {
			const node = buildGraphModel(designerText({ apiName: 'shared_teams' }, type), catalog).nodes[0];
			assert.strictEqual(node.connectorId, undefined);
			assert.strictEqual(node.catalogIconUri, undefined);
		}
	});

	test('tries metadata, then catalog, then category, then no icon when each image fails', () => {
		const text = designerText({ apiName: 'shared_teams', iconUri: 'https://designer.example/teams.svg' });
		const node = buildGraphModel(text, catalog).nodes[0];
		assert.strictEqual(node.iconUri, 'https://designer.example/teams.svg');
		assert.strictEqual(node.catalogIconUri, catalog[0].iconUri);
		const [icon] = renderWorkflowIcons(text, catalog);
		const [fallback, metadata] = icon.children;
		assert.strictEqual(findElements(icon, 'wf-catalog-icon').length, 0, 'do not request the catalog image before metadata fails');
		metadata.listeners.get('load')!();
		assert.ok(fallback.classList.contains('hidden'));
		metadata.listeners.get('error')!();
		const image = findElements(icon, 'wf-catalog-icon')[0];
		assert.strictEqual(image.getAttribute('href'), catalog[0].iconUri);
		assert.strictEqual(findElements(icon, 'wf-metadata-icon').length, 0);
		image.listeners.get('load')!();
		assert.ok(fallback.classList.contains('hidden'));
		image.listeners.get('error')!();
		assert.strictEqual(fallback.classList.contains('hidden'), false);
		assert.strictEqual(fallback.children[0].getAttribute('data-category'), 'connector');
		fallback.children[0].listeners.get('error')!();
		assert.strictEqual(icon.children.length, 1);
		assert.strictEqual(fallback.children.length, 0);
		assert.strictEqual(icon.parent?.querySelector('text')?.textContent, 'Renamed step');
		assert.ok(icon.parent?.querySelector('title')?.textContent.includes('Connector catalog icon unavailable.'));
	});

	test('uses the catalog directly when metadata is missing or invalid and never retries the same URL', () => {
		for (const iconUri of [undefined, 'javascript:alert(1)', 'http://designer.example/teams.png']) {
			const [icon] = renderWorkflowIcons(designerText({ apiName: 'shared_teams', iconUri }), catalog);
			assert.strictEqual(findElements(icon, 'wf-metadata-icon').length, 0);
			const image = findElements(icon, 'wf-catalog-icon')[0];
			assert.strictEqual(image.getAttribute('href'), catalog[0].iconUri);
			image.listeners.get('load')!();
			assert.ok(icon.children[0].classList.contains('hidden'));
		}
		const [icon] = renderWorkflowIcons(designerText({ apiName: 'shared_teams', iconUri: catalog[0].iconUri }), catalog);
		findElements(icon, 'wf-metadata-icon')[0].listeners.get('error')!();
		assert.strictEqual(findElements(icon, 'wf-catalog-icon').length, 0);
		assert.strictEqual(icon.children[0].classList.contains('hidden'), false);
	});

	test('rejects unsafe catalog URLs and does not restore a category image that already failed', () => {
		for (const iconUri of ['javascript:alert(1)', 'http://catalog.example/icon.png',
			'https://user:password@catalog.example/icon.png', 'file:///icon.png', 'not a URL']) {
			const connectors = [{ internalId: 'shared_teams', iconUri }];
			assert.strictEqual(buildGraphModel(designerText({ apiName: 'shared_teams' }), connectors).nodes[0].catalogIconUri, undefined);
			const [icon] = renderWorkflowIcons(designerText({ apiName: 'shared_teams' }), connectors);
			assert.strictEqual(findElements(icon, 'wf-catalog-icon').length, 0);
		}
		const [icon] = renderWorkflowIcons(designerText({
			apiName: 'shared_teams', iconUri: 'https://designer.example/broken.png',
		}), catalog);
		icon.children[0].children[0].listeners.get('error')!();
		findElements(icon, 'wf-metadata-icon')[0].listeners.get('error')!();
		const image = findElements(icon, 'wf-catalog-icon')[0];
		image.listeners.get('load')!();
		assert.ok(icon.children[0].classList.contains('hidden'));
		image.listeners.get('error')!();
		assert.strictEqual(icon.children[0].children.length, 0);
		assert.strictEqual(icon.children[0].classList.contains('hidden'), false);
	});
});

describe('Classic portal rendering', () => {
	test('leaves connector artwork empty without a catalog instead of guessing from connector IDs', () => {
		const text = JSON.stringify({
			properties: {
				connectionReferences: {
					teamsAlias: { api: { name: 'shared_teams' } },
					approvalsAlias: { api: { name: 'shared_advancedapprovals' } },
				},
				definition: {
					triggers: {
						renamed: { type: 'OpenApiConnectionWebhook', inputs: { host: { connectionName: 'teamsAlias' } } },
					},
					actions: {
						Renamed_step: {
							type: 'OpenApiConnection',
							inputs: { host: { apiId: '/providers/Microsoft.PowerApps/apis/shared_teams' } },
						},
						Scope: {
							type: 'Scope',
							actions: {
								Another_step: {
									type: 'OpenApiConnectionWebhook',
									inputs: { host: { apiId: '/providers/Microsoft.PowerApps/apis/shared_advancedapprovals' } },
								},
								Reference_only: {
									type: 'OpenApiConnectionWebhook',
									inputs: { host: { connectionName: 'approvalsAlias' } },
								},
							},
						},
					},
				},
			},
		});
		const model = buildGraphModel(text);
		const icons = renderWorkflowIcons(text);
		for (const id of [
			'trigger:renamed', 'Renamed_step', 'Scope/Another_step', 'Scope/Reference_only',
		]) {
			assert.strictEqual(model.nodes.find(item => item.id === id)!.catalogIconUri, undefined);
			const icon = icons.find(item => item.parent?.getAttribute('data-id') === id)!;
			assert.strictEqual(icon.children.length, 1);
			const [fallback] = icon.children;
			assert.strictEqual(fallback.classList.contains('hidden'), false);
			assert.strictEqual(fallback.children.length, 0);
		}
	});

	test('resolves connector icons from catalog IDs, including aliases, nested actions and custom connectors', () => {
		const ids = ['shared_teams', 'shared_office365', 'shared_approvals',
			'shared_advancedapprovals', 'shared_onedriveforbusiness', 'custom-new-connector'];
		const connectors = ids.map((id, index) => ({
			internalId: index % 2 ? `/providers/Microsoft.PowerApps/apis/${id}` : id,
			iconUri: `https://catalog.example/${index}.png`,
		}));
		const workflow = {
			properties: {
				connectionReferences: { alias: { api: { name: ids[1] } } },
				definition: {
					triggers: {
						Renamed: { type: 'OpenApiConnectionWebhook', inputs: {
							host: { apiId: `/providers/Microsoft.PowerApps/apis/${ids[0]}` },
						} },
					},
					actions: {
						Scope: { type: 'Scope', actions: Object.fromEntries(ids.slice(1).map((id, index) => [
							`Renamed_${index}`, {
								type: index % 2 ? 'OpenApiConnectionWebhook' : 'OpenApiConnection',
								inputs: { host: index === 0 ? { connectionName: 'alias' }
									: { apiId: `/providers/Microsoft.PowerApps/apis/${id}` } },
							},
						])) },
						Unknown: { type: 'OpenApiConnection', inputs: { host: {
							apiId: '/providers/Microsoft.PowerApps/apis/unknown',
						} } },
						Not_a_connector: { type: 'customFutureStep', inputs: { host: { apiId: ids[0] } } },
						Wrong_provider: { type: 'OpenApiConnection', inputs: { host: {
							apiId: `/providers/Other/apis/${ids[0]}`,
						} } },
					},
				},
			},
		};
		const text = JSON.stringify(workflow);
		const model = buildGraphModel(text, connectors);
		const icons = renderWorkflowIcons(text, connectors);
		for (const [index, nodeId] of ['trigger:Renamed', ...ids.slice(1).map((_, i) => `Scope/Renamed_${i}`)].entries()) {
			const node = model.nodes.find(node => node.id === nodeId)!;
			assert.strictEqual(node.catalogIconUri, connectors[index].iconUri);
			assert.strictEqual(node.iconUri, undefined);
			const icon = icons.find(item => item.parent?.getAttribute('data-id') === nodeId)!;
			const [fallback, image] = icon.children;
			assert.strictEqual(image.getAttribute('href'), connectors[index].iconUri);
			image.listeners.get('load')!();
			assert.strictEqual(fallback.classList.contains('hidden'), true);
			image.listeners.get('error')!();
			assert.strictEqual(icon.children.length, 1);
			assert.strictEqual(fallback.children.length, 0);
			assert.ok(icon.parent?.querySelector('title')?.textContent.includes('Connector catalog icon unavailable.'));
		}
		for (const id of ['Unknown', 'Not_a_connector', 'Wrong_provider']) {
			assert.strictEqual(model.nodes.find(node => node.id === id)!.catalogIconUri, undefined);
			assert.strictEqual(icons.find(icon => icon.parent?.getAttribute('data-id') === id)!
				.children[0].children.length, 0);
		}
		assert.deepStrictEqual(buildGraphModel(workflowText(), connectors), buildGraphModel(workflowText()));
	});

	test('rejects unsafe catalog icon URLs and never substitutes classic metadata artwork', () => {
		const workflow = JSON.parse(classicWorkflowText());
		const action = workflow.properties.definition.actions.Send_an_email_notification;
		action.inputs = { host: { apiId: '/providers/Microsoft.PowerApps/apis/shared_test' } };
		action.metadata = { iconUri: 'https://guessed.example/icon.png' };
		for (const iconUri of ['javascript:alert(1)', 'http://catalog.example/icon.png',
			'https://user:password@catalog.example/icon.png', 'file:///icon.svg', 'not a URL']) {
			const text = JSON.stringify(workflow);
			const connectors = [{ internalId: 'shared_test', iconUri }];
			assert.strictEqual(buildGraphModel(text, connectors).nodes.find(node =>
				node.id === 'Send_an_email_notification')!.catalogIconUri, undefined);
			assert.strictEqual(renderWorkflowIcons(text, connectors).find(icon =>
				icon.parent?.getAttribute('data-id') === 'Send_an_email_notification')!.children[0].children.length, 0);
		}
	});

	test('uses wide, compact cards with larger icons and simple directional connections', () => {
		const text = classicWorkflowText();
		const model = buildGraphModel(text);
		const ordered = [...model.nodes].sort((a, b) => a.y - b.y);
		for (const [index, node] of ordered.entries()) {
			assert.deepStrictEqual([node.width, node.height], [600, 74]);
			assert.strictEqual(node.x, ordered[0].x);
			if (index) {
				assert.strictEqual(node.y - ordered[index - 1].y - ordered[index - 1].height, 46);
			}
			assert.ok(text.slice(node.actionOffset!, node.actionOffset! + node.actionLength!).includes(
				node.id === 'trigger:manual' ? '"manual"' : `"${node.id}"`));
		}
		const surface = renderWorkflow(text);
		assert.strictEqual(surface.getAttribute('data-format'), 'classic');
		for (const card of findElements(surface, 'wf-node-card')) {
			assert.strictEqual(card.getAttribute('rx'), '12');
		}
		for (const icon of findElements(surface, 'wf-category-icon')) {
			assert.strictEqual(icon.getAttribute('width'), '40');
			assert.strictEqual(icon.getAttribute('height'), '40');
		}
		assert.strictEqual(findElements(surface, 'wf-port').length, 0);
		assert.strictEqual(findElements(surface, 'wf-edge').length, model.edges.length);
		assert.ok(findElements(surface, 'wf-edge').every(edge => edge.getAttribute('marker-end') === 'url(#arrow)'));
	});

	test('uses HTTP icons only for HTTP-kind request/response operations, including nested responses', () => {
		for (const kind of [undefined, 'Skills', 'Other', 'Http', 'http', 'HTTP']) {
			const workflow = JSON.parse(classicWorkflowText());
			workflow.properties.definition.triggers.manual.kind = kind;
			workflow.properties.definition.actions.Respond_to_the_agent.kind = kind;
			workflow.properties.definition.actions.Send_an_email_notification.kind = kind;
			workflow.properties.definition.actions.Scope = {
				type: 'Scope',
				actions: {
					Nested_response: { type: 'Response', kind, runAfter: {} },
					Unmapped_step: { type: 'customFutureStep', kind, runAfter: {} },
				},
				runAfter: {},
			};
			const text = JSON.stringify(workflow);
			const model = buildGraphModel(text);
			const icons = renderWorkflowIcons(text);
			const isHttp = kind?.toLowerCase() === 'http';
			for (const id of ['trigger:manual', 'Respond_to_the_agent', 'Scope/Nested_response']) {
				assert.strictEqual(model.nodes.find(node => node.id === id)!.operationKind, kind);
				const icon = icons.find(item => item.parent?.getAttribute('data-id') === id)!;
				assert.strictEqual(icon.children[0].children[0].getAttribute('data-category'),
					isHttp ? 'response' : 'agent-flow');
			}
			assert.strictEqual(icons.find(icon => icon.parent?.getAttribute('data-id') === 'Send_an_email_notification')!
				.children[0].children.length, 0);
			assert.strictEqual(icons.find(icon => icon.parent?.getAttribute('data-id') === 'Scope/Unmapped_step')!
				.children[0].children.length, 0);
			if (isHttp) {
				assert.strictEqual(model.nodes[0].label, 'When an HTTP request is received');
			}
		}
	});

	test('preserves friendly agent and HTTP trigger labels without changing renamed triggers', () => {
		const workflow = JSON.parse(classicWorkflowText());
		workflow.properties.definition.actions.Respond_to_the_agent.kind = 'Skills';
		const text = JSON.stringify(workflow);
		const model = buildGraphModel(text);
		const trigger = model.nodes.find(node => node.id === 'trigger:manual')!;
		assert.strictEqual(trigger.type, 'Request');
		assert.strictEqual(trigger.label, 'When an agent calls the flow');
		const icons = renderWorkflowIcons(text);
		for (const id of ['trigger:manual', 'Respond_to_the_agent']) {
			assert.strictEqual(icons.find(icon => icon.parent?.getAttribute('data-id') === id)!
				.children[0].children[0].getAttribute('data-category'), 'agent-flow');
		}
		workflow.properties.definition.triggers.manual.kind = 'Http';
		workflow.properties.definition.actions.Respond_to_the_agent.kind = undefined;
		assert.strictEqual(buildGraphModel(JSON.stringify(workflow)).nodes[0].label, 'When an HTTP request is received');
		for (const icon of renderWorkflowIcons(JSON.stringify(workflow)).filter(icon =>
			['trigger:manual', 'Respond_to_the_agent'].includes(icon.parent!.getAttribute('data-id')!))) {
			assert.strictEqual(icon.children[0].children[0].getAttribute('data-category'),
				icon.parent!.getAttribute('data-id') === 'trigger:manual' ? 'response' : 'agent-flow');
		}
		workflow.properties.definition.triggers.Renamed_agent_trigger = workflow.properties.definition.triggers.manual;
		delete workflow.properties.definition.triggers.manual;
		assert.strictEqual(buildGraphModel(JSON.stringify(workflow)).nodes[0].label, 'Renamed agent trigger');
	});

	test('omits normal success labels regardless of casing while retaining exceptional run-after conditions', () => {
		const workflow = JSON.parse(classicWorkflowText());
		for (const statuses of [['SUCCEEDED'], ['Succeeded'], ['succeeded'], ['Failed'], ['Succeeded', 'Skipped']]) {
			workflow.properties.definition.actions.Respond_to_the_agent.runAfter.Send_an_email_notification = statuses;
			const model = buildGraphModel(JSON.stringify(workflow));
			const edge = model.edges.find(item => item.target === 'Respond_to_the_agent')!;
			assert.strictEqual(edge.label, statuses.every(status => status.toUpperCase() === 'SUCCEEDED')
				? undefined : statuses.join(', '));
		}
	});
});

describe('Saved designer rendering', () => {
	test('separates branch-card titles from outcomes without adding dividers to ordinary cards or classic workflows', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes = ['ifElse', 'classifyOnInlineAgent', 'agent'].map((type, index) => ({
			id: String(index), name: type, type, position: { x: index * 320, y: 100 },
			width: 240, height: 169,
			data: type === 'agent' ? {} : { outcomes: [{ id: 'first', label: 'First' }, { id: 'other', label: 'Other' }] },
		}));
		graph.edges = [];
		const surface = renderWorkflow(JSON.stringify(workflow));
		const dividers = findElements(surface, 'wf-header-divider');
		assert.strictEqual(dividers.length, 2);
		for (const divider of dividers) {
			const group = divider.parent!;
			const card = group.querySelector('rect')!;
			const label = group.querySelector('text')!;
			const branch = findElements(group, 'wf-branch')[0];
			assert.strictEqual(Number(divider.getAttribute('x1')), Number(card.getAttribute('x')));
			assert.strictEqual(Number(divider.getAttribute('x2')), Number(card.getAttribute('x')) + Number(card.getAttribute('width')));
			assert.ok(Number(divider.getAttribute('y1')) - Number(label.getAttribute('y')) >= 12);
			assert.ok(Number(branch.getAttribute('y')) - Number(divider.getAttribute('y1')) >= 12);
			assert.strictEqual(divider.getAttribute('y1'), divider.getAttribute('y2'));
		}
		assert.strictEqual(findElements(renderWorkflow(classicConditionWorkflowText()), 'wf-header-divider').length, 0);
	});

	test('renders notes as colored, clipped text without interpreting markdown as HTML', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes = [{
			id: 'note', type: 'canvasNote', name: 'Note', width: 250, height: 150,
			position: { x: 10, y: 20 }, data: { config: { markdown: '<script>alert(1)</script>\nSecond line', color: 'blue' } },
		}];
		graph.edges = [];
		const text = JSON.stringify(workflow);
		assert.deepStrictEqual(buildGraphModel(text).nodes[0].note, {
			text: '<script>alert(1)</script>\nSecond line', color: 'blue',
		});
		const surface = renderWorkflow(text);
		assert.strictEqual(findElements(surface, 'wf-node-icon').length, 0);
		assert.strictEqual(findElements(surface, 'wf-note')[0].getAttribute('data-note-color'), 'blue');
		const label = findElements(surface, 'wf-note-text')[0];
		assert.ok(label.getAttribute('clip-path')?.startsWith('url(#note-clip-'));
		assert.strictEqual(label.children[0].tagName, 'tspan');
		assert.strictEqual(label.children[0].textContent, '<script>alert(1)</script>');
		graph.nodes[0].data.config.markdown = '';
		assert.strictEqual(findElements(renderWorkflow(JSON.stringify(workflow)), 'wf-note-empty')[0].children[0].textContent, 'Empty note');
	});

	test('routes horizontal internal connections from the left container boundary without arrowheads', () => {
		const surface = renderWorkflow(workflowText());
		assert.strictEqual(surface.getAttribute('data-format'), 'designer');
		const edges = findElements(surface, 'wf-edge');
		assert.ok(edges.every(edge => !edge.getAttribute('marker-end')));
		const model = buildGraphModel(workflowText());
		const parent = model.nodes.find(node => node.id === 'if-node')!;
		const target = model.nodes.find(node => node.id === 'no-node')!;
		const points = edges[2].getAttribute('d')!.match(/-?\d+(?:\.\d+)?/g)!.map(Number);
		assert.strictEqual(points[0], parent.x);
		assert.strictEqual(points[6], target.x);
		assert.strictEqual(points[7], target.y + target.height / 2);
		const classic = renderWorkflow(classicWorkflowText());
		assert.strictEqual(classic.getAttribute('data-format'), 'classic');
		assert.ok(findElements(classic, 'wf-edge').every(edge => edge.getAttribute('marker-end') === 'url(#arrow)'));
	});
});

function workflowText(): string {
	return JSON.stringify({
		properties: {
			definition: {
				triggers: {
					manual: {
						metadata: {
							associatedData: {
								graph: {
									nodes: [
										{ id: 'start', name: 'Start', type: 'start', position: { x: 0, y: 0 }, width: 120, height: 60 },
										{ id: 'if-node', name: 'Customer choice', type: 'ifElse', position: { x: 0, y: 120 }, width: 240, height: 80 },
										{ id: 'yes-node', name: 'Yes answer', type: 'agent', parentId: 'if-node', position: { x: 40, y: 80 }, width: 180, height: 70 },
										{ id: 'no-node', name: 'No answer', type: 'agent', parentId: 'if-node', position: { x: 300, y: 80 }, width: 180, height: 70 },
									],
									edges: [
										{ id: 'start-if', source: 'start', target: 'if-node', sourceHandle: 'output', targetHandle: 'input' },
										{ id: 'if-yes', source: 'if-node', target: 'yes-node', sourceHandle: 'category: yes', targetHandle: 'input' },
										{ id: 'if-no', source: 'if-node', target: 'no-node', sourceHandle: 'internal-source', targetHandle: 'input' },
									],
								},
							},
						},
					},
				},
				actions: {
					Choice: {
						type: 'If',
						metadata: { nodeId: 'if-node', description: 'Fallback choice label' },
						expression: true,
						actions: {
							YesAction: {
								type: 'Compose',
								metadata: { nodeId: 'yes-node', description: 'Fallback yes label' },
								inputs: 'yes',
							},
						},
						else: {
							actions: {
								NoAction: {
									type: 'Compose',
									metadata: { nodeId: 'no-node', description: 'Fallback no label' },
									inputs: 'no',
								},
							},
						},
					},
					ShowCard: {
						type: 'Compose',
						inputs: {
							body: JSON.stringify({ type: 'AdaptiveCard', version: '1.5' }),
						},
					},
				},
			},
		},
	}, null, 2);
}

function overlappingContainerText(): string {
	return JSON.stringify({
		properties: {
			definition: {
				triggers: {
					manual: {
						metadata: {
							associatedData: {
								graph: {
									nodes: [
										{ id: 'loop-a', name: 'Loop', type: 'loop', position: { x: 100, y: 100 }, width: 600, height: 300 },
										{ id: 'loop-a-child-1', name: 'Loop child 1', type: 'agent', parentId: 'loop-a', position: { x: 40, y: 80 }, width: 240, height: 70 },
										{ id: 'loop-a-child-2', name: 'Loop child 2', type: 'agent', parentId: 'loop-a', position: { x: 320, y: 80 }, width: 240, height: 70 },
										{ id: 'loop-b', name: 'Loop 3', type: 'loop', position: { x: 686, y: 100 }, width: 600, height: 300 },
										{ id: 'loop-b-child-1', name: 'Loop 3 child 1', type: 'agent', parentId: 'loop-b', position: { x: 40, y: 80 }, width: 240, height: 70 },
										{ id: 'loop-b-child-2', name: 'Loop 3 child 2', type: 'agent', parentId: 'loop-b', position: { x: 380, y: 80 }, width: 240, height: 70 },
									],
									edges: [],
								},
							},
						},
					},
				},
				actions: {
					LoopA: { type: 'Foreach', metadata: { nodeId: 'loop-a' }, actions: {} },
					LoopB: { type: 'Foreach', metadata: { nodeId: 'loop-b' }, actions: {} },
				},
			},
		},
	}, null, 2);
}

function classicWorkflowText(): string {
	return JSON.stringify({
		properties: {
			definition: {
				triggers: {
					manual: {
						type: 'Request',
						kind: 'Skills',
					},
				},
				actions: {
					Respond_to_the_agent: {
						type: 'Response',
						runAfter: {
							Send_an_email_notification: ['SUCCEEDED'],
						},
					},
					Send_an_email_notification: {
						type: 'OpenApiConnection',
						inputs: {
							host: {
								operationId: 'SendEmailV3',
							},
						},
						runAfter: {},
					},
				},
			},
		},
	}, null, 2);
}

function classicConditionWorkflowText(): string {
	return JSON.stringify({
		properties: {
			definition: {
				triggers: {
					manual: { type: 'Request' },
				},
				actions: {
					List_rows: {
						type: 'OpenApiConnection',
						runAfter: {},
					},
					Condition: {
						type: 'If',
						runAfter: { List_rows: ['SUCCEEDED'] },
						actions: {
							Scope: {
								type: 'Scope',
								actions: {
									Scope_1: {
										type: 'Scope',
										actions: {
											Respond_to_the_agent: { type: 'Response' },
										},
									},
								},
							},
						},
						else: {
							actions: {
								Scope_3: { type: 'Scope', actions: {} },
							},
						},
					},
				},
			},
		},
	}, null, 2);
}

function classicSwitchWorkflowText(): string {
	return JSON.stringify({
		properties: {
			definition: {
				triggers: {
					manual: { type: 'Request' },
				},
				actions: {
					Switch: {
						type: 'Switch',
						runAfter: {},
						cases: {
							Case: {
								case: 'US',
								actions: {
									Run_an_agent: { type: 'OpenApiConnection' },
								},
							},
							'Case 2': {
								case: 'CA',
								actions: {
									Request_information: { type: 'OpenApiConnectionWebhook' },
									Apply_to_each: {
										type: 'Foreach',
										runAfter: { List_rows: ['Succeeded'] },
										actions: {},
									},
									List_rows: {
										type: 'OpenApiConnection',
										runAfter: { Request_information: ['Succeeded'] },
									},
								},
							},
						},
						default: {
							actions: {
								Do_until: { type: 'Until', actions: {} },
							},
						},
					},
					After_switch: {
						type: 'OpenApiConnection',
						runAfter: { Switch: ['Succeeded'] },
					},
				},
			},
		},
	}, null, 2);
}

describe('workflowParser', () => {
	test('recognizes workflow.json only under workflow folders', () => {
		assert.strictEqual(isWorkflowFile('C:/agent/workflows/TestWorkflow/workflow.json'), true);
		assert.strictEqual(isWorkflowFile('C:/agent/topics/TestTopic/workflow.json'), false);
		assert.strictEqual(isWorkflowFile('C:/agent/workflows/TestWorkflow/metadata.yml'), false);
	});

	test('builds action hierarchy with graph labels and source ranges', () => {
		const text = workflowText();
		const model = parseWorkflow(text);

		assert.strictEqual(model.valid, true);
		assert.strictEqual(model.root.children.length, 2);

		const choice = model.root.children[0];
		assert.strictEqual(choice.label, 'Customer choice');
		assert.strictEqual(choice.detail, 'If');
		assert.strictEqual(choice.graphType, 'ifElse');
		assert.ok(text.slice(choice.range.offset, choice.range.offset + choice.range.length).includes('"Choice"'));
		assert.deepStrictEqual(choice.children.map((child) => child.label), ['If', 'Else']);
		assert.strictEqual(choice.children[0].children[0].label, 'Yes answer');
		assert.strictEqual(choice.children[1].children[0].label, 'No answer');
	});

	test('finds embedded JSON strings and resolves their value spans', () => {
		const text = workflowText();
		const fields = findEmbeddedJsonFields(text);
		const card = fields.find((field) => field.label === 'body');

		assert.ok(card, 'expected embedded JSON body field');
		assert.deepStrictEqual(card.path, ['properties', 'definition', 'actions', 'ShowCard', 'inputs', 'body']);

		const span = resolveStringValueSpan(text, card.path);
		assert.ok(span, 'expected string value span for embedded JSON field');
		assert.ok(text.slice(span.offset, span.offset + span.length).includes('AdaptiveCard'));
	});
});

describe('workflowGraphModel', () => {
	test('preserves valid saved container bounds and expands only when children need more room', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes = [
			{ id: 'loop', type: 'loop', position: { x: 100, y: 100 }, width: 500, height: 319 },
			{ id: 'child', type: 'agent', parentId: 'loop', position: { x: 70, y: 80 }, width: 240, height: 217 },
		];
		graph.edges = [];
		const before = JSON.stringify(workflow);
		const container = buildGraphModel(before).nodes[0];
		assert.deepStrictEqual([container.x, container.y, container.width, container.height], [100, 100, 500, 319]);
		graph.nodes[1].position.x = 400;
		const expanded = buildGraphModel(JSON.stringify(workflow)).nodes[0];
		assert.deepStrictEqual([expanded.x, expanded.y, expanded.width, expanded.height], [100, 100, 670, 319]);
	});
	test('preserves saved card coordinates, dimensions, and parent-relative positions', () => {
		const text = workflowText();
		const saved = buildGraphModel(text);
		const start = saved.nodes.find((node) => node.id === 'start')!;
		const yes = saved.nodes.find((node) => node.id === 'yes-node')!;
		const no = saved.nodes.find((node) => node.id === 'no-node')!;

		assert.strictEqual(saved.valid, true);
		assert.strictEqual(saved.direction, 'horizontal');
		assert.deepStrictEqual([start.x, start.y, start.width, start.height], [0, 0, 120, 60]);
		assert.deepStrictEqual([yes.x, yes.y, yes.width, yes.height], [40, 200, 180, 70]);
		assert.deepStrictEqual([no.x, no.y, no.width, no.height], [300, 200, 180, 70]);
		assert.ok(text.slice(yes.actionOffset!, yes.actionOffset! + yes.actionLength!).includes('"YesAction"'));
		assert.deepStrictEqual(buildGraphModel(text), saved);
	});

	test('reads saved condition outcomes and matches their exact connection handles', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes = graph.nodes.slice(0, 2);
		graph.nodes[1].data = {
			outcomes: [
				{ id: 'condition:12345678-1234-1234-1234-123456789abc', label: 'If' },
				{ id: 'else', label: 'Else' },
			],
		};
		graph.nodes[1].measured = { width: 240, height: 169 };
		delete graph.nodes[1].width;
		delete graph.nodes[1].height;
		graph.edges[1].sourceHandle = graph.nodes[1].data.outcomes[0].id;
		const model = buildGraphModel(JSON.stringify(workflow));
		const condition = model.nodes[1];

		assert.deepStrictEqual(condition.outcomes, graph.nodes[1].data.outcomes);
		assert.strictEqual(model.edges[1].sourceHandle, condition.outcomes![0].id);
		assert.strictEqual(model.edges[1].label, undefined);
		assert.deepStrictEqual([condition.x, condition.y, condition.width, condition.height], [0, 120, 240, 169]);
		assert.strictEqual(condition.isContainer, false);
		delete graph.nodes[1].measured;
		assert.ok(buildGraphModel(JSON.stringify(workflow)).nodes[1].height >= 150, 'expected space for unsized branch rows');
	});

	test('uses the saved operation display name when the node has no name', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes[0].type = 'builtinFunction';
		delete graph.nodes[0].name;
		graph.nodes[0].data = { config: { displayName: 'Current time' } };

		assert.strictEqual(buildGraphModel(JSON.stringify(workflow)).nodes[0].label, 'Current time');
	});

	test('retains Function group identity with missing or localized category display names', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes = [{
			id: 'function', type: 'builtinFunction', name: 'Renamed step', position: { x: 0, y: 0 },
			data: { config: { category: 'providers/Microsoft.ProcessSimple/operationGroups/Http' } },
		}];
		graph.edges = [];
		for (const categoryDisplayName of [undefined, 'Localized category']) {
			graph.nodes[0].data.config.categoryDisplayName = categoryDisplayName;
			const model = buildGraphModel(JSON.stringify(workflow));
			assert.strictEqual(model.nodes[0].functionCategory, 'providers/Microsoft.ProcessSimple/operationGroups/Http');
			assert.strictEqual(model.nodes[0].iconUri, undefined);
			const [icon] = renderWorkflowIcons(JSON.stringify(workflow));
			assert.strictEqual(icon.children[0].children[1].getAttribute('data-subcategory'), 'function-http');
		}
	});

	test('uses metadata icons for arbitrary step types without changing saved positions', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes[0].type = 'customFutureStep';
		graph.nodes[0].data = { config: { iconUri: 'https://example.com/custom-step.svg' } };
		graph.nodes[1].data = { config: { iconUri: 'https://example.com/condition.png' } };
		const model = buildGraphModel(JSON.stringify(workflow));

		assert.strictEqual(model.nodes[0].iconUri, 'https://example.com/custom-step.svg');
		assert.strictEqual(model.nodes[1].iconUri, 'https://example.com/condition.png');
		assert.strictEqual(model.nodes[2].iconUri, undefined);
		assert.deepStrictEqual([model.nodes[0].x, model.nodes[0].y], [0, 0]);
	});

	test('ignores unusable or unsafe optional icon URLs', () => {
		const workflow = JSON.parse(workflowText());
		const node = workflow.properties.definition.triggers.manual.metadata.associatedData.graph.nodes[0];
		for (const iconUri of ['', 'not a URL', 'javascript:alert(1)', 'file:///icon.svg',
			'http://example.com/icon.svg', 'https://user:password@example.com/icon.svg']) {
			node.data = { config: { iconUri } };
			const model = buildGraphModel(JSON.stringify(workflow));
			assert.strictEqual(model.nodes[0].iconUri, undefined, `expected neutral fallback for ${iconUri}`);
			assert.strictEqual(model.valid, true);
		}
	});

	test('uses action descriptions and cards for unnamed empty control nodes', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes = graph.nodes.filter((node: { id: string }) => node.id === 'start' || node.id === 'if-node');
		delete graph.nodes[1].name;
		graph.nodes[1].width = 0;
		graph.nodes[1].height = -1;
		const model = buildGraphModel(JSON.stringify(workflow));
		const condition = model.nodes.find((node) => node.id === 'if-node')!;

		assert.strictEqual(condition.label, 'Fallback choice label');
		assert.strictEqual(condition.isContainer, false);
		assert.strictEqual(condition.width, 240);
		assert.strictEqual(condition.height, 66);
		assert.ok(model.nodes.find((node) => node.id === 'start')?.actionLength, 'expected trigger source navigation');
	});

	test('hides opaque handle ids but preserves explicit branch labels', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.edges[0].sourceHandle = 'condition:12345678-1234-1234-1234-123456789abc';
		graph.edges[1].label = 'Approved';
		const model = buildGraphModel(JSON.stringify(workflow));

		assert.strictEqual(model.edges[0].label, undefined);
		assert.strictEqual(model.edges[1].label, 'Approved');
		assert.strictEqual(model.edges[0].sourceHandle, graph.edges[0].sourceHandle);
	});

	test('saved layout handles cyclic or missing metadata parents', () => {
		const workflow = JSON.parse(workflowText());
		const graph = workflow.properties.definition.triggers.manual.metadata.associatedData.graph;
		graph.nodes[0].parentId = 'if-node';
		graph.nodes[1].parentId = 'start';
		graph.nodes[2].parentId = 'missing';
		const model = buildGraphModel(JSON.stringify(workflow));

		assert.strictEqual(model.valid, true);
		assert.ok(model.nodes.every((node) => Number.isFinite(node.x) && Number.isFinite(node.y)));
		assert.strictEqual(model.nodes.find((node) => node.id === 'yes-node')?.parentId, undefined);
	});

	test('keeps edge handles and labels only branch/category handles', () => {
		const model = buildGraphModel(workflowText());
		assert.strictEqual(model.valid, true);

		const branchEdge = model.edges.find((edge) => edge.id === 'if-yes');
		assert.ok(branchEdge, 'expected branch edge');
		assert.strictEqual(branchEdge.sourceHandle, 'category: yes');
		assert.strictEqual(branchEdge.targetHandle, 'input');
		assert.strictEqual(branchEdge.label, 'category: yes');

		const internalEdge = model.edges.find((edge) => edge.id === 'if-no');
		assert.ok(internalEdge, 'expected internal edge');
		assert.strictEqual(internalEdge.sourceHandle, 'internal-source');
		assert.strictEqual(internalEdge.label, undefined);
	});

	test('maps graph nodes back to action source spans', () => {
		const text = workflowText();
		const model = buildGraphModel(text);
		const yesNode = model.nodes.find((node) => node.id === 'yes-node');

		assert.ok(yesNode, 'expected yes graph node');
		assert.strictEqual(yesNode.label, 'Yes answer');
		assert.strictEqual(yesNode.type, 'agent');
		assert.strictEqual(yesNode.isContainer, false);
		assert.strictEqual(typeof yesNode.actionOffset, 'number');
		assert.strictEqual(typeof yesNode.actionLength, 'number');
		assert.ok(text.slice(yesNode.actionOffset!, yesNode.actionOffset! + yesNode.actionLength!).includes('"YesAction"'));
	});

	test('fits sibling containers to children so stale saved sizes do not overlap', () => {
		const model = buildGraphModel(overlappingContainerText());
		const loop = model.nodes.find((node) => node.id === 'loop-a');
		const loop3 = model.nodes.find((node) => node.id === 'loop-b');

		assert.ok(loop, 'expected first loop container');
		assert.ok(loop3, 'expected second loop container');
		assert.strictEqual(loop.isContainer, true);
		assert.strictEqual(loop3.isContainer, true);
		assert.ok(loop.x + loop.width < loop3.x, `expected fitted containers not to overlap, got ${JSON.stringify({ loop, loop3 })}`);
		assert.ok(loop.width < 600, 'expected stale saved width to be reduced to child bounds');
	});

	test('builds a fallback graph for classic workflows without graph metadata', () => {
		const text = classicWorkflowText();
		const model = buildGraphModel(text);

		assert.strictEqual(model.valid, true);
		assert.ok(model.nodes.find((node) => node.id === 'trigger:manual'), 'expected trigger node');
		assert.strictEqual(model.direction, 'vertical');
		const sendEmail = model.nodes.find((node) => node.id === 'Send_an_email_notification');
		assert.ok(sendEmail, 'expected connector action node');
		assert.strictEqual(sendEmail.label, 'Send an email notification');
		assert.strictEqual(sendEmail.type, 'OpenApiConnection');
		assert.strictEqual(sendEmail.actionOffset !== undefined, true);

		assert.ok(model.edges.find((edge) => edge.source === 'trigger:manual' && edge.target === 'Send_an_email_notification'));
		assert.ok(model.edges.find((edge) => edge.source === 'Send_an_email_notification' && edge.target === 'Respond_to_the_agent'));
	});

	test('nests classic condition branches and scope children', () => {
		const model = buildGraphModel(classicConditionWorkflowText());

		const condition = model.nodes.find((node) => node.id === 'Condition');
		const trueBranch = model.nodes.find((node) => node.id === 'Condition/true');
		const falseBranch = model.nodes.find((node) => node.id === 'Condition/false');
		const scope = model.nodes.find((node) => node.id === 'Condition/true/Scope');
		const scope1 = model.nodes.find((node) => node.id === 'Condition/true/Scope/Scope_1');
		const respond = model.nodes.find((node) => node.id === 'Condition/true/Scope/Scope_1/Respond_to_the_agent');
		const falseScope = model.nodes.find((node) => node.id === 'Condition/false/Scope_3');

		assert.ok(condition?.isContainer, 'expected condition container');
		assert.strictEqual(condition.type, 'If');
		assert.strictEqual(respond?.type, 'Response');
		assert.strictEqual(trueBranch?.parentId, 'Condition');
		assert.strictEqual(falseBranch?.parentId, 'Condition');
		assert.strictEqual(trueBranch?.label, 'true');
		assert.strictEqual(falseBranch?.label, 'false');
		assert.strictEqual(scope?.parentId, 'Condition/true');
		assert.strictEqual(scope1?.parentId, 'Condition/true/Scope');
		assert.strictEqual(respond?.parentId, 'Condition/true/Scope/Scope_1');
		assert.strictEqual(falseScope?.parentId, 'Condition/false');
		assert.ok(condition.width > trueBranch!.width + falseBranch!.width, 'expected condition to wrap both branch containers');

		const trueEdge = model.edges.find((edge) => edge.source === 'Condition' && edge.target === 'Condition/true');
		assert.strictEqual(trueEdge?.label, 'true');
		assert.strictEqual(trueEdge?.sourceHandle, 'internal-source');
	});

	test('renders classic switch as an action with case containers', () => {
		const model = buildGraphModel(classicSwitchWorkflowText());

		const switchNode = model.nodes.find((node) => node.id === 'Switch');
		const branchBlock = model.nodes.find((node) => node.id === 'Switch/branches');
		const caseNode = model.nodes.find((node) => node.id === 'Switch/case:Case');
		const case2Node = model.nodes.find((node) => node.id === 'Switch/case:Case 2');
		const defaultNode = model.nodes.find((node) => node.id === 'Switch/default');
		const runAgent = model.nodes.find((node) => node.id === 'Switch/case:Case/Run_an_agent');
		const listRows = model.nodes.find((node) => node.id === 'Switch/case:Case 2/List_rows');
		const applyToEach = model.nodes.find((node) => node.id === 'Switch/case:Case 2/Apply_to_each');
		const afterSwitch = model.nodes.find((node) => node.id === 'After_switch');

		assert.ok(switchNode, 'expected switch action node');
		assert.strictEqual(switchNode.isContainer, false);
		assert.strictEqual(switchNode.type, 'Switch');
		assert.strictEqual(switchNode.parentId, 'Switch/branches');
		assert.strictEqual(branchBlock?.isContainer, true);
		assert.strictEqual(branchBlock?.type, 'SwitchBlock');
		assert.ok(switchNode.y > branchBlock!.y, 'expected switch action inside switch branch block');
		assert.strictEqual(caseNode?.parentId, 'Switch/branches');
		assert.strictEqual(case2Node?.parentId, 'Switch/branches');
		assert.strictEqual(defaultNode?.parentId, 'Switch/branches');
		assert.strictEqual(caseNode?.isContainer, true);
		assert.strictEqual(case2Node?.isContainer, true);
		assert.strictEqual(defaultNode?.isContainer, true);
		assert.strictEqual(runAgent?.parentId, 'Switch/case:Case');
		assert.strictEqual(listRows?.parentId, 'Switch/case:Case 2');
		assert.strictEqual(applyToEach?.parentId, 'Switch/case:Case 2');
		assert.ok(listRows!.y < applyToEach!.y, 'expected local dependency to place List_rows before Apply_to_each');

		assert.ok(afterSwitch!.y > branchBlock!.y + branchBlock!.height, 'expected downstream action below switch branch block');

		const caseEdge = model.edges.find((edge) => edge.source === 'Switch' && edge.target === 'Switch/case:Case');
		assert.strictEqual(caseEdge?.label, 'US');
		assert.ok(model.edges.find((edge) => edge.source === 'Switch/case:Case 2/List_rows' && edge.target === 'Switch/case:Case 2/Apply_to_each'));
		assert.ok(model.edges.find((edge) => edge.source === 'Switch/branches' && edge.target === 'After_switch'));
	});
});
