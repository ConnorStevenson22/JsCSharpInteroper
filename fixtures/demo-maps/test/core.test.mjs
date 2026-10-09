import { test } from "node:test";
import assert from "node:assert/strict";
import { Collection, Evented } from "../core.js";
import { Point } from "../geometry.js";

test("watch reports changes with the old value, and only real changes", () => {
  const point = new Point({ x: 1 });
  const seen = [];
  const handle = point.watch("x", (value, old) => seen.push([value, old]));
  point.x = 2;
  point.x = 2;
  handle.remove();
  point.x = 3;
  assert.deepEqual(seen, [[2, 1]]);
});

test("declaredClass is the runtime class name of the most-derived class", () => {
  assert.equal(new Point().declaredClass, "geometry.Point");
  assert.equal(new Collection().declaredClass, "core.Collection");
});

test("on/emit deliver events until the handle is removed", () => {
  const evented = new Evented();
  const seen = [];
  const handle = evented.on("ping", (e) => seen.push(e));
  assert.equal(evented.emit("ping", 1), true);
  handle.remove();
  assert.equal(evented.emit("ping", 2), false);
  assert.deepEqual(seen, [1]);
});

test("Collection reports adds and removes as change events", () => {
  const items = new Collection(["a"]);
  const changes = [];
  items.on("change", (e) => changes.push(e));
  items.add("b");
  assert.equal(items.remove("a"), true);
  assert.equal(items.remove("missing"), false);
  assert.deepEqual(changes, [{ added: ["b"], removed: [] }, { added: [], removed: ["a"] }]);
  assert.deepEqual(items.toArray(), ["b"]);
  assert.equal(items.length, 1);
  assert.equal(items.at(5), undefined);
  assert.equal(items.find((item) => item === "b"), "b");
});
