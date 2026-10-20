import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { extract } from "../extract.mjs";

/** A temporary folder, removed after the test `t`. */
function tempDir(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

const fixture = fileURLToPath(new URL("../../../fixtures/demo-maps", import.meta.url));
const ir = extract(fixture, { eventMap: "$events" });

const decl = (module, name) => {
  const m = ir.modules.find((x) => x.specifier === `@acme/demo-maps/${module}.js`);
  return m.declarations.find((d) => d.name === name);
};
const member = (module, type, name) => decl(module, type).members.find((m) => m.name === name);
const num = { kind: "primitive", name: "number" };
const ref = (module, name, args) => ({ kind: "ref", name, module: `@acme/demo-maps/${module}.js`, ...(args && { args }) });
const pair = { kind: "tuple", elements: [num, num] };

test("modules are keyed by specifier, declarations keep source order", () => {
  assert.equal(ir.version, 1);
  assert.deepEqual(ir.package, { name: "@acme/demo-maps", version: "0.0.0" });
  assert.deepEqual(ir.modules.map((m) => m.specifier.slice("@acme/demo-maps/".length)), ["MapView.js", "Marker.js", "core.js", "demo-map.js", "geometry.js", "layers/Layer.js", "layers/Refreshable.js", "layers/TileLayer.js", "measure.js", "ops/a.js", "ops/b.js", "pins.js", "styles.js"]);
  assert.deepEqual(ir.modules.find((m) => m.specifier.endsWith("/geometry.js")).declarations.map((d) => `${d.kind} ${d.name}`),
    ["alias ShapeType", "interface Bounds", "class Shape", "class Point", "class Line", "class Polygon"]);
});

test("a default export keeps its declaration's name, and says its export", () => {
  const tile = decl("layers/TileLayer", "TileLayer");
  assert.equal(tile.export, "default");
  assert.equal(decl("layers/Layer", "Layer").export, undefined); // a named export is the declaration's name
  // References to it use the declaration's name too, its own and other modules' alike.
  assert.deepEqual(member("layers/TileLayer", "TileLayer", "clone").signatures[0].returns, ref("layers/TileLayer", "TileLayer"));
  assert.deepEqual(tile.extends, ref("layers/Layer", "Layer"));
});

test("§5: an accessor pair keeps the getter's and the setter's type", () => {
  const position = member("Marker", "Marker", "position");
  assert.deepEqual(position.type, ref("geometry", "Point"));
  assert.deepEqual(position.writeType, { kind: "union", types: [ref("geometry", "Point"), pair] });
  assert.equal(position.readonly, undefined);
  assert.equal(member("Marker", "Marker", "displayLabel").readonly, true);
});

test("R24: events come from the event map, resolved per class", () => {
  assert.deepEqual(decl("MapView", "MapView").events, [{ name: "click", type: ref("MapView", "ClickEvent"), doc: { summary: "A click on the map, after the view has handled it." } }]);
  assert.deepEqual(decl("core", "Collection").events, [{ name: "change", type: ref("core", "CollectionChangeEvent", [{ kind: "typeParameter", name: "T" }]) }]);
  assert.equal(decl("core", "Emitter").events, undefined); // `$events: object` declares none
  assert.equal(member("MapView", "MapView", "$events"), undefined); // the phantom property isn't a member
});

test("R19: a union of records gets its discriminant", () => {
  assert.deepEqual(decl("styles", "Style").type, {
    kind: "union",
    types: [ref("styles", "FillStyle"), ref("styles", "StrokeStyle"), ref("styles", "IconStyle")],
    discriminant: "type",
  });
  assert.equal(decl("MapView", "Camera").type.discriminant, undefined);
});

test("R19: a union of intersections with a literal gets its discriminant", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), `
    export interface Heat { radius: number }
    export interface Grid { size: number }
    export type Grouping = (Heat & { type: "heat" }) | (Grid & { type: "grid" });`);
  const [{ declarations: [, , grouping] }] = extract(dir).modules;
  assert.equal(grouping.type.discriminant, "type");
  assert.deepEqual(grouping.type.types.map((c) => c.kind), ["object", "object"]); // R11: resolved
});

test("R6: unions keep written order and named members", () => {
  assert.deepEqual(decl("MapView", "FlyToTarget").type.types, [ref("geometry", "Shape"), pair, ref("MapView", "Camera")]);
  assert.deepEqual(member("MapView", "MapView", "title"), { kind: "property", name: "title", optional: true, type: { kind: "union", types: [{ kind: "primitive", name: "string" }, { kind: "primitive", name: "null" }] } });
});

test("R12, R18: Partial<Pick<…>> constructor bags are resolved by the checker", () => {
  const [ctor] = decl("geometry", "Point").constructors;
  assert.deepEqual(ctor.params, [{
    name: "options",
    optional: true,
    type: { kind: "object", members: [
      { kind: "property", name: "x", optional: true, inherited: true, type: num },
      { kind: "property", name: "y", optional: true, inherited: true, type: num },
    ] },
  }]);
  // A named alias of a utility type stays a reference; its declaration holds the resolved members.
  assert.deepEqual(decl("MapView", "MapView").constructors[0].params[0].type, ref("MapView", "MapViewOptions"));
  assert.deepEqual(decl("MapView", "MapViewOptions").type.members.map((m) => m.name), ["center", "zoom", "rotation", "basemap", "cursor", "title", "container"]);
  assert.deepEqual(decl("Marker", "MarkerOptions").members.map((m) => `${m.name}${m.inherited ? " (inherited)" : ""}`),
    ["position", "label (inherited)", "style (inherited)", "labelFormatter (inherited)"]);
});

