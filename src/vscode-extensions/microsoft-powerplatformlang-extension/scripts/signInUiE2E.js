const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { spawn } = require('node:child_process');
const { downloadAndUnzipVSCode } = require('@vscode/test-electron');
const { acquireProfileLock, buildProduct, extensionRoot, loadConfig, trackChild, userDataDirectory } = require('./uiE2ELauncher');

const { config } = loadConfig();

async function main() {
  acquireProfileLock('test:ui:signin');
  buildProduct();

  const scratch = path.join(os.tmpdir(), 'mcs-lsp-e2e', 'signin');
  fs.mkdirSync(scratch, { recursive: true });

  const executable = await downloadAndUnzipVSCode();

  console.log('');
  console.log('A VS Code window will open with the Copilot Studio extension loaded.');
  console.log('');
  console.log('  1. Run  Copilot Studio: Sign in  from the command palette and complete it.');
  console.log('  2. Run  Copilot Studio: Clone agent  and clone any agent from');
  console.log(`     environment ${config.environmentId} into the opened folder.`);
  console.log('  3. Approve every "wants to sign in using Microsoft" prompt along the way.');
  console.log('  4. Close the window.');
  console.log('');
  console.log('Step 2 matters as much as step 1: the suite needs cached sessions for three');
  console.log('different audiences — BAP, Dataverse and Copilot Studio — and a clone is the');
  console.log('one action that exercises all three. Signing in alone grants only the first.');
  console.log('');
  console.log('VS Code refuses modal dialogs while running tests, so these consents cannot');
  console.log('be given from inside a run.');
  console.log('');
  console.log(`profile: ${userDataDirectory}`);
  console.log(`folder : ${scratch}`);
  console.log('');

  const child = trackChild(spawn(executable, [
    scratch,
    `--user-data-dir=${userDataDirectory}`,
    `--extensionDevelopmentPath=${extensionRoot}`,
    '--disable-workspace-trust',
    '--skip-release-notes',
    '--skip-welcome',
  ], { stdio: 'inherit', detached: process.platform !== 'win32' }));

  child.on('exit', (code) => {
    console.log('');
    console.log('window closed — now run:  npm run test:ui');
    process.exit(code ?? 0);
  });
}

main().catch((error) => {
  console.error(error.message ?? error);
  process.exit(1);
});
