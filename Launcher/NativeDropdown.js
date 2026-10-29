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
  let state, user, pending = false, signature = '', reported = '', loginSince = 0, loginOpened = '', loginCompleted = '';
  let logoutResult, sessionResult, sessionReloaded, updating = false;
  let nativeWork, userLoad, uncertain = false;
  let signInRetryAt = 0, signInRetryDelay = 250;
  const timeout = Symbol('timeout'), withdrawn = Symbol('withdrawn');
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
  async function bounded(work, deadline) {
    let timer;
    try {
      return await Promise.race([work, new Promise((_, reject) => {
        timer = setTimeout(() => { uncertain = true; reject(timeout); }, Math.max(0, deadline - Date.now()));
      })]);
    } finally { clearTimeout(timer); }
  }
  async function invoke(action, current, deadline, loading = false) {
    if (!current()) throw withdrawn;
    if (Date.now() >= deadline) { uncertain = true; throw timeout; }
    const previous = loading ? userLoad : nativeWork;
    if (previous) {
      try { await bounded(previous, deadline); }
      catch (error) { if (error === timeout) throw error; }
    }
    if (!current()) throw withdrawn;
    if (Date.now() >= deadline) { uncertain = true; throw timeout; }
    // A deadline cannot cancel native work or a Pinia load; retain ownership until it actually settles.
    if (loading) uncertain = true;
    const work = Promise.resolve().then(action);
    if (loading) userLoad = work; else nativeWork = work;
    const release = () => {
      if (loading) { if (userLoad === work) userLoad = null; }
      else if (nativeWork === work) nativeWork = null;
      if (!updating) wake();
    };
    work.then(release, release);
    const result = await bounded(work, deadline);
    if (!current()) throw withdrawn;
    return result;
  }
  async function command(action, store) {
    if (!state || ((state.busy || state.login) && action !== 'cancel') || pending) return;
    const loginRequest = state.login;
    pending = action; commandVersion++;
    if (action === 'cancel') { loginCompleted = ''; reported = ''; }
    try { await request('command', { action, ...(store ? { store } : {}), ...(action === 'cancel' ? { loginRequest } : {}) }); }
    catch (error) { alert(error.message); }
    finally {
      // Discard polls started before this command; never allow Play against stale switch state.
      commandVersion++; state = null;
      try { state = await request('state'); } catch { /* Keep Play blocked while disconnected. */ }
      pending = false;
      refresh();
      wake();
    }
  }
  document.addEventListener('click', event => {
    if (event.target.closest('.launch-game__play-hytale .hybutton-split__main')) {
      const profile = user?.getUserProfile?.uuid;
      if (!state || pending || uncertain || nativeWork || userLoad || state.busy || state.holdLogin || state.logout || state.login ||
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
    row(state.login ? 'Cancel login' : 'Add account', '', state.login ? 'cancel' : 'add');
    // Verified Play retains its native handler; settings has its own independent control.
  }
  async function update() {
    if (updating) return;
    updating = true;
    const version = commandVersion;
    try {
      const next = await request('state');
      if (version !== commandVersion) return;
      state = next;
      if (!state.signInRecovery) { signInRetryAt = 0; signInRetryDelay = 250; }
      if (sessionResult?.request !== state.session?.request) sessionResult = null;
      if (logoutResult?.request !== state.logout) logoutResult = null;
      const loginRequest = state.login;
      const sessionRequest = state.session?.request, logoutRequest = state.logout;
      const current = () => version === commandVersion && state?.login === loginRequest &&
        state?.session?.request === sessionRequest && state?.logout === logoutRequest;
      const confirmCurrent = async () => {
        const latest = await request('state');
        if (version !== commandVersion) return false;
        state = latest;
        return current();
      };
      // Leave time for delivery and helper recovery before its 30-second acknowledgement deadline.
      const deadline = Date.now() + 6000;
      const native = action => invoke(action, current, deadline);
      const loadUser = () => invoke(() => user.load(), current, deadline, true);
      if (!listening && window.runtime?.EventsOn) {
        for (const event of ['reload', 'profile_changed'])
          window.runtime.EventsOn(event, () => {
            if (pending !== 'cancel' && state?.login && loginOpened === state.login) { loginCompleted = loginOpened; reported = ''; }
          });
        listening = true;
      }
      document.documentElement?.setAttribute('data-account-ready', '');
      document.documentElement?.toggleAttribute('data-account-transition', !!state.holdLogin);
      refresh();
      if (!user) user = document.querySelector('#app')?.__vue_app__?.config.globalProperties.$pinia?._s.get('user');
      if (state.session) {
        if (!sessionResult) {
          const operation = state.session.operation, requestId = state.session.request;
          if (operation === 'run' && !user) return;
          let success = false, profile = null, loggedIn = null;
          try {
            if (!['stop', 'load', 'run', 'clear'].includes(operation)) throw new Error('Invalid native operation.');
            // Signed-out UI refresh must not start authenticated background tasks.
            if (operation !== 'run' || await native(() => window.go.account.Service.IsUserLoggedIn()))
              await native(() => window.go.account.Service.CheckNetworkMode(operation === 'clear', 'hya:' +
                (operation === 'run' ? 'run!' : operation === 'clear' ? 'stop' : operation)));
            if (operation !== 'stop') {
              if (operation === 'load' && !await native(() => window.go.account.Service.HasValidSession())) throw new Error('Saved native session is signed out.');
              loggedIn = await native(() => window.go.account.Service.IsUserLoggedIn());
              if (loggedIn) profile = await native(() => window.go.user.Service.GetUserProfile());
              if (operation === 'run') {
                await loadUser();
              }
            }
            success = true;
          } catch (error) {
            if (error === withdrawn) return;
            // Unknown completion must not masquerade as a definite native failure.
            if (error === timeout) success = null;
          }
          sessionResult = { action: 'session-result', request: requestId, success,
            profile: profile?.uuid ?? null, username: profile?.username ?? null, loggedIn };
        }
        if (!await confirmCurrent()) return;
        if (state.session.operation === 'run' && sessionResult.success && sessionReloaded !== sessionRequest) {
          uncertain = false; reported = ''; sessionReloaded = sessionRequest;
          window.runtime.EventsEmit('reload');
        }
        await request('command', sessionResult);
        return;
      }
      if (state.logout) {
        if (!logoutResult) {
          logoutResult = { action: 'logout-result', request: state.logout, success: null };
          try {
            await native(() => window.go.account.Service.LogoutUser());
          } catch (error) {
            if (error === withdrawn) return;
            if (error !== timeout) logoutResult.success = false;
          }
        }
        if (logoutResult.success === null) {
          try {
            if (await native(() => window.go.account.Service.IsUserLoggedIn()) === false) logoutResult.success = true;
          } catch { /* Retry confirmation, never an already completed logout. */ }
        }
        if (await confirmCurrent()) await request('command', logoutResult);
        return;
      }
      if (state.signInRecovery && !state.busy) {
        if (!nativeWork && !userLoad && Date.now() >= signInRetryAt) {
          signInRetryAt = Date.now() + signInRetryDelay;
          signInRetryDelay = Math.min(signInRetryDelay * 2, 30000);
          await request('command', { action: 'retry-signin', request: state.signInRecovery });
          wake();
        }
        return;
      }
      if (loginRequest && loginOpened !== loginRequest) {
        loginCompleted = '';
        try {
          const authorization = new URL(await native(() => window.go.account.Service.GenerateOauthURL()));
          if (!await confirmCurrent()) return;
          if (authorization.protocol !== 'https:' || authorization.hostname !== 'oauth.accounts.hytale.com') throw new Error('Invalid native login URL.');
          authorization.searchParams.set('prompt', 'login');
          loginOpened = loginRequest;
          await native(() => window.runtime.BrowserOpenURL(authorization.href));
        } catch {
          if (current()) { await request('command', { action: 'login-failed', loginRequest }); wake(); }
          return;
        }
      }
      if (!current()) return;
      if (loginRequest && location.pathname.startsWith('/error')) {
        await request('command', { action: 'login-failed', loginRequest });
        wake();
        return;
      }
      if (!user) return;
      if (loginRequest && loginCompleted === loginRequest) {
        await loadUser();
        if (!await confirmCurrent()) return;
        uncertain = false;
      }
      if (!current()) return;
      if (uncertain || nativeWork || userLoad) return;
      const profile = user.getUserProfile?.uuid;
      if (!profile && document.querySelector('.login__container')) loginSince ||= Date.now();
      else loginSince = 0;
      const identity = profile || 'signed-out';
      if ((!loginRequest || loginCompleted === loginRequest) && reported !== identity && (profile || (loginSince && Date.now() - loginSince >= 3000))) {
        await request('command', { action: 'ready', profile: profile ?? null, username: user.getUserProfile?.username ?? null,
          ...(loginRequest ? { loginRequest } : {}) });
        if (current()) reported = identity;
        wake();
      }
      refresh();
    } catch { if (version === commandVersion) state = null; /* Block Play without discarding a newer command's state. */ }
    finally {
      updating = false;
      // Keep the idle poll quiet; advance native transitions without one-second gaps.
      if (pending || state?.busy || state?.session || state?.logout || sessionResult || logoutResult) {
        clearTimeout(fastUpdate);
        fastUpdate = setTimeout(update, 25);
      } else if (state?.signInRecovery && !nativeWork && !userLoad) {
        clearTimeout(fastUpdate);
        fastUpdate = setTimeout(update, Math.max(25, signInRetryAt - Date.now()));
      }
    }
  }
  new MutationObserver(refresh).observe(document.body, { childList: true, subtree: true });
  setInterval(update, 1000); void update();
})();
