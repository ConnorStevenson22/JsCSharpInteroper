import { describe } from "./support/entry.js";

// Outside a browser the element is a plain EventTarget, so its logic still runs under node --test.
const Base = globalThis.HTMLElement ?? EventTarget;

export class Legend extends Base {
  static className = "Legend";
  view = null;
  heading = "Legend";

  get entries() {
    return (this.view?.layers.toArray() ?? []).map((layer) => ({ title: layer.title ?? "", visible: layer.visible }));
  }

  select(index) {
    const entry = this.entries[index];
    this.dispatchEvent(new CustomEvent("demo-legend-select", { detail: { index, title: describe(entry) } }));
  }
}

export function legendFor(view) {
  const legend = new Legend();
  legend.view = view;
  return legend;
}

if (globalThis.customElements && !customElements.get("demo-legend")) customElements.define("demo-legend", Legend);
