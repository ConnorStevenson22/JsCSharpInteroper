# value-model spike

The JavaScript value model for generated code: **`undefined` is a real union case, and it is the `default`**.

```bash
dotnet run   # .NET 11 SDK
```

| Piece | Behaviour (verified) |
|---|---|
| `Undefined` | Sealed class with a private constructor and one runtime-owned instance (`Undefined.Value`). Users can't construct it. |
| Generated union (`ColorInput`) | Custom non-boxing `[Union]` struct. Cases are the TypeScript members plus `Undefined?`. `default` is undefined; the `null` literal is JS null; values convert implicitly. |
| `Opt<T>` | The same three states for non-union types in optional positions (`Opt<double>`, `Opt<Geometry>`). |
| Exhaustiveness | A `switch` must cover every case, `Undefined` (else CS8509) and `null` (else CS8655). |
| Properties bags | Records with **no initializers**: unset members are undefined automatically, so sparse patches fall out for free. |
| Wire format | Plain STJ with `WhenWritingDefault` omits undefined and writes `null`: `{"color":[255,0,0,1],"size":null}`. |
| Closed proxy hierarchy | `closed class Geometry` (implicitly abstract; writing `abstract` too is CS9384) makes subclass switches exhaustive, including through `Opt<Geometry>`. |

Two compiler facts drove the design:

1. **`null` never takes a union conversion.** `ColorInput c = null;` is CS0037. A user-defined `implicit operator ColorInput(Undefined? value)` makes the literal compile.
2. **The nullable bottom case (`Undefined?`) is what makes the compiler demand a `null` arm.** With a non-nullable `Undefined` case, a missing `null` arm compiles silently even though `Value` can be null.
