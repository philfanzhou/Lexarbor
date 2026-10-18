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
