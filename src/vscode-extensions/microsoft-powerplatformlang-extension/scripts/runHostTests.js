const path = require('node:path');
const { runTests } = require('@vscode/test-electron');

// Default test fixture: same workspace the previous .vscode-test.mjs config opened.
// Several host tests (e.g. originalState.test.ts) require a workspace folder.
const DEFAULT_TEST_WORKSPACE = path.resolve(
  __dirname,
  '..',
  '..',
  '..',
  'LanguageServers',
  'PowerPlatformLS',
  'UnitTests',
  'PowerPlatformLS.UnitTests',
  'TestData',
  'WorkspaceWithSubAgents',
);

async function main() {
  const extensionDevelopmentPath = path.resolve(__dirname, '..');
  const extensionTestsPath = path.resolve(
    __dirname,
    '..',
    'client',
    'out',
    'tests',
    'host',
    'runner.js',
  );

  const workspacePath =
    process.env.VSCODE_COPILOTSTUDIO_TEST_WORKSPACE || DEFAULT_TEST_WORKSPACE;

  const launchArgs = [workspacePath, '--disable-extensions'];

  const exitCode = await runTests({
    extensionDevelopmentPath,
    extensionTestsPath,
    launchArgs,
  });

  process.exit(exitCode);
}

main().catch((err) => {
  console.error('Host test driver failed:', err);
  process.exit(1);
});
