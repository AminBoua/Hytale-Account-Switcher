const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
let tick, nativeLogouts = 0, generated = 0, requestId = 0, failLogin = false;
const calls = [], created = [], browserUrls = [], events = {}, nativeActions = [];
const timers = new Map();
let timerId = 0, automaticSwitch = false, showDropdown = false, clickHandler, loadGate, urlGate;
const accountB = { uuid: 'accountB-profile', username: 'accountB' };
const accountA = { uuid: 'accountA-profile', username: 'accountA' };
let nativeProfile = accountB;
const user = { getUserProfile: accountB, async load() { const profile = nativeProfile; await loadGate; this.getUserProfile = profile; } };
const state = { stores: [{ id: 'accountB', label: 'accountB' }, { id: 'accountA', label: 'accountA' }], active: 'accountB', selected: 'accountB', profile: accountB.uuid, busy: false, login: null };
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
function element() {
  return { dataset: {}, attributes: [], children: [], handlers: {}, attrs: {},
    append(child) { this.children.push(child); }, setAttribute(name, value) { this.attrs[name] = value; },
    addEventListener(name, fn) { this.handlers[name] = fn; }, remove() { list.children.splice(list.children.indexOf(this), 1); } };
}
const list = element();
list.querySelector = () => list.children[0]; list.querySelectorAll = () => [...list.children];
const document = {
  addEventListener(name, fn) { if (name === 'click') clickHandler = fn; }, currentScript: { src: 'http://127.0.0.1:1234/cap/ui.js' },
  createElement: tag => { created.push(tag); return element(); }, body: { append() {} },
  querySelector: selector => selector === '.launch-game__profile-dropdown' && showDropdown ? { querySelector: () => list } : selector === '.login__container' ? {} : selector === '#app' ?
    { __vue_app__: { config: { globalProperties: { $pinia: { _s: new Map([['user', user]]) } } } } } : null
};
const source = fs.readFileSync('Launcher/NativeDropdown.js', 'utf8')
  .replace('  new MutationObserver', '  window.testCommand = command;\n  new MutationObserver');
