namespace PhotoClippy.Plugins.Reporting;

/// <summary>Inline CSS and JavaScript for the HTML report. No external resources are ever referenced.</summary>
internal static class ReportAssets
{
    public const string FontStack = "\"Inter\",\"SF Pro Text\",-apple-system,\"Segoe UI\",Roboto,\"Helvetica Neue\",Arial,sans-serif";

    private const string LightVars =
        "--bg:#F6F4F0;--surface:#FBFAF7;--ink:#1E2226;--muted:#5B6168;--hair:#E4E0D8;--axis:#BDB6AA;" +
        "--fjord:#4F6D8A;--moss:#7C8E6B;--clay:#C27C5A;--dusk:#8A7F9E;--sand:#CDBF9F;--accent:#A64B58;" +
        "--band:.32;--hl:rgba(79,109,138,.09);color-scheme:light;";

    private const string DarkVars =
        "--bg:#15181B;--surface:#1C2024;--ink:#E9E6E0;--muted:#9EA5AC;--hair:#2C3238;--axis:#48515A;" +
        "--fjord:#7D9CBB;--moss:#9DB08A;--clay:#D99878;--dusk:#AFA3C6;--sand:#D9CCAF;--accent:#D9707F;" +
        "--band:.13;--hl:rgba(125,156,187,.13);color-scheme:dark;";

    public static string Css(string fontStack) =>
        ":root{" + LightVars + "--font:" + fontStack + ";--mono:ui-monospace,\"SF Mono\",Menlo,Consolas,monospace}" +
        ":root[data-theme=dark]{" + DarkVars + "}" +
        "@media (prefers-color-scheme:dark){:root[data-theme=auto]{" + DarkVars + "}}" +
        Layout + ChartCss +
        "@media print{:root,:root[data-theme]{" + LightVars + "--bg:#FFFFFF;--surface:#FFFFFF}" + Print + "}";

