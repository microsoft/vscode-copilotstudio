const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const crypto = require('node:crypto');
const { execFileSync, spawn } = require('node:child_process');
const { stopProcessTree } = require('./processTree');

const extensionRoot = path.resolve(__dirname, '..');
const driverRoot = path.join(extensionRoot, 'client', 'testdriver');
const configPath = path.join(extensionRoot, 'lspe2e.config.json');
const fixturesRoot = path.join(extensionRoot, 'client', 'src', 'tests', 'ui', 'fixtures');
const userDataDirectory = path.join(extensionRoot, '.vscode-test-ui-profile');
const serverBinary = path.join(extensionRoot, 'lspOut', process.platform === 'win32' ? 'LanguageServerHost.exe' : 'LanguageServerHost');
const languageServerSources = path.resolve(extensionRoot, '..', '..', 'LanguageServers');
const extensionBundle = path.join(extensionRoot, 'dist', 'extension.js');
const runsRoot = path.join(os.tmpdir(), 'mcs-lsp-e2e');

const MINIMUM_PREFIX_LENGTH = 3;
const PUBLISHER_PREFIX = 'abc12';
const NAME_PREFIX = 'lspe2e';

const wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

const fail = (message) => {
  console.error(message);
  process.exit(2);
};

const loadConfig = (prefixOverride) => {
  if (!fs.existsSync(configPath)) {
    const example = path.join(extensionRoot, 'lspe2e.config.example.json');

    console.error('');
    console.error('The end-to-end suite needs a config naming the environment it may use.');
    console.error('');
    console.error(`Create   ${configPath}`);
    console.error(`Copy it from   ${example}`);
    console.error('');
    console.error(`  cd ${extensionRoot}`);
    console.error(process.platform === 'win32'
      ? '  copy lspe2e.config.example.json lspe2e.config.json'
      : '  cp lspe2e.config.example.json lspe2e.config.json');
    console.error('');

    if (fs.existsSync(example)) {
      console.error(fs.readFileSync(example, 'utf8').trimEnd());
      console.error('');
    }

    console.error('environmentId identifies the development environment to use.');
    console.error('msagent must be signed in to that environment, since cleanup deletes through it.');
    console.error('');
    fail('setup required: see the end-to-end section of the repository README');
  }

  const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));

  for (const key of ['environmentId']) {
    if (!config[key]) {
      fail(`lspe2e.config.json is missing "${key}"`);
    }
  }

  const namePrefix = String(prefixOverride || NAME_PREFIX).trim();

  if (namePrefix.length < MINIMUM_PREFIX_LENGTH) {
    fail(`refusing to run with namePrefix '${namePrefix}': cleanup deletes every agent whose name starts with it, so at least ${MINIMUM_PREFIX_LENGTH} characters are required`);
  }

  return { config, namePrefix };
};

const allocateRunRoot = (label) => {
  fs.mkdirSync(runsRoot, { recursive: true });

  for (let attempt = 0; attempt < 50; attempt++) {
    const suffix = `${label}${crypto.randomBytes(4).toString('hex')}`;
    const runRoot = path.join(runsRoot, suffix);

    try {
      fs.mkdirSync(runRoot);
      return { suffix, runRoot };
    } catch (error) {
      if (error.code !== 'EEXIST') {
        throw error;
      }
    }
  }

  throw new Error(`could not allocate an unused run directory under ${runsRoot}`);
};

const driverEnvironment = (config, namePrefix, runRoot, suffix) => ({
  MCSUI_RUN_SUFFIX: suffix,
  MCSUI_RUN_ROOT: runRoot,
  MCSUI_FIXTURES: fixturesRoot,
  MCSUI_ENVIRONMENT_ID: config.environmentId,
  MCSUI_PUBLISHER_PREFIX: PUBLISHER_PREFIX,
  MCSUI_NAME_PREFIX: namePrefix,
});

const windowArgs = (runRoot) => [
  runRoot,
  `--user-data-dir=${userDataDirectory}`,
  `--extensionDevelopmentPath=${extensionRoot}`,
  `--extensionDevelopmentPath=${driverRoot}`,
  '--disable-workspace-trust',
  '--skip-release-notes',
  '--skip-welcome',
  '--new-window',
];

