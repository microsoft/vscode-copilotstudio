import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { buildGraphModel } from '../../workflowVisualization/workflowGraphModel';
import { findEmbeddedJsonFields, isWorkflowFile, parseWorkflow, resolveStringValueSpan } from '../../workflows/workflowParser';

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
