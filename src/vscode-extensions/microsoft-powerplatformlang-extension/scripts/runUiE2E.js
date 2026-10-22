const path = require('node:path');
const fs = require('node:fs');
const { downloadAndUnzipVSCode } = require('@vscode/test-electron');
const {
  acquireProfileLock,
  allocateRunRoot,
  awaitCompletion,
  buildProduct,
  compileTests,
  driverEnvironment,
  extensionRoot,
  fail,
  launchWindow,
  loadConfig,
  printArtifacts,
  printTranscript,
  stopOrphanedLanguageServers,
  userDataDirectory,
} = require('./uiE2ELauncher');

const RUN_TIMEOUT_MS = 60 * 60 * 1000;
const KNOWN_GROUPS = ['all', 'vscode', 'workflow', 'interop', 'smoke'];

const { config, namePrefix } = loadConfig();

const skipCleanup = process.argv.includes('--no-clean');
const groupFlag = process.argv.indexOf('--group');
const groups = groupFlag === -1 ? 'all' : (process.argv[groupFlag + 1] ?? '').trim();

if (!groups || !groups.split(',').every(entry => KNOWN_GROUPS.includes(entry.trim().toLowerCase()))) {
  fail(`--group expects one of ${KNOWN_GROUPS.join(', ')} (comma separated); received '${groups}'`);
}

async function main() {
  acquireProfileLock('test:ui');
  compileTests();
  buildProduct();

  const driverEntry = path.join(extensionRoot, 'client', 'out', 'tests', 'ui', 'driver.js');

  if (!fs.existsSync(driverEntry)) {
    fail(`missing ${driverEntry} — run: npm run compile-tests`);
  }

  const { suffix, runRoot } = allocateRunRoot('');

  for (const relative of ['seed', path.join('work', 'v1'), path.join('work', 'v2')]) {
    fs.mkdirSync(path.join(runRoot, relative), { recursive: true });
  }

  const executable = await downloadAndUnzipVSCode();

  console.log('');
  console.log(`Environment  ${config.environmentId}`);
  console.log(`Run          ${runRoot}`);
  console.log('');
  console.log(`This run creates cloud agents and deletes everything named '${namePrefix}*' when it finishes.`);

  if (skipCleanup) {
    console.log('--no-clean: cleanup is DISABLED for this run, agents will be left behind.');
  }

  console.log('');

  const discardSavedWorkspaces = () => {
    const saved = path.join(userDataDirectory, 'Workspaces');

    if (!fs.existsSync(saved)) {
      return;
    }

    const entries = fs.readdirSync(saved);

    if (entries.length === 0) {
      return;
    }

    console.log(`pre-flight: discarding ${entries.length} saved multi-root workspace(s) from an earlier run — VS Code restores the last one, which would open that run's folders instead of this one's.`);
    fs.rmSync(saved, { recursive: true, force: true });
  };

  stopOrphanedLanguageServers('pre-flight');
  discardSavedWorkspaces();

  const child = launchWindow(executable, runRoot, {
    ...driverEnvironment(config, namePrefix, runRoot, suffix),
    MCSUI_SKIP_CLEANUP: skipCleanup ? '1' : '0',
    MCSUI_GROUPS: groups,
  });

  const { finished, childExited, code } = await awaitCompletion(child, runRoot, RUN_TIMEOUT_MS);

  stopOrphanedLanguageServers('after the run');

  console.log('');

  if (!finished) {
    console.error(childExited
      ? '\nVS Code exited before the run reported a result'
      : `\nthe run did not finish within ${RUN_TIMEOUT_MS / 60000} minutes`);
    printTranscript(runRoot);
  }

  printArtifacts(runRoot);

  process.exit(code);
}

main().catch((error) => {
  console.error(error.message ?? error);
  process.exit(1);
});
