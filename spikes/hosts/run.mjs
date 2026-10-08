// Visits each URL in turn in one browser session and prints the page's #results text.
// One session matters for InteractiveAuto: the first visit downloads WebAssembly, later ones can use it.
//   CHROME=/path/to/chromium node run.mjs http://127.0.0.1:5281/wasm [more urls...]
import { chromium } from "playwright-core";

const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ["--no-sandbox"] });
const page = await (await browser.newContext()).newPage();
const errors = [];
page.on("console", (m) => { if (m.type() === "error" && !m.text().includes("404")) errors.push(m.text()); });
page.on("pageerror", (e) => errors.push("pageerror " + e.message));

for (const url of process.argv.slice(2)) {
  console.log(`===== ${url}`);
  await page.goto(url);
  try {
    await page.waitForFunction(() => {
      const t = document.querySelector("#results")?.textContent ?? "";
      return t && t !== "running";
    }, null, { timeout: 120000 });
    console.log(await page.textContent("#results"));
  } catch (e) {
    console.log("TIMEOUT", e.message);
  }
  if (errors.length) console.log("console errors:\n" + errors.splice(0).slice(0, 8).join("\n"));
}
await browser.close();
