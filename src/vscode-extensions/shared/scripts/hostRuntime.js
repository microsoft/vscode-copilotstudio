const hostRuntimeIdentifiers = {
    win32: { x64: 'win-x64', arm64: 'win-arm64' },
    darwin: { x64: 'osx-x64', arm64: 'osx-arm64' },
    linux: { x64: 'linux-x64', arm64: 'linux-arm64' },
};

/**
 * Resolves the .NET runtime identifier matching the machine running this script.
 *
 * @returns {string} The host .NET runtime identifier, for example `osx-arm64`.
 * @throws {Error} If the host platform/architecture combination is not supported.
 */
function getHostRuntimeIdentifier() {
    const runtime = hostRuntimeIdentifiers[process.platform]?.[process.arch];

    if (!runtime) {
        throw new Error(
            `Unsupported host platform '${process.platform}-${process.arch}'. Pass --target <runtime-identifier> explicitly.`
        );
    }

    return runtime;
}

module.exports = {
    getHostRuntimeIdentifier
};