    private const string Layout = """
*{box-sizing:border-box}
html{background:var(--bg);-webkit-text-size-adjust:100%}
body{margin:0;background:var(--bg);color:var(--ink);font-family:var(--font);font-size:15px;line-height:1.55;font-variant-numeric:tabular-nums;font-feature-settings:"tnum" 1,"cv11" 1;-webkit-font-smoothing:antialiased;text-rendering:optimizeLegibility}
.wrap{max-width:1100px;margin:0 auto;padding:0 40px}
a{color:inherit;text-decoration-color:var(--axis);text-underline-offset:3px}
a:hover{text-decoration-color:currentColor}
:focus-visible{outline:2px solid var(--fjord);outline-offset:2px}
p{margin:0}
.top{padding-top:76px;padding-bottom:44px}
.eyebrow,.lbl{margin:0 0 14px;font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:var(--muted)}
h1{margin:0;font-size:54px;line-height:1.06;font-weight:300;letter-spacing:-.025em}
.meta{display:flex;flex-wrap:wrap;gap:6px 32px;margin-top:20px;color:var(--muted);font-size:14px}
nav.toc{position:sticky;top:0;z-index:5;background:var(--bg);border-top:1px solid var(--hair);border-bottom:1px solid var(--hair)}
nav.toc .wrap{display:flex;align-items:center;gap:20px}
nav.toc ol{display:flex;gap:4px 18px;margin:0;padding:13px 24px 13px 0;list-style:none;overflow-x:auto;scrollbar-width:none;flex:1;white-space:nowrap;-webkit-mask-image:linear-gradient(to right,#000 calc(100% - 40px),transparent);mask-image:linear-gradient(to right,#000 calc(100% - 40px),transparent)}
nav.toc ol::-webkit-scrollbar{display:none}
nav.toc a{font-size:13px;color:var(--muted);text-decoration:none;padding:3px 0;border-bottom:1px solid transparent}
nav.toc .n{font-size:11px;margin-right:5px;letter-spacing:.04em}
nav.toc a:hover,nav.toc a.on{color:var(--ink);border-bottom-color:var(--ink)}
#theme{font:inherit;font-size:12px;color:var(--muted);background:transparent;border:1px solid var(--hair);border-radius:999px;padding:4px 12px;cursor:pointer;white-space:nowrap}
#theme:hover{color:var(--ink);border-color:var(--axis)}
main{padding-bottom:72px}
.hero{padding:56px 0 24px}
.headline{max-width:860px;margin:0 0 56px}
.headline .h{font-size:28px;line-height:1.3;font-weight:300;letter-spacing:-.01em}
.headline .t{color:var(--muted);margin-top:10px;font-size:16px;max-width:70ch}
.headline.att .h{border-left:2px solid var(--accent);padding-left:18px;margin-left:-20px}
.kpis{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:32px;margin:0;padding:24px 0 0;border-top:1px solid var(--hair)}
.kpi{margin:0;min-width:0}
.kpi dt{font-size:12px;color:var(--muted);letter-spacing:.03em}
.kpi dd{margin:8px 0 0;font-size:40px;line-height:1.08;font-weight:300;letter-spacing:-.025em;overflow-wrap:anywhere}
.kpi dd.word{font-size:28px;line-height:1.2;padding-top:8px}
.kpis+.insights{margin:48px 0 0}
.ovx{overflow-x:auto}
.big{display:block;font-size:40px;line-height:1.1;font-weight:300;letter-spacing:-.02em;margin-top:18px}
.big.acc{color:var(--accent)}
.big+.sub{margin-top:2px}
.sl{margin:22px 0 8px;font-size:13px;font-weight:500;color:var(--muted)}
figcaption+.sl{margin-top:0}
tr.grp th{padding-top:14px;color:var(--ink);font-size:13px}
.kpi dd.sub{margin:6px 0 0;font-size:13px;line-height:1.4;font-weight:400;letter-spacing:0;color:var(--muted)}
section.sec{padding:80px 0 8px;scroll-margin-top:52px}
section.sec+section.sec{border-top:1px solid var(--hair);margin-top:32px}
.sh{max-width:760px;margin-bottom:40px}
.sh .num{margin:0 0 10px;font-size:13px;letter-spacing:.16em;color:var(--muted)}
h2{margin:0;font-size:34px;line-height:1.12;font-weight:300;letter-spacing:-.015em}
.lede{margin-top:12px;color:var(--muted);font-size:16px;max-width:66ch}
.grid{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:56px 32px;align-items:start}
.grid+.grid,.grid+.more,.insights+.grid{margin-top:56px}
.c-12{grid-column:span 12}.c-8{grid-column:span 8}.c-6{grid-column:span 6}.c-4{grid-column:span 4}.c-3{grid-column:span 3}
figure{margin:0;min-width:0}
figcaption{margin-bottom:16px}
h3{margin:0;font-size:15px;font-weight:600;letter-spacing:0;line-height:1.35}
h4{margin:0;font-size:14px;font-weight:600}
.sub{margin-top:3px;font-size:13px;color:var(--muted)}
.note{font-size:13px;color:var(--muted);margin-top:12px;max-width:72ch}
.empty{color:var(--muted);font-size:14px;padding:16px 0;border-top:1px solid var(--hair);border-bottom:1px solid var(--hair)}
.stat{display:flex;flex-wrap:wrap;gap:4px 24px;margin-top:10px;font-size:13px;color:var(--muted)}
.stat b{font-weight:500;color:var(--ink)}
details.data{margin-top:10px;font-size:13px}
details.data summary{cursor:pointer;color:var(--muted);font-size:12px;letter-spacing:.04em;list-style:none;display:inline-block;padding:2px 0}
details.data summary::-webkit-details-marker{display:none}
details.data summary::before{content:"+ "}
details.data[open] summary::before{content:"\2013  "}
details.data summary:hover{color:var(--ink)}
.tw{overflow:auto;max-height:440px;margin-top:8px}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{padding:6px 14px 6px 0;text-align:left;border-bottom:1px solid var(--hair);vertical-align:top}
th{font-weight:500;color:var(--muted);font-size:12px;white-space:nowrap}
td.r,th.r{text-align:right}
td.nw{white-space:nowrap}
tr.hl td{background:var(--hl)}
.ledger td{vertical-align:middle;padding-top:9px;padding-bottom:9px}
.ledger td.name{font-weight:500}
.ledger .dim{display:block;font-weight:400;font-size:12px;color:var(--muted)}
.insights{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:28px 32px;margin:0 0 56px}
.ins{border-top:1px solid var(--axis);padding-top:14px;min-width:0}
.ins.att{border-top:2px solid var(--accent);padding-top:13px}
.ins .tag{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--muted)}
.ins.att .tag{color:var(--accent)}
.ins h4{margin:6px 0 4px;font-size:15px}
.ins .metric{display:block;font-size:26px;line-height:1.2;font-weight:300;margin:2px 0 4px}
.ins p{font-size:14px;color:var(--muted)}
.more{margin-top:56px}
.more>summary{display:flex;align-items:baseline;gap:14px;cursor:pointer;list-style:none;padding:12px 0;border-top:1px solid var(--hair)}.more>summary::-webkit-details-marker{display:none}.more>summary h3{margin:0}.more>summary::before{content:'+';color:var(--muted);width:10px}.more[open]>summary::before{content:'\2212'}.more[open]>summary{margin-bottom:18px}
.sm h4{font-size:13px;margin-bottom:2px}
.sm .sub{margin-bottom:8px;font-size:12px}
.chips{display:flex;flex-wrap:wrap;gap:8px}
.badge{display:inline-block;font-size:11px;letter-spacing:.08em;text-transform:uppercase;border:1px solid var(--axis);border-radius:999px;padding:1px 9px;color:var(--muted);vertical-align:2px;font-weight:500;white-space:nowrap}
.badge.acc{border-color:var(--accent);color:var(--accent)}
.recs{margin-top:8px}
.rec{display:grid;grid-template-columns:72px minmax(0,1fr);gap:0 24px;padding:32px 0;border-top:1px solid var(--hair)}
.rec .rank{font-size:44px;line-height:1;font-weight:300;letter-spacing:-.03em;color:var(--muted)}
.rec.best .rank{color:var(--accent)}
.rec h3{font-size:20px;font-weight:500;display:flex;flex-wrap:wrap;align-items:baseline;gap:4px 12px}
.rec .verdict{margin-top:6px;font-size:15px}
.rec .scoreline{display:flex;flex-wrap:wrap;align-items:center;gap:6px 18px;margin-top:16px;font-size:13px;color:var(--muted)}
.rec .scoreline b{font-size:22px;font-weight:300;color:var(--ink);letter-spacing:-.01em}
.rec.best .scoreline b{color:var(--accent)}
.rec .scoreline .chart{width:300px;max-width:100%;height:auto}
.crit{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px 28px;margin-top:20px}
.crit span{display:flex;justify-content:space-between;font-size:12px;color:var(--muted);margin-bottom:5px}
.crit .chart{width:100%;height:6px}
.crit em{font-style:normal;color:var(--ink)}
.cols3{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:20px 32px;margin-top:22px}
.cols3 h4{font-size:12px;letter-spacing:.1em;text-transform:uppercase;color:var(--muted);font-weight:500}
ul.ev{margin:8px 0 0;padding-left:16px;font-size:14px}
ul.ev li{margin:3px 0}
.redund{margin-top:16px;font-size:13px;color:var(--muted)}
.rec .meta2{margin-top:4px;font-size:13px;color:var(--muted)}
.rec.owned{opacity:.86}
.rhead{max-width:860px;margin:0 0 8px;font-size:24px;line-height:1.35;font-weight:300;letter-spacing:-.01em}
.rhead+.note{margin-bottom:8px}
td.hot{color:var(--accent);font-weight:500}
.kv.small{font-size:12px;gap:4px 18px;margin-top:16px}
.prints{margin-top:40px}
.small{font-size:12px;color:var(--muted)}
.small ul{padding-left:16px;margin:6px 0 0}
code,.mono{font-family:var(--mono);font-size:.92em}
code{background:var(--surface);border:1px solid var(--hair);border-radius:4px;padding:2px 6px}
pre.cli{font-family:var(--mono);font-size:13px;background:var(--surface);border:1px solid var(--hair);border-radius:6px;padding:14px 18px;overflow-x:auto;margin:14px 0 0}
.kv{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:6px 28px;margin:0;font-size:14px}
.kv dt{color:var(--muted)}
.kv dd{margin:0;overflow-wrap:anywhere}
.chart.mini{display:inline-block;width:110px;height:7px;vertical-align:middle;margin-right:10px}
.foot{padding-top:28px;padding-bottom:64px;color:var(--muted);font-size:12px}
.foot .wrap{border-top:1px solid var(--hair);padding-top:24px}
@media (max-width:1020px){.c-3{grid-column:span 6}.kpis{grid-template-columns:repeat(3,minmax(0,1fr))}.insights{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media (max-width:760px){.wrap{padding:0 20px}.c-8,.c-6,.c-4,.c-3{grid-column:span 12}.kpis{grid-template-columns:repeat(2,minmax(0,1fr))}.insights,.cols3{grid-template-columns:1fr}h1{font-size:38px}h2{font-size:28px}.rec{grid-template-columns:1fr}.rec .rank{margin-bottom:10px}}
@media (prefers-reduced-motion:no-preference){html{scroll-behavior:smooth}.chart rect,.chart circle{transition:opacity .15s}}
@media (prefers-reduced-motion:reduce){*,*::before{transition:none!important;animation:none!important;scroll-behavior:auto!important}}
""";

