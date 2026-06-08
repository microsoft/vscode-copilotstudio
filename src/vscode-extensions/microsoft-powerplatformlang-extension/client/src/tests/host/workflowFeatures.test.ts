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
});
