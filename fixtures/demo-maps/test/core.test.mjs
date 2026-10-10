import { test } from "node:test";
import assert from "node:assert/strict";
import { Collection, Emitter } from "../core.js";
import { Point } from "../geometry.js";

test("watch reports changes with the old value, and only real changes", () => {
  const point = new Point({ x: 1 });
  const seen = [];
  const subscription = point.watch("x", (value, old) => seen.push([value, old]));
  point.x = 2;
  point.x = 2;
  subscription.dispose();
  point.x = 3;
  assert.deepEqual(seen, [[2, 1]]);
});

test("className is the runtime class name of the most-derived class", () => {
  assert.equal(new Point().className, "geometry.Point");
  assert.equal(new Collection().className, "core.Collection");
});

test("on/emit deliver events until the subscription is disposed", () => {
  const emitter = new Emitter();
  const seen = [];
  const subscription = emitter.on("ping", (e) => seen.push(e));
  assert.equal(emitter.emit("ping", 1), true);
  subscription.dispose();
  assert.equal(emitter.emit("ping", 2), false);
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

test("Collection.forEach calls back with each item and its index, and nothing else", () => {
  const items = new Collection(["a", "b"]);
  const seen = [];
  items.forEach((...args) => seen.push(args));
  assert.deepEqual(seen, [["a", 0], ["b", 1]]);
});