test("R23: overloads are separate signatures, each with its own JSDoc", () => {
  const { signatures, doc } = member("MapView", "MapView", "screenToMap");
  assert.equal(doc, undefined); // a method's docs are on its signatures
  assert.deepEqual(signatures.map((s) => s.params.map((p) => p.name)), [["x", "y"], ["offset"]]);
  assert.deepEqual(signatures[1].params[0].type, pair);
  assert.deepEqual(signatures.map((s) => s.doc), [
    { summary: "Screen offset (pixels from the center) to map coordinates.", since: "0.2" },
    { summary: "The offset as an `[x, y]` pair.", deprecated: "Pass `x` and `y`.", see: ["MapView.flyTo"] },
  ]);
});

test("JSDoc: summary and @default", () => {
  assert.deepEqual(member("MapView", "FlyToOptions", "duration").doc, { summary: "Milliseconds.", default: "0" });
  assert.deepEqual(member("styles", "Rgba", "a").doc, { summary: "0–1.", default: "1" });
  assert.deepEqual(member("MapView", "MapView", "basemap").doc, { default: '"plain"' });
});

test("JSDoc: {@link} is spelled {@link Name} or {@link Name text}, and line breaks stay (R29)", () => {
  assert.deepEqual(member("MapView", "MapView", "selected").doc, { summary: "The {@link Marker} the user picked last, or `null` when none is." });
  assert.deepEqual(member("MapView", "MapView", "flyTo").signatures[0].doc, {
    summary: "Animates to {@link FlyToTarget a shape, a point or a camera}.\nResolves when the view has arrived & settled.",
    since: "0.3",
  });
});

test("classes: base, abstract, own members, generics, statics", () => {
  const shape = decl("geometry", "Shape");
  assert.equal(shape.abstract, true);
  assert.deepEqual(shape.extends, ref("core", "Observable"));
  assert.equal(member("geometry", "Shape", "type").abstract, true);
  assert.deepEqual(member("geometry", "Point", "type"), { kind: "property", name: "type", readonly: true, type: { kind: "literal", value: "point" } }); // no docs copied from Shape
  // Members a named base declares stay with it; `this` stays `this`.
  assert.deepEqual(decl("geometry", "Point").members.map((m) => m.name), ["type", "x", "y"]);
  assert.deepEqual(member("geometry", "Shape", "clone").signatures[0].returns, { kind: "this" });
  assert.deepEqual(decl("core", "Collection").typeParameters, [{ name: "T" }]);
  assert.deepEqual(decl("core", "Tagged").typeParameters, [{ name: "T", default: { kind: "primitive", name: "any" } }]); // §2: a default
  assert.deepEqual(member("MapView", "MapView", "markers").type, ref("core", "Collection", [ref("Marker", "Marker")]));
  // Members declared outside the package (HTMLElement's) are left out; the base is a platform reference.
  const element = decl("demo-map", "DemoMapElement");
  assert.deepEqual(element.extends, { kind: "ref", name: "HTMLElement" });
  assert.deepEqual(element.members.map((m) => `${m.static ? "static " : ""}${m.name}`), ["view", "basemap", "zoom", "center", "clickAt", "static observedAttributes"]);
});

test("a mixin's base: the class it starts with, and the mixin's members as inherited", () => {
  // `class Pin extends PinBase`, where `PinBase: typeof Marker & typeof Draggable`.
  const pin = decl("pins", "Pin");
  assert.deepEqual(pin.extends, ref("Marker", "Marker"));
  assert.deepEqual(pin.members.map((m) => `${m.name}${m.inherited ? " (inherited)" : ""}`), ["meta", "on (inherited)", "dragBy (inherited)"]);
  assert.deepEqual(pin.events, [{ name: "drag", type: ref("pins", "PinDragEvent") }]);
  assert.equal(pin.mixins, undefined); // Draggable isn't exported
  // An exported mixin is listed, and its members are still inherited.
  const tile = decl("layers/TileLayer", "TileLayer");
  assert.deepEqual(tile.mixins, [ref("layers/Refreshable", "Refreshable")]);
  assert.ok(["refreshInterval", "refresh"].every((name) => tile.members.find((m) => m.name === name)?.inherited));
  // The mixin class and the constant aren't exported.
  assert.deepEqual(ir.modules.find((m) => m.specifier.endsWith("/pins.js")).declarations.map((d) => d.name), ["PinDragEvent", "PinEvents", "Pin"]);
});

test("a mixin's base in any order: the first named, exported class", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), `
    export {};
    export class Shape { area: number }
    declare class Glow { constructor(...args: any[]); glow(): void }
    declare const GlowFirst: typeof Glow & typeof Shape;
    export class Lamp extends GlowFirst { watts: number }
    declare const Anonymous: { new (...args: any[]): { spin(): void } } & typeof Shape;
    export class Top extends Anonymous { speed: number }`);
  const [{ declarations }] = extract(dir).modules;
  const shape = { kind: "ref", name: "Shape", module: "@acme/extra/index.js" };
  const [lamp, top] = ["Lamp", "Top"].map((name) => declarations.find((d) => d.name === name));
  assert.deepEqual(lamp.extends, shape);
  assert.deepEqual(lamp.members.map((m) => `${m.name}${m.inherited ? " (inherited)" : ""}`), ["watts", "glow (inherited)"]);
  assert.deepEqual(top.extends, shape);
  assert.deepEqual(top.members.map((m) => `${m.name}${m.inherited ? " (inherited)" : ""}`), ["speed", "spin (inherited)"]);
});

