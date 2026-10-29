const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
let tick, nativeLogouts = 0, generated = 0, requestId = 0, failLogin = false, userLoads = 0;
const calls = [], created = [], browserUrls = [], events = {}, nativeActions = [];
const deliveryFailures = new Map(), delivered = [];
const timers = new Map();
let timerId = 0, automaticSwitch = false, automaticSignIn = false, signInAttempts = 0, disconnected = false, loadGate, urlGate, commandGate, stateGate, refresh;
let nativeGate, nativeFailure, logoutGate, verificationGate, now = Date.now(), completed = false, reloads = 0;
process.on('beforeExit', () => {
  if (!completed && !process.exitCode) {
    console.error('FAIL frontend checks did not finish (an unresolved promise cannot silently pass)');
    process.exitCode = 1;
  }
});
function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}
async function expireWait() {
  await new Promise(setImmediate);
  const waiting = [...timers].filter(([, task]) => task.delay > 25);
  assert.equal(waiting.length, 1, 'stalled work has exactly one bounded wait');
  const [id, task] = waiting[0];
  assert(task.delay <= 6000, 'frontend deadline precedes the helper acknowledgement deadline');
  timers.delete(id); now += task.delay; task.fn();
  await new Promise(setImmediate);
}
const accountB = { uuid: 'accountB-profile', username: 'accountB' };
const accountA = { uuid: 'accountA-profile', username: 'accountA' };
let nativeProfile = accountB;
let logoutFailure = false, verificationFailures = 0, verifications = 0;
const user = { getUserProfile: accountB, async load() { userLoads++; const profile = nativeProfile; await loadGate; this.getUserProfile = profile; } };
const state = { stores: [{ id: 'accountB', label: 'accountB' }, { id: 'accountA', label: 'accountA' },
  { id: 'signed-out', label: 'Signed out', signedOut: true }], active: 'accountB', selected: 'accountB', profile: accountB.uuid, busy: false, holdLogin: false, login: null };
