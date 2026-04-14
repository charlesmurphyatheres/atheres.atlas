/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: {
        brand: {
          50: '#eff6ff',
          500: '#1a56db',
          600: '#1e40af',
          700: '#1d3a8a',
        },
      },
    },
  },
  plugins: [],
}
