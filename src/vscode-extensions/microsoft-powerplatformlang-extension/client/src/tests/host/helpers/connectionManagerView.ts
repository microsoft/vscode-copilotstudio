import * as assert from 'node:assert/strict';
import { createContext, runInContext } from 'node:vm';
import { buildConnectionManagerHtml } from '../../../connections/connectionManagerHtml';
import { Element } from './dom';

export function createConnectionManagerView() {
  const elements = new Map<string, Element>();
  const element = (id: string) => {
    let value = elements.get(id);
    if (!value) {
      value = new Element();
      elements.set(id, value);
    }
    return value;
  };
  let onMessage: ((event: { data: unknown }) => void) | undefined;
  const messages: { type: string; deploymentId?: string; hasPendingChanges?: boolean }[] = [];
  const documentEvents = new Element();
  const body = new Element();
  element('deployment-picker').appendChild(element('deployment-select'));
  element('deployment-picker').appendChild(element('deployment-options'));
  const html = buildConnectionManagerHtml({ cspSource: 'vscode-webview:' }, 'Agent');
  const script = /<script nonce="[^"]+">([\s\S]*?)<\/script>/.exec(html)?.[1];
  assert.ok(script);
  const sandbox = createContext({
    acquireVsCodeApi: () => ({ postMessage: (message: typeof messages[number]) => messages.push(message) }),
    document: {
      body, activeElement: body,
      getElementById: element, createElement: () => new Element(),
      createTextNode: (text: string) => {
        const node = new Element();
        node.textContent = text;
        return node;
      },
      addEventListener: documentEvents.addEventListener.bind(documentEvents)
    },
    window: { addEventListener: (_type: string, listener: typeof onMessage) => { onMessage = listener; } }
  });
  runInContext(script, sandbox);
  assert.ok(onMessage);
  return { element, messages, html, sandbox, onMessage, documentEvents };
}
