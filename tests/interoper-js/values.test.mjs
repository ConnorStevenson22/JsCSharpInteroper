import { test } from "node:test";
import assert from "node:assert/strict";
import { call, create, get, ids, modules, release, send, sendAsync, set, value } from "./realm.mjs";

const point = (x, y) => ({ $c: ids.Point, type: "point", x, y }); // a PointSnapshot on the wire (R16, R20)

test("$c inside a constructor bag arrives as an instance (R16)", () => {
  // MapViewOptions.center is a Point; the fixture doesn't autocast, so a plain bag would stay a plain object.
  const view = create(ids.MapView, { center: point(1, 2) });
  const center = get(view, "center");
  assert.equal(center.$t, ids.Point); // a handle, so a class instance, not a copied bag
  assert.equal(get(center, "type"), "point");
  assert.deepEqual([get(center, "x"), get(center, "y")], [1, 2]);
  release(view, center);
});

test("$c is built innermost first: nested bags become instances before their parent", async () => {
  const shapes = create(ids.Collection, [point(1, 1), { $c: ids.Line, type: "line", path: [[0, 0], [4, 2]] }]);
  const line = call(shapes, "at", 1);
  assert.equal(line.$t, ids.Line);
  // flyTo only treats a target as a Shape (and centers on its bounds) when it is an instance.
  const view = create(ids.MapView);
  value(await sendAsync({ op: "call", h: view.$h, name: "flyTo", args: [line] }));
  assert.deepEqual(get(view, "center", true), point(2, 1));
  release(view, shapes, line);
});

test("$c as a setter value and as a Promise-method argument (R16, R8)", async () => {
  const marker = create(ids.Marker);
  set(marker, "position", point(3, 4));
  assert.deepEqual(get(marker, "position", true), point(3, 4));

  const view = create(ids.MapView);
  assert.deepEqual(await sendAsync({ op: "call", h: view.$h, name: "flyTo", args: [point(5, 6)] }), {});
  assert.deepEqual(get(view, "center", true), point(5, 6));
  release(marker, view);
});

test("$c of an unknown class id is an error", () => {
  const bag = { $c: 99, x: 1 };
  assert.equal(send({ op: "call", module: modules.measure, name: "distance", args: [bag, bag] }).e.message, "Unknown class id: 99");
});

test("$u: an undefined argument reaches JS as undefined, not null (R1, R2, R22)", async () => {
  const view = create(ids.MapView);
  // flyTo(target, options?) destructures its options: undefined takes the defaults, null throws.
  assert.deepEqual(await sendAsync({ op: "call", h: view.$h, name: "flyTo", args: [[7, 8], { $u: 1 }] }), {});
  assert.deepEqual(get(view, "center", true), point(7, 8));
  assert.equal((await sendAsync({ op: "call", h: view.$h, name: "flyTo", args: [[7, 8], null] })).e.name, "TypeError");
  release(view);
});

test("$u: undefined comes back as $u in arrays and as an omitted value elsewhere (R1, R2)", () => {
  const list = create(ids.Collection, [1, { $u: 1 }, null]);
  assert.equal(get(list, "length"), 3);
  assert.deepEqual(call(list, "toArray"), [1, { $u: 1 }, null]);
  assert.deepEqual(send({ op: "call", h: list.$h, name: "at", args: [1] }), {});
  assert.deepEqual(send({ op: "call", h: list.$h, name: "at", args: [2] }), { v: null });

  const marker = create(ids.Marker, { label: "a" });
  set(marker, "label", { $u: 1 });
  assert.deepEqual(send({ op: "get", h: marker.$h, name: "label" }), {});
  set(marker, "label", null);
  assert.deepEqual(send({ op: "get", h: marker.$h, name: "label" }), { v: null });
  release(list, marker);
});

test("plain data stays plain: data records and module results are copied", async () => {
  assert.deepEqual(value(await sendAsync({ op: "call", module: modules.styles, name: "toRgba", args: ["#ff0000"] })), { r: 255, g: 0, b: 0, a: 1 });
});
