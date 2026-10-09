import { test } from "node:test";
import assert from "node:assert/strict";
import { Line, Point, Polygon, Shape } from "../geometry.js";
import { area, distance, length } from "../operators.js";

test("Shape is abstract; subclasses carry a type discriminant", () => {
  assert.throws(() => new Shape(), TypeError);
  assert.deepEqual([new Point(), new Line(), new Polygon()].map((s) => s.type), ["point", "line", "polygon"]);
  assert.ok(new Line() instanceof Shape);
});

test("constructor bags are partial", () => {
  const point = new Point({ y: 5 });
  assert.deepEqual([point.x, point.y], [0, 5]);
});

test("extent and clone", () => {
  const polygon = new Polygon({ rings: [[[0, 0], [4, 0], [4, 3]]] });
  assert.deepEqual(polygon.extent, { xmin: 0, ymin: 0, xmax: 4, ymax: 3 });
  const copy = polygon.clone();
  assert.ok(copy instanceof Polygon);
  assert.notEqual(copy, polygon);
  assert.deepEqual(copy.rings, polygon.rings);
});

test("module functions accept instances and plain JSON alike", () => {
  assert.equal(distance(new Point({ x: 0, y: 0 }), { x: 3, y: 4 }), 5);
  assert.equal(length(new Line({ path: [[0, 0], [3, 4], [3, 10]] })), 11);
  assert.equal(area({ type: "polygon", rings: [[[0, 0], [4, 0], [4, 3], [0, 3]]] }), 12);
});