test("a mixin's base: its other exported classes are its mixins", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), `
    export {};
    export class Shape { area: number }
    export abstract class Dimmable { constructor(...args: any[]); dim(): void }
    declare class Glow { constructor(...args: any[]); glow(): void }
    declare const BulbBase: typeof Dimmable & typeof Shape & typeof Glow;
    export class Bulb extends BulbBase { watts: number }`);
  const [{ declarations }] = extract(dir).modules;
  const bulb = declarations.find((d) => d.name === "Bulb");
  assert.deepEqual(bulb.extends, { kind: "ref", name: "Dimmable", module: "@acme/extra/index.js" });
  assert.deepEqual(bulb.mixins, [{ kind: "ref", name: "Shape", module: "@acme/extra/index.js" }]);
  assert.equal(declarations.find((d) => d.name === "Shape").mixins, undefined);
});

test("R8, R22: Promise returns and optional parameters", () => {
  const [flyTo] = member("MapView", "MapView", "flyTo").signatures;
  assert.deepEqual(flyTo.returns, { kind: "ref", name: "Promise", args: [{ kind: "primitive", name: "void" }] });
  assert.deepEqual(flyTo.params[1], { name: "options", type: ref("MapView", "FlyToOptions"), optional: true });
  const [markerAt] = member("MapView", "MapView", "markerAt").signatures;
  assert.deepEqual(markerAt.returns.args[0].types, [ref("Marker", "Marker"), { kind: "primitive", name: "null" }]);
});

test("R9, R10: literal unions and the open-ended form", () => {
  assert.equal(decl("styles", "IconName").type.types.length, 120);
  assert.deepEqual(decl("MapView", "Basemap").type.types.at(-1), { kind: "literal", value: "3d-relief" });
  assert.deepEqual(decl("MapView", "Cursor").type.types.at(-1), { kind: "intersection", types: [{ kind: "primitive", name: "string" }, { kind: "object" }] });
});

test("R21: function types and module functions", () => {
  assert.deepEqual(decl("Marker", "LabelFormatter").type, { kind: "function", params: [{ name: "marker", type: ref("Marker", "Marker") }], returns: { kind: "primitive", name: "string" } });
  assert.deepEqual(decl("measure", "distance").signatures[0].params, [
    { name: "a", type: ref("geometry", "Point") },
    { name: "b", type: ref("geometry", "Point") },
    { name: "unit", type: { kind: "union", types: [ref("measure", "LengthUnit"), num] }, optional: true }, // R2: the alias stays a ref
  ]);
});

test("R22: a rest parameter's type is its array", () => {
  assert.deepEqual(decl("measure", "perimeter").signatures[0].params, [{ name: "points", type: { kind: "array", element: ref("geometry", "Point") }, rest: true }]);
});

test("TS enums, and R11: an intersection of object types is resolved", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), `
    export enum Mode {
      Fast = 1,
      /** Careful. */
      Slow = "slow",
    }
    export interface Named { name: string; mode: Mode }
    export type Tagged = Named & { type: "tagged" };`);
  const [{ specifier, declarations: [mode, , tagged] }] = extract(dir).modules;
  assert.equal(specifier, "@acme/extra/index.js");
  assert.deepEqual(mode, { kind: "enum", name: "Mode", members: [{ name: "Fast", value: 1 }, { name: "Slow", value: "slow", doc: { summary: "Careful." } }] });
  assert.deepEqual(tagged.type.members.map((m) => [m.name, m.type]), [
    ["name", { kind: "primitive", name: "string" }],
    ["mode", { kind: "ref", name: "Mode", module: "@acme/extra/index.js" }],
    ["type", { kind: "literal", value: "tagged" }],
  ]);
  assert.deepEqual(tagged.type.bases, [{ kind: "ref", name: "Named", module: "@acme/extra/index.js" }]); // R11: what names its record
});

test("design §2: custom elements come from the manifest, linked to the typings", () => {
  const [element] = ir.elements;
  assert.deepEqual(element.class, ref("demo-map", "DemoMapElement"));
  assert.deepEqual(element.doc, { summary: "A map view as an element." });
  // Attributes link to the class's properties (R9: `basemap` is a `Basemap`), and keep the manifest's own type, which
  // can be narrower than the property's (R28: a primitive one renders as an HTML attribute).
  assert.deepEqual(element.attributes, [
    { name: "basemap", property: "basemap", type: ref("MapView", "Basemap"), doc: { default: '"plain"' } },
    { name: "zoom", property: "zoom", type: num, doc: { summary: "Clamped to 0–20, like the view's.", default: "1" } },
  ]);
  assert.deepEqual(member("demo-map", "DemoMapElement", "basemap").type, ref("MapView", "Basemap"));
  // Event types are resolved by the checker; a CustomEvent<T> gives its detail type.
  assert.deepEqual(element.events.map((e) => [e.name, e.detail]), [
    ["demo-map-click", ref("demo-map", "DemoMapClickDetail")],
    ["demo-map-zoom-change", { kind: "object", members: [{ kind: "property", name: "zoom", type: num }] }],
    ["demo-map-ready", { kind: "primitive", name: "null" }],
  ]);
  assert.deepEqual(element.slots.map((s) => s.name), ["", "toolbar"]);
});

