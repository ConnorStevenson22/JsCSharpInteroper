import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { extract } from "../extract.mjs";

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
  assert.deepEqual(ir.modules.map((m) => m.specifier.slice("@acme/demo-maps/".length)), ["MapView.js", "Marker.js", "core.js", "demo-map.js", "geometry.js", "layers/Layer.js", "layers/TileLayer.js", "measure.js", "ops/a.js", "ops/b.js", "pins.js", "styles.js"]);
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
  assert.deepEqual(decl("MapView", "MapView").events, [{ name: "click", type: ref("MapView", "ClickEvent") }]);
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
  assert.deepEqual(decl("core", "Collection").typeParameters, [{ name: "T", default: { kind: "primitive", name: "any" } }]);
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
  // The mixin class and the constant aren't exported.
  assert.deepEqual(ir.modules.find((m) => m.specifier.endsWith("/pins.js")).declarations.map((d) => d.name), ["PinDragEvent", "PinEvents", "Pin"]);
});

test("a mixin's base in any order: the first named, exported class", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
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
  fs.rmSync(dir, { recursive: true });
  const shape = { kind: "ref", name: "Shape", module: "@acme/extra/index.js" };
  const [lamp, top] = ["Lamp", "Top"].map((name) => declarations.find((d) => d.name === name));
  assert.deepEqual(lamp.extends, shape);
  assert.deepEqual(lamp.members.map((m) => `${m.name}${m.inherited ? " (inherited)" : ""}`), ["watts", "glow (inherited)"]);
  assert.deepEqual(top.extends, shape);
  assert.deepEqual(top.members.map((m) => `${m.name}${m.inherited ? " (inherited)" : ""}`), ["speed", "spin (inherited)"]);
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
  assert.deepEqual(decl("measure", "distance"), { kind: "function", name: "distance", signatures: [{ params: [{ name: "a", type: ref("geometry", "Point") }, { name: "b", type: ref("geometry", "Point") }], returns: num }] });
});

test("R22: a rest parameter's type is its array", () => {
  assert.deepEqual(decl("measure", "perimeter").signatures[0].params, [{ name: "points", type: { kind: "array", element: ref("geometry", "Point") }, rest: true }]);
});

test("TS enums, and R11: an intersection of object types is resolved", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
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
  fs.rmSync(dir, { recursive: true });
  assert.equal(specifier, "@acme/extra/index.js");
  assert.deepEqual(mode, { kind: "enum", name: "Mode", members: [{ name: "Fast", value: 1 }, { name: "Slow", value: "slow", doc: { summary: "Careful." } }] });
  assert.deepEqual(tagged.type.members.map((m) => [m.name, m.type]), [
    ["name", { kind: "primitive", name: "string" }],
    ["mode", { kind: "ref", name: "Mode", module: "@acme/extra/index.js" }],
    ["type", { kind: "literal", value: "tagged" }],
  ]);
});

test("design §2: custom elements come from the manifest, linked to the typings", () => {
  const [element] = ir.elements;
  assert.deepEqual(element.class, ref("demo-map", "DemoMapElement"));
  assert.deepEqual(element.doc, { summary: "A map view as an element." });
  // Attributes link to the class's properties, which carry the types (R9: `basemap` is a `Basemap`).
  assert.deepEqual(element.attributes, [
    { name: "basemap", property: "basemap", doc: { default: '"plain"' } },
    { name: "zoom", property: "zoom", doc: { default: "1" } },
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

test("design §2: manifest types the typings don't give, ones that don't resolve, and detail-typed events", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
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
  fs.rmSync(dir, { recursive: true });
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

test("a default-exported function keeps its own name", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "area.d.ts"), "export default function area(): number;");
  const [{ declarations }] = extract(dir).modules;
  fs.rmSync(dir, { recursive: true });
  assert.deepEqual(declarations, [{ kind: "function", name: "area", export: "default", signatures: [{ returns: { kind: "primitive", name: "number" } }] }]);
});

test("an export of a declaration elsewhere in the file keeps the declaration, once", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/extra", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "pin.d.ts"), "declare class Pin { x: number; }\nexport default Pin;");
  fs.writeFileSync(path.join(dir, "both.d.ts"), "export declare class Tag { y: number; }\nexport default Tag;");
  fs.writeFileSync(path.join(dir, "again.d.ts"), 'export { Tag } from "./both";');
  const modules = extract(dir).modules;
  fs.rmSync(dir, { recursive: true });
  const property = (name) => ({ kind: "property", name, type: { kind: "primitive", name: "number" } });
  assert.deepEqual(modules.map((m) => m.specifier), ["@acme/extra/both.js", "@acme/extra/pin.js"]); // a re-export is the declaring module's
  assert.deepEqual(modules[1].declarations, [{ kind: "class", name: "Pin", export: "default", constructors: [{}], members: [property("x")] }]);
  assert.deepEqual(modules[0].declarations, [{ kind: "class", name: "Tag", constructors: [{}], members: [property("y")] }]); // exported under its own name too
});

test("a package without a manifest has no elements", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/plain", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), "export const x: number;");
  const out = extract(dir);
  fs.rmSync(dir, { recursive: true });
  assert.equal(out.elements, undefined);
});

