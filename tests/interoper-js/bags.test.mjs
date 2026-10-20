import { test } from "node:test";
import assert from "node:assert/strict";
import { ids, metadata } from "./realm.mjs";

// A fresh interoper.js instance, which has imported only the value-like classes' modules: an entity's bag is then the
// first use of its module (R16, R18, design §3.5).
const interoper = await import("../../src/Interoper.Runtime/wwwroot/interoper.js?bags");
await interoper.start(metadata, () => null);
const pin = JSON.stringify({ op: "wrap", value: { $c: ids.Pin, label: "p" } });

test("invokeSync can't build an entity from $c before its module is loaded; sync mode preloads it", () => {
  assert.match(JSON.parse(interoper.invokeSync(pin)).e.message, /^Class pins\.Pin is not loaded/);
});

test("invoke imports the module of each entity a $c names, then builds it with new Class(bag) (R16, R18)", async () => {
  const built = JSON.parse(await interoper.invoke(pin)).v;
  assert.equal(built.$t, ids.Pin);
  const label = (h) => JSON.parse(interoper.invokeSync(JSON.stringify({ op: "get", h, name: "displayLabel" }))).v;
  assert.equal(label(built.$h), "p");
  // Now loaded, so invokeSync builds one too, also nested in a batch's ops.
  const view = JSON.parse(await interoper.invoke(JSON.stringify({ op: "create", t: ids.MapView, args: [] }))).v;
  const bags = Array.from({ length: 3 }, (_, i) => ({ $c: ids.Marker, label: `${i}` }));
  const edit = { op: "batch", ops: [{ op: "call", h: view.$h, name: "edit", args: [{ add: bags, select: { $c: ids.Pin, label: "s" } }] }] };
  assert.deepEqual(JSON.parse(await interoper.invoke(JSON.stringify(edit))), {});
  const selected = JSON.parse(interoper.invokeSync(JSON.stringify({ op: "get", h: view.$h, name: "selected" }))).v;
  assert.equal(selected.$t, ids.Pin);
  assert.equal(label(selected.$h), "s");
  interoper.invokeSync(JSON.stringify({ op: "release", ids: [built.$h, view.$h, selected.$h] }));
  assert.equal(interoper.census(), 0);
});
