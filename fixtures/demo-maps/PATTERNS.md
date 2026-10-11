# demo-maps patterns

`@acme/demo-maps` is a tiny made-up map library. It exists only so Interoper's tests have something to bind that reproduces the hard patterns of real libraries ([design §6](../../docs/design.md#6-testing)). Each module is a plain ES module (`X.js`) with hand-written typings next to it (`X.d.ts`). It runs in Node and in the browser without a bundler.

```bash
tsc --noEmit -p fixtures/demo-maps             # typings, plus test/typings.check.ts
tsc --noEmit -p fixtures/demo-widgets          # the second package's typings
node --test "fixtures/demo-maps/test/*.test.mjs"
```

## Patterns

| Pattern | Rules ([mapping.md](../../docs/mapping.md)) | Where |
|---|---|---|
| Abstract base with subclasses | R4, R15 | `Shape` → `Point`, `Line`, `Polygon` (`geometry`); the entity `Layer` → `TileLayer` (`layers/`) |
| Runtime class-name property | R19 (step 1) | `Observable.className` (`core`), e.g. `"geometry.Point"`, `"MapView"`, `"layers.TileLayer"` |
| Module folders | R27 | `layers/Layer`, `layers/TileLayer`: the `Layers` namespace |
| Same names in sibling modules | R27 | `ops/a` and `ops/b` each export an `Options` and an `execute(x, options?)` |
| Mixin base | [IR `extends`](../../docs/ir.md#declarations), §2 | `class Pin extends PinBase`, where `PinBase: typeof Marker & typeof Draggable` (`pins`): a `Marker`, with the mixin's `dragBy` and `drag` event |
| Default export | [IR `export`](../../docs/ir.md#declarations), [design §2](../../docs/design.md#2-what-we-generate) | `export default class TileLayer` (`layers/TileLayer`) |
| Member a subclass redeclares | R26 (`new`) | `TileLayer.clone(): TileLayer` over `Layer.clone(): Layer` |
| Value-like classes (live class + snapshot) | R16–R18 | `Shape` and subclasses, via `profile.json` |
| No autocast: class-typed positions need instances ([design §3.4](../../docs/design.md#34-protocol) `$c`) | R16, R20 | `MapView.center`, `Marker.position`, `MapView.flyTo`; constructors accept a bag that keeps its `type` |
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
| `null \| undefined` positions | R2, R3 | `MapView.title?: string \| null`, `MapView.cursor?`, `MapView.selected: Marker \| null`, `Marker.label`, `Marker.labelFormatter`, `Style` colors, `Collection.at` |
| DOM element type | R13 | `MapView.container: HTMLElement \| null` (setter-only), `MapViewOptions.container` (a record only written) |
| `Partial<Pick<…>>` constructor bags | R12, R18 | `Point`, `Line`, `Polygon` constructors; `MapViewOptions`; `MarkerOptions` (also extends with a wider `position`) |
| Promise methods | R8 | `MapView.flyTo`, `MapView.markerAt` |
| `AbortSignal` parameter | R14 | `FlyToOptions.signal` |
| Optional record member that can't be mapped | R15 | `FlyToOptions.signal` (R14), `MarkerOptions.labelFormatter` (a callback, R21): the records leave them out |
| Overloads | R23 | `MapView.screenToMap(x, y)` and `screenToMap([x, y])` |
| JSDoc and manifest docs | R29 | `@default` (`FlyToOptions.duration`, `Rgba.a`, `MapView.basemap` alone); `@since`, `@deprecated`, `@see` on the `screenToMap` overloads; `{@link}` with and without text, and two lines (`MapView.selected`, `MapView.flyTo`); the `<demo-map>` manifest's descriptions and defaults |
| Optional parameters | R22 | `MapView.flyTo(target, options?)`, `DemoMapElement.clickAt(x, y, button?)` |
| Rest parameter | R22 | `perimeter(...points)` (`measure`) |
| Callbacks | R21 | `on`, `watch`, `Collection.forEach` (result ignored), `Collection.find` (synchronous `boolean` result, IOP2005 in async mode) |
| Callback with a synchronous return value | R21, IOP2005 | `Marker.labelFormatter` (`LabelFormatter`), a setter-only callback property read by `Marker.displayLabel` |
| Event map through a phantom property | R24 | `"$events"` + `on<K extends keyof this["$events"]>` (`core`); `MapViewEvents.click` |
| Subscription with `remove()`, not `dispose()` | R24, [design §3.4](../../docs/design.md#34-protocol) | `Pin.on` (`pins`) |
| Property watch | design §2 | `Observable.watch(name, callback)` returns a `Subscription` (`core`), e.g. `view.watch("zoom", …)` |
| Collection type (generic) | §2 (generic `T`), R17 | `Collection<T>` with a `change` event whose payload holds entities (`core`); `MapView.markers`, and `MapView.layers` of an abstract class |
| Type parameter with a default | §2 | `Tagged<T = any>`, a generic record (`core`) |
| Module functions | §5 | `distance`, `perimeter`, `describe` (`measure`); `toRgba` (`styles`); `execute` (`ops/a`, `ops/b`) |
| Custom element: attributes, properties, events, slots | R28, design §2 | `<demo-map>` (`demo-map`, `custom-elements.json`): attributes `basemap`, `zoom`; properties `view`, `center`; events `demo-map-click`, `demo-map-zoom-change` (pairs with `zoom` for `@bind`), `demo-map-ready`; slots default and `toolbar` |

The golden slice (`tests/GoldenSlice`) is the fixture's generated bindings: every declaration the generator can map.

## Node and the browser

- `demo-map.js` extends `HTMLElement` when it exists and `EventTarget` otherwise, and registers `<demo-map>` only when `customElements` exists. So its logic runs under `node --test`.
- `MapView.container` is only stored, never touched, so the view needs no DOM.
- `MapView.emit("click", …)` and `DemoMapElement.clickAt(x, y)` raise clicks without a pointer.

## `profile.json`

The generator's `Profile` (`src/Interoper.Generator/Profile.cs`) is the schema.

| Field | Meaning |
|---|---|
| `package` | The npm package the profile applies to. |
| `runtimeClassProperty` | Instance property holding the runtime class name; maps a JS object to its most-derived C# type. |
| `runtimeClassPrefix` | Prepended to the runtime class names of the package's own classes in the metadata module, for libraries whose names start with a namespace; another package's classes keep theirs. Absent, as here, means none. |
| `valueClasses` | Classes that also get an `XSnapshot` record used by value (R16). Every other class is an entity (R15). |
| `events` | Event convention: `mapProperty` is the phantom event-map property (`$events`), `subscribe` the method that takes `(type, listener)` and returns a `Subscription` with `dispose()` (`Pin.on`'s has `remove()` instead). |
| `watch` | Method that takes `(propertyName, callback)` and returns a `Subscription` with `dispose()`. Every property of a class that has it gets a watch. |
| `collections` | Generic classes treated as live collections of their type argument, an entity: no `Create`, no registry entry (mapping §2). |
| `include` | What to emit: module specifier globs (`"@acme/demo-maps/geometry.js"`; `*` matches any characters, `/` included). Every exported declaration of a matching module, the bases of its classes and the data types they use are emitted. Absent, as here, means every module. |
