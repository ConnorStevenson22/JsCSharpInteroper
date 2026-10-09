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
  assert.deepEqual(ir.modules.map((m) => m.specifier.slice("@acme/demo-maps/".length)), ["MapView.js", "Marker.js", "core.js", "demo-map.js", "geometry.js", "measure.js", "styles.js"]);
  assert.deepEqual(ir.modules.find((m) => m.specifier.endsWith("/geometry.js")).declarations.map((d) => `${d.kind} ${d.name}`),
    ["alias ShapeType", "interface Bounds", "class Shape", "class Point", "class Line", "class Polygon"]);
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
  assert.deepEqual(decl("core", "Collection").typeParameters, [{ name: "T" }]);
  assert.deepEqual(member("MapView", "MapView", "markers").type, ref("core", "Collection", [ref("Marker", "Marker")]));
  // Members declared outside the package (HTMLElement's) are left out; the base is a platform reference.
  const element = decl("demo-map", "DemoMapElement");
  assert.deepEqual(element.extends, { kind: "ref", name: "HTMLElement" });
  assert.deepEqual(element.members.map((m) => `${m.static ? "static " : ""}${m.name}`), ["view", "basemap", "zoom", "center", "clickAt", "static observedAttributes"]);
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

test("design §2: manifest types the typings don't give, and ones that don't resolve", () => {
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
      events: [{ name: "x-pin-press", type: { text: "MouseEvent" } }, { name: "x-pin-done" }],
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
    events: [{ name: "x-pin-press", type: { kind: "ref", name: "MouseEvent" } }, { name: "x-pin-done" }],
  }]);
});

test("a package without a manifest has no elements", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "extract-"));
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify({ name: "@acme/plain", version: "1.0.0" }));
  fs.writeFileSync(path.join(dir, "index.d.ts"), "export const x: number;");
  const out = extract(dir);
  fs.rmSync(dir, { recursive: true });
  assert.equal(out.elements, undefined);
});

// The golden IR is the generator's input for the fixture. It's also the "byte-identical across runs" check.
const golden = fileURLToPath(new URL("demo-maps.ir.json", import.meta.url));

test("the CLI writes the golden IR for the fixture, byte for byte", () => {
  const json = execFileSync(process.execPath, [fileURLToPath(new URL("../extract.mjs", import.meta.url)), fixture, "--profile", `${fixture}/profile.json`], { encoding: "utf8" });
  if (process.env.UPDATE_GOLDEN) fs.writeFileSync(golden, json);
  assert.ok(json === fs.readFileSync(golden, "utf8"),
    "The IR differs from src/interoper-extractor/test/demo-maps.ir.json. If the change is intended, run\n" +
    "  UPDATE_GOLDEN=1 node --test src/interoper-extractor/test/extract.test.mjs\nand review the diff.");
  assert.deepEqual(JSON.parse(json), ir);
});
