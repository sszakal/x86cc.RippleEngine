/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./src/**/*.{html,ts}'],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        // TailAdmin-ish brand palette
        brand: {
          50: '#ecf3ff', 100: '#dde9ff', 500: '#465fff', 600: '#3641f5', 700: '#2a31d8'
        }
      },
      fontFamily: {
        sans: ['Outfit', 'ui-sans-serif', 'system-ui', 'sans-serif']
      }
    }
  },
  plugins: []
};
