import fs from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(new URL('../../frontend/package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  const samples = await page.evaluate(async (bytes) => {
    const context = new AudioContext();
    try {
      const decoded = await context.decodeAudioData(Uint8Array.from(bytes).buffer);
      return { duration: decoded.duration, channels: decoded.numberOfChannels, samples: decoded.length };
    } finally { await context.close(); }
  }, [...fs.readFileSync(process.argv[2])]);
  if (samples.duration < 0.4 || samples.channels !== 1 || samples.samples < 10000) {
    throw new Error('Downloaded MP3 did not decode as the synthetic tone.');
  }
  console.log('Chromium decoded downloaded MP3 (synthetic 440 Hz tone).');
} finally { await browser.close(); }
