const requestedTheme = new URLSearchParams(window.location.search).get('scoutTheme')
const theme =
  requestedTheme === 'light' || requestedTheme === 'dark'
    ? requestedTheme
    : window.matchMedia('(prefers-color-scheme: dark)').matches
      ? 'dark'
      : 'light'

document.documentElement.setAttribute('data-theme', theme)