    private const string ChartCss = """
.chart{display:block;width:100%;height:auto;overflow:visible;font-family:var(--font)}
.chart text{font-size:10.5px;fill:var(--muted)}
.chart .lb{font-size:12px;fill:var(--ink)}
.chart .sm{font-size:11px}
.chart .xs{font-size:10px}
.chart .big{font-size:15px;font-weight:300}
.chart .vl{fill:var(--ink)}
.chart .at{fill:var(--accent);font-weight:600}
.chart .ttl{letter-spacing:.04em}
.chart .pkl{font-weight:500}
.chart .m{fill:var(--fjord)}
.chart .m.a{fill:var(--accent)}
.chart .m.pr{fill-opacity:.35}
.chart .f1{fill:var(--fjord)}.chart .f2{fill:var(--moss)}.chart .f3{fill:var(--clay)}.chart .f4{fill:var(--dusk)}.chart .f5{fill:var(--sand)}
.chart .ax{stroke:var(--axis);stroke-width:1;shape-rendering:crispEdges}
.chart .lead{stroke:var(--axis);stroke-width:1}
.chart .hit{fill:transparent}
.chart .band{fill:var(--sand);fill-opacity:var(--band)}
.chart .hc{fill:var(--fjord)}
.chart .z{fill:var(--hair);fill-opacity:.5}
.chart .ti{fill:var(--bg)}
.chart .pk{stroke:var(--ink);stroke-width:1;stroke-dasharray:2 3}
.chart .ar{fill:var(--fjord);fill-opacity:.16}
.chart .ln{fill:none;stroke:var(--fjord);stroke-width:1.5;stroke-linejoin:round}
.chart .dot{fill:var(--ink)}
.chart .trk{fill:var(--hair)}
.chart .wh{stroke:var(--ink);stroke-width:1.2}
.chart .th{stroke:var(--ink);stroke-opacity:.35;stroke-width:1;stroke-dasharray:3 4}
.chart .fr{fill:none;stroke:var(--axis);stroke-width:1}
.chart .gapb{fill:var(--accent);fill-opacity:.07}
.chart .cv{fill:var(--ink);fill-opacity:.7}
.chart g:hover>.m,.chart g:hover>.hc,.chart g:hover>.sw,.chart g.hl>.m,.chart g.hl>.sw{opacity:.72}
""";