const compileTests = () => {
  let tsc;

  try {
    tsc = require.resolve('typescript/bin/tsc');
  } catch {
    fail('typescript is not installed — run: npm install');
  }

  console.log('compiling the driver and scenarios…');

  try {
    execFileSync(process.execPath, [tsc, '-b', 'client'], { cwd: extensionRoot, stdio: 'inherit' });
  } catch {
    fail('the driver and scenarios did not compile; fix the errors above and run again');
  }
};

const newestModification = (root, skipDirectories) => {
  let newest = 0;

  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const candidate = path.join(directory, entry.name);

      if (entry.isDirectory()) {
        if (!skipDirectories.includes(entry.name)) {
          walk(candidate);
        }

        continue;
      }

      newest = Math.max(newest, fs.statSync(candidate).mtimeMs);
    }
  };

  if (fs.existsSync(root)) {
    walk(root);
  }

  return newest;
};

const buildProduct = () => {
  console.log('bundling the extension…');

  try {
    execFileSync(process.execPath, ['esbuild.js'], { cwd: extensionRoot, stdio: 'inherit' });
  } catch {
    fail('the extension bundle did not build; fix the errors above and run again');
  }

  if (!fs.existsSync(extensionBundle)) {
    fail(`${extensionBundle} was not produced, so VS Code would load nothing`);
  }

  if (!fs.existsSync(serverBinary)) {
    fail(`missing ${serverBinary} — the suite would drive a language server that does not exist. Run: npm run buildLsp`);
  }

  if (newestModification(languageServerSources, ['bin', 'obj', '.vs', 'node_modules']) > fs.statSync(serverBinary).mtimeMs) {
    fail(`${serverBinary} is older than its sources, so this run would report on a language server that is not the one you changed. Run: npm run buildLsp`);
  }
};

const lockPath = path.join(userDataDirectory, 'lspe2e.lock');

const trackedChildren = new Set();

const trackChild = (child) => {
  trackedChildren.add(child);
  child.on('exit', () => trackedChildren.delete(child));

  return child;
};

const stopTrackedChildren = () => {
  for (const child of [...trackedChildren]) {
    trackedChildren.delete(child);
    stopProcessTree(child.pid);
  }
};

const lockHolderIsAlive = (holder) => {
  if (!holder || typeof holder.pid !== 'number') {
    return false;
  }

  try {
    process.kill(holder.pid, 0);
    return true;
  } catch (error) {
    return error.code === 'EPERM';
  }
};

const readLockHolder = () => {
  try {
    return JSON.parse(fs.readFileSync(lockPath, 'utf8'));
  } catch {
    return undefined;
  }
};

const discardStaleLock = () => {
  const reclaimed = `${lockPath}.stale-${process.pid}`;

  try {
    fs.renameSync(lockPath, reclaimed);
  } catch (error) {
    if (error.code !== 'ENOENT') {
      throw error;
    }

    return;
  }

  fs.rmSync(reclaimed, { force: true });
};

const acquireProfileLock = (label) => {
  fs.mkdirSync(userDataDirectory, { recursive: true });

  const claim = () => fs.writeFileSync(lockPath, JSON.stringify({ pid: process.pid, label, at: new Date().toISOString() }), { flag: 'wx' });

  for (let attempt = 0; attempt < 5; attempt++) {
    try {
      claim();
      break;
    } catch (error) {
      if (error.code !== 'EEXIST') {
        throw error;
      }
    }

    const holder = readLockHolder();

    if (lockHolderIsAlive(holder)) {
      fail(`another suite invocation (${holder.label}, pid ${holder.pid}) already owns ${userDataDirectory}. `
        + 'VS Code would hand this launch to that window and report a false result, so refusing to start.');
    }

    discardStaleLock();

    if (attempt === 4) {
      fail(`could not take ownership of ${userDataDirectory}: another invocation keeps reclaiming it`);
    }
  }

  const release = () => {
    const holder = readLockHolder();

    if (holder && holder.pid === process.pid) {
      fs.rmSync(lockPath, { force: true });
    }
  };

  process.on('exit', release);
  process.on('SIGINT', () => {
    stopTrackedChildren();
    release();
    process.exit(130);
  });

  return release;
};

