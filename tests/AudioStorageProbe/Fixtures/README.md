# Synthetic MP3 fixture

`tone.mp3` contains a generated 440 Hz mono sine wave at 44.1 kHz for 0.5 seconds, encoded with LAME 3.100 at 128 kbit/s. It contains no speech, personal data, or third-party audio. The integration script verifies the downloaded bytes and decodes them with Chromium's `AudioContext.decodeAudioData`; the fixture is not arbitrary bytes labeled as MP3.