    private const string Print = """
@page{size:A4;margin:16mm 14mm}
body{font-size:10pt;background:#FFFFFF}
nav.toc,#theme,details.data{display:none!important}
.wrap{max-width:none;padding:0}
.top{padding-top:0}
section.sec{break-before:page;padding-top:0;border-top:0!important;margin-top:0!important}
figure,.rec,.ins,.kpi,tr,.sm{break-inside:avoid}
h2,h3,figcaption{break-after:avoid}
a{text-decoration:none}
*{-webkit-print-color-adjust:exact;print-color-adjust:exact}
""";

    public const string BootScript =
        "(function(){var r=document.documentElement;r.className+=' js';try{var t=localStorage.getItem('clippy-theme');" +
        "if(t==='light'||t==='dark'||t==='auto')r.setAttribute('data-theme',t)}catch(e){}})();";

    public const string Script = """
(function(){
var d=document,r=d.documentElement,b=d.getElementById('theme'),N={auto:'Auto',light:'Light',dark:'Dark'},O=['auto','light','dark'];
function cur(){var t=r.getAttribute('data-theme');return N[t]?t:'auto'}
function lab(){var t=cur();b.textContent='Theme: '+N[t];b.setAttribute('aria-label','Colour theme: '+N[t]+'. Activate to change.')}
if(b){b.hidden=false;lab();b.addEventListener('click',function(){var n=O[(O.indexOf(cur())+1)%3];r.setAttribute('data-theme',n);try{localStorage.setItem('clippy-theme',n)}catch(e){}lab()})}
var as=d.querySelectorAll('nav.toc a[href^="#"]'),M={};
for(var i=0;i<as.length;i++)M[as[i].getAttribute('href').slice(1)]=as[i];
if('IntersectionObserver' in window){var io=new IntersectionObserver(function(es){es.forEach(function(e){if(!e.isIntersecting)return;for(var k in M){M[k].classList.remove('on');M[k].removeAttribute('aria-current')}var a=M[e.target.id];if(a){a.classList.add('on');a.setAttribute('aria-current','location');if(a.scrollIntoView&&a.parentNode&&a.parentNode.parentNode){var ol=a.parentNode.parentNode;var x=a.offsetLeft-ol.offsetLeft;if(x<ol.scrollLeft||x>ol.scrollLeft+ol.clientWidth-60)ol.scrollLeft=x-20}}})},{rootMargin:'-40% 0px -55% 0px'});for(var k in M){var s=d.getElementById(k);if(s)io.observe(s)}}
var K=d.querySelectorAll('[data-k]');
function hl(t,on){if(!t)return;var k=t.getAttribute('data-k');for(var j=0;j<K.length;j++)if(K[j].getAttribute('data-k')===k)K[j].classList.toggle('hl',on)}
function f(e){return e.target&&e.target.closest?e.target.closest('[data-k]'):null}
d.addEventListener('mouseover',function(e){hl(f(e),true)});
d.addEventListener('mouseout',function(e){hl(f(e),false)});
})();
""";
}
