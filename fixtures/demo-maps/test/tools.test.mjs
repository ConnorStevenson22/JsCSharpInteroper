import { test } from "node:test";
import assert from "node:assert/strict";
import { MapView } from "../MapView.js";
import { createPanTool, createMeasureTool, toolsOf } from "../tools.js";

test("factories return plain objects with methods, kept per view", () => {
  const view = new MapView({ zoom: 4 });
  const pan = createPanTool(view);
  const measure = createMeasureTool(view);
  assert.equal(Object.getPrototypeOf(pan), Object.prototype);
  assert.equal(pan.active, false);
  pan.activate();
  assert.equal(pan.active, true);
  assert.equal(measure.measure(), 4);
  assert.equal(measure.readings, 1);
  measure.clear();
  assert.equal(measure.readings, 0);
  assert.deepEqual(toolsOf(view), [pan, measure]);
  assert.deepEqual(toolsOf(new MapView()), []);
});
