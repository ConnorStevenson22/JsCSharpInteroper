import { test } from "node:test";
import assert from "node:assert/strict";
import { Line, Point, Polygon, Shape } from "../geometry.js";
import { distance, perimeter } from "../measure.js";

test("Shape is abstract; subclasses carry a type discriminant", () => {
  assert.throws(() => new Shape(), TypeError);
  assert.deepEqual([new Point(), new Line(), new Polygon()].map((s) => s.type), ["point", "line", "polygon"]);
  assert.ok(new Line() instanceof Shape);
});

test("constructor bags are partial", () => {
  const point = new Point({ y: 5 });
  assert.deepEqual([point.x, point.y], [0, 5]);
});

test("constructors accept a snapshot bag that keeps its discriminant", () => {
  // R16, R20: the runtime calls new Point(bag) with the bag's `type` still in it.
  const point = new Point({ type: "point", x: 1, y: 2 });
  assert.deepEqual([point.type, point.x, point.y], ["point", 1, 2]);
  assert.deepEqual(new Line({ type: "line", path: [[0, 0], [1, 1]] }).path, [[0, 0], [1, 1]]);
  assert.deepEqual(new Polygon({ type: "polygon", vertices: [[0, 0], [1, 0], [0, 1]] }).vertices.length, 3);
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

test("perimeter takes any number of points and closes the path", () => {
  assert.equal(perimeter(new Point({ x: 0, y: 0 }), new Point({ x: 3, y: 0 }), { type: "point", x: 3, y: 4 }), 12);
  assert.equal(perimeter(), 0);
});
