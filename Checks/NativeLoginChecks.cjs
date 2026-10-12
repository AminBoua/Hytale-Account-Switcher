const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
let tick, nativeLogouts = 0, generated = 0, requestId = 0, failLogin = false;
const calls = [], created = [], browserUrls = [], events = {}, nativeActions = [];
const timers = new Map();
let timerId = 0, automaticSwitch = false;
const accountB = { uuid: 'accountB-profile', username: 'accountB' };
const accountA = { uuid: 'accountA-profile', username: 'accountA' };
let nativeProfile = accountB;
const user = { getUserProfile: accountB, async load() { this.getUserProfile = nativeProfile; } };
const state = { stores: [], active: 'accountB', selected: 'accountB', profile: accountB.uuid, busy: false, login: null };
const window = {
  go: { account: { Service: {
    GenerateOauthURL: async () => { generated++; if (failLogin) throw Error('native login failed'); return 'https://oauth.accounts.hytale.com/oauth2/auth?state=native-state&code_challenge=native-pkce'; },
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
const document = {
  addEventListener() {}, currentScript: { src: 'http://127.0.0.1:1234/cap/ui.js' },
  createElement: tag => { created.push(tag); return {}; }, body: { append() {} },
  querySelector: selector => selector === '.login__container' ? {} : selector === '#app' ?
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
      if (data.action === 'login-failed') state.login = null;
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
    return { ok: true, json: async () => state };
  }
});
(async () => {
  await new Promise(setImmediate); // Allow the initial asynchronous poll to finish.
  await tick();
  assert.deepEqual(created, ['style'], 'native sign-in receives no account controls');
  await window.testCommand('add'); await tick(); await tick();
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
  await window.testCommand('add'); await tick(); events.profile_changed(); await tick();
  assert.equal(calls.at(-1).loginRequest, 'login-2', 'same-account login is confirmed by the native callback too');
  assert.equal(browserUrls.length, 2);
  failLogin = true; await window.testCommand('add'); await tick();
  assert.equal(calls.at(-1).action, 'login-failed'); assert.equal(state.active, 'accountA');
  assert.equal(browserUrls.length, 2, 'failed native URL generation preserves the current account');
  assert(!calls.some(call => ['select', 'logout'].includes(call.action)), 'Add never requests a launcher switch or logout');
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
  assert.equal(browserUrls.length, 2, 'switching does not open a browser');
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
  assert.equal(browserUrls.length, 2, 'logout and sign-in refresh never open the browser automatically');
  state.session = null; state.busy = false; state.login = null;
  state.active = state.selected = 'accountB'; state.profile = accountB.uuid;
  nativeProfile = user.getUserProfile = accountB;
  timers.clear(); await tick();
  assert.equal(timers.size, 0, 'idle frontend does not schedule fast polls');
  automaticSwitch = true;
  const beforeFastSwitch = nativeActions.length;
  await window.testCommand('select', 'accountA');
  assert.equal([...timers.values()][0].delay, 0, 'the account click starts native processing immediately');
  let schedulingDelay = 0, polls = 0;
  while (state.busy) {
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
})().catch(error => { console.error(error); process.exitCode = 1; });
