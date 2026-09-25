(function () {
  try {
    var t = window.localStorage.getItem('cpm.theme');
    var dark = t === 'dark' || (t !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.classList.toggle('dark', dark);
    document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
  } catch (e) {
    /* storage unavailable: fall back to light until the app starts */
  }
})();
