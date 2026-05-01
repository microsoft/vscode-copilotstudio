const path = require('node:path');
const { runTests } = require('@vscode/test-electron');

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

  const launchArgs = [];
  if (process.env.VSCODE_COPILOTSTUDIO_TEST_WORKSPACE) {
    launchArgs.push(process.env.VSCODE_COPILOTSTUDIO_TEST_WORKSPACE);
  }
  launchArgs.push('--disable-extensions');

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
