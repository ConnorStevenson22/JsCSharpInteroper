import { MapView } from "./MapView.js";

// Outside a browser the element is a plain EventTarget, so its logic still runs under node --test.
const Base = globalThis.HTMLElement ?? EventTarget;

export class DemoMapElement extends Base {
  static observedAttributes = ["basemap", "zoom"];
  #view = new MapView();

  constructor() {
    super();
    this.#view.watch("zoom", (zoom) => this.#fire("demo-map-zoom-change", { zoom }));
    this.#view.on("click", ({ x, y, mapPoint }) => this.#fire("demo-map-click", { x, y, mapX: mapPoint.x, mapY: mapPoint.y }));
  }

  get view() { return this.#view; }
  get basemap() { return this.#view.basemap; }
  set basemap(value) { this.#view.basemap = value; }
  get zoom() { return this.#view.zoom; }
  set zoom(value) { this.#view.zoom = value; }
  get center() { return this.#view.center; }
  set center(value) { this.#view.center = value; }

  attributeChangedCallback(name, _old, value) {
    if (name === "zoom") this.zoom = Number(value);
    else if (name === "basemap") this.basemap = value;
  }

  connectedCallback() {
    if (!this.shadowRoot) {
      this.attachShadow({ mode: "open" }).innerHTML =
        `<slot name="toolbar"></slot><div part="surface" style="min-height:100px"></div><slot></slot>`;
      this.shadowRoot.querySelector("[part=surface]").addEventListener("click", (e) => {
        const rect = e.currentTarget.getBoundingClientRect();
        this.clickAt(e.clientX - rect.left - rect.width / 2, e.clientY - rect.top - rect.height / 2, e.button);
      });
    }
    this.#fire("demo-map-ready", null);
  }

  // Emits the view's click event as if the map were clicked at this offset from its center.
  clickAt(x, y, button = 0) {
    this.#view.emit("click", { type: "click", x, y, button, mapPoint: this.#view.toMap(x, y) });
  }

  #fire(type, detail) {
    this.dispatchEvent(new CustomEvent(type, { detail, bubbles: true, composed: true }));
  }
}

if (globalThis.customElements && !customElements.get("demo-map")) customElements.define("demo-map", DemoMapElement);
