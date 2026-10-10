import { test } from "node:test";
import assert from "node:assert/strict";
import { Layer } from "../layers/Layer.js";
import TileLayer from "../layers/TileLayer.js";
import { MapView } from "../MapView.js";

test("Layer is abstract; TileLayer is the module's default export", () => {
  assert.throws(() => new Layer(), TypeError);
  const tile = new TileLayer({ url: "https://tiles/{z}/{x}/{y}.png", title: "Streets" });
  assert.ok(tile instanceof Layer);
  assert.deepEqual([tile.url, tile.title, tile.visible], ["https://tiles/{z}/{x}/{y}.png", "Streets", true]);
  assert.deepEqual([new TileLayer().url, new TileLayer().title], ["", null]);
});

test("className is the module path, since the file is named after the class", () => {
  assert.equal(new TileLayer().className, "layers.TileLayer");
});

test("clone keeps the most-derived class and the members", () => {
  const tile = new TileLayer({ url: "u", title: "t", visible: false });
  const copy = tile.clone();
  assert.ok(copy instanceof TileLayer);
  assert.notEqual(copy, tile);
  assert.deepEqual([copy.url, copy.title, copy.visible], ["u", "t", false]);
});

test("a view's layers are a collection", () => {
  const view = new MapView();
  const tile = new TileLayer();
  view.layers.add(tile);
  assert.deepEqual(view.layers.toArray(), [tile]);
});
