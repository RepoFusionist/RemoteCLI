namespace RemoteCli.Vps;

internal static class AdminPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Remote CLI 服务端配置</title>
<style>
:root{color-scheme:dark;font:15px system-ui,-apple-system,"Segoe UI",sans-serif;background:#141615;color:#e9eee9}body{max-width:820px;margin:0 auto;padding:32px 20px}h1{font-size:25px;margin:0 0 6px}h2{font-size:18px;margin:0 0 12px}.muted{color:#a7b1a8;font-size:13px}.card{background:#202521;border:1px solid #364038;border-radius:12px;padding:20px;margin:18px 0;box-shadow:0 8px 28px #0002}.row{display:flex;gap:14px;align-items:center;justify-content:space-between;padding:11px 0;border-top:1px solid #303930}.row:first-of-type{border-top:0}.row span{max-width:62%}label{display:block;margin:10px 0 6px;color:#b9c5ba}input[type=text],input[type=password],input[type=number]{width:100%;box-sizing:border-box;background:#151915;color:#eef4ee;border:1px solid #526054;border-radius:7px;padding:10px}input[type=checkbox]{width:18px;height:18px}button{border:0;border-radius:7px;padding:10px 16px;background:#9ee6b0;color:#102116;font-weight:650;cursor:pointer}button.secondary{background:#3a473d;color:#e9eee9}button:disabled{opacity:.5;cursor:not-allowed}.actions{display:flex;gap:10px;align-items:center;margin-top:16px}.error{color:#ff9c9c;min-height:20px}.ok{color:#9ee6b0;min-height:20px}.fingerprint{font:12px ui-monospace,Consolas,monospace;word-break:break-all;color:#bde7c5;background:#151915;padding:10px;border-radius:6px}.hidden{display:none!important}</style>
</head>
<body>
<h1>Remote CLI 服务端配置</h1>
<p class="muted">首次进入需要使用服务器生成的初始化密钥创建 root 用户。初始化密钥只保存在服务端数据目录的 bootstrap.key 中，不会通过网页回显。</p>
<p id="message" class="error"></p>
<section id="setup" class="card hidden">
  <h2>首次初始化</h2>
  <label>服务器生成的初始化密钥</label><input id="bootstrapKey" type="password" autocomplete="off">
  <label>Root 用户名</label><input id="rootUser" type="text" value="root" autocomplete="username">
  <label>Root 密码（至少 12 位）</label><input id="setupPassword" type="password" autocomplete="new-password">
  <div class="actions"><button id="setupButton">初始化服务端</button></div>
</section>
<section id="login" class="card hidden">
  <h2>管理员登录</h2>
  <label>Root 用户名</label><input id="loginUser" type="text" value="root" autocomplete="username">
  <label>密码</label><input id="loginPassword" type="password" autocomplete="current-password">
  <div class="actions"><button id="loginButton">登录</button></div>
</section>
<section id="config" class="card hidden">
  <h2>Relay 常用配置</h2>
  <p class="muted">变更会立即影响新连接；已建立的端到端会话仍由当前权限和会话过期时间控制。</p>
  <div class="row"><span>允许非官方客户端<small class="muted"><br>关闭后只接受 Remote CLI 官方客户端请求。</small></span><input id="allowUnofficial" type="checkbox"></div>
  <div class="row"><span>允许固定密码<small class="muted"><br>关闭后只能使用随机密码连接授权。</small></span><input id="allowFixed" type="checkbox"></div>
  <div class="row"><span>允许随机密码</span><input id="allowRandom" type="checkbox"></div>
  <div class="row"><span>授权有效期上限（分钟）</span><input id="maxMinutes" type="number" min="1" max="10"></div>
  <div class="row"><span>每 IP 每分钟请求上限</span><input id="rateLimit" type="number" min="1" max="600"></div>
  <label>服务端 SHA-256 指纹（客户端连接前会校验）</label><div id="fingerprint" class="fingerprint">—</div>
  <p id="saveMessage" class="ok"></p><div class="actions"><button id="saveButton">保存配置</button><button id="logoutButton" class="secondary">退出登录</button></div>
</section>
<section id="password" class="card hidden">
  <h2>修改 Root 密码</h2>
  <label>当前密码</label><input id="currentPassword" type="password" autocomplete="current-password">
  <label>新密码（至少 12 位）</label><input id="newPassword" type="password" autocomplete="new-password">
  <div class="actions"><button id="passwordButton">修改密码</button></div>
</section>
<script>
const $=id=>document.getElementById(id), message=(text,error=true)=>{ $("message").textContent=text||""; $("message").className=error?"error":"ok"; }, adminBase=(window.location.pathname.endsWith("/")?window.location.pathname:window.location.pathname+"/"), api=path=>adminBase+path;
async function json(url,options){ const response=await fetch(url,Object.assign({headers:{"Content-Type":"application/json"}},options||{})); let body={}; try{body=await response.json()}catch(e){} if(!response.ok) throw new Error(body.error||("HTTP "+response.status)); return body; }
function show(id,on){$(id).classList.toggle("hidden",!on)}
async function load(){ try{const status=await json(api("api/setup/status"),{headers:{}}); show("setup",!status.initialized); show("login",status.initialized); if(!status.initialized)return; try{await loadConfig()}catch(e){message("")} }catch(e){message("无法读取服务端状态："+e.message)} }
async function loadConfig(){ const config=await json(api("api/config"),{headers:{}}); show("login",false); show("config",true); show("password",true); $("allowUnofficial").checked=config.allowUnofficialClients; $("allowFixed").checked=config.allowFixedPasswords; $("allowRandom").checked=config.allowRandomPasswords; $("maxMinutes").value=config.maxAuthorizationMinutes; $("rateLimit").value=config.rateLimitPerMinute; $("fingerprint").textContent=config.serverFingerprintSha256; }
$("setupButton").onclick=async()=>{try{await json(api("api/setup"),{method:"POST",body:JSON.stringify({bootstrapKey:$('bootstrapKey').value,rootUser:$('rootUser').value,password:$('setupPassword').value})});message("初始化完成，请登录。",false);show("setup",false);show("login",true)}catch(e){message("初始化失败："+e.message)}};
$("loginButton").onclick=async()=>{try{await json(api("api/login"),{method:"POST",body:JSON.stringify({rootUser:$('loginUser').value,password:$('loginPassword').value})});message("登录成功。",false);await loadConfig()}catch(e){message("登录失败："+e.message)}};
$("saveButton").onclick=async()=>{try{await json(api("api/config"),{method:"PUT",body:JSON.stringify({allowUnofficialClients:$('allowUnofficial').checked,allowFixedPasswords:$('allowFixed').checked,allowRandomPasswords:$('allowRandom').checked,maxAuthorizationMinutes:Number($('maxMinutes').value),rateLimitPerMinute:Number($('rateLimit').value)})});$("saveMessage").textContent="配置已保存。";setTimeout(()=>$("saveMessage").textContent="",2500)}catch(e){$("saveMessage").textContent="保存失败："+e.message}};
$("logoutButton").onclick=async()=>{await fetch(api("api/logout"),{method:"POST"});location.reload()};
$("passwordButton").onclick=async()=>{try{await json(api("api/password"),{method:"POST",body:JSON.stringify({currentPassword:$('currentPassword').value,newPassword:$('newPassword').value})});message("密码已修改，请重新登录。",false);setTimeout(()=>location.reload(),500)}catch(e){message("修改密码失败："+e.message)}};
load();
</script>
</body></html>
""";
}
