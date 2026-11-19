# Security policy

## Supported versions

Lexarbor is currently maintained from the `main` branch. Security fixes are released from the latest source and are not backported to older tags unless a release note says otherwise.

## Reporting a vulnerability

Do not disclose suspected vulnerabilities in a public issue, pull request, discussion, or commit message.

Use GitHub's **Security > Report a vulnerability** flow when it is available. If private vulnerability reporting is unavailable, contact the repository owner privately using the contact information on the owner's GitHub profile.

Include the affected version or commit, deployment assumptions, reproduction steps, impact, and any suggested mitigation. You should receive an acknowledgement within seven days. Please allow time for investigation and a coordinated fix before publishing details.

## Learner records

A learning key authorizes access to every learner in its caller namespace. User and teacher authorization belongs to the calling backend. Restrict access to keys, PostgreSQL, historical backups and logs. A learner deletion cascades to plans, cards, reviews, questions, notebook records and imported evidence. Backups follow the operator’s retention and deletion procedures. [Learner progress](docs/api/LearnerProgress.md).
