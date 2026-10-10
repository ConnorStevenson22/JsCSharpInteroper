// A metadata table with a custom element (its `tag`) and the components' custom events (R28), for elements.test.mjs.
const at = (file) => new URL(`../../fixtures/demo-maps/${file}`, import.meta.url).href;

export const ids = { Point: 2, MapView: 5, DemoMapElement: 10 };

export default {
  classNameProperty: "className",
  classes: {
    [ids.Point]: { module: at("geometry.js"), export: "Point", name: "geometry.Point", value: true, members: ["type", "x", "y"] },
    [ids.MapView]: { module: at("MapView.js"), export: "MapView", name: "MapView", members: [] },
    [ids.DemoMapElement]: { module: at("demo-map.js"), export: "DemoMapElement", name: "demo-map.DemoMapElement", tag: "demo-map", members: [] },
  },
  customEvents: [{ name: "demo-map-click", fields: ["x", "mapPoint"] }, { name: "demo-map-ready" }, { name: "zoomChange" }],
};
