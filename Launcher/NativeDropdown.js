(() => {
  if (location.origin !== 'http://wails.localhost' || window.__nativeAccounts) return;
  window.__nativeAccounts = true;
  const style = document.createElement('style');
  style.textContent = '.launch-game__profile-dropdown .hy-dropdown__list > :not([data-native-store]) { display:none !important; }';
  style.textContent += `
    html:not([data-account-ready]) .login__container, html[data-account-transition] .login__container { visibility:hidden !important; }
  `;
  (document.head ?? document.body).append(style);
  const url = new URL(document.currentScript.src);
  const capability = url.pathname.split('/')[1];
  let state, user, pending = false, signature = '', reported = '', loginSince = 0, loginOpened = '', loginCompleted = false;
  let logoutAttempt = '';
  let sessionAttempt = '', updating = false;
  let listening = false;
  let commandVersion = 0;
  let fastUpdate;
  function wake() {
    clearTimeout(fastUpdate);
    fastUpdate = setTimeout(update, 0);
  }
  async function request(path, body) {
    const response = await fetch(url.origin + '/' + path, {
      method: body ? 'POST' : 'GET', cache: 'no-store', signal: AbortSignal.timeout(6000),
      headers: { Authorization: 'Bearer ' + capability, ...(body ? { 'Content-Type': 'application/json' } : {}) },
      ...(body ? { body: JSON.stringify(body) } : {})
    });
    if (!response.ok) throw new Error('Account switch unavailable. Previous selection kept.');
    return body ? null : response.json();
  }
  async function command(action, store) {
    if (!state || ((state.busy || state.login) && action !== 'cancel') || pending) return;
    pending = true; commandVersion++;
    try { await request('command', { action, ...(store ? { store } : {}) }); }
    catch (error) { alert(error.message); }
    finally {
      // Discard polls started before this command; never allow Play against stale switch state.
      commandVersion++; state = null;
      try { state = await request('state'); } catch { /* Keep Play blocked while disconnected. */ }
      pending = false;
      wake();
    }
  }
  document.addEventListener('click', event => {
    if (event.target.closest('.launch-game__play-hytale .hybutton-split__main')) {
      const profile = user?.getUserProfile?.uuid;
      if (!state || pending || state.busy || state.holdLogin || state.logout || state.login ||
          state.active !== state.selected || !state.profile || profile !== state.profile) {
        event.preventDefault(); event.stopImmediatePropagation();
      }
      return;
    }
    const trigger = event.target.closest('.settings__action-button');
    if (!trigger || trigger.textContent.trim() !== 'Logout') return;
    event.preventDefault(); event.stopImmediatePropagation(); void command('logout', state?.active);
  }, true);
  function refresh() {
    const dropdown = document.querySelector('.launch-game__profile-dropdown');
    const list = dropdown?.querySelector('.hy-dropdown__list');
    if (!list || !state) return;
    // Reuse native classes and Vue scope attributes so the original styling applies.
    const current = JSON.stringify(state);
    if (signature === current && list.querySelector('[data-native-store]')) return;
    signature = current;
    const sample = list.querySelector('.hy-dropdown__item');
    const scopes = [...(sample?.attributes ?? list.attributes)].filter(a => a.name.startsWith('data-v-')).map(a => a.name);
    list.querySelectorAll('[data-native-store]').forEach(node => node.remove());
    function row(label, id, action) {
      const item = document.createElement('div');
      item.className = 'hy-dropdown__item'; item.dataset.nativeStore = id;
      scopes.forEach(name => item.setAttribute(name, ''));
      item.setAttribute('role', 'option'); item.setAttribute('aria-selected', String(id === state.active));
      const disabled = (state.busy || !!state.login) && action !== 'cancel';
      item.setAttribute('aria-disabled', String(disabled)); item.tabIndex = disabled ? -1 : 0;
      item.title = state.message;
      const text = document.createElement('span'); text.textContent = label; item.append(text);
      const activate = event => { event.preventDefault(); event.stopPropagation(); void command(action, id); };
      item.addEventListener('click', activate);
      item.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') activate(event); });
      list.append(item);
    }
    state.stores.filter(store => !store.signedOut).forEach(store => row(store.label, store.id, 'select'));
    row('Add account', '', 'add');
    // Verified Play retains its native handler; settings has its own independent control.
  }
  async function update() {
    if (updating) return;
    updating = true;
    try {
      const version = commandVersion;
      const next = await request('state');
      if (version !== commandVersion) return;
      state = next;
      if (!listening && window.runtime?.EventsOn) {
        for (const event of ['reload', 'profile_changed'])
          window.runtime.EventsOn(event, () => { if (state?.login) { loginCompleted = true; reported = ''; } });
        listening = true;
      }
      document.documentElement?.setAttribute('data-account-ready', '');
      document.documentElement?.toggleAttribute('data-account-transition', !!state.holdLogin);
      if (state.session) {
        if (sessionAttempt !== state.session.request) {
          const operation = state.session.operation, requestId = state.session.request;
          sessionAttempt = requestId;
          let success = false, profile = null, loggedIn = null;
          try {
            if (!['stop', 'load', 'run', 'clear'].includes(operation)) throw new Error('Invalid native operation.');
            // Signed-out UI refresh must not start authenticated background tasks.
            if (operation !== 'run' || await window.go.account.Service.IsUserLoggedIn())
              await window.go.account.Service.CheckNetworkMode(operation === 'clear', 'hya:' +
                (operation === 'run' ? 'run!' : operation === 'clear' ? 'stop' : operation));
            if (operation !== 'stop') {
              if (operation === 'load' && !await window.go.account.Service.HasValidSession()) throw new Error('Saved native session is signed out.');
              loggedIn = await window.go.account.Service.IsUserLoggedIn();
              if (loggedIn) profile = await window.go.user.Service.GetUserProfile();
              if (operation === 'run') {
                user ||= (await import(location.origin + '/assets/index-C4Qf_qVT.js')).S();
                await user.load();
                reported = '';
                window.runtime.EventsEmit('reload');
              }
            }
            success = true;
          } catch { /* The helper restores and verifies the previous native session. */ }
          await request('command', { action: 'session-result', request: requestId, success,
            profile: profile?.uuid ?? null, username: profile?.username ?? null, loggedIn });
        }
        return;
      }
      if (state.logout) {
        if (logoutAttempt !== state.logout) {
          logoutAttempt = state.logout;
          let success = false;
          try {
            await window.go.account.Service.LogoutUser();
            success = (await window.go.account.Service.IsUserLoggedIn()) === false;
          } catch { /* Report failure without deleting any store files. */ }
          await request('command', { action: 'logout-result', request: state.logout, success });
        }
        return;
      }
      if (state.login && loginOpened !== state.login) {
        loginOpened = state.login; loginCompleted = false;
        try {
          const authorization = new URL(await window.go.account.Service.GenerateOauthURL());
          if (authorization.protocol !== 'https:' || authorization.hostname !== 'oauth.accounts.hytale.com') throw new Error('Invalid native login URL.');
          authorization.searchParams.set('prompt', 'login');
          await window.runtime.BrowserOpenURL(authorization.href);
        } catch {
          await request('command', { action: 'login-failed' });
          return;
        }
      }
      if (state.login && location.pathname.startsWith('/error')) {
        await request('command', { action: 'login-failed' });
      }
      if (!user) user = (await import(location.origin + '/assets/index-C4Qf_qVT.js')).S();
      if (state.login && loginCompleted) await user.load();
      const profile = user.getUserProfile?.uuid;
      if (!profile && document.querySelector('.login__container')) loginSince ||= Date.now();
      else loginSince = 0;
      const identity = profile || 'signed-out';
      if ((!state.login || loginCompleted) && reported !== identity && (profile || (loginSince && Date.now() - loginSince >= 3000))) {
        await request('command', { action: 'ready', profile: profile ?? null, username: user.getUserProfile?.username ?? null,
          ...(state.login ? { loginRequest: state.login } : {}) }); reported = identity;
      }
      refresh();
    } catch { state = null; /* Block new Play until the selected native identity is verified again. */ }
    finally {
      updating = false;
      // Keep the idle poll quiet; advance native transitions without one-second gaps.
      if (pending || state?.busy || state?.session || state?.logout) {
        clearTimeout(fastUpdate);
        fastUpdate = setTimeout(update, 25);
      }
    }
  }
  new MutationObserver(refresh).observe(document.body, { childList: true, subtree: true });
  setInterval(update, 1000); void update();
})();