test("specifiers through an exports map: exact keys, then patterns in order, conditions; unreached files are internal", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  const write = (name, value) => {
    fs.mkdirSync(path.dirname(path.join(dir, name)), { recursive: true });
    fs.writeFileSync(path.join(dir, name), typeof value === "string" ? value : JSON.stringify(value));
  };
  write("package.json", { name: "@acme/extra", version: "1.0.0", type: "module", exports: {
    "./tools/*": { node: { import: "./lib/tools/*.js" }, default: "./lib/tools/*.js" }, // `node` isn't a condition we follow
    "./*": "./lib/*.js",
    "./tools/special": "./lib/tools/special.js", // an exact key wins over the patterns before it
    "./hidden/*": null,
  } });
  for (const name of ["tools/special", "tools/plain", "core", "hidden/secret"]) write(`lib/${name}.d.ts`, `export declare class ${name.split("/")[1] ?? name} {}`);
  write("internal/impl.d.ts", "export declare class Impl {}");
  const modules = extract(dir).modules;
  write("package.json", { name: "@acme/extra", version: "1.0.0", type: "module", exports: { import: { types: "./lib/core.d.ts", default: "./lib/core.js" } } });
  const root = extract(dir).modules.find((m) => m.declarations[0].name === "core");
  fs.rmSync(dir, { recursive: true });
  assert.deepEqual(modules.map((m) => [m.specifier, m.internal]), [
    ["@acme/extra/core", undefined],
    ["@acme/extra/hidden/secret", undefined], // the earlier "./*" reaches it
    ["@acme/extra/internal/impl.js", true], // no export reaches it: its path in the package
    ["@acme/extra/tools/plain", undefined],
    ["@acme/extra/tools/special", undefined],
  ]);
  assert.equal(root.specifier, "@acme/extra"); // a map of conditions is the "." export
});

test("manifest references through `export { … } from` resolve to the declaring module", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  const write = (name, value) => fs.writeFileSync(path.join(dir, name), typeof value === "string" ? value : JSON.stringify(value));
  write("package.json", { name: "@acme/extra", version: "1.0.0", type: "module", customElements: "cem.json" });
  write("x-tag.d.ts", "export declare class TagElement extends HTMLElement {}\nexport interface TagDetail { id: string }");
  write("index.d.ts", 'export { TagElement as Tag, type TagDetail } from "./x-tag.js";');
  write("cem.json", { modules: [{ path: "index.js", declarations: [{ kind: "class", name: "Tag", tagName: "x-tag",
    events: [{ name: "x-tag-pick", type: { text: "CustomEvent<TagDetail>", references: [{ name: "TagDetail", module: "index.js" }] } }] }] }] });
  const { modules, elements: [element] } = extract(dir);
  fs.rmSync(dir, { recursive: true });
  assert.deepEqual(modules.map((m) => m.specifier), ["@acme/extra/x-tag.js"]); // index.js declares nothing of its own
  assert.deepEqual(element.class, { kind: "ref", name: "TagElement", module: "@acme/extra/x-tag.js" });
  assert.deepEqual(element.events[0].detail, { kind: "ref", name: "TagDetail", module: "@acme/extra/x-tag.js" });
});

