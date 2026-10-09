import { test } from "node:test";
import assert from "node:assert/strict";
import * as interoper from "../../src/Interoper.Runtime/wwwroot/interoper.js";
import { call, census, create, get, ids, modules, release, send, sendAsync, set, value } from "./realm.mjs";

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

test("handles report the most-derived class id (R19)", () => {
  // Collection<Shape>: read through a Shape position, a Polygon still reports Polygon.
  const shapes = create(ids.Collection, [{ $c: ids.Polygon, type: "polygon", vertices: [[0, 0], [1, 1], [1, 0]] }]);
  const shape = call(shapes, "at", 0);
  assert.equal(shape.$t, ids.Polygon);
  assert.equal(call(shapes, "at", 1), undefined);
  release(shapes, shape);
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

test("module functions: loaded on first use, then synchronous", async () => {
  const op = { op: "call", module: modules.measure, name: "distance", args: [{ x: 0, y: 0 }, { x: 3, y: 4 }] };
  assert.equal(send(op).e.message, "The op returned a Promise; send it through invoke.");
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
    cursor: "default",
    selected: null, // title is undefined, so it is omitted (R2)
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
  assert.notEqual(create(ids.MapView).$h, view.$h);
});
