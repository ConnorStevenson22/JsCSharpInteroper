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

test("the root re-exports the swatch, which no export reaches directly", async () => {
  const { Swatch, swatchFor } = await import("../dist/index.js");
  assert.equal((await import("../dist/support/swatch.js")).Swatch, Swatch);
  assert.deepEqual([swatchFor({ title: "a", visible: false }).color, new Swatch().color], ["grey", "black"]);
  assert.equal(new Swatch().className, "dist.support.swatch.Swatch");
});

test("a component folder's index re-exports its element, which shows and copies its view's zoom", async () => {
  const { ZoomLabelElement } = await import("../dist/components/zoom-label/index.js");
  assert.equal((await import("../dist/components/zoom-label/element.js")).ZoomLabelElement, ZoomLabelElement);
  const label = new ZoomLabelElement();
  label.view = new MapView();
  label.view.zoom = 3.25;
  label.precision = 1;
  const seen = [];
  label.addEventListener("demo-zoom-label-copy", (e) => seen.push(e.detail));
  label.copy();
  assert.deepEqual(seen, [{ text: "3.3" }]);
});
