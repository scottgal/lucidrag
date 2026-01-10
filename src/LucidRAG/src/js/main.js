// Main JavaScript entry point for LucidRAG
// Import CSS for Vite bundling
import '../css/main.css'

// Theme management
const ThemeManager = {
  STORAGE_KEY: 'lucidrag-theme',

  init() {
    const savedTheme = localStorage.getItem(this.STORAGE_KEY) || 'light'
    this.apply(savedTheme)
  },

  apply(theme) {
    document.documentElement.setAttribute('data-theme', theme)
    localStorage.setItem(this.STORAGE_KEY, theme)
  },

  toggle() {
    const current = document.documentElement.getAttribute('data-theme')
    this.apply(current === 'dark' ? 'light' : 'dark')
  }
}

// Export for global access
window.ThemeManager = ThemeManager

// Initialize on DOM ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => ThemeManager.init())
} else {
  ThemeManager.init()
}

// Utility functions
export function formatDate(date) {
  return new Intl.DateTimeFormat('en-US', {
    dateStyle: 'medium',
    timeStyle: 'short'
  }).format(new Date(date))
}

export function formatBytes(bytes) {
  if (bytes === 0) return '0 Bytes'
  const k = 1024
  const sizes = ['Bytes', 'KB', 'MB', 'GB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
}

// Toast notifications
export function showToast(message, type = 'info') {
  const toast = document.createElement('div')
  toast.className = `alert alert-${type} fixed bottom-4 right-4 z-50 max-w-sm shadow-lg`
  toast.innerHTML = `<span>${message}</span>`
  document.body.appendChild(toast)

  setTimeout(() => {
    toast.classList.add('opacity-0', 'transition-opacity')
    setTimeout(() => toast.remove(), 300)
  }, 3000)
}

window.showToast = showToast

console.log('LucidRAG initialized')
