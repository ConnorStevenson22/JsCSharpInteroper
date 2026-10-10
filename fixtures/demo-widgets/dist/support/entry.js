// No export of the package reaches this file; the legend uses it.

export function describe(entry) {
  return entry.visible ? entry.title : `${entry.title} (hidden)`;
}
