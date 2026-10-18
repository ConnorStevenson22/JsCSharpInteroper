// The golden slice's metadata table (tests/GoldenSlice/wwwroot/demo-maps.meta.js) for Node: file URLs instead of bare
// specifiers, and the members these tests use beyond what the golden slice binds, with ids of their own.
const at = (file) => new URL(`../../fixtures/demo-maps/${file}`, import.meta.url).href;

export const ids = { Shape: 1, Point: 2, Line: 3, Polygon: 4, MapView: 5, Marker: 6, Collection: 7, Pin: 8, Plain: 9 };
export const modules = { measure: at("measure.js"), styles: at("styles.js") };

export default {
  classNameProperty: "className",
  classes: {
    [ids.Shape]: { module: at("geometry.js"), export: "Shape", name: "geometry.Shape", value: true, members: ["type"] },
    [ids.Point]: { module: at("geometry.js"), export: "Point", name: "geometry.Point", value: true, members: ["type", "x", "y"] },
    [ids.Line]: { module: at("geometry.js"), export: "Line", name: "geometry.Line", value: true, members: ["type", "path"] },
    [ids.Polygon]: { module: at("geometry.js"), export: "Polygon", name: "geometry.Polygon", value: true, members: ["type", "vertices"] },
    [ids.MapView]: {
      module: at("MapView.js"), export: "MapView", name: "MapView",
      members: ["center", "zoom", "rotation", "basemap", "cursor", "title", "selected", "markers"],
    },
    [ids.Marker]: { module: at("Marker.js"), export: "Marker", name: "Marker", members: ["position", "label", "style"] },
    [ids.Collection]: { module: at("core.js"), export: "Collection", name: "core.Collection", members: ["length"] },
    [ids.Pin]: { module: at("pins.js"), export: "Pin", name: "pins.Pin", members: ["position", "label", "style"] },
    [ids.Plain]: { module: new URL("./faulty.mjs", import.meta.url).href, export: "Plain", name: "Plain", members: [] },
  },
};
