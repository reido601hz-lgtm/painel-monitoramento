/* Painel BROK - cliente ES5 (Chrome 42 / Android 4.2), sem fetch/Promise/let/const */

(function () {
  'use strict';
  /* Tela cheia no tablet (best effort; Chrome 42 pode exigir gesto do usuario) */
  function entrarTelaCheia() {
    try {
      var el = document.documentElement;
      var fn = el.requestFullscreen || el.webkitRequestFullscreen ||
               el.mozRequestFullScreen || el.msRequestFullscreen;
      if (fn) fn.call(el);
    } catch (e) { }
  }
  setTimeout(entrarTelaCheia, 800);
  document.addEventListener('click', entrarTelaCheia, false);
  document.addEventListener('touchstart', entrarTelaCheia, false);

  var API = '/api/metrics';
  var INTERVAL = 1000;

  function get(id) { return document.getElementById(id); }

  var cpuG = null;
  var gpuG = null;
  var coreBars = [];
  var cache = { fans: '', temps: '', io: '' };
  var busy = false;
  var fails = 0;

  /* Hora do painel = hora do SERVIDOR (PC), nao a do tablet: o RTC do tablet perde a data
     (fica em 1970) e o tzdata 2012h do Android 4.2 ainda aplica horario de verao no Brasil.
     Por isso o fuso e fixo em UTC-3 (Brasilia, sem horario de verao desde 2019). */
  var FUSO_MS = -3 * 3600 * 1000;
  var offsetMs = null; /* hora do servidor (UTC) menos Date() do tablet; null = nao sincronizou */

  function drawGauge(circleId, valId, pct, centerText) {
    var circle = get(circleId);
    if (!circle) return;
    var p = Math.max(0, Math.min(100, pct || 0)) / 100;
    var offset = 364 * (1 - p);
    circle.setAttribute('stroke-dashoffset', offset.toFixed(1));
    circle.setAttribute('stroke', p < 0.6 ? '#00F0FF' : (p < 0.85 ? '#f59e0b' : '#ef4444'));
    /* ponta reta: com 0% o traco de comprimento zero ainda aparecia como um fio no topo */
    circle.setAttribute('stroke-opacity', p <= 0.004 ? '0' : '1');

    var valEl = get(valId);
    if (valEl) valEl.textContent = centerText;
  }

  /* ---------------- formatacao ---------------- */

  function num(v, d) {
    if (v === null || v === undefined || isNaN(v)) return '--';
    var s = Number(v).toFixed(d === undefined ? 0 : d);
    return s.replace('.', ',');
  }

  /* versao curta para caber numa linha: K = KB/s, M = MB/s */
  function bytesCurto(v) {
    if (!v || v <= 0) return '0K';
    var kb = v / 1024;
    if (kb < 1000) return num(kb, 0) + 'K';
    return num(kb / 1024, 1) + 'M';
  }

  function bytes(v) {
    if (!v || v <= 0) return '0 KB/s';
    var kb = v / 1024;
    if (kb < 1000) return num(kb, 0) + ' KB/s';
    return num(kb / 1024, 1) + ' MB/s';
  }

  function uptime(sec) {
    if (!sec || sec < 0) return '--';
    var d = Math.floor(sec / 86400);
    var h = Math.floor((sec % 86400) / 3600);
    var m = Math.floor((sec % 3600) / 60);
    /* rotulo discreto + tempo em destaque (estilo .selo2 span / .selo2 b) */
    var rot = '<span>ligado h\u00e1</span> ';
    if (d > 0) return rot + '<b>' + d + 'd ' + h + 'h</b>';
    if (h > 0) return rot + '<b>' + h + 'h ' + m + 'min</b>';
    return rot + '<b>' + m + 'min</b>';
  }

  /* Cores das temperaturas (verde / laranja / vermelho) por TIPO de sensor, em graus C: [laranja a
     partir de, vermelho a partir de]. Referencias e grau de certeza: README do painel ("Cores das
     temperaturas"). Resumo: CPU (Intel i5-14600K, Tjunction 100) 70/90; GPU (RTX 4060 Ti, maximo 90)
     75/85; VRM 80/100; chipset 70/90; RAM DDR4 (limite de case 85) 65/80; sensores de placa 55/70;
     NVMe 65/75; SSD SATA (Kingston A400, operacao 0-70) 55/65; HDD (Seagate, 5-50, novos ate 60) 45/55. */
  var LIM_TEMP = {
    cpu: [70, 90], gpu: [75, 85], vrm: [80, 100], pch: [70, 90], ram: [65, 80], placa: [55, 70],
    nvme: [65, 75], ssd: [55, 65], hdd: [45, 55]
  };

  function classeTemp(tipo, v) {
    var l = LIM_TEMP[tipo] || LIM_TEMP.placa;
    if (v >= l[1]) return 'crit';
    if (v >= l[0]) return 'warn';
    return 'ok';
  }

  function tipoSensor(nome) {
    if (/VRM/i.test(nome)) return 'vrm';
    if (/PCH|chipset/i.test(nome)) return 'pch';
    if (/RAM|DIMM/i.test(nome)) return 'ram';
    return 'placa';
  }

  /* ST... = Seagate (HDD); "NVMe" no nome = NVMe; o resto e' tratado como SSD SATA (limites mais
     rigorosos, o que erra para o lado seguro) */
  function tipoDisco(nome) {
    if (/^ST\d/i.test(nome)) return 'hdd';
    if (/nvme/i.test(nome)) return 'nvme';
    return 'ssd';
  }

  function flagClass(v, warn, crit) {
    if (v >= crit) return 'crit';
    if (v >= warn) return 'warn';
    return '';
  }

  function shortName(s, max) {
    if (!s) return '';
    s = String(s).replace(/\(R\)|\(TM\)|\(C\)/g, '').replace(/\s+/g, ' ');
    if (s.length > max) return s.substring(0, max - 1) + '\u2026';
    return s;
  }

  /* Com texto de 22px a lista de temperaturas cabe em 6 linhas (uma coluna). Ordem de
     prioridade: o que nao aparece em outro card vem primeiro; CPU Package/Core Max/Core
     Average repetem a CPU do anel e ficam por ultimo (so entram se sobrar lugar). */
  var TEMP_ORDEM = ['VRM MOS', 'Chipset (PCH)', 'PCIe x16', 'RAM DIMM #0',
    'Sistema 1', 'Sistema 2', 'Core Max', 'CPU Package', 'Core Average']; /* "CPU (socket)" fica de fora: o anel ja mostra a CPU */
  var TEMP_MAX = 6;

  function ordemTemp(nome) {
    for (var i = 0; i < TEMP_ORDEM.length; i++) if (TEMP_ORDEM[i] === nome) return i;
    return TEMP_ORDEM.length;
  }

  /* Nome curto de disco para caber em fonte grande: primeira palavra (marca/modelo) mais a
     segunda so' se for curta e tiver numero (capacidade, ex.: "WALRAM 512G"). */
  function nomeDisco(s) {
    s = String(s || '').replace(/\(R\)|\(TM\)|\(C\)/g, '').replace(/\s+/g, ' ').replace(/^ | $/g, '');
    var p = s.split(' ');
    var r = p[0];
    if (p.length > 1 && p[1].length <= 5 && /\d/.test(p[1])) r += ' ' + p[1];
    return r;
  }

  /* ---------------- listas ---------------- */

  function renderList(ulId, key, items) {
    var sig = '';
    var i;
    for (i = 0; i < items.length; i++) sig += items[i].name + '=' + items[i].value + '|';
    if (sig === cache[key]) return;
    cache[key] = sig;

    var ul = get(ulId);
    if (!ul) return;

    var html = '';
    for (i = 0; i < items.length; i++) {
      var it = items[i];
      html += '<li><span>' + escapeHtml(it.name) + '</span><b class="' + (it.cls || '') + '">' +
        escapeHtml(it.value) + '</b></li>';
    }
    ul.innerHTML = html;
  }

  function escapeHtml(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  /* ---------------- render ---------------- */

  function bars(pct) {
    var e = pct >= 90 ? 'crit' : (pct >= 70 ? 'warn' : '');
    return e;
  }

  function renderCoreBars(cores) {
    var box = get('cores');
    if (!box || !cores) return;

    if (coreBars.length !== cores.length) {
      var html = '';
      var cw = (100 / cores.length) - 0.8;
      var estilo = 'width:' + cw.toFixed(2) + '%;margin-right:0.8%';
      for (var i = 0; i < cores.length; i++) html += '<div class="core" style="' + estilo + '"><i></i></div>';
      box.innerHTML = html;
      coreBars = box.getElementsByTagName('i');
    }

    for (var j = 0; j < cores.length && j < coreBars.length; j++) {
      coreBars[j].style.height = Math.max(3, Math.min(100, cores[j])) + '%';
    }
  }

  function render(d) {
    get('host').innerHTML = escapeHtml(d.host || 'BROK');

    /* CPU */
    if (d.cpu) {
      var load = d.cpu.load || 0;
      drawGauge('cpuCircle', 'cpuVal', load, num(load, 0) + '%');
      get('cpuName').innerHTML = escapeHtml(shortName(d.cpu.name, 30));
      get('cpuTemp').innerHTML = num(d.cpu.temp, 0);
      get('cpuTemp').className = 'vt ' + classeTemp('cpu', d.cpu.temp);
      get('cpuPower').innerHTML = num(d.cpu.power, 0);
      renderCoreBars(d.cpu.cores);
      var th = get('cpuThreads');
      if (th && d.cpu.cores) th.innerHTML = d.cpu.cores.length + ' threads';
    }

    /* GPU */
    if (d.gpu) {
      var gload = d.gpu.load || 0;
      drawGauge('gpuCircle', 'gpuVal', gload, num(gload, 0) + '%');
      get('gpuName').innerHTML = escapeHtml(shortName(d.gpu.name, 30));
      get('gpuTemp').innerHTML = num(d.gpu.temp, 0);
      get('gpuTemp').className = 'vt ' + classeTemp('gpu', d.gpu.temp);
      get('gpuPower').innerHTML = num(d.gpu.power, 0);
      get('gpuClock').innerHTML = num(d.gpu.clockMhz, 0);
      var gb = get('gpuBadge');
      if (gb) gb.innerHTML = num((d.gpu.memTotalMb || 0) / 1024, 0) + ' GB';

      var vt = d.gpu.memTotalMb || 0;
      var vu = d.gpu.memUsedMb || 0;
      var vp = vt > 0 ? (vu / vt) * 100 : 0;
      get('vramText').innerHTML = num(vu / 1024, 1) + ' / ' + num(vt / 1024, 1) + ' GB';
      var vb = get('vramBar');
      vb.style.width = vp + '%';
      vb.className = bars(vp);
    }

    /* RAM */
    if (d.ram) {
      get('ramText').innerHTML = num(d.ram.usedGb, 1) + ' / ' + num(d.ram.totalGb, 1) + ' GB';
      var rb = get('ramBar');
      rb.style.width = (d.ram.percent || 0) + '%';
      rb.className = bars(d.ram.percent || 0);
    }

    get('uptime').innerHTML = uptime(d.uptimeSec);

    /* ventoinhas */
    var fans = [];
    if (d.fans) {
      /* as duas ventoinhas da GPU viram uma linha "GPU" (rpm da mais rapida); a GPU em % some
         quando ha rpm */
      var gpuRpm = 0;
      var i;
      var f;
      for (i = 0; i < d.fans.length; i++) {
        f = d.fans[i];
        if (/^GPU Fan/i.test(f.name) && f.rpm > gpuRpm) gpuRpm = f.rpm;
      }
      for (i = 0; i < d.fans.length; i++) {
        f = d.fans[i];
        if (/^GPU Fan/i.test(f.name)) continue;
        if (gpuRpm > 0 && f.name === 'GPU' && !(f.rpm > 0)) continue;
        fans.push({
          name: f.name,
          value: f.rpm > 0 ? num(f.rpm, 0) + ' rpm' : num(f.percent, 0) + '%'
        });
      }
      if (gpuRpm > 0) fans.push({ name: 'GPU', value: num(gpuRpm, 0) + ' rpm' });
    }
    /* o card cabe 6 linhas de 36 px; com as ventoinhas da GPU (rpm) a lista chega a 7: saem
       primeiro as que so tem porcentagem (a GPU ja aparece em rpm) e depois o excedente */
    for (var fi = fans.length - 1; fi >= 0 && fans.length > 6; fi--) {
      if (/%$/.test(fans[fi].value)) fans.splice(fi, 1);
    }
    if (fans.length > 6) fans.length = 6;
    if (!fans.length) fans.push({ name: 'sem leitura', value: '--' });
    renderList('fans', 'fans', fans);

    /* temperaturas */
    var temps = [];
    if (d.temps) {
      var tl = d.temps.slice(0);
      /* ordena por prioridade; sort do ES5 nao e estavel, entao o indice desempata */
      for (var ti = 0; ti < tl.length; ti++) tl[ti] = { o: ordemTemp(tl[ti].name), i: ti, t: tl[ti] };
      tl.sort(function (a, b) { return (a.o - b.o) || (a.i - b.i); });
      for (var t = 0; t < tl.length && t < TEMP_MAX; t++) {
        temps.push({
          name: shortName(tl[t].t.name, 22),
          value: num(tl[t].t.value, 0) + '\u00b0C',
          cls: classeTemp(tipoSensor(tl[t].t.name), tl[t].t.value)
        });
      }
    }
    renderList('temps', 'temps', temps);

    /* rede e discos: discos sem nome repetido e em ordem fixa (a ordem do servidor varia
       a cada leitura e a lista piscava); nomes curtos; "KINGSTON" repetido ganha 1, 2 */
    var io = [];
    var disks = (d.disks || []).slice(0);
    disks.sort(function (a, b) { return a.name < b.name ? -1 : (a.name > b.name ? 1 : 0); });
    var vistos = {};
    var unicos = [];
    for (var u = 0; u < disks.length; u++) {
      if (vistos[disks[u].name]) continue;
      vistos[disks[u].name] = true;
      unicos.push(disks[u]);
    }
    var curtos = [];
    var cont = {};
    for (var c1 = 0; c1 < unicos.length; c1++) {
      var cn = nomeDisco(unicos[c1].name);
      curtos.push(cn);
      cont[cn] = (cont[cn] || 0) + 1;
    }
    /* so os 3 discos MAIS QUENTES (o resto poluia), mostrados em ordem de nome para a lista nao
       trocar de lugar a cada leitura */
    var seq = {};
    var itens = [];
    for (var k = 0; k < unicos.length; k++) {
      var nm = curtos[k];
      if (cont[nm] > 1) { seq[nm] = (seq[nm] || 0) + 1; nm += ' ' + seq[nm]; }
      var tp = unicos[k].temp;
      if (tp === null || tp === undefined || isNaN(tp)) tp = -1;
      itens.push({ i: k, nome: nm, temp: tp, tipo: tipoDisco(unicos[k].name) });
    }
    var quentes = itens.slice(0).sort(function (a, b) { return (b.temp - a.temp) || (a.i - b.i); }).slice(0, 3);
    quentes.sort(function (a, b) { return a.i - b.i; });
    for (var q = 0; q < quentes.length; q++) {
      io.push({
        name: quentes[q].nome,
        value: num(quentes[q].temp, 0) + '\u00b0C', /* sem o % de uso e sem espaco antes do grau */
        cls: classeTemp(quentes[q].tipo, quentes[q].temp)
      });
    }

    /* rede: uma linha so' com descida e subida (K = KB/s, M = MB/s) */
    var nets = (d.net || []).slice(0);
    nets.sort(function (a, b) { return (b.down + b.up) - (a.down + a.up); });
    if (nets.length) {
      io.push({
        name: shortName(nets[0].name, 18),
        value: '\u2193' + bytesCurto(nets[0].down) + ' \u2191' + bytesCurto(nets[0].up)
      });
    }
    renderList('io', 'io', io);
  }

  /* ---------------- rede ---------------- */

  function setOffline(on) {
    var st = get('status');
    var dot = get('dot');
    var chip = get('chip');
    /* o chip de status so' aparece quando ha falha (com tudo OK ele poluia) */
    if (chip) chip.style.display = on ? 'block' : 'none';
    if (st) st.innerHTML = on ? 'Sem conex' + '\u00e3o com o servidor' : 'Sistemas operacionais OK';
    if (dot) dot.className = on ? 'dot off' : 'dot';
  }

  function poll() {
    if (busy) return;
    busy = true;

    var xhr = new XMLHttpRequest();
    var finished = false;

    function done(okState) {
      if (finished) return;
      finished = true;
      busy = false;
      if (okState) {
        fails = 0;
        setOffline(false);
      } else {
        fails++;
        if (fails >= 2) setOffline(true);
      }
      setTimeout(poll, INTERVAL);
    }

    xhr.open('GET', API + '?t=' + new Date().getTime(), true);
    xhr.timeout = 4000;

    xhr.onreadystatechange = function () {
      if (xhr.readyState !== 4) return;
      if (xhr.status === 200) {
        var data;
        try {
          data = JSON.parse(xhr.responseText);
        } catch (e) {
          done(false);
          return;
        }
        sincronizarRelogio(xhr, data);
        try {
          render(data);
        } catch (e2) {
          /* nao derruba o poll por erro de render */
        }
        done(true);
      } else {
        done(false);
      }
    };

    xhr.ontimeout = function () { done(false); };
    xhr.onerror = function () { done(false); };

    try {
      xhr.send(null);
    } catch (e3) {
      done(false);
    }
  }

  /* ---------------- FPS e frametime (jogo em primeiro plano) ---------------- */

  /* O servidor (/api/fps, PresentMon) devolve {ativo:false} sem jogo. Com jogo, o card
     "Nucleos" vira "Frametime": FPS, 1% low, CPU/GPU ocupadas e a linha de 12 s (120 pontos
     de 100 ms, o MAXIMO de cada ponto, para os picos aparecerem). Linha de 3 px em SVG
     (estilo "N" escolhido pelo usuario). Poll de 500 ms com jogo, 2 s sem jogo, 5 s se o
     servidor nao tiver /api/fps (build antigo). */
  var FT_W = 194;
  var FT_H = 70;
  var FT_ESCALAS = [8, 12, 16.7, 25, 33.3, 50, 100];
  var fpsModo = null; /* null = ainda nao aplicou; true = mostrando frametime */
  var fpsBusy = false;
  var fpsFalhas = 0;

  function mostrarFt(on) {
    if (fpsModo === on) return;
    fpsModo = on;
    get('ftBox').style.display = on ? 'block' : 'none';
    get('ftJogo').style.display = on ? 'block' : 'none';
    get('nucBox').style.display = on ? 'none' : 'block';
    get('nucTit').innerHTML = on ? 'Frametime' : 'N&uacute;cleos';
  }

  /* menor escala que cabe 2,2x o frametime medio e 1,25x o percentil 95 da serie. Um pico isolado
     (carregamento, troca de tela) NAO define a escala: ele sai cortado no topo, e o resto da linha
     continua legivel (com o maximo como base, um quadro de 50 ms achatava tudo em 4 px) */
  function escalaFt(serie, ft) {
    var o = serie.slice(0).sort(function (a, b) { return a - b; });
    var p95 = o.length ? o[Math.min(o.length - 1, Math.floor(o.length * 0.95))] : 0;
    var alvo = Math.max(ft * 2.2, p95 * 1.25);
    for (var k = 0; k < FT_ESCALAS.length; k++) if (FT_ESCALAS[k] >= alvo) return FT_ESCALAS[k];
    return FT_ESCALAS[FT_ESCALAS.length - 1];
  }

  function yFt(v, ymax) {
    var t = Math.min(v, ymax) / ymax;
    return (FT_H - 3) - t * (FT_H - 6); /* 3..67: a linha de 3 px nao encosta na borda */
  }

  function posRef(id, ms, ymax) {
    var l = get(id);
    if (!l) return;
    if (ms >= ymax) { l.setAttribute('stroke-opacity', '0'); return; }
    var y = yFt(ms, ymax).toFixed(1);
    l.setAttribute('y1', y);
    l.setAttribute('y2', y);
    l.setAttribute('stroke-opacity', '1');
  }

  function desenharFt(serie, ft) {
    var n = serie.length;
    if (n < 2) return;
    var ymax = escalaFt(serie, ft);
    var pts = '';
    for (var i = 0; i < n; i++) {
      pts += (i * (FT_W / (n - 1))).toFixed(1) + ',' + yFt(serie[i], ymax).toFixed(1) + ' ';
    }
    get('ftLinha').setAttribute('points', pts);
    posRef('ftRef1', 16.7, ymax); /* 60 fps */
    posRef('ftRef2', 33.3, ymax); /* 30 fps */
  }

  function aplicarFps(d) {
    if (!d || !d.ativo) { mostrarFt(false); return; }
    mostrarFt(true);
    get('ftJogo').innerHTML = escapeHtml(d.jogo || '');
    get('ftFps').innerHTML = num(d.fps, 0);
    get('ftLow').innerHTML = '<span>1%</span> <b>' + num(d.fps1, 0) + '</b>';
    get('ftCg').innerHTML = 'CPU <b>' + num(d.cpu, 1) + '</b> GPU <b>' + num(d.gpu, 1) + '</b> ms';
    desenharFt(d.serie || [], d.ft || 0);
  }

  function pollFps() {
    if (fpsBusy) return;
    fpsBusy = true;
    var xhr = new XMLHttpRequest();
    var feito = false;

    function fim(d) {
      if (feito) return;
      feito = true;
      fpsBusy = false;
      if (d) {
        fpsFalhas = 0;
        try { aplicarFps(d); } catch (e) { }
      } else {
        fpsFalhas++;
        if (fpsFalhas >= 3) { try { aplicarFps(null); } catch (e2) { } }
      }
      setTimeout(pollFps, d && d.ativo ? 500 : (fpsFalhas ? 5000 : 2000));
    }

    xhr.open('GET', '/api/fps?t=' + new Date().getTime(), true);
    xhr.timeout = 2500;
    xhr.onreadystatechange = function () {
      if (xhr.readyState !== 4) return;
      if (xhr.status === 200) {
        var d = null;
        try { d = JSON.parse(xhr.responseText); } catch (e3) { }
        fim(d);
      } else {
        fim(null);
      }
    };
    xhr.ontimeout = function () { fim(null); };
    xhr.onerror = function () { fim(null); };

    try {
      xhr.send(null);
    } catch (e4) {
      fim(null);
    }
  }

  /* ---------------- relogio ---------------- */

  /* Calcula a diferenca entre a hora do servidor e a do tablet. Usa o cabecalho HTTP Date
     da resposta (hora atual do servidor) e, se faltar, o campo ts (epoch em segundos, UTC).
     O Date vem truncado em segundos, entao o offset oscila ate ~1 s entre leituras: so
     reajusta quando a diferenca passa de 2,5 s (ex.: o relogio do tablet foi corrigido). */
  function sincronizarRelogio(xhr, d) {
    var srv = NaN;
    try {
      var h = xhr.getResponseHeader('Date');
      if (h) srv = Date.parse(h);
    } catch (e) { }
    if (isNaN(srv) && d && d.ts) srv = d.ts * 1000;
    if (isNaN(srv)) return;
    var off = srv - new Date().getTime();
    if (offsetMs === null || Math.abs(off - offsetMs) > 2500) offsetMs = off;
  }

  function tickClock() {
    var el = get('clock');
    if (!el) return;
    /* sem sincronizar nao mostra hora: uma hora errada e pior que nenhuma */
    if (offsetMs === null) return;
    var d = new Date(new Date().getTime() + offsetMs + FUSO_MS);
    function p(n) { return n < 10 ? '0' + n : '' + n; }
    el.innerHTML = p(d.getUTCHours()) + ':' + p(d.getUTCMinutes()) + ':' + p(d.getUTCSeconds());
  }

  function boot() {
    // cpuGauge is now SVG, no setup needed
    // gpuGauge is now SVG, no setup needed

    drawGauge('cpuCircle', 'cpuVal', 0, '--');
    drawGauge('gpuCircle', 'gpuVal', 0, '--');

    tickClock();
    setInterval(tickClock, 1000);
    poll();
    setTimeout(pollFps, 1500);
  }

  if (document.addEventListener) {
    document.addEventListener('DOMContentLoaded', boot, false);
  } else {
    window.attachEvent('onload', boot);
  }
})();