test("design §2: manifest types the typings don't give, ones that don't resolve, and detail-typed events", (t) => {
  const dir = tempDir(t);
  const write = (name, value) => fs.writeFileSync(path.join(dir, name), typeof value === "string" ? value : JSON.stringify(value));
  write("package.json", { name: "@acme/extra", version: "1.0.0", type: "module", customElements: "cem.json" });
  write("kinds.d.ts", 'export type Kind = "a" | "b";');
  write("x-pin.d.ts", "export class PinElement extends HTMLElement {}");
  write("cem.json", { modules: [
    { path: "x-pin.js", declarations: [{
      kind: "class", name: "PinElement", deprecated: true,
      attributes: [
        { name: "kind", type: { text: "Kind | null", references: [{ name: "Kind", module: "kinds.js" }] }, description: "No property." },
        { name: "size", type: { text: "Size" } },
      ],
      // Some manifests give an event's detail type rather than its event type.
      events: [{ name: "x-pin-press", type: { text: "MouseEvent" } }, { name: "x-pin-move", type: { text: "{ kind: Kind }", references: [{ name: "Kind", module: "kinds.js" }] } }, { name: "x-pin-done" }],
    }] },
    // The definition can live in another module.
    { path: "define.js", exports: [{ kind: "custom-element-definition", name: "x-pin", declaration: { name: "PinElement", module: "x-pin.js" } }] },
  ] });
  const { elements } = extract(dir);
  assert.deepEqual(elements, [{
    tag: "x-pin",
    class: { kind: "ref", name: "PinElement", module: "@acme/extra/x-pin.js" },
    doc: { deprecated: "" },
    attributes: [
      { name: "kind", type: { kind: "union", types: [{ kind: "ref", name: "Kind", module: "@acme/extra/kinds.js" }, { kind: "primitive", name: "null" }] }, doc: { summary: "No property." } },
      { name: "size", type: { kind: "unresolved", text: "Size" } },
    ],
    events: [
      { name: "x-pin-press", type: { kind: "ref", name: "MouseEvent" } },
      { name: "x-pin-move", detail: { kind: "object", members: [{ kind: "property", name: "kind", type: { kind: "ref", name: "Kind", module: "@acme/extra/kinds.js" } }] } },
      { name: "x-pin-done" },
    ],
  }]);
});

test("a member keyed by a symbol is left out, own or picked", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), `
    export declare class Bag { size: number; [Symbol.iterator](): IterableIterator<number>; }
    export interface Entries { [Symbol.iterator](): Iterator<string>; count: number }
    export interface View extends Pick<Bag, "size" | typeof Symbol.iterator> {}`);
  const [{ declarations }] = extract(dir).modules;
  assert.deepEqual(declarations.map((d) => [d.name, d.members.map((m) => m.name)]), [["Bag", ["size"]], ["Entries", ["count"]], ["View", ["size"]]]);
});

test("a member named like a type-only marker is left out", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), `export declare class Swatch { color: string; "@types": { color?: string | null }; }`);
  const [{ declarations }] = extract(dir).modules;
  assert.deepEqual(declarations.map((d) => [d.name, d.members.map((m) => m.name)]), [["Swatch", ["color"]]]);
});

test("a default-exported function keeps its own name", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "area.d.ts"), "export default function area(): number;");
  const [{ declarations }] = extract(dir).modules;
  assert.deepEqual(declarations, [{ kind: "function", name: "area", export: "default", signatures: [{ returns: { kind: "primitive", name: "number" } }] }]);
});

test("an export of a declaration elsewhere in the file keeps the declaration, once", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "pin.d.ts"), "declare class Pin { x: number; }\nexport default Pin;");
  fs.writeFileSync(path.join(dir, "both.d.ts"), "export declare class Tag { y: number; }\nexport default Tag;");
  fs.writeFileSync(path.join(dir, "again.d.ts"), 'export { Tag } from "./both";');
  const modules = extract(dir).modules;
  const property = (name) => ({ kind: "property", name, type: { kind: "primitive", name: "number" } });
  assert.deepEqual(modules.map((m) => m.specifier), ["@acme/extra/both.js", "@acme/extra/pin.js"]); // a re-export is the declaring module's
  assert.deepEqual(modules[1].declarations, [{ kind: "class", name: "Pin", export: "default", constructors: [{}], members: [property("x")] }]);
  assert.deepEqual(modules[0].declarations, [{ kind: "class", name: "Tag", constructors: [{}], members: [property("y")] }]); // exported under its own name too
});

test("a package without a manifest has no elements", (t) => {
  const dir = tempDir(t);
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/plain", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), "export const x: number;");
  const out = extract(dir);
  assert.equal(out.elements, undefined);
});

/** A temporary package of the given files (`{ [path]: text or JSON }`); `run` gets its folder, which is removed after. */
function withPackage(files, run) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  try {
    for (const [name, value] of Object.entries(files)) {
      fs.mkdirSync(path.dirname(path.join(dir, name)), { recursive: true });
      fs.writeFileSync(path.join(dir, name), typeof value === "string" ? value : JSON.stringify(value));
    }
    return run(dir);
  } finally {
    fs.rmSync(dir, { recursive: true });
  }
}

const specifiers = (exports, files) => withPackage({
  "package.json": { name: "@acme/extra", version: "1.0.0", type: "module", exports },
  ...Object.fromEntries(files.map((f, i) => [f, `export declare class C${i} {}`])),
}, (dir) => Object.fromEntries(extract(dir).modules.map((m) => [m.declarations[0].name, m.internal ? `${m.specifier} (internal)` : m.specifier])));

