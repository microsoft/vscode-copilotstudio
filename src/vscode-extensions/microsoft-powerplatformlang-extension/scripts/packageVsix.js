const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const extensionRoot = path.resolve(__dirname, '..');
const repoSrcRoot = path.resolve(extensionRoot, '..', '..');
const languageServerProject = path.join(
    repoSrcRoot,
    'LanguageServers',
    'PowerPlatformLS',
    'LanguageServerHost',
    'LanguageServerHost.csproj'
);
const lspOut = path.join(extensionRoot, 'lspOut');

const targets = {
    'win32-x64': 'win-x64',
    'win32-arm64': 'win-arm64',
    'linux-x64': 'linux-x64',
    'linux-arm64': 'linux-arm64',
    'darwin-x64': 'osx-x64',
    // Keep this aligned with extension.proj: macOS packages currently use the x64 LSP binary for both VSIX targets.
    'darwin-arm64': 'osx-x64',
};

const rawArgs = process.argv.slice(2);
let target = 'win32-x64';
let configuration = 'Release';
let preRelease = true;
let version = '0.0.1';
let out;
let dryRun = false;

function printUsage() {
    console.log(`Usage: npm run package -- [options]

Builds the production extension bundle, publishes the target language server, and packages a VSIX.

Options:
  --target <target>           VS Code target: ${Object.keys(targets).join(', ')} (default: win32-x64)
  --configuration <config>    .NET publish configuration: Debug or Release (default: Release)
  --mode <config>             Alias for --configuration
  --out <path>                Output .vsix path passed to vsce
  --version <version>         Version to stamp on the VSIX (default: 0.0.1)
  --pre-release              Mark package as pre-release (default)
  --no-pre-release           Do not pass --pre-release to vsce
  --dry-run                  Print commands without executing them
  -h, --help                 Show this help
`);
}

function readValue(index, optionName) {
    const value = rawArgs[index + 1];
    if (!value || value.startsWith('--')) {
        throw new Error(`${optionName} requires a value.`);
    }
    return value;
}

for (let i = 0; i < rawArgs.length; i++) {
    const arg = rawArgs[i];
    if (arg === '-h' || arg === '--help') {
        printUsage();
        process.exit(0);
    } else if (arg === '--target') {
        target = readValue(i, arg);
        i++;
    } else if (arg.startsWith('--target=')) {
        target = arg.substring('--target='.length);
    } else if (arg === '--configuration' || arg === '--mode') {
        configuration = readValue(i, arg);
        i++;
    } else if (arg.startsWith('--configuration=')) {
        configuration = arg.substring('--configuration='.length);
    } else if (arg.startsWith('--mode=')) {
        configuration = arg.substring('--mode='.length);
    } else if (arg === '--out') {
        out = readValue(i, arg);
        i++;
    } else if (arg.startsWith('--out=')) {
        out = arg.substring('--out='.length);
    } else if (arg === '--version') {
        version = readValue(i, arg);
        i++;
    } else if (arg.startsWith('--version=')) {
        version = arg.substring('--version='.length);
    } else if (arg === '--pre-release') {
        preRelease = true;
    } else if (arg === '--no-pre-release') {
        preRelease = false;
    } else if (arg === '--dry-run') {
        dryRun = true;
    } else {
        throw new Error(`Unknown option: ${arg}`);
    }
}

const dotnetRuntime = targets[target];
if (!dotnetRuntime) {
    throw new Error(`Unsupported target '${target}'. Supported targets: ${Object.keys(targets).join(', ')}`);
}

if (configuration !== 'Debug' && configuration !== 'Release') {
    throw new Error(`Unsupported configuration '${configuration}'. Use Debug or Release.`);
}

if (!/^\d+\.\d+\.\d+$/.test(version)) {
    throw new Error(`Unsupported version '${version}'. Use a semver value like 0.0.1.`);
}

function commandName(command) {
    return process.platform === 'win32' && (command === 'npm' || command === 'npx')
        ? `${command}.cmd`
        : command;
}

function quote(value) {
    return value.includes(' ') ? `"${value}"` : value;
}

function run(command, args) {
    console.log(`> ${command} ${args.map(quote).join(' ')}`);
    if (dryRun) {
        return;
    }

    // On Windows, npm/npx resolve to .cmd shims. Node >= 20 refuses to spawn
    // .cmd files without shell: true (throws EINVAL). Use the shell on Windows
    // and wrap every argument in double quotes so cmd.exe treats shell
    // metacharacters (e.g. the ^ in @vscode/vsce@^3.3.2) literally.
    const useShell =
        process.platform === 'win32' && (command === 'npm' || command === 'npx');
    const spawnArgs = useShell ? args.map((value) => `"${value}"`) : args;

    const result = spawnSync(commandName(command), spawnArgs, {
        cwd: extensionRoot,
        stdio: 'inherit',
        shell: useShell,
    });

    if (result.error) {
        throw result.error;
    }

    if (result.status !== 0) {
        process.exit(result.status ?? 1);
    }
}

function cleanLspOut() {
    console.log(`> remove ${quote(lspOut)}`);
    if (!dryRun) {
        fs.rmSync(lspOut, { recursive: true, force: true });
    }
}

const vsceArgs = [
    '-y',
    '@vscode/vsce@^3.3.2',
    'package',
    version,
    '--no-update-package-json',
    '--target',
    target,
    '--no-dependencies',
];

if (preRelease) {
    vsceArgs.push('--pre-release');
}

if (out) {
    vsceArgs.push('--out', out);
}

cleanLspOut();
run('dotnet', [
    'publish',
    languageServerProject,
    '-c',
    configuration,
    '-r',
    dotnetRuntime,
    '--self-contained',
    'true',
    '-p:PublishSingleFile=true',
    '-o',
    lspOut,
]);
run('npm', ['run', 'check-types']);
run('node', ['esbuild.js', '--production']);
run('npx', vsceArgs);
