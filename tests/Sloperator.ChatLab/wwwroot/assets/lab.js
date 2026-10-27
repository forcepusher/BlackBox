// Shared by the lab's sites: the state from the server (long polling), what Alex does, and a chat
// pane (messages and a composer where Enter sends and Shift+Enter starts a new line, as in most
// chat apps).

const sleep = ms => new Promise(r => setTimeout(r, ms));

const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const Lab = {
  app: document.body.dataset.app,
  state: null,
  listeners: [],

  async poll() {
    let v = 0;
    for (;;) {
      try {
        const r = await fetch(`/api/state?app=${this.app}&v=${v}`, { cache: 'no-store' });
        if (!r.ok) { await sleep(1000); continue; }
        const s = await r.json();
        if (s.v !== v) {
          v = s.v;
          this.state = s;
          for (const f of this.listeners) {
            try { f(s); } catch (e) { console.error(e); }
          }
        }
      } catch {
        await sleep(1000);
      }
    }
  },

  onState(f) {
    this.listeners.push(f);
    if (this.state) f(this.state);
  },

  post(path, body) {
    return fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  },
  send(chat, text) { return this.post('/api/send', { chat, text }); },
  open(chat) { return this.post('/api/open', { chat }); },
  comment(post, replyTo, text) { return this.post('/api/comment', { post, replyTo, text }); },
  like(post, comment) { return this.post('/api/like', { post, comment }); },
  seen() { return this.post('/api/seen', { app: this.app }); },

  person(id) { return this.state?.people?.[id] ?? { name: id, initials: '?', color: '#999', status: '' }; },
  chat(id) { return this.state?.chats?.find(c => c.id === id); },
};

// ---- formatting

function parseTime(iso) { return new Date(iso); }

function fmtTime(iso) {
  const d = parseTime(iso);
  return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
}

function dayLabel(iso, now) {
  const d = parseTime(iso), n = parseTime(now ?? Lab.state?.now ?? new Date().toISOString());
  const day = x => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((day(n) - day(d)) / 86400000);
  if (diff === 0) return 'Today';
  if (diff === 1) return 'Yesterday';
  if (diff < 7) return d.toLocaleDateString('en-US', { weekday: 'long' });
  return d.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: d.getFullYear() === n.getFullYear() ? undefined : 'numeric' });
}

// The time in a chat list: today's time, a weekday this week, or a date.
function listTime(iso) {
  const label = dayLabel(iso);
  if (label === 'Today') return fmtTime(iso);
  if (label === 'Yesterday') return 'Yesterday';
  const d = parseTime(iso);
  const diff = (parseTime(Lab.state.now) - d) / 86400000;
  if (diff < 7) return d.toLocaleDateString('en-US', { weekday: 'short' });
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
}

// "2h", "1d": how long ago, as social networks write it.
function ago(iso) {
  const s = (parseTime(Lab.state.now) - parseTime(iso)) / 1000;
  if (s < 60) return 'Just now';
  if (s < 3600) return Math.floor(s / 60) + 'm';
  if (s < 86400) return Math.floor(s / 3600) + 'h';
  if (s < 7 * 86400) return Math.floor(s / 86400) + 'd';
  return Math.floor(s / (7 * 86400)) + 'w';
}

function avatar(personId, size = 40, extra = '') {
  const p = Lab.person(personId);
  return `<div class="avatar ${extra}" style="width:${size}px;height:${size}px;background:${esc(p.color)};font-size:${Math.round(size * 0.38)}px">${esc(p.initials)}</div>`;
}

const textHtml = s => esc(s).replace(/\n/g, '<br>');

const verifiedBadge = '<svg class="verified" viewBox="0 0 24 24" width="16" height="16" aria-label="Verified"><path fill="#3390ec" d="M12 1l2.6 2.2 3.4-.4.9 3.3 3 1.7-1.2 3.2 1.2 3.2-3 1.7-.9 3.3-3.4-.4L12 23l-2.6-2.2-3.4.4-.9-3.3-3-1.7L3.3 13 2.1 9.8l3-1.7.9-3.3 3.4.4z"/><path fill="#fff" d="M10.6 15.6l-3.3-3.3 1.4-1.4 1.9 1.9 4.7-4.7 1.4 1.4z"/></svg>';

// ---- a chat pane: messages above, composer below

