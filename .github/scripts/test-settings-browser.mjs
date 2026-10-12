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
  await page.goto(base + '#/settings');
  const audioNotice = page.getByRole('status', { name: '音频存储状态' });
  await page.getByLabel('区域 Region', { exact: true }).waitFor();
  const save = page.getByRole('button', { name: '保存音频存储设置' });
  if (mode === 'configure') {
    step = 'initial disabled state without file';
    check(!await page.getByRole('checkbox', { name: '音频存储草稿开关' }).isChecked());
    const second = await context.newPage(); await second.goto(base + '#/settings');
    await second.getByLabel('区域 Region', { exact: true }).waitFor();
    step = 'first browser save with CSRF and If-Match';
    for (const [label, value] of [
      ['服务访问地址 InternalEndpoint', process.env.LEXARBOR_S3_TEST_INTERNAL],
      ['浏览器播放地址 PublicEndpoint', process.env.LEXARBOR_S3_TEST_PUBLIC],
      ['私有桶 Bucket', process.env.LEXARBOR_S3_TEST_BUCKET], ['对象前缀 KeyPrefix', 'settings/'],
      ['AccessKey', 'synthetic-audio-access'], ['SecretKey', 'synthetic-audio-secret']
    ]) await page.getByLabel(label, { exact: true }).fill(value);
    await page.getByRole('checkbox', { name: '音频存储草稿开关' }).check();
    const written = page.waitForRequest(request => request.method() === 'PUT');
    await save.click(); const request = await written;
    check(request.headers()['if-match'] && request.headers()['x-signacore-csrf']);
    await page.waitForFunction(() => document.querySelector('[aria-label="音频存储状态"]').textContent.includes('已保存到配置文件'));
    check(await page.getByLabel('SecretKey', { exact: true }).inputValue() === '');
    step = 'reload and concurrent cross-panel version conflict';
    await page.reload(); await page.getByLabel('区域 Region', { exact: true }).waitFor();
    check(await page.getByRole('checkbox', { name: '音频存储草稿开关' }).isChecked());
    await second.getByRole('checkbox', { name: '公共 API草稿开关' }).check();
    await second.getByRole('button', { name: '保存限流设置' }).click();
    await second.getByText('当前生效状态：未知', { exact: true }).waitFor();
    check((await second.getByRole('status').first().textContent()).includes('版本已变化'));
    await second.close();
    step = 'live rate-limit off/on/429/off through browser controls';
    const statuses = () => page.evaluate(async () => {
      const values = []; for (let i = 0; i < 4; i++) values.push((await fetch('/api/settings-probe')).status); return values;
    });
    const disabledStatuses = await statuses(); console.log('Rate-limit disabled statuses: ' + disabledStatuses.join(',')); check(disabledStatuses.every(status => status === 200));
    // S3 save changed the shared version, so refresh this panel before saving.
    step = 'refresh rate-limit version before enabling';
    await page.getByRole('button', { name: '重新读取', exact: true }).click();
    await page.getByRole('checkbox', { name: '公共 API草稿开关' }).check();
    step = 'enable rate-limit save and confirmation';
    await page.getByRole('button', { name: '保存限流设置' }).click();
    await page.waitForFunction(() => document.querySelector('.settings-page [role="status"]').textContent.includes('保存后已回读'));
    step = 'enabled quota enforcement';
    const enabledStatuses = await statuses(); console.log('Rate-limit enabled statuses: ' + enabledStatuses.join(','));
    check(JSON.stringify(enabledStatuses) === '[200,200,429,429]');
    step = 'disable rate-limit save and confirmation';
    await page.getByRole('checkbox', { name: '公共 API草稿开关' }).uncheck();
    await page.getByRole('button', { name: '保存限流设置' }).click();
    await page.waitForFunction(() => document.querySelector('.settings-page [role="status"]').textContent.includes('保存后已回读'));
    check((await statuses()).every(status => status === 200));
    step = 'lost committed response recovers with GET without replay';
    await page.getByRole('button', { name: '重新读取音频存储' }).click();
    let puts = 0;
    await page.route('**/admin/system/settings/audio-storage', async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      puts++; await route.fetch(); await route.abort('failed');
    });
    await page.getByLabel('区域 Region', { exact: true }).fill('us-west-1');
    await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="音频存储状态"]').textContent.includes('已回读当前生效配置'));
    check(puts === 1); check(await page.getByLabel('区域 Region', { exact: true }).inputValue() === 'us-west-1');
    // Restore actual S3 signing region with an explicit new user submission.
    await page.unroute('**/admin/system/settings/audio-storage');
    await page.getByLabel('区域 Region', { exact: true }).fill('us-east-1'); await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="音频存储状态"]').textContent.includes('已保存到配置文件'));
  } else if (mode === 'readonly') {
    step = 'read-only file failure preserves service and current configuration';
    await page.getByLabel('区域 Region', { exact: true }).fill('test-region'); await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="音频存储状态"]').textContent.includes('保存失败（503）'));
    check((await page.evaluate(async () => (await fetch('/api/settings-probe')).status)) === 200);
    check(await page.getByRole('checkbox', { name: '音频存储草稿开关' }).isChecked());
  } else if (mode === 'override') {
    step = 'command-line override is readonly with reason and effective value';
    check(await page.getByLabel('区域 Region', { exact: true }).isDisabled());
    check(await page.getByLabel('区域 Region', { exact: true }).inputValue() === 'us-east-1');
    check((await page.locator('#audio-source-region').textContent()).includes('启动参数覆盖，网页只读'));
  } else if (mode === 'disable' || mode === 'enable') {
    step = 'live audio toggle from real form keeps credentials and objects';
    await page.getByRole('checkbox', { name: '音频存储草稿开关' }).setChecked(mode === 'enable');
    await save.click();
    await page.waitForFunction(() => document.querySelector('[aria-label="音频存储状态"]').textContent.includes('已保存到配置文件'));
    check(await page.getByText('已有密钥（不返回原值）').count() === 2);
  } else if (mode === 'playback' || mode === 'restart') {
    step = 'persisted settings and empty secret controls';
    check(await page.getByRole('checkbox', { name: '音频存储草稿开关' }).isChecked());
    check(await page.getByLabel('对象前缀 KeyPrefix', { exact: true }).inputValue() === 'settings/');
    check(await page.getByLabel('SecretKey', { exact: true }).inputValue() === '');
    if (mode === 'playback') {
      step = 'existing historical destination refuses browser save';
      await page.getByLabel('私有桶 Bucket', { exact: true }).fill('other-bucket'); await save.click();
      await page.waitForFunction(() => document.querySelector('[aria-label="音频存储状态"]').textContent.includes('保存失败（409）'));
      check((await audioNotice.textContent()).includes('单独迁移'));
    }
    step = 'actual administrator word drawer MP3 playback';
    await page.evaluate(() => {
      const OriginalAudio = window.Audio; window.__probeAudios = [];
      window.Audio = class extends OriginalAudio { constructor() { super(); window.__probeAudios.push(this); } };
    });
    step = 'actual vocabulary list query';
    await page.goto(base + '#/vocabulary');
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