/** A node_modules with both fixtures linked into it, as an install has them, so each resolves the other by name. */
function linked() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  const scope = path.join(root, "node_modules", "@acme");
  fs.mkdirSync(scope, { recursive: true });
  for (const name of ["demo-maps", "demo-widgets"]) fs.symlinkSync(fileURLToPath(new URL(`../../../fixtures/${name}`, import.meta.url)), path.join(scope, name), "junction");
  return { maps: path.join(scope, "demo-maps"), widgets: path.join(scope, "demo-widgets"), remove: () => fs.rmSync(root, { recursive: true }) };
}

test("a listed package's dependency contributes only the declarations it reaches, under their own modules", () => {
  const packages = linked();
  const widgets = extract(packages.widgets, { eventMap: "$events" });
  packages.remove();
  assert.deepEqual(widgets.package, { name: "@acme/demo-widgets", version: "0.0.0" });
  const names = Object.fromEntries(widgets.modules.map((m) => [m.specifier, m.declarations.map((d) => d.name)]));
  // Legend.view is a MapView, which reaches its own types, its bases, its members' types and theirs.
  assert.deepEqual(Object.keys(names), ["@acme/demo-maps/MapView.js", "@acme/demo-maps/Marker.js", "@acme/demo-maps/core.js", "@acme/demo-maps/geometry.js",
    "@acme/demo-maps/layers/Layer.js", "@acme/demo-maps/styles.js", "@acme/demo-widgets/dist/support/entry.js", "@acme/demo-widgets/legend"]);
  assert.deepEqual(names["@acme/demo-maps/geometry.js"], ["ShapeType", "Bounds", "Shape", "Point"]); // no Line or Polygon
  assert.ok(widgets.modules.filter((m) => m.specifier.startsWith("@acme/demo-maps/")).every((m) => m.package === "@acme/demo-maps"));
  // A dependency's declaration is the same as when its package is listed; its manifest isn't read.
  assert.deepEqual(widgets.modules[0].declarations.find((d) => d.name === "MapView"), decl("MapView", "MapView"));
  assert.deepEqual(widgets.elements.map((e) => e.tag), ["demo-legend"]);
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

// Both fixtures in one IR, demo-maps first, the way the private validation extracts a library and its components.
const widgetsGolden = fileURLToPath(new URL("../../../fixtures/demo-widgets/demo-widgets.ir.json", import.meta.url));

test("the CLI writes the golden IR for several packages, byte for byte", () => {
  const packages = linked();
  const json = execFileSync(process.execPath, [fileURLToPath(new URL("../extract.mjs", import.meta.url)), packages.maps, packages.widgets, "--profile", `${fixture}/profile.json`], { encoding: "utf8" });
  packages.remove();
  if (process.env.UPDATE_GOLDEN) fs.writeFileSync(widgetsGolden, json);
  assert.ok(json === fs.readFileSync(widgetsGolden, "utf8"),
    "The IR differs from fixtures/demo-widgets/demo-widgets.ir.json. If the change is intended, run\n" +
    "  UPDATE_GOLDEN=1 node --test src/interoper-extractor/test/extract.test.mjs\nand review the diff.");

  const both = JSON.parse(json);
  // The first package is the IR's; its part is what it is alone. The others' modules say their package.
  assert.deepEqual(both.package, ir.package);
  assert.deepEqual({ ...both, modules: both.modules.filter((m) => !m.package), elements: both.elements.filter((e) => e.tag !== "demo-legend") }, ir);
  const widgets = both.modules.filter((m) => m.package);
  // `exports` gives "./legend" for dist/legend.js (its `import` condition); no export reaches dist/support/entry.js.
  assert.deepEqual(widgets.map((m) => [m.specifier, m.package, m.internal]), [
    ["@acme/demo-widgets/dist/support/entry.js", "@acme/demo-widgets", true],
    ["@acme/demo-widgets/legend", "@acme/demo-widgets", undefined],
  ]);
  const legend = widgets[1].declarations.find((d) => d.name === "Legend");
  assert.deepEqual(legend.members.find((m) => m.name === "view").type, ref("MapView", "MapView"));
  // The manifest names dist/index.js, which does `export * from "./legend.js"`: its references resolve to legend.
  const element = both.elements.find((e) => e.tag === "demo-legend");
  assert.deepEqual(element.class, { kind: "ref", name: "Legend", module: "@acme/demo-widgets/legend" });
  assert.deepEqual(element.events.map((e) => e.detail), [{ kind: "ref", name: "LegendSelectDetail", module: "@acme/demo-widgets/legend" }]);
});
