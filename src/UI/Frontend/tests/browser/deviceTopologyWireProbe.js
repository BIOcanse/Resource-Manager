// Exercise the production subscription, adapters and rendered facts with typed backend scenarios.
export async function checkDeviceTopologyWireFacts(page, app, language, baseUrl) {
  const topology = page.locator(".device-topology-panel");
  const keys = ["deviceTopologyMachineIdentity", "deviceTopologyGraphicsDock", "deviceTopologyExternalMachine",
    "deviceTopologyNumericUsb", "deviceTopologyWireless", "deviceTopologyTransport"];
  const before = await (await fetch(`${baseUrl}/__test/scenario`)).json();
  const restore = Object.fromEntries(keys.map(key => [key, before[key]]));
  const scope = await topology.locator('.device-scope-tabs [role="radio"][aria-checked="true"]').textContent();
  const result = { attachments: null, usb: [], wireless: [], transport: [] };
  try {
    await publish({ deviceTopologyMachineIdentity: "laptop", deviceTopologyGraphicsDock: true,
      deviceTopologyExternalMachine: true, deviceTopologyNumericUsb: true, deviceTopologyWireless: "standards",
      deviceTopologyTransport: false });
    const figure = topology.locator('.device-machine-figure[data-form-factor="laptop"]');
    await figure.waitFor();
    const formName = topology.locator(".device-machine-form strong");
    await formName.getByText(app.deviceTopology.machine.formFactor.laptop, { exact: true }).waitFor();
    const dock = topology.locator('[data-attachment-kind="graphics_dock"]');
    await dock.getByText("NVIDIA RTX PRO 6000 External Adapter", { exact: true }).waitFor();
    assert(await dock.count() === 1, `${language}: the graphics dock was duplicated`);
    const peer = topology.locator("[data-external-machine]");
    await peer.waitFor();
    assert(await peer.getAttribute("data-external-machine") === String.raw`USB4\FIXTURE_PEER_LINK`,
      `${language}: the external machine lost its local link identity`);
    const soc = peer.locator('[data-component="soc"]');
    await soc.waitFor();
    assert(await soc.count() === 1 && (await soc.textContent()).includes(app.deviceTopology.machine.unknown),
      `${language}: the peer must contain one SoC with an unknown model`);
    assert((await peer.textContent()).includes("windows-peer-properties-unavailable"),
      `${language}: unavailable peer properties lost their backend reason`);
    assert((await formName.textContent()).trim() === app.deviceTopology.machine.formFactor.laptop,
      `${language}: an attached workstation or graphics dock changed the local laptop form`);
    const evidence = await topology.locator(".device-machine-evidence").textContent();
    assert(!evidence.includes("NVIDIA RTX PRO 6000 External Adapter"),
      `${language}: detachable graphics were counted as workstation components`);
    result.attachments = { form: await formName.textContent(), dock: await dock.textContent(), peer: await peer.textContent() };

    await topology.getByRole("radio", { name: app.deviceTopology.externalScope, exact: true }).click();
    const usbCases = [
      ["keyboard", "keyboard", "3/1/1"], ["mouse", "mouse", "3/1/2"],
      ["audio", "audio-device", "1/2/0"], ["hub", "usb-hub", null],
      ["storage-uas", "external-storage", "8/6/98", "UAS"],
      ["storage-bot", "external-storage", "8/6/80", "BOT"],
      ["camera", "camera", "14/2/0"], ["mobile", "mobile-device", "6/1/1"],
      ["vendor", "generic-device", "255/128/254"],
      ["unknown", "generic-device", app.deviceTopology.deviceCategory.unknown]
    ];
    for (const [fixture, kind, code, transport] of usbCases) {
      const node = topology.locator(".device-port-item.device-node").filter({ hasText: `Fixture ${fixture}` }).first();
      await node.click();
      await topology.locator(".device-details-button").click();
      const dialog = page.locator(".user-details-modal:visible");
      const details = dialog.locator(`.device-specialized-details.kind-${kind}`);
      await details.waitFor();
      if (code) await details.getByText(code, { exact: false }).first().waitFor();
      if (fixture === "hub") await dialog.locator(".user-details-row")
        .filter({ has: page.getByText(app.deviceTopology.label.usbClass, { exact: true }) })
        .locator("dd").getByText("9", { exact: true }).waitFor();
      if (transport) await details.getByText(transport, { exact: true }).waitFor();
      const text = await details.textContent();
      assert(!text.includes("Misleading"), `${language}: ${fixture} used a legacy USB summary: ${text}`);
      result.usb.push({ fixture, kind, code, transport });
      await page.keyboard.press("Escape");
      await dialog.waitFor({ state: "hidden" });
    }

    await topology.getByRole("radio", { name: app.deviceTopology.internalScope, exact: true }).click();
    for (const [variant, expected] of [["standards", "802.11ac"], ["unknown", app.deviceTopology.deviceCategory.unknown]]) {
      await publish({ deviceTopologyWireless: variant });
      const node = topology.locator(".device-port-item.device-node").filter({ hasText: "Misleading Wi-Fi 7 Name" }).first();
      await node.click();
      const standard = topology.locator(".device-detail-summary-item")
        .filter({ has: page.getByText(app.deviceAdapters.label.networkStandard, { exact: true }) }).locator("strong");
      await standard.getByText(expected, { exact: true }).waitFor();
      const actual = (await standard.textContent()).trim();
      assert(actual === expected && !actual.includes("Wi-Fi 7"),
        `${language}: ${variant} WLAN facts were replaced by the device name: ${actual}`);
      result.wireless.push({ variant, actual });
    }

    await publish({ deviceTopologyGraphicsDock: false, deviceTopologyExternalMachine: false,
      deviceTopologyNumericUsb: false, deviceTopologyWireless: null, deviceTopologyTransport: true });
    const transportCases = [
      ["Misleading NVMe USB4 transport name", "ACPI"],
      ["Misleading Bluetooth NVMe transport name", `${app.deviceTopology.deviceCategory.unknown} (not-queried)`],
      ["Fixture ordinary PCI transport", "PCI"],
      ["Fixture parent-chain transport", "I2C HID"]
    ];
    for (const [name, expected] of transportCases) {
      const node = topology.locator(".device-port-item.device-node").filter({ hasText: name }).first();
      await node.click();
      const summary = topology.locator(".device-detail-summary-item")
        .filter({ has: page.getByText(app.deviceAdapters.label.internalTransport, { exact: true }) }).locator("strong");
      await summary.getByText(expected, { exact: true }).waitFor();
      const actual = (await summary.textContent()).trim();
      assert(actual === expected, `${language}: ${name} replaced its transport fact: ${actual}`);
      await topology.locator(".device-details-button").click();
      const dialog = page.locator(".user-details-modal:visible");
      const transport = dialog.locator(".user-details-row")
        .filter({ has: page.getByText(app.deviceAdapters.label.internalTransport, { exact: true }) }).locator("dd");
      await transport.getByText(expected, { exact: true }).waitFor();
      assert((await transport.textContent()).trim() === expected,
        `${language}: ${name} lost authoritative transport in the details dialog`);
      assert(!/PCI Express|PCIe/i.test(await dialog.textContent()),
        `${language}: ${name} invented PCI Express from ordinary PCI or a device ID`);
      result.transport.push({ name, actual, dialog: (await transport.textContent()).trim() });
      await page.keyboard.press("Escape");
      await dialog.waitFor({ state: "hidden" });
    }
    assert(!/PCI Express|PCIe/i.test(await topology.locator(".device-port-list").textContent()),
      `${language}: the transport tree invented a PCI Express interface`);
    return result;
  } finally {
    const dialog = page.locator(".user-details-modal:visible");
    if (await dialog.count()) { await page.keyboard.press("Escape"); await dialog.waitFor({ state: "hidden" }); }
    await publish(restore);
    await topology.getByRole("radio", { name: scope?.trim() || app.deviceTopology.externalScope, exact: true }).click();
  }

  async function publish(patch) {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST",
      headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
    assert(response.ok, `${language}: topology wire scenario rejected: ${response.status}`);
  }
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
