const path = require('node:path');

const extensionRoot = path.resolve(__dirname, '..', '..', '..');
const repoExtensionsRoot = path.resolve(extensionRoot, '..');

const vsCodeTargetOperatingSystems = {
    win32: 'windows',
    darwin: 'macos',
    linux: 'linux'
};

const runtimeIdentifierOperatingSystems = {
    win: 'windows',
    osx: 'macos',
    linux: 'linux'
};

function describe(value, operatingSystems, kind) {
    const parts = value.split('-');

    if (parts.length !== 2) {
        throw new Error(`Malformed ${kind} '${value}'.`);
    }

    const operatingSystem = operatingSystems[parts[0]];

    if (!operatingSystem) {
        throw new Error(`Unknown operating system in ${kind} '${value}'.`);
    }

    return { operatingSystem, architecture: parts[1] };
}

function describeVsCodeTarget(target) {
    return describe(target, vsCodeTargetOperatingSystems, 'VS Code target');
}

function describeRuntimeIdentifier(runtimeIdentifier) {
    return describe(runtimeIdentifier, runtimeIdentifierOperatingSystems, 'runtime identifier');
}

function runtimeMatchesTarget(target, runtimeIdentifier) {
    const describedTarget = describeVsCodeTarget(target);
    const describedRuntime = describeRuntimeIdentifier(runtimeIdentifier);

    return (
        describedTarget.operatingSystem === describedRuntime.operatingSystem &&
        describedTarget.architecture === describedRuntime.architecture
    );
}

module.exports = {
    extensionRoot,
    repoExtensionsRoot,
    describeVsCodeTarget,
    describeRuntimeIdentifier,
    runtimeMatchesTarget
};
