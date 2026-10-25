import assert from "node:assert/strict";
import { fanModeFacts } from "./fanModeFacts.js";

export default async function fanManagementModesJourney(page, baseUrl) {
  let objects = [{ objectId: "fan:mixed", settings: [
    { capabilityId: "fan.curve", curveExecution: "firmware", curve: [{temperatureCelsius:40,percent:30},{temperatureCelsius:90,percent:100}] },
    { capabilityId: "fan.duty", number: 42, unit: "%" }
  ] }];
  const writes = [];
  const state = () => ({ desired: { objects }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } });
  const capabilities = [
    { id: "fan.rpm", valueKind: "number", supported: false, unavailableKind: "platform", unavailableReason: {code:"fan-rpm-read-only"}, requiredAccessLevel:"normal" },
    { id: "fan.curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal", curvePointLimit: 6 },
    { id: "fan.duty", valueKind: "number", supported: true, requiredAccessLevel: "normal", range:{minimum:0,maximum:100,step:1,unit:"%",defaultValue:50} },
    { id: "fan.lock-maximum", valueKind: "toggle", supported: true, requiredAccessLevel: "normal" }
  ];
  await page.route("**/api/control/objects", route => route.fulfill({json:{readAt:new Date().toISOString(),objects:[
    {id:"fan:all",kind:"fan",reportedName:"Mode Fan",detail:"uniwill-wmi-ec",terms:["cpu"],platform:{operatingSystem:"windows",vendor:"oem"},isControllable:true,capabilities,
      ...fanModeFacts(["fan.curve","fan.duty","fan.lock-maximum"],{pointKind:"table",allFans:true})},
    {id:"fan:limited",kind:"fan",reportedName:"Limited Fan",platform:{operatingSystem:"windows",vendor:"oem"},isControllable:true,capabilities,
      ...fanModeFacts(["fan.curve","fan.duty","fan.lock-maximum"],{firmware:false})},
    {id:"fan:mixed",kind:"fan",reportedName:"Mixed Fan",platform:{operatingSystem:"windows",vendor:"oem"},isControllable:true,capabilities,
      ...fanModeFacts(["fan.curve","fan.duty","fan.lock-maximum"])}
  ]}}));
  await page.route("**/api/control/state**", route => {
    if(route.request().method()==="PUT") {
      const id=decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop());
      const settings=route.request().postDataJSON();writes.push({id,settings});
      objects=[...objects.filter(object=>object.objectId!==id),...(settings.length?[{objectId:id,settings}]:[])];
    }
    return route.fulfill({json:state()});
  });
  await page.route("**/api/control/objects/*/curve",route=>route.fulfill({json:[{temperatureCelsius:40,percent:30},{temperatureCelsius:90,percent:100}]}));
  const patched=await fetch(`${baseUrl}/__test/scenario`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({controlActualValues:[{objectId:"fan:all",capabilityId:"fan.rpm",number:2500,unit:"RPM"}]})});
  assert.ok(patched.ok);
  await page.goto(baseUrl,{waitUntil:"domcontentloaded"});
  await page.getByRole("navigation",{name:"页面切换"}).getByRole("button",{name:"控制面",exact:true}).click();
  const card=page.locator(".control-object").filter({hasText:"Mode Fan"});
  const limited=page.locator(".control-object").filter({hasText:"Limited Fan"});
  const mixed=page.locator(".control-object").filter({hasText:"Mixed Fan"});
  await card.locator(".control-readings dd").filter({hasText:"2500"}).waitFor();
  assert.equal(await card.locator('.control-ownership-choice [role="radio"]').count(),3);
  await card.getByRole("note").filter({hasText:"所有风扇"}).waitFor();
  assert.doesNotMatch(await card.locator("header").innerText(),/uniwill-wmi-ec|takeover-all-fans|curve-firmware/);
  assert.equal(await limited.getByRole("radio",{name:"固件写入",exact:true}).isDisabled(),true);
  await limited.getByText("固件写入：此通道不支持写入固件曲线。",{exact:true}).waitFor();
  assert.equal(await card.getByRole("note").count(),1);
  await card.locator(".control-fan-mode-hint").filter({hasText:"本程序只读取转速"}).waitFor();
  await mixed.getByRole("alert").filter({hasText:"重新选择"}).waitFor();
  assert.equal(await mixed.getByRole("button",{name:"应用",exact:true}).isDisabled(),true);
  assert.equal(await mixed.locator('.control-ownership-choice [aria-checked="true"]').count(),0);
  assert.deepEqual(writes,[]);
  for(const mode of ["固件写入","软件管理","固件管理"]) {
    await card.getByRole("radio",{name:mode,exact:true}).click();
    await card.locator(".control-readings dd").filter({hasText:"2500"}).waitFor();
    assert.equal(await card.locator(".control-capabilities").getByText("转速",{exact:true}).count(),0);
    assert.equal(await card.locator(".control-capability").count(),mode==="固件写入"?1:mode==="软件管理"?3:0);
    await card.locator(".control-fan-mode-hint").filter({hasText:mode==="固件写入"?"直到改回固件管理":mode==="软件管理"?"程序不在时风扇交还固件":"本程序只读取转速"}).waitFor();
    if(mode!=="固件管理") {
      await card.getByRole("checkbox",{name:"转速曲线",exact:true}).check();
      await card.locator(".fan-curve-chart").waitFor();
      if(mode==="固件写入") {
        assert.equal(await card.locator(".fan-curve-chart").getByRole("slider").count(),6);
        await card.getByRole("note").filter({hasText:"温度断点"}).waitFor();
      } else assert.equal(await card.locator(".fan-curve-chart").getByRole("slider").count(),15);
      assert.equal(await card.locator(".control-curve-execution").count(),0);
      await Promise.all([page.waitForResponse(response=>response.url().endsWith('/api/control/state/fan%3Aall') && response.request().method()==='PUT'), card.getByRole("button",{name:"应用",exact:true}).click()]);
      assert.equal(writes.at(-1).settings[0].curveExecution,mode==="固件写入"?"firmware":"software");
    }
  }
  await mixed.getByRole("radio",{name:"软件管理",exact:true}).click();
  assert.equal(await mixed.getByRole("alert").count(),0);
  assert.equal(await mixed.getByRole("checkbox",{name:"转速曲线",exact:true}).isChecked(),false);
  return {threeModes:true,disabledReason:true,mixedStateRequiresSelection:true,rpmVisibleInEveryMode:true,modeSetsExecution:true};
}
