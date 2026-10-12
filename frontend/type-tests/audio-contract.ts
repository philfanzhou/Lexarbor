import { getAdminAudio, getAdminAudioPlayback, type AudioMetadata, type AudioPlayback, type AudioLocale } from '@/services/adminAudioApi'
const locale: AudioLocale = 'en-GB'
const metadata: Promise<AudioMetadata> = getAdminAudio('word', null, { signal: new AbortController().signal, lxIsCurrent: () => true })
const playback: Promise<AudioPlayback> = getAdminAudioPlayback('word', 'meaning', locale, {})
void metadata
void playback
// @ts-expect-error Only the two supported accents can be requested.
getAdminAudioPlayback('word', null, 'fr-FR', {})
