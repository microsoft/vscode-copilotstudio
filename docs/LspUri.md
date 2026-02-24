# LSP URI System

## Overview

The LSP URI system provides **type safety** for URI handling in the language server to prevent confusing file paths with URIs and ensure scheme-aware processing. This is the initial implementation that establishes the foundation - additional URI schemes will be implemented as they prove valuable for fixing user bugs or adding features.

**Key Goal**: Eliminate `FilePath`/`LspUri` confusion and provide scheme-aware URI handling throughout the codebase.

## Architecture

:::mermaid
graph TD
    A[LSP JSON Request] --> B[LspUriFactory.FromJsonElement]
    B --> C{Scheme Type}
    C -->|file://| D[FileLspUri]
    C -->|untitled:/git:/ssh:/etc| E[UnsupportedLspUri]
    D --> F[ILanguageProvider.TryGetLanguageForDocument]
    E --> F
    F --> G{IsSupported?}
    G -->|Yes| H[MCS Language Processing by our LSP]
    G -->|No| I[Default Language Handler]
:::

## Current Implementation (Phase 1)

### Supported Schemes
- **`file://`** → `FileLspUri` (full language support)
- **All others** → `UnsupportedLspUri` (routes to default language)

### Core Types

**`LspUri` (abstract)**
- `Uri Uri` - Absolute from JSON RPC
- `string Scheme` - URI scheme (e.g., "file", "git")  
- `string Raw` - Original string for emission
- `bool IsSupported` - Whether scheme is supported

**`FileLspUri`**
- `AsFilePathNormalized()` - Bridge to existing `FilePath` logic
- Full language provider support

**`UnsupportedLspUri`**
- `string Scheme`, `string ReasonCode`
- Routes to default language handler
- Preserves scheme for future implementation

## Key Code Changes

### 1. Factory Entry Point
**File**: `Impl.Core/LSP/Uris/LspUriFactory.cs`

```csharp
// Single entry point for all URI creation
LspUri uri = LspUriFactory.FromJsonElement(jsonElement, logger);
if (uri.IsSupported) { /* handle normally */ }
else { /* route to default */ }
```

### 2. Language Provider Interface
**File**: `Impl.Core/LSP/ILanguageProvider.cs`

```csharp
// OLD: bool TryGetLanguageForDocument(FilePath documentPath, ...)
// NEW: bool TryGetLanguageForDocument(LspUri uri, ...)
```

### 3. Request Routing
**File**: `Impl.Core/LSP/LanguageServer.cs`

```csharp
// OLD: JSON parsing + uri.ToFilePath() + provider.TryGetLanguageForDocument(filepath, out language)
// NEW: var typedLspUri = LspUriFactory.FromJsonElement(parameters, Logger);
//      provider.TryGetLanguageForDocument(typedLspUri, out language)
```

### 4. Context Resolution
**File**: `Impl.Core/LSP/IRequestContextResolver.cs`

- **Pattern**: Convert `System.Uri` → `LspUri` at boundaries
- **Bridge**: `FileLspUri.AsFilePathNormalized()` for workspace operations
- **Fallback**: Unsupported URIs get default language with null workspace

## Scheme Preservation & Collision Handling

**Why Preserve Schemes**: Different logical documents can map to same file path:
- `file:///c:/repo/file.cs` vs `git://repo/file.cs?ref=main`
- `vscode-remote://host/file.cs` vs local `file:///c:/file.cs`
- `untitled:/c%3A/file.cs` vs saved `file:///c:/file.cs`

**Current Approach**: 
- File URIs use normalized path (existing behavior)
- Non-file URIs are no longer present.

## Testing Strategy

### Core Factory Tests
**File**: `UnitTests/.../LspUriFactoryTests.cs`

```csharp
// Verify supported schemes return correct types
Factory_File_Supported("file:///c:/test.mcs") → FileLspUri, IsSupported=true

// Verify unsupported schemes handled gracefully  
Factory_Git_Unsupported("git://repo/file.mcs") → UnsupportedLspUri, ReasonCode=UnsupportedScheme

// Verify logging throttling (one log per scheme per session)
```

### Integration Tests
**File**: `UnitTests/.../LanguageServerTests.cs`

```csharp
// Theory-based test covering 8 unsupported schemes
UnsupportedUriSchemes_ResolveToDefaultLanguage_Async()

// Verifies: unsupported URIs route to default, produce expected warning logs
```

## Development Guidelines

### Adding New Schemes (Future)
1. Add scheme to `SupportedSchemes` in `LspUriFactory.cs`
2. Create new `XyzLspUri` class extending `LspUri`
3. Add factory branch: `"xyz" => new XyzLspUri(uri, scheme)`
4. Update provider logic in `LspLanguageProvider.cs`
5. Add tests for new scheme behavior

### Code Review Checklist
- [ ] No direct `uri.ToFilePath()` calls in routing (use `LspUriFactory`)
- [ ] `FileLspUri.AsFilePathNormalized()` used for workspace operations
- [ ] Unsupported URIs handled gracefully (no exceptions)
- [ ] New URI handling preserves scheme information
- [ ] Tests cover both supported and unsupported paths

### Finding URI Handling Code
**Search patterns**:
- `uri.ToFilePath()` - Legacy direct conversion (should be rare)
- `LspUriFactory.FromJsonElement` - Correct entry points
- `ILanguageProvider.TryGetLanguageForDocument` - Core routing
- `textDocument.uri` in JSON - LSP parameter extraction

## Error Handling

**Pure ErrorObject Pattern**: Factory never throws, always returns `LspUri`

```csharp
// Malformed JSON → UnsupportedLspUri with ReasonCode=ParseError
// Relative URI → UnsupportedLspUri with ReasonCode=NotAbsolute  
// Unknown scheme → UnsupportedLspUri with ReasonCode=UnsupportedScheme
```

**Logging**: Throttled info logs for unsupported schemes (one per scheme per session)

## Migration Status

✅ **Complete**: Factory, core types, language provider signature, request routing
✅ **Complete**: Request context resolver using typed URIs  
🚧 **Ongoing**: Push `LspUri` usage deeper into codebase
🔮 **Future**: Additional scheme implementations (git, ssh, vscode-remote, etc.)

---

**Next Steps**: As specific schemes prove valuable for user scenarios, implement dedicated `XyzLspUri` classes following the established pattern. The foundation supports this with minimal code changes.
