import { test } from "node:test";
import assert from "node:assert/strict";
import { MapView } from "../../demo-maps/MapView.js";
import TileLayer from "../../demo-maps/layers/TileLayer.js";
import { Legend, legendFor } from "../dist/index.js";

test("the root re-exports the legend", async () => {
  assert.equal((await import("../dist/legend.js")).Legend, Legend);
});

test("a legend lists its view's layers and reports a selection", () => {
  const view = new MapView();
  view.layers.add(new TileLayer({ title: "Streets" }));
  view.layers.add(new TileLayer({ title: "Night", visible: false }));
  const legend = legendFor(view);
  assert.equal(legend.view, view);
  assert.deepEqual(legend.entries, [{ title: "Streets", visible: true }, { title: "Night", visible: false }]);
  const seen = [];
  legend.addEventListener("demo-legend-select", (e) => seen.push(e.detail));
  legend.select(1);
  assert.deepEqual(seen, [{ index: 1, title: "Night (hidden)" }]);
});
