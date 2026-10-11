import { test } from "node:test";
import assert from "node:assert/strict";
import { ids } from "./elements.meta.mjs";

// A page as Blazor makes it, for a fresh interoper.js instance: elements with the capture attribute an @ref gives
// them (R13), and the Blazor global that custom events are registered with (R28).
class Element {}
class FakeElement extends Element {
  constructor(localName, captureId, className = "") {
    super();
    Object.assign(this, { localName, captureId, className });
  }
}
// The element's `className` is its CSS class, which happens to be a runtime class name.
const rendered = [new FakeElement("demo-map", "a1", "MapView"), new FakeElement("div", "a2", "MapView")];
const registrations = [];
Object.assign(globalThis, {
  Element,
  document: { querySelector: (selector) => rendered.find((e) => selector === `[_bl_${e.captureId}]`) ?? null },
  Blazor: { registerCustomEventType: (name, options) => registrations.push({ name, options }) },
});

const metadata = new URL("./elements.meta.mjs", import.meta.url).href;
const interoper = await import("../../src/Interoper.Runtime/wwwroot/interoper.js?elements");
await interoper.start(metadata, () => null);
const send = (op) => JSON.parse(interoper.invokeSync(JSON.stringify(op)));

test("wrap returns an ElementReference's element as a handle, its class id found by its tag (R13, R28)", () => {
  const element = send({ op: "wrap", value: { $e: "a1" } }).v;
  assert.equal(element.$t, ids.DemoMapElement);
  const untagged = send({ op: "wrap", value: { $e: "a2" } }).v;
  assert.equal(untagged.$t, 0); // not MapView: an element's className is its CSS class
  send({ op: "release", ids: [untagged.$h] });
  assert.deepEqual(send({ op: "wrap", value: { $e: "a1" } }).v, element); // one element, one handle
  // Any other value comes back in wire form, as a result would.
  assert.deepEqual(send({ op: "wrap", value: { a: [1, { $u: 1 }] } }).v, { a: [1, { $u: 1 }] });
  send({ op: "release", ids: [element.$h] });
  assert.equal(interoper.census(), 0);
});

test("a value-like class without a module isn't imported at start, and can't be sent as a value (design §3.5)", () => {
  // start() above didn't fail on it; the other value-like class was imported.
  assert.match(send({ op: "wrap", value: { $c: ids.Unimportable, x: 1 } }).e.message, /internal\.Unimportable is not loaded/);
  assert.equal(send({ op: "wrap", value: { $c: ids.Point, x: 1, y: 2 } }).v.$t, ids.Point);
});

test("an ElementReference that isn't rendered is an error (R13)", () => {
  assert.deepEqual(send({ op: "wrap", value: { $e: "zz" } }).e.message,
    "No element has the capture attribute _bl_zz; send an ElementReference after its first render");
});

test("custom events are registered with Blazor once per page, by their lower-case name (R28)", async () => {
  // A second transport's instance starts too, and registers nothing again.
  const other = await import("../../src/Interoper.Runtime/wwwroot/interoper.js?elements-2");
  await other.start(metadata, () => null);
  assert.deepEqual(registrations.map((r) => [r.name, r.options.browserEventName]),
    [["demo-map-click", undefined], ["demo-map-ready", undefined], ["zoomchange", "zoomChange"]]);
});

test("a custom event's args carry its detail as wire JSON, projected to its fields (R24, R28)", () => {
  const args = (name, detail) => JSON.parse(registrations.find((r) => r.name === name).options.createEventArgs({ detail }).json);
  const before = interoper.census();
  const live = new FakeElement("demo-map", "c3");
  assert.deepEqual(args("demo-map-click", { x: 1, y: 2, live, mapPoint: { x: 3 } }), { x: 1, mapPoint: { x: 3 } });
  assert.equal(interoper.census(), before); // what isn't projected gets no handle
  assert.equal(args("demo-map-ready", null), null);
  assert.deepEqual(args("demo-map-ready", undefined), { $u: 1 });
});
