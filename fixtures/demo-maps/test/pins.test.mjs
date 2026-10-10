import { test } from "node:test";
import assert from "node:assert/strict";
import { Pin } from "../pins.js";
import { Marker } from "../Marker.js";
import { Point } from "../geometry.js";
import { MapView } from "../MapView.js";

test("a Pin is a Marker with the Draggable mixin", () => {
  const pin = new Pin({ position: [1, 2], label: "p" });
  assert.ok(pin instanceof Marker);
  assert.equal(pin.className, "pins.Pin");
  assert.deepEqual([pin.position.x, pin.position.y, pin.displayLabel], [1, 2, "p"]);
  const view = new MapView();
  view.selected = pin; // where a Marker is expected
  assert.equal(view.selected, pin);
});

test("dragBy moves the position and emits drag; the subscription has remove(), not dispose()", () => {
  const pin = new Pin();
  const drags = [];
  const subscription = pin.on("drag", (e) => drags.push(e));
  assert.equal(typeof subscription.remove, "function");
  assert.equal(subscription.dispose, undefined);
  pin.dragBy(2, 3);
  assert.ok(pin.position instanceof Point);
  assert.deepEqual([pin.position.x, pin.position.y], [2, 3]);
  subscription.remove();
  pin.dragBy(1, 1);
  assert.deepEqual(drags, [{ dx: 2, dy: 3 }]);
});

test("meta holds anything, and is watched like any property", () => {
  const pin = new Pin();
  const seen = [];
  pin.watch("meta", (value) => seen.push(value));
  pin.meta = { id: 7, tags: ["a"] };
  pin.meta = 3;
  assert.deepEqual(seen, [{ id: 7, tags: ["a"] }, 3]);
});