test("specifiers through an exports map: a key is accepted when Node resolves it back to the file", () => {
  assert.deepEqual(specifiers({
    "./*": "./lib/*.js",
    "./tools/special": "./lib/tools/special.js", // an exact key comes before the patterns
    "./tools/*": { node: { import: "./lib/node/*.js" }, default: "./lib/tools/*.js" }, // `node` isn't a condition we follow
    "./hidden/*": null, // a longer prefix than "./*", so it excludes what "./*" would reach
    "./extra/*": "./lib/elsewhere/*.js", // shadows "./*" for lib/extra/
  }, ["lib/tools/special.d.ts", "lib/tools/plain.d.ts", "lib/core.d.ts", "lib/hidden/secret.d.ts", "lib/extra/x.d.ts", "lib/elsewhere/y.d.ts", "src/impl.d.ts"]), {
    C0: "@acme/extra/tools/special",
    C1: "@acme/extra/tools/plain",
    C2: "@acme/extra/core",
    C3: "@acme/extra/lib/hidden/secret.js (internal)",
    C4: "@acme/extra/lib/extra/x.js (internal)", // "./extra/x" resolves to lib/elsewhere/x.js
    C5: "@acme/extra/elsewhere/y", // "./*" reaches it first; "./extra/y" would too
    C6: "@acme/extra/src/impl.js (internal)", // no export reaches it: its path in the package
  });
});

test("export targets: conditions fall through, arrays take their first usable entry, `types` matches the .d.ts", () => {
  assert.deepEqual(specifiers({
    "./fall": { import: { browser: "./lib/no.js" }, default: "./lib/fall.js" }, // `import` resolves to nothing here
    "./array": [{ worker: "./lib/no.js" }, "./lib/array.js"],
    "./typed": { types: "./types/typed.d.ts", default: "./lib/typed.js" },
    "./typed/*": { import: { types: "./types/sub/*.d.ts", default: "./lib/sub/*.js" } },
  }, ["lib/fall.d.ts", "lib/array.d.ts", "types/typed.d.ts", "types/sub/a.d.ts"]), {
    C0: "@acme/extra/fall",
    C1: "@acme/extra/array",
    C2: "@acme/extra/typed",
    C3: "@acme/extra/typed/a",
  });
  // A string, or a map of conditions, is the "." export.
  assert.deepEqual(specifiers("./lib/core.js", ["lib/core.d.ts"]), { C0: "@acme/extra" });
  assert.deepEqual(specifiers({ import: { types: "./lib/core.d.ts", default: "./lib/core.js" } }, ["lib/core.d.ts"]), { C0: "@acme/extra" });
});

test("a declaration of an internal module is imported from a module that re-exports it", () => {
  const modules = withPackage({
    "package.json": { name: "@acme/extra", version: "1.0.0", type: "module", exports: { ".": "./index.js", "./b": "./b.js" } },
    "impl/a.d.ts": "export declare class A {}\nexport declare function make(): A;\nexport declare class Hidden {}",
    "impl/c.d.ts": "export default class C {}",
    "b.d.ts": 'export { A as Renamed } from "./impl/a.js";', // later than "@acme/extra" by specifier
    "index.d.ts": 'export { A, make } from "./impl/a.js";\nexport { default as C } from "./impl/c.js";',
  }, (dir) => extract(dir).modules);
  assert.deepEqual(modules.map((m) => [m.specifier, m.internal, m.declarations.map((d) => [d.name, d.export, d.importFrom])]), [
    ["@acme/extra/impl/a.js", true, [["A", undefined, { module: "@acme/extra", file: "index.js" }], ["make", undefined, { module: "@acme/extra", file: "index.js" }], ["Hidden", undefined, undefined]]],
    ["@acme/extra/impl/c.js", true, [["C", "default", { module: "@acme/extra", file: "index.js" }]]], // its own name there
  ]);
});

test("a module's file is listed when its specifier isn't the package's name plus the file's path", () => {
  const files = withPackage({
    "package.json": { name: "@acme/extra", version: "1.0.0", type: "module", exports: { ".": "./lib/index.js", "./parts/*": "./lib/parts/*/index.js", "./*.js": "./*.js" } },
    "lib/index.d.ts": "export declare class Root {}",
    "plain.d.ts": "export declare class Plain {}", // "./*.js" mirrors the files
    "lib/parts/dial/index.d.ts": "export declare class Dial {}",
    "lib/hidden.d.ts": "export declare class Hidden {}", // "./*.js" reaches lib/hidden.js by its path
  }, (dir) => extract(dir).modules.map((m) => [m.specifier, m.file]));
  assert.deepEqual(files, [
    ["@acme/extra", "lib/index.js"],
    ["@acme/extra/lib/hidden.js", undefined],
    ["@acme/extra/parts/dial", "lib/parts/dial/index.js"],
    ["@acme/extra/plain.js", undefined],
  ]);
});