const stopOrphanedLanguageServers = (label) => {
  if (process.platform !== 'win32') {
    return;
  }

  let ids = [];

  try {
    const listed = execFileSync('powershell', [
      '-NoProfile', '-NonInteractive', '-Command',
      `Get-CimInstance Win32_Process -Filter "Name='LanguageServerHost.exe'" | Where-Object { $_.ExecutablePath -eq '${serverBinary.replace(/'/g, "''")}' -and -not (Get-Process -Id $_.ParentProcessId -ErrorAction SilentlyContinue) } | ForEach-Object { $_.ProcessId }; exit 0`,
    ], { encoding: 'utf8', windowsHide: true });

    ids = listed.split('\n').map((line) => line.trim()).filter((line) => /^\d+$/.test(line));
  } catch {
    return;
  }

  if (ids.length === 0) {
    return;
  }

  console.log(`${label}: stopping ${ids.length} orphaned language server process(es) whose owning window is gone — a stale server keeps serving its old workspaces over the same pipe, so this run would never see its own.`);

  for (const id of ids) {
    stopProcessTree(id);
  }
};

const launchWindow = (executable, runRoot, environment) => {
  const logFd = fs.openSync(path.join(runRoot, 'vscode.log'), 'a');
  const child = trackChild(spawn(executable, windowArgs(runRoot), {
    stdio: ['ignore', logFd, logFd],
    detached: process.platform !== 'win32',
    env: { ...process.env, ...environment },
  }));

  child.on('exit', () => {
    try {
      fs.closeSync(logFd);
    } catch {
    }
  });

  return child;
};

const createTail = (filePath) => {
  let offset = 0;

  return () => {
    let handle;

    try {
      handle = fs.openSync(filePath, 'r');
    } catch {
      return;
    }

    try {
      const size = fs.fstatSync(handle).size;

      if (size <= offset) {
        return;
      }

      const buffer = Buffer.alloc(size - offset);
      fs.readSync(handle, buffer, 0, buffer.length, offset);
      offset = size;
      process.stdout.write(buffer.toString('utf8'));
    } finally {
      fs.closeSync(handle);
    }
  };
};

const awaitCompletion = async (child, runRoot, timeoutMs) => {
  const donePath = path.join(runRoot, 'done');
  const drain = createTail(path.join(runRoot, 'summary.log'));
  let childExited = false;
  child.on('exit', () => { childExited = true; });

  const deadline = Date.now() + timeoutMs;

  while (!fs.existsSync(donePath) && Date.now() < deadline && !childExited) {
    drain();
    await wait(1000);
  }

  const finished = fs.existsSync(donePath);
  const reported = finished ? Number(fs.readFileSync(donePath, 'utf8').trim()) : 1;

  if (!childExited) {
    stopTrackedChildren();

    for (let attempt = 0; attempt < 15 && !childExited; attempt++) {
      await wait(1000);
    }
  }

  drain();

  return { finished, childExited, code: finished && Number.isFinite(reported) ? reported : 1 };
};

const printTranscript = (runRoot) => {
  const transcriptPath = path.join(runRoot, 'console.log');

  if (fs.existsSync(transcriptPath)) {
    console.log('');
    console.log(fs.readFileSync(transcriptPath, 'utf8'));
  }
};

const printArtifacts = (runRoot) => {
  console.log('');
  console.log(`  ${path.join(runRoot, 'console.log')}`);
};

module.exports = {
  MINIMUM_PREFIX_LENGTH,
  acquireProfileLock,
  allocateRunRoot,
  awaitCompletion,
  buildProduct,
  compileTests,
  driverEnvironment,
  extensionRoot,
  fail,
  fixturesRoot,
  launchWindow,
  loadConfig,
  printArtifacts,
  printTranscript,
  stopOrphanedLanguageServers,
  stopProcessTree,
  trackChild,
  userDataDirectory,
  wait,
  windowArgs,
};
