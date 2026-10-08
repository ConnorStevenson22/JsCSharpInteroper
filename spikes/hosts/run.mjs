import { chromium } from "playwright-core";
const url = process.argv[2];
const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ["--no-sandbox"] });
const page = await browser.newPage();
const logs = [];
page.on("console", m => { if (m.type() === "error" && !m.text().includes("404")) logs.push(m.text()); });
page.on("pageerror", e => logs.push("pageerror " + e.message));
await page.goto(url);
try {
  await page.waitForFunction(() => { const t = document.querySelector("#results")?.textContent ?? ""; return t && t !== "running"; }, null, { timeout: 120000 });
  console.log(await page.textContent("#results"));
} catch (e) { console.log("TIMEOUT", e.message); }
if (logs.length) console.log("console errors:\n" + logs.slice(0, 8).join("\n"));
await browser.close();
