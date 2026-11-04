import * as assert from 'node:assert/strict';
import { test } from 'node:test';
import { createContext, runInContext } from 'node:vm';
import { deploymentSelectorScript } from '../../deployments/deploymentSelectorHtml';
import { Element } from './helpers/dom';

test('deployment selector works without connection-page state and does not repeat fallback IDs', () => {
  const elements = new Map<string, Element>();
  const element = (id: string) => {
    let result = elements.get(id);
    if (!result) {
      result = new Element();
      elements.set(id, result);
    }
    return result;
  };
  const selections: string[] = [];
  let refreshes = 0;
  const sandbox = createContext({
    document: {
      body: new Element(),
      getElementById: element,
      createElement: () => new Element(),
      addEventListener() {}
    },
    onSelect: (id: string) => selections.push(id),
    onRefresh: () => { refreshes++; }
  });
  runInContext(deploymentSelectorScript + `
    const selector = createDeploymentSelector({ onSelect, onRefresh, onChange() {} });
    selector.setContext({
      environmentId: 'workspace',
      deployments: [
        { id: 'attached', name: 'Attached deployment', environmentId: 'WORKSPACE' },
        { id: 'fallback-id', name: 'fallback-id', environmentId: 'remote' }
      ],
      currentDeploymentId: 'attached'
    });
    selector.setBusy(false);
  `, sandbox);
  assert.equal(element('deployment-options').children[1].children[0].textContent, 'fallback-id');
  assert.equal(element('sync-target-badge').textContent, 'Attached');
  element('deployment-options').children[1].fire('click');
  assert.deepEqual(selections, ['fallback-id']);
  assert.equal(element('deployment-select').value, 'attached', 'selection changes only after host confirmation');
  element('refresh-deployments').fire('click');
  assert.equal(refreshes, 1);
  runInContext('selector.setBusy(true);', sandbox);
  element('deployment-options').children[1].fire('click');
  element('refresh-deployments').fire('click');
  assert.equal(selections.length, 1);
  assert.equal(refreshes, 1);
});
