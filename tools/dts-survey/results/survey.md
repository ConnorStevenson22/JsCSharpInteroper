| Library | Positions | Nullable | null **and** undefined | Literal union (enum) | Primitive union | Discriminated obj union | Untagged obj union | Mixed union | **Needs a real union** | Intersection | Callback params | Promise returns | Asym. get/set | Overloaded methods |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| @arcgis/core | 32585 | 32.8% | 19.8% | 6.2% | 0.5% | 3.1% | 1.2% | 2.8% | **7.6%** | 0.3% | 47 | 883 | 1553 | 17 |
| @arcgis/map-components | 10503 | 29.1% | 2.7% | 4.3% | 0.0% | 0.6% | 0.3% | 1.5% | **2.4%** | 0.7% | 7 | 284 | 17 | 0 |
| @types/google.maps | 4815 | 47.9% | 19.1% | 4.8% | 0.1% | 0.0% | 2.8% | 9.6% | **12.5%** | 0.0% | 260 | 26 | 158 | 114 |
| @types/leaflet | 2369 | 32.7% | 0.3% | 5.4% | 0.7% | 0.1% | 0.4% | 6.8% | **8.0%** | 0.3% | 229 | 0 | 0 | 18 |
| ol (OpenLayers) | 11508 | 25.2% | 0.6% | 1.9% | 0.4% | 0.0% | 1.3% | 4.9% | **6.5%** | 1.2% | 134 | 27 | 0 | 0 |
| maplibre-gl | 8413 | 12.0% | 1.2% | 2.2% | 0.5% | 0.6% | 0.9% | 1.9% | **4.0%** | 1.5% | 64 | 72 | 0 | 5 |
| cesium | 11851 | 33.4% | 0.0% | 0.1% | 0.1% | 0.0% | 1.6% | 3.3% | **5.0%** | 0.0% | 36 | 79 | 0 | 87 |
| chart.js | 4164 | 8.6% | 0.1% | 2.2% | 1.0% | 0.0% | 0.4% | 5.0% | **6.5%** | 1.2% | 15 | 1 | 0 | 2 |
| echarts | 23081 | 39.2% | 2.0% | 4.3% | 3.2% | 0.1% | 0.9% | 7.3% | **11.4%** | 2.1% | 366 | 0 | 0 | 141 |
| @types/three | 20068 | 16.1% | 1.1% | 2.8% | 0.2% | 0.2% | 1.7% | 5.3% | **7.4%** | 13.3% | 164 | 62 | 192 | 48 |
| monaco-editor | 2723 | 35.1% | 3.5% | 3.1% | 0.6% | 0.1% | 1.4% | 3.2% | **5.2%** | 0.2% | 17 | 8 | 0 | 19 |
| lib.dom.d.ts (WebIDL-derived) | 19273 | 21.4% | 0.7% | 3.2% | 0.1% | 0.0% | 0.7% | 9.0% | **9.9%** | 0.0% | 1013 | 239 | 29 | 542 |

| Library | distinct literal unions | distinct primitive unions | distinct discriminated | distinct untagged object | distinct mixed | median / p90 members per union |
|---|---:|---:|---:|---:|---:|---:|
| @arcgis/core | 795 | 6 | 377 | 163 | 281 | 3 / 13 |
| @arcgis/map-components | 192 | 1 | 17 | 24 | 34 | 4 / 2631 |
| @types/google.maps | 69 | 2 | 0 | 23 | 58 | 2 / 4 |
| @types/leaflet | 16 | 6 | 1 | 4 | 38 | 2 / 8 |
| ol (OpenLayers) | 57 | 4 | 2 | 44 | 95 | 2 / 6 |
| maplibre-gl | 74 | 6 | 17 | 30 | 40 | 3 / 10 |
| cesium | 5 | 1 | 0 | 63 | 78 | 2 / 6 |
| chart.js | 38 | 13 | 1 | 10 | 91 | 3 / 7 |
| echarts | 286 | 72 | 15 | 113 | 619 | 3 / 5 |
| @types/three | 137 | 6 | 20 | 93 | 164 | 3 / 9 |
| monaco-editor | 62 | 8 | 2 | 27 | 42 | 3 / 4 |
| lib.dom.d.ts (WebIDL-derived) | 246 | 9 | 1 | 38 | 90 | 2 / 4 |

| Library | Generic decls | Index signatures | Mapped | Conditional | Template literal | keyof | Indexed access | typeof | Utility type refs | any/unknown | Widest union (members) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| @arcgis/core | 270 | 3 | 1 | 3 | 2 | 67 | 434 | 1082 | 1112 | 0.5% | 2631 (`Widget.icon`) |
| @arcgis/map-components | 103 | 0 | 0 | 1 | 0 | 1 | 2575 | 1 | 508 | 0.0% | 2631 (`type IconName`) |
| @types/google.maps | 118 | 5 | 0 | 0 | 74 | 115 | 115 | 286 | 0 | 1.6% | 26 (`MapsNetworkError.code`) |
| @types/leaflet | 33 | 9 | 0 | 0 | 0 | 2 | 6 | 25 | 1 | 10.8% | 50 (`Events.listens(type)`) |
| ol (OpenLayers) | 142 | 301 | 0 | 8 | 0 | 1 | 1 | 11 | 5 | 17.8% | 11 (`type GeolocationObjectEventTypes`) |
| maplibre-gl | 96 | 101 | 32 | 4 | 0 | 66 | 127 | 32 | 170 | 3.3% | 139 (`type PaintPropertyEntry`) |
| cesium | 8 | 16 | 0 | 0 | 0 | 0 | 0 | 0 | 9 | 3.4% | 20 (`type MetadataValue`) |
| chart.js | 110 | 15 | 13 | 8 | 0 | 25 | 37 | 19 | 47 | 18.4% | 32 (`index.easing`) |
| echarts | 926 | 57 | 69 | 111 | 0 | 252 | 846 | 874 | 399 | 5.3% | 60 (`type PathKey`) |
| @types/three | 338 | 96 | 10 | 58 | 6 | 45 | 94 | 1165 | 127 | 0.9% | 60 (`type PixelFormatGPU`) |
| monaco-editor | 25 | 6 | 2 | 3 | 0 | 3 | 17 | 1 | 40 | 0.9% | 177 (`type FindEditorOptionsKeyById`) |
| lib.dom.d.ts (WebIDL-derived) | 558 | 43 | 0 | 0 | 6 | 495 | 495 | 10 | 24 | 0.9% | 867 (`HTMLInputElement.autocomplete`) |
