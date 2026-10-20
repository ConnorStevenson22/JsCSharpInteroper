# demo-maps patterns

`@acme/demo-maps` is a tiny made-up map library that gives the tests the hard patterns of real libraries to bind ([design §6](../../docs/design.md#6-testing)). Each module is a plain ES module (`X.js`) with hand-written typings (`X.d.ts`). It runs in Node and the browser without a bundler.

```bash
tsc --noEmit -p fixtures/demo-maps             # typings, plus test/typings.check.ts
tsc --noEmit -p fixtures/demo-widgets          # the second package's typings
node --test "fixtures/demo-maps/test/*.test.mjs"
```

## Patterns

| Pattern | Rules ([mapping.md](../../docs/mapping.md)) | Where |
|---|---|---|
| Abstract base with subclasses | R4, R15 | `Shape` → `Point`, `Line`, `Polygon` (`geometry`); the entity `Layer` → `TileLayer` (`layers/`) |
| Runtime class-name property | R19 (step 1) | `Observable.className` (`core`), e.g. `"geometry.Point"` |
| Module folders | R27 | `layers/Layer`, `layers/TileLayer`: the `Layers` namespace |
| Same names in sibling modules | R27 | `ops/a` and `ops/b` each export an `Options` and an `execute(x, options?)` |
| Mixin base | [IR `extends`](../../docs/ir.md#declarations), §2 | `class Pin extends PinBase`, where `PinBase: typeof Marker & typeof Draggable` (`pins`) |
| Exported mixin | [IR `mixins`](../../docs/ir.md#declarations), [§5](../../docs/mapping.md#5-members) | `TileLayer extends TileLayerBase`, where `TileLayerBase: typeof Layer & typeof Refreshable`, with `refresh()` and `sibling(): this` (`layers/Refreshable`) |
| Default export | [IR `export`](../../docs/ir.md#declarations), [design §2](../../docs/design.md#2-what-we-generate) | `export default class TileLayer` (`layers/TileLayer`) |
| Member a subclass redeclares | R26 (`new`) | `TileLayer.clone(): TileLayer` over `Layer.clone(): Layer` |
| Value-like classes (live class + snapshot) | R16–R18 | `Shape` and subclasses, via `profile.json` |
| Class-typed positions need instances, not bags (`$c`) | R16, R20 | `MapView.center`, `Marker.position`, `MapView.flyTo` |
| Data records | R15, R20 | `Bounds` (`geometry`), `Rgba`, `FillStyle`, … (`styles`), `ClickEvent` (`MapView`) |
| Properties, read-only properties | §5, R7 | `MapView.zoom`, `MapView.markers` |
| Getter type ≠ setter type | §5, R27 | `Marker.position`: get `Point`, set `Point \| [number, number]` |
| Discriminated union | R2, R19 (step 2), R20 | `Style` = `FillStyle \| StrokeStyle \| IconStyle` on `type` (`styles`); `Shape.type` |
| Untagged union | R2, R19 (step 3) | `Camera` = `Bounds \| CenterAndZoom` (`MapView`) |
| Mixed union (live object, tuple, nested union) | R2, R5, R6, R19 | `FlyToTarget` = `Shape \| [number, number] \| Camera` (`MapView`) |
| Mixed union (primitive, array, object) | R2, R19 (step 4) | `ColorInput` = `string \| number[] \| Rgba` (`styles`) |
| Union with an entity case, only written | R2, R17 | `Measurable` = `Point \| Polygon \| Marker`, the parameter of `describe` (`measure`) |
| `any` | §2, IOP2001 | `Pin.meta` (`pins`) |
| String-literal union | R9, R25 | `Basemap` (`MapView`), including `"3d-relief"` |
| Literal union with 100+ members | R10, R25 | `IconName`, `"pin-1"` … `"pin-120"` (`styles`) |
| Open-ended literal union | R10 | `Cursor` = known values `\| (string & {})` (`MapView`) |
| Number-literal union | §2 | `ClickEvent.button` (`MapView`) |
| Homogeneous numeric tuples | R5 | `Line.path`, `Polygon.vertices`, `CenterAndZoom.center` |
| Arrays with `null` or `undefined` elements | R3, R17 | `distances(origin, markers: (Marker \| null)[])` returns `(number \| null)[]`, `centers(shapes: Array<Shape \| null \| undefined>)` (`measure`); `MapView.markersAt` returns `(Marker \| null)[]` |
| `null \| undefined` positions | R2, R3 | `MapView.title?: string \| null`, `MapView.cursor?`, `MapView.selected: Marker \| null`, `Marker.label`, `Marker.labelFormatter`, `Style` colors, `Collection.at` |
| DOM element type | R13 | `MapView.container: HTMLElement \| string \| null` (setter-only, a union only written), `MapViewOptions.container` (a record only written) |
| `Partial<Pick<…>>` constructor bags | R12, R18 | `Point`, `Line`, `Polygon` constructors; `MapViewOptions`; `MarkerOptions` (also extends with a wider `position`) |
| Promise methods | R8 | `MapView.flyTo`, `MapView.markerAt` |
| `AbortSignal` parameter | R14 | `FlyToOptions.signal` |
| Optional record member that can't be mapped | R15 | `FlyToOptions.signal`, `MarkerOptions.labelFormatter` (left out) |
| Overloads | R23 | `MapView.screenToMap(x, y)` and `screenToMap([x, y])` |
| JSDoc and manifest docs | R29 | `@default` (`FlyToOptions.duration`, `Rgba.a`, `MapView.basemap`); `@since`, `@deprecated`, `@see` on `screenToMap`; `{@link}` (`MapView.selected`, `MapView.flyTo`); the `<demo-map>` manifest docs |
| Optional parameters | R22 | `MapView.flyTo(target, options?)`, `DemoMapElement.clickAt(x, y, button?)` |
| Rest parameter | R22 | `perimeter(...points)` (`measure`) |
| Callbacks | R21 | `on`, `watch`, `Collection.forEach` (result ignored), `Collection.find` (synchronous `boolean` result, IOP2005 in async mode) |
| Callback with a synchronous return value | R21, IOP2005 | `Marker.labelFormatter`, a setter-only callback property read by `Marker.displayLabel` |
| Event map through a phantom property | R24 | `"$events"` + `on<K extends keyof this["$events"]>` (`core`); `MapViewEvents.click` |
| Subscription with `remove()`, not `dispose()` | R24, [design §3.4](../../docs/design.md#34-protocol) | `Pin.on` (`pins`) |
| Property watch | §5 | `Observable.watch(name, callback)` (`core`) |
| Several watched properties changing in one call | R18, [design §3.4](../../docs/design.md#34-protocol) | `Observable.watch` calls back synchronously on each change; `MapView.reset()` changes `zoom` and `rotation` at once |
| Collection type (generic) | §2 (generic `T`), R17 | `Collection<T>` with a `change` event whose payload holds entities (`core`); `MapView.markers`, and `MapView.layers` of an abstract class |
| Type parameter with a default | §2 | `Tagged<T = any>`, a generic record (`core`) |
| Module functions | §5 | `distance`, `perimeter`, `describe` (`measure`); `toRgba` (`styles`); `execute` (`ops/a`, `ops/b`) |
| Custom element: attributes, properties, events, slots | R28, design §2 | `<demo-map>` (`demo-map`, `custom-elements.json`): attributes `basemap`, `zoom`; properties `view`, `center`; events `demo-map-click`, `demo-map-zoom-change` (`@bind-Zoom`), `demo-map-ready`; default and `toolbar` slots |

The golden slice (`tests/GoldenSlice`) holds the fixture's generated bindings.

## Node and the browser

- `demo-map.js` extends `HTMLElement` if it exists, else `EventTarget`, and registers `<demo-map>` only if `customElements` exists, so it runs under `node --test`.
- `MapView.container` is only stored, never touched, so the view needs no DOM.
- `MapView.emit("click", …)` and `DemoMapElement.clickAt(x, y)` raise clicks without a pointer.

## `profile.json`

The schema is `Profile` in `src/Interoper.Generator/Profile.cs`.

| Field | Meaning |
|---|---|
| `package` | The npm package the profile applies to. |
| `runtimeClassProperty` | Instance property that holds the runtime class name. Maps a JS object to its most-derived C# type. |
| `runtimeClassPrefix` | Prefix added to the package's own runtime class names in the metadata module. Not set here. |
| `valueClasses` | Classes that also get an `XSnapshot` record used by value (R16). Every other class is an entity (R15). |
| `events` | `mapProperty` is the phantom event-map property (`$events`). `subscribe` is the method that takes `(type, listener)` and returns a subscription with `dispose()` or `remove()`. |
| `watch` | Method that takes `(propertyName, callback)` and returns a subscription. Every property of a class that has it gets a watch. |
| `collections` | Generic classes that are live collections of an entity type. No `Create`, no registry entry (mapping §2). |
| `include` | Module specifier globs to emit (`"@acme/demo-maps/geometry.js"`; `*` also matches `/`). Emits each matching module's exports plus the bases and data types they use. Not set here, so every module is emitted. |
