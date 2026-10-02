const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

const { repoExtensionsRoot, runtimeMatchesTarget } = require('./helpers/targets');

const { getHostRuntimeIdentifier } = require(
    path.join(repoExtensionsRoot, 'shared', 'scripts', 'hostRuntime.js')
);

function withHost(platform, architecture, action) {
    const originalPlatform = Object.getOwnPropertyDescriptor(process, 'platform');
    const originalArch = Object.getOwnPropertyDescriptor(process, 'arch');

    Object.defineProperty(process, 'platform', { value: platform, configurable: true });
    Object.defineProperty(process, 'arch', { value: architecture, configurable: true });

    try {
        return action();
    } finally {
        Object.defineProperty(process, 'platform', originalPlatform);
        Object.defineProperty(process, 'arch', originalArch);
    }
}

const supportedHosts = [
    { platform: 'win32', architecture: 'x64', expected: 'win-x64' },
    { platform: 'win32', architecture: 'arm64', expected: 'win-arm64' },
    { platform: 'darwin', architecture: 'x64', expected: 'osx-x64' },
    { platform: 'darwin', architecture: 'arm64', expected: 'osx-arm64' },
    { platform: 'linux', architecture: 'x64', expected: 'linux-x64' },
    { platform: 'linux', architecture: 'arm64', expected: 'linux-arm64' }
];

for (const { platform, architecture, expected } of supportedHosts) {
    test(`${platform}-${architecture} resolves to the ${expected} runtime identifier`, () => {
        const runtimeIdentifier = withHost(platform, architecture, getHostRuntimeIdentifier);

        assert.equal(runtimeIdentifier, expected);
        assert.ok(
            runtimeMatchesTarget(`${platform}-${architecture}`, runtimeIdentifier),
            `Host ${platform}-${architecture} resolved to '${runtimeIdentifier}', which targets a different OS or architecture.`
        );
    });
}

test('an unsupported host fails instead of silently building another architecture', () => {
    assert.throws(
        () => withHost('aix', 'ppc64', getHostRuntimeIdentifier),
        /Unsupported host platform 'aix-ppc64'/
    );
});

test('the current host resolves to a usable runtime identifier', () => {
    const runtimeIdentifier = getHostRuntimeIdentifier();

    assert.ok(
        runtimeMatchesTarget(`${process.platform}-${process.arch}`, runtimeIdentifier),
        `Host ${process.platform}-${process.arch} resolved to '${runtimeIdentifier}'.`
    );
});
