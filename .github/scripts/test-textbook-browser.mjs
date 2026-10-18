// Disposable S3, production frontend/mappers/SQLite/runtime. The Host supplies
// isolated test identity/CSRF; complete Host security tests cover real guards.
// Never capture traces, response bodies, signed URLs or credential values.
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require = createRequire(new URL('../../frontend/package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const [base] = process.argv.slice(2);
const browser = await chromium.launch({ headless: true, args: ['--autoplay-policy=no-user-gesture-required'] });
let step = 'opening production textbook entry';
function check(value) { if (!value) throw new Error('Assertion failed'); }
try {
  const page = await browser.newPage();
  await page.addInitScript(() => {
    const OriginalAudio = window.Audio;
    window.__textbookMedia = [];
    window.__textbookDecoded = 0;
    window.Audio = class extends OriginalAudio {
      constructor() {
        super(); window.__textbookMedia.push(this);
        this.addEventListener('loadedmetadata', () => { if (this.duration > .4) window.__textbookDecoded += 1; });
      }
    };
  });
  let playbackRequests = 0;
  let csrfWrites = 0;
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path.endsWith('/playback')) playbackRequests += 1;
    if (path.includes('/audio-tracks') && ['POST', 'PUT', 'DELETE'].includes(request.method())) {
      check(Boolean(request.headers()['x-signacore-csrf'])); csrfWrites += 1;
    }
  });
  await page.goto(base + '#/books/recording-book/words');
  await page.getByRole('button', { name: '教材录音', exact: true }).click();
  await page.getByText('本单元暂无录音，可新增录音目录').waitFor();
  check((await page.locator('.audio-meta').textContent()).includes('禁用教材'));
  const catalog = async () => page.evaluate(async () => {
    const response = await fetch('/admin/vocabulary-books/recording-book/units/recording-unit/audio-tracks?page=1&size=20');
    if (!response.ok) throw new Error('Catalog unavailable');
    return (await response.json()).data.items;
  });
  const row = title => page.getByRole('row').filter({ has: page.getByRole('cell', { name: title, exact: true }) });
  const create = async (title, order) => {
    await page.getByRole('button', { name: '新增录音', exact: true }).click();
    await page.getByRole('textbox', { name: '录音标题' }).fill(title);
    await page.getByRole('spinbutton', { name: '录音顺序' }).fill(String(order));
    await page.getByRole('button', { name: '保存', exact: true }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    await row(title).waitFor();
  };
  step = 'webpage creates two tracks in empty unit';
  await create('First recording', 20); await create('Second recording', 10);
  check((await catalog()).length === 2);
  check(await row('First recording').getByRole('button', { name: '试听录音 First recording', exact: true }).isDisabled());
  check(playbackRequests === 0);
  const upload = async title => {
    await row(title).getByRole('button', { name: '上传文件', exact: true }).click();
    await page.getByLabel('选择 MP3 文件').setInputFiles(fileURLToPath(new URL('../../frontend/e2e/fixtures/tone.mp3', import.meta.url)));
    await page.getByRole('button', { name: '开始上传', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.upload-feedback').textContent.includes('本次上传已确认'));
    await page.getByRole('button', { name: '关闭上传', exact: true }).waitFor({ state: 'hidden' });
    check((await catalog()).find(track => track.title === title).isAvailable === false);
    check((await catalog()).find(track => track.title === title).durationSeconds > .4);
  };
  step = 'real file selection uploads two MP3 files through production content mapper';
  await upload('First recording'); await upload('Second recording');
  const preview = async title => {
    const before = await page.evaluate(() => window.__textbookDecoded);
    await row(title).getByRole('button', { name: '试听录音 ' + title, exact: true }).click();
    await page.waitForFunction(before => window.__textbookDecoded > before, before);
    check(await page.evaluate(() => window.__textbookMedia.filter(audio => !audio.paused && audio.getAttribute('src')).length <= 1));
    await page.waitForFunction(() => window.__textbookMedia.every(audio => audio.paused && audio.getAttribute('src') === null));
    check((await page.locator('.preview-feedback').textContent()).trim() === '');
  };
  step = 'disabled book and unavailable tracks decode private signed MP3 in Chromium';
  await preview('First recording'); await preview('Second recording');
  const edit = async (title, order, available) => {
    step = 'open metadata editor for ' + title;
    await row(title).getByRole('button', { name: '编辑', exact: true }).click();
    step = 'set metadata order for ' + title;
    await page.getByRole('spinbutton', { name: '录音顺序' }).fill(String(order));
    step = 'set metadata availability for ' + title;
    const checkbox = page.getByRole('checkbox', { name: '设为可用' });
    if (await checkbox.isChecked() !== available) await checkbox.press('Space');
    check(await checkbox.isChecked() === available);
    step = 'save metadata for ' + title;
    await page.getByRole('button', { name: '保存', exact: true }).click();
    step = 'close confirmed metadata editor for ' + title;
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    step = 'read confirmed metadata catalog for ' + title;
    await page.getByText('写操作已确认，目录已重新读取。', { exact: true }).waitFor();
  };
  step = 'explicit availability and server ordering';
  await edit('First recording', 0, true); await edit('Second recording', 1, true);
  check((await catalog()).map(track => track.title).join(',') === 'First recording,Second recording');
  check((await catalog()).every(track => track.isAvailable));
  await edit('Second recording', 1, false);
  check((await catalog())[1].isAvailable === false);
  step = 'browser replacement changes version and resets availability';
  const firstVersion = (await catalog())[0].version;
  await upload('First recording');
  check((await catalog())[0].version !== firstVersion && !(await catalog())[0].isAvailable);
  await preview('First recording');
  await edit('First recording', 0, true);
  step = 'confirmed removals reflect actual catalog and release every media';
  for (const title of ['First recording', 'Second recording']) {
    await row(title).getByRole('button', { name: '移除', exact: true }).click();
    await page.getByRole('button', { name: '确认移除', exact: true }).click();
    await row(title).waitFor({ state: 'hidden' });
  }
  check((await catalog()).length === 0 && playbackRequests === 3 && csrfWrites === 11);
  check(await page.evaluate(() => localStorage.length === 0 && sessionStorage.length === 0 && window.__textbookMedia.every(audio => audio.paused && audio.getAttribute('src') === null && audio.onplaying === null && audio.onended === null && audio.onerror === null)));
  console.log('Real textbook webpage create/two uploads/replace/preview/availability/order/remove passed with private S3 and Chromium.');
} catch {
  console.error('Real textbook webpage failed at ' + step + '.'); process.exitCode = 1;
} finally { await browser.close(); }
