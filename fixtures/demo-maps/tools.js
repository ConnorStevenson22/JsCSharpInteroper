// Factories that return plain objects (R15). Each view keeps the tools made for it.

const made = new WeakMap();

function tool(view, name, members = {}) {
  let active = false;
  const t = {
    name,
    get active() { return active; },
    activate() { active = true; },
    deactivate() { active = false; },
  };
  Object.defineProperties(t, Object.getOwnPropertyDescriptors(members)); // keeps the getters
  made.set(view, [...toolsOf(view), t]);
  return t;
}

export function createPanTool(view) {
  return tool(view, "pan");
}

export function createMeasureTool(view) {
  let readings = 0;
  return tool(view, "measure", {
    get readings() { return readings; },
    measure() { readings++; return view.zoom; },
    clear() { readings = 0; },
  });
}

export function toolsOf(view) {
  return made.get(view) ?? [];
}
