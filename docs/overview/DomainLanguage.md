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

**Meaning Submission**:
A definition an integration client proposes because no book has it. It belongs to no book until an administrator approves or rejects it.
_Avoid_: Suggestion, Draft Meaning

**Supplementary Book**:
The one Vocabulary Book the deployment names to receive approved Meaning Submissions; its questions borrow distractors from every enabled book.
_Avoid_: Custom Book, User Book