const window = {
  go: { account: { Service: {
    GenerateOauthURL: async () => { generated++; await urlGate; if (failLogin) throw Error('native login failed'); return 'https://oauth.accounts.hytale.com/oauth2/auth?state=native-state&code_challenge=native-pkce'; },
    LogoutUser: async () => { nativeLogouts++; await logoutGate; if (logoutFailure) throw Error('native logout failed'); nativeProfile = null; },
    IsUserLoggedIn: async () => { verifications++; await verificationGate; if (verificationFailures > 0) { verificationFailures--; throw Error('verification unavailable'); } return !!nativeProfile; },
    HasValidSession: async () => true,
    CheckNetworkMode: async (automatic, source) => {
      nativeActions.push(source);
      await nativeGate;
      if (source === nativeFailure) throw Error('native recovery failed');
      if (automatic) { assert.equal(source, 'hya:stop'); nativeProfile = null; }
      if (automaticSwitch && source === 'hya:load') nativeProfile = accountA;
    }
  } }, user: { Service: { GetUserProfile: async () => nativeProfile } } },
  runtime: { BrowserOpenURL: url => browserUrls.push(url), EventsOn: (name, fn) => { events[name] = fn; }, EventsEmit: name => { reloads++; events[name]?.(); } }
};
// shortcut: only the selectors/events used here are modeled; extend when production needs more DOM APIs.
function element(className = '') {
  return { className, dataset: {}, children: [], handlers: {}, attrs: {},
    get attributes() { return Object.keys(this.attrs).map(name => ({ name })); },
    append(child) { child.parent = this; this.children.push(child); },
    setAttribute(name, value) { this.attrs[name] = value; },
    toggleAttribute(name, enabled) { if (enabled) this.attrs[name] = ''; else delete this.attrs[name]; },
    addEventListener(name, fn, capture = false) { this.handlers[name] = { fn, capture }; },
    remove() { this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; },
    matches(selector) {
      if (selector === '[data-native-store]') return Object.hasOwn(this.dataset, 'nativeStore');
      if (selector.startsWith('#')) return this.id === selector.slice(1);
      assert(selector.startsWith('.') && !selector.includes(' '), 'unsupported fixture selector: ' + selector);
      return this.className.split(' ').includes(selector.slice(1));
    },
    querySelectorAll(selector) {
      return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
    },
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; },
    closest(selector) {
      const parts = selector.split(' '), last = parts.pop();
      for (let node = this; node; node = node.parent) {
        if (node.matches(last) && (!parts.length || node.parent?.closest(parts.join(' ')))) return node;
      }
      return null;
    }
  };
}
const document = element(), body = element(), login = element('login__container'), app = element();
document.documentElement = element(); document.append(document.documentElement);
document.body = body; document.documentElement.append(body); body.append(login); body.append(app);
app.id = 'app'; app.__vue_app__ = { config: { globalProperties: { $pinia: { _s: new Map() } } } };
const nativeScript = element(); nativeScript.src = 'http://wails.localhost/assets/index-renamed.js'; body.append(nativeScript);
document.currentScript = { src: 'http://127.0.0.1:1234/cap/ui.js' };
document.createElement = tag => { created.push(tag); return element(); };
const dropdown = element('launch-game__profile-dropdown'), list = element('hy-dropdown__list');
const nativeRow = element('hy-dropdown__item'); nativeRow.setAttribute('data-v-native', ''); list.append(nativeRow); dropdown.append(list);
const play = element('launch-game__play-hytale'), playButton = element('hybutton-split__main'), playLabel = element();
play.append(playButton); playButton.append(playLabel);
let nativePlays = 0;
playButton.addEventListener('click', () => { nativePlays++; });
function dispatch(target, type = 'click', key) {
  const event = { target, key, defaultPrevented: false, stopped: false,
    preventDefault() { this.defaultPrevented = true; },
    stopPropagation() { this.stopped = true; }, stopImmediatePropagation() { this.stopped = true; } };
  const capture = document.handlers[type];
  if (capture) { assert.equal(capture.capture, true, 'Play protection must run before native handlers'); capture.fn(event); }
  for (let node = target; node && !event.stopped; node = node.parent) {
    if (!node.handlers[type]?.capture) node.handlers[type]?.fn(event);
  }
  return event;
}
const rows = () => list.querySelectorAll('[data-native-store]');
const actionRow = () => rows().at(-1);
async function command(action, store) {
  const row = ['add', 'cancel'].includes(action) ? actionRow() : rows().find(row => row.dataset.nativeStore === store);
  assert(row, 'account action must have a rendered row');
  dispatch(row.children[0]);
  await new Promise(setImmediate);
}
const playBlocked = () => {
  const before = nativePlays, event = dispatch(playLabel);
  assert.equal(event.stopped, event.defaultPrevented, 'blocked Play stops native propagation too');
  assert.equal(nativePlays - before, event.defaultPrevented ? 0 : 1, 'verified Play retains the native handler');
  return event.defaultPrevented;
};
const source = fs.readFileSync('Launcher/NativeDropdown.js', 'utf8');
state.session = { request: 'startup-run', operation: 'run' }; state.busy = true;
user.getUserProfile = null;
vm.runInNewContext(source, {
  location: { origin: 'http://wails.localhost', pathname: '/launch-game' }, window, document, URL, AbortSignal,
  Date: class extends Date { static now() { return now; } },
  MutationObserver: class { constructor(fn) { refresh = fn; } observe() {} }, setInterval: fn => { tick = fn; },
  setTimeout: (fn, delay) => { timers.set(++timerId, { fn, delay }); return timerId; },
  clearTimeout: id => timers.delete(id),
  alert: message => { throw Error(message); },
  fetch: async (url, options) => {
    if (options.body) {
      const data = JSON.parse(options.body); calls.push(data); await commandGate;
      const failureKey = data.request ?? data.loginRequest;
      if (deliveryFailures.get(failureKey)) {
        deliveryFailures.set(failureKey, deliveryFailures.get(failureKey) - 1);
        throw Error('command POST failed before delivery');
      }
      delivered.push(data);
      if (data.action === 'add') state.login = 'login-' + ++requestId;
      if (['cancel', 'login-failed'].includes(data.action) && data.loginRequest === state.login) state.login = null;
      if (automaticSwitch && data.action === 'select') {
        state.busy = true; state.session = { request: 'fast-stop', operation: 'stop' };
      }
      if (automaticSwitch && data.action === 'session-result') {
        const stages = ['stop', 'load', 'run'];
        const next = stages[stages.indexOf(state.session.operation) + 1];
        state.session = next ? { request: 'fast-' + next, operation: next } : null;
        if (!next) {
          state.busy = false; state.active = state.selected = 'accountA'; state.profile = accountA.uuid;
        }
      }
      if (automaticSignIn && data.action === 'retry-signin') {
        assert.equal(data.request, state.signInRecovery, 'automatic sign-in retry uses the current helper request');
        assert(!state.busy, 'automatic sign-in retries cannot overlap');
        state.busy = true; state.signInRecovery = 'sign-in-retry-' + ++signInAttempts;
        state.session = { request: state.signInRecovery + '-stop', operation: 'stop' };
      }
      if (automaticSignIn && data.action === 'session-result') {
        assert.equal(data.request, state.session.request);
        const stages = ['stop', 'clear', 'run'];
        const next = data.success === true && stages[stages.indexOf(state.session.operation) + 1];
        if (next) state.session = { request: state.signInRecovery + '-' + next, operation: next };
        else {
          state.session = null; state.busy = false;
          if (data.success === true && data.loggedIn === false) {
            state.signInRecovery = null; state.holdLogin = false; state.active = 'pending-sign-in';
          }
        }
      }
      if (data.action === 'ready' && data.loginRequest === state.login) {
        state.login = null; state.active = state.selected = data.username; state.profile = data.profile;
      }
    }
    if (!options.body && disconnected) throw Error('helper disconnected');
    return { ok: true, json: async () => {
      const next = structuredClone(state), gate = stateGate;
      stateGate = null;
      await gate;
      return next;
    } };
  }
}, { importModuleDynamically: async asset => { throw Error('Unavailable frontend import: ' + asset + '; mounted asset: ' + nativeScript.src); } });
(async () => {
  await new Promise(setImmediate); // Allow the initial asynchronous poll to finish.
  assert.equal(calls.length, 0, 'run received first waits for Pinia without acknowledging a result');
  assert.equal(nativeActions.length, 0, 'waiting for Pinia does not start native tasks');
  assert(playBlocked(), 'Play stays blocked while the user store is unavailable');
  const waiting = [...timers.entries()].at(-1);
  assert(waiting && waiting[1].delay <= 25, 'unready store retries without a one-second transition delay');
  app.__vue_app__.config.globalProperties.$pinia._s.set('user', user);
  deliveryFailures.set('startup-run', 1);
  timers.delete(waiting[0]); await waiting[1].fn();
  const recovered = calls.at(-1);
  assert.deepEqual(recovered, { action: 'session-result', request: 'startup-run', success: true,
    profile: accountB.uuid, username: accountB.username, loggedIn: true }, 'first run recovers from the mounted store with renamed frontend assets');
  assert.equal(user.getUserProfile, accountB, 'recovery loads the native profile into the existing frontend');
  assert.equal(delivered.length, 0, 'failed recovery response has not reached the helper');
  assert(playBlocked(), 'lost recovery response keeps Play blocked');
  const retry = [...timers.entries()].at(-1);
  assert(retry && retry[1].delay <= 25, 'recovery response retries immediately');
  timers.delete(retry[0]); await retry[1].fn();
  assert.deepEqual(delivered.at(-1), recovered, 'recovery retries the original result');
  assert.deepEqual(nativeActions, ['hya:run!'], 'first run starts native tasks exactly once across retries');
  assert.equal(userLoads, 1, 'first run reloads the mounted store exactly once across retries');
  assert.equal(nativeLogouts, 0); assert.equal(browserUrls.length, 0);
  state.session = null;
  await tick();
  assert.equal(calls.at(-1).action, 'ready', 'initial native startup can report readiness while the helper is busy');
  user.getUserProfile = null;
  await tick(); now += 3000; await tick();
  assert.equal(calls.at(-1).profile, null, 'signed-out startup can confirm the native sign-in screen while busy');
  user.getUserProfile = accountB; state.busy = false;
  calls.length = delivered.length = nativeActions.length = 0;
  await tick();
  assert.deepEqual(created, ['style'], 'native sign-in receives no account controls');
  assert.equal(document.querySelector('.launch-game__profile-dropdown'), null);
  assert.equal(login.children.length, 0, 'native sign-in receives no addon children');
  login.remove(); body.append(dropdown); body.append(play); refresh();
  assert.deepEqual(rows().map(row => [row.dataset.nativeStore, row.children[0].textContent]),
    [['accountB', 'accountB'], ['accountA', 'accountA'], ['', 'Add account']], 'only usable saved accounts and Add render');
  for (const row of rows()) {
    assert.equal(row.className, 'hy-dropdown__item'); assert.equal(row.attrs['data-v-native'], '');
    assert.equal(row.attrs.role, 'option'); assert.equal(row.attrs['aria-disabled'], 'false'); assert.equal(row.tabIndex, 0);
    assert.equal(row.attrs['aria-selected'], String(row.dataset.nativeStore === state.active));
  }
  const initialRows = rows(); refresh();
  assert.deepEqual(rows(), initialRows, 'unchanged refresh reuses existing rows');
  assert(list.children.includes(nativeRow), 'refresh preserves the original native row');
  assert(!playBlocked(), 'verified matching account permits native Play');
  for (const change of [{ busy: true }, { holdLogin: true }, { selected: 'accountA' }, { profile: null }, { profile: accountA.uuid }]) {
    const original = { ...state }; Object.assign(state, change); await tick();
    assert(playBlocked(), 'Play blocked for ' + JSON.stringify(change));
    if (change.busy) {
      const before = calls.length;
      await command('select', 'accountA'); await command('add');
      assert.equal(calls.length, before, 'busy rows cannot invoke account commands');
      assert(rows().every(row => row.attrs['aria-disabled'] === 'true' && row.tabIndex === -1));
    }
    Object.assign(state, original); await tick();
    assert(!playBlocked(), 'verified state restores native Play');
  }
  user.getUserProfile = null;
  assert(playBlocked(), 'missing native identity blocks Play'); user.getUserProfile = accountB;
  disconnected = true; await tick();
  assert(playBlocked(), 'disconnected helper blocks Play'); disconnected = false; await tick();
  assert(!playBlocked(), 'verified reconnection restores native Play');
  const beforeIgnoredKey = calls.length;
  dispatch(rows()[1], 'keydown', 'Escape');
  assert.equal(calls.length, beforeIgnoredKey, 'unrelated keys do not select an account');
  let releaseCommand; commandGate = new Promise(resolve => { releaseCommand = resolve; });
  const selecting = command('select', 'accountA');
  assert(playBlocked(), 'pending command blocks Play before the helper responds');
  assert.deepEqual(calls.at(-1), { action: 'select', store: 'accountA' }, 'saved-row click invokes select for its store');
  const beforePendingClick = calls.length; dispatch(actionRow());
  assert.equal(calls.length, beforePendingClick, 'pending command blocks a second row action');
  releaseCommand(); await selecting; commandGate = null;
  assert(!playBlocked(), 'completed command permits Play only after re-verification');
  dispatch(rows()[1], 'keydown', ' '); await new Promise(setImmediate);
  assert.deepEqual(calls.at(-1), { action: 'select', store: 'accountA' }, 'Space selects the saved account');
  dispatch(rows()[0], 'keydown', 'Enter'); await new Promise(setImmediate);
  assert.deepEqual(calls.at(-1), { action: 'select', store: 'accountB' }, 'Enter selects the correct saved account');
  const beforeAdd = calls.length;
  let releaseState;
  stateGate = new Promise(resolve => { releaseState = resolve; });
  await command('add');
  assert.equal(stateGate, null, "Add's final state response is held while other polls remain available");
  await tick(); await tick();
  assert.deepEqual(calls[beforeAdd], { action: 'add' }, 'Add click invokes add without a store');
  assert.equal(actionRow().children[0].textContent, 'Cancel login');
  assert.equal(rows().length, state.stores.filter(store => !store.signedOut).length + 1, 'Cancel reuses the Add row without an extra control');
  assert.equal(actionRow().attrs['aria-disabled'], 'false'); assert.equal(actionRow().tabIndex, 0);
  assert.equal(rows()[0].attrs['aria-disabled'], 'true', 'switching stays disabled during login');
  const beforeDisabledClick = calls.length;
  await command('select', 'accountA');
  assert.equal(calls.length, beforeDisabledClick, 'login disables saved-account selection');
  assert.equal(generated, 1); assert.equal(browserUrls.length, 1, 'one Add opens the default browser exactly once');
  const authorization = new URL(browserUrls[0]);
  assert.equal(authorization.origin, 'https://oauth.accounts.hytale.com');
  assert.equal(authorization.searchParams.get('state'), 'native-state');
  assert.equal(authorization.searchParams.get('code_challenge'), 'native-pkce');
  assert.equal(authorization.searchParams.get('prompt'), 'login');
  assert.equal(nativeLogouts, 0, 'Add does not log out the existing account');
  assert.equal(state.active, 'accountB', 'existing account stays active until browser authentication succeeds');
  assert(!calls.some(call => call.loginRequest), 'do not save old profile as a fresh login before the callback');
  nativeProfile = accountA; events.reload();
  releaseState(); await new Promise(setImmediate); await tick();
  assert.equal(state.login, null, "one completion event during Add's final state request finishes login");
  assert.deepEqual(calls.at(-1), { action: 'ready', profile: accountA.uuid, username: 'accountA', loginRequest: 'login-1' });
  assert.equal(state.active, 'accountA');
  assert.equal([...timers.values()].at(-1).delay, 0, 'completion refreshes the Add row without waiting for the idle poll');
  await tick(); assert.equal(actionRow().children[0].textContent, 'Add account', 'completion restores the existing Add row');
  assert(!playBlocked(), 'one completion event restores Play after the completed login is verified');
  assert.equal(browserUrls.length, 1, 'completion during the pending Add does not reopen the browser');
  deliveryFailures.set('login-2', 1);
  await command('add'); await tick(); events.profile_changed(); await tick();
  assert.equal(calls.at(-1).loginRequest, 'login-2', 'same-account login is confirmed by the native callback too');
  const completion = calls.at(-1);
  assert(playBlocked(), 'failed account completion keeps Play blocked');
  await tick();
  assert.deepEqual(delivered.at(-1), completion, 'account completion retries the same request and identity');
  assert(playBlocked(), 'Play stays blocked until the completed transition is read back');
  await tick();
  assert(!playBlocked(), 'Play resumes after the retried completion is verified');
  assert.equal(browserUrls.length, 2);
  await tick();
  failLogin = true; await command('add'); await tick();
  assert.equal(calls.at(-1).action, 'login-failed'); assert.equal(state.active, 'accountA');
  assert.equal(calls.at(-1).loginRequest, 'login-3', 'login failure is bound to its attempt');
  assert.equal([...timers.values()].at(-1).delay, 0, 'login failure begins native recovery immediately');
  assert.equal(browserUrls.length, 2, 'failed native URL generation preserves the current account');
  await tick();
  assert(!calls.slice(beforeAdd).some(call => ['select', 'logout'].includes(call.action)), 'Add never requests a launcher switch or logout');
  failLogin = false;
  await command('add'); await tick();
  assert(playBlocked(), 'Play is blocked while browser login is pending');
  commandGate = new Promise(resolve => { releaseCommand = resolve; });
  dispatch(actionRow(), 'keydown', 'Enter'); await new Promise(setImmediate);
  events.reload(); const cancellingPoll = tick(); await new Promise(setImmediate);
  assert(!calls.some(call => call.loginRequest === 'login-4' && call.action === 'ready'), 'completion during pending cancellation cannot confirm the login');
  releaseCommand(); commandGate = null; await cancellingPoll; await new Promise(setImmediate); await tick();
  assert(calls.some(call => call.action === 'cancel' && call.loginRequest === 'login-4'), 'keyboard activates Cancel on the reused row');
  assert.equal(state.login, null); assert.equal(state.active, 'accountA'); assert.equal(state.selected, 'accountA');
  assert.equal(actionRow().children[0].textContent, 'Add account'); assert(!playBlocked(), 'verified current account can Play after cancellation');
  assert.equal(rows()[0].attrs['aria-disabled'], 'false', 'switching is enabled again');
  events.reload(); await tick();
  assert(!calls.some(call => call.loginRequest === 'login-4' && call.action === 'ready'), 'late event after cancel cannot complete the cancelled login');
  await command('add'); await tick();
  let releaseLoad; loadGate = new Promise(resolve => { releaseLoad = resolve; });
  events.profile_changed(); const oldCompletion = tick(); await new Promise(setImmediate);
  await command('cancel'); await command('add');
  releaseLoad(); await oldCompletion; loadGate = null;
  assert(!calls.some(call => call.action === 'ready' && ['login-5', 'login-6'].includes(call.loginRequest)),
    'an in-flight old profile load cannot complete a cancelled or newer login');
  await tick(); events.reload(); await tick();
  assert.equal(calls.at(-1).loginRequest, 'login-6', 'a fresh native event completes the newer login');
  await tick();
  let releaseUrl; urlGate = new Promise(resolve => { releaseUrl = resolve; });
  await command('add'); const oldOpen = tick(); await new Promise(setImmediate);
  events.reload();
  const beforeCancelledOpen = browserUrls.length;
  await command('cancel'); await command('add');
  releaseUrl(); await oldOpen; urlGate = null;
  assert.equal(browserUrls.length, beforeCancelledOpen, 'cancelled URL generation cannot open the browser after retry');
  await tick();
  assert(!calls.some(call => call.action === 'ready' && call.loginRequest === 'login-8'), 'events before the new browser login opens cannot complete it');
  dispatch(actionRow()); await new Promise(setImmediate); await tick();
  let rejectUrl;
  urlGate = new Promise((resolve, reject) => { rejectUrl = reject; });
  await command('add'); const oldFailure = tick(); await new Promise(setImmediate);
  await command('cancel'); await command('add');
  rejectUrl(Error('old authentication failed')); await oldFailure; urlGate = null;
  assert(!calls.some(call => call.action === 'login-failed' && ['login-9', 'login-10'].includes(call.loginRequest)),
    'late URL failure cannot cancel a newer login');
  await tick(); events.reload(); await tick(); await tick();
  const browserCount = browserUrls.length;
  for (const operation of ['stop', 'load', 'run']) {
    state.session = { request: 'switch-' + operation, operation }; state.busy = true;
    await tick(); await tick();
    assert.equal(calls.at(-1).action, 'session-result');
    assert.equal(calls.at(-1).request, 'switch-' + operation);
    assert.equal(calls.at(-1).success, true);
    assert.equal(calls.at(-1).profile, operation === 'stop' ? null : accountA.uuid);
  }
  assert.deepEqual(nativeActions, ['hya:stop', 'hya:load', 'hya:run!'], 'each native stage executes once');
  assert.equal(user.getUserProfile.uuid, accountA.uuid, 'native account state is reloaded into the existing frontend');
  assert.equal(nativeLogouts, 0, 'switching never revokes a saved session');
  assert.equal(browserUrls.length, browserCount, 'switching does not open a browser');
  state.session = null; state.logout = 'logout-current';
  await tick(); await tick();
  assert.equal(nativeLogouts, 1, 'native logout executes exactly once');
  assert.equal(calls.at(-1).action, 'logout-result');
  assert.equal(calls.at(-1).success, true);
  state.logout = null;
  nativeProfile = accountA; // An unusable fallback can still leave a cached profile.
  state.session = { request: 'clear-cached-account', operation: 'clear' };
  await tick(); await tick();
  assert.equal(calls.at(-1).success, true); assert.equal(calls.at(-1).loggedIn, false);
  const beforeSignedOutRun = nativeActions.length;
  state.session = { request: 'signed-out-ui', operation: 'run' };
  await tick();
  assert.equal(calls.at(-1).success, true, 'signed-out resume remains a valid sign-in UI refresh');
  assert.equal(calls.at(-1).loggedIn, false); assert.equal(calls.at(-1).profile, null);
  assert(playBlocked(), 'signed-out resume cannot enable Play');
  assert.equal(nativeActions.length, beforeSignedOutRun, 'sign-in refresh never starts account background tasks');
  assert.equal(user.getUserProfile, null, 'native sign-in clears the existing frontend profile');
  assert.equal(nativeLogouts, 1, 'clearing cached state never logs out a fallback account');
  assert.equal(browserUrls.length, browserCount, 'logout and sign-in refresh never open the browser automatically');
  dropdown.remove(); play.remove(); body.append(login);
  const beforeSignIn = created.length; refresh(); await tick();
  assert.equal(created.length, beforeSignIn, 'returning to native sign-in creates no addon controls');
  assert.equal(document.querySelectorAll('[data-native-store]').length, 0);
  assert.equal(login.children.length, 0, 'native sign-in stays free of Choose/Add account controls');
  login.remove(); body.append(dropdown); body.append(play); refresh();
  state.session = null; state.busy = false; state.login = null;
  for (const operation of ['stop', 'load', 'run', 'clear', 'logout']) {
    timers.clear();
    nativeProfile = user.getUserProfile = accountA;
    const request = 'retry-' + operation;
    if (operation === 'logout') state.logout = request;
    else state.session = { request, operation };
    state.busy = true;
    deliveryFailures.set(request, 1);
    const beforeActions = nativeActions.length, beforeLogouts = nativeLogouts;
    await tick();
    const completed = calls.at(-1);
    assert.equal(completed.request, request);
    assert.equal(completed.success, true, 'native operation succeeds before result delivery fails');
    assert(!delivered.some(call => call.request === request), 'the first result never reaches the helper');
    assert(playBlocked(), 'failed delivery keeps Play blocked');
    const scheduled = [...timers.entries()].at(-1);
    assert(scheduled && scheduled[1].delay <= 25, 'failed result delivery retains immediate transition scheduling');
    nativeProfile = accountB;
    timers.delete(scheduled[0]); await scheduled[1].fn();
    assert.deepEqual(delivered.at(-1), completed, 'retry delivers the original completed result');
    await tick();
    assert.deepEqual(calls.at(-1), completed, 'keep reporting the cached result while the helper requests it');
    assert.equal(nativeActions.length - beforeActions, operation === 'logout' ? 0 : 1, 'each session operation executes exactly once across retries');
    assert.equal(nativeLogouts - beforeLogouts, operation === 'logout' ? 1 : 0, 'native logout executes exactly once across retries');
    const replacement = request + '-new';
    if (operation === 'logout') state.logout = replacement;
    else state.session = { request: replacement, operation };
    deliveryFailures.set(replacement, 1);
    await tick();
    assert.equal(calls.at(-1).request, replacement, 'changed requests discard the previous completed result');
    assert.equal(nativeActions.length - beforeActions, operation === 'logout' ? 0 : 2, 'a new session request executes its own operation once');
    assert.equal(nativeLogouts - beforeLogouts, operation === 'logout' ? 2 : 0, 'a new logout request executes its own operation once');
    const reportedCount = calls.filter(call => [request, replacement].includes(call.request)).length;
    state.session = null; state.logout = null; state.busy = false;
    await tick();
    assert.equal(calls.filter(call => [request, replacement].includes(call.request)).length, reportedCount, 'withdrawn requests discard undelivered results');
  }
  for (const scenario of ['transient', 'persistent', 'failed']) {
    timers.clear();
    nativeProfile = user.getUserProfile = accountA;
    state.active = state.selected = 'accountA'; state.profile = accountA.uuid;
    state.logout = 'confirmation-' + scenario; state.busy = true;
    logoutFailure = scenario === 'failed';
    verificationFailures = scenario === 'transient' ? 1 : scenario === 'persistent' ? 4 : 0;
    const beforeLogouts = nativeLogouts, beforeVerifications = verifications;
    if (scenario === 'transient') deliveryFailures.set(state.logout, 1);
    await tick();
    assert.equal(nativeLogouts - beforeLogouts, 1);
    assert.equal(calls.at(-1).request, state.logout);
    assert.equal(calls.at(-1).success, scenario === 'failed' ? false : null,
      'verification failure is distinct from a failed LogoutUser');
    assert(playBlocked(), 'unconfirmed logout keeps Play blocked despite the cached UI profile');
    for (let retry = 0; retry < 3; retry++) {
      const scheduled = [...timers.entries()].at(-1);
      assert(scheduled && scheduled[1].delay <= 25, 'logout confirmation retries without a one-second wait');
      timers.delete(scheduled[0]); await scheduled[1].fn();
    }
    assert.equal(nativeLogouts - beforeLogouts, 1, 'verification and result retries never repeat LogoutUser');
    assert.equal(calls.at(-1).success, scenario === 'transient' ? true : scenario === 'failed' ? false : null);
    assert.equal(verifications - beforeVerifications, scenario === 'transient' ? 2 : scenario === 'persistent' ? 4 : 0,
      'only uncertain confirmation retries native verification');
    assert.equal(nativeProfile, scenario === 'failed' ? accountA : null);
    state.logout = null; state.busy = false;
    state.holdLogin = scenario === 'persistent';
    if (state.holdLogin) state.profile = null;
    await tick();
    if (scenario === 'persistent') assert(playBlocked(), 'timed-out confirmation remains blocked after the helper withdraws the request');
    verificationFailures = 0; logoutFailure = false; state.holdLogin = false;
  }
  assert.equal(browserUrls.length, browserCount, 'logout confirmation never opens an authentication browser');
  {
    timers.clear();
    nativeProfile = user.getUserProfile = accountA;
    state.active = state.selected = 'accountA'; state.profile = null;
    state.busy = state.holdLogin = true; state.logout = 'confirmed-recovery-logout';
    const beforeLogouts = nativeLogouts, beforeBrowsers = browserUrls.length;
    await tick();
    assert.equal(calls.at(-1).success, true, 'logout completes before recovery fails');
    state.logout = null; state.selected = null; nativeProfile = accountB;
    for (const operation of ['load', 'stop']) {
      state.session = { request: 'failed-confirmed-recovery-' + operation, operation };
      nativeFailure = 'hya:' + operation;
      await tick();
      assert.equal(calls.at(-1).success, false, 'failed recovery reports its native failure');
    }
    nativeFailure = null; state.session = null; state.busy = false;
    for (const cached of [accountA, null, accountB, accountA]) {
      user.getUserProfile = cached;
      await tick();
      assert(playBlocked(), 'confirmed logout recovery fence rejects cached or late frontend identity');
      assert(Object.hasOwn(document.documentElement.attrs, 'data-account-transition'), 'failed recovery keeps native sign-in concealed');
    }
    state.session = { request: 'confirmed-recovery-run', operation: 'run' }; state.busy = true;
    await tick();
    assert.equal(calls.at(-1).profile, accountB.uuid, 'fresh recovery reports the actual native account');
    state.session = null; state.busy = false; await tick();
    assert(playBlocked(), 'frontend success waits for helper verification to release the fence');
    state.holdLogin = false; state.active = state.selected = 'accountB'; state.profile = accountB.uuid;
    await tick(); assert(!playBlocked(), 'verified saved-account recovery restores Play');
    state.busy = state.holdLogin = true; state.profile = null; state.selected = null;
    for (const operation of ['clear', 'run']) {
      state.session = { request: 'confirmed-sign-in-' + operation, operation };
      await tick();
      assert.equal(calls.at(-1).success, true);
      assert.equal(calls.at(-1).loggedIn, false, 'final-account sign-in accepts a signed-out UI refresh');
    }
    state.session = null; state.busy = state.holdLogin = false; state.active = 'pending-sign-in';
    await tick();
    assert(!Object.hasOwn(document.documentElement.attrs, 'data-account-transition'), 'completed signed-out refresh reveals native sign-in');
    assert.equal(user.getUserProfile, null); assert(playBlocked(), 'sign-in remains signed out until authentication');
    assert.equal(nativeLogouts, beforeLogouts + 1, 'recovery retries never repeat confirmed logout');
    assert.equal(browserUrls.length, beforeBrowsers, 'recovery never starts browser authentication');
  }
  console.log('PASS confirmed logout recovery failures fence cached and late readiness and Play until verified recovery or signed-out UI refresh');
  for (const failure of ['ordinary', 'native deadline', 'refresh deadline']) {
    timers.clear();
    nativeProfile = user.getUserProfile = accountA;
    state.active = state.selected = 'accountA'; state.profile = null;
    state.busy = state.holdLogin = true; state.logout = 'final-sign-in-' + failure;
    const beforeLogouts = nativeLogouts, beforeBrowsers = browserUrls.length;
    await tick();
    assert.equal(calls.at(-1).success, true, 'final account completes native logout before sign-in failure');
    state.logout = null; state.selected = null;
    state.session = { request: 'final-sign-in-fault-' + failure, operation: failure === 'refresh deadline' ? 'run' : 'stop' };
    const gate = deferred();
    if (failure === 'ordinary') nativeFailure = 'hya:stop';
    else if (failure === 'native deadline') nativeGate = gate.promise;
    else loadGate = gate.promise;
    const stalled = tick();
    if (failure !== 'ordinary') await expireWait();
    await stalled;
    assert.equal(calls.at(-1).success, failure === 'ordinary' ? false : null);
    state.session = null; state.busy = false; state.signInRecovery = 'final-sign-in-recovery-' + failure;
    automaticSignIn = true;
    const beforeAttempts = signInAttempts;
    if (failure !== 'ordinary') {
      for (let i = 0; i < 3; i++) await tick();
      assert.equal(signInAttempts, beforeAttempts, 'recovery waits for outstanding native work and Pinia loads to settle');
    }
    disconnected = true; await tick();
    assert(playBlocked(), 'disconnect keeps the final-account recovery fence');
    assert(Object.hasOwn(document.documentElement.attrs, 'data-account-transition'), 'sign-in remains concealed during uncertainty');
    nativeGate = loadGate = null; gate.resolve(); await new Promise(setImmediate);
    assert.equal(signInAttempts, beforeAttempts, 'late completion and disconnection cannot release the fence or start recovery');
    disconnected = false;
    if (failure === 'ordinary') deliveryFailures.set(state.signInRecovery, 1);
    await tick(); // Automatic retry after reconnect; ordinary case first loses its command before delivery.
    if (failure === 'ordinary') {
      assert.equal(signInAttempts, beforeAttempts);
      await tick(); assert.equal(signInAttempts, beforeAttempts, 'lost retry commands observe backoff');
      now += 250; await tick();
    }
    assert.equal(signInAttempts, beforeAttempts + 1, 'the normal frontend exchange automatically retries confirmed sign-in recovery');
    if (failure === 'ordinary') {
      for (let attempt = 0; attempt < 9; attempt++) {
        await tick(); // Stop fails through the ordinary native service.
        assert.equal(state.busy, false); assert(state.signInRecovery); assert(state.holdLogin);
        const count = signInAttempts;
        await tick(); await tick();
        assert.equal(signInAttempts, count, 'repeated polls do not create an uncontrolled retry loop');
        const scheduled = [...timers.values()].at(-1);
        assert(scheduled.delay > 0 && scheduled.delay <= 30000, 'retry delay is positive and capped at thirty seconds');
        now += scheduled.delay; await tick();
        assert.equal(signInAttempts, count + 1, 'persistent ordinary failure remains retryable after bounded backoff');
      }
    }
    nativeFailure = null;
    const resumed = nativeActions.length;
    for (let stage = 0; stage < 3; stage++) {
      await tick();
      if (stage < 2) {
        assert(state.holdLogin, 'successful stop and clear keep the fence until fresh signed-out refresh');
        assert(playBlocked());
      }
    }
    await tick();
    assert.equal(state.signInRecovery, null); assert.equal(state.holdLogin, false);
    assert.equal(user.getUserProfile, null, 'fresh refresh clears cached identity before exposing sign-in');
    assert(!Object.hasOwn(document.documentElement.attrs, 'data-account-transition'), 'automatic successful recovery reveals native sign-in');
    assert(playBlocked(), 'signed-out recovery never permits Play');
    assert.deepEqual(nativeActions.slice(resumed), ['hya:stop', 'hya:stop'], 'recovery stops and clears without starting signed-in background work');
    assert.equal(nativeLogouts, beforeLogouts + 1, 'automatic recovery never repeats completed logout');
    assert.equal(browserUrls.length, beforeBrowsers, 'automatic recovery never starts browser authentication');
    automaticSignIn = false;
  }
  console.log('PASS final-account sign-in automatically retries through frontend commands after ordinary failures, deadlines and reconnects, with serialized capped backoff and exactly one logout');
  state.active = state.selected = 'accountB'; state.profile = accountB.uuid;
  nativeProfile = user.getUserProfile = accountB;
  timers.clear(); await tick();
  for (const [id, task] of [...timers]) { timers.delete(id); await task.fn(); }
  assert.equal(timers.size, 0, 'idle frontend does not schedule fast polls');
  automaticSwitch = true;
  const beforeFastSwitch = nativeActions.length;
  await command('select', 'accountA');
  assert.equal([...timers.values()][0].delay, 0, 'the account click starts native processing immediately');
  let schedulingDelay = 0, polls = 0;
  while (state.busy || timers.size) {
    const [id, task] = [...timers.entries()][0] ?? [];
    assert(task, 'each native stage schedules its successor without an interval tick');
    timers.delete(id); schedulingDelay += task.delay;
    assert(++polls <= 5, 'switch must complete without duplicate polling chains');
    await task.fn();
  }
  assert.deepEqual(nativeActions.slice(beforeFastSwitch), ['hya:stop', 'hya:load', 'hya:run!']);
  assert(schedulingDelay <= 100, 'three native stages have no multi-second artificial delay');
  assert.equal(user.getUserProfile.uuid, accountA.uuid);
  assert.equal(timers.size, 0, 'fast polling stops after the transition');
  automaticSwitch = false;
  for (const kind of ['session', 'login']) {
    timers.clear();
    state.session = null; state.logout = null; state.login = null; state.busy = state.holdLogin = false;
    state.active = state.selected = 'accountA'; state.profile = accountA.uuid;
    nativeProfile = user.getUserProfile = accountA;
    await tick();
    if (kind === 'login') { await command('add'); await tick(); events.reload(); }
    else { state.session = { request: 'stalled-run', operation: 'run' }; state.busy = true; }
    const gate = deferred(); loadGate = gate.promise;
    const beforeLoads = userLoads, beforeReloads = reloads, beforeCalls = calls.length;
    const stalled = tick(); await new Promise(setImmediate);
    assert.equal(userLoads, beforeLoads + 1, kind + ' enters the controlled, never-settling user.load');
    if (kind === 'login') await command('cancel');
    await expireWait(); await stalled;
    if (kind === 'session') {
      assert.equal(calls.at(-1).request, 'stalled-run');
      assert.equal(calls.at(-1).success, null, 'load deadline reports uncertainty instead of a definite native failure');
    }
    assert(playBlocked(), kind + ' load timeout keeps Play blocked');
    state.login = null; state.busy = state.holdLogin = true;
    nativeProfile = accountB;
    for (const operation of ['stop', 'load']) {
      state.session = { request: kind + '-recovery-' + operation, operation };
      await tick();
      assert.equal(calls.at(-1).request, state.session.request);
      assert.equal(calls.at(-1).success, true, 'later recovery executes despite the unsettled frontend load');
    }
    state.session = { request: kind + '-recovery-run', operation: 'run' };
    const recovering = tick(); await new Promise(setImmediate);
    assert.equal(userLoads, beforeLoads + 1, 'a recovery cannot start a conflicting Pinia load');
    await expireWait(); await recovering;
    assert.equal(calls.at(-1).success, null, 'unresolved frontend refresh cannot confirm recovery');
    state.session = null; state.busy = state.holdLogin = false;
    state.active = state.selected = 'accountB'; state.profile = accountB.uuid;
    user.getUserProfile = accountB;
    await tick();
    assert(playBlocked(), 'even a matching cached profile cannot clear uncertainty');
    const afterWithdrawal = calls.length;
    loadGate = null; gate.resolve(); await new Promise(setImmediate); await tick();
    assert.equal(user.getUserProfile, accountA, 'late native load can still write its old Pinia result');
    assert.equal(calls.length, afterWithdrawal, 'late completion cannot acknowledge a withdrawn request or publish readiness');
    assert.equal(reloads, beforeReloads, 'withdrawn loads cannot emit stale reload events');
    assert(!calls.slice(beforeCalls).some(call => call.action === 'ready'), 'neither timed-out load publishes readiness');
    assert(playBlocked(), 'late completion cannot unblock Play');
    state.session = { request: kind + '-fresh-run', operation: 'run' }; state.busy = true;
    await tick();
    assert.equal(calls.at(-1).request, state.session.request);
    assert.equal(calls.at(-1).success, true, 'fresh load verifies recovery after the old load actually settles');
    assert.equal(user.getUserProfile, accountB, 'a late old load cannot overwrite a newer frontend result');
    assert.equal(reloads, beforeReloads + 1, 'only the current verified run emits reload');
    state.session = null; state.busy = false; state.holdLogin = true; state.profile = null;
    await tick();
    assert(playBlocked(), 'successful frontend refresh cannot bypass the helper failed-recovery readiness fence');
    state.holdLogin = false; state.profile = accountB.uuid; await tick();
    assert(!playBlocked(), 'verified recovery restores Play in the existing frontend');
  }
  console.log('PASS controlled session/login load deadlines process later recovery, serialize Pinia loads, and fence withdrawn late completions, readiness and Play');
  console.log('PASS successful frontend refresh after a deadline still respects the helper failed-recovery readiness fence');
  {
    timers.clear();
    const gate = deferred(); nativeGate = gate.promise;
    state.session = { request: 'stalled-native-stop', operation: 'stop' }; state.busy = true;
    const beforeActions = nativeActions.length;
    const stalled = tick(); await expireWait(); await stalled;
    assert.equal(calls.at(-1).success, null, 'unsettled native transition remains uncertain');
    state.session = { request: 'later-native-stop', operation: 'stop' };
    const recovering = tick(); await new Promise(setImmediate);
    assert.equal(nativeActions.length, beforeActions + 1, 'recovery never overlaps an unsettled native transition');
    await expireWait(); await recovering;
    assert.equal(calls.at(-1).request, state.session.request);
    assert.equal(calls.at(-1).success, null);
    const recoveryResult = calls.at(-1);
    nativeGate = null; gate.resolve(); await new Promise(setImmediate); await tick();
    assert.deepEqual(calls.at(-1), recoveryResult, 'late native completion cannot replace a newer cached result');
    assert.equal(nativeActions.length, beforeActions + 1, 'deadline and result retries do not repeat native transitions');
    state.session = null; state.busy = false; await tick();
    assert(playBlocked(), 'a late native completion alone cannot clear uncertainty');
    state.session = { request: 'verified-native-run', operation: 'run' }; state.busy = true;
    await tick(); state.session = null; state.busy = false; await tick();
    assert(!playBlocked());
  }
  console.log('PASS native transition deadlines retain ownership until actual settlement and cannot overwrite newer results or enable Play');
  for (const phase of ['logout', 'confirmation']) {
    timers.clear();
    nativeProfile = user.getUserProfile = accountB;
    state.logout = 'deadline-' + phase; state.busy = true;
    const gate = deferred();
    if (phase === 'logout') logoutGate = gate.promise; else verificationGate = gate.promise;
    const beforeLogouts = nativeLogouts, beforeCalls = calls.length;
    const stalled = tick(); await expireWait(); await stalled;
    assert.equal(calls.at(-1).success, null, phase + ' deadline preserves unconfirmed logout');
    const retry = tick(); await expireWait(); await retry;
    assert.equal(nativeLogouts, beforeLogouts + 1, 'deadline retries only confirmation, never LogoutUser');
    state.logout = null; state.busy = false; state.holdLogin = true; state.profile = null;
    await tick();
    const withdrawnCalls = calls.length;
    logoutGate = verificationGate = null; gate.resolve(); await new Promise(setImmediate); await tick();
    assert.equal(calls.length, withdrawnCalls, 'late logout/confirmation cannot acknowledge a withdrawn request');
    assert.equal(nativeLogouts, beforeLogouts + 1, 'withdrawal never repeats completed logout');
    assert(!calls.slice(beforeCalls).some(call => call.action === 'ready'), 'unconfirmed logout never publishes cached readiness');
    assert(playBlocked(), 'uncertain logout remains blocked after late settlement');
    nativeProfile = accountB;
    state.session = { request: 'logout-recovery-' + phase, operation: 'run' }; state.busy = true;
    await tick();
    assert.equal(calls.at(-1).success, true);
    state.session = null; state.busy = state.holdLogin = false; state.profile = accountB.uuid;
    await tick(); assert(!playBlocked());
  }
  console.log('PASS controlled LogoutUser and confirmation deadlines preserve uncertainty and never repeat logout after retries or withdrawal');
  for (const kind of ['session', 'logout']) {
    timers.clear();
    const gate = deferred(), request = 'withdrawn-' + kind;
    if (kind === 'session') { loadGate = gate.promise; state.session = { request, operation: 'run' }; }
    else { logoutGate = gate.promise; state.logout = request; }
    state.busy = true;
    const beforeCalls = calls.length, beforeReloads = reloads, beforeLogouts = nativeLogouts;
    const inFlight = tick(); await new Promise(setImmediate);
    // Helper withdrawal can happen without a frontend command changing commandVersion.
    state.logout = null; state.session = { request: request + '-recovery', operation: 'stop' };
    loadGate = logoutGate = null; gate.resolve(); await inFlight;
    assert.equal(calls.length, beforeCalls, 'completion rechecks the helper request before acknowledging it');
    assert.equal(reloads, beforeReloads, 'withdrawal before the deadline also fences reload');
    await tick();
    assert.equal(calls.at(-1).request, state.session.request, 'the next poll immediately handles the replacement request');
    assert.equal(calls.at(-1).success, true);
    assert.equal(nativeLogouts - beforeLogouts, kind === 'logout' ? 1 : 0, 'withdrawal cannot repeat completed logout');
    nativeProfile = accountB;
    state.session = { request: request + '-verified-run', operation: 'run' };
    await tick();
    assert.equal(calls.at(-1).success, true);
    state.session = null; state.busy = false; await tick();
    assert(!playBlocked());
  }
  console.log('PASS helper request identities fence session/logout completions withdrawn before the frontend deadline, without a command-version change');
  {
    timers.clear();
    const browsers = browserUrls.length;
    nativeProfile = user.getUserProfile = accountB;
    state.active = state.selected = 'accountB'; state.profile = null;
    state.busy = state.holdLogin = true;
    await tick(); assert(playBlocked(), 'existing recovery fence blocks Play during replacement startup');
    state.profile = accountB.uuid; state.busy = state.holdLogin = false;
    await tick(); assert(!playBlocked(), 'authenticated startup completion restores native Play');
    state.profile = null; state.busy = state.holdLogin = true;
    nativeProfile = user.getUserProfile = null;
    dropdown.remove(); play.remove(); body.append(login);
    await tick();
    assert(Object.hasOwn(document.documentElement.attrs, 'data-account-transition'), 'startup retains sign-in concealment until verification completes');
    state.active = 'pending-startup-sign-in'; state.busy = state.holdLogin = false;
    await tick(); now += 3000; await tick();
    assert(!Object.hasOwn(document.documentElement.attrs, 'data-account-transition'), 'signed-out startup completion reveals native sign-in');
    assert.equal(login.children.length, 0, 'startup recovery adds no sign-in controls');
    assert(playBlocked(), 'signed-out startup cannot enable Play');
    nativeProfile = user.getUserProfile = accountA;
    await tick();
    assert.equal(calls.at(-1).action, 'ready');
    assert.equal(calls.at(-1).profile, accountA.uuid, 'native sign-in can report a different account after signed-out startup');
    state.active = state.selected = 'accountA'; state.profile = accountA.uuid;
    login.remove(); body.append(dropdown); body.append(play); refresh();
    await tick(); assert(!playBlocked(), 'verified sign-in after startup restores native Play');
    assert.equal(browserUrls.length, browsers, 'startup completion opens no browser authentication');
  }
  console.log('PASS startup recovery completion releases Play and native sign-in concealment only after authenticated or signed-out verification');
  console.log('PASS first-update run uses the mounted Pinia store with renamed assets, waits for readiness, and retries results without repeating recovery');
  console.log('PASS production Play handler blocks pending, busy, disconnected and mismatched accounts; verified matching identity reaches native Play');
  console.log('PASS dropdown renders usable saved accounts and Add, dispatches click/keyboard actions, and leaves native sign-in free of addon controls');
  console.log('PASS Add opens native Hytale login once, retains current account, confirms callbacks, and preserves login state/PKCE');
  console.log('PASS one completion event during the pending Add state response restores Add and Play without reopening the browser');
  console.log('PASS switching pauses, reloads and resumes native state once per request without logout or browser login');
  console.log('PASS logout executes once and final-account sign-in clears cached identity without logging out other accounts');
  console.log('PASS account clicks immediately process native stages with at most 100 ms scheduling delay and no fast idle polling');
  console.log('PASS lost session/logout results retry immediately with identical payloads and exactly one native execution per request');
  console.log('PASS transient and persistent logout verification errors retry confirmation only, preserve Play blocking, and remain distinct from genuine logout failure');
  console.log('PASS the existing row cancels login, restores Play/switching, and fences late profile loads and browser opens across retries');
  completed = true;
})().catch(error => { console.error(error); process.exitCode = 1; });
