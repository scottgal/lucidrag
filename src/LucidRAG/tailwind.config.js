/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './Views/**/*.cshtml',
    './wwwroot/**/*.html',
    './wwwroot/**/*.js',
    './src/js/**/*.js'
  ],
  theme: {
    extend: {
      fontFamily: {
        'brand': ['Raleway', 'sans-serif']
      }
    }
  },
  plugins: [
    require('daisyui'),
    require('@tailwindcss/forms'),
    require('@tailwindcss/typography')
  ],
  daisyui: {
    themes: ['light', 'dark'],
    darkTheme: 'dark'
  }
}
