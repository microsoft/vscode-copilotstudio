const { test } = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const path = require('node:path');

const { extensionRoot, runtimeMatchesTarget } = require('./helpers/targets');

const packageScript = path.join(extensionRoot, 'scripts', 'packageVsix.js');

function runPackageScript(args) {
    const result = spawnSync(process.execPath, [packageScript, ...args], {
        cwd: extensionRoot,
        encoding: 'utf8'
    });

    if (result.error) {
        throw result.error;
    }

    return result;
}

function getSupportedTargets() {
    const result = runPackageScript(['--help']);

    assert.equal(result.status, 0, `--help failed: ${result.stderr}`);

    const match = /VS Code target: (.+?) \(default/.exec(result.stdout);

    assert.ok(match, 'Could not read the supported target list from --help output.');

    return match[1].split(',').map((value) => value.trim());
}

function getPublishRuntime(target) {
    const result = runPackageScript(['--target', target, '--dry-run']);

    assert.equal(result.status, 0, `Dry run for '${target}' failed: ${result.stderr}`);

    const publishLine = result.stdout
        .split(/\r?\n/)
        .find((line) => line.startsWith('> dotnet publish'));

    assert.ok(publishLine, `No dotnet publish command was planned for '${target}'.`);

    const runtimeMatch = /\s-r\s+(\S+)/.exec(publishLine);

    assert.ok(runtimeMatch, `No runtime identifier was passed to dotnet publish for '${target}'.`);

    return { runtimeIdentifier: runtimeMatch[1], stdout: result.stdout };
}

test('every supported VS Code target publishes a language server for its own architecture', () => {
    const targets = getSupportedTargets();

    assert.ok(targets.length > 0, 'No supported targets were reported.');

    for (const target of targets) {
        const { runtimeIdentifier } = getPublishRuntime(target);

        assert.ok(
            runtimeMatchesTarget(target, runtimeIdentifier),
            `VS Code target '${target}' publishes runtime '${runtimeIdentifier}', which targets a different OS or architecture. ` +
                'A mismatched binary fails to spawn on the user machine.'
        );
    }
});

test('darwin-arm64 publishes the osx-arm64 language server', () => {
    const { runtimeIdentifier } = getPublishRuntime('darwin-arm64');

    assert.equal(runtimeIdentifier, 'osx-arm64');
});

test('darwin-x64 publishes the osx-x64 language server', () => {
    const { runtimeIdentifier } = getPublishRuntime('darwin-x64');

    assert.equal(runtimeIdentifier, 'osx-x64');
});

test('the planned vsce package command uses the requested target', () => {
    const { stdout } = getPublishRuntime('darwin-arm64');

    const vsceLine = stdout.split(/\r?\n/).find((line) => line.includes('vsce'));

    assert.ok(vsceLine, 'No vsce command was planned.');
    assert.match(vsceLine, /--target darwin-arm64\b/);
});

test('an unsupported target is rejected', () => {
    const result = runPackageScript(['--target', 'ios-x64', '--dry-run']);

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Unsupported target 'ios-x64'/);
});
