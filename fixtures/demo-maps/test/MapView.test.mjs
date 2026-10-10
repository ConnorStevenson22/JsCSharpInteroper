import { test } from "node:test";
import assert from "node:assert/strict";
import { Line, Point } from "../geometry.js";
import { MapView } from "../MapView.js";
import { Marker } from "../Marker.js";

test("defaults and constructor bag", () => {
  const view = new MapView({ zoom: 4, basemap: "3d-relief" });
  assert.deepEqual([view.zoom, view.rotation, view.basemap, view.cursor], [4, 0, "3d-relief", undefined]);
  assert.equal(view.title, undefined);
  assert.equal(view.selected, null);
  assert.equal(view.container, null);
  assert.equal(view.className, "MapView");
});

test("setters normalize: zoom is clamped, rotation wraps", () => {
  const view = new MapView();
  view.zoom = 99;
  view.rotation = -90;
  assert.deepEqual([view.zoom, view.rotation], [20, 270]);
});

test("watch on zoom", () => {
  const view = new MapView();
  const seen = [];
  view.watch("zoom", (zoom, old) => seen.push([zoom, old]));
  view.zoom = 2;
  assert.deepEqual(seen, [[2, 1]]);
});

test("screenToMap takes x and y, or an [x, y] pair", () => {
  const view = new MapView({ center: new Point({ x: 10, y: 10 }), zoom: 1 });
  assert.deepEqual([view.screenToMap(4, 2).x, view.screenToMap([4, 2]).y], [12, 9]);
});

test("click events carry the map point", () => {
  const view = new MapView({ center: new Point({ x: 10, y: 10 }), zoom: 1 });
  const seen = [];
  view.on("click", (e) => seen.push(e));
  view.emit("click", { type: "click", x: 4, y: 2, button: 0, mapPoint: view.screenToMap(4, 2) });
  assert.deepEqual([seen[0].mapPoint.x, seen[0].mapPoint.y], [12, 9]);
});

test("flyTo accepts every FlyToTarget case", async () => {
  const view = new MapView();
  const center = () => [view.center.x, view.center.y];
  await view.flyTo(new Line({ path: [[0, 0], [4, 2]] }));
  assert.deepEqual(center(), [2, 1]);
  await view.flyTo([5, 6]);
  assert.deepEqual(center(), [5, 6]);
  await view.flyTo({ minX: 0, minY: 0, maxX: 10, maxY: 20 });
  assert.deepEqual(center(), [5, 10]);
  await view.flyTo({ center: [1, 1], zoom: 7 });
  assert.deepEqual([...center(), view.zoom], [1, 1, 7]);
});

test("flyTo rejects when aborted", async () => {
  const view = new MapView();
  const controller = new AbortController();
  const moving = view.flyTo([1, 1], { duration: 1000, signal: controller.signal });
  controller.abort();
  await assert.rejects(moving, { name: "AbortError" });
  assert.equal(view.center.x, 0);
});

test("markerAt resolves to the marker under the offset, or null", async () => {
  const view = new MapView({ zoom: 0 });
  const marker = new Marker({ position: [100, -50] });
  view.markers.add(marker);
  assert.equal(await view.markerAt(105, 50), marker);
  assert.equal(await view.markerAt(0, 0), null);
});

test("no autocast: a plain snapshot is not turned into a Point", () => {
  // Design §3.4: interoper.js builds instances from `$c`, so the library never has to.
  const view = new MapView({ center: { type: "point", x: 1, y: 2 } });
  assert.equal(view.center instanceof Point, false);
  const marker = new Marker({ position: { type: "point", x: 1, y: 2 } });
  assert.equal(marker.position instanceof Point, false);
});
