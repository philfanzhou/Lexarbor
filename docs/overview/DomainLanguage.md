# Lexarbor Vocabulary Catalog

The Vocabulary Catalog defines the stable language shared by entries, books, and meanings.

## Language

**Vocabulary Entry**:
A vocabulary item identified by its normalized word form.
_Avoid_: Vocabulary, Meaning

**Vocabulary Book**:
A collection of Vocabulary Entry meanings organized around a learning purpose.
_Avoid_: Document, Question Catalog

**Meaning**:
The part of speech and definition a Vocabulary Entry carries within one Vocabulary Book.
_Avoid_: Vocabulary Entry, Definition Record

## Textbook audio tracks

A `TextbookAudioTrack` is an ordered recording catalog entry belonging to one
existing book and unit. It is independent of vocabulary meanings, pronunciation
bindings, accents and source revisions. Several tracks may share a display order;
ID breaks ties. Empty or disabled books can still hold tracks.

A track has a server-generated opaque version for conditional metadata writes, an
optional current asset and duration, and explicit administrator availability. No
file means null duration and unavailable; a file requires positive finite duration.
Enabling also requires recorded asset publication. Metadata does not publish or
read objects. Deleting a track retains assets and operation history, while a book
or unit with tracks cannot be deleted. Vocabulary-only cleanup retains tracks.

See the [catalog API](../api/TextbookAudio.md) for version races, cancellation,
parent deletion and the separate later upload/playback capabilities.
