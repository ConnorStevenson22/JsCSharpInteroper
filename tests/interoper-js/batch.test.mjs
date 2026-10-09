import { test } from "node:test";
import assert from "node:assert/strict";
import { invokeSync } from "../../src/Interoper.Runtime/wwwroot/interoper.js";
import { callback, census, create, get, ids, modules, release, send, sendAsync, value } from "./realm.mjs";

test("a batch runs its ops in order and returns each result", () => {
  const view = create(ids.MapView);
  const ops = [
    { op: "set", h: view.$h, name: "zoom", value: 2 },
    { op: "set", h: view.$h, name: "zoom", value: 3 },
    { op: "get", h: view.$h, name: "zoom" },
    { op: "get", h: view.$h, name: "title" },
  ];
  assert.deepEqual(value(send({ op: "batch", ops })), [{ $u: 1 }, { $u: 1 }, 3, { $u: 1 }]);
  release(view);
});

test("a batch awaits a Promise op before running the next", async () => {
  const view = create(ids.MapView);
  const ops = [
    { op: "call", h: view.$h, name: "flyTo", args: [[4, 4], { duration: 5 }] },
    { op: "get", h: view.$h, name: "center", byValue: true },
  ];
  const [, center] = value(await sendAsync({ op: "batch", ops }));
  assert.deepEqual(center, { $c: ids.Point, type: "point", x: 4, y: 4 });
  assert.equal(send({ op: "batch", ops }).e.message, "The op returned a Promise; send it through invoke.");
  release(view);
});

test("failed ops don't stop the batch; the error lists each of them", async () => {
  const view = create(ids.MapView);
  const ops = [
    { op: "set", h: view.$h, name: "zoom", value: 2 },
    { op: "call", h: view.$h, name: "noSuchMethod" },
    { op: "call", h: view.$h, name: "flyTo", args: [null] }, // rejects
    { op: "get", h: -1, name: "zoom" },
    { op: "set", h: view.$h, name: "zoom", value: 4 },
  ];
  const { e } = await sendAsync({ op: "batch", ops });
  assert.equal(e.name, "BatchError");
  assert.equal(e.message, "3 of 5 batched ops failed");
  assert.deepEqual(e.errors.map(({ index, name }) => [index, name]), [[1, "TypeError"], [2, "TypeError"], [3, "InteroperError"]]);
  assert.match(e.errors[0].stack, /TypeError/);
  assert.equal(get(view, "zoom"), 4);
  release(view);
});

test("errors carry the JS name, message and stack", async () => {
  const { e } = await sendAsync({ op: "call", module: modules.styles, name: "toRgba", args: ["nope"] });
  assert.equal(e.name, "RangeError");
  assert.equal(e.message, "Unknown color: nope");
  assert.match(e.stack, /toRgba/);
  assert.equal(send({ op: "nope" }).e.message, "Unknown op: nope");
  assert.equal(JSON.parse(invokeSync("{")).e.name, "SyntaxError");
});

test("a thrown non-Error becomes an Error with its string form", () => {
  const marker = create(ids.Marker, { labelFormatter: callback(() => { throw "boom"; }) });
  assert.deepEqual(send({ op: "get", h: marker.$h, name: "displayLabel" }), { e: { name: "Error", message: "boom" } });
  release(marker);
});

test("release in a batch returns the census to baseline", () => {
  const baseline = census();
  const view = create(ids.MapView);
  const markers = get(view, "markers");
  value(send({ op: "batch", ops: [{ op: "release", ids: [view.$h] }, { op: "release", ids: [markers.$h] }] }));
  assert.equal(census(), baseline);
});