test("a module's file is the JS its export loads in a browser, not the .js next to its typings", () => {
  const files = withPackage({
    "package.json": { name: "@acme/extra", version: "1.0.0", type: "module", exports: {
      "./geo": { types: "./types/geo.d.ts", import: "./esm/geo.js" }, // typings and JS in separate folders
      "./a.js": { types: "./a.d.ts", import: "./esm/a.js" }, // a mirror for the typings only
      "./tile": { types: "./types/tile.d.ts", browser: "./browser/tile.js", import: "./node/tile.js" },
      "./only": { types: "./types/only.d.ts" }, // no JS target: the .js next to the typings
      "./bare": { types: "./types/bare.d.ts" }, // no JS target and no .js: nothing loads it, so it's internal
      "./multi-types": { types: "./types/multi.d.ts" }, // several exports reach it: the one with a JS target wins
      "./multi": { types: "./types/multi.d.ts", import: "./esm/multi.js" },
    } },
    "types/geo.d.ts": "export declare class Geo {}",
    "a.d.ts": "export declare class A {}",
    "types/tile.d.ts": "export declare class Tile {}",
    "types/only.d.ts": "export declare class Only {}",
    "types/only.js": "export class Only {}",
    "types/bare.d.ts": "export declare class Bare {}",
    "types/multi.d.ts": "export declare class Multi {}",
  }, (dir) => extract(dir).modules.map((m) => [m.specifier, m.file, m.internal]));
  assert.deepEqual(files, [
    ["@acme/extra/a.js", "esm/a.js", undefined],
    ["@acme/extra/bare", undefined, true], // its subpath kept, so namespaces and profile keys don't move
    ["@acme/extra/geo", "esm/geo.js", undefined],
    ["@acme/extra/multi", "esm/multi.js", undefined],
    ["@acme/extra/only", "types/only.js", undefined],
    ["@acme/extra/tile", "browser/tile.js", undefined],
  ]);
});

test("design §3.5: a base class exported for its typings only, and re-exported as a type, can't be imported", () => {
  const modules = withPackage({
    "package.json": { name: "@acme/extra", version: "1.0.0", type: "module", exports: {
      ".": { types: "./index.d.ts", import: "./index.js" },
      "./base": { types: "./base.d.ts" },
      "./star": { types: "./star.d.ts" },
      "./imp": { types: "./imp.d.ts" },
      "./plain": { types: "./plain.d.ts" },
    } },
    "base.d.ts": "export declare class Base { text: string }",
    "star.d.ts": "export declare class StarBase {}",
    "imp.d.ts": "export declare class ImpBase {}",
    "plain.d.ts": "export declare class PlainBase {}",
    "index.d.ts": [
      'import { Base } from "./base.js";',
      'export type { Base } from "./base.js";', // export type { X } from
      'export type * from "./star.js";', // export type * from
      'import type { ImpBase } from "./imp.js";',
      "export { ImpBase };", // import type, then export
      'export * from "./plain.js";', // a value re-export: index.js has it
      "export declare class Label extends Base {}",
    ].join("\n"),
    "index.js": "export class Label {}",
  }, (dir) => extract(dir).modules);
  assert.deepEqual(modules.map((m) => [m.specifier, m.internal, m.declarations.map((d) => [d.name, d.importFrom])]), [
    ["@acme/extra", undefined, [["Label", undefined]]],
    ["@acme/extra/base", true, [["Base", undefined]]], // no importFrom: index.js doesn't export it
    ["@acme/extra/imp", true, [["ImpBase", undefined]]],
    ["@acme/extra/plain", true, [["PlainBase", { module: "@acme/extra", file: "index.js" }]]],
    ["@acme/extra/star", true, [["StarBase", undefined]]],
  ]);
});

test("manifest references through `export { … } from` resolve to the declaring module", () => {
  const { modules, elements: [element] } = withPackage({
    "package.json": { name: "@acme/extra", version: "1.0.0", type: "module", customElements: "cem.json" },
    "x-tag.d.ts": "export declare class TagElement extends HTMLElement {}\nexport interface TagDetail { id: string }",
    "index.d.ts": 'export { TagElement as Tag, type TagDetail } from "./x-tag.js";',
    "cem.json": { modules: [{ path: "index.js", declarations: [{ kind: "class", name: "Tag", tagName: "x-tag",
      events: [{ name: "x-tag-pick", type: { text: "CustomEvent<TagDetail>", references: [{ name: "TagDetail", module: "index.js" }] } }] }] }] },
  }, extract);
  assert.deepEqual(modules.map((m) => m.specifier), ["@acme/extra/x-tag.js"]); // index.js declares nothing of its own
  assert.deepEqual(element.class, { kind: "ref", name: "TagElement", module: "@acme/extra/x-tag.js" });
  assert.deepEqual(element.events[0].detail, { kind: "ref", name: "TagDetail", module: "@acme/extra/x-tag.js" });
});

test("a manifest module path that is an export subpath resolves through the package's exports, then its re-exports", () => {
  // No "type": the virtual modules are ES modules anyway, so the exports' `import` condition applies.
  const { elements: [element] } = withPackage({
    "package.json": { name: "@acme/extra", version: "1.0.0", customElements: "cem.json", exports: { "./parts/*": { import: "./lib/parts/*/index.js" } } },
    "lib/parts/x-dial/index.d.ts": 'export * from "./dial.js";',
    "lib/parts/x-dial/dial.d.ts": "export declare class DialElement extends HTMLElement {}\nexport interface DialTurn { by: number }",
    "cem.json": { modules: [{ path: "parts/x-dial", declarations: [{ kind: "class", name: "DialElement", tagName: "x-dial",
      events: [{ name: "x-dial-turn", type: { text: "CustomEvent<DialTurn>", references: [{ name: "DialTurn", module: "parts/x-dial" }] } }] }] }] },
  }, extract);
  assert.deepEqual(element.class, { kind: "ref", name: "DialElement", module: "@acme/extra/lib/parts/x-dial/dial.js" });
  assert.deepEqual(element.events[0].detail, { kind: "ref", name: "DialTurn", module: "@acme/extra/lib/parts/x-dial/dial.js" });
});

