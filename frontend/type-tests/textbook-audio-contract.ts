import { createTextbookAudioTrack, replaceTextbookAudioTrack, removeTextbookAudioTrack, getTextbookAudioTracks, type TextbookAudioPage, type TextbookAudioTrack } from '@/services/textbookAudioApi'
const config = { signal: new AbortController().signal, lxIsCurrent: () => true }
const list: Promise<TextbookAudioPage> = getTextbookAudioTracks('book', 'unit', { page: 1, size: 20 }, config)
const created: Promise<TextbookAudioTrack> = createTextbookAudioTrack('book', 'unit', { title: 'Recording', displayOrder: 0 }, config)
void list
void created
void removeTextbookAudioTrack('book', 'unit', 'track', 'version', config)
// @ts-expect-error Full replacement requires the original version and availability.
replaceTextbookAudioTrack('book', 'unit', 'track', { title: 'Recording', displayOrder: 1 }, config)
// @ts-expect-error Metadata cannot set file state.
createTextbookAudioTrack('book', 'unit', { title: 'Recording', displayOrder: 0, hasFile: true }, config)

import { getTextbookAudioUploadPolicy, uploadTextbookAudioContent } from '@/services/textbookAudioApi'
import { createTextbookUploadAttempt, type TextbookUploadAttempt } from '@/services/textbookAudioUpload'
const attempt: TextbookUploadAttempt = createTextbookUploadAttempt('book', 'unit', 'track', 'version', new File([], 'tone.mp3'))
const receipt: Promise<unknown> = uploadTextbookAudioContent(attempt, config)
void receipt
void getTextbookAudioUploadPolicy('book', 'unit', config)
// @ts-expect-error Original conditions must remain immutable for exact replay.
attempt.expectedVersion = 'another-version'
// @ts-expect-error An upload requires the original File, not a storage URL.
createTextbookUploadAttempt('book', 'unit', 'track', 'version', 'https://example.test/audio.mp3')
