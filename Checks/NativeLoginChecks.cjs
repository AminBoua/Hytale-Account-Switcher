const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
let tick, nativeLogouts = 0, generated = 0, requestId = 0, failLogin = false, userLoads = 0;
const calls = [], created = [], browserUrls = [], events = {}, nativeActions = [];
const deliveryFailures = new Map(), delivered = [];
const timers = new Map();
let timerId = 0, automaticSwitch = false, disconnected = false, loadGate, urlGate, commandGate, refresh;
const accountB = { uuid: 'accountB-profile', username: 'accountB' };
const accountA = { uuid: 'accountA-profile', username: 'accountA' };
let nativeProfile = accountB;
const user = { getUserProfile: accountB, async load() { userLoads++; const profile = nativeProfile; await loadGate; this.getUserProfile = profile; } };
const state = { stores: [{ id: 'accountB', label: 'accountB' }, { id: 'accountA', label: 'accountA' },
  { id: 'signed-out', label: 'Signed out', signedOut: true }], active: 'accountB', selected: 'accountB', profile: accountB.uuid, busy: false, holdLogin: false, login: null };
const window = {
  go: { account: { Service: {
    GenerateOauthURL: async () => { generated++; await urlGate; if (failLogin) throw Error('native login failed'); return 'https://oauth.accounts.hytale.com/oauth2/auth?state=native-state&code_challenge=native-pkce'; },
    LogoutUser: async () => { nativeLogouts++; nativeProfile = null; }, IsUserLoggedIn: async () => !!nativeProfile,
    HasValidSession: async () => true,
    CheckNetworkMode: async (automatic, source) => {
      nativeActions.push(source);
      if (automatic) { assert.equal(source, 'hya:stop'); nativeProfile = null; }
      if (automaticSwitch && source === 'hya:load') nativeProfile = accountA;
    }
  } }, user: { Service: { GetUserProfile: async () => nativeProfile } } },
  runtime: { BrowserOpenURL: url => browserUrls.push(url), EventsOn: (name, fn) => { events[name] = fn; }, EventsEmit: name => events[name]?.() }
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
  location: { origin: 'http://wails.localhost', pathname: '/launch-game' }, window, document, URL, AbortSignal, Date,
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
      if (data.action === 'ready' && data.loginRequest === state.login) {
        state.login = null; state.active = state.selected = data.username; state.profile = data.profile;
      }
    }
    if (!options.body && disconnected) throw Error('helper disconnected');
    return { ok: true, json: async () => structuredClone(state) };
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
  state.session = null; state.busy = false;
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
  await command('add'); await tick(); await tick();
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
  nativeProfile = accountA; events.reload(); await tick();
  assert.deepEqual(calls.at(-1), { action: 'ready', profile: accountA.uuid, username: 'accountA', loginRequest: 'login-1' });
  assert.equal(state.active, 'accountA');
  assert.equal([...timers.values()].at(-1).delay, 0, 'completion refreshes the Add row without waiting for the idle poll');
  await tick(); assert.equal(actionRow().children[0].textContent, 'Add account', 'completion restores the existing Add row');
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
  dispatch(actionRow(), 'keydown', 'Enter'); await new Promise(setImmediate); await tick();
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
  console.log('PASS first-update run uses the mounted Pinia store with renamed assets, waits for readiness, and retries results without repeating recovery');
  console.log('PASS production Play handler blocks pending, busy, disconnected and mismatched accounts; verified matching identity reaches native Play');
  console.log('PASS dropdown renders usable saved accounts and Add, dispatches click/keyboard actions, and leaves native sign-in free of addon controls');
  console.log('PASS Add opens native Hytale login once, retains current account, confirms callbacks, and preserves login state/PKCE');
  console.log('PASS switching pauses, reloads and resumes native state once per request without logout or browser login');
  console.log('PASS logout executes once and final-account sign-in clears cached identity without logging out other accounts');
  console.log('PASS account clicks immediately process native stages with at most 100 ms scheduling delay and no fast idle polling');
  console.log('PASS lost session/logout results retry immediately with identical payloads and exactly one native execution per request');
  console.log('PASS the existing row cancels login, restores Play/switching, and fences late profile loads and browser opens across retries');
})().catch(error => { console.error(error); process.exitCode = 1; });
