const { test } = require('node:test');
const assert = require('node:assert/strict');
const Module = require('node:module');
const path = require('node:path');

const { extensionRoot, repoExtensionsRoot, runtimeMatchesTarget } = require('./helpers/targets');

const buildScripts = [
    {
        name: 'buildLsp',
        scriptPath: path.join(extensionRoot, 'scripts', 'buildLsp.js'),
        sharedPath: path.join(repoExtensionsRoot, 'shared', 'scripts', 'buildLsp.js'),
        exportName: 'buildLanguageServer'
    },
    {
        name: 'watchLsp',
        scriptPath: path.join(extensionRoot, 'scripts', 'watchLsp.js'),
        sharedPath: path.join(repoExtensionsRoot, 'shared', 'scripts', 'watchLsp.js'),
        exportName: 'watchLanguageServer'
    }
];

function stubModule(filename, exports) {
    const stub = new Module(filename, null);

    stub.filename = filename;
    stub.loaded = true;
    stub.exports = exports;
    require.cache[filename] = stub;
}

function resolveTarget({ scriptPath, sharedPath, exportName }, { platform, architecture, args = [] }) {
    const resolvedScript = require.resolve(scriptPath);
    const resolvedShared = require.resolve(sharedPath);

    const originalArgv = process.argv;
    const originalPlatform = Object.getOwnPropertyDescriptor(process, 'platform');
    const originalArch = Object.getOwnPropertyDescriptor(process, 'arch');
    const originalShared = require.cache[resolvedShared];

    let captured;

    stubModule(resolvedShared, {
        [exportName]: (_serverName, _projectPath, _outputPath, target) => {
            captured = target;
        }
    });

    process.argv = [process.execPath, resolvedScript, ...args];
    Object.defineProperty(process, 'platform', { value: platform, configurable: true });
    Object.defineProperty(process, 'arch', { value: architecture, configurable: true });

    try {
        delete require.cache[resolvedScript];
        require(resolvedScript);
    } finally {
        delete require.cache[resolvedScript];

        if (originalShared) {
            require.cache[resolvedShared] = originalShared;
        } else {
            delete require.cache[resolvedShared];
        }

        process.argv = originalArgv;
        Object.defineProperty(process, 'platform', originalPlatform);
        Object.defineProperty(process, 'arch', originalArch);
    }

    return captured;
}

const hosts = [
    { platform: 'win32', architecture: 'x64', expected: 'win-x64' },
    { platform: 'win32', architecture: 'arm64', expected: 'win-arm64' },
    { platform: 'darwin', architecture: 'x64', expected: 'osx-x64' },
    { platform: 'darwin', architecture: 'arm64', expected: 'osx-arm64' },
    { platform: 'linux', architecture: 'x64', expected: 'linux-x64' },
    { platform: 'linux', architecture: 'arm64', expected: 'linux-arm64' }
];

for (const script of buildScripts) {
    for (const { platform, architecture, expected } of hosts) {
        test(`${script.name} defaults to ${expected} on a ${platform}-${architecture} host`, () => {
            const target = resolveTarget(script, { platform, architecture });

            assert.equal(target, expected);
            assert.ok(
                runtimeMatchesTarget(`${platform}-${architecture}`, target),
                `${script.name} on ${platform}-${architecture} would build '${target}', which cannot run on the host.`
            );
        });
    }

    test(`${script.name} still honors an explicit --target`, () => {
        const target = resolveTarget(script, {
            platform: 'darwin',
            architecture: 'arm64',
            args: ['--target', 'linux-x64']
        });

        assert.equal(target, 'linux-x64');
    });
}
