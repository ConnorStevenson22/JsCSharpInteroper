# UnionInterop prototype

Checks that the TypeScript → C# 15 mappings proposed in [`docs/report.md`](../docs/report.md) work through the **real `Microsoft.JSInterop` serializer**, the same code path Blazor's `IJSRuntime` uses, with no browser.

```bash
cd UnionInterop
dotnet run          # needs the .NET 11 SDK (tested on 11.0.100-rc.1)
```

| File | Role |
|---|---|
| `Runtime/JsDiscriminator.cs` | `JsUnionClassifier`: picks a union case by JS handle type (`__jsType`), discriminator value (`type: "point"`), or JSON token kind |
| `Runtime/JsProxy.cs` | Proxies over `IJSObjectReference` that can sit inside DTOs and unions |
| `Runtime/JsOptional.cs` | Three-state `undefined` / `null` / value |
| `Generated/Sample.cs` | Hand-written stand-in for generator output from a small made-up JS library; illustrative TypeScript is quoted above each type |
| `FakeJsRuntime.cs` | A `JSRuntime` that records outgoing JSON and replies through `DotNetDispatcher.EndInvokeJS`, as `blazor.*.js` does |
| `Program.cs` | 18 checks; exits non-zero if any check doesn't behave as documented |
