# Common Language Server Protocol Framework (CLaSP)

CLaSP was created by Roslyn team and was initially cloned from [their github](https://github.com/dotnet/roslyn).
You can find documentation, including usage examples, [here](https://github.com/dotnet/roslyn/tree/main/src/Features/LanguageServer/Microsoft.CommonLanguageServerProtocol.Framework).
It is not officially supported as of March 2025.

Our version includes minor changes to remove some unused dependencies:
- `StreamJsonRpc` is replaced by our own definition of IJsonRpcStream interface.
- `Newtonsoft.Json` is removed and classes depending on it are removed: we don't need them.
