// Public App - Alpine.js component for public chat interface
// This module exports the publicApp function used by Alpine.js

export function publicApp(tenantName) {
    return {
        tenantName: tenantName || 'LucidRAG',
        sidebarOpen: false,
        loading: true,
        collections: [],
        selectedCollection: null,
        communities: [],
        selectedCommunity: null,
        stats: { totalDocuments: 0, totalEntities: 0 },
        theme: localStorage.getItem('lucidrag-theme') || 'light',
        conversationId: null,
        messages: [],
        currentMessage: '',
        isTyping: false,

        // Autocomplete
        autocompleteSuggestions: [],
        autocompleteVisible: false,
        autocompleteSelectedIndex: -1,

        // Search preferences
        searchMode: localStorage.getItem('lucidrag-search-mode') || 'full', // 'full', 'simple', 'vector'
        advancedSearchOpen: false,
        advancedFilters: {
            dateFrom: null,
            dateTo: null,
            signals: []
        },

        async init() {
            // Apply theme
            document.documentElement.setAttribute('data-theme', this.theme);

            // Load collections and stats
            await Promise.all([
                this.loadCollections(),
                this.loadStats()
            ]);

            // Check URL for collection slug
            this.checkUrlForCollection();

            this.loading = false;
        },

        async loadCommunities() {
            if (!this.selectedCollection) return;

            try {
                const response = await fetch(`/api/public/communities?collectionId=${this.selectedCollection.id}`);
                if (response.ok) {
                    this.communities = await response.json();
                }
            } catch (e) {
                console.error('Failed to load communities:', e);
            }
        },

        async loadCollections() {
            try {
                const response = await fetch('/api/public/collections?pageSize=100');
                const data = await response.json();
                this.collections = data.data || [];

                // Auto-select if only one collection
                if (this.collections.length === 1 && !this.selectedCollection) {
                    this.selectCollection(this.collections[0]);
                }
            } catch (e) {
                console.error('Failed to load collections:', e);
            }
        },

        async loadStats() {
            try {
                const response = await fetch('/api/public/stats');
                this.stats = await response.json();
            } catch (e) {
                console.error('Failed to load stats:', e);
            }
        },

        checkUrlForCollection() {
            // Parse /collection/{slug} from URL
            const match = window.location.pathname.match(/\/collection\/([^\/]+)/);
            if (match) {
                const slug = decodeURIComponent(match[1]);
                const collection = this.collections.find(c =>
                    c.name.toLowerCase().replace(/\s+/g, '-') === slug.toLowerCase() ||
                    c.id === slug
                );
                if (collection) {
                    this.selectCollection(collection);
                }
            }
        },

        async selectCollection(collection) {
            this.selectedCollection = collection;
            this.messages = [];
            this.conversationId = null;
            this.selectedCommunity = null;
            this.communities = [];
            this.sidebarOpen = false;

            // Load communities for this collection
            await this.loadCommunities();

            // Update URL
            const slug = collection.name.toLowerCase().replace(/\s+/g, '-');
            history.pushState({}, '', `/collection/${slug}`);
        },

        selectCommunity(community) {
            this.selectedCommunity = community;
            this.messages = [];
            this.conversationId = null;
        },

        clearCommunity() {
            this.selectedCommunity = null;
            this.messages = [];
            this.conversationId = null;
        },

        clearCollection() {
            this.selectedCollection = null;
            this.selectedCommunity = null;
            this.communities = [];
            this.messages = [];
            this.conversationId = null;
            history.pushState({}, '', '/');
        },

        toggleTheme() {
            this.theme = this.theme === 'light' ? 'dark' : 'light';
            document.documentElement.setAttribute('data-theme', this.theme);
            localStorage.setItem('lucidrag-theme', this.theme);
        },

        async sendMessage() {
            if (!this.currentMessage.trim() || !this.selectedCollection) return;

            const userMessage = this.currentMessage.trim();
            this.currentMessage = '';

            // Add user message
            this.messages.push({ role: 'user', content: userMessage });
            this.isTyping = true;

            // Scroll to bottom to show user message
            this.$nextTick(() => {
                const container = document.getElementById('chat-messages');
                if (container) container.scrollTop = container.scrollHeight;
            });

            try {
                // Get CSRF token from meta tag
                const csrfToken = document.querySelector('meta[name="x-csrf-token"]')?.getAttribute('content');

                const response = await fetch('/api/public/chat', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-XSRF-TOKEN': csrfToken || ''
                    },
                    body: JSON.stringify({
                        message: userMessage,
                        conversationId: this.conversationId,
                        collectionId: this.selectedCollection.id,
                        communityId: this.selectedCommunity?.id,
                        searchMode: this.searchMode,
                        filters: this.advancedFilters
                    })
                });

                if (!response.ok) {
                    const errorData = await response.json().catch(() => ({ message: 'Unknown error' }));
                    console.error('Chat API error:', errorData);
                    throw new Error(errorData.message || `HTTP ${response.status}`);
                }

                const data = await response.json();
                this.conversationId = data.conversationId;
                this.messages.push({
                    role: 'assistant',
                    content: data.response,
                    sourceCount: data.sourceCount
                });
            } catch (e) {
                console.error('Chat error:', e);
                this.messages.push({
                    role: 'assistant',
                    content: `Sorry, I encountered an error: ${e.message}. Please try again.`,
                    error: true
                });
            } finally {
                this.isTyping = false;
                this.$nextTick(() => {
                    const container = document.getElementById('chat-messages');
                    if (container) container.scrollTop = container.scrollHeight;
                });
            }
        },

        // Autocomplete methods
        async fetchAutocomplete() {
            if (!this.currentMessage || this.currentMessage.length < 2 || !this.selectedCollection) {
                this.autocompleteVisible = false;
                this.autocompleteSuggestions = [];
                return;
            }

            try {
                const response = await fetch(
                    `/api/public/autocomplete?query=${encodeURIComponent(this.currentMessage)}&collectionId=${this.selectedCollection.id}&limit=5`
                );

                if (response.ok) {
                    const suggestions = await response.json();
                    this.autocompleteSuggestions = suggestions;
                    this.autocompleteVisible = suggestions.length > 0;
                    this.autocompleteSelectedIndex = -1;
                }
            } catch (e) {
                console.error('Autocomplete error:', e);
            }
        },

        selectSuggestion(suggestion) {
            this.currentMessage = suggestion.term;
            this.autocompleteVisible = false;
            this.autocompleteSuggestions = [];
            this.autocompleteSelectedIndex = -1;
        },

        handleAutocompleteKeydown(event) {
            if (!this.autocompleteVisible || this.autocompleteSuggestions.length === 0) {
                return;
            }

            // Arrow down
            if (event.key === 'ArrowDown') {
                event.preventDefault();
                this.autocompleteSelectedIndex = Math.min(
                    this.autocompleteSelectedIndex + 1,
                    this.autocompleteSuggestions.length - 1
                );
            }
            // Arrow up
            else if (event.key === 'ArrowUp') {
                event.preventDefault();
                this.autocompleteSelectedIndex = Math.max(
                    this.autocompleteSelectedIndex - 1,
                    -1
                );
            }
            // Enter
            else if (event.key === 'Enter' && this.autocompleteSelectedIndex >= 0) {
                event.preventDefault();
                this.selectSuggestion(this.autocompleteSuggestions[this.autocompleteSelectedIndex]);
            }
            // Escape
            else if (event.key === 'Escape') {
                this.autocompleteVisible = false;
                this.autocompleteSelectedIndex = -1;
            }
        },

        // Search preferences
        setSearchMode(mode) {
            this.searchMode = mode;
            localStorage.setItem('lucidrag-search-mode', mode);
        },

        clearAdvancedFilters() {
            this.advancedFilters = {
                dateFrom: null,
                dateTo: null,
                signals: []
            };
        },

        applyAdvancedFilters() {
            this.advancedSearchOpen = false;
            // Filters will be applied on next search/chat query
            console.log('Advanced filters applied:', this.advancedFilters);
        }
    };
}

// Export for global access (used by Alpine.js in templates)
window.publicApp = publicApp;
