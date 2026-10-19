import { test } from "node:test";
import assert from "node:assert/strict";
import * as interoper from "../../src/Interoper.Runtime/wwwroot/interoper.js";
import { call, callback, census, create, faulty, get, ids, modules, release, send, sendAsync, set, value } from "./realm.mjs";

test("create returns a handle with the class id", () => {
  const view = create(ids.MapView, { zoom: 3 });
  assert.deepEqual(Object.keys(view), ["$h", "$t"]);
  assert.equal(view.$t, ids.MapView);
  assert.equal(get(view, "zoom"), 3);
  release(view);
});

test("get and set", () => {
  const view = create(ids.MapView);
  assert.equal(set(view, "zoom", 99), undefined);
  assert.equal(get(view, "zoom"), 20); // the setter clamps
  set(view, "basemap", "3d-relief");
  assert.equal(get(view, "basemap"), "3d-relief");
  release(view);
});

test("one JS object has one id", () => {
  const view = create(ids.MapView);
  const markers = get(view, "markers");
  assert.deepEqual(get(view, "markers"), markers);
  assert.equal(markers.$t, ids.Collection);
  const marker = create(ids.Marker);
  call(markers, "add", marker);
  assert.deepEqual(call(markers, "at", 0), marker);
  release(view, markers, marker);
});

test("handles report the most-derived class id (design §3.2)", () => {
  // Collection<Shape>: read through a Shape position, a Polygon still reports Polygon.
  const shapes = create(ids.Collection, [{ $c: ids.Polygon, type: "polygon", vertices: [[0, 0], [1, 1], [1, 0]] }]);
  const shape = call(shapes, "at", 0);
  assert.equal(shape.$t, ids.Polygon);
  assert.equal(call(shapes, "at", 1), undefined);
  release(shapes, shape);
});

test("an object of a class the metadata doesn't know is its nearest registered ancestor (R19 step 1)", async () => {
  const fast = value(await sendAsync({ op: "call", module: faulty, name: "fastMarker" }));
  assert.equal(fast.$t, ids.Marker);
  const guarded = value(await sendAsync({ op: "call", module: faulty, name: "guardedMarker" })); // its prototype's read throws
  assert.equal(guarded.$t, ids.Marker);
  const source = value(await sendAsync({ op: "call", module: faulty, name: "source" })); // no registered ancestor
  assert.equal(source.$t, 0);
  const endless = value(await sendAsync({ op: "call", module: faulty, name: "endlessChain" })); // the walk stops (design §3.4)
  assert.equal(endless.$t, 0);
  release(fast, guarded, source, endless);
});

test("a variant has its own class id and its base's entry (§5)", async () => {
  const glow = value(await sendAsync({ op: "call", module: faulty, name: "glowMarker" }));
  assert.equal(glow.$t, ids.GlowMarker);
  set(glow, "label", "lit");
  assert.deepEqual(Object.keys(value(send({ op: "snapshot", h: glow.$h }))), ["position", "label", "style"]); // Marker's members
  release(glow);
});

test("an object of a plain class with no runtime class name is found by its constructor (R19 step 1)", async () => {
  const plain = value(await sendAsync({ op: "call", module: faulty, name: "plain" }));
  assert.equal(plain.$t, ids.Plain);
  const internal = value(await sendAsync({ op: "call", module: faulty, name: "internalPlain" }));
  assert.equal(internal.$t, ids.Plain);
  release(plain, internal);
});

test("an entry's export is default and its members none when left out (design §2)", async () => {
  const layer = create(ids.TileLayer, { url: "tiles" });
  assert.equal(layer.$t, ids.TileLayer);
  assert.deepEqual(value(send({ op: "snapshot", h: layer.$h })), { url: "tiles" });
  const plain = value(await sendAsync({ op: "call", module: faulty, name: "plain" }));
  assert.deepEqual(value(send({ op: "snapshot", h: plain.$h })), {});
  release(layer, plain);
});

test("call returns handles, or value-like instances by value with byValue (R16)", () => {
  const view = create(ids.MapView, { zoom: 0 });
  const point = call(view, "screenToMap", 3, 4);
  assert.equal(point.$t, ids.Point);
  assert.equal(get(point, "x"), 3);
  const data = value(send({ op: "call", h: view.$h, name: "screenToMap", args: [3, 4], byValue: true }));
  assert.deepEqual(data, { $c: ids.Point, type: "point", x: 3, y: -4 });
  release(view, point);
});

test("call awaits Promise results through the async seam (R8)", async () => {
  const view = create(ids.MapView, { zoom: 0 });
  const marker = create(ids.Marker, { position: { $c: ids.Point, type: "point", x: 5, y: 0 } });
  call(get(view, "markers"), "add", marker);
  assert.deepEqual(await sendAsync({ op: "call", h: view.$h, name: "markerAt", args: [5, 0] }), { v: marker });
  assert.deepEqual(await sendAsync({ op: "call", h: view.$h, name: "markerAt", args: [50, 0] }), { v: null });
  release(view, marker, get(view, "markers"));
});

test("modules load on first use through invoke; invokeSync never starts an import", async () => {
  const op = { op: "call", module: modules.measure, name: "distance", args: [{ x: 0, y: 0 }, { x: 3, y: 4 }] };
  const notLoaded = `Module not loaded: ${modules.measure}; send load first`;
  assert.equal(send(op).e.message, notLoaded);
  await new Promise((resolve) => setTimeout(resolve, 10));
  assert.equal(send(op).e.message, notLoaded); // nothing was imported in the background
  assert.equal(send({ op: "load", modules: [modules.measure] }).e.name, "InteroperError");
  assert.deepEqual(await sendAsync(op), { v: 5 });
  assert.deepEqual(send(op), { v: 5 });
});