test("a manifest module path without an extension, or a folder, that no export reaches is the package's file", () => {
  // No "type" and no exports: the virtual ES modules import the files by their full names.
  const { elements } = withPackage({
    "package.json": { name: "@acme/extra", version: "1.0.0", customElements: "cem.json" },
    "lib/knob.d.ts": "export declare class KnobElement extends HTMLElement {}",
    "lib/slider/index.d.ts": 'export * from "./slider.js";',
    "lib/slider/slider.d.ts": "export declare class SliderElement extends HTMLElement {}",
    "cem.json": { modules: [
      { path: "lib/knob", declarations: [{ kind: "class", name: "KnobElement", tagName: "x-knob" }] },
      { path: "lib/slider/", declarations: [{ kind: "class", name: "SliderElement", tagName: "x-slider" }] },
    ] },
  }, extract);
  assert.deepEqual(elements.map((e) => e.class), [
    { kind: "ref", name: "KnobElement", module: "@acme/extra/lib/knob.js" },
    { kind: "ref", name: "SliderElement", module: "@acme/extra/lib/slider/slider.js" },
  ]);
});

/** A node_modules with both fixtures copied into it, as an install has them, so each resolves the other by name. */
function installed(t) {
  const root = tempDir(t);
  const at = (name) => path.join(root, "node_modules", "@acme", name);
  for (const name of ["demo-maps", "demo-widgets"]) fs.cpSync(fileURLToPath(new URL(`../../../fixtures/${name}`, import.meta.url)), at(name), { recursive: true });
  return { maps: at("demo-maps"), widgets: at("demo-widgets") };
}

test("a listed package's dependency contributes only the declarations it reaches, under their own modules", (t) => {
  const packages = installed(t);
  const widgets = extract(packages.widgets, { eventMap: "$events" });
  assert.deepEqual(widgets.package, { name: "@acme/demo-widgets", version: "0.0.0" });
  const names = Object.fromEntries(widgets.modules.map((m) => [m.specifier, m.declarations.map((d) => d.name)]));
  // Legend.view is a MapView, which reaches its own types, its bases, its members' types and theirs.
  assert.deepEqual(Object.keys(names), ["@acme/demo-maps/MapView.js", "@acme/demo-maps/Marker.js", "@acme/demo-maps/core.js", "@acme/demo-maps/geometry.js",
    "@acme/demo-maps/layers/Layer.js", "@acme/demo-maps/styles.js", "@acme/demo-widgets/dist/components/zoom-label/element.js",
    "@acme/demo-widgets/dist/support/entry.js", "@acme/demo-widgets/dist/support/swatch.js", "@acme/demo-widgets/legend"]);
  assert.deepEqual(names["@acme/demo-maps/geometry.js"], ["ShapeType", "Bounds", "Shape", "Point"]); // no Line or Polygon
  assert.ok(widgets.modules.filter((m) => m.specifier.startsWith("@acme/demo-maps/")).every((m) => m.package === "@acme/demo-maps"));
  // A dependency's declaration is the same as when its package is listed; its manifest isn't read.
  assert.deepEqual(widgets.modules[0].declarations.find((d) => d.name === "MapView"), decl("MapView", "MapView"));
  assert.deepEqual(widgets.elements.map((e) => e.tag), ["demo-legend", "demo-zoom-label"]);
});

test("a listed package that a dependency has its own copy of is listed once", (t) => {
  // demo-widgets' import of @acme/demo-maps reaches the copy next to it, not the fixture listed by its real path.
  const packages = installed(t);
  const both = extract([fixture, packages.widgets]);
  const specifiers = both.modules.map((m) => m.specifier);
  assert.deepEqual(specifiers, [...new Set(specifiers)]);
  assert.deepEqual(both.modules.filter((m) => !m.package).map((m) => m.specifier), ir.modules.map((m) => m.specifier));
});

// The golden IR is the generator's input for the fixture. It's also the "byte-identical across runs" check.
const golden = path.join(fixture, "demo-maps.ir.json");

test("the CLI writes the golden IR for the fixture, byte for byte", () => {
  const json = execFileSync(process.execPath, [fileURLToPath(new URL("../extract.mjs", import.meta.url)), fixture, "--profile", `${fixture}/profile.json`], { encoding: "utf8" });
  if (process.env.UPDATE_GOLDEN) fs.writeFileSync(golden, json);
  assert.ok(json === fs.readFileSync(golden, "utf8"),
    "The IR differs from fixtures/demo-maps/demo-maps.ir.json. If the change is intended, run\n" +
    "  UPDATE_GOLDEN=1 node --test src/interoper-extractor/test/extract.test.mjs\nand review the diff.");
  assert.deepEqual(JSON.parse(json), ir);
});

// Both fixtures in one IR, demo-maps first, the way the private validation extracts a library and its components. The
// golden file holds what demo-widgets adds to demo-maps' IR: its modules and elements.
const widgetsGolden = fileURLToPath(new URL("../../../fixtures/demo-widgets/demo-widgets.ir.json", import.meta.url));

/** demo-maps' IR with what another package adds to it, in IR order. */
const combined = (maps, delta) => ({
  ...maps,
  modules: [...maps.modules, ...delta.modules].sort((a, b) => (a.specifier < b.specifier ? -1 : 1)),
  elements: [...maps.elements, ...delta.elements].sort((a, b) => (a.tag < b.tag ? -1 : 1)),
});

