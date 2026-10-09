import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import * as core from "../core.js";
import * as geometry from "../geometry.js";
import * as demoMap from "../demo-map.js";

const json = (name) => JSON.parse(readFileSync(new URL(`../${name}`, import.meta.url), "utf8"));

test("profile.json names classes that exist", () => {
  const profile = json("profile.json");
  for (const name of profile.valueClasses) assert.equal(typeof geometry[name], "function", name);
  for (const name of profile.collections) assert.equal(typeof core[name], "function", name);
  assert.ok(profile.runtimeClassProperty in new geometry.Point());
});

test("custom-elements.json matches the element module", () => {
  const [module] = json("custom-elements.json").modules;
  const [element] = module.declarations;
  assert.equal(typeof demoMap[element.name], "function");
  assert.deepEqual(element.attributes.map((a) => a.name), demoMap.DemoMapElement.observedAttributes);
});