test("snapshot reads every member: value-like members by value, entities as handles (R17, R18)", () => {
  const view = create(ids.MapView, { zoom: 2, center: { $c: ids.Point, type: "point", x: 1, y: 2 } });
  const markers = get(view, "markers");
  assert.deepEqual(value(send({ op: "snapshot", h: view.$h })), {
    center: { $c: ids.Point, type: "point", x: 1, y: 2 },
    zoom: 2,
    rotation: 0,
    basemap: "plain",
    selected: null, // title and cursor are undefined, so they are omitted (R2)
    markers,
  });
  const line = create(ids.Line, { path: [[0, 1], [2, 3]] });
  assert.deepEqual(value(send({ op: "snapshot", h: line.$h })), { $c: ids.Line, type: "line", path: [[0, 1], [2, 3]] });
  release(view, markers, line);
});

test("apply sets the patch's members in order (R18)", () => {
  const view = create(ids.MapView);
  const patch = { zoom: 7, title: null, center: { $c: ids.Point, type: "point", x: 9, y: 8 } };
  assert.deepEqual(send({ op: "apply", h: view.$h, value: patch }), {});
  const snapshot = value(send({ op: "snapshot", h: view.$h }));
  assert.deepEqual([snapshot.zoom, snapshot.title, snapshot.center], [7, null, { $c: ids.Point, type: "point", x: 9, y: 8 }]);
  release(view, get(view, "markers"));
});

test("typed fast paths read and write primitives", () => {
  const view = create(ids.MapView);
  interoper.setValue(view.$h, "zoom", 4);
  interoper.setValue(view.$h, "basemap", "satellite");
  assert.equal(interoper.getValue(view.$h, "zoom"), 4);
  assert.equal(interoper.getValue(view.$h, "basemap"), "satellite");
  assert.throws(() => interoper.getValue(-1, "zoom"), { name: "InteroperError" });
  assert.throws(() => interoper.getValue(view.$h, "title"), { message: "title is undefined; a typed fast path needs a value" });
  assert.throws(() => interoper.getValue(view.$h, "selected"), { message: "selected is null; a typed fast path needs a value" });
  release(view);
});

test("takeError returns what the last fast path call threw, once", () => {
  const view = create(ids.MapView);
  assert.throws(() => interoper.getValue(-1, "zoom"));
  interoper.getValue(view.$h, "zoom");
  assert.equal(interoper.takeError(), null);
  assert.throws(() => interoper.setValue(-1, "zoom", 1));
  const { e } = JSON.parse(interoper.takeError());
  assert.deepEqual([e.name, e.message, typeof e.stack], ["InteroperError", "Unknown handle: -1", "string"]);
  assert.equal(interoper.takeError(), null);
  release(view);
});

test("release drops handles, is idempotent, and the census returns to baseline", () => {
  const baseline = census();
  const view = create(ids.MapView);
  const markers = get(view, "markers");
  assert.equal(census(), baseline + 2);
  release(view, markers);
  release(view);
  assert.equal(census(), baseline);
  assert.equal(send({ op: "get", h: view.$h, name: "zoom" }).e.message, `Unknown handle: ${view.$h}`);
  // A released object gets a new id the next time it crosses.
  const again = create(ids.MapView);
  assert.notEqual(again.$h, view.$h);
  release(again);
  assert.equal(census(), baseline);
});

test("release releases every id even when a dispose() throws", async () => {
  const baseline = census();
  const source = value(await sendAsync({ op: "call", module: faulty, name: "source" }));
  const subscribe = () => value(send({ op: "subscribe", h: source.$h, method: "onFaulty", name: "x", callback: null }));
  const [a, b] = [subscribe(), subscribe()];
  const { e } = send({ op: "release", ids: [a, source.$h, b] });
  assert.equal(e.name, "AggregateError");
  assert.equal(census(), baseline);
  assert.equal(send({ op: "release", ids: [a] }).e, undefined);
});

test("a subscription may have remove() instead of dispose(), which release calls", () => {
  const baseline = census();
  const pin = create(ids.Pin);
  const drags = [];
  const subscription = value(send({ op: "subscribe", h: pin.$h, method: "on", name: "drag", callback: callback((e) => { drags.push(e); }) }));
  call(pin, "dragBy", 1, 2);
  release(subscription);
  call(pin, "dragBy", 3, 4);
  assert.deepEqual(drags, [{ dx: 1, dy: 2 }]);
  release(pin);
  assert.equal(census(), baseline);
});

test("subscribe refuses a returned object with neither dispose() nor remove()", async () => {
  const source = value(await sendAsync({ op: "call", module: faulty, name: "source" }));
  const { e } = send({ op: "subscribe", h: source.$h, method: "onNothing", name: "x", callback: null });
  assert.equal(e.message, "onNothing returned no dispose() or remove()");
  release(source);
});

test("subscribe refuses a returned object that already has a handle", async () => {
  const source = value(await sendAsync({ op: "call", module: faulty, name: "source" }));
  const { e } = send({ op: "subscribe", h: source.$h, method: "onSelf", name: "x", callback: null });
  assert.equal(e.message, "onSelf returned an object that already has a handle");
  release(source);
});

test("results that can't be encoded and errors with no string form become error envelopes", async () => {
  value(await sendAsync({ op: "load", modules: [faulty] }));
  for (const respond of [send, sendAsync]) {
    assert.equal((await respond({ op: "call", module: faulty, name: "big" })).e.name, "TypeError");
    assert.deepEqual(await respond({ op: "call", module: faulty, name: "throwBare" }), { e: { name: "Error", message: "Unknown error" } });
  }
});
