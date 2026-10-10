import { test } from "node:test";
import assert from "node:assert/strict";
import { call, callback, census, create, ids, release, send, sendAsync, value } from "./realm.mjs";

// The browser check's EventTarget fake, served by the E2E client.
const dom = new URL("../Interoper.E2E.Client/wwwroot/event-target.js", import.meta.url).href;

const subscribe = (h, name, fn, options) => value(send({ op: "subscribe", h: h.$h, method: "on", name, callback: callback(fn), options }));
const mapPoint = { $c: ids.Point, type: "point", x: 1, y: 2 };

test("subscribe options: preventDefault and stopPropagation run before .NET sees the event (R24)", () => {
  const view = create(ids.MapView);
  const order = [];
  const subscription = subscribe(view, "click", () => { order.push("listener"); }, { preventDefault: true, stopPropagation: true, fields: [] });
  // A library event with these methods works as a DOM event does; one without them is fine too.
  call(view, "emit", "click", { preventDefault: callback(() => { order.push("preventDefault"); }), stopPropagation: callback(() => { order.push("stopPropagation"); }) });
  call(view, "emit", "click", { x: 1 });
  call(view, "emit", "click", null);
  assert.deepEqual(order, ["preventDefault", "stopPropagation", "listener", "listener", "listener"]);
  release(subscription, view);
});

test("subscribe options: fields project the payload to those top-level members (R24)", () => {
  const baseline = census();
  const view = create(ids.MapView);
  const clicks = [];
  const subscription = subscribe(view, "click", (e) => { clicks.push(e); }, { fields: ["x", "y", "mapPoint", "missing"] });
  // The method and the live object never cross the wire, so they get no handle.
  const marker = create(ids.Marker);
  call(view, "emit", "click", { x: 1, y: -2, button: 0, mapPoint, marker, preventDefault: callback(() => {}) });
  call(view, "emit", "click", [1, 2]); // not an object: as it is
  assert.deepEqual(clicks, [{ x: 1, y: -2, mapPoint }, [1, 2]]);
  release(subscription, view, marker);
  assert.equal(census(), baseline);
});

test("subscribe options: a DOM event, by its prototype's getters and methods (R24)", async () => {
  const target = value(await sendAsync({ op: "call", module: dom, name: "target" }));
  const types = [];
  const prevented = subscribe(target, "click", (e) => { types.push(e); }, { preventDefault: true, fields: ["type"] });
  assert.equal(call(target, "dispatch", "click"), true);
  assert.deepEqual(types, [{ type: "click" }]);
  release(prevented);

  // Without options the listener is the callback itself.
  const plain = subscribe(target, "click", () => {});
  assert.equal(call(target, "dispatch", "click"), false);
  release(plain, target);
});