vm.runInNewContext(source, {
  location: { origin: 'http://wails.localhost', pathname: '/launch-game' }, window, document, URL, AbortSignal, Date,
  MutationObserver: class { observe() {} }, setInterval: fn => { tick = fn; },
  setTimeout: (fn, delay) => { timers.set(++timerId, { fn, delay }); return timerId; },
  clearTimeout: id => timers.delete(id),
  alert: message => { throw Error(message); },
  fetch: async (url, options) => {
    if (options.body) {
      const data = JSON.parse(options.body); calls.push(data);
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
    return { ok: true, json: async () => structuredClone(state) };
  }
});
(async () => {
  await new Promise(setImmediate); // Allow the initial asynchronous poll to finish.
  await tick();
  assert.deepEqual(created, ['style'], 'native sign-in receives no account controls');
  showDropdown = true;
  await window.testCommand('add'); await tick(); await tick();
  const actionRow = () => list.children.at(-1);
  assert.equal(actionRow().children[0].textContent, 'Cancel login');
  assert.equal(list.children.length, state.stores.length + 1, 'Cancel reuses the Add row without an extra control');
  assert.equal(actionRow().attrs['aria-disabled'], 'false'); assert.equal(actionRow().tabIndex, 0);
  assert.equal(list.children[0].attrs['aria-disabled'], 'true', 'switching stays disabled during login');
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
  await window.testCommand('add'); await tick(); events.profile_changed(); await tick();
  assert.equal(calls.at(-1).loginRequest, 'login-2', 'same-account login is confirmed by the native callback too');
  assert.equal(browserUrls.length, 2);
  await tick();
  failLogin = true; await window.testCommand('add'); await tick();
  assert.equal(calls.at(-1).action, 'login-failed'); assert.equal(state.active, 'accountA');
  assert.equal(calls.at(-1).loginRequest, 'login-3', 'login failure is bound to its attempt');
  assert.equal([...timers.values()].at(-1).delay, 0, 'login failure begins native recovery immediately');
  assert.equal(browserUrls.length, 2, 'failed native URL generation preserves the current account');
  await tick();
  assert(!calls.some(call => ['select', 'logout'].includes(call.action)), 'Add never requests a launcher switch or logout');
  failLogin = false;
  const playBlocked = () => {
    let blocked = false;
    clickHandler({ target: { closest: selector => selector.includes('play-hytale') ? {} : null },
      preventDefault() { blocked = true; }, stopImmediatePropagation() {} });
    return blocked;
  };
  await window.testCommand('add'); await tick();
  assert(playBlocked(), 'Play is blocked while browser login is pending');
  const rowEvent = { preventDefault() {}, stopPropagation() {}, key: 'Enter' };
  actionRow().handlers.keydown(rowEvent); await new Promise(setImmediate); await tick();
  assert(calls.some(call => call.action === 'cancel' && call.loginRequest === 'login-4'), 'keyboard activates Cancel on the reused row');
  assert.equal(state.login, null); assert.equal(state.active, 'accountA'); assert.equal(state.selected, 'accountA');
  assert.equal(actionRow().children[0].textContent, 'Add account'); assert(!playBlocked(), 'verified current account can Play after cancellation');
  assert.equal(list.children[0].attrs['aria-disabled'], 'false', 'switching is enabled again');
  events.reload(); await tick();
  assert(!calls.some(call => call.loginRequest === 'login-4' && call.action === 'ready'), 'late event after cancel cannot complete the cancelled login');
  await window.testCommand('add'); await tick();
  let releaseLoad; loadGate = new Promise(resolve => { releaseLoad = resolve; });
  events.profile_changed(); const oldCompletion = tick(); await new Promise(setImmediate);
  await window.testCommand('cancel'); await window.testCommand('add');
  releaseLoad(); await oldCompletion; loadGate = null;
  assert(!calls.some(call => call.action === 'ready' && ['login-5', 'login-6'].includes(call.loginRequest)),
    'an in-flight old profile load cannot complete a cancelled or newer login');
  await tick(); events.reload(); await tick();
  assert.equal(calls.at(-1).loginRequest, 'login-6', 'a fresh native event completes the newer login');
  await tick();
  let releaseUrl; urlGate = new Promise(resolve => { releaseUrl = resolve; });
  await window.testCommand('add'); const oldOpen = tick(); await new Promise(setImmediate);
  events.reload();
  const beforeCancelledOpen = browserUrls.length;
  await window.testCommand('cancel'); await window.testCommand('add');
  releaseUrl(); await oldOpen; urlGate = null;
  assert.equal(browserUrls.length, beforeCancelledOpen, 'cancelled URL generation cannot open the browser after retry');
  await tick();
  assert(!calls.some(call => call.action === 'ready' && call.loginRequest === 'login-8'), 'events before the new browser login opens cannot complete it');
  actionRow().handlers.click(rowEvent); await new Promise(setImmediate); await tick();
  let rejectUrl;
  urlGate = new Promise((resolve, reject) => { rejectUrl = reject; });
  await window.testCommand('add'); const oldFailure = tick(); await new Promise(setImmediate);
  await window.testCommand('cancel'); await window.testCommand('add');
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
  state.session = null; state.busy = false; state.login = null;
  state.active = state.selected = 'accountB'; state.profile = accountB.uuid;
  nativeProfile = user.getUserProfile = accountB;
  timers.clear(); await tick();
  for (const [id, task] of [...timers]) { timers.delete(id); await task.fn(); }
  assert.equal(timers.size, 0, 'idle frontend does not schedule fast polls');
  automaticSwitch = true;
  const beforeFastSwitch = nativeActions.length;
  await window.testCommand('select', 'accountA');
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
  console.log('PASS Add opens native Hytale login once, retains current account, confirms callbacks, and preserves login state/PKCE');
  console.log('PASS switching pauses, reloads and resumes native state once per request without logout or browser login');
  console.log('PASS logout executes once and final-account sign-in clears cached identity without logging out other accounts');
  console.log('PASS account clicks immediately process native stages with at most 100 ms scheduling delay and no fast idle polling');
  console.log('PASS the existing row cancels login, restores Play/switching, and fences late profile loads and browser opens across retries');
})().catch(error => { console.error(error); process.exitCode = 1; });
