"use strict";
(() => {
  const el = id => document.getElementById(id);
  const match = /^#([0-9a-f]{64})\.([0-9a-f]{64})$/.exec(location.hash);
  history.replaceState(null, "", location.pathname);
  const status = message => { el("status").textContent = message; };
  const bytes = hex => Uint8Array.from(hex.match(/../g), x => parseInt(x, 16));
  const hex = data => Array.from(new Uint8Array(data), x => x.toString(16).padStart(2, "0")).join("");
  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  let pc, channel, stopped = false, frame, pendingImage = null, decoding = false;
  const screen = el("screen");
  function send(data) {
    if (channel?.readyState === "open" && channel.bufferedAmount < 65536) channel.send(JSON.stringify(data));
  }
  function end(message) {
    if (stopped) return;
    send({type:"release"}); stopped = true; pc?.close(); frame = null; pendingImage = null;
    screen.width = 1; el("session").hidden = true; el("lobby").hidden = false;
    el("code").textContent = ""; status(message);
  }
  async function receive(event) {
    if (stopped) return;
    if (typeof event.data === "string") {
      const head = JSON.parse(event.data);
      if (!Number.isInteger(head.size) || head.size < 1 || head.size > 524288) throw Error("画面数据无效。");
      frame = {size:head.size, received:0, parts:[]}; return;
    }
    if (!frame || event.data.byteLength > 16384) throw Error("画面数据无效。");
    frame.received += event.data.byteLength;
    if (frame.received > frame.size) throw Error("画面数据无效。");
    frame.parts.push(event.data);
    if (frame.received !== frame.size) return;
    pendingImage = new Blob(frame.parts, {type:"image/jpeg"}); frame = null;
    if (decoding) return;
    decoding = true;
    try {
      while (!stopped && pendingImage) {
        const next = pendingImage;
        pendingImage = null;
        const image = await createImageBitmap(next);
        try {
          if (stopped) return;
          if (screen.width !== image.width || screen.height !== image.height) { screen.width=image.width; screen.height=image.height; }
          screen.getContext("2d").drawImage(image, 0, 0);
          if (el("session").hidden) { el("lobby").hidden=true; el("session").hidden=false; screen.focus(); }
        } finally { image.close(); }
      }
    } finally { decoding = false; }
  }
  async function connect() {
    el("connect").disabled = true;
    const [_, room, secret] = match, id = hex(crypto.getRandomValues(new Uint8Array(32)));
    const key = await crypto.subtle.importKey("raw", bytes(secret), {name:"HMAC",hash:"SHA-256"}, false, ["sign","verify"]);
    const payload = (role,sdp) => new TextEncoder().encode("piulink-web-1\n"+role+"\n"+id+"\n"+sdp);
    async function signal(body) {
      const res=await fetch("./web/v1/connect",{method:"POST",cache:"no-store",redirect:"error",referrerPolicy:"no-referrer",
        headers:{"Authorization":"Bearer "+room,"Content-Type":"application/json"},body:JSON.stringify(body),signal:AbortSignal.timeout(8000)});
      if (!res.ok) throw Error(res.status===404?"邀请已关闭或过期，请向 Host 索取新链接。":res.status===409?"该邀请已有请求，请让 Host 重新开启网页访问。":"打洞服务暂不可用。");
      return res.json();
    }
    const stunHost = location.origin === "https://atipiu.org:8443" ? "172.245.182.117" : location.hostname;
    pc = new RTCPeerConnection({iceServers:[{urls:"stun:"+stunHost+":3478"}]});
    channel=pc.createDataChannel("desktop",{ordered:true}); channel.binaryType="arraybuffer";
    channel.onmessage=e=>receive(e).catch(()=>end("接收到无效画面，请重新连接。"));
    channel.onclose=()=>end("网页访问已结束，请向 Host 索取新链接。");
    pc.onconnectionstatechange=()=>{if (pc.connectionState==="failed") end("没有建立可用的直连路径。");};
    status("正在收集直连地址…"); await pc.setLocalDescription(await pc.createOffer());
    const gather=Date.now()+5500;
    while (!stopped && pc.iceGatheringState!=="complete" && Date.now()<gather) await pause(100);
    if (stopped) return;
    const sdp=pc.localDescription.sdp, mac=hex(await crypto.subtle.sign("HMAC",key,payload("offer",sdp)));
    let response=await signal({action:"offer",id,description:{sdp,mac}});
    el("code").textContent=id.slice(0,6).toUpperCase();status("等待 Host 确认此码并允许键鼠控制…");
    const deadline=Date.now()+65000;
    while (!stopped && response.action==="waiting" && Date.now()<deadline) { await pause(900); response=await signal({action:"poll",id}); }
    if (stopped) return;
    if (response.action==="reject") throw Error("Host 已拒绝或取消本次请求。");
    if (response.id!==id || response.action!=="answer") throw Error("等待确认超时，请让 Host 重新生成邀请。");
    const answer=response.description;
    if (!/^[a-f0-9]{64}$/.test(answer.mac) || !await crypto.subtle.verify("HMAC",key,bytes(answer.mac),payload("answer",answer.sdp)))
      throw Error("Host 身份校验失败。");
    await pc.setRemoteDescription({type:"answer",sdp:answer.sdp});status("已获允许，正在连接桌面…");
    setTimeout(()=>{if(!stopped && el("session").hidden)end("未收到桌面画面，请检查两端网络并重新生成邀请。");},22000);
  }
  function pointer(e,type) {
    if (!e.isPrimary) return;
    const r=screen.getBoundingClientRect(), clamp=n=>Math.max(0,Math.min(1,n));
    send({type,x:clamp((e.clientX-r.left)/r.width),y:clamp((e.clientY-r.top)/r.height),button:e.button});
  }
  screen.onpointerdown=e=>{e.preventDefault();screen.focus();screen.setPointerCapture(e.pointerId);pointer(e,"down");};
  screen.onpointermove=e=>pointer(e,"move");screen.onpointerup=e=>pointer(e,"up");
  screen.onpointercancel=()=>send({type:"release"});screen.oncontextmenu=e=>e.preventDefault();
  screen.addEventListener("wheel",e=>{e.preventDefault();send({type:"wheel",delta:Math.sign(-e.deltaY)});},{passive:false});
  for(const type of ["keydown","keyup"]) screen.addEventListener(type,e=>{
    if(e.isComposing || e.key==="Process")return; e.preventDefault();send({type,code:e.code});
  });
  screen.onblur=()=>send({type:"release"});window.addEventListener("blur",()=>send({type:"release"}));
  el("textForm").onsubmit=e=>{e.preventDefault();const input=el("text");if(input.value)send({type:"text",text:input.value});input.value="";screen.focus();};
  el("disconnect").onclick=()=>end("你已断开连接。");window.addEventListener("pagehide",()=>end("已断开。"));
  if (!match || !window.isSecureContext || !window.RTCPeerConnection || !window.crypto?.subtle) {
    el("connect").disabled=true;status(match?"请使用支持 WebRTC 的现代 HTTPS 浏览器。":"请打开 Host 生成的完整网页访问链接。");
  } else el("connect").onclick=()=>connect().catch(e=>end(e.message));
})();
