// Disposable test host and synthetic secrets only; never capture request bodies,
// browser traces, screenshots, or raw server errors from a credential form.
import { createRequire } from 'node:module';
const require = createRequire(new URL('../../frontend/package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const [base, mode] = process.argv.slice(2);
const browser = await chromium.launch({ headless: true, args: ['--autoplay-policy=no-user-gesture-required'] });
let step = 'opening settings';
function check(value) { if (!value) throw new Error('Assertion failed'); }
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(base + '#/settings/audio-storage');
  const tab = name => page.getByRole('navigation', { name: '设置分类' }).getByRole('link', { name, exact: true });
  const notice = page.getByRole('status', { name: '配置状态', exact: true });
  const region = () => page.getByLabel('区域', { exact: true });
  // The row is there whether the key is editable here or owned by the deployment.
  const regionRow = page.locator('[data-setting="Audio:Region"]');
  const storageSwitch = page.getByRole('switch', { name: '启用音频存储', exact: true });
  const save = page.getByRole('button', { name: '保存修改', exact: true });
  // A credential shows no input until its replacement is asked for.
  const reveal = label => page.getByRole('button', { name: new RegExp('^' + label + '：(设置|更换)$') }).click();
  const secretInputs = () => page.locator('input[type="password"]');
  await regionRow.waitFor();
  if (mode === 'configure') {
    step = 'initial disabled state without file';
    check(!await storageSwitch.isChecked());
    const second = await context.newPage(); await second.goto(base + '#/settings/rate-limits');
    const secondRateSwitch = second.getByRole('switch', { name: '启用登录限流', exact: true });
    await secondRateSwitch.waitFor();
    step = 'first browser save with CSRF and If-Match';
    for (const [label, value] of [
      ['服务访问地址', process.env.LEXARBOR_S3_TEST_INTERNAL],
      ['浏览器播放地址', process.env.LEXARBOR_S3_TEST_PUBLIC],
      ['存储桶', process.env.LEXARBOR_S3_TEST_BUCKET], ['对象前缀', 'settings/']
    ]) await page.getByLabel(label, { exact: true }).fill(value);
    for (const [label, value] of [['Access Key', 'synthetic-audio-access'], ['Secret Key', 'synthetic-audio-secret']]) {
      await reveal(label); await page.getByLabel(label, { exact: true }).fill(value);
    }
    await storageSwitch.check();
    const written = page.waitForRequest(request => request.method() === 'PUT');
    await save.click(); const request = await written;
    check(request.headers()['if-match'] && request.headers()['x-signacore-csrf']);
    await page.waitForFunction(() => document.querySelector('[aria-label="配置状态"]').textContent.includes('已保存到配置文件'));
    check(await secretInputs().count() === 0);
    step = 'reload and concurrent cross-category version conflict';
    await page.reload(); await region().waitFor();
    check(await storageSwitch.isChecked());
    await secondRateSwitch.check();
    await second.getByRole('button', { name: '保存修改', exact: true }).click();
    await second.getByRole('heading', { name: '当前配置状态未知' }).waitFor();
    check((await second.getByRole('status', { name: '配置状态', exact: true }).textContent()).includes('版本已变化'));
    await second.close();
    step = 'live rate-limit off/on/429/off through browser controls';
    const statuses = () => page.evaluate(async () => {
      const values = []; for (let i = 0; i < 4; i++) values.push((await fetch('/admin/settings-login-probe')).status); return values;
    });
    const disabledStatuses = await statuses(); console.log('Rate-limit disabled statuses: ' + disabledStatuses.join(',')); check(disabledStatuses.every(status => status === 200));
    // Opening the rate-limit tab reads the shared version after the S3 save.
    step = 'open rate-limit settings before enabling';
    await tab('请求限流').click();
    const rateSwitch = page.getByRole('switch', { name: '启用登录限流', exact: true });
    const rateSaved = page.getByRole('status', { name: '配置状态', exact: true }).filter({ hasText: '已回读确认' });
    const saveRateLimits = page.getByRole('button', { name: '保存修改', exact: true });
    await rateSwitch.check();
    step = 'enable rate-limit save and confirmation';
    await saveRateLimits.click();
    await rateSaved.waitFor();
    step = 'enabled quota enforcement';
    const enabledStatuses = await statuses(); console.log('Rate-limit enabled statuses: ' + enabledStatuses.join(','));
    check(JSON.stringify(enabledStatuses) === '[200,200,429,429]');
    step = 'disable rate-limit save and confirmation';
    await rateSwitch.uncheck();
    await saveRateLimits.click();
    await rateSaved.waitFor();
    check((await statuses()).every(status => status === 200));
    step = 'lost committed response recovers with GET without replay';
    await tab('音频存储').click();
    let puts = 0;
    await page.route('**/admin/system/settings/audio-storage', async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      puts++; await route.fetch(); await route.abort('failed');
    });
    await region().fill('us-west-1');
    await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="配置状态"]').textContent.includes('保存响应未确认'));
    check(puts === 1); check(await region().inputValue() === 'us-west-1');
    // Restore actual S3 signing region with an explicit new user submission.
    await page.unroute('**/admin/system/settings/audio-storage');
    await region().fill('us-east-1'); await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="配置状态"]').textContent.includes('已保存到配置文件'));
  } else if (mode === 'readonly') {
    step = 'read-only file failure preserves service and current configuration';
    await region().fill('test-region'); await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="配置状态"]').textContent.includes('保存失败（503）'));
    check((await page.evaluate(async () => (await fetch('/admin/settings-login-probe')).status)) === 200);
    check(await storageSwitch.isChecked());
  } else if (mode === 'override') {
    step = 'command-line override is readonly with reason and effective value';
    // A deployment-owned key shows its value with a lock, and offers no control.
    check(await region().count() === 0);
    check((await regionRow.textContent()).includes('us-east-1'));
    check((await regionRow.textContent()).includes('来源：启动参数 · 网页只读'));
  } else if (mode === 'disable' || mode === 'enable') {
    step = 'live audio toggle from real form keeps credentials and objects';
    await storageSwitch.setChecked(mode === 'enable');
    await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="配置状态"]').textContent.includes('已保存到配置文件'));
    check(await page.getByText('已设置（不显示原值）').count() === 2);
  } else if (mode === 'playback' || mode === 'restart') {
    step = 'persisted settings and empty secret controls';
    check(await storageSwitch.isChecked());
    check(await page.getByLabel('对象前缀', { exact: true }).inputValue() === 'settings/');
    check(await secretInputs().count() === 0);
    if (mode === 'playback') {
      step = 'existing historical destination refuses browser save';
      await page.getByLabel('存储桶', { exact: true }).fill('other-bucket'); await save.click();
      await page.waitForFunction(() => document.querySelector('[aria-label="配置状态"]').textContent.includes('保存失败（409）'));
      check((await notice.textContent()).includes('单独迁移'));
    }
    step = 'actual administrator word drawer MP3 playback';
    await page.evaluate(() => {
      const OriginalAudio = window.Audio; window.__probeAudios = [];
      window.Audio = class extends OriginalAudio { constructor() { super(); window.__probeAudios.push(this); } };
    });
    step = 'actual vocabulary list query';
    if (mode === 'playback') page.once('dialog', dialog => dialog.accept());
    await page.getByRole('navigation', { name: '主导航' }).getByRole('link', { name: '单词管理', exact: true }).click();
    await page.locator('.el-table__row', { hasText: 'tone' }).getByRole('button', { name: '详情' }).click();
    step = 'actual administrator MP3 media playback';
    await page.getByRole('button', { name: '试听单词默认英式音频' }).click();
    await page.waitForFunction(() => window.__probeAudios.some(audio => audio.duration > 0));
    step = 'actual public signed MP3 decoded in browser';
    check(await page.evaluate(async () => {
      const response = await fetch('/api/vocabulary/settings-word/audio?bookId=settings-book&locale=en-GB');
      if (!response.ok) return false;
      const { data } = await response.json(); const audio = await fetch(data.items[0].url);
      if (!audio.ok) return false;
      const context = new AudioContext();
      try { return (await context.decodeAudioData(await audio.arrayBuffer())).duration > .4; }
      finally { await context.close(); }
    }));
  }
  step = 'no persistent secrets';
  check(await page.evaluate(() => localStorage.length === 0 && sessionStorage.length === 0));
  console.log('Real browser settings ' + mode + ' passed.');
} catch {
  // A fixed stage description is enough to diagnose; never print credential values.
  console.error('Real browser settings failed at ' + step + '.');
  process.exitCode = 1;
} finally { await browser.close(); }
