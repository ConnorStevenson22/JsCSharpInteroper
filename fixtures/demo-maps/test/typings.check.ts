// Compiled by `tsc --noEmit` only: checks that the typings resolve the patterns the way callers use them.
import { MapView, type FlyToTarget } from "../MapView.js";
import { Marker } from "../Marker.js";
import { Point, type Shape } from "../geometry.js";
import TileLayer from "../layers/TileLayer.js";
import { perimeter } from "../measure.js";
import type { Style } from "../styles.js";

const view = new MapView({ zoom: 3, basemap: "3d-relief", cursor: "grab" });
view.on("click", (event) => event.mapPoint.x satisfies number);
// @ts-expect-error: not in MapViewEvents
view.on("dblclick", () => {});
view.watch("zoom", (zoom, old) => zoom - old);
view.markers.on("change", ({ added }) => added.map((m) => m.displayLabel));
view.markers.forEach((m, index) => void (m.position.x + index));

const marker = new Marker({ position: [1, 2], labelFormatter: (m) => `${m.position.x}` });
marker.position = [3, 4];
const position: Point = marker.position;
// @ts-expect-error: the getter never returns the setter's tuple form
const pair: [number, number] = marker.position;

const targets: FlyToTarget[] = [position, [0, 0], { minX: 0, minY: 0, maxX: 1, maxY: 1 }, { center: [0, 0], zoom: 2 }];
void Promise.all(targets.map((t) => view.flyTo(t)));

function describe(shape: Shape): string {
  switch (shape.type) {
    case "point": return "point";
    case "line": return "line";
    case "polygon": return "polygon";
  }
}

function width(style: Style): number | undefined {
  return style.type === "stroke" ? style.width : undefined;
}

const tile: TileLayer = new TileLayer({ url: "u" }).clone(); // the redeclared clone returns the subclass
view.layers.add(tile);
perimeter(position, ...targets.filter((t): t is Point => t instanceof Point)) satisfies number;

void pair;
void describe;
void width;
