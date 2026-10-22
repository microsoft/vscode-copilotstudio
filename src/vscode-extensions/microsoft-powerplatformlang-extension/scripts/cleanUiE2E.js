const { downloadAndUnzipVSCode } = require('@vscode/test-electron');
const {
  acquireProfileLock,
  allocateRunRoot,
  awaitCompletion,
  buildProduct,
  compileTests,
  driverEnvironment,
  launchWindow,
  loadConfig,
  printTranscript,
} = require('./uiE2ELauncher');

const CLEANUP_TIMEOUT_MS = 15 * 60 * 1000;

const { config, namePrefix } = loadConfig(process.argv[2]);

async function main() {
  acquireProfileLock('test:ui:clean');
  compileTests();
  buildProduct();

  const { suffix, runRoot } = allocateRunRoot('clean');
  const executable = await downloadAndUnzipVSCode();

  console.log('');
  console.log(`environment : ${config.environmentId}`);
  console.log(`deleting    : everything named '${namePrefix}*'`);
  console.log('');

  const child = launchWindow(executable, runRoot, {
    ...driverEnvironment(config, namePrefix, runRoot, suffix),
    MCSUI_CLEANUP_ONLY: '1',
  });

  const { code } = await awaitCompletion(child, runRoot, CLEANUP_TIMEOUT_MS);

  printTranscript(runRoot);

  process.exit(code);
}

main().catch((error) => {
  console.error(error.message ?? error);
  process.exit(1);
});