test("the CLI writes the golden IR for several packages, byte for byte", (t) => {
  const packages = installed(t);
  const json = execFileSync(process.execPath, [fileURLToPath(new URL("../extract.mjs", import.meta.url)), packages.maps, packages.widgets, "--profile", `${fixture}/profile.json`], { encoding: "utf8" });
  const both = JSON.parse(json);
  const tags = new Set(ir.elements.map((e) => e.tag));
  if (process.env.UPDATE_GOLDEN) {
    const delta = { ...both, modules: both.modules.filter((m) => m.package), elements: both.elements.filter((e) => !tags.has(e.tag)) };
    fs.writeFileSync(widgetsGolden, JSON.stringify(delta, null, 2) + "\n");
  }
  assert.ok(json === JSON.stringify(combined(ir, JSON.parse(fs.readFileSync(widgetsGolden, "utf8"))), null, 2) + "\n",
    "The IR differs from demo-maps.ir.json plus fixtures/demo-widgets/demo-widgets.ir.json. If the change is intended, run\n" +
    "  UPDATE_GOLDEN=1 node --test src/interoper-extractor/test/extract.test.mjs\nand review the diff.");

  // The first package is the IR's; its part is what it is alone. The others' modules say their package.
  assert.deepEqual(both.package, ir.package);
  assert.deepEqual({ ...both, modules: both.modules.filter((m) => !m.package), elements: both.elements.filter((e) => tags.has(e.tag)) }, ir);
  const widgets = both.modules.filter((m) => m.package);
  // `exports` gives "./legend" for dist/legend.js (its `types` condition); no export reaches dist/support/, nor a
  // component's element.js ("./components/*" reaches its index.js).
  // Only legend's file isn't its specifier's path, for import maps; demo-maps' `./*.js` mirrors its files.
  assert.deepEqual(widgets.map((m) => [m.specifier, m.package, m.internal, m.file]), [
    ["@acme/demo-widgets/dist/components/zoom-label/element.js", "@acme/demo-widgets", true, undefined],
    ["@acme/demo-widgets/dist/support/entry.js", "@acme/demo-widgets", true, undefined],
    ["@acme/demo-widgets/dist/support/swatch.js", "@acme/demo-widgets", true, undefined],
    ["@acme/demo-widgets/legend", "@acme/demo-widgets", undefined, "dist/legend.js"],
  ]);
  assert.ok(both.modules.filter((m) => !m.package).every((m) => m.file === undefined));
  // The root re-exports swatch.js's declarations, so they're imported from it; entry.js's aren't importable.
  const root = { module: "@acme/demo-widgets", file: "dist/index.js" };
  assert.deepEqual(widgets[2].declarations.map((d) => [d.name, d.importFrom]), [["Swatch", root], ["swatchFor", root]]);
  assert.deepEqual(widgets[1].declarations.map((d) => [d.name, d.importFrom]), [["LegendEntry", undefined], ["describe", undefined]]);
  const legend = widgets[3].declarations.find((d) => d.name === "Legend");
  assert.equal(legend.importFrom, undefined); // its own module is reachable
  assert.deepEqual(legend.members.find((m) => m.name === "view").type, ref("MapView", "MapView"));
  // The manifest names dist/index.js, which does `export * from "./legend.js"`: its references resolve to legend.
  const element = both.elements.find((e) => e.tag === "demo-legend");
  assert.deepEqual(element.class, { kind: "ref", name: "Legend", module: "@acme/demo-widgets/legend" });
  assert.deepEqual(element.events.map((e) => e.detail), [{ kind: "ref", name: "LegendSelectDetail", module: "@acme/demo-widgets/legend" }]);
  // The manifest names the zoom label's module by an export subpath, `components/zoom-label`: it's imported as
  // `@acme/demo-widgets/components/zoom-label`, whose index.js re-exports element.js, which declares the element.
  const zoomLabel = both.elements.find((e) => e.tag === "demo-zoom-label");
  const declaring = "@acme/demo-widgets/dist/components/zoom-label/element.js";
  assert.deepEqual(zoomLabel.class, { kind: "ref", name: "ZoomLabelElement", module: declaring });
  assert.deepEqual(zoomLabel.events.map((e) => e.detail), [{ kind: "ref", name: "ZoomLabelCopyDetail", module: declaring }]);
  const label = { module: "@acme/demo-widgets/components/zoom-label", file: "dist/components/zoom-label/index.js" };
  assert.deepEqual(widgets[0].declarations.map((d) => [d.name, d.importFrom]),
    [["ZoomLabelCopyDetail", label], ["ZoomLabelElement", label]]);
});

// Design §4: TypeScript comes from INTEROPER_TYPESCRIPT (the CLI's), else the extractor's own folder, and it must be 6.x.
test("the CLI loads TypeScript from INTEROPER_TYPESCRIPT, and only 6.x", (t) => {
  const fake = path.join(tempDir(t), "typescript");
  fs.mkdirSync(fake);
  fs.writeFileSync(path.join(fake, "package.json"), JSON.stringify({ name: "typescript", version: "5.9.3", main: "index.js" }));
  fs.writeFileSync(path.join(fake, "index.js"), 'module.exports = { version: "5.9.3" };\n');
  const cli = fileURLToPath(new URL("../extract.mjs", import.meta.url));
  const env = { ...process.env, INTEROPER_TYPESCRIPT: fake };
  assert.throws(() => execFileSync(process.execPath, [cli, fixture], { encoding: "utf8", stdio: "pipe", env }),
    (e) => e.stderr.includes("TypeScript 5.9.3 found; the extractor needs 6.x: run `npm install -D typescript@6.0.3`"));
});
