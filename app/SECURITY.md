# Security Policy

## Supported Versions

Only the latest commit on the default branch is expected to receive fixes before a first tagged release.

## Reporting

Do not open a public issue with private session data, local paths, tokens, screenshots, or chat transcripts. Use a private GitHub security advisory after the repository is published, or contact the maintainer privately.

## Data Handling

Codex Local Retrieval is local-first. It should treat source session files as read-only and store app metadata separately under `%LocalAppData%\CodexLocalRetrieval`.

Optional AI provider metadata, such as provider name, base URL, and model, may be stored in app metadata. API keys should be stored through Windows credentials from the native app and should not be committed to source, fixtures, logs, or screenshots.

Before publishing screenshots or logs, check for:

- usernames and home-directory paths
- project, course, or client names
- chat transcript content
- tokens, API keys, cookies, or credentials
- raw `.codex` paths or `state_5.sqlite` content
