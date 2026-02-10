(function () {
  "use strict";

  const SCRIPT = document.currentScript;
  const API_KEY = SCRIPT?.getAttribute("data-api-key") || "";
  const MODE = SCRIPT?.getAttribute("data-mode") || "search";
  const THEME = SCRIPT?.getAttribute("data-theme") || "auto";
  const PLACEHOLDER =
    SCRIPT?.getAttribute("data-placeholder") || "Search...";
  const POSITION = SCRIPT?.getAttribute("data-position") || "inline";
  const BASE_URL =
    SCRIPT?.getAttribute("data-base-url") ||
    new URL(SCRIPT?.src || "").origin;

  const API = {
    search: `${BASE_URL}/api/saas/search`,
    chat: `${BASE_URL}/api/saas/chat/stream`,
    autocomplete: `${BASE_URL}/api/saas/autocomplete`,
    config: `${BASE_URL}/api/saas/widget/config`,
  };

  const STYLES = `
:host {
  all: initial;
  display: block;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  font-size: 14px;
  line-height: 1.5;
  color: var(--lr-text, #1f2937);
  --lr-bg: #ffffff;
  --lr-text: #1f2937;
  --lr-muted: #6b7280;
  --lr-border: #e5e7eb;
  --lr-primary: #6366f1;
  --lr-primary-hover: #4f46e5;
  --lr-surface: #f9fafb;
  --lr-radius: 8px;
}
:host([data-theme="dark"]), :host .dark {
  --lr-bg: #111827;
  --lr-text: #f9fafb;
  --lr-muted: #9ca3af;
  --lr-border: #374151;
  --lr-surface: #1f2937;
}
* { box-sizing: border-box; margin: 0; padding: 0; }
.lr-container {
  max-width: 100%;
  background: var(--lr-bg);
  border: 1px solid var(--lr-border);
  border-radius: var(--lr-radius);
  overflow: hidden;
}
.lr-container.floating {
  position: fixed;
  bottom: 20px;
  right: 20px;
  width: 400px;
  max-height: 600px;
  box-shadow: 0 20px 60px rgba(0,0,0,0.15);
  z-index: 99999;
}
.lr-search-box {
  display: flex;
  align-items: center;
  padding: 8px 12px;
  border-bottom: 1px solid var(--lr-border);
  gap: 8px;
}
.lr-search-box svg { flex-shrink: 0; color: var(--lr-muted); }
.lr-search-box input {
  flex: 1;
  border: none;
  outline: none;
  font-size: 14px;
  background: transparent;
  color: var(--lr-text);
}
.lr-search-box input::placeholder { color: var(--lr-muted); }
.lr-autocomplete {
  border-bottom: 1px solid var(--lr-border);
  background: var(--lr-surface);
}
.lr-autocomplete-item {
  padding: 6px 12px;
  cursor: pointer;
  font-size: 13px;
  color: var(--lr-text);
}
.lr-autocomplete-item:hover, .lr-autocomplete-item.active {
  background: var(--lr-primary);
  color: white;
}
.lr-results {
  max-height: 400px;
  overflow-y: auto;
  padding: 8px;
}
.lr-result {
  padding: 10px 12px;
  border-bottom: 1px solid var(--lr-border);
  cursor: default;
}
.lr-result:last-child { border-bottom: none; }
.lr-result-title {
  font-weight: 600;
  font-size: 13px;
  color: var(--lr-text);
  margin-bottom: 4px;
}
.lr-result-text {
  font-size: 13px;
  color: var(--lr-muted);
  display: -webkit-box;
  -webkit-line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.lr-result-section {
  font-size: 11px;
  color: var(--lr-primary);
  margin-top: 4px;
}
.lr-chat-panel {
  max-height: 350px;
  overflow-y: auto;
  padding: 12px;
}
.lr-chat-msg {
  margin-bottom: 12px;
  padding: 8px 12px;
  border-radius: 6px;
  font-size: 13px;
  white-space: pre-wrap;
}
.lr-chat-msg.user {
  background: var(--lr-primary);
  color: white;
  margin-left: 40px;
  text-align: right;
}
.lr-chat-msg.assistant {
  background: var(--lr-surface);
  color: var(--lr-text);
  margin-right: 40px;
}
.lr-sources {
  padding: 8px 12px;
  border-top: 1px solid var(--lr-border);
  font-size: 12px;
  color: var(--lr-muted);
}
.lr-sources summary { cursor: pointer; font-weight: 600; }
.lr-source-item { padding: 2px 0; }
.lr-tabs {
  display: flex;
  border-bottom: 1px solid var(--lr-border);
}
.lr-tab {
  flex: 1;
  padding: 8px;
  text-align: center;
  cursor: pointer;
  font-size: 13px;
  font-weight: 500;
  color: var(--lr-muted);
  border: none;
  background: none;
}
.lr-tab.active {
  color: var(--lr-primary);
  border-bottom: 2px solid var(--lr-primary);
}
.lr-badge {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 4px 8px;
  border-top: 1px solid var(--lr-border);
  font-size: 10px;
  color: var(--lr-muted);
  gap: 4px;
}
.lr-badge a { color: var(--lr-muted); text-decoration: none; }
.lr-badge a:hover { color: var(--lr-primary); }
.lr-spinner {
  display: inline-block;
  width: 14px; height: 14px;
  border: 2px solid var(--lr-border);
  border-top-color: var(--lr-primary);
  border-radius: 50%;
  animation: lr-spin 0.6s linear infinite;
}
@keyframes lr-spin { to { transform: rotate(360deg); } }
.lr-empty {
  text-align: center;
  padding: 24px;
  color: var(--lr-muted);
  font-size: 13px;
}
`;

  const SEARCH_ICON = `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/></svg>`;

  class LucidRagWidget extends HTMLElement {
    constructor() {
      super();
      this.attachShadow({ mode: "open" });
      this._debounceTimer = null;
      this._conversationId = null;
      this._messages = [];
      this._activeTab = MODE === "chat" ? "chat" : "search";
      this._autocompleteIndex = -1;
      this._suggestions = [];
    }

    connectedCallback() {
      this.setAttribute("data-theme", THEME);
      this._render();
      this._bindEvents();
    }

    _render() {
      const showTabs = MODE === "both";
      this.shadowRoot.innerHTML = `
        <style>${STYLES}</style>
        <div class="lr-container ${POSITION === "floating" ? "floating" : ""}">
          ${showTabs ? `
          <div class="lr-tabs">
            <button class="lr-tab ${this._activeTab === "search" ? "active" : ""}" data-tab="search">Search</button>
            <button class="lr-tab ${this._activeTab === "chat" ? "active" : ""}" data-tab="chat">Chat</button>
          </div>` : ""}
          <div class="lr-search-box">
            ${SEARCH_ICON}
            <input type="text" placeholder="${PLACEHOLDER}" aria-label="Search" autocomplete="off" />
            <span class="lr-loading" style="display:none"><span class="lr-spinner"></span></span>
          </div>
          <div class="lr-autocomplete" style="display:none"></div>
          <div class="lr-chat-panel" style="display:${this._activeTab === "chat" ? "block" : "none"}"></div>
          <div class="lr-results" style="display:${this._activeTab === "search" ? "block" : "none"}"></div>
          <div class="lr-badge"><a href="https://lucidrag.com" target="_blank" rel="noopener">Powered by LucidRAG</a></div>
        </div>`;
    }

    _bindEvents() {
      const input = this.shadowRoot.querySelector("input");
      const autocomplete = this.shadowRoot.querySelector(".lr-autocomplete");

      input.addEventListener("input", () => {
        clearTimeout(this._debounceTimer);
        this._debounceTimer = setTimeout(() => {
          const q = input.value.trim();
          if (q.length >= 2) this._fetchAutocomplete(q);
          else this._hideAutocomplete();
        }, 200);
      });

      input.addEventListener("keydown", (e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          this._hideAutocomplete();
          const q = input.value.trim();
          if (q) {
            if (this._activeTab === "chat") this._sendChat(q);
            else this._doSearch(q);
          }
        } else if (e.key === "ArrowDown" || e.key === "ArrowUp") {
          e.preventDefault();
          this._navigateAutocomplete(e.key === "ArrowDown" ? 1 : -1);
        } else if (e.key === "Escape") {
          this._hideAutocomplete();
        }
      });

      autocomplete.addEventListener("click", (e) => {
        const item = e.target.closest(".lr-autocomplete-item");
        if (item) {
          input.value = item.textContent;
          this._hideAutocomplete();
          if (this._activeTab === "chat") this._sendChat(item.textContent);
          else this._doSearch(item.textContent);
        }
      });

      // Tab switching
      this.shadowRoot.querySelectorAll(".lr-tab").forEach((tab) => {
        tab.addEventListener("click", () => {
          this._activeTab = tab.dataset.tab;
          this.shadowRoot.querySelectorAll(".lr-tab").forEach((t) => t.classList.remove("active"));
          tab.classList.add("active");
          const results = this.shadowRoot.querySelector(".lr-results");
          const chat = this.shadowRoot.querySelector(".lr-chat-panel");
          results.style.display = this._activeTab === "search" ? "block" : "none";
          chat.style.display = this._activeTab === "chat" ? "block" : "none";
        });
      });
    }

    async _fetchAutocomplete(q) {
      try {
        const res = await fetch(`${API.autocomplete}?q=${encodeURIComponent(q)}&limit=5&api_key=${encodeURIComponent(API_KEY)}`);
        if (!res.ok) return;
        const data = await res.json();
        this._suggestions = data.suggestions || [];
        this._autocompleteIndex = -1;
        if (this._suggestions.length === 0) {
          this._hideAutocomplete();
          return;
        }
        const ac = this.shadowRoot.querySelector(".lr-autocomplete");
        ac.innerHTML = this._suggestions
          .map((s, i) => `<div class="lr-autocomplete-item" data-index="${i}">${this._esc(s)}</div>`)
          .join("");
        ac.style.display = "block";
      } catch { /* ignore */ }
    }

    _navigateAutocomplete(dir) {
      const items = this.shadowRoot.querySelectorAll(".lr-autocomplete-item");
      if (items.length === 0) return;
      this._autocompleteIndex = Math.max(-1, Math.min(items.length - 1, this._autocompleteIndex + dir));
      items.forEach((el, i) => el.classList.toggle("active", i === this._autocompleteIndex));
      if (this._autocompleteIndex >= 0) {
        this.shadowRoot.querySelector("input").value = this._suggestions[this._autocompleteIndex];
      }
    }

    _hideAutocomplete() {
      this.shadowRoot.querySelector(".lr-autocomplete").style.display = "none";
      this._autocompleteIndex = -1;
    }

    async _doSearch(query) {
      this._setLoading(true);
      const results = this.shadowRoot.querySelector(".lr-results");
      try {
        const res = await fetch(API.search, {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-Api-Key": API_KEY },
          body: JSON.stringify({ query }),
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        if (!data.results || data.results.length === 0) {
          results.innerHTML = `<div class="lr-empty">No results found</div>`;
        } else {
          results.innerHTML = data.results
            .map(
              (r) => `
            <div class="lr-result">
              <div class="lr-result-title">${this._esc(r.title)}</div>
              <div class="lr-result-text">${this._esc(r.text)}</div>
              ${r.pageOrSection ? `<div class="lr-result-section">${this._esc(r.pageOrSection)}</div>` : ""}
            </div>`
            )
            .join("");
        }
      } catch (err) {
        results.innerHTML = `<div class="lr-empty">Search failed: ${this._esc(err.message)}</div>`;
      }
      this._setLoading(false);
    }

    async _sendChat(query) {
      const chat = this.shadowRoot.querySelector(".lr-chat-panel");
      const input = this.shadowRoot.querySelector("input");
      input.value = "";

      // Add user message
      this._messages.push({ role: "user", content: query });
      this._renderChat();
      this._setLoading(true);

      try {
        const res = await fetch(API.chat, {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-Api-Key": API_KEY },
          body: JSON.stringify({ query, conversationId: this._conversationId }),
        });

        if (!res.ok) throw new Error(`HTTP ${res.status}`);

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let assistantMsg = "";
        let sources = [];
        let buffer = "";

        this._messages.push({ role: "assistant", content: "", sources: [] });

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split("\n");
          buffer = lines.pop() || "";

          for (const line of lines) {
            if (!line.startsWith("data: ") || line === "data: [DONE]") continue;
            try {
              const chunk = JSON.parse(line.slice(6));
              if (chunk.conversationId) this._conversationId = chunk.conversationId;
              if (chunk.type === "sources" && chunk.sources) {
                sources = chunk.sources;
              } else if (chunk.type === "text" && chunk.text) {
                assistantMsg += chunk.text;
              }
              this._messages[this._messages.length - 1].content = assistantMsg;
              this._messages[this._messages.length - 1].sources = sources;
              this._renderChat();
            } catch { /* skip malformed chunks */ }
          }
        }
      } catch (err) {
        this._messages.push({ role: "assistant", content: `Error: ${err.message}`, sources: [] });
        this._renderChat();
      }

      this._setLoading(false);
    }

    _renderChat() {
      const chat = this.shadowRoot.querySelector(".lr-chat-panel");
      chat.innerHTML = this._messages
        .map((m) => {
          let html = `<div class="lr-chat-msg ${m.role}">${this._esc(m.content)}</div>`;
          if (m.sources && m.sources.length > 0) {
            html += `<div class="lr-sources"><details><summary>Sources (${m.sources.length})</summary>`;
            html += m.sources.map((s) => `<div class="lr-source-item">${this._esc(s.documentName)}</div>`).join("");
            html += `</details></div>`;
          }
          return html;
        })
        .join("");
      chat.scrollTop = chat.scrollHeight;
    }

    _setLoading(on) {
      const el = this.shadowRoot.querySelector(".lr-loading");
      if (el) el.style.display = on ? "inline" : "none";
    }

    _esc(str) {
      if (!str) return "";
      const d = document.createElement("div");
      d.textContent = str;
      return d.innerHTML;
    }
  }

  // Register web component
  if (!customElements.get("lucidrag-search")) {
    customElements.define("lucidrag-search", LucidRagWidget);
  }

  // Auto-init: find the target container and insert the widget
  const container = document.getElementById("lucidrag-search");
  if (container) {
    const widget = document.createElement("lucidrag-search");
    container.appendChild(widget);
  }
})();
