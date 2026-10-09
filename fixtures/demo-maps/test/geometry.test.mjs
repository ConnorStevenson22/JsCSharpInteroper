import { test } from "node:test";
import assert from "node:assert/strict";
import { Line, Point, Polygon, Shape } from "../geometry.js";
import { distance } from "../measure.js";

test("Shape is abstract; subclasses carry a type discriminant", () => {
  assert.throws(() => new Shape(), TypeError);
  assert.deepEqual([new Point(), new Line(), new Polygon()].map((s) => s.type), ["point", "line", "polygon"]);
  assert.ok(new Line() instanceof Shape);
});

test("constructor bags are partial", () => {
  const point = new Point({ y: 5 });
  assert.deepEqual([point.x, point.y], [0, 5]);
});

test("bounds and clone", () => {
  const polygon = new Polygon({ vertices: [[0, 0], [4, 0], [4, 3]] });
  assert.deepEqual(polygon.bounds, { minX: 0, minY: 0, maxX: 4, maxY: 3 });
  const copy = polygon.clone();
  assert.ok(copy instanceof Polygon);
  assert.notEqual(copy, polygon);
  assert.deepEqual(copy.vertices, polygon.vertices);
});

test("an empty shape has a zero box", () => {
  assert.deepEqual(new Line().bounds, { minX: 0, minY: 0, maxX: 0, maxY: 0 });
});

test("distance accepts instances and plain JSON alike", () => {
  assert.equal(distance(new Point({ x: 0, y: 0 }), { type: "point", x: 3, y: 4 }), 5);
});