class ChatPane {
  // opts: names (true: show sender names on incoming messages; 'group': only in group chats), avatars (show the sender's picture),
  // placeholder, sendLabel (text on the send button; an icon when empty), emptyText.
  constructor(root, opts = {}) {
    this.opts = opts;
    this.chatId = null;
    this.drafts = {};
    this.signature = '';
    root.classList.add('cp');
    root.innerHTML = `
      <div class="cp-messages"></div>
      <form class="cp-composer" autocomplete="off">
        <textarea class="cp-input" rows="1" placeholder="${esc(opts.placeholder ?? 'Write a message…')}" aria-label="${esc(opts.placeholder ?? 'Message')}"></textarea>
        <button class="cp-send" type="submit" title="Send" aria-label="Send">${opts.sendLabel ? esc(opts.sendLabel) :
          '<svg viewBox="0 0 24 24" width="24" height="24"><path fill="currentColor" d="M3.4 20.4l17.4-7.5c.8-.4.8-1.5 0-1.9L3.4 3.6c-.7-.3-1.4.2-1.4.9L2 9.1c0 .5.4.9.9 1l12.1 1.9-12.1 1.9c-.5.1-.9.5-.9 1l.1 4.6c0 .7.7 1.2 1.3.9z"/></svg>'}</button>
      </form>`;
    this.list = root.querySelector('.cp-messages');
    this.form = root.querySelector('.cp-composer');
    this.input = root.querySelector('.cp-input');
    this.button = root.querySelector('.cp-send');
    this.input.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        this.submit();
      }
    });
    this.input.addEventListener('input', () => {
      this.drafts[this.chatId] = this.input.value;
      this.grow();
      this.button.classList.toggle('ready', this.input.value.trim().length > 0);
    });
    this.form.addEventListener('submit', e => { e.preventDefault(); this.submit(); });
    window.addEventListener('resize', () => this.grow());
  }

  grow() {
    this.input.style.height = 'auto';
    // Hidden or not laid out yet: its height is measured again once it shows.
    if (!this.input.offsetParent || this.input.clientWidth < 50) return;
    const borders = this.input.offsetHeight - this.input.clientHeight;
    this.input.style.height = Math.min(this.input.scrollHeight + borders, 150) + 'px';
    this.input.style.overflowY = this.input.scrollHeight + borders > 150 ? 'auto' : 'hidden';
  }

  setChat(chatId) {
    if (this.chatId === chatId) return;
    this.chatId = chatId;
    this.signature = '';
    this.input.value = this.drafts[chatId] ?? '';
    requestAnimationFrame(() => this.grow());
    this.button.classList.toggle('ready', this.input.value.trim().length > 0);
    if (Lab.state) this.render(Lab.state, true);
  }

  focus() { this.input.focus(); }

  async submit() {
    const text = this.input.value;
    if (!text.trim() || !this.chatId || this.sending) return;
    this.sending = true;
    try {
      const r = await Lab.send(this.chatId, text);
      if (r.ok) {
        this.input.value = '';
        this.drafts[this.chatId] = '';
        this.grow();
        this.button.classList.remove('ready');
      }
    } finally {
      this.sending = false;
    }
  }

  render(state, force = false) {
    const chat = state.chats.find(c => c.id === this.chatId);
    if (!chat) {
      this.list.innerHTML = this.opts.emptyText ? `<div class="cp-empty">${esc(this.opts.emptyText)}</div>` : '';
      this.signature = '';
      return;
    }
    const sig = chat.messages.map(m => m.id + (m.read ? 'r' : '')).join(',') + '|' + chat.typing.join(',');
    if (sig === this.signature && !force) return;
    const firstRender = this.signature === '';
    this.signature = sig;
    const atBottom = this.list.scrollHeight - this.list.scrollTop - this.list.clientHeight < 120;
    let html = '', lastDay = '', lastFrom = null;
    for (const m of chat.messages) {
      const day = dayLabel(m.time, state.now);
      if (day !== lastDay) {
        html += `<div class="cp-day"><span>${esc(day)}</span></div>`;
        lastDay = day;
        lastFrom = null;
      }
      if (m.from === '') {
        html += `<div class="cp-system"><span>${esc(m.text)}</span></div>`;
        lastFrom = null;
        continue;
      }
      const out = m.from === state.me;
      const p = Lab.person(m.from);
      const showName = !out && m.from !== lastFrom && (this.opts.names === true || (this.opts.names === 'group' && chat.kind === 'group'));
      const showAvatar = !out && this.opts.avatars;
      html += `<div class="cp-msg ${out ? 'out' : 'in'}${m.from === lastFrom ? ' cont' : ''}">` +
        (showAvatar ? (m.from !== lastFrom ? avatar(m.from, 30, 'cp-avatar') : '<div class="cp-avatar-gap"></div>') : '') +
        `<div class="cp-bubble">` +
        (showName ? `<div class="cp-name" style="color:${esc(p.color)}">${esc(p.name)}</div>` : '') +
        `<span class="cp-text">${textHtml(m.text)}</span>` +
        `<span class="cp-meta"><span class="cp-time">${fmtTime(m.time)}</span>` +
        (out ? `<span class="cp-ticks${m.read ? ' read' : ''}">${m.read ? '✓✓' : '✓'}</span>` : '') +
        `</span></div></div>`;
      lastFrom = m.from;
    }
    for (const who of chat.typing) {
      const p = Lab.person(who);
      html += `<div class="cp-msg in">` + (this.opts.avatars ? avatar(who, 30, 'cp-avatar') : '') +
        `<div class="cp-bubble cp-typing"><span class="cp-dots"><i></i><i></i><i></i></span>` +
        `<span class="cp-typing-who">${esc(p.name.split(' ')[0])} is typing</span></div></div>`;
    }
    this.list.innerHTML = html;
    const last = chat.messages[chat.messages.length - 1];
    if (firstRender || atBottom || (last && last.from === state.me))
      this.list.scrollTop = this.list.scrollHeight;
  }
}

Lab.poll();
