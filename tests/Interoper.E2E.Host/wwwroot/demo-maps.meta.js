// A hand-written stand-in for the generated metadata table (design §2), with the class ids of
// tests/interoper-js/demo-maps.meta.mjs. Bare specifiers resolve through the host's import map.
const at = (file) => `@acme/demo-maps/${file}`;

export default {
  classNameProperty: "className",
  classes: {
    1: { module: at("geometry.js"), export: "Shape", name: "geometry.Shape", value: true, members: ["type"] },
    2: { module: at("geometry.js"), export: "Point", name: "geometry.Point", value: true, members: ["type", "x", "y"] },
    3: { module: at("geometry.js"), export: "Line", name: "geometry.Line", value: true, members: ["type", "path"] },
    4: { module: at("geometry.js"), export: "Polygon", name: "geometry.Polygon", value: true, members: ["type", "vertices"] },
    5: {
      module: at("MapView.js"), export: "MapView", name: "MapView",
      members: ["center", "zoom", "rotation", "basemap", "cursor", "title", "selected", "markers"],
    },
    6: { module: at("Marker.js"), export: "Marker", name: "Marker", members: ["position", "label", "style"] },
    7: { module: at("core.js"), export: "Collection", name: "core.Collection", members: ["length"] },
  },
};
