// No export of the package reaches this file, so the IR marks its module internal.

export interface LegendEntry {
  title: string;
  visible: boolean;
}

/** The entry's title, marked when it's hidden. */
export function describe(entry: LegendEntry): string;
