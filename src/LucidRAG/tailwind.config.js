/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './Views/**/*.cshtml',
    './wwwroot/**/*.html',
    './wwwroot/**/*.js'
  ],
  theme: {
    extend: {
      fontFamily: {
        'brand': ['Raleway', 'sans-serif']
      }
    }
  },
  plugins: [
    require('daisyui')
  ],
  daisyui: {
    themes: ['light', 'dark'],
    darkTheme: 'dark'
  }
}
