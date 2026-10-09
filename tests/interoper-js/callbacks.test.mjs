import { test } from "node:test";
import assert from "node:assert/strict";
import * as interoper from "../../src/Interoper.Runtime/wwwroot/interoper.js";
import { call, callback, census, create, get, ids, metadata, release, send, set, sink, value } from "./realm.mjs";

const subscribe = (h, method, name, fn) => value(send({ op: "subscribe", h: h.$h, method, name, callback: callback(fn) }));

test("$f: a callback with a synchronous return value, as on WebAssembly (R21)", () => {
  const seen = [];
  const marker = create(ids.Marker, {
    label: "a",
    labelFormatter: callback((m) => {
      seen.push(m);
      return `#${get(m, "label")}`;
    }),
  });
  assert.equal(get(marker, "displayLabel"), "#a");
  assert.deepEqual(seen, [marker]); // entities arrive as their handle
  release(marker);
});

test("$f: arguments go by value and the result is revived", () => {
  const list = create(ids.Collection, [{ $c: ids.Point, type: "point", x: 1, y: 2 }, { $c: ids.Point, type: "point", x: 3, y: 4 }]);
  const args = [];
  const found = call(list, "find", callback((item, index) => {
    args.push([item, index]);
    return index === 1;
  }));
  assert.equal(found.$t, ids.Point);
  assert.deepEqual(args, [
    [{ $c: ids.Point, type: "point", x: 1, y: 2 }, 0],
    [{ $c: ids.Point, type: "point", x: 3, y: 4 }, 1],
  ]);
  release(list, found);
});

test("subscribe to an event; release disposes the subscription (R24)", () => {
  const baseline = census();
  const view = create(ids.MapView, { zoom: 0 });
  const clicks = [];
  const subscription = subscribe(view, "on", "click", (e) => { clicks.push(e); });
  assert.equal(typeof subscription, "number");

  // The library raises the event; the payload's Point arrives by value.
  const mapPoint = { $c: ids.Point, type: "point", x: 1, y: 2 };
  assert.equal(call(view, "emit", "click", { type: "click", x: 1, y: -2, button: 0, mapPoint }), true);
  assert.deepEqual(clicks, [{ type: "click", x: 1, y: -2, button: 0, mapPoint }]);

  release(subscription);
  assert.equal(call(view, "emit", "click", { type: "click", x: 0, y: 0, button: 0, mapPoint }), false);
  assert.equal(clicks.length, 1);
  release(view);
  assert.equal(census(), baseline);
});

test("subscribe to a property watch", () => {
  const view = create(ids.MapView, { zoom: 1 });
  const changes = [];
  const subscription = subscribe(view, "watch", "zoom", (zoom, old) => { changes.push([zoom, old]); });
  set(view, "zoom", 5);
  release(subscription);
  set(view, "zoom", 6);
  assert.deepEqual(changes, [[5, 1]]);
  release(view);
});

test("subscribe needs a method that returns something disposable", () => {
  const view = create(ids.MapView);
  assert.equal(send({ op: "subscribe", h: view.$h, method: "emit", name: "click", callback: callback(() => {}) }).e.name, "InteroperError");
  release(view);
});

test("an async callback sink (Server, Hybrid) still delivers events; sync results become Promises", async () => {
  const calls = [];
  const dotNetObject = {
    invokeMethodAsync: (method, id, argsJson) => {
      calls.push(method);
      return Promise.resolve(sink(id, argsJson));
    },
  };
  await interoper.startWithDotNetObject(metadata, dotNetObject);
  try {
    const view = create(ids.MapView, { zoom: 1 });
    const zooms = [];
    subscribe(view, "watch", "zoom", (zoom) => { zooms.push(zoom); });
    set(view, "zoom", 2);
    await Promise.resolve();
    assert.deepEqual(zooms, [2]);
    assert.deepEqual(calls, ["InvokeCallback"]);

    // A synchronous-result callback can't work over an async bridge (R21, IOP2005): JS gets a Promise, not "x".
    const marker = create(ids.Marker, { labelFormatter: callback(() => "x") });
    const label = get(marker, "displayLabel");
    assert.deepEqual(Object.keys(label), ["$h", "$t"]);
    assert.equal(label.$t, 0); // no class id: an object the metadata doesn't know
    release(view, marker, label);
  } finally {
    await interoper.start(metadata, sink);
  }
});
