const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { extensionRoot, runtimeMatchesTarget } = require('./helpers/targets');

const extensionProjPath = path.join(extensionRoot, 'extension.proj');
const projectText = fs.readFileSync(extensionProjPath, 'utf8');

function getRuntimeByPublishProperty() {
    const runtimes = new Map();
    const pattern = /<(LSP[A-Za-z0-9]+)>\$\(LanguageServerPublishOutDir\)\\([^\\<]+)\\publish<\/\1>/g;

    for (const match of projectText.matchAll(pattern)) {
        runtimes.set(match[1], match[2]);
    }

    return runtimes;
}

function getPublishPropertyByCopyItem() {
    const properties = new Map();
    const pattern = /<(LSP[A-Za-z0-9]+ToCopy)\s+Include="\$\((LSP[A-Za-z0-9]+)\)/g;

    for (const match of projectText.matchAll(pattern)) {
        properties.set(match[1], match[2]);
    }

    return properties;
}

function getVsixPackSteps() {
    const start = projectText.indexOf('<Target Name="VSIXPack"');

    assert.notEqual(start, -1, 'The VSIXPack target is missing from extension.proj.');

    const body = projectText.slice(start);
    const pattern = /<Copy\s+SourceFiles\s*=\s*"@\((LSP[A-Za-z0-9]+ToCopy)\)"|vsce package --target (\S+)/g;

    const runtimeByProperty = getRuntimeByPublishProperty();
    const propertyByItem = getPublishPropertyByCopyItem();

    const steps = [];
    let stagedRuntime;

    for (const match of body.matchAll(pattern)) {
        const copiedItem = match[1];
        const packagedTarget = match[2];

        if (copiedItem) {
            const property = propertyByItem.get(copiedItem);

            assert.ok(property, `Copy item '${copiedItem}' has no matching publish property.`);

            stagedRuntime = runtimeByProperty.get(property);

            assert.ok(stagedRuntime, `Publish property '${property}' has no runtime identifier.`);
            continue;
        }

        steps.push({ target: packagedTarget, stagedRuntime });
    }

    return steps;
}

test('every packaged VSIX stages the language server built for its own architecture', () => {
    const steps = getVsixPackSteps();

    assert.ok(steps.length > 0, 'No vsce package commands were found in the VSIXPack target.');

    for (const { target, stagedRuntime } of steps) {
        assert.ok(stagedRuntime, `VS Code target '${target}' is packaged without staging a language server binary.`);

        assert.ok(
            runtimeMatchesTarget(target, stagedRuntime),
            `VS Code target '${target}' is packaged with the '${stagedRuntime}' language server, which targets a different OS or architecture. ` +
                'A mismatched binary fails to spawn on the user machine.'
        );
    }
});

test('darwin-arm64 is packaged with the osx-arm64 language server', () => {
    const steps = getVsixPackSteps();
    const darwinArm64 = steps.find((step) => step.target === 'darwin-arm64');

    assert.ok(darwinArm64, 'No darwin-arm64 VSIX is packaged.');
    assert.equal(darwinArm64.stagedRuntime, 'osx-arm64');
});

test('every published runtime identifier is packaged into a VSIX', () => {
    const publishedRuntimes = [...projectText.matchAll(/dotnet publish [^"]*?\s-r\s+(\S+)/g)].map(
        (match) => match[1]
    );
    const packagedRuntimes = new Set(getVsixPackSteps().map((step) => step.stagedRuntime));

    assert.ok(publishedRuntimes.length > 0, 'No dotnet publish commands were found.');

    for (const runtime of publishedRuntimes) {
        assert.ok(
            packagedRuntimes.has(runtime),
            `Runtime '${runtime}' is published but never packaged into a VSIX.`
        );
    }
});
