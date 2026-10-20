import * as vscode from 'vscode';

function getNonce(): string {
  let text = '';
  const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  for (let i = 0; i < 32; i++) {
    text += possible.charAt(Math.floor(Math.random() * possible.length));
  }
  return text;
}

export function buildWebviewHtml(webview: vscode.Webview, extensionUri: vscode.Uri): string {
  const nonce = getNonce();
  const scriptUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'client', 'src', 'workflowVisualization', 'media', 'visualizer.js'),
  );
  const styleUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'client', 'src', 'workflowVisualization', 'media', 'visualizer.css'),
  );
  const iconBaseUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'client', 'src', 'workflowVisualization', 'media', 'icons'),
  );

  const csp = [
    `default-src 'none'`,
    `img-src ${webview.cspSource} https: data:`,
    `style-src ${webview.cspSource}`,
    `script-src 'nonce-${nonce}'`,
    `font-src ${webview.cspSource}`,
  ].join('; ');

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy" content="${csp}" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <link href="${styleUri}" rel="stylesheet" />
  <title>Workflow Visualization</title>
</head>
<body>
  <div id="toolbar">
    <div class="toolbar-group" role="group" aria-label="Graph navigation">
      <button id="btn-fit" title="Fit to view (F)">Fit</button>
      <button id="btn-zoom-out" title="Zoom out (-)" aria-label="Zoom out">&minus;</button>
      <button id="btn-reset" title="Actual size (0)">100%</button>
      <button id="btn-zoom-in" title="Zoom in (+)" aria-label="Zoom in">+</button>
    </div>
  </div>
  <div id="banner" class="hidden" role="status"></div>
  <div id="canvas" tabindex="0" role="region" aria-label="Workflow graph. Drag to pan, scroll to zoom. Use arrow keys to pan, F to fit, and Tab to navigate nodes.">
    <svg id="surface" data-icon-base="${iconBaseUri}" xmlns="http://www.w3.org/2000/svg" role="group" aria-label="Workflow nodes and connections"></svg>
  </div>
  <div id="hint">Drag to pan &middot; Scroll to zoom &middot; Click a node to open its source</div>
  <script nonce="${nonce}" src="${scriptUri}"></script>
</body>
</html>`;
}
