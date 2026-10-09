import { test } from "node:test";
import assert from "node:assert/strict";
import { DemoMapElement } from "../demo-map.js";

test("attributes and properties drive the view", () => {
  const element = new DemoMapElement();
  element.attributeChangedCallback("zoom", null, "6");
  element.attributeChangedCallback("basemap", null, "night");
  assert.deepEqual([element.view.zoom, element.view.basemap], [6, "night"]);
  element.zoom = 8;
  assert.equal(element.view.zoom, 8);
});

test("view changes surface as DOM events", () => {
  const element = new DemoMapElement();
  const seen = [];
  element.addEventListener("demo-map-zoom-change", (e) => seen.push(e.detail));
  element.addEventListener("demo-map-click", (e) => seen.push(e.detail));
  element.zoom = 2;
  element.clickAt(4, 0);
  assert.deepEqual(seen, [{ zoom: 2 }, { x: 4, y: 0, mapX: 1, mapY: 0 }]);
});

test("registers <demo-map> when customElements exists", async () => {
  const defined = new Map();
  globalThis.customElements = { get: (name) => defined.get(name), define: (name, cls) => defined.set(name, cls) };
  try {
    await import("../demo-map.js?with-registry");
    assert.equal(defined.get("demo-map")?.name, "DemoMapElement");
  } finally {
    delete globalThis.customElements;
  }
});
