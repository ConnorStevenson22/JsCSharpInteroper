# demo-maps patterns

`@acme/demo-maps` is a tiny made-up map library. It exists only so Interoper's tests have something to bind that reproduces the hard patterns of real libraries ([design §6](../../docs/design.md#6-testing)). Each module is a plain ES module (`X.js`) with hand-written typings next to it (`X.d.ts`). It runs in Node and in the browser without a bundler.

```bash
tsc --noEmit -p fixtures/demo-maps             # typings, plus test/typings.check.ts
node --test "fixtures/demo-maps/test/*.test.mjs"
```

## Patterns

| Pattern | Rules ([mapping.md](../../docs/mapping.md)) | Where |
|---|---|---|
| Abstract base with subclasses | R4, R15 | `Shape` → `Point`, `Line`, `Polygon` (`geometry`) |
| Runtime class-name property | R19 (step 1) | `Observable.className` (`core`), e.g. `"geometry.Point"`, `"MapView"` |
| Value-like classes (live class + snapshot) | R16–R18 | `Shape` and subclasses, via `profile.json` |
| No autocast: class-typed positions need instances ([design §3.4](../../docs/design.md#34-protocol) `$c`) | R16, R20 | `MapView.center`, `Marker.position`, `MapView.flyTo`; constructors accept a bag that keeps its `type` |
| Data records | R15, R20 | `Bounds` (`geometry`), `Rgba`, `FillStyle`, … (`styles`), `ClickEvent` (`MapView`) |
| Properties, read-only properties | §5, R7 | `MapView.zoom`, `MapView.markers` |
| Getter type ≠ setter type | §5, R27 | `Marker.position`: get `Point`, set `Point \| [number, number]` |
| Discriminated union | R2, R19 (step 2), R20 | `Style` = `FillStyle \| StrokeStyle \| IconStyle` on `type` (`styles`); `Shape.type` |
| Untagged union | R2, R19 (step 3) | `Camera` = `Bounds \| CenterAndZoom` (`MapView`) |
| Mixed union (live object, tuple, nested union) | R2, R5, R6, R19 | `FlyToTarget` = `Shape \| [number, number] \| Camera` (`MapView`) |
| Mixed union (primitive, array, object) | R2, R19 (step 4) | `ColorInput` = `string \| number[] \| Rgba` (`styles`) |
| String-literal union | R9, R25 | `Basemap` (`MapView`), including `"3d-relief"` |
| Literal union with 100+ members | R10, R25 | `IconName`, `"pin-1"` … `"pin-120"` (`styles`) |
| Open-ended literal union | R10 | `Cursor` = known values `\| (string & {})` (`MapView`) |
| Number-literal union | §2 | `ClickEvent.button` (`MapView`) |
| Homogeneous numeric tuples | R5 | `Line.path`, `Polygon.vertices`, `CenterAndZoom.center` |
| `null \| undefined` positions | R2, R3 | `MapView.title?: string \| null`, `MapView.selected: Marker \| null`, `Marker.label`, `Marker.labelFormatter`, `Style` colors, `Collection.at` |
| DOM element type | R13 | `MapView.container: HTMLElement \| null` |
| `Partial<Pick<…>>` constructor bags | R12, R18 | `Point`, `Line`, `Polygon` constructors; `MapViewOptions`; `MarkerOptions` (also extends with a wider `position`) |
| Promise methods | R8 | `MapView.flyTo`, `MapView.markerAt` |
| `AbortSignal` parameter | R14 | `FlyToOptions.signal` |
| Optional parameters | R22 | `MapView.flyTo(target, options?)`, `DemoMapElement.clickAt(x, y, button?)` |
| Callbacks | R21 | `on`, `watch`, `Collection.find` (synchronous `boolean` result) |
| Callback with a synchronous return value | R21, IOP2005 | `Marker.labelFormatter` (`LabelFormatter`), read by `Marker.displayLabel` |
| Event map through a phantom property | R24 | `"$events"` + `on<K extends keyof this["$events"]>` (`core`); `MapViewEvents.click` |
| Property watch | design §2 | `Observable.watch(name, callback)` returns a `Subscription` (`core`), e.g. `view.watch("zoom", …)` |
| Collection type (generic) | §2 (generic `T`) | `Collection<T>` with a `change` event (`core`); `MapView.markers` |
| Module functions | §5 | `distance` (`measure`); `toRgba` (`styles`) |
| Custom element: attributes, properties, events, slots | design §2 | `<demo-map>` (`demo-map`, `custom-elements.json`): attributes `basemap`, `zoom`; properties `view`, `center`; events `demo-map-click`, `demo-map-zoom-change` (pairs with `zoom` for `@bind`), `demo-map-ready`; slots default and `toolbar` |

The golden slice binds `Shape`/`Point`/`Line`/`Polygon`, `MapView` (`center`, `zoom`, `flyTo`, `click`), `Marker.position`, `Style`, `Basemap`, `watch("zoom")` and `distance`.

## Node and the browser

- `demo-map.js` extends `HTMLElement` when it exists and `EventTarget` otherwise, and registers `<demo-map>` only when `customElements` exists. So its logic runs under `node --test`.
- `MapView.container` is only stored, never touched, so the view needs no DOM.
- `MapView.emit("click", …)` and `DemoMapElement.clickAt(x, y)` raise clicks without a pointer.

## `profile.json`

Provisional; the generator (plan phase 2) fixes the schema.

| Field | Meaning |
|---|---|
| `package` | The npm package the profile applies to. |
| `runtimeClassProperty` | Instance property holding the runtime class name; maps a JS object to its most-derived C# type. |
| `valueClasses` | Classes that also get an `XSnapshot` record used by value (R16). Every other class is an entity (R15). |
| `events` | Event convention: `mapProperty` is the phantom event-map property (`$events`), `subscribe` the method that takes `(type, listener)` and returns a `Subscription` with `dispose()`. |
| `watch` | Method that takes `(propertyName, callback)` and returns a `Subscription` with `dispose()`. |
| `collections` | Generic classes treated as collections of their type argument. |
