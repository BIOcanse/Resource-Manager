export default async function resourceBarProportionJourney(page, baseUrl) {
  const results = [];
  for (const width of [390, 1100]) {
    await page.setViewportSize({ width, height: 844 });
    for (const value of [1, 25, 95, 99, 99.9, 100]) {
      await page.request.post(`${baseUrl}/__test/scenario`, {
        data: { resourceCpuValue: value, resourceSegmentCount: 1, settingsTheme: "dark" }
      });
      await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
      const canvas = page.locator('.resource-bar-track[data-metric-id="cpu.usage"] canvas');
      await canvas.waitFor({ state: "visible" });
      await page.waitForTimeout(600);
      const result = await canvas.evaluate((canvas, value) => {
        const ctx = canvas.getContext("2d");
        const y = Math.floor(canvas.height / 4);
        const pixels = ctx.getImageData(0, y, canvas.width, 1).data;
        const reference = [...pixels.slice(0, 4)].join(",");
        let end = 0;
        while (end < canvas.width && [...pixels.slice(end * 4, end * 4 + 4)].join(",") === reference) end++;
        const expected = Math.floor(canvas.width * value / 100);
        if (Math.abs(end - expected) > 1) throw new Error(`Bar ${value}%: ${end}/${canvas.width}, expected ${expected}`);
        if (value < 100 && end === canvas.width) throw new Error("Non-full usage painted full");
        return { value, width: canvas.width, end, expected };
      }, value);
      results.push(result);
    }
  }
  return results;
}
