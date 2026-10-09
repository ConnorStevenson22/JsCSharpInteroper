import { test } from "node:test";
import assert from "node:assert/strict";
import { Point } from "../geometry.js";
import { Marker } from "../Marker.js";

test("position: the setter takes a pair, the getter always returns a Point", () => {
  const marker = new Marker({ position: [1, 2] });
  assert.ok(marker.position instanceof Point);
  assert.deepEqual([marker.position.x, marker.position.y], [1, 2]);
  const point = new Point({ x: 3 });
  marker.position = point;
  assert.equal(marker.position, point);
});

test("defaults: an icon style, and an undefined label", () => {
  const marker = new Marker();
  assert.deepEqual(marker.style, { type: "icon", name: "pin-1" });
  assert.equal(marker.label, undefined);
  assert.equal(marker.displayLabel, "");
});

test("displayLabel uses the formatter's synchronous return value", () => {
  const marker = new Marker({ label: "home", position: [2, 3] });
  assert.equal(marker.displayLabel, "home");
  marker.labelFormatter = (m) => `${m.label} @ ${m.position.x},${m.position.y}`;
  assert.equal(marker.displayLabel, "home @ 2,3");
  marker.labelFormatter = null;
  marker.label = null;
  assert.equal(marker.displayLabel, "");
});